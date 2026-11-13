using System.Text.Json.Nodes;

namespace Cuedeck.Fixtures;

/// <summary>
/// The fixture profiles that come with the app, with illustrative value ranges. Real fixtures differ.
/// </summary>
static class BuiltInProfiles
{
    static JsonObject Ch(string name, string role, int? @default = null, bool fine = false, JsonArray? ranges = null)
    {
        var c = new JsonObject { ["name"] = name, ["role"] = role };
        if (@default is { } d)
            c["default"] = d;
        if (fine)
            c["fine"] = true;
        if (ranges is not null)
            c["ranges"] = ranges;
        return c;
    }

    static JsonObject Slot(int lo, int hi, string label, string? color = null)
    {
        var s = new JsonObject { ["lo"] = lo, ["hi"] = hi, ["label"] = label };
        if (color is not null)
            s["color"] = color;
        return s;
    }

    static JsonArray StrobeRanges() =>
        [Slot(0, 7, "Shutter closed"), Slot(8, 15, "Shutter open"), Slot(16, 131, "Strobe slow-fast"), Slot(132, 255, "Shutter open")];

    static JsonArray ColorWheelRanges() =>
        [
            Slot(0, 9, "Open / white", "#ffffff"),
            Slot(10, 19, "Red", "#e01f1f"),
            Slot(20, 29, "Orange", "#f07a10"),
            Slot(30, 39, "Yellow", "#f2d011"),
            Slot(40, 49, "Green", "#1fae3d"),
            Slot(50, 59, "Blue", "#1f52e0"),
            Slot(60, 69, "Magenta", "#d016b0"),
        ];

    static JsonArray GoboRanges() =>
        [Slot(0, 7, "Open"), Slot(8, 15, "Gobo 1"), Slot(16, 23, "Gobo 2"), Slot(24, 31, "Gobo 3"), Slot(32, 39, "Gobo 4"), Slot(40, 47, "Gobo 5")];

    public static IReadOnlyList<FixtureProfile> All()
    {
        List<FixtureProfile> list =
        [
            new("dimmer1", "Dimmer (1ch)", [Ch("Dimmer", Roles.Dimmer)]),
            new("rgb3", "RGB (3ch)", [Ch("Red", Roles.Red), Ch("Green", Roles.Green), Ch("Blue", Roles.Blue)]),
            new("rgbw4", "RGBW (4ch)", [Ch("Red", Roles.Red), Ch("Green", Roles.Green), Ch("Blue", Roles.Blue), Ch("White", Roles.White)]),
            new("rgbaw5", "RGBAW (5ch)", [Ch("Red", Roles.Red), Ch("Green", Roles.Green), Ch("Blue", Roles.Blue), Ch("Amber", Roles.Amber), Ch("White", Roles.White)]),
            new(
                "mh_rgbw_pt7",
                "Moving Head RGBW Pan/Tilt (7ch)",
                [Ch("Pan", Roles.Pan), Ch("Tilt", Roles.Tilt), Ch("Dimmer", Roles.Dimmer), Ch("Red", Roles.Red), Ch("Green", Roles.Green), Ch("Blue", Roles.Blue), Ch("White", Roles.White)]
            ),
            // The same fixture in an 8-channel and a 16-bit 14-channel personality: fine channels, value slots and
            // per-channel defaults all at once.
            new(
                "mh_spot",
                "Moving Head Spot (multi-mode)",
                null,
                [
                    new JsonObject
                    {
                        ["name"] = "8 ch",
                        ["channels"] = new JsonArray(
                            Ch("Pan", Roles.Pan, 128),
                            Ch("Tilt", Roles.Tilt, 128),
                            Ch("Dimmer", Roles.Dimmer),
                            Ch("Shutter", Roles.Strobe, 12, ranges: StrobeRanges()),
                            Ch("Colour Wheel", Roles.ColorWheel, ranges: ColorWheelRanges()),
                            Ch("Gobo Wheel", Roles.Gobo, ranges: GoboRanges()),
                            Ch("Gobo Rotation", Roles.Generic),
                            Ch("Pan/Tilt Speed", Roles.Generic)
                        ),
                    },
                    new JsonObject
                    {
                        ["name"] = "14 ch (16-bit)",
                        ["channels"] = new JsonArray(
                            Ch("Pan", Roles.Pan, 128),
                            Ch("Pan Fine", Roles.PanFine, fine: true),
                            Ch("Tilt", Roles.Tilt, 128),
                            Ch("Tilt Fine", Roles.TiltFine, fine: true),
                            Ch("Pan/Tilt Speed", Roles.Generic),
                            Ch("Dimmer", Roles.Dimmer),
                            Ch("Dimmer Fine", Roles.Generic, fine: true),
                            Ch("Shutter", Roles.Strobe, 12, ranges: StrobeRanges()),
                            Ch("Colour Wheel", Roles.ColorWheel, ranges: ColorWheelRanges()),
                            Ch("Gobo Wheel", Roles.Gobo, ranges: GoboRanges()),
                            Ch("Gobo Rotation", Roles.Generic),
                            Ch("Prism", Roles.Generic),
                            Ch("Focus", Roles.Generic, 128),
                            Ch("Function", Roles.Generic)
                        ),
                    },
                ]
            ),
        ];
        foreach (var p in list)
            p.Manufacturer = "Generic";
        return list;
    }
}
