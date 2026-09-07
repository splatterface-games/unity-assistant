// Stable, reload-survivable references to scene/asset objects.
//
// Backs both directions of "object hyperlinks":
//  - tool outputs include a globalId so the assistant can emit links and reference
//    objects durably,
//  - chat links / context attachments use the splatter://obj/<globalId> URI, which
//    resolves back to the live object.
//
// GlobalObjectId is Unity's own serializable handle (scene GUID + local file id); it
// survives domain reloads and editor restarts, unlike GetInstanceID().

using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatter.Editor.Tools
{
    internal static class SceneObjectRef
    {
        public const string Scheme = "splatter://obj/";

        /// <summary>Stable id string for an object, or null if it has none.</summary>
        public static string GlobalId(Object obj)
        {
            if (obj == null) return null;
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            // identifierType 0 == "null"/unknown (e.g. a transient object).
            return gid.identifierType == 0 ? null : gid.ToString();
        }

        /// <summary>The splatter://obj/&lt;id&gt; URI for an object.</summary>
        public static string Uri(Object obj)
        {
            var id = GlobalId(obj);
            return id == null ? null : Scheme + id;
        }

        /// <summary>A markdown link "[name](splatter://obj/&lt;id&gt;)", or just the name if unlinkable.</summary>
        public static string MarkdownLink(Object obj)
        {
            if (obj == null) return "";
            var uri = Uri(obj);
            return uri == null ? obj.name : $"[{obj.name}]({uri})";
        }

        /// <summary>Resolves a splatter://obj/ URI to the live object. Accepts a GlobalObjectId
        /// (durable) or a bare instanceId (session-only fallback, e.g. a just-created object
        /// whose tool result only carried instanceId).</summary>
        public static Object Resolve(string idOrUri)
        {
            if (string.IsNullOrEmpty(idOrUri)) return null;
            var s = idOrUri.StartsWith(Scheme) ? idOrUri.Substring(Scheme.Length) : idOrUri;

            if (GlobalObjectId.TryParse(s, out var gid))
            {
                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (obj != null) return obj;
            }

            // Fallback: a bare instanceId. Valid within the editor session.
            if (int.TryParse(s, out var instanceId) && instanceId != 0)
                return EditorUtility.InstanceIDToObject(instanceId);

            return null;
        }
    }
}
