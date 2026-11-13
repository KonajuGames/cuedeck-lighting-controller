using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuedeck.Common;

/// <summary>
/// Reading sLight's JSON as leniently as its GDScript did. Godot reads every JSON number as a float, and the show code
/// converted with <c>int()</c>, <c>float()</c>, <c>bool()</c> and <c>String()</c>, so a value saved as 3, 3.0 or "3"
/// all read back as 3. These do the same, falling back to a default where the key's missing or the value can't be
/// read.
/// </summary>
static class Json
{
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 };

    /// <summary>
    /// <paramref name="node"/> as compact JSON text, as Godot's <c>JSON.stringify()</c> without an indent.
    /// </summary>
    public static string Stringify(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    /// <summary>
    /// <paramref name="node"/> as tab-indented JSON text, as sLight saved show files.
    /// </summary>
    public static string StringifyIndented(JsonNode? node) => node?.ToJsonString(Indented) ?? "null";

    /// <summary>
    /// Parses <paramref name="text"/>, or null if it isn't JSON.
    /// </summary>
    public static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The value at <paramref name="key"/>, or null if <paramref name="obj"/> isn't an object or hasn't the key.
    /// </summary>
    public static JsonNode? Get(this JsonNode? obj, string key) => obj is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static bool Has(this JsonNode? obj, string key) => obj is JsonObject o && o.ContainsKey(key);

    /// <summary>
    /// <c>String(d.get(key, fallback))</c>. Godot's String() of a number writes it out; this does too.
    /// </summary>
    public static string Str(this JsonNode? obj, string key, string fallback = "") => AsString(obj.Get(key), fallback);

    public static string AsString(JsonNode? node, string fallback = "")
    {
        if (node is not JsonValue v)
            return node is null ? fallback : Stringify(node);
        switch (v.GetValueKind())
        {
            case JsonValueKind.String:
                return v.GetValue<string>();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                var d = Number(v);
                return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);
            default:
                return fallback;
        }
    }

    /// <summary>
    /// A JSON number's value as a double, however it was made (parsed, or from an int or a float in code).
    /// </summary>
    static double Number(JsonValue v) => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// The value of <paramref name="v"/> as Godot would convert it to a number: numbers as they are, booleans as 0 or
    /// 1, strings parsed (0 if they aren't numbers); null for anything else.
    /// </summary>
    static double? Numeric(JsonValue v) =>
        v.GetValueKind() switch
        {
            JsonValueKind.Number => Number(v),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String => double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => null,
        };

    /// <summary>
    /// <c>float(d.get(key, fallback))</c>.
    /// </summary>
    public static double Num(this JsonNode? obj, string key, double fallback = 0) => AsDouble(obj.Get(key), fallback);

    public static double AsDouble(JsonNode? node, double fallback = 0)
    {
        return node is JsonValue v && Numeric(v) is { } d ? d : fallback;
    }

    /// <summary>
    /// <c>int(d.get(key, fallback))</c>: a fractional number truncated towards zero, as Godot's int() does.
    /// </summary>
    public static int Int(this JsonNode? obj, string key, int fallback = 0) => AsInt(obj.Get(key), fallback);

    public static int AsInt(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue v)
            return fallback;
        if (v.GetValueKind() == JsonValueKind.String)
        {
            var s = v.GetValue<string>().Trim();
            return IsValidInt(s) ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
        }
        return Numeric(v) is { } d ? (double.IsFinite(d) ? (int)Math.Clamp(Math.Truncate(d), int.MinValue, int.MaxValue) : 0) : fallback;
    }

    /// <summary>
    /// <c>bool(d.get(key, fallback))</c>: a number is true unless it's 0.
    /// </summary>
    public static bool Bool(this JsonNode? obj, string key, bool fallback = false) => AsBool(obj.Get(key), fallback);

    public static bool AsBool(JsonNode? node, bool fallback = false)
    {
        if (node is not JsonValue v)
            return fallback;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Number(v) != 0,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            _ => fallback,
        };
    }

    /// <summary>
    /// The array at <paramref name="key"/>, or an empty one.
    /// </summary>
    public static JsonArray Arr(this JsonNode? obj, string key) => obj.Get(key) as JsonArray ?? [];

    /// <summary>
    /// The object at <paramref name="key"/>, or null.
    /// </summary>
    public static JsonObject? Obj(this JsonNode? obj, string key) => obj.Get(key) as JsonObject;

    /// <summary>
    /// GDScript's <c>String.is_valid_int()</c>: an optional sign then digits only.
    /// </summary>
    public static bool IsValidInt(string s)
    {
        var t = s.AsSpan();
        if (t.Length > 0 && (t[0] == '-' || t[0] == '+'))
            t = t[1..];
        return t.Length > 0 && t.IndexOfAnyExceptInRange('0', '9') < 0 && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// A deep copy of <paramref name="node"/>, or null.
    /// </summary>
    public static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

    /// <summary>
    /// Whether two JSON trees hold the same things, comparing numbers by value (3 and 3.0 alike), as two of Godot's
    /// parsed dictionaries would compare.
    /// </summary>
    public static bool SameTree(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject oa, JsonObject ob):
                return oa.Count == ob.Count && oa.All(kv => ob.TryGetPropertyValue(kv.Key, out var other) && SameTree(kv.Value, other));
            case (JsonArray aa, JsonArray ab):
                return aa.Count == ab.Count && aa.Zip(ab).All(p => SameTree(p.First, p.Second));
            case (JsonValue va, JsonValue vb):
                if (va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
                    return Number(va) == Number(vb);
                return va.ToJsonString() == vb.ToJsonString();
            default:
                return false;
        }
    }
}
