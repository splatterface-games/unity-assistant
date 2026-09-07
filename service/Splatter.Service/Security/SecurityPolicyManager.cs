// Security Policy Manager - M6.5 Implementation
// SSRF policy, shell env allowlist, redaction policies, local adversarial process model

using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Security;

/// <summary>
/// SSRF protection policy for web fetch operations.
/// </summary>
public sealed record SsrfPolicy(
    bool AllowLocalhost,
    bool AllowPrivateNetworks,
    bool AllowLinkLocal,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<string> BlockedHosts,
    IReadOnlyList<string> AllowedProtocols,
    int MaxRedirects,
    TimeSpan RequestTimeout);

/// <summary>
/// Shell environment allowlist policy.
/// </summary>
public sealed record ShellEnvPolicy(
    IReadOnlyList<string> AllowedVariables,
    IReadOnlyList<string> BlockedVariables,
    IReadOnlyList<string> SensitivePatterns,
    bool InheritPath,
    bool InheritHome,
    bool InheritTemp);

/// <summary>
/// Redaction policy for sensitive data.
/// </summary>
public sealed record RedactionPolicy(
    bool RedactApiKeys,
    bool RedactPasswords,
    bool RedactEmails,
    bool RedactIpAddresses,
    bool RedactPaths,
    bool RedactTokens,
    IReadOnlyList<Regex> CustomPatterns,
    IReadOnlyList<string> ExemptFields);

/// <summary>
/// Local adversarial process model - threat assessment.
/// </summary>
public sealed record AdversarialAssessment(
    string OperationType,
    ThreatLevel ThreatLevel,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Mitigations,
    bool RequiresExplicitConsent);

public enum ThreatLevel
{
    None,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Security violation event.
/// </summary>
public sealed record SecurityViolation(
    string ViolationId,
    SecurityViolationType Type,
    string Description,
    ThreatLevel Severity,
    string? SourceIdentifier,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string>? Context);

public enum SecurityViolationType
{
    SsrfAttempt,
    ShellEnvLeak,
    PathTraversal,
    SensitiveDataExposure,
    UnauthorizedAccess,
    RateLimitExceeded,
    MaliciousInput,
    PolicyViolation
}

/// <summary>
/// Security policy manager interface.
/// </summary>
public interface ISecurityPolicyManager
{
    // SSRF Protection
    Task<bool> ValidateUrlAsync(string url, CancellationToken ct);
    SsrfPolicy GetSsrfPolicy();
    void SetSsrfPolicy(SsrfPolicy policy);

    // Shell Environment
    Dictionary<string, string> SanitizeEnvironment(Dictionary<string, string>? requestedEnv);
    ShellEnvPolicy GetShellEnvPolicy();
    void SetShellEnvPolicy(ShellEnvPolicy policy);

    // Redaction
    string Redact(string input, string? context = null);
    RedactionPolicy GetRedactionPolicy();
    void SetRedactionPolicy(RedactionPolicy policy);

    // Adversarial Assessment
    AdversarialAssessment AssessOperation(string operationType, IReadOnlyDictionary<string, object?>? context);

    // Violation Tracking
    void RecordViolation(SecurityViolation violation);
    IReadOnlyList<SecurityViolation> GetRecentViolations(int maxCount = 100);

    // Events
    event EventHandler<SecurityViolation>? ViolationOccurred;
}

public sealed class SecurityPolicyManager : ISecurityPolicyManager
{
    private readonly ILogger<SecurityPolicyManager> _logger;
    private readonly ConcurrentQueue<SecurityViolation> _violations = new();
    private readonly int _maxViolations = 1000;

    private SsrfPolicy _ssrfPolicy;
    private ShellEnvPolicy _shellEnvPolicy;
    private RedactionPolicy _redactionPolicy;

    public event EventHandler<SecurityViolation>? ViolationOccurred;

