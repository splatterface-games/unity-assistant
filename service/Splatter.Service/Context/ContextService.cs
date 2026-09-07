// Context Service Implementation

using System.Text;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Context;

public sealed class ContextService : IContextService
{
    private readonly ILogger<ContextService> _logger;
    private readonly ISkillRegistry _skills;

    public ContextService(ILogger<ContextService> logger, ISkillRegistry skills)
    {
        _logger = logger;
        _skills = skills;
    }

    public async Task<string> BuildSystemPromptAsync(string workspaceId, AgentPermissionMode mode, CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are Splatter, an AI assistant integrated with Unity Editor.");
        sb.AppendLine("You help users with game development tasks including:");
        sb.AppendLine("- Answering questions about Unity and their project");
        sb.AppendLine("- Writing and editing code");
        sb.AppendLine("- Creating and modifying assets");
        sb.AppendLine("- Debugging issues");
        sb.AppendLine();

        // Add mode-specific instructions
        switch (mode)
        {
            case AgentPermissionMode.ReadOnly:
                sb.AppendLine("IMPORTANT: You are in READ-ONLY mode. You can only read and analyze - do not attempt to modify files or execute commands.");
                break;
            case AgentPermissionMode.AskBeforeWrite:
                sb.AppendLine("You can read files freely. Before making any changes, you will be asked for permission. Wait for approval before proceeding.");
                break;
            case AgentPermissionMode.WorkspaceWrite:
                sb.AppendLine("You have permission to modify files in the project. Be careful and create checkpoints before making significant changes.");
                break;
            case AgentPermissionMode.FullAuto:
                sb.AppendLine("You have full autonomy to make changes. Use this responsibly and ensure changes are correct before applying.");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("When using tools:");
        sb.AppendLine("- Always read files before modifying them");
        sb.AppendLine("- Use search tools to find relevant code");
        sb.AppendLine("- Create checkpoints before making changes");
        sb.AppendLine("- Test your changes when possible");
        sb.AppendLine("- Explain what you're doing and why");

        // Add skills metadata. Only surface enabled skills the user has allowed:
        // non-internal skills stay hidden from the model until explicitly opted in.
        var skills = await _skills.GetCachedSkillsAsync(workspaceId, ct);
        var visibleSkills = skills.Where(s => s.Enabled && s.Allowed).ToList();
        if (visibleSkills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available skills (use skill.read_body to load full instructions):");
            foreach (var skill in visibleSkills)
            {
                sb.AppendLine($"- {skill.Name}: {skill.Description}");
            }
        }

        return sb.ToString();
    }

    public async Task<string> BuildContextAsync(string workspaceId, IReadOnlyList<ContextAttachment> attachments, CancellationToken ct)
    {
        if (attachments.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("# Context");
        sb.AppendLine();

        foreach (var attachment in attachments)
        {
            sb.AppendLine($"## {attachment.DisplayName}");

            switch (attachment.Type)
            {
                case "file":
                    sb.AppendLine($"File: {attachment.Path}");
                    if (attachment.Content != null)
                    {
                        sb.AppendLine("```");
                        sb.AppendLine(attachment.Content.ToString());
                        sb.AppendLine("```");
                    }
                    break;

                case "selection":
                    sb.AppendLine($"Selected: {attachment.DisplayName}");
                    if (attachment.Content != null)
                    {
                        sb.AppendLine(attachment.Content.ToString());
                    }
                    break;

                case "console":
                    sb.AppendLine("Console output:");
                    sb.AppendLine("```");
                    sb.AppendLine(attachment.Content?.ToString() ?? "");
                    sb.AppendLine("```");
                    break;

                case "custom_instructions":
                    sb.AppendLine("User instructions:");
                    sb.AppendLine(attachment.Content?.ToString() ?? "");
                    break;

                default:
                    if (attachment.Content != null)
                    {
                        sb.AppendLine(attachment.Content.ToString());
                    }
                    break;
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
