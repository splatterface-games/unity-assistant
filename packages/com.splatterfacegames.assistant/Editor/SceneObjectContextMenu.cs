// Hierarchy right-click integration: copy a chat link to an object. Items under
// "GameObject/Splatterface Games/Assistant/..." appear in both the Hierarchy context menu and the top
// GameObject menu. Paste the link into an agent terminal to reference the object.

using UnityEditor;
using UnityEngine;
using Splatter.Editor.Tools;

namespace Splatter.Editor
{
    internal static class SceneObjectContextMenu
    {
        [MenuItem("GameObject/Splatterface Games/Assistant/Copy Object Link", false, 30)]
        private static void CopyLink()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;
            EditorGUIUtility.systemCopyBuffer = SceneObjectRef.MarkdownLink(go);
        }

        [MenuItem("GameObject/Splatterface Games/Assistant/Copy Object Link", true)]
        private static bool ValidateCopy() => Selection.activeGameObject != null;

        // Project-window equivalent for assets (meshes, materials, prefabs, ...).
        // GlobalObjectId covers assets too.
        [MenuItem("Assets/Splatterface Games/Assistant/Copy Object Link", false, 30)]
        private static void CopyAssetLink()
        {
            var obj = Selection.activeObject;
            if (obj != null) EditorGUIUtility.systemCopyBuffer = SceneObjectRef.MarkdownLink(obj);
        }

        [MenuItem("Assets/Splatterface Games/Assistant/Copy Object Link", true)]
        private static bool ValidateAsset() => Selection.activeObject != null;
    }
}