    // Default SSRF policy - restrictive
    private static readonly SsrfPolicy DefaultSsrfPolicy = new(
        AllowLocalhost: false,
        AllowPrivateNetworks: false,
        AllowLinkLocal: false,
        AllowedHosts: new[]
        {
            "api.anthropic.com",
            "api.openai.com",
            "generativelanguage.googleapis.com",
            "openrouter.ai",
            "api.stability.ai",
            "api.replicate.com",
            "packages.unity.com",
            "upm-candidates.unity.com",
            "api.github.com",
            "raw.githubusercontent.com"
        },
        BlockedHosts: new[]
        {
            "metadata.google.internal",
            "169.254.169.254", // AWS/GCP metadata
            "metadata.azure.internal"
        },
        AllowedProtocols: new[] { "https" },
        MaxRedirects: 3,
        RequestTimeout: TimeSpan.FromSeconds(30)
    );

    // Default shell environment policy
    private static readonly ShellEnvPolicy DefaultShellEnvPolicy = new(
        AllowedVariables: new[]
        {
            "PATH",
            "HOME",
            "USERPROFILE",
            "USER",
            "USERNAME",
            "TEMP",
            "TMP",
            "TMPDIR",
            "LANG",
            "LC_ALL",
            "TERM",
            "SHELL",
            "COMSPEC",
            "PWD",
            "OLDPWD",
            // Development tools
            "DOTNET_ROOT",
            "JAVA_HOME",
            "PYTHON",
            "NODE_PATH",
            "NPM_CONFIG_PREFIX",
            // Unity specific
            "UNITY_EDITOR_PATH",
            "UNITY_VERSION"
        },
        BlockedVariables: new[]
        {
            // API Keys
            "ANTHROPIC_API_KEY",
            "OPENAI_API_KEY",
            "GOOGLE_API_KEY",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AZURE_CLIENT_SECRET",
            "GH_TOKEN",
            "GITHUB_TOKEN",
            // Sensitive credentials
            "DATABASE_URL",
            "DB_PASSWORD",
            "REDIS_URL",
            "SMTP_PASSWORD",
            // Session/Auth
            "SESSION_SECRET",
            "JWT_SECRET",
            "COOKIE_SECRET"
        },
        SensitivePatterns: new[]
        {
            ".*_KEY$",
            ".*_SECRET$",
            ".*_TOKEN$",
            ".*_PASSWORD$",
            ".*_CREDENTIAL.*",
            ".*_AUTH.*"
        },
        InheritPath: true,
        InheritHome: true,
        InheritTemp: true
    );

    // Default redaction policy
    private static readonly RedactionPolicy DefaultRedactionPolicy = new(
        RedactApiKeys: true,
        RedactPasswords: true,
        RedactEmails: true,
        RedactIpAddresses: true,
        RedactPaths: true,
        RedactTokens: true,
        CustomPatterns: Array.Empty<Regex>(),
        ExemptFields: new[] { "localhost", "127.0.0.1", "::1" }
    );

