using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.Mcp
{
    internal enum McpRegistryTarget
    {
        Claude,
        Cursor,
        VSCode,
        Codex,
        OpenAI
    }

    [Serializable]
    internal sealed class McpRegistry
    {
        [JsonProperty("$schema")]
        public string schema = "https://splatter.dev/schemas/mcp-registry.schema.json";
        public int version = 1;
        public McpRegistryDefaults defaults = new();
        public List<McpServerDefinition> servers = new();
    }

    [Serializable]
    internal sealed class McpRegistryDefaults
    {
        public bool enabled = true;
        public bool required;
        public McpPermissionPolicy permission = McpPermissionPolicy.Default();
        public McpTimeouts timeouts = new();
    }

    [Serializable]
    internal sealed class McpTimeouts
    {
        public int connectMs = 10000;
        public int callMs = 60000;
    }

    [Serializable]
    internal sealed class McpServerDefinition
    {
        public string id = "";
        public string displayName = "";
        public string description = "";
        public bool enabled = true;
        public bool required;
        public List<string> tags = new();
        public string risk = "external";
        public McpTransportDefinition transport = new();
        public McpAuthDefinition auth = new();
        public McpCapabilities capabilities = new();
        public McpToolPolicy tools = new();
        public McpPermissionPolicy permission = McpPermissionPolicy.Default();
        public Dictionary<string, JToken> targets = new();
    }

    [Serializable]
    internal sealed class McpTransportDefinition
    {
        public string kind = "stdio";
        public string command = "";
        public List<string> args = new();
        public string cwd = "";
        public Dictionary<string, string> env = new();
        public string url = "";
        public Dictionary<string, string> headers = new();
    }

    [Serializable]
    internal sealed class McpAuthDefinition
    {
        public string kind = "none";
        public string tokenRef = "";
        public string headerName = "";
        public string authServerMetadataUrl = "";
        public List<string> scopes = new();
    }

    [Serializable]
    internal sealed class McpCapabilities
    {
        public bool readsWorkspace;
        public bool writesWorkspace;
        public bool readsExternalData;
        public bool writesExternalData;
        public bool executesCommands;
        public bool network;
    }

    [Serializable]
    internal sealed class McpToolPolicy
    {
        public List<string> allow = new() { "*" };
        public List<string> deny = new();
        public Dictionary<string, string> classifications = new() { { "*", "ask" } };
    }

    [Serializable]
    internal sealed class McpPermissionPolicy
    {
        public string @default = "ask";
        public string readOnly = "allow";
        public string write = "ask";
        public string destructive = "deny";

        public static McpPermissionPolicy Default()
        {
            return new McpPermissionPolicy();
        }
    }

    internal sealed class McpRegistryImportResult
    {
        public readonly List<McpServerDefinition> Servers = new();
        public readonly List<string> Warnings = new();
    }

    internal sealed class McpRegistryDoctorResult
    {
        public readonly List<string> Errors = new();
        public readonly List<string> Warnings = new();
    }

    internal static class McpRegistryStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string RegistryDirectory => Path.Combine(ProjectRoot, ".splatter");
        public static string RegistryPath => Path.Combine(RegistryDirectory, "splatter.mcp.jsonc");
        public static string GeneratedDirectory => Path.Combine(RegistryDirectory, "generated", "mcp");

        public static McpRegistry Load()
        {
            if (!File.Exists(RegistryPath))
                return CreateDefaultRegistry();

            try
            {
                var json = StripJsonComments(File.ReadAllText(RegistryPath));
                return JsonConvert.DeserializeObject<McpRegistry>(json, JsonSettings) ?? CreateDefaultRegistry();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Splatter] Failed to read MCP registry: {ex.Message}");
                return CreateDefaultRegistry();
            }
        }

        public static void Save(McpRegistry registry)
        {
            Directory.CreateDirectory(RegistryDirectory);
            File.WriteAllText(RegistryPath, JsonConvert.SerializeObject(registry, JsonSettings));
            AssetDatabase.Refresh();
        }

        public static McpRegistry CreateDefaultRegistry()
        {
            return new McpRegistry
            {
                servers = new List<McpServerDefinition>
                {
                    new()
                    {
                        id = "figma-official",
                        displayName = "Figma Official MCP",
                        description = "Figma design context for UI generation.",
                        enabled = false,
                        tags = new List<string> { "figma", "design", "ui", "context" },
                        risk = "external",
                        transport = new McpTransportDefinition
                        {
                            kind = "streamable-http",
                            url = "https://mcp.figma.com/mcp"
                        },
                        auth = new McpAuthDefinition
                        {
                            kind = "oauth",
                            tokenRef = "keychain:splatter/mcp/figma-official"
                        },
                        capabilities = new McpCapabilities
                        {
                            readsExternalData = true,
                            network = true
                        },
                        tools = new McpToolPolicy
                        {
                            allow = new List<string> { "*" },
                            classifications = new Dictionary<string, string> { { "*", "read" } }
                        }
                    }
                }
            };
        }

        public static McpRegistryImportResult Import(string path, McpRegistryTarget source)
        {
            var result = new McpRegistryImportResult();
            if (!File.Exists(path))
            {
                result.Warnings.Add($"File does not exist: {path}");
                return result;
            }

            try
            {
                if (source == McpRegistryTarget.Codex)
                    ImportCodexToml(File.ReadAllLines(path), result);
                else
                    ImportJson(File.ReadAllText(path), source, result);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Import failed: {ex.Message}");
            }

            return result;
        }

        public static string Export(McpRegistry registry, McpRegistryTarget target)
        {
            return target switch
            {
                McpRegistryTarget.Codex => ExportCodexToml(registry),
                McpRegistryTarget.VSCode => ExportVSCodeJson(registry),
                McpRegistryTarget.OpenAI => ExportOpenAIJson(registry),
                _ => ExportMcpServersJson(registry, target)
            };
        }

        public static string DefaultExportPath(McpRegistryTarget target)
        {
            Directory.CreateDirectory(GeneratedDirectory);
            return target switch
            {
                McpRegistryTarget.Codex => Path.Combine(GeneratedDirectory, "codex.config.toml"),
                McpRegistryTarget.VSCode => Path.Combine(GeneratedDirectory, "vscode.mcp.json"),
                McpRegistryTarget.OpenAI => Path.Combine(GeneratedDirectory, "openai-agents.mcp.json"),
                McpRegistryTarget.Cursor => Path.Combine(GeneratedDirectory, "cursor.mcp.json"),
                _ => Path.Combine(GeneratedDirectory, "claude.mcp.json")
            };
        }

        public static void WriteExport(McpRegistry registry, McpRegistryTarget target, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GeneratedDirectory);
            File.WriteAllText(path, Export(registry, target));
            AssetDatabase.Refresh();
        }

        public static McpRegistryDoctorResult Doctor(McpRegistry registry)
        {
            var result = new McpRegistryDoctorResult();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var server in registry.servers)
            {
                if (string.IsNullOrWhiteSpace(server.id))
                {
                    result.Errors.Add("A server has no id.");
                    continue;
                }

                if (!ids.Add(server.id))
                    result.Errors.Add($"Duplicate server id: {server.id}");

                if (server.id.Any(c => !(char.IsLower(c) || char.IsDigit(c) || c == '-' || c == '_')))
                    result.Warnings.Add($"{server.id}: id should be lowercase ASCII with dashes/underscores only.");

                switch (server.transport.kind)
                {
                    case "stdio":
                        if (string.IsNullOrWhiteSpace(server.transport.command))
                            result.Errors.Add($"{server.id}: stdio transport requires command.");
                        if (LooksLikePlainSecret(server.transport.env))
                            result.Warnings.Add($"{server.id}: env may contain plaintext secrets. Prefer secret refs.");
                        break;
                    case "streamable-http":
                    case "http":
                    case "sse":
                    case "websocket":
                        if (string.IsNullOrWhiteSpace(server.transport.url))
                            result.Errors.Add($"{server.id}: remote transport requires url.");
                        else if (server.transport.url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                 !server.transport.url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
                                 !server.transport.url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                            result.Warnings.Add($"{server.id}: non-local HTTP MCP URL should use HTTPS.");
                        break;
                    default:
                        result.Warnings.Add($"{server.id}: unknown transport kind '{server.transport.kind}'.");
                        break;
                }

                if (!string.IsNullOrEmpty(server.auth.tokenRef) && !server.auth.tokenRef.Contains(":"))
                    result.Warnings.Add($"{server.id}: auth.tokenRef should use a scheme such as env: or keychain:.");
            }

            return result;
        }

        public static void MergeImported(McpRegistry registry, IEnumerable<McpServerDefinition> imported)
        {
            foreach (var importedServer in imported)
            {
                importedServer.id = MakeUniqueId(SanitizeId(importedServer.id), registry.servers.Select(s => s.id));
                if (string.IsNullOrWhiteSpace(importedServer.displayName))
                    importedServer.displayName = importedServer.id;
                registry.servers.Add(importedServer);
            }
        }

        private static void ImportJson(string jsonOrJsonc, McpRegistryTarget source, McpRegistryImportResult result)
        {
            var root = JObject.Parse(StripJsonComments(jsonOrJsonc));
            var containerName = source == McpRegistryTarget.VSCode ? "servers" : "mcpServers";
            var container = root[containerName] as JObject;

            if (container == null && source == McpRegistryTarget.OpenAI)
            {
                ImportOpenAIJson(root, result);
                return;
            }

            if (container == null)
            {
                result.Warnings.Add($"No top-level '{containerName}' object found.");
                return;
            }

            foreach (var property in container.Properties())
            {
                var value = property.Value as JObject;
                if (value == null)
                    continue;

                var server = FromGenericJsonServer(property.Name, value, source.ToString().ToLowerInvariant());
                result.Servers.Add(server);
            }
        }

        private static McpServerDefinition FromGenericJsonServer(string id, JObject value, string source)
        {
            var server = NewImportedServer(id, source, value);
            var type = value.Value<string>("type") ?? "";
            var url = value.Value<string>("url") ?? value.Value<string>("serverUrl") ?? "";
            if (!string.IsNullOrEmpty(url) || type.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("sse", StringComparison.OrdinalIgnoreCase))
            {
                server.transport.kind = type.Equals("sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "streamable-http";
                server.transport.url = url;
                CopyStringDictionary(value["headers"], server.transport.headers);
            }
            else
            {
                server.transport.kind = "stdio";
                server.transport.command = value.Value<string>("command") ?? "";
                CopyStringArray(value["args"], server.transport.args);
                CopyStringDictionary(value["env"], server.transport.env);
                server.transport.cwd = value.Value<string>("cwd") ?? "";
            }

            server.enabled = value.Value<bool?>("enabled") ?? true;
            server.required = value.Value<bool?>("required") ?? false;
            CopyStringArray(value["enabled_tools"], server.tools.allow);
            CopyStringArray(value["disabled_tools"], server.tools.deny);
            if (server.tools.allow.Count == 0)
                server.tools.allow.Add("*");

            return server;
        }

        private static void ImportOpenAIJson(JObject root, McpRegistryImportResult result)
        {
            if (root["tools"] is not JArray tools)
            {
                result.Warnings.Add("OpenAI import expected a top-level tools array.");
                return;
            }

            foreach (var tool in tools.OfType<JObject>())
            {
                if (!string.Equals(tool.Value<string>("type"), "mcp", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = tool.Value<string>("server_label") ?? tool.Value<string>("serverLabel") ?? "openai-mcp";
                var server = NewImportedServer(id, "openai", tool);
                server.transport.kind = "hosted-openai";
                server.transport.url = tool.Value<string>("server_url") ?? tool.Value<string>("serverUrl") ?? "";
                server.permission.@default = MapOpenAIApproval(tool.Value<string>("require_approval"));
                result.Servers.Add(server);
            }
        }

        private static void ImportCodexToml(IReadOnlyList<string> lines, McpRegistryImportResult result)
        {
            McpServerDefinition current = null;
            string currentSection = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Split('#')[0].Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                if (line.StartsWith("[mcp_servers.") && line.EndsWith("]"))
                {
                    var name = line.Substring("[mcp_servers.".Length, line.Length - "[mcp_servers.".Length - 1);
                    var dot = name.IndexOf('.');
                    currentSection = dot >= 0 ? name[(dot + 1)..] : "";
                    name = dot >= 0 ? name[..dot] : name;

                    if (currentSection.Length == 0)
                    {
                        current = NewImportedServer(name, "codex", new JObject());
                        result.Servers.Add(current);
                    }
                    continue;
                }

                if (current == null)
                    continue;

                var eq = line.IndexOf('=');
                if (eq < 0)
                    continue;

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (currentSection == "env")
                {
                    current.transport.env[key] = Unquote(value);
                    continue;
                }

                switch (key)
                {
                    case "command":
                        current.transport.kind = "stdio";
                        current.transport.command = Unquote(value);
                        break;
                    case "args":
                        current.transport.args = ParseTomlStringArray(value);
                        break;
                    case "url":
                        current.transport.kind = "streamable-http";
                        current.transport.url = Unquote(value);
                        break;
                    case "enabled":
                        current.enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "required":
                        current.required = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "enabled_tools":
                        current.tools.allow = ParseTomlStringArray(value);
                        break;
                    case "disabled_tools":
                        current.tools.deny = ParseTomlStringArray(value);
                        break;
                    case "bearer_token_env_var":
                        current.auth.kind = "bearer-token";
                        current.auth.tokenRef = "env:" + Unquote(value);
                        break;
                }
            }
        }

        private static string ExportMcpServersJson(McpRegistry registry, McpRegistryTarget target)
        {
            var root = new JObject();
            var servers = new JObject();
            root["mcpServers"] = servers;

            foreach (var server in registry.servers.Where(s => s.enabled))
                servers[server.id] = ToGenericJsonServer(server, target);

            return root.ToString(Formatting.Indented);
        }

        private static string ExportVSCodeJson(McpRegistry registry)
        {
            var root = new JObject();
            var servers = new JObject();
            root["servers"] = servers;
            root["inputs"] = new JArray();

            foreach (var server in registry.servers.Where(s => s.enabled))
                servers[server.id] = ToGenericJsonServer(server, McpRegistryTarget.VSCode);

            return root.ToString(Formatting.Indented);
        }

        private static string ExportOpenAIJson(McpRegistry registry)
        {
            var root = new JObject();
            var tools = new JArray();
            root["tools"] = tools;

            foreach (var server in registry.servers.Where(s => s.enabled))
            {
                if (server.transport.kind == "stdio")
                    continue;

                var tool = new JObject
                {
                    ["type"] = "mcp",
                    ["server_label"] = server.id,
                    ["server_url"] = server.transport.url,
                    ["require_approval"] = server.permission.@default == "allow" ? "never" : "always"
                };
                tools.Add(tool);
            }

            return root.ToString(Formatting.Indented);
        }

        private static JObject ToGenericJsonServer(McpServerDefinition server, McpRegistryTarget target)
        {
            var obj = new JObject();
            if (server.transport.kind == "stdio")
            {
                obj["command"] = server.transport.command;
                obj["args"] = new JArray(server.transport.args);
                if (!string.IsNullOrEmpty(server.transport.cwd))
                    obj["cwd"] = server.transport.cwd;
                if (server.transport.env.Count > 0)
                    obj["env"] = JObject.FromObject(server.transport.env);
            }
            else
            {
                obj["type"] = server.transport.kind == "sse" ? "sse" : "http";
                obj["url"] = server.transport.url;
                if (server.transport.headers.Count > 0)
                    obj["headers"] = JObject.FromObject(server.transport.headers);
            }

            if (target == McpRegistryTarget.Claude && server.required)
                obj["alwaysLoad"] = true;

            return obj;
        }

        private static string ExportCodexToml(McpRegistry registry)
        {
            var sb = new StringBuilder();
            foreach (var server in registry.servers.Where(s => s.enabled))
            {
                sb.AppendLine($"[mcp_servers.{server.id}]");
                if (server.transport.kind == "stdio")
                {
                    sb.AppendLine($"command = {QuoteToml(server.transport.command)}");
                    sb.AppendLine($"args = {TomlArray(server.transport.args)}");
                }
                else
                {
                    sb.AppendLine($"url = {QuoteToml(server.transport.url)}");
                }

                sb.AppendLine($"enabled = {server.enabled.ToString().ToLowerInvariant()}");
                sb.AppendLine($"required = {server.required.ToString().ToLowerInvariant()}");
                if (server.tools.allow.Count > 0 && server.tools.allow.Any(t => t != "*"))
                    sb.AppendLine($"enabled_tools = {TomlArray(server.tools.allow)}");
                if (server.tools.deny.Count > 0)
                    sb.AppendLine($"disabled_tools = {TomlArray(server.tools.deny)}");
                if (server.auth.tokenRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"bearer_token_env_var = {QuoteToml(server.auth.tokenRef.Substring(4))}");

                if (server.transport.env.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"[mcp_servers.{server.id}.env]");
                    foreach (var pair in server.transport.env)
                        sb.AppendLine($"{pair.Key} = {QuoteToml(pair.Value)}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static McpServerDefinition NewImportedServer(string id, string source, JToken raw)
        {
            var server = new McpServerDefinition
            {
                id = SanitizeId(id),
                displayName = id,
                enabled = true,
                targets = new Dictionary<string, JToken>
                {
                    [source] = new JObject { ["raw"] = raw.DeepClone() }
                }
            };
            return server;
        }

        private static string StripJsonComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            var inString = false;
            var escaping = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escaping)
                        escaping = false;
                    else if (c == '\\')
                        escaping = true;
                    else if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                        i++;
                    if (i < text.Length)
                        sb.Append('\n');
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                        i++;
                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static void CopyStringArray(JToken token, List<string> target)
        {
            if (token is not JArray array)
                return;
            target.Clear();
            foreach (var item in array)
                target.Add(item.ToString());
        }

        private static void CopyStringDictionary(JToken token, Dictionary<string, string> target)
        {
            if (token is not JObject obj)
                return;
            target.Clear();
            foreach (var property in obj.Properties())
                target[property.Name] = property.Value.ToString();
        }

        private static string SanitizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "mcp-server";

            var chars = id.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var result = new string(chars).Trim('-');
            return string.IsNullOrEmpty(result) ? "mcp-server" : result;
        }

        private static string MakeUniqueId(string id, IEnumerable<string> existingIds)
        {
            var existing = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(id))
                return id;

            for (var i = 2;; i++)
            {
                var candidate = $"{id}-{i}";
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }

        private static bool LooksLikePlainSecret(Dictionary<string, string> values)
        {
            return values.Keys.Any(k => k.IndexOf("TOKEN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        k.IndexOf("SECRET", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        k.IndexOf("KEY", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string MapOpenAIApproval(string value)
        {
            return value switch
            {
                "never" => "allow",
                "always" => "ask",
                _ => "ask"
            };
        }

        private static List<string> ParseTomlStringArray(string value)
        {
            value = value.Trim();
            if (!value.StartsWith("[") || !value.EndsWith("]"))
                return new List<string>();

            value = value[1..^1];
            return value.Split(',')
                .Select(v => Unquote(v.Trim()))
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
        }

        private static string Unquote(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            return value;
        }

        private static string QuoteToml(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string TomlArray(IEnumerable<string> values)
        {
            return "[" + string.Join(", ", values.Select(QuoteToml)) + "]";
        }
    }
}
