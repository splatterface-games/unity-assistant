// Skill Registry Tests - package skills, opt-in allowlist, precedence/diagnostics
// Covers the upstream 2.9.0-pre.2 skill deltas.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Splatter.Protocol;
using Splatter.Service;
using Splatter.Service.Context;
using Xunit;

namespace Splatter.Service.Tests.Context;

public sealed class SkillRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _projectRoot;
    private const string Workspace = "ws1";

    public SkillRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "splatter-skills-" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "data");
        _projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ScanSkills_DiscoversPackageSkills()
    {
        WritePackageSkill("com.example.pkg", "pkg-skill", "A package-provided skill");
        var registry = CreateRegistry();

        var result = await registry.ScanSkillsAsync(Workspace, fullRescan: true, CancellationToken.None);

        var skill = result.Skills.FirstOrDefault(s => s.Name == "pkg-skill");
        Assert.NotNull(skill);
        Assert.Equal(SkillSource.Package, skill!.Source);
    }

    [Fact]
    public async Task ScanSkills_NonInternalSkillsDeniedByDefault()
    {
        WriteUserSkill("user-skill", "A user skill");
        WritePackageSkill("com.example.pkg", "pkg-skill", "A package skill");
        var registry = CreateRegistry();

        var result = await registry.ScanSkillsAsync(Workspace, fullRescan: true, CancellationToken.None);

        var userSkill = result.Skills.First(s => s.Name == "user-skill");
        var pkgSkill = result.Skills.First(s => s.Name == "pkg-skill");
        Assert.False(userSkill.Allowed);
        Assert.False(pkgSkill.Allowed);

        // Builtin/internal skills are always allowed.
        Assert.All(result.Skills.Where(s => s.Source == SkillSource.Builtin), s => Assert.True(s.Allowed));
    }

    [Fact]
    public async Task SetSkillAllowed_GrantsOptIn()
    {
        WriteUserSkill("user-skill", "A user skill");
        var registry = CreateRegistry();
        await registry.ScanSkillsAsync(Workspace, fullRescan: true, CancellationToken.None);

        await registry.SetSkillAllowedAsync(Workspace, "user-skill", allowed: true, CancellationToken.None);
        var result = await registry.ScanSkillsAsync(Workspace, fullRescan: true, CancellationToken.None);

        Assert.True(result.Skills.First(s => s.Name == "user-skill").Allowed);
    }

    [Fact]
    public async Task ReadSkillBody_DeniedUntilOptIn()
    {
        WriteUserSkill("user-skill", "A user skill");
        var registry = CreateRegistry();
        await registry.ScanSkillsAsync(Workspace, fullRescan: true, CancellationToken.None);

        // Denied before opt-in.
        var denied = await registry.ReadSkillBodyAsync(Workspace, "user-skill", CancellationToken.None);
        Assert.NotNull(denied.Error);
        Assert.Equal(ErrorCodes.ToolPermissionDenied, denied.Error!.Code);

        // Allowed after opt-in.
        await registry.SetSkillAllowedAsync(Workspace, "user-skill", allowed: true, CancellationToken.None);
        var allowed = await registry.ReadSkillBodyAsync(Workspace, "user-skill", CancellationToken.None);
        Assert.Null(allowed.Error);
        Assert.NotNull(allowed.Content);
    }

    [Fact]
    public async Task ScanSkills_UserWinsOverProject_AndRecordsDuplicate()
    {
        WriteUserSkill("dup", "User copy");
        WriteProjectSkill("dup", "Project copy");
        var registry = CreateRegistry();

        var result = await registry.ScanSkillsAsync(Workspace, fullRescan: true, CancellationToken.None);

        // Exactly one survives, and it is the User source (higher precedence).
        var surviving = result.Skills.Where(s => s.Name == "dup").ToList();
        Assert.Single(surviving);
        Assert.Equal(SkillSource.User, surviving[0].Source);

        // The collision is surfaced for diagnostics.
        Assert.NotNull(result.Duplicates);
        var dup = result.Duplicates!.First(d => d.Name == "dup");
        Assert.Equal(SkillSource.User, dup.WinningSource);
        Assert.Equal(SkillSource.Project, dup.ShadowedSource);
    }

    // === Helpers ===

    private SkillRegistry CreateRegistry()
    {
        var config = new ServiceConfiguration
        {
            DataDirectory = _dataDir,
            ProjectRoot = _projectRoot
        };
        return new SkillRegistry(NullLogger<SkillRegistry>.Instance, config);
    }

    private void WriteUserSkill(string name, string description)
        => WriteSkill(Path.Combine(_dataDir, "skills", name), name, description);

    private void WriteProjectSkill(string name, string description)
        => WriteSkill(Path.Combine(_dataDir, "Projects", Workspace, "skills", name), name, description);

    private void WritePackageSkill(string packageId, string name, string description)
        => WriteSkill(Path.Combine(_projectRoot, "Packages", packageId, "AIAssistantSkills", name), name, description);

    private static void WriteSkill(string skillDir, string name, string description)
    {
        Directory.CreateDirectory(skillDir);
        var content = $"---\nname: {name}\ndescription: {description}\nenabled: true\n---\n\n# {name}\n\nInstructions for {name}.\n";
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), content);
    }
}
