// Resolves how to actually launch a harness CLI on the host.
//
// On Windows an npm-installed CLI (e.g. `gemini`) is a .cmd shim, not a .exe — Process.Start
// (CreateProcess) can neither resolve it from a bare name (no PATHEXT search) nor execute a
// .cmd directly. We resolve it to the underlying `node <bundle>.js` so we run a real
// executable with clean argument quoting (avoiding cmd.exe, which would mangle a prompt
// containing &, |, >, ^, etc.). A native CLI (e.g. claude.exe) resolves and runs directly.

using System.Text.RegularExpressions;

namespace Splatter.Service.Agents;

public static class HarnessLauncher
{
    private static readonly string[] ExecExtensions =
        OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "", ".sh" };

    /// <summary>
    /// Returns the executable to launch plus any prefix args. For an npm .cmd/.bat shim this
    /// is ("node", [bundle.js]); for a native binary it's (fullPath, []).
    /// </summary>
    public static (string FileName, List<string> Prefix) ResolveLaunch(string configured)
    {
        var resolved = ResolveOnPath(configured);
        if (resolved == null) return (configured, new List<string>()); // let the caller fail with the original name

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            var js = ExtractNodeScript(resolved);
            if (js != null)
                return (ResolveOnPath("node") ?? "node", new List<string> { js });
            return ("cmd.exe", new List<string> { "/c", resolved }); // fallback (rare)
        }

        return (resolved, new List<string>());
    }

    /// <summary>Finds an executable on PATH, trying platform extensions. Honours an explicit path.</summary>
    public static string? ResolveOnPath(string name)
    {
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            if (File.Exists(name)) return Path.GetFullPath(name);
            foreach (var e in ExecExtensions)
                if (e.Length > 0 && File.Exists(name + e)) return Path.GetFullPath(name + e);
            return null;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string baseName;
            try { baseName = Path.Combine(dir, name); } catch { continue; }
            foreach (var e in ExecExtensions)
            {
                var candidate = baseName + e;
                if (e.Length == 0 && !OperatingSystem.IsWindows() && File.Exists(candidate)) return candidate;
                if (e.Length > 0 && File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    // Pulls the "node <script>.js" target out of an npm shim, expanding %dp0% / $basedir.
    private static string? ExtractNodeScript(string shimPath)
    {
        try
        {
            var text = File.ReadAllText(shimPath);
            var m = Regex.Match(text, "\"([^\"]*\\.js)\"");
            if (!m.Success) return null;
            var dir = Path.GetDirectoryName(shimPath) ?? "";
            var raw = m.Groups[1].Value
                .Replace("%dp0%", dir + Path.DirectorySeparatorChar)
                .Replace("%~dp0", dir + Path.DirectorySeparatorChar)
                .Replace("$basedir", dir);
            var full = Path.GetFullPath(raw);
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }
}
