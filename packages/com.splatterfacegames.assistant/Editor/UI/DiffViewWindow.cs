// Diff View Window - Review proposed code changes before applying

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.UI
{
    /// <summary>
    /// Window for reviewing proposed code changes in diff format.
    /// </summary>
    public class DiffViewWindow : EditorWindow
    {
        private static DiffViewWindow _instance;

        // Diff data
        private string _filePath;
        private string _description;
        private string _oldContent;
        private string _newContent;
        private List<DiffLine> _diffLines;
        private string _patchId;

        // UI state
        private Vector2 _scrollPosition;
        private bool _sideBySide = false;
        private float _splitPosition = 0.5f;
        private bool _showLineNumbers = true;

        // Callbacks
        private Action<bool> _onDecision;

        // Styles
        private GUIStyle _addedLineStyle;
        private GUIStyle _removedLineStyle;
        private GUIStyle _contextLineStyle;
        private GUIStyle _lineNumberStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _filePathStyle;
        private bool _stylesInitialized;

        // Colors
        private static readonly Color AddedBgColor = new Color(0.2f, 0.4f, 0.2f, 0.3f);
        private static readonly Color RemovedBgColor = new Color(0.4f, 0.2f, 0.2f, 0.3f);
        private static readonly Color AddedTextColor = new Color(0.4f, 0.9f, 0.4f);
        private static readonly Color RemovedTextColor = new Color(0.9f, 0.4f, 0.4f);

        /// <summary>
        /// Show the diff view window with the given diff data.
        /// </summary>
        public static void ShowDiff(
            string filePath,
            string description,
            string oldContent,
            string newContent,
            string patchId,
            Action<bool> onDecision)
        {
            if (_instance != null)
            {
                _instance.Close();
            }

            _instance = CreateInstance<DiffViewWindow>();
            _instance.titleContent = new GUIContent("Review Changes");
            _instance._filePath = filePath;
            _instance._description = description;
            _instance._oldContent = oldContent ?? "";
            _instance._newContent = newContent ?? "";
            _instance._patchId = patchId;
            _instance._onDecision = onDecision;
            _instance._diffLines = ComputeDiff(oldContent ?? "", newContent ?? "");

            // Size and position
            var windowSize = new Vector2(800, 600);
            var screenCenter = new Vector2(Screen.currentResolution.width / 2f, Screen.currentResolution.height / 2f);
            _instance.position = new Rect(screenCenter - windowSize / 2f, windowSize);
            _instance.minSize = new Vector2(500, 400);

            _instance.ShowUtility();
            _instance.Focus();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _addedLineStyle = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                wordWrap = false,
                padding = new RectOffset(4, 4, 2, 2),
                margin = new RectOffset(0, 0, 0, 0),
                font = GetMonospaceFont()
            };

            _removedLineStyle = new GUIStyle(_addedLineStyle);
            _contextLineStyle = new GUIStyle(_addedLineStyle);

            _lineNumberStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(4, 8, 2, 2),
                font = GetMonospaceFont()
            };
            _lineNumberStyle.normal.textColor = Color.gray;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                padding = new RectOffset(5, 5, 5, 5)
            };

            _filePathStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Italic
            };
            _filePathStyle.normal.textColor = new Color(0.6f, 0.8f, 1f);

            _stylesInitialized = true;
        }

        private Font GetMonospaceFont()
        {
            // Try to get a monospace font
            var font = Font.CreateDynamicFontFromOSFont("Consolas", 12);
            if (font == null)
                font = Font.CreateDynamicFontFromOSFont("Monaco", 12);
            if (font == null)
                font = Font.CreateDynamicFontFromOSFont("Courier New", 12);
            return font;
        }

        private void OnGUI()
        {
            InitializeStyles();

            EditorGUILayout.BeginVertical();

            // Header
            DrawHeader();

            // Toolbar
            DrawToolbar();

            // Diff content
            DrawDiffContent();

            // Action buttons
            DrawActionButtons();

            EditorGUILayout.EndVertical();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Proposed Changes", _headerStyle);
            EditorGUILayout.LabelField(_filePath, _filePathStyle);

            if (!string.IsNullOrEmpty(_description))
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField(_description, EditorStyles.wordWrappedLabel);
            }

            // Stats
            var additions = 0;
            var deletions = 0;
            foreach (var line in _diffLines)
            {
                if (line.Type == DiffLineType.Added) additions++;
                else if (line.Type == DiffLineType.Removed) deletions++;
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            var statsStyle = new GUIStyle(EditorStyles.miniLabel);
            statsStyle.normal.textColor = AddedTextColor;
            EditorGUILayout.LabelField($"+{additions}", statsStyle, GUILayout.Width(50));
            statsStyle.normal.textColor = RemovedTextColor;
            EditorGUILayout.LabelField($"-{deletions}", statsStyle, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            _showLineNumbers = GUILayout.Toggle(_showLineNumbers, "Line Numbers", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();

            // View mode toggle
            if (GUILayout.Button(_sideBySide ? "Unified" : "Side-by-Side", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                _sideBySide = !_sideBySide;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawDiffContent()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.ExpandHeight(true));

            if (_sideBySide)
            {
                DrawSideBySideDiff();
            }
            else
            {
                DrawUnifiedDiff();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawUnifiedDiff()
        {
            var lineNumWidth = _showLineNumbers ? 50f : 0f;

            foreach (var line in _diffLines)
            {
                var rect = EditorGUILayout.BeginHorizontal(GUILayout.Height(18));

                // Background color
                Color bgColor = line.Type switch
                {
                    DiffLineType.Added => AddedBgColor,
                    DiffLineType.Removed => RemovedBgColor,
                    _ => Color.clear
                };

                if (bgColor != Color.clear)
                {
                    EditorGUI.DrawRect(rect, bgColor);
                }

                // Line number
                if (_showLineNumbers)
                {
                    var lineNumText = line.Type switch
                    {
                        DiffLineType.Added => $"+{line.NewLineNum}",
                        DiffLineType.Removed => $"-{line.OldLineNum}",
                        _ => line.NewLineNum > 0 ? line.NewLineNum.ToString() : ""
                    };
                    EditorGUILayout.LabelField(lineNumText, _lineNumberStyle, GUILayout.Width(lineNumWidth));
                }

                // Prefix
                var prefix = line.Type switch
                {
                    DiffLineType.Added => "+ ",
                    DiffLineType.Removed => "- ",
                    _ => "  "
                };

                // Content with color
                var textColor = line.Type switch
                {
                    DiffLineType.Added => AddedTextColor,
                    DiffLineType.Removed => RemovedTextColor,
                    _ => Color.white
                };

                var style = new GUIStyle(_contextLineStyle);
                style.normal.textColor = textColor;

                EditorGUILayout.LabelField(prefix + line.Content, style);

                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSideBySideDiff()
        {
            var availableWidth = position.width - 20;
            var halfWidth = availableWidth / 2 - 5;
            var lineNumWidth = _showLineNumbers ? 40f : 0f;

            // Build side-by-side pairs
            var leftLines = new List<(int lineNum, string content, DiffLineType type)>();
            var rightLines = new List<(int lineNum, string content, DiffLineType type)>();

            foreach (var line in _diffLines)
            {
                switch (line.Type)
                {
                    case DiffLineType.Context:
                        leftLines.Add((line.OldLineNum, line.Content, DiffLineType.Context));
                        rightLines.Add((line.NewLineNum, line.Content, DiffLineType.Context));
                        break;
                    case DiffLineType.Removed:
                        leftLines.Add((line.OldLineNum, line.Content, DiffLineType.Removed));
                        rightLines.Add((0, "", DiffLineType.Context));
                        break;
                    case DiffLineType.Added:
                        leftLines.Add((0, "", DiffLineType.Context));
                        rightLines.Add((line.NewLineNum, line.Content, DiffLineType.Added));
                        break;
                }
            }

            // Header row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Original", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(halfWidth));
            EditorGUILayout.LabelField("Modified", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(halfWidth));
            EditorGUILayout.EndHorizontal();

            // Content rows
            for (int i = 0; i < leftLines.Count; i++)
            {
                var left = leftLines[i];
                var right = rightLines[i];

                EditorGUILayout.BeginHorizontal(GUILayout.Height(18));

                // Left side
                DrawSideBySideLine(left.lineNum, left.content, left.type, halfWidth, lineNumWidth, true);

                // Separator
                GUILayout.Space(2);
                var sepRect = GUILayoutUtility.GetRect(1, 18, GUILayout.Width(1));
                EditorGUI.DrawRect(sepRect, Color.gray);
                GUILayout.Space(2);

                // Right side
                DrawSideBySideLine(right.lineNum, right.content, right.type, halfWidth, lineNumWidth, false);

                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSideBySideLine(int lineNum, string content, DiffLineType type, float width, float lineNumWidth, bool isLeft)
        {
            var rect = GUILayoutUtility.GetRect(width, 18);

            // Background
            var bgColor = type switch
            {
                DiffLineType.Added => AddedBgColor,
                DiffLineType.Removed => RemovedBgColor,
                _ => Color.clear
            };

            if (bgColor != Color.clear)
            {
                EditorGUI.DrawRect(rect, bgColor);
            }

            // Line number
            if (_showLineNumbers && lineNum > 0)
            {
                var numRect = new Rect(rect.x, rect.y, lineNumWidth, rect.height);
                EditorGUI.LabelField(numRect, lineNum.ToString(), _lineNumberStyle);
                rect.x += lineNumWidth;
                rect.width -= lineNumWidth;
            }

            // Content
            var textColor = type switch
            {
                DiffLineType.Added => AddedTextColor,
                DiffLineType.Removed => RemovedTextColor,
                _ => Color.white
            };

            var style = new GUIStyle(_contextLineStyle);
            style.normal.textColor = textColor;

            EditorGUI.LabelField(rect, content, style);
        }

        private void DrawActionButtons()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();

            GUILayout.FlexibleSpace();

            // Reject button
            var rejectContent = new GUIContent("Reject", "Discard these changes");
            if (GUILayout.Button(rejectContent, GUILayout.Width(100), GUILayout.Height(30)))
            {
                _onDecision?.Invoke(false);
                Close();
            }

            GUILayout.Space(20);

            // Accept button
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.6f, 0.3f);
            var acceptContent = new GUIContent("Accept", "Apply these changes");
            if (GUILayout.Button(acceptContent, GUILayout.Width(100), GUILayout.Height(30)))
            {
                _onDecision?.Invoke(true);
                Close();
            }
            GUI.backgroundColor = prevBg;

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(10);
        }

        #region Diff Algorithm

        private static List<DiffLine> ComputeDiff(string oldContent, string newContent)
        {
            var oldLines = oldContent.Split('\n');
            var newLines = newContent.Split('\n');

            // Simple LCS-based diff
            var lcs = ComputeLCS(oldLines, newLines);
            var result = new List<DiffLine>();

            int oldIdx = 0, newIdx = 0, lcsIdx = 0;

            while (oldIdx < oldLines.Length || newIdx < newLines.Length)
            {
                if (lcsIdx < lcs.Count)
                {
                    var (lcsOld, lcsNew) = lcs[lcsIdx];

                    // Output removed lines (in old but not at LCS position yet)
                    while (oldIdx < lcsOld)
                    {
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Removed,
                            Content = oldLines[oldIdx].TrimEnd('\r'),
                            OldLineNum = oldIdx + 1,
                            NewLineNum = 0
                        });
                        oldIdx++;
                    }

                    // Output added lines (in new but not at LCS position yet)
                    while (newIdx < lcsNew)
                    {
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Added,
                            Content = newLines[newIdx].TrimEnd('\r'),
                            OldLineNum = 0,
                            NewLineNum = newIdx + 1
                        });
                        newIdx++;
                    }

                    // Output context line (matching LCS line)
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Context,
                        Content = oldLines[oldIdx].TrimEnd('\r'),
                        OldLineNum = oldIdx + 1,
                        NewLineNum = newIdx + 1
                    });
                    oldIdx++;
                    newIdx++;
                    lcsIdx++;
                }
                else
                {
                    // No more LCS - remaining old lines are removed
                    while (oldIdx < oldLines.Length)
                    {
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Removed,
                            Content = oldLines[oldIdx].TrimEnd('\r'),
                            OldLineNum = oldIdx + 1,
                            NewLineNum = 0
                        });
                        oldIdx++;
                    }

                    // Remaining new lines are added
                    while (newIdx < newLines.Length)
                    {
                        result.Add(new DiffLine
                        {
                            Type = DiffLineType.Added,
                            Content = newLines[newIdx].TrimEnd('\r'),
                            OldLineNum = 0,
                            NewLineNum = newIdx + 1
                        });
                        newIdx++;
                    }
                }
            }

            return result;
        }

        private static List<(int oldIdx, int newIdx)> ComputeLCS(string[] oldLines, string[] newLines)
        {
            int m = oldLines.Length;
            int n = newLines.Length;

            // DP table
            var dp = new int[m + 1, n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (oldLines[i - 1].TrimEnd('\r') == newLines[j - 1].TrimEnd('\r'))
                    {
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    }
                    else
                    {
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                    }
                }
            }

            // Backtrack to find LCS
            var result = new List<(int, int)>();
            int x = m, y = n;

            while (x > 0 && y > 0)
            {
                if (oldLines[x - 1].TrimEnd('\r') == newLines[y - 1].TrimEnd('\r'))
                {
                    result.Add((x - 1, y - 1));
                    x--;
                    y--;
                }
                else if (dp[x - 1, y] > dp[x, y - 1])
                {
                    x--;
                }
                else
                {
                    y--;
                }
            }

            result.Reverse();
            return result;
        }

        #endregion

        #region Data Types

        private enum DiffLineType
        {
            Context,
            Added,
            Removed
        }

        private class DiffLine
        {
            public DiffLineType Type;
            public string Content;
            public int OldLineNum;
            public int NewLineNum;
        }

        #endregion
    }
}
