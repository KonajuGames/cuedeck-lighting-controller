namespace Cuedeck.Fixtures;

/// <summary>
/// What a fixture's channel does, as sLight's profiles name it.
/// </summary>
static class Roles
{
    public const string Dimmer = "DIMMER";
    public const string Red = "RED";
    public const string Green = "GREEN";
    public const string Blue = "BLUE";
    public const string White = "WHITE";
    public const string Amber = "AMBER";
    public const string Uv = "UV";
    public const string Pan = "PAN";
    public const string PanFine = "PAN_FINE";
    public const string Tilt = "TILT";
    public const string TiltFine = "TILT_FINE";
    public const string Strobe = "STROBE";
    public const string Zoom = "ZOOM";
    public const string Gobo = "GOBO";
    public const string GoboRotation = "GOBO_ROT";
    public const string ColorWheel = "COLOR_WHEEL";
    public const string Generic = "GENERIC";

    public static readonly string[] All =
    [
        Dimmer, Red, Green, Blue, White, Amber, Uv, Pan, PanFine, Tilt, TiltFine, Strobe, Zoom, Gobo, GoboRotation, ColorWheel, Generic,
    ];

    /// <summary>
    /// The colour and white roles a virtual dimmer scales on a fixture without a DIMMER of its own.
    /// </summary>
    public static readonly string[] VirtualDimmer = [Red, Green, Blue, White, Amber, Uv];

    /// <summary>
    /// The colour and white roles: a fixture's intensity when it has no DIMMER.
    /// </summary>
    public static readonly string[] Colour = [Red, Green, Blue, White, Amber, Uv];

    /// <summary>
    /// Every role that sets how much light comes out: the dimmer and the colours.
    /// </summary>
    public static readonly string[] Intensity = [Dimmer, Red, Green, Blue, White, Amber, Uv];
}
