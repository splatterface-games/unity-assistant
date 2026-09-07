// Writes Splatter's context/skills into the harness CLIs' instruction files.
//
// A managed, version-tagged block in the project root's CLAUDE.md (Claude Code) and
// AGENTS.md (Codex) delivers the Unity live-state guidance the headless runner used
// to inject via --append-system-prompt. Idempotent: the block is replaced in place
// when the version changes and user content around it is never touched. The files
// are left in place after sessions end - the content is static per package version,
// shared by concurrent sessions, and useful for manually-launched CLIs too.

using System;
using System.IO;
using UnityEngine;

namespace Splatter.Editor.Launch
{
    public static class LaunchContextWriter
    {
        private const int Version = 1;
        private const string BeginPrefix = "<!-- BEGIN SPLATTER:MANAGED";
        private static string BeginMarker => $"{BeginPrefix} (v{Version}) -->";
        private const string EndMarker = "<!-- END SPLATTER:MANAGED -->";

        public static void EnsureManagedBlocks(string projectRoot)
        {
            var block = BuildBlock();
            foreach (var file in new[] { "CLAUDE.md", "AGENTS.md" })
            {
                try
                {
                    EnsureBlock(Path.Combine(projectRoot, file), block);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Splatter] Couldn't update {file}: {ex.Message}");
                }
            }
        }

        private static void EnsureBlock(string path, string block)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, block + "\n");
                return;
            }

            var existing = File.ReadAllText(path);
            var begin = existing.IndexOf(BeginPrefix, StringComparison.Ordinal);
            if (begin < 0)
            {
                // No managed block yet: append below the user's content.
                var separator = existing.EndsWith("\n") ? "\n" : "\n\n";
                File.WriteAllText(path, existing + separator + block + "\n");
                return;
            }

            var end = existing.IndexOf(EndMarker, begin, StringComparison.Ordinal);
            if (end < 0)
            {
                // Broken block (end marker lost): replace from the begin marker down.
                File.WriteAllText(path, existing.Substring(0, begin) + block + "\n");
                return;
            }

            var current = existing.Substring(begin, end + EndMarker.Length - begin);
            if (current == block)
                return; // up to date - don't touch the file

            File.WriteAllText(path,
                existing.Substring(0, begin) + block + existing.Substring(end + EndMarker.Length));
        }

        // The guidance itself. Ported from the headless runner's UnityLiveStateSystemPrompt,
        // plus the launcher-mode permission contract. Bump Version when this changes.
        private static string BuildBlock()
        {
            return BeginMarker + @"
# Splatter: working inside a live Unity Editor

You are operating inside a live Unity Editor session via the Splatter assistant's
`splatter-unity` MCP tools.

## Live state, not files

- The scene tools (`scene_get_hierarchy`, `scene_find_objects`, `scene_read_object`,
  `selection_get`, `console_get_logs`) report the LIVE state of the open Editor,
  including unsaved, in-memory changes. To answer any question about the current
  scene, GameObjects, selection, or console, you MUST call these tools rather than
  guessing.
- Do NOT infer scene or object state by reading `.unity`, `.prefab`, or `.asset`
  files from disk: those reflect the last save and are often stale. Read project
  files only for source code, configuration, or assets no live tool can report.

## Seeing the editor

For VISUAL work (appearance, lighting, materials, UI layout, ""why does this look
wrong"") you can SEE the editor: call `capture_game` or `capture_scene` for the
viewport, or `capture_multi_angle` to frame a specific GameObject from several
angles. After changing something visual, CAPTURE AGAIN to verify and iterate
(look -> change -> look). Prefer the cheaper structural tools for hierarchy /
component / value questions; capture only when you genuinely need to look.

## Permissions

Unity mutations (scene/asset/package writes) pop an Allow/Deny prompt inside the
Unity editor - not in this terminal. If a tool call returns ""Permission denied by
the user"", the user chose Deny: do not retry the operation; ask them what they
want instead. Reads and captures never prompt. Consider creating a checkpoint
(`checkpoint_create`) before a risky batch of edits.

## Presentation

- When you mention a specific GameObject, render it as a markdown link
  `[Name](splatter://obj/<globalId>)` using the object's `globalId` from the scene
  tools, so the user can click it to select and ping the object in the editor.
- Present lists of GameObjects, components, or property values as GitHub-flavored
  markdown tables with a header row.
" + EndMarker;
        }
    }
}