    // Compiled regex patterns for redaction
    private static readonly Regex[] RedactionPatterns = new[]
    {
        // API keys
        new Regex(@"(sk-[a-zA-Z0-9]{20,})", RegexOptions.Compiled),
        new Regex(@"(AIza[a-zA-Z0-9\-_]{35})", RegexOptions.Compiled),
        new Regex(@"(api[_-]?key['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-_]{16,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Tokens and secrets
        new Regex(@"(bearer\s+)([a-zA-Z0-9\-_\.]{20,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(token['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-_\.]{20,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(secret['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-_]{16,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(password['""]?\s*[:=]\s*['""]?)([^\s'"",]{6,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Emails (partial redaction)
        new Regex(@"([a-zA-Z0-9._%+-]+)@([a-zA-Z0-9.-]+\.[a-zA-Z]{2,})", RegexOptions.Compiled),

        // User paths
        new Regex(@"(/Users/|/home/|C:\\Users\\)([^/\\]+)", RegexOptions.Compiled),

        // Private IPs (except localhost)
        new Regex(@"\b(10\.\d{1,3}\.\d{1,3}\.\d{1,3})\b", RegexOptions.Compiled),
        new Regex(@"\b(172\.(1[6-9]|2[0-9]|3[01])\.\d{1,3}\.\d{1,3})\b", RegexOptions.Compiled),
        new Regex(@"\b(192\.168\.\d{1,3}\.\d{1,3})\b", RegexOptions.Compiled)
    };

    // Private IP ranges for SSRF check
    private static readonly (IPAddress Start, IPAddress End)[] PrivateRanges = new[]
    {
        (IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.255.255.255")),
        (IPAddress.Parse("172.16.0.0"), IPAddress.Parse("172.31.255.255")),
        (IPAddress.Parse("192.168.0.0"), IPAddress.Parse("192.168.255.255")),
        (IPAddress.Parse("127.0.0.0"), IPAddress.Parse("127.255.255.255")),
        (IPAddress.Parse("169.254.0.0"), IPAddress.Parse("169.254.255.255"))
    };

    public SecurityPolicyManager(ILogger<SecurityPolicyManager> logger)
    {
        _logger = logger;
        _ssrfPolicy = DefaultSsrfPolicy;
        _shellEnvPolicy = DefaultShellEnvPolicy;
        _redactionPolicy = DefaultRedactionPolicy;
    }

    #region SSRF Protection

    public async Task<bool> ValidateUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                RecordViolation(CreateViolation(SecurityViolationType.SsrfAttempt, "Invalid URL format", ThreatLevel.Low, url));
                return false;
            }

            // Check protocol
            if (!_ssrfPolicy.AllowedProtocols.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            {
                RecordViolation(CreateViolation(SecurityViolationType.SsrfAttempt, $"Blocked protocol: {uri.Scheme}", ThreatLevel.Medium, url));
                return false;
            }

            // Check blocked hosts
            if (_ssrfPolicy.BlockedHosts.Any(h => uri.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
            {
                RecordViolation(CreateViolation(SecurityViolationType.SsrfAttempt, "Blocked host", ThreatLevel.High, url));
                return false;
            }

            // Resolve IP to check for private networks
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);

            foreach (var addr in addresses)
            {
                // Check localhost
                if (IPAddress.IsLoopback(addr) && !_ssrfPolicy.AllowLocalhost)
                {
                    RecordViolation(CreateViolation(SecurityViolationType.SsrfAttempt, "Localhost access blocked", ThreatLevel.High, url));
                    return false;
                }

                // Check link-local
                if (addr.IsIPv6LinkLocal && !_ssrfPolicy.AllowLinkLocal)
                {
                    RecordViolation(CreateViolation(SecurityViolationType.SsrfAttempt, "Link-local access blocked", ThreatLevel.High, url));
                    return false;
                }

                // Check private networks
                if (!_ssrfPolicy.AllowPrivateNetworks && IsPrivateAddress(addr))
                {
                    RecordViolation(CreateViolation(SecurityViolationType.SsrfAttempt, "Private network access blocked", ThreatLevel.High, url));
                    return false;
                }
            }

            // Check if host is in allowed list (if list is non-empty)
            if (_ssrfPolicy.AllowedHosts.Count > 0)
            {
                var isAllowed = _ssrfPolicy.AllowedHosts.Any(h =>
                    uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    _logger.LogDebug("URL not in allowlist: {Host}", uri.Host);
                    // Don't record as violation - just not explicitly allowed
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSRF validation failed for URL: {Url}", Redact(url));
            return false;
        }
    }

    public SsrfPolicy GetSsrfPolicy() => _ssrfPolicy;

    public void SetSsrfPolicy(SsrfPolicy policy)
    {
        _ssrfPolicy = policy;
        _logger.LogInformation("SSRF policy updated");
    }

    private static bool IsPrivateAddress(IPAddress addr)
    {
        var bytes = addr.GetAddressBytes();

        foreach (var (start, end) in PrivateRanges)
        {
            if (addr.AddressFamily == start.AddressFamily)
            {
                var startBytes = start.GetAddressBytes();
                var endBytes = end.GetAddressBytes();

                var inRange = true;
                for (int i = 0; i < bytes.Length && i < startBytes.Length; i++)
                {
                    if (bytes[i] < startBytes[i] || bytes[i] > endBytes[i])
                    {
                        inRange = false;
                        break;
                    }
                }

                if (inRange) return true;
            }
        }

        return false;
    }

    #endregion

    #region Shell Environment

    public Dictionary<string, string> SanitizeEnvironment(Dictionary<string, string>? requestedEnv)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sensitivePatterns = _shellEnvPolicy.SensitivePatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();

        // Start with basic allowed variables from system
        if (_shellEnvPolicy.InheritPath)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
                result["PATH"] = path;
        }

        if (_shellEnvPolicy.InheritHome)
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(home))
            {
                result["HOME"] = home;
                result["USERPROFILE"] = home;
            }
        }

        if (_shellEnvPolicy.InheritTemp)
        {
            var temp = Environment.GetEnvironmentVariable("TEMP") ?? Environment.GetEnvironmentVariable("TMP") ?? Environment.GetEnvironmentVariable("TMPDIR");
            if (!string.IsNullOrEmpty(temp))
            {
                result["TEMP"] = temp;
                result["TMP"] = temp;
            }
        }

        // Add explicitly allowed variables from system
        foreach (var varName in _shellEnvPolicy.AllowedVariables)
        {
            if (result.ContainsKey(varName))
                continue;

            var value = Environment.GetEnvironmentVariable(varName);
            if (!string.IsNullOrEmpty(value))
            {
                result[varName] = value;
            }
        }

        // Process requested environment variables
        if (requestedEnv != null)
        {
            foreach (var (key, value) in requestedEnv)
            {
                // Check if blocked
                if (_shellEnvPolicy.BlockedVariables.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    RecordViolation(CreateViolation(
                        SecurityViolationType.ShellEnvLeak,
                        $"Blocked environment variable: {key}",
                        ThreatLevel.Medium,
                        key));
                    continue;
                }

                // Check against sensitive patterns
                if (sensitivePatterns.Any(p => p.IsMatch(key)))
                {
                    RecordViolation(CreateViolation(
                        SecurityViolationType.ShellEnvLeak,
                        $"Sensitive pattern matched: {key}",
                        ThreatLevel.Medium,
                        key));
                    continue;
                }

                // Check if explicitly allowed
                if (_shellEnvPolicy.AllowedVariables.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    result[key] = value;
                }
                else
                {
                    _logger.LogDebug("Environment variable not in allowlist: {Name}", key);
                }
            }
        }

        return result;
    }

