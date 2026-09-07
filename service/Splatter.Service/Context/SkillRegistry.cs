// Full Skill Registry Implementation

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Context;

public sealed class SkillRegistry : ISkillRegistry
{
    private readonly ILogger<SkillRegistry> _logger;
    private readonly ServiceConfiguration _config;
    private readonly ConcurrentDictionary<string, SkillScanResult> _cachedScans = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _disabledSkills = new();
    // Per-workspace opt-in allowlist for non-internal skills. Non-internal skills
    // (User/Project/Package) stay denied until their name is added here.
    private readonly ConcurrentDictionary<string, HashSet<string>> _allowedSkills = new();
    private readonly IReadOnlyList<SkillInfo> _builtinSkills;

    private const string PackageSkillsFolder = "AIAssistantSkills";

    private const int MaxSkillBodySize = 1024 * 1024; // 1 MiB
    private const string SkillFileName = "SKILL.md";

    public SkillRegistry(ILogger<SkillRegistry> logger, ServiceConfiguration config)
    {
        _logger = logger;
        _config = config;
        _builtinSkills = CreateBuiltinSkills();
    }

    public IReadOnlyList<SkillInfo> GetBuiltinSkills() => _builtinSkills;

    public async Task<SkillScanResult> ScanSkillsAsync(string workspaceId, bool fullRescan, CancellationToken ct)
    {
        if (!fullRescan && _cachedScans.TryGetValue(workspaceId, out var cached))
        {
            // Return cached if fresh (within 5 minutes)
            if ((DateTimeOffset.UtcNow - cached.ScannedAt).TotalMinutes < 5)
            {
                return cached;
            }
        }

        var skills = new List<SkillInfo>();
        var parseErrors = new List<SkillParseError>();
        var duplicates = new List<SkillDuplicateInfo>();
        // Deterministic precedence: User > Project > Package > Internal/Builtin.
        // We scan highest-precedence sources first and keep the first occurrence of
        // each name; later sources with the same name are recorded as shadowed.
        var seen = new Dictionary<string, SkillSource>(StringComparer.OrdinalIgnoreCase);

        // 1. User/global skills (highest precedence).
        var globalSkillsDir = Path.Combine(_config.DataDirectory, "skills");
        if (Directory.Exists(globalSkillsDir))
        {
            await ScanDirectoryForSkillsAsync(globalSkillsDir, SkillSource.User, skills, seen, duplicates, parseErrors, ct);
        }

        // 2. Project skills.
        // Note: In real implementation, workspace path would come from workspace registry
        // For now we'll look for it in the workspace data
        var projectSkillsDir = Path.Combine(_config.DataDirectory, "Projects", workspaceId, "skills");
        if (Directory.Exists(projectSkillsDir))
        {
            await ScanDirectoryForSkillsAsync(projectSkillsDir, SkillSource.Project, skills, seen, duplicates, parseErrors, ct);
        }

        // 3. Package-provided skills (AIAssistantSkills/ under installed UPM packages).
        foreach (var packageAiSkillsDir in GetPackageSkillDirectories())
        {
            await ScanDirectoryForSkillsAsync(packageAiSkillsDir, SkillSource.Package, skills, seen, duplicates, parseErrors, ct);
        }

        // 4. Builtin/internal skills (lowest precedence).
        foreach (var builtin in _builtinSkills)
        {
            if (seen.TryGetValue(builtin.Name, out var winner))
            {
                duplicates.Add(new SkillDuplicateInfo(builtin.Name, winner, SkillSource.Builtin));
                continue;
            }
            seen[builtin.Name] = SkillSource.Builtin;
            skills.Add(builtin);
        }

        // Apply enabled/disabled state and the opt-in allowlist. Internal/builtin
        // skills are always allowed; non-internal skills require explicit opt-in.
        var disabled = _disabledSkills.GetOrAdd(workspaceId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var allowed = _allowedSkills.GetOrAdd(workspaceId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var finalSkills = skills.Select(s => s with
        {
            Enabled = s.Enabled && !disabled.Contains(s.Name),
            Allowed = s.Source == SkillSource.Builtin || allowed.Contains(s.Name)
        }).ToList();

        var result = new SkillScanResult(
            parseErrors.Count > 0 ? SkillScanStatus.Partial : SkillScanStatus.Fresh,
            finalSkills,
            parseErrors.Count > 0 ? parseErrors : null,
            DateTimeOffset.UtcNow,
            duplicates.Count > 0 ? duplicates : null);

        _cachedScans[workspaceId] = result;
        _logger.LogDebug(
            "Scanned {Count} skills for workspace {WorkspaceId} ({Duplicates} duplicate name(s) shadowed)",
            finalSkills.Count, workspaceId, duplicates.Count);

        return result;
    }

    public async Task<IReadOnlyList<SkillInfo>> GetCachedSkillsAsync(string workspaceId, CancellationToken ct)
    {
        if (_cachedScans.TryGetValue(workspaceId, out var cached))
        {
            return cached.Skills;
        }

        var result = await ScanSkillsAsync(workspaceId, false, ct);
        return result.Skills;
    }

    public async Task<(string? Content, bool Truncated, NormalizedError? Error)> ReadSkillBodyAsync(
        string workspaceId, string skillName, CancellationToken ct)
    {
        var skills = await GetCachedSkillsAsync(workspaceId, ct);
        var skill = skills.FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
        {
            return (null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Skill not found: {skillName}", null, false, null, null));
        }

        if (!skill.Enabled)
        {
            return (null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Skill is disabled: {skillName}", null, false, null, null));
        }

        if (!skill.Allowed)
        {
            return (null, false, new NormalizedError(
                ErrorCodes.ToolPermissionDenied,
                $"Skill '{skillName}' requires explicit user opt-in before it can be loaded ({skill.Source} source)",
                null, false, null, null));
        }

        if (!skill.Compatible)
        {
            return (null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Skill is incompatible: {skill.IncompatibilityReason}", null, false, null, null));
        }

        // For builtin skills, return embedded content
        if (skill.Source == SkillSource.Builtin)
        {
            return GetBuiltinSkillBody(skillName);
        }

        var skillPath = Path.Combine(skill.SourcePath, SkillFileName);
        if (!File.Exists(skillPath))
        {
            return (null, false, new NormalizedError(
                ErrorCodes.ToolFailed, "Skill file not found", null, false, null, null));
        }

        try
        {
            var content = await File.ReadAllTextAsync(skillPath, ct);
            var truncated = false;

            if (content.Length > MaxSkillBodySize)
            {
                content = content[..MaxSkillBodySize] + "\n\n[TRUNCATED - File exceeds 1 MiB limit]";
                truncated = true;
            }

            // Remove frontmatter for body read
            content = RemoveFrontmatter(content);

            return (content, truncated, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read skill body: {SkillName}", skillName);
            return (null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Failed to read skill: {ex.Message}", null, false, null, null));
        }
    }

    public async Task<(string? Content, string? ContentType, bool Truncated, NormalizedError? Error)> ReadSkillResourceAsync(
        string workspaceId, string skillName, string resourcePath, CancellationToken ct)
    {
        // Validate resource path to prevent traversal
        if (resourcePath.Contains("..") || Path.IsPathRooted(resourcePath))
        {
            _logger.LogWarning("Invalid skill resource path traversal attempt: {Path}", resourcePath);
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, "Invalid resource path: path traversal not allowed", null, false, null, null));
        }

        var skills = await GetCachedSkillsAsync(workspaceId, ct);
        var skill = skills.FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
        {
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Skill not found: {skillName}", null, false, null, null));
        }

        if (!skill.Enabled)
        {
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Skill is disabled: {skillName}", null, false, null, null));
        }

        if (!skill.Allowed)
        {
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolPermissionDenied,
                $"Skill '{skillName}' requires explicit user opt-in before its resources can be loaded ({skill.Source} source)",
                null, false, null, null));
        }

        if (!skill.Compatible)
        {
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Skill is incompatible: {skill.IncompatibilityReason}", null, false, null, null));
        }

        // For builtin skills, return embedded resources
        if (skill.Source == SkillSource.Builtin)
        {
            return GetBuiltinSkillResource(skillName, resourcePath);
        }

        var basePath = skill.SourcePath;
        var fullPath = Path.Combine(basePath, resourcePath);

        // Double-check path containment
        var normalizedBase = Path.GetFullPath(basePath);
        var normalizedFull = Path.GetFullPath(fullPath);
        if (!normalizedFull.StartsWith(normalizedBase))
        {
            _logger.LogWarning("Skill resource path traversal blocked: {Path}", resourcePath);
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, "Invalid resource path", null, false, null, null));
        }

        if (!File.Exists(fullPath))
        {
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Resource not found: {resourcePath}", null, false, null, null));
        }

        try
        {
            var contentType = GetContentType(fullPath);
            var content = await File.ReadAllTextAsync(fullPath, ct);
            var truncated = false;

            if (content.Length > MaxSkillBodySize)
            {
                content = content[..MaxSkillBodySize] + "\n\n[TRUNCATED]";
                truncated = true;
            }

            return (content, contentType, truncated, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read skill resource: {SkillName}/{Path}", skillName, resourcePath);
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Failed to read resource: {ex.Message}", null, false, null, null));
        }
    }

    public Task<bool> SetSkillEnabledAsync(string workspaceId, string skillName, bool enabled, CancellationToken ct)
    {
        var disabled = _disabledSkills.GetOrAdd(workspaceId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        if (enabled)
        {
            disabled.Remove(skillName);
        }
        else
        {
            disabled.Add(skillName);
        }

        // Invalidate cache
        _cachedScans.TryRemove(workspaceId, out _);

        return Task.FromResult(true);
    }

    public Task<bool> SetSkillAllowedAsync(string workspaceId, string skillName, bool allowed, CancellationToken ct)
    {
        var allowlist = _allowedSkills.GetOrAdd(workspaceId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        if (allowed)
        {
            allowlist.Add(skillName);
        }
        else
        {
            allowlist.Remove(skillName);
        }

        // Invalidate cache so the recomputed Allowed state is reflected.
        _cachedScans.TryRemove(workspaceId, out _);

        _logger.LogInformation(
            "Skill '{SkillName}' opt-in {State} for workspace {WorkspaceId}",
            skillName, allowed ? "granted" : "revoked", workspaceId);

        return Task.FromResult(true);
    }

    public async Task<SkillValidateResponse> ValidateSkillAsync(string workspaceId, string skillName, CancellationToken ct)
    {
        var skills = await GetCachedSkillsAsync(workspaceId, ct);
        var skill = skills.FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
        {
            return new SkillValidateResponse(false, null, $"Skill not found: {skillName}");
        }

        // Compatibility was already checked when the skill was loaded/parsed via CheckCompatibility()
        // Return the cached compatibility state for the skill
        return new SkillValidateResponse(
            skill.Compatible,
            skill.RequiredPackages?.ToDictionary(p => p.Key, p => p.Value),
            skill.IncompatibilityReason);
    }

    private async Task ScanDirectoryForSkillsAsync(
        string directory,
        SkillSource source,
        List<SkillInfo> skills,
        Dictionary<string, SkillSource> seen,
        List<SkillDuplicateInfo> duplicates,
        List<SkillParseError> parseErrors,
        CancellationToken ct)
    {
        foreach (var skillDir in Directory.GetDirectories(directory))
        {
            var skillFile = Path.Combine(skillDir, SkillFileName);
            if (!File.Exists(skillFile)) continue;

            try
            {
                var skill = await ParseSkillFileAsync(skillFile, skillDir, source, ct);
                if (skill == null) continue;

                if (seen.TryGetValue(skill.Name, out var winner))
                {
                    duplicates.Add(new SkillDuplicateInfo(skill.Name, winner, source));
                    _logger.LogDebug(
                        "Skipping duplicate skill: {Name} from {Path} ({Source} shadowed by {Winner})",
                        skill.Name, skillDir, source, winner);
                    continue;
                }

                seen[skill.Name] = source;
                skills.Add(skill);
            }
            catch (Exception ex)
            {
                parseErrors.Add(new SkillParseError(skillFile, ex.Message, null));
                _logger.LogWarning(ex, "Failed to parse skill file: {Path}", skillFile);
            }
        }
    }

    /// <summary>
    /// Enumerates `AIAssistantSkills` folders provided by installed UPM packages,
    /// covering both embedded packages and the resolved package cache. Package
    /// skills are mixed-trust dependency-provided instructions and require opt-in.
    /// </summary>
    private IEnumerable<string> GetPackageSkillDirectories()
    {
        var projectRoot = _config.ProjectRoot;
        if (string.IsNullOrEmpty(projectRoot))
        {
            yield break;
        }

        var packageRoots = new[]
        {
            Path.Combine(projectRoot, "Packages"),
            Path.Combine(projectRoot, "Library", "PackageCache")
        };

        foreach (var packageRoot in packageRoots)
        {
            if (!Directory.Exists(packageRoot)) continue;

            string[] packageDirs;
            try
            {
                packageDirs = Directory.GetDirectories(packageRoot);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enumerate package root {Root}", packageRoot);
                continue;
            }

            foreach (var packageDir in packageDirs)
            {
                var aiSkillsDir = Path.Combine(packageDir, PackageSkillsFolder);
                if (Directory.Exists(aiSkillsDir))
                {
                    yield return aiSkillsDir;
                }
            }
        }
    }

    private async Task<SkillInfo?> ParseSkillFileAsync(
        string path,
        string skillDir,
        SkillSource source,
        CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(path, ct);
        var dirName = Path.GetFileName(skillDir) ?? "unknown";

        // Parse YAML frontmatter
        var frontmatter = ParseFrontmatter(content);

        var name = frontmatter.GetValueOrDefault("name", dirName);
        var description = frontmatter.GetValueOrDefault("description", name);
        var enabled = ParseBool(frontmatter.GetValueOrDefault("enabled", "true"));
        var version = frontmatter.GetValueOrDefault("version");
        var requiredEditorVersion = frontmatter.GetValueOrDefault("requiredEditorVersion");

        // Parse required packages
        Dictionary<string, string>? requiredPackages = null;
        if (frontmatter.TryGetValue("requiredPackages", out var packagesValue))
        {
            requiredPackages = ParsePackages(packagesValue);
        }

        // Parse tools
        List<SkillToolRef>? tools = null;
        if (frontmatter.TryGetValue("tools", out var toolsValue))
        {
            tools = ParseToolRefs(toolsValue);
        }

        // Discover resources
        var resources = DiscoverResources(skillDir);

        // Check compatibility
        var (compatible, incompatibilityReason) = CheckCompatibility(requiredPackages, requiredEditorVersion);

        var bodySize = new FileInfo(path).Length;

        return new SkillInfo(
            name,
            description,
            source,
            skillDir,
            enabled,
            compatible,
            incompatibilityReason,
            requiredPackages,
            requiredEditorVersion,
            tools,
            resources,
            bodySize,
            version);
    }

    private static Dictionary<string, string> ParseFrontmatter(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!content.StartsWith("---"))
            return result;

        var endIndex = content.IndexOf("---", 3);
        if (endIndex <= 0)
            return result;

        var frontmatter = content[3..endIndex].Trim();
        var currentKey = "";
        var currentValue = new StringBuilder();
        var inMultiline = false;

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();

            // Check for new key
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0 && !line.StartsWith(" ") && !line.StartsWith("\t"))
            {
                // Save previous key
                if (!string.IsNullOrEmpty(currentKey))
                {
                    result[currentKey] = currentValue.ToString().Trim();
                }

                currentKey = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();

                if (value == "|" || value == ">")
                {
                    inMultiline = true;
                    currentValue.Clear();
                }
                else
                {
                    inMultiline = false;
                    currentValue.Clear();
                    currentValue.Append(value);
                }
            }
            else if (inMultiline && !string.IsNullOrEmpty(currentKey))
            {
                if (currentValue.Length > 0) currentValue.AppendLine();
                currentValue.Append(trimmed);
            }
        }

        // Save last key
        if (!string.IsNullOrEmpty(currentKey))
        {
            result[currentKey] = currentValue.ToString().Trim();
        }

        return result;
    }

    private static Dictionary<string, string>? ParsePackages(string value)
    {
        var result = new Dictionary<string, string>();

        // Parse YAML-style package list
        foreach (var line in value.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-"))
            {
                trimmed = trimmed[1..].Trim();
            }

            var parts = trimmed.Split(':', 2);
            if (parts.Length == 2)
            {
                result[parts[0].Trim()] = parts[1].Trim();
            }
            else if (parts.Length == 1 && !string.IsNullOrEmpty(parts[0]))
            {
                result[parts[0].Trim()] = "*";
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static List<SkillToolRef>? ParseToolRefs(string value)
    {
        var result = new List<SkillToolRef>();

        foreach (var line in value.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-"))
            {
                trimmed = trimmed[1..].Trim();
            }

            if (!string.IsNullOrEmpty(trimmed))
            {
                result.Add(new SkillToolRef(trimmed, trimmed, ""));
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static List<SkillResourceRef> DiscoverResources(string skillDir)
    {
        var resources = new List<SkillResourceRef>();

        try
        {
            foreach (var file in Directory.GetFiles(skillDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(skillDir, file);
                if (relativePath.Equals(SkillFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileInfo = new FileInfo(file);
                resources.Add(new SkillResourceRef(
                    relativePath,
                    GetContentType(file),
                    fileInfo.Length));
            }
        }
        catch
        {
            // Ignore errors during resource discovery
        }

        return resources;
    }

    private (bool Compatible, string? Reason) CheckCompatibility(
        Dictionary<string, string>? requiredPackages,
        string? requiredEditorVersion)
    {
        // Check Unity editor version if specified
        if (!string.IsNullOrEmpty(requiredEditorVersion))
        {
            var currentVersion = GetProjectUnityVersion();
            if (currentVersion != null)
            {
                if (!IsVersionCompatible(currentVersion, requiredEditorVersion))
                {
                    return (false, $"Requires Unity {requiredEditorVersion} or higher, found {currentVersion}");
                }
            }
        }

        // Check required packages
        if (requiredPackages != null && requiredPackages.Count > 0)
        {
            var installedPackages = GetInstalledPackages();
            var missingPackages = new List<string>();

            foreach (var (packageId, requiredVersion) in requiredPackages)
            {
                if (!installedPackages.TryGetValue(packageId, out var installedVersion))
                {
                    missingPackages.Add($"{packageId}@{requiredVersion}");
                }
                else if (requiredVersion != "*" && !IsVersionCompatible(installedVersion, requiredVersion))
                {
                    missingPackages.Add($"{packageId}@{requiredVersion} (found {installedVersion})");
                }
            }

            if (missingPackages.Count > 0)
            {
                return (false, $"Missing or incompatible packages: {string.Join(", ", missingPackages)}");
            }
        }

        return (true, null);
    }

    private string? GetProjectUnityVersion()
    {
        try
        {
            var projectRoot = _config.ProjectRoot;
            if (string.IsNullOrEmpty(projectRoot)) return null;

            var versionPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(versionPath)) return null;

            var content = File.ReadAllText(versionPath);
            var match = System.Text.RegularExpressions.Regex.Match(content, @"m_EditorVersion:\s*(.+)");
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Unity version");
        }
        return null;
    }

    private Dictionary<string, string> GetInstalledPackages()
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var projectRoot = _config.ProjectRoot;
            if (string.IsNullOrEmpty(projectRoot)) return packages;

            // Parse manifest.json for packages
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (File.Exists(manifestPath))
            {
                var content = File.ReadAllText(manifestPath);
                var json = System.Text.Json.JsonDocument.Parse(content);

                if (json.RootElement.TryGetProperty("dependencies", out var deps))
                {
                    foreach (var prop in deps.EnumerateObject())
                    {
                        var version = prop.Value.GetString() ?? "";
                        // Strip git URLs, file: paths to just get version
                        if (version.Contains('#'))
                        {
                            version = version[(version.LastIndexOf('#') + 1)..];
                        }
                        else if (version.StartsWith("file:") || version.StartsWith("git:") || version.StartsWith("https:"))
                        {
                            version = "local";
                        }
                        packages[prop.Name] = version;
                    }
                }
            }

            // Also parse packages-lock.json for resolved versions
            var lockPath = Path.Combine(projectRoot, "Packages", "packages-lock.json");
            if (File.Exists(lockPath))
            {
                var content = File.ReadAllText(lockPath);
                var json = System.Text.Json.JsonDocument.Parse(content);

                if (json.RootElement.TryGetProperty("dependencies", out var deps))
                {
                    foreach (var prop in deps.EnumerateObject())
                    {
                        if (prop.Value.TryGetProperty("version", out var versionProp))
                        {
                            var version = versionProp.GetString();
                            if (!string.IsNullOrEmpty(version))
                            {
                                packages[prop.Name] = version;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read installed packages");
        }

        return packages;
    }

    private static bool IsVersionCompatible(string installed, string required)
    {
        if (required == "*" || string.IsNullOrEmpty(required))
            return true;

        if (installed == "local")
            return true; // Local packages are assumed compatible

        // Parse semantic versions - handle formats like "1.0.0", "1.0", "2021.3.1f1"
        var installedParts = ParseVersion(installed);
        var requiredParts = ParseVersion(required);

        if (installedParts == null || requiredParts == null)
            return true; // If we can't parse, assume compatible

        // Check if installed >= required
        for (int i = 0; i < Math.Max(installedParts.Length, requiredParts.Length); i++)
        {
            var installedPart = i < installedParts.Length ? installedParts[i] : 0;
            var requiredPart = i < requiredParts.Length ? requiredParts[i] : 0;

            if (installedPart > requiredPart) return true;
            if (installedPart < requiredPart) return false;
        }

        return true; // Equal versions
    }

    private static int[]? ParseVersion(string version)
    {
        if (string.IsNullOrEmpty(version))
            return null;

        // Handle Unity version format like "2021.3.1f1" or "6000.0.1f1"
        var cleaned = System.Text.RegularExpressions.Regex.Replace(version, @"[a-zA-Z].*$", "");
        var parts = cleaned.Split('.', '-');

        var result = new List<int>();
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var num))
            {
                result.Add(num);
            }
        }

        return result.Count > 0 ? result.ToArray() : null;
    }

    private static string RemoveFrontmatter(string content)
    {
        if (!content.StartsWith("---"))
            return content;

        var endIndex = content.IndexOf("---", 3);
        if (endIndex <= 0)
            return content;

        return content[(endIndex + 3)..].TrimStart();
    }

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".yaml" or ".yml" => "text/yaml",
            ".cs" => "text/x-csharp",
            ".js" => "text/javascript",
            ".py" => "text/x-python",
            ".sh" => "text/x-shellscript",
            _ => "text/plain"
        };
    }

    private static bool ParseBool(string value) =>
        value.ToLowerInvariant() is "true" or "yes" or "1";

    #region Builtin Skills

    private static IReadOnlyList<SkillInfo> CreateBuiltinSkills()
    {
        return new List<SkillInfo>
        {
            new(
                "unity-basics",
                "Basic Unity development knowledge and best practices",
                SkillSource.Builtin,
                "builtin://unity-basics",
                true,
                true,
                null,
                null,
                null,
                null,
                new List<SkillResourceRef>
                {
                    new("coding-standards.md", "text/markdown", 5000),
                    new("common-patterns.md", "text/markdown", 8000)
                },
                15000,
                "1.0.0"),
            new(
                "c-sharp-unity",
                "C# coding patterns and best practices for Unity",
                SkillSource.Builtin,
                "builtin://c-sharp-unity",
                true,
                true,
                null,
                null,
                null,
                null,
                new List<SkillResourceRef>
                {
                    new("async-patterns.md", "text/markdown", 3000),
                    new("serialization.md", "text/markdown", 4000)
                },
                12000,
                "1.0.0"),
            new(
                "urp-rendering",
                "Universal Render Pipeline development guidance",
                SkillSource.Builtin,
                "builtin://urp-rendering",
                true,
                true,
                null,
                new Dictionary<string, string> { ["com.unity.render-pipelines.universal"] = "12.0.0" },
                null,
                null,
                new List<SkillResourceRef>
                {
                    new("shader-graph.md", "text/markdown", 6000),
                    new("post-processing.md", "text/markdown", 4000)
                },
                10000,
                "1.0.0"),
            new(
                "ui-toolkit",
                "UI Toolkit development patterns and components",
                SkillSource.Builtin,
                "builtin://ui-toolkit",
                true,
                true,
                null,
                null,
                "2021.3",
                null,
                new List<SkillResourceRef>
                {
                    new("uxml-basics.md", "text/markdown", 5000),
                    new("uss-styling.md", "text/markdown", 4000),
                    new("data-binding.md", "text/markdown", 3000)
                },
                12000,
                "1.0.0")
        };
    }

    private (string? Content, bool Truncated, NormalizedError? Error) GetBuiltinSkillBody(string skillName)
    {
        // Built-in skill content is provided inline; could be moved to embedded resources for easier maintenance
        var content = skillName.ToLowerInvariant() switch
        {
            "unity-basics" => @"# Unity Basics

This skill provides fundamental Unity development guidance.

## Core Concepts

- GameObjects and Components
- Transform hierarchy
- Physics and colliders
- Input handling
- Coroutines and async
- ScriptableObjects

## Best Practices

1. Use composition over inheritance
2. Cache component references
3. Avoid Find methods in Update
4. Use object pooling for frequent spawns
5. Profile before optimizing

## Common Patterns

See resources for detailed patterns.",

            "c-sharp-unity" => @"# C# for Unity

C# coding patterns optimized for Unity development.

## Key Topics

- Unity-specific attributes
- Serialization rules
- Async/await in Unity
- Memory management
- Native collections

## Recommendations

- Use [SerializeField] for inspector-visible private fields
- Prefer structs for value types
- Use Span<T> and Memory<T> for performance
- Implement IDisposable correctly",

            "urp-rendering" => @"# Universal Render Pipeline

Guidance for URP shader and rendering development.

## Topics

- Shader Graph workflows
- Custom render features
- Post-processing effects
- Light baking
- Performance optimization

## Prerequisites

Requires com.unity.render-pipelines.universal package.",

            "ui-toolkit" => @"# UI Toolkit Development

Modern Unity UI development with UI Toolkit.

## Key Topics

- UXML document structure
- USS styling
- Data binding
- Custom controls
- Responsive layouts

## Best Practices

- Use USS variables for theming
- Implement INotifyPropertyChanged for binding
- Use visual tree asset references",

            _ => null
        };

        if (content == null)
        {
            return (null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Unknown builtin skill: {skillName}", null, false, null, null));
        }

        return (content, false, null);
    }

    private (string? Content, string? ContentType, bool Truncated, NormalizedError? Error) GetBuiltinSkillResource(
        string skillName, string resourcePath)
    {
        // Built-in skill resources are provided inline; could be moved to embedded resources for easier maintenance
        var key = $"{skillName.ToLowerInvariant()}/{resourcePath.ToLowerInvariant()}";

        var content = key switch
        {
            "unity-basics/coding-standards.md" => @"# Unity Coding Standards

## Naming Conventions

- PascalCase for public members
- camelCase for private fields (with _ prefix)
- UPPER_CASE for constants

## Structure

- One class per file
- Organize by feature, not type
- Use namespaces matching folder structure",

            "unity-basics/common-patterns.md" => @"# Common Unity Patterns

## Singleton Pattern

```csharp
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }
}
```

## Object Pooling

Use for frequently spawned objects to reduce GC pressure.",

            _ => null
        };

        if (content == null)
        {
            return (null, null, false, new NormalizedError(
                ErrorCodes.ToolFailed, $"Resource not found: {resourcePath}", null, false, null, null));
        }

        return (content, "text/markdown", false, null);
    }

    #endregion
}
