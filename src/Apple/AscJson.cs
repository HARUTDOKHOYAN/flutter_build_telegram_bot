using System.Globalization;
using System.Text.Json.Nodes;

namespace BuildChatBot.Apple;

/// <summary>
/// Defensive accessors over <see cref="JsonNode"/>. App Store Connect payloads are read
/// with these helpers instead of a typed model so that unexpected shapes (a missing
/// <c>attributes</c> object, a null relationship, a numeric field where a string was
/// expected) degrade to <c>null</c> rather than throwing.
/// </summary>
internal static class AscJson
{
    /// <summary>Child property of an object node, or null when the node is not an object / has no such member.</summary>
    public static JsonNode? Prop(JsonNode? node, string name)
        => node is JsonObject o && o.TryGetPropertyValue(name, out var value) ? value : null;

    /// <summary>Walk a chain of property names (<c>Path(root, "data", "attributes", "version")</c>).</summary>
    public static JsonNode? Path(JsonNode? node, params string[] names)
    {
        var current = node;
        foreach (var name in names)
        {
            current = Prop(current, name);
            if (current is null) return null;
        }
        return current;
    }

    /// <summary>Child property as an array, or null.</summary>
    public static JsonArray? Array(JsonNode? node, string name) => Prop(node, name) as JsonArray;

    /// <summary>String value of a node (numbers/bools are stringified), or null.</summary>
    public static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        return value.TryGetValue<string>(out var s) ? s : value.ToString();
    }

    /// <summary>String value of <paramref name="name"/> on an object node, or null.</summary>
    public static string? Str(JsonNode? node, string name) => Str(Prop(node, name));

    public static bool Bool(JsonNode? node, string name, bool fallback = false)
        => Prop(node, name) is JsonValue value && value.TryGetValue<bool>(out var b) ? b : fallback;

    public static DateTimeOffset? Date(JsonNode? node, string name)
    {
        var raw = Str(node, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