    public ShellEnvPolicy GetShellEnvPolicy() => _shellEnvPolicy;

    public void SetShellEnvPolicy(ShellEnvPolicy policy)
    {
        _shellEnvPolicy = policy;
        _logger.LogInformation("Shell environment policy updated");
    }

    #endregion

    #region Redaction

    public string Redact(string input, string? context = null)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;

        foreach (var pattern in RedactionPatterns)
        {
            result = pattern.Replace(result, match =>
            {
                // Check exempt fields
                if (_redactionPolicy.ExemptFields.Contains(match.Value))
                    return match.Value;

                if (match.Groups.Count > 2)
                {
                    // Preserve prefix, redact value
                    return match.Groups[1].Value + "[REDACTED]";
                }
                else if (match.Value.Contains("@"))
                {
                    // Email: partial redaction
                    if (!_redactionPolicy.RedactEmails)
                        return match.Value;

                    var atIndex = match.Value.IndexOf('@');
                    return "[REDACTED]" + match.Value[atIndex..];
                }
                else
                {
                    return "[REDACTED]";
                }
            });
        }

        // Apply custom patterns
        foreach (var pattern in _redactionPolicy.CustomPatterns)
        {
            result = pattern.Replace(result, "[REDACTED]");
        }

        return result;
    }

    public RedactionPolicy GetRedactionPolicy() => _redactionPolicy;

    public void SetRedactionPolicy(RedactionPolicy policy)
    {
        _redactionPolicy = policy;
        _logger.LogInformation("Redaction policy updated");
    }

    #endregion

    #region Adversarial Assessment

