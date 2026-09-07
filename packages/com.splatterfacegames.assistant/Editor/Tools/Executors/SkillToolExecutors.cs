// Skill Tool Executors - User-defined AI workflow operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    #region Skill Data Models

    /// <summary>
    /// Represents a skill parameter definition.
    /// </summary>
    [Serializable]
    public class SkillParameter
    {
        public string Name;
        public string Type;
        public string Description;
        public bool Required;
        public string Default;

        public SkillParameter() { }

        public SkillParameter(string name, string type, string description = null, bool required = false, string defaultValue = null)
        {
            Name = name;
            Type = type;
            Description = description;
            Required = required;
            Default = defaultValue;
        }

        public object ToOutput()
        {
            return new
            {
                name = Name,
                type = Type,
                description = Description,
                required = Required,
                @default = Default
            };
        }
    }

    /// <summary>
    /// Represents a skill usage example.
    /// </summary>
    [Serializable]
    public class SkillExample
    {
        public string Input;
        public string Output;
        public string Description;

        public SkillExample() { }

        public SkillExample(string input, string output, string description = null)
        {
            Input = input;
            Output = output;
            Description = description;
        }

        public object ToOutput()
        {
            return new
            {
                input = Input,
                output = Output,
                description = Description
            };
        }
    }

    /// <summary>
    /// Represents a complete skill definition loaded from YAML/JSON files.
    /// </summary>
    [Serializable]
    public class SkillDefinition
    {
        public string Id;
        public string Name;
        public string Description;
        public string Category;
        public string Author;
        public string Version;
        public string[] Tags;
        public string Instructions;
        public SkillParameter[] Parameters;
        public SkillExample[] Examples;
        public Dictionary<string, string> Resources;

        // Internal tracking
        [NonSerialized] public string SourcePath;
        [NonSerialized] public string SourceLocation; // "project", "user", or "package"

        public SkillDefinition()
        {
            Tags = Array.Empty<string>();
            Parameters = Array.Empty<SkillParameter>();
            Examples = Array.Empty<SkillExample>();
            Resources = new Dictionary<string, string>();
        }

        public object ToListOutput()
        {
            return new
            {
                id = Id,
                name = Name,
                description = Description,
                category = Category,
                author = Author,
                version = Version,
                tags = Tags,
                location = SourceLocation
            };
        }

        public object ToBodyOutput()
        {
            return new
            {
                id = Id,
                name = Name,
                description = Description,
                instructions = Instructions,
                parameters = Parameters?.Select(p => p.ToOutput()).ToArray() ?? Array.Empty<object>(),
                examples = Examples?.Select(e => e.ToOutput()).ToArray() ?? Array.Empty<object>()
            };
        }
    }

    #endregion

    #region Skill Discovery

    /// <summary>
    /// Discovers and loads skill definitions from various locations.
    /// </summary>
    public static class SkillDiscovery
    {
        private static readonly string[] SkillFilePatterns = { "skill.yaml", "skill.yml", "skill.json" };

        private static Dictionary<string, SkillDefinition> _skillCache;
        private static DateTime _lastCacheUpdate;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets all search locations for skills.
        /// </summary>
        public static IEnumerable<(string path, string location)> GetSearchLocations()
        {
            // Project location: Assets/SplatterAI/Skills/
            var projectSkillsPath = Path.Combine(Application.dataPath, "SplatterAI", "Skills");
            yield return (projectSkillsPath, "project");

            // User location: {UserProfile}/SplatterAI/Skills/
            var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userSkillsPath = Path.Combine(userProfilePath, "SplatterAI", "Skills");
            yield return (userSkillsPath, "user");

            // Package location: Packages/com.splatterfacegames.assistant/Skills/
            var packageSkillsPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Packages", "com.splatterfacegames.assistant", "Skills"));
            yield return (packageSkillsPath, "package");
        }

        /// <summary>
        /// Discovers all available skills from all search locations.
        /// </summary>
        public static List<SkillDefinition> DiscoverSkills(bool forceRefresh = false)
        {
            if (!forceRefresh && _skillCache != null && DateTime.UtcNow - _lastCacheUpdate < CacheDuration)
            {
                return _skillCache.Values.ToList();
            }

            _skillCache = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var (searchPath, location) in GetSearchLocations())
            {
                if (!Directory.Exists(searchPath))
                    continue;

                try
                {
                    // Find all skill directories
                    foreach (var skillDir in Directory.GetDirectories(searchPath))
                    {
                        var skill = TryLoadSkillFromDirectory(skillDir, location);
                        if (skill != null && !string.IsNullOrEmpty(skill.Id))
                        {
                            // Later locations (user/package) don't override earlier (project)
                            if (!_skillCache.ContainsKey(skill.Id))
                            {
                                _skillCache[skill.Id] = skill;
                            }
                        }
                    }

                    // Also check for skill files directly in the search path
                    foreach (var pattern in SkillFilePatterns)
                    {
                        var files = Directory.GetFiles(searchPath, pattern);
                        foreach (var file in files)
                        {
                            var skill = TryLoadSkillFromFile(file, location);
                            if (skill != null && !string.IsNullOrEmpty(skill.Id))
                            {
                                if (!_skillCache.ContainsKey(skill.Id))
                                {
                                    _skillCache[skill.Id] = skill;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Splatter] Error scanning skill directory {searchPath}: {ex.Message}");
                }
            }

            _lastCacheUpdate = DateTime.UtcNow;
            return _skillCache.Values.ToList();
        }

        /// <summary>
        /// Loads a specific skill by ID.
        /// </summary>
        public static SkillDefinition LoadSkill(string skillId)
        {
            if (string.IsNullOrEmpty(skillId))
                return null;

            // Ensure cache is populated
            var skills = DiscoverSkills();

            if (_skillCache.TryGetValue(skillId, out var skill))
            {
                return skill;
            }

            return null;
        }

        /// <summary>
        /// Gets a resource file content from a skill.
        /// </summary>
        public static (string content, string fullPath, string resourceType) GetSkillResource(string skillId, string resourceName)
        {
            var skill = LoadSkill(skillId);
            if (skill == null)
                return (null, null, null);

            // Get the skill directory
            var skillDir = Path.GetDirectoryName(skill.SourcePath);
            if (string.IsNullOrEmpty(skillDir))
                return (null, null, null);

            // Check if resource is defined in the skill's resources dictionary
            string relativePath = null;
            if (skill.Resources != null && skill.Resources.TryGetValue(resourceName, out var definedPath))
            {
                relativePath = definedPath;
            }
            else
            {
                // Try common resource locations
                var possiblePaths = new[]
                {
                    resourceName,
                    Path.Combine("resources", resourceName),
                    Path.Combine("templates", resourceName),
                    Path.Combine("schemas", resourceName),
                    Path.Combine("examples", resourceName)
                };

                foreach (var possiblePath in possiblePaths)
                {
                    var testPath = Path.Combine(skillDir, possiblePath);
                    if (File.Exists(testPath))
                    {
                        relativePath = possiblePath;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(relativePath))
                return (null, null, null);

            var fullPath = Path.Combine(skillDir, relativePath);
            if (!File.Exists(fullPath))
                return (null, null, null);

            // Determine resource type from path or extension
            var resourceType = DetermineResourceType(relativePath);

            try
            {
                var content = File.ReadAllText(fullPath);
                return (content, fullPath, resourceType);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Splatter] Error reading skill resource {fullPath}: {ex.Message}");
                return (null, null, null);
            }
        }

        /// <summary>
        /// Clears the skill cache to force re-discovery.
        /// </summary>
        public static void ClearCache()
        {
            _skillCache = null;
        }

        #region Private Helper Methods

        private static SkillDefinition TryLoadSkillFromDirectory(string skillDir, string location)
        {
            foreach (var pattern in SkillFilePatterns)
            {
                var skillFile = Path.Combine(skillDir, pattern);
                if (File.Exists(skillFile))
                {
                    return TryLoadSkillFromFile(skillFile, location);
                }
            }

            return null;
        }

        private static SkillDefinition TryLoadSkillFromFile(string filePath, string location)
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var extension = Path.GetExtension(filePath).ToLowerInvariant();

                SkillDefinition skill;
                if (extension == ".json")
                {
                    skill = ParseSkillFromJson(content);
                }
                else
                {
                    skill = ParseSkillFromYaml(content);
                }

                if (skill != null)
                {
                    skill.SourcePath = filePath;
                    skill.SourceLocation = location;

                    // Generate ID from directory name if not specified
                    if (string.IsNullOrEmpty(skill.Id))
                    {
                        skill.Id = Path.GetFileName(Path.GetDirectoryName(filePath));
                    }
                }

                return skill;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Splatter] Error loading skill from {filePath}: {ex.Message}");
                return null;
            }
        }

        private static SkillDefinition ParseSkillFromJson(string json)
        {
            // Use a simple JSON parser since Unity's JsonUtility doesn't support all features we need
            var skill = new SkillDefinition();

            try
            {
                var parser = new SimpleJsonParser(json);
                var root = parser.Parse();

                if (root is Dictionary<string, object> dict)
                {
                    PopulateSkillFromDictionary(skill, dict);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Splatter] JSON parse error: {ex.Message}");
                return null;
            }

            return skill;
        }

        private static SkillDefinition ParseSkillFromYaml(string yaml)
        {
            var skill = new SkillDefinition();

            try
            {
                var parser = new SimpleYamlParser(yaml);
                var root = parser.Parse();

                if (root is Dictionary<string, object> dict)
                {
                    PopulateSkillFromDictionary(skill, dict);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Splatter] YAML parse error: {ex.Message}");
                return null;
            }

            return skill;
        }

        private static void PopulateSkillFromDictionary(SkillDefinition skill, Dictionary<string, object> dict)
        {
            if (dict.TryGetValue("id", out var id))
                skill.Id = id?.ToString();
            if (dict.TryGetValue("name", out var name))
                skill.Name = name?.ToString();
            if (dict.TryGetValue("description", out var description))
                skill.Description = description?.ToString();
            if (dict.TryGetValue("category", out var category))
                skill.Category = category?.ToString();
            if (dict.TryGetValue("author", out var author))
                skill.Author = author?.ToString();
            if (dict.TryGetValue("version", out var version))
                skill.Version = version?.ToString();
            if (dict.TryGetValue("instructions", out var instructions))
                skill.Instructions = instructions?.ToString();

            // Parse tags
            if (dict.TryGetValue("tags", out var tags) && tags is List<object> tagList)
            {
                skill.Tags = tagList.Select(t => t?.ToString()).Where(t => t != null).ToArray();
            }

            // Parse parameters
            if (dict.TryGetValue("parameters", out var parameters) && parameters is List<object> paramList)
            {
                skill.Parameters = paramList
                    .OfType<Dictionary<string, object>>()
                    .Select(ParseParameter)
                    .Where(p => p != null)
                    .ToArray();
            }

            // Parse examples
            if (dict.TryGetValue("examples", out var examples) && examples is List<object> exampleList)
            {
                skill.Examples = exampleList
                    .OfType<Dictionary<string, object>>()
                    .Select(ParseExample)
                    .Where(e => e != null)
                    .ToArray();
            }

            // Parse resources
            if (dict.TryGetValue("resources", out var resources) && resources is Dictionary<string, object> resourceDict)
            {
                skill.Resources = resourceDict
                    .Where(kvp => kvp.Value != null)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());
            }
        }

        private static SkillParameter ParseParameter(Dictionary<string, object> dict)
        {
            var param = new SkillParameter();

            if (dict.TryGetValue("name", out var name))
                param.Name = name?.ToString();
            if (dict.TryGetValue("type", out var type))
                param.Type = type?.ToString();
            if (dict.TryGetValue("description", out var description))
                param.Description = description?.ToString();
            if (dict.TryGetValue("required", out var required))
                param.Required = Convert.ToBoolean(required);
            if (dict.TryGetValue("default", out var defaultValue))
                param.Default = defaultValue?.ToString();

            return param;
        }

        private static SkillExample ParseExample(Dictionary<string, object> dict)
        {
            var example = new SkillExample();

            if (dict.TryGetValue("input", out var input))
                example.Input = input?.ToString();
            if (dict.TryGetValue("output", out var output))
                example.Output = output?.ToString();
            if (dict.TryGetValue("description", out var description))
                example.Description = description?.ToString();

            return example;
        }

        private static string DetermineResourceType(string relativePath)
        {
            var normalizedPath = relativePath.Replace('\\', '/').ToLowerInvariant();

            if (normalizedPath.StartsWith("templates/") || normalizedPath.Contains("/templates/"))
                return "template";
            if (normalizedPath.StartsWith("schemas/") || normalizedPath.Contains("/schemas/"))
                return "schema";
            if (normalizedPath.StartsWith("examples/") || normalizedPath.Contains("/examples/"))
                return "example";
            if (normalizedPath.StartsWith("resources/") || normalizedPath.Contains("/resources/"))
                return "resource";

            // Determine by extension
            var extension = Path.GetExtension(relativePath).ToLowerInvariant();
            return extension switch
            {
                ".json" => "schema",
                ".prefab" => "template",
                ".unity" => "template",
                ".txt" => "text",
                ".md" => "documentation",
                ".cs" => "code",
                ".shader" => "code",
                _ => "resource"
            };
        }

        #endregion
    }

    #endregion

    #region Simple Parsers

    /// <summary>
    /// Simple JSON parser that supports dictionaries and arrays.
    /// </summary>
    internal class SimpleJsonParser
    {
        private readonly string _json;
        private int _position;

        public SimpleJsonParser(string json)
        {
            _json = json ?? "";
            _position = 0;
        }

        public object Parse()
        {
            SkipWhitespace();
            return ParseValue();
        }

        private object ParseValue()
        {
            SkipWhitespace();
            if (_position >= _json.Length)
                return null;

            var c = _json[_position];
            return c switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => ParseString(),
                't' or 'f' => ParseBoolean(),
                'n' => ParseNull(),
                _ when char.IsDigit(c) || c == '-' => ParseNumber(),
                _ => null
            };
        }

        private Dictionary<string, object> ParseObject()
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _position++; // Skip '{'
            SkipWhitespace();

            while (_position < _json.Length && _json[_position] != '}')
            {
                SkipWhitespace();
                if (_json[_position] == '}')
                    break;

                var key = ParseString();
                SkipWhitespace();

                if (_position < _json.Length && _json[_position] == ':')
                    _position++;

                SkipWhitespace();
                var value = ParseValue();

                if (key != null)
                    dict[key] = value;

                SkipWhitespace();
                if (_position < _json.Length && _json[_position] == ',')
                    _position++;
            }

            if (_position < _json.Length && _json[_position] == '}')
                _position++;

            return dict;
        }

        private List<object> ParseArray()
        {
            var list = new List<object>();
            _position++; // Skip '['
            SkipWhitespace();

            while (_position < _json.Length && _json[_position] != ']')
            {
                var value = ParseValue();
                list.Add(value);

                SkipWhitespace();
                if (_position < _json.Length && _json[_position] == ',')
                    _position++;
                SkipWhitespace();
            }

            if (_position < _json.Length && _json[_position] == ']')
                _position++;

            return list;
        }

        private string ParseString()
        {
            if (_position >= _json.Length || _json[_position] != '"')
                return null;

            _position++; // Skip opening quote
            var sb = new StringBuilder();

            while (_position < _json.Length)
            {
                var c = _json[_position++];
                if (c == '"')
                    break;
                if (c == '\\' && _position < _json.Length)
                {
                    var escaped = _json[_position++];
                    sb.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        '/' => '/',
                        'b' => '\b',
                        'f' => '\f',
                        _ => escaped
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private object ParseNumber()
        {
            var start = _position;
            while (_position < _json.Length && (char.IsDigit(_json[_position]) || _json[_position] == '-' || _json[_position] == '.' || _json[_position] == 'e' || _json[_position] == 'E' || _json[_position] == '+'))
            {
                _position++;
            }

            var numStr = _json.Substring(start, _position - start);
            if (numStr.Contains('.') || numStr.Contains('e') || numStr.Contains('E'))
            {
                if (double.TryParse(numStr, out var d))
                    return d;
            }
            else
            {
                if (long.TryParse(numStr, out var l))
                    return l;
            }
            return 0;
        }

        private bool ParseBoolean()
        {
            if (_json.Substring(_position).StartsWith("true"))
            {
                _position += 4;
                return true;
            }
            if (_json.Substring(_position).StartsWith("false"))
            {
                _position += 5;
                return false;
            }
            return false;
        }

        private object ParseNull()
        {
            if (_json.Substring(_position).StartsWith("null"))
            {
                _position += 4;
            }
            return null;
        }

        private void SkipWhitespace()
        {
            while (_position < _json.Length && char.IsWhiteSpace(_json[_position]))
            {
                _position++;
            }
        }
    }

    /// <summary>
    /// Simple YAML parser for skill definitions.
    /// Supports basic YAML features: key-value pairs, lists, multi-line strings.
    /// </summary>
    internal class SimpleYamlParser
    {
        private readonly string[] _lines;
        private int _lineIndex;

        public SimpleYamlParser(string yaml)
        {
            _lines = (yaml ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            _lineIndex = 0;
        }

        public object Parse()
        {
            return ParseObject(0);
        }

        private Dictionary<string, object> ParseObject(int expectedIndent)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            while (_lineIndex < _lines.Length)
            {
                var line = _lines[_lineIndex];

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                {
                    _lineIndex++;
                    continue;
                }

                var indent = GetIndent(line);

                // If indent is less than expected, we're done with this object
                if (indent < expectedIndent)
                    break;

                // Skip if indent is greater than expected (shouldn't happen at root)
                if (indent > expectedIndent && expectedIndent >= 0)
                {
                    _lineIndex++;
                    continue;
                }

                var trimmed = line.TrimStart();

                // Check for list item
                if (trimmed.StartsWith("- "))
                {
                    // This is a list, but we expected an object - skip
                    _lineIndex++;
                    continue;
                }

                // Parse key-value pair
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex <= 0)
                {
                    _lineIndex++;
                    continue;
                }

                var key = trimmed.Substring(0, colonIndex).Trim();
                var valueStr = colonIndex < trimmed.Length - 1 ? trimmed.Substring(colonIndex + 1).Trim() : "";

                _lineIndex++;

                // Check if value is on same line
                if (!string.IsNullOrEmpty(valueStr))
                {
                    // Check for inline list: [item1, item2]
                    if (valueStr.StartsWith("[") && valueStr.EndsWith("]"))
                    {
                        dict[key] = ParseInlineList(valueStr);
                    }
                    // Check for multi-line string indicator
                    else if (valueStr == "|" || valueStr == ">")
                    {
                        dict[key] = ParseMultilineString(indent);
                    }
                    else
                    {
                        dict[key] = ParseScalarValue(valueStr);
                    }
                }
                else
                {
                    // Value is on next line(s) - could be nested object or list
                    var nextIndent = PeekNextIndent();
                    if (nextIndent > indent)
                    {
                        // Check if next line is a list item
                        var nextLine = PeekNextLine()?.TrimStart();
                        if (nextLine != null && nextLine.StartsWith("- "))
                        {
                            dict[key] = ParseList(nextIndent);
                        }
                        else
                        {
                            dict[key] = ParseObject(nextIndent);
                        }
                    }
                    else
                    {
                        dict[key] = null;
                    }
                }
            }

            return dict;
        }

        private List<object> ParseList(int expectedIndent)
        {
            var list = new List<object>();

            while (_lineIndex < _lines.Length)
            {
                var line = _lines[_lineIndex];

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                {
                    _lineIndex++;
                    continue;
                }

                var indent = GetIndent(line);

                // If indent is less than expected, we're done with this list
                if (indent < expectedIndent)
                    break;

                var trimmed = line.TrimStart();

                // Must be a list item
                if (!trimmed.StartsWith("- "))
                {
                    break;
                }

                var itemValue = trimmed.Substring(2).Trim();
                _lineIndex++;

                // Check if list item is a scalar or nested object
                if (string.IsNullOrEmpty(itemValue))
                {
                    // Nested object in list
                    var nextIndent = PeekNextIndent();
                    if (nextIndent > indent)
                    {
                        list.Add(ParseObject(nextIndent));
                    }
                    else
                    {
                        list.Add(null);
                    }
                }
                else if (itemValue.Contains(":"))
                {
                    // Inline object in list item: "- name: value"
                    var colonIdx = itemValue.IndexOf(':');
                    var key = itemValue.Substring(0, colonIdx).Trim();
                    var val = itemValue.Substring(colonIdx + 1).Trim();

                    // Check if there are more properties on subsequent lines
                    var nextIndent = PeekNextIndent();
                    if (nextIndent > indent + 2)
                    {
                        var nestedObj = ParseObject(nextIndent);
                        nestedObj[key] = ParseScalarValue(val);
                        list.Add(nestedObj);
                    }
                    else
                    {
                        var itemDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            [key] = ParseScalarValue(val)
                        };

                        // Check for more properties at same indent level
                        while (_lineIndex < _lines.Length)
                        {
                            var nextLine = _lines[_lineIndex];
                            if (string.IsNullOrWhiteSpace(nextLine))
                            {
                                _lineIndex++;
                                continue;
                            }

                            var nextLineIndent = GetIndent(nextLine);
                            if (nextLineIndent <= indent)
                                break;

                            var nextTrimmed = nextLine.TrimStart();
                            if (nextTrimmed.StartsWith("- "))
                                break;

                            var nextColonIdx = nextTrimmed.IndexOf(':');
                            if (nextColonIdx > 0)
                            {
                                var nextKey = nextTrimmed.Substring(0, nextColonIdx).Trim();
                                var nextVal = nextTrimmed.Substring(nextColonIdx + 1).Trim();
                                itemDict[nextKey] = ParseScalarValue(nextVal);
                                _lineIndex++;
                            }
                            else
                            {
                                break;
                            }
                        }

                        list.Add(itemDict);
                    }
                }
                else
                {
                    // Simple scalar value
                    list.Add(ParseScalarValue(itemValue));
                }
            }

            return list;
        }

        private List<object> ParseInlineList(string value)
        {
            var list = new List<object>();
            var content = value.Substring(1, value.Length - 2); // Remove [ and ]

            foreach (var item in content.Split(','))
            {
                var trimmed = item.Trim();
                list.Add(ParseScalarValue(trimmed));
            }

            return list;
        }

        private string ParseMultilineString(int baseIndent)
        {
            var sb = new StringBuilder();
            var contentStarted = false;
            var contentIndent = -1;

            while (_lineIndex < _lines.Length)
            {
                var line = _lines[_lineIndex];
                var indent = GetIndent(line);

                // Determine expected indent from first content line
                if (!contentStarted && !string.IsNullOrWhiteSpace(line))
                {
                    if (indent <= baseIndent)
                        break;
                    contentStarted = true;
                    contentIndent = indent;
                }

                if (contentStarted)
                {
                    if (!string.IsNullOrWhiteSpace(line) && indent <= baseIndent)
                        break;

                    // Preserve relative indentation
                    if (line.Length > contentIndent)
                    {
                        sb.AppendLine(line.Substring(contentIndent));
                    }
                    else if (string.IsNullOrWhiteSpace(line))
                    {
                        sb.AppendLine();
                    }
                    else
                    {
                        sb.AppendLine(line.TrimStart());
                    }
                }

                _lineIndex++;
            }

            return sb.ToString().TrimEnd();
        }

        private object ParseScalarValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            // Remove quotes
            if ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                (value.StartsWith("'") && value.EndsWith("'")))
            {
                return value.Substring(1, value.Length - 2);
            }

            // Parse booleans
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;

            // Parse null
            if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value == "~")
                return null;

            // Parse numbers
            if (long.TryParse(value, out var longVal))
                return longVal;
            if (double.TryParse(value, out var doubleVal))
                return doubleVal;

            return value;
        }

        private int GetIndent(string line)
        {
            int indent = 0;
            foreach (var c in line)
            {
                if (c == ' ')
                    indent++;
                else if (c == '\t')
                    indent += 2;
                else
                    break;
            }
            return indent;
        }

        private int PeekNextIndent()
        {
            for (int i = _lineIndex; i < _lines.Length; i++)
            {
                var line = _lines[i];
                if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"))
                {
                    return GetIndent(line);
                }
            }
            return 0;
        }

        private string PeekNextLine()
        {
            for (int i = _lineIndex; i < _lines.Length; i++)
            {
                var line = _lines[i];
                if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"))
                {
                    return line;
                }
            }
            return null;
        }
    }

    #endregion

    #region Tool Executors

    /// <summary>
    /// Lists all available skills from all search locations.
    /// </summary>
    public class SkillListExecutor : IToolExecutor
    {
        public string ToolId => "skill.list";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Check if force refresh is requested
                var forceRefresh = context.Arguments != null &&
                    context.Arguments.TryGetValue("refresh", out var refresh) &&
                    Convert.ToBoolean(refresh);

                var skills = SkillDiscovery.DiscoverSkills(forceRefresh);

                // Optional filtering
                string category = null;
                string tag = null;
                string search = null;

                if (context.Arguments != null)
                {
                    if (context.Arguments.TryGetValue("category", out var cat))
                        category = cat?.ToString();
                    if (context.Arguments.TryGetValue("tag", out var t))
                        tag = t?.ToString();
                    if (context.Arguments.TryGetValue("search", out var s))
                        search = s?.ToString();
                }

                var filteredSkills = skills.AsEnumerable();

                if (!string.IsNullOrEmpty(category))
                {
                    filteredSkills = filteredSkills.Where(s =>
                        s.Category != null && s.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    filteredSkills = filteredSkills.Where(s =>
                        s.Tags != null && s.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
                }

                if (!string.IsNullOrEmpty(search))
                {
                    var searchLower = search.ToLowerInvariant();
                    filteredSkills = filteredSkills.Where(s =>
                        (s.Name?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                        (s.Description?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                        (s.Id?.ToLowerInvariant().Contains(searchLower) ?? false));
                }

                var skillList = filteredSkills.Select(s => s.ToListOutput()).ToList();

                // Also return search locations info
                var locations = SkillDiscovery.GetSearchLocations()
                    .Select(loc => new
                    {
                        type = loc.location,
                        path = loc.path,
                        exists = Directory.Exists(loc.path)
                    })
                    .ToList();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    count = skillList.Count,
                    skills = skillList,
                    searchLocations = locations
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Reads the body/instructions of a specific skill.
    /// </summary>
    public class SkillReadBodyExecutor : IToolExecutor
    {
        public string ToolId => "skill.read_body";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (context.Arguments == null || !context.Arguments.TryGetValue("skillId", out var skillIdObj))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("skillId is required"));
                }

                var skillId = skillIdObj?.ToString();
                if (string.IsNullOrEmpty(skillId))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("skillId cannot be empty"));
                }

                var skill = SkillDiscovery.LoadSkill(skillId);
                if (skill == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Skill not found: {skillId}"));
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(skill.ToBodyOutput()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Reads a resource file from a skill.
    /// </summary>
    public class SkillReadResourceExecutor : IToolExecutor
    {
        public string ToolId => "skill.read_resource";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (context.Arguments == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("skillId and resourceName are required"));
                }

                if (!context.Arguments.TryGetValue("skillId", out var skillIdObj) || string.IsNullOrEmpty(skillIdObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("skillId is required"));
                }

                if (!context.Arguments.TryGetValue("resourceName", out var resourceNameObj) || string.IsNullOrEmpty(resourceNameObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("resourceName is required"));
                }

                var skillId = skillIdObj.ToString();
                var resourceName = resourceNameObj.ToString();

                // Verify skill exists
                var skill = SkillDiscovery.LoadSkill(skillId);
                if (skill == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Skill not found: {skillId}"));
                }

                var (content, fullPath, resourceType) = SkillDiscovery.GetSkillResource(skillId, resourceName);

                if (content == null)
                {
                    // List available resources
                    var availableResources = skill.Resources?.Keys.ToList() ?? new List<string>();
                    return Task.FromResult(ToolExecutionResult.Failed(
                        $"Resource not found: {resourceName}. Available resources: {string.Join(", ", availableResources)}"));
                }

                // Truncate large content
                var maxLength = 100000;
                var truncated = false;
                if (content.Length > maxLength)
                {
                    content = content.Substring(0, maxLength) + "\n\n[TRUNCATED]";
                    truncated = true;
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    skillId,
                    resourceName,
                    content,
                    path = fullPath,
                    type = resourceType,
                    size = new FileInfo(fullPath).Length,
                    truncated
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    #endregion
}
