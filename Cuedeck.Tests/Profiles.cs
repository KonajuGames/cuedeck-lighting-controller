using System.Text.Json.Nodes;
using Cuedeck.Fixtures;

namespace Cuedeck.Tests;

/// <summary>
/// Fixture profiles for tests, from sLight's channel JSON.
/// </summary>
static class Profiles
{
    /// <summary>
    /// A profile with one mode of <paramref name="channelsJson"/>, a JSON list of channels.
    /// </summary>
    public static FixtureProfile Make(string id, string model, string channelsJson, string? physicalJson = null, string manufacturer = "") =>
        new(id, model, (JsonArray)JsonNode.Parse(channelsJson)!, null, physicalJson is null ? null : (JsonObject)JsonNode.Parse(physicalJson)!, manufacturer);

    /// <summary>
    /// A profile of channels given as name and role.
    /// </summary>
    public static FixtureProfile Simple(string id, string model, params (string Name, string Role)[] channels) =>
        new(id, model, [.. channels.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["role"] = c.Role })]);

    public static FixtureProfile Rgb() => Simple("rgb3", "RGB", ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));

    public static FixtureProfile Rgbw() =>
        Simple("rgbw", "RGBW par", ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"), ("White", "WHITE"), ("Strobe", "STROBE"));

    public static FixtureProfile DimmedRgb() =>
        Simple("drgb", "Dimmer RGB par", ("Dimmer", "DIMMER"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));
}