    public AdversarialAssessment AssessOperation(string operationType, IReadOnlyDictionary<string, object?>? context)
    {
        // Local adversarial process model assessment
        // Evaluates operations for potential security risks

        var risks = new List<string>();
        var mitigations = new List<string>();
        var threatLevel = ThreatLevel.None;
        var requiresConsent = false;

        switch (operationType.ToLowerInvariant())
        {
            case "shell.execute":
                threatLevel = ThreatLevel.High;
                risks.Add("Command injection risk");
                risks.Add("Potential file system modification");
                risks.Add("Potential network access");
                mitigations.Add("Sanitize command arguments");
                mitigations.Add("Run in sandboxed environment");
                mitigations.Add("Limit environment variables");
                requiresConsent = true;
                break;

            case "file.write":
                threatLevel = ThreatLevel.Medium;
                risks.Add("File overwrite risk");
                risks.Add("Path traversal risk");
                mitigations.Add("Validate path is within workspace");
                mitigations.Add("Create backup before modification");
                requiresConsent = true;
                break;

            case "file.delete":
                threatLevel = ThreatLevel.High;
                risks.Add("Data loss risk");
                risks.Add("Path traversal risk");
                mitigations.Add("Validate path is within workspace");
                mitigations.Add("Require explicit confirmation");
                requiresConsent = true;
                break;

            case "web.fetch":
                threatLevel = ThreatLevel.Medium;
                risks.Add("SSRF risk");
                risks.Add("Data exfiltration risk");
                mitigations.Add("Validate URL against allowlist");
                mitigations.Add("Block private network access");
                mitigations.Add("Limit redirect following");
                requiresConsent = false;
                break;

            case "credential.access":
                threatLevel = ThreatLevel.Critical;
                risks.Add("Credential exposure risk");
                risks.Add("API key theft risk");
                mitigations.Add("Never log or transmit credentials");
                mitigations.Add("Use secure credential storage");
                mitigations.Add("Require explicit user confirmation");
                requiresConsent = true;
                break;

            case "package.install":
                threatLevel = ThreatLevel.High;
                risks.Add("Malicious package risk");
                risks.Add("Supply chain attack risk");
                mitigations.Add("Verify package source");
                mitigations.Add("Check package signatures");
                requiresConsent = true;
                break;

            case "unity.execute":
                threatLevel = ThreatLevel.Medium;
                risks.Add("Editor corruption risk");
                risks.Add("Project modification risk");
                mitigations.Add("Validate commands are safe");
                mitigations.Add("Create checkpoint before execution");
                requiresConsent = false;
                break;

            case "scene.modify":
                threatLevel = ThreatLevel.Low;
                risks.Add("Scene corruption risk");
                mitigations.Add("Create checkpoint before modification");
                mitigations.Add("Validate object references");
                requiresConsent = false;
                break;

            case "context.index":
                threatLevel = ThreatLevel.Low;
                risks.Add("Performance impact");
                risks.Add("Memory exhaustion risk");
                mitigations.Add("Limit file sizes");
                mitigations.Add("Skip binary files");
                requiresConsent = false;
                break;

            default:
                threatLevel = ThreatLevel.Low;
                mitigations.Add("Standard validation applied");
                break;
        }

        return new AdversarialAssessment(operationType, threatLevel, risks, mitigations, requiresConsent);
    }

    #endregion

    #region Violation Tracking

    public void RecordViolation(SecurityViolation violation)
    {
        _violations.Enqueue(violation);

        while (_violations.Count > _maxViolations)
        {
            _violations.TryDequeue(out _);
        }

        var logLevel = violation.Severity switch
        {
            ThreatLevel.Critical => LogLevel.Error,
            ThreatLevel.High => LogLevel.Warning,
            ThreatLevel.Medium => LogLevel.Warning,
            _ => LogLevel.Information
        };

        _logger.Log(logLevel, "Security violation: {Type} - {Description} (severity: {Severity})",
            violation.Type, violation.Description, violation.Severity);

        ViolationOccurred?.Invoke(this, violation);
    }

    public IReadOnlyList<SecurityViolation> GetRecentViolations(int maxCount = 100)
    {
        return _violations.ToArray()
            .OrderByDescending(v => v.Timestamp)
            .Take(maxCount)
            .ToList();
    }

    private SecurityViolation CreateViolation(
        SecurityViolationType type,
        string description,
        ThreatLevel severity,
        string? source,
        Dictionary<string, string>? context = null)
    {
        return new SecurityViolation(
            $"sec_{Guid.NewGuid():N}"[..16],
            type,
            description,
            severity,
            source,
            DateTimeOffset.UtcNow,
            context);
    }

    #endregion
}
