using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Splatter.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.UI
{
    /// <summary>
    /// Package Manager-style registry window for MCP server definitions.
    /// Runtime server lifecycle and tool discovery will layer on top of this registry.
    /// </summary>
    public sealed class McpManagerWindow : EditorWindow
    {
        private const float SidebarWidth = 280f;

        private McpRegistry _registry;
        private McpServerDefinition _selected;
        private string _search = "";
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private string _status = "";
        private List<string> _warnings = new();
        private List<string> _errors = new();
        private bool _stylesInitialized;
        private GUIStyle _headerStyle;
        private GUIStyle _mutedStyle;
        private GUIStyle _listItemStyle;
        private GUIStyle _selectedListItemStyle;
        private McpRegistryTarget _importSource = McpRegistryTarget.Claude;
        private McpRegistryTarget _exportTarget = McpRegistryTarget.Claude;

        [MenuItem("Window/Splatterface Games/Assistant/MCP Servers")]
        public static void ShowWindow()
        {
            var window = GetWindow<McpManagerWindow>("MCP Servers");
            window.minSize = new Vector2(820, 520);
        }

        private void OnEnable()
        {
            LoadRegistry();
        }

        private void OnGUI()
        {
            InitializeStyles();
            DrawToolbar();
            DrawWarnings();

            var bodyRect = GUILayoutUtility.GetRect(0, 100000, 0, 100000);
            var listRect = new Rect(bodyRect.x, bodyRect.y, SidebarWidth, bodyRect.height);
            var splitterRect = new Rect(listRect.xMax, bodyRect.y, 1, bodyRect.height);
            var detailRect = new Rect(splitterRect.xMax + 1, bodyRect.y, bodyRect.width - SidebarWidth - 2, bodyRect.height);

            EditorGUI.DrawRect(splitterRect, EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.65f, 0.65f, 0.65f));
            GUILayout.BeginArea(listRect);
            DrawServerList();
            GUILayout.EndArea();

            GUILayout.BeginArea(detailRect);
            DrawServerDetails();
            GUILayout.EndArea();
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized)
                return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                margin = new RectOffset(0, 0, 6, 6)
            };
            _mutedStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true
            };
            _listItemStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 7, 7),
                margin = new RectOffset(6, 6, 2, 2),
                richText = true
            };
            _selectedListItemStyle = new GUIStyle(_listItemStyle);
            _selectedListItemStyle.normal.background = Texture2D.grayTexture;
            _selectedListItemStyle.normal.textColor = Color.white;
            _stylesInitialized = true;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Add", EditorStyles.toolbarButton, GUILayout.Width(52)))
                AddServer();

            if (GUILayout.Button("Import", EditorStyles.toolbarButton, GUILayout.Width(64)))
                ImportServers();

            _importSource = (McpRegistryTarget)EditorGUILayout.EnumPopup(_importSource, EditorStyles.toolbarPopup, GUILayout.Width(90));

            GUILayout.Space(8);

            if (GUILayout.Button("Export", EditorStyles.toolbarButton, GUILayout.Width(64)))
                ExportServers();

            _exportTarget = (McpRegistryTarget)EditorGUILayout.EnumPopup(_exportTarget, EditorStyles.toolbarPopup, GUILayout.Width(90));

            GUILayout.Space(8);

            if (GUILayout.Button("Doctor", EditorStyles.toolbarButton, GUILayout.Width(64)))
                RunDoctor();

            if (GUILayout.Button("Reveal", EditorStyles.toolbarButton, GUILayout.Width(64)))
                EditorUtility.RevealInFinder(McpRegistryStore.RegistryPath);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(McpRegistryStore.RegistryPath, EditorStyles.toolbarTextField, GUILayout.Width(Mathf.Min(position.width * 0.45f, 520)));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawWarnings()
        {
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);

            foreach (var error in _errors)
                EditorGUILayout.HelpBox(error, MessageType.Error);

            foreach (var warning in _warnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }

        private void DrawServerList()
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("MCP Registry", _headerStyle);
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{FilteredServers().Count()} servers", _mutedStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(64)))
                LoadRegistry();
            EditorGUILayout.EndHorizontal();

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            foreach (var server in FilteredServers())
                DrawServerListItem(server);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawServerListItem(McpServerDefinition server)
        {
            var selected = ReferenceEquals(server, _selected);
            var style = selected ? _selectedListItemStyle : _listItemStyle;
            EditorGUILayout.BeginVertical(style);
            EditorGUILayout.BeginHorizontal();

            var enabled = EditorGUILayout.Toggle(server.enabled, GUILayout.Width(18));
            if (enabled != server.enabled)
            {
                server.enabled = enabled;
                SaveRegistry("Updated server enabled state.");
            }

            if (GUILayout.Button(string.IsNullOrEmpty(server.displayName) ? server.id : server.displayName, EditorStyles.boldLabel))
                _selected = server;

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"{server.transport.kind}  {TransportSummary(server)}", _mutedStyle);
            if (server.tags.Count > 0)
                EditorGUILayout.LabelField(string.Join(", ", server.tags), _mutedStyle);
            EditorGUILayout.EndVertical();
        }

        private void DrawServerDetails()
        {
            if (_selected == null)
            {
                EditorGUILayout.HelpBox("Select an MCP server or add/import one.", MessageType.Info);
                return;
            }

            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
            EditorGUILayout.LabelField(_selected.displayName, _headerStyle);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", GUILayout.Width(70)))
                SaveRegistry("Saved MCP registry.");
            if (GUILayout.Button("Duplicate", GUILayout.Width(86)))
                DuplicateSelected();
            if (GUILayout.Button("Delete", GUILayout.Width(70)))
                DeleteSelected();
            if (GUILayout.Button("Copy Snippet", GUILayout.Width(100)))
                CopySelectedSnippet();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            DrawIdentitySection();
            DrawTransportSection();
            DrawAuthSection();
            DrawPolicySection();
            DrawTargetMetadataSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawIdentitySection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            _selected.enabled = EditorGUILayout.Toggle("Enabled", _selected.enabled);
            _selected.required = EditorGUILayout.Toggle("Required", _selected.required);
            _selected.id = EditorGUILayout.TextField("ID", _selected.id);
            _selected.displayName = EditorGUILayout.TextField("Display Name", _selected.displayName);
            EditorGUILayout.LabelField("Description");
            _selected.description = EditorGUILayout.TextArea(_selected.description, GUILayout.MinHeight(42));
            _selected.risk = EditorGUILayout.TextField("Risk", _selected.risk);
            _selected.tags = SplitCsv(EditorGUILayout.TextField("Tags", string.Join(", ", _selected.tags)));
        }

        private void DrawTransportSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Transport", EditorStyles.boldLabel);
            _selected.transport.kind = DrawChoice("Kind", _selected.transport.kind, new[] { "stdio", "streamable-http", "sse", "websocket", "hosted-openai", "custom" });

            if (_selected.transport.kind == "stdio")
            {
                _selected.transport.command = EditorGUILayout.TextField("Command", _selected.transport.command);
                _selected.transport.args = SplitCommandLine(EditorGUILayout.TextField("Args", JoinArgs(_selected.transport.args)));
                _selected.transport.cwd = EditorGUILayout.TextField("Working Dir", _selected.transport.cwd);
                DrawDictionary("Env", _selected.transport.env);
            }
            else
            {
                _selected.transport.url = EditorGUILayout.TextField("URL", _selected.transport.url);
                DrawDictionary("Headers", _selected.transport.headers);
            }
        }

        private void DrawAuthSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Auth", EditorStyles.boldLabel);
            _selected.auth.kind = DrawChoice("Kind", _selected.auth.kind, new[] { "none", "env", "bearer-token", "basic", "oauth", "headers-helper", "custom" });
            _selected.auth.tokenRef = EditorGUILayout.TextField("Token Ref", _selected.auth.tokenRef);
            _selected.auth.headerName = EditorGUILayout.TextField("Header Name", _selected.auth.headerName);
            _selected.auth.authServerMetadataUrl = EditorGUILayout.TextField("OAuth Metadata", _selected.auth.authServerMetadataUrl);
            _selected.auth.scopes = SplitCsv(EditorGUILayout.TextField("Scopes", string.Join(", ", _selected.auth.scopes)));
        }

        private void DrawPolicySection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Capabilities", EditorStyles.boldLabel);
            _selected.capabilities.readsWorkspace = EditorGUILayout.Toggle("Reads Workspace", _selected.capabilities.readsWorkspace);
            _selected.capabilities.writesWorkspace = EditorGUILayout.Toggle("Writes Workspace", _selected.capabilities.writesWorkspace);
            _selected.capabilities.readsExternalData = EditorGUILayout.Toggle("Reads External Data", _selected.capabilities.readsExternalData);
            _selected.capabilities.writesExternalData = EditorGUILayout.Toggle("Writes External Data", _selected.capabilities.writesExternalData);
            _selected.capabilities.executesCommands = EditorGUILayout.Toggle("Executes Commands", _selected.capabilities.executesCommands);
            _selected.capabilities.network = EditorGUILayout.Toggle("Network", _selected.capabilities.network);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Tool Filters", EditorStyles.boldLabel);
            _selected.tools.allow = SplitCsv(EditorGUILayout.TextField("Allow", string.Join(", ", _selected.tools.allow)));
            _selected.tools.deny = SplitCsv(EditorGUILayout.TextField("Deny", string.Join(", ", _selected.tools.deny)));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Permission Policy", EditorStyles.boldLabel);
            _selected.permission.@default = DrawChoice("Default", _selected.permission.@default, new[] { "allow", "ask", "deny" });
            _selected.permission.readOnly = DrawChoice("Read", _selected.permission.readOnly, new[] { "allow", "ask", "deny" });
            _selected.permission.write = DrawChoice("Write", _selected.permission.write, new[] { "allow", "ask", "deny" });
            _selected.permission.destructive = DrawChoice("Destructive", _selected.permission.destructive, new[] { "allow", "ask", "deny" });
        }

        private void DrawTargetMetadataSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Target Metadata", EditorStyles.boldLabel);
            if (_selected.targets.Count == 0)
            {
                EditorGUILayout.LabelField("No imported target-specific metadata.", _mutedStyle);
                return;
            }

            foreach (var target in _selected.targets)
            {
                EditorGUILayout.LabelField(target.Key, EditorStyles.boldLabel);
                EditorGUILayout.TextArea(target.Value.ToString(Newtonsoft.Json.Formatting.Indented), GUILayout.MinHeight(80));
            }
        }

        private void DrawDictionary(string label, Dictionary<string, string> values)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            var removeKey = "";
            foreach (var key in values.Keys.ToList())
            {
                EditorGUILayout.BeginHorizontal();
                var newKey = EditorGUILayout.TextField(key, GUILayout.Width(160));
                var newValue = EditorGUILayout.TextField(values[key]);
                if (GUILayout.Button("-", GUILayout.Width(24)))
                    removeKey = key;
                EditorGUILayout.EndHorizontal();

                if (newKey != key && !values.ContainsKey(newKey))
                {
                    var value = values[key];
                    values.Remove(key);
                    values[newKey] = value;
                    break;
                }

                values[newKey] = newValue;
            }

            if (!string.IsNullOrEmpty(removeKey))
                values.Remove(removeKey);

            if (GUILayout.Button($"Add {label}", EditorStyles.miniButton, GUILayout.Width(100)))
                values[MakeUniqueDictionaryKey(values)] = "";
        }

        private void LoadRegistry()
        {
            _registry = McpRegistryStore.Load();
            _selected = _registry.servers.FirstOrDefault();
            _status = File.Exists(McpRegistryStore.RegistryPath) ? "Loaded MCP registry." : "Using default MCP registry. Save to create .splatter/splatter.mcp.jsonc.";
            RunDoctor();
        }

        private void SaveRegistry(string status)
        {
            McpRegistryStore.Save(_registry);
            _status = status;
            RunDoctor();
        }

        private void AddServer()
        {
            var id = MakeUniqueId("new-server");
            var server = new McpServerDefinition
            {
                id = id,
                displayName = "New MCP Server",
                enabled = false,
                transport = new McpTransportDefinition { kind = "stdio" }
            };
            _registry.servers.Add(server);
            _selected = server;
            SaveRegistry("Added MCP server.");
        }

        private void DuplicateSelected()
        {
            if (_selected == null)
                return;

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(_selected);
            var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<McpServerDefinition>(json);
            clone.id = MakeUniqueId(_selected.id + "-copy");
            clone.displayName = _selected.displayName + " Copy";
            _registry.servers.Add(clone);
            _selected = clone;
            SaveRegistry("Duplicated MCP server.");
        }

        private void DeleteSelected()
        {
            if (_selected == null)
                return;

            if (!EditorUtility.DisplayDialog("Delete MCP Server", $"Delete '{_selected.displayName}' from the registry?", "Delete", "Cancel"))
                return;

            _registry.servers.Remove(_selected);
            _selected = _registry.servers.FirstOrDefault();
            SaveRegistry("Deleted MCP server.");
        }

        private void ImportServers()
        {
            var path = EditorUtility.OpenFilePanel("Import MCP config", McpRegistryStore.ProjectRoot, _importSource == McpRegistryTarget.Codex ? "toml" : "json");
            if (string.IsNullOrEmpty(path))
                return;

            var import = McpRegistryStore.Import(path, _importSource);
            McpRegistryStore.MergeImported(_registry, import.Servers);
            _warnings = import.Warnings;
            _selected = import.Servers.FirstOrDefault() ?? _selected;
            SaveRegistry($"Imported {import.Servers.Count} MCP server(s) from {_importSource}.");
        }

        private void ExportServers()
        {
            var defaultPath = McpRegistryStore.DefaultExportPath(_exportTarget);
            var path = EditorUtility.SaveFilePanel("Export MCP config", Path.GetDirectoryName(defaultPath), Path.GetFileName(defaultPath), Path.GetExtension(defaultPath).TrimStart('.'));
            if (string.IsNullOrEmpty(path))
                return;

            McpRegistryStore.WriteExport(_registry, _exportTarget, path);
            _status = $"Exported {_exportTarget} MCP config to {path}";
        }

        private void CopySelectedSnippet()
        {
            if (_selected == null)
                return;

            var temp = new McpRegistry { servers = new List<McpServerDefinition> { _selected } };
            EditorGUIUtility.systemCopyBuffer = McpRegistryStore.Export(temp, _exportTarget);
            _status = $"Copied {_exportTarget} snippet for {_selected.id}.";
        }

        private void RunDoctor()
        {
            if (_registry == null)
                return;

            var doctor = McpRegistryStore.Doctor(_registry);
            _errors = doctor.Errors;
            if (_warnings == null || _warnings.Count == 0)
                _warnings = doctor.Warnings;
            else
                _warnings = _warnings.Concat(doctor.Warnings).Distinct().ToList();
        }

        private IEnumerable<McpServerDefinition> FilteredServers()
        {
            if (string.IsNullOrWhiteSpace(_search))
                return _registry.servers;

            return _registry.servers.Where(s =>
                Contains(s.id, _search) ||
                Contains(s.displayName, _search) ||
                Contains(s.description, _search) ||
                s.tags.Any(t => Contains(t, _search)));
        }

        private string DrawChoice(string label, string value, string[] options)
        {
            var index = Mathf.Max(0, Array.IndexOf(options, value));
            index = EditorGUILayout.Popup(label, index, options);
            return options[index];
        }

        private static string TransportSummary(McpServerDefinition server)
        {
            return server.transport.kind == "stdio"
                ? server.transport.command
                : server.transport.url;
        }

        private static bool Contains(string value, string search)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> SplitCsv(string value)
        {
            return (value ?? "")
                .Split(',')
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
        }

        private static List<string> SplitCommandLine(string value)
        {
            return SplitCsv(value.Replace(" ", ","));
        }

        private static string JoinArgs(IEnumerable<string> args)
        {
            return string.Join(" ", args);
        }

        private string MakeUniqueId(string baseId)
        {
            var existing = new HashSet<string>(_registry.servers.Select(s => s.id), StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(baseId))
                return baseId;

            for (var i = 2;; i++)
            {
                var candidate = $"{baseId}-{i}";
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }

        private static string MakeUniqueDictionaryKey(Dictionary<string, string> values)
        {
            if (!values.ContainsKey("NAME"))
                return "NAME";

            for (var i = 2;; i++)
            {
                var candidate = "NAME_" + i;
                if (!values.ContainsKey(candidate))
                    return candidate;
            }
        }
    }
}
