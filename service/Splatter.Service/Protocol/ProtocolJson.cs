// Canonical wire-protocol JSON options

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Splatter.Service.Protocol;

/// <summary>
/// The single source of truth for how the Unity client and the local service
/// (de)serialize messages on the wire. Both the transport (which serializes the
/// <see cref="MessageEnvelope"/>) and the API handler (which deserializes payloads)
/// use these options so the two can never drift:
/// <list type="bullet">
///   <item>camelCase property names</item>
///   <item>case-insensitive reads (tolerant of client casing)</item>
///   <item>enums serialized as strings (no dependency on ordinal values)</item>
/// </list>
/// The Unity client must produce/consume JSON that matches these options.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = CamelCaseFromAny.Instance,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Converts a C# member name to its camelCase wire form, handling BOTH PascalCase
    /// members (<c>CorrelationId</c> → <c>correlationId</c>) and snake_case payload keys
    /// emitted by anonymous objects (<c>conversation_id</c> → <c>conversationId</c>).
    /// This mirrors the Unity client's <c>SnakeToCamelNamingStrategy</c> exactly, so
    /// payloads align in both directions. The built-in <see cref="JsonNamingPolicy.CamelCase"/>
    /// only lowercases the first character and leaves underscores intact, which would
    /// leave <c>conversation_id</c> as-is and break correlation with the client.
    /// </summary>
    private sealed class CamelCaseFromAny : JsonNamingPolicy
    {
        public static readonly CamelCaseFromAny Instance = new();

        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var sb = new StringBuilder(name.Length);
            foreach (var part in name.Split('_'))
            {
                if (part.Length == 0)
                    continue;
                if (sb.Length == 0)
                    sb.Append(char.ToLowerInvariant(part[0])).Append(part.Substring(1));
                else
                    sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1));
            }
            return sb.Length == 0 ? name : sb.ToString();
        }
    }
}
