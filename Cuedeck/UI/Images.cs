using System.Reflection;
using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// The app's pictures, packed into the atlas with the fonts: the logo, and small white icons for tinting, among them
/// the triangles drawn for arrows, the options button's cog, the headphones button, the search box's cross and the
/// folder list's icons, the last two again larger for the folder list's large text; and the mixer's knob caps, in
/// colour, with the dot their arcs and pointers are drawn with. Each is a PNG built into the app (see
/// Assets\draw_assets.py).
/// </summary>
sealed record Images(
    Image Logo,
    Image Played,
    Image TriangleRight,
    Image TriangleDown,
    Image TriangleLeft,
    Image Cog,
    Image Cross,
    FolderIcons Folders,
    Image LargeTriangleRight,
    Image LargeTriangleDown,
    FolderIcons LargeFolders,
    Image Headphones,
    Image Knob,
    Image LargeKnob,
    Image Dot,
    Image Pause,
    Image Timeline,
    FixtureIcons Fixture
)
{
    /// <summary>
    /// Adds every picture to <paramref name="atlas"/>, which mustn't be built yet.
    /// </summary>
    public static Images Load(FontAtlas atlas) =>
        new(
            Logo: atlas.AddImage(Read("logo-22.png")),
            Played: atlas.AddImage(Read("played-14.png")),
            TriangleRight: atlas.AddImage(Read("triangle-right-10.png")),
            TriangleDown: atlas.AddImage(Read("triangle-down-10.png")),
            TriangleLeft: atlas.AddImage(Read("triangle-left-10.png")),
            Cog: atlas.AddImage(Read("cog-18.png")),
            Cross: atlas.AddImage(Read("cross-10.png")),
            Folders: FolderIcons.Load(atlas, 14),
            LargeTriangleRight: atlas.AddImage(Read("triangle-right-14.png")),
            LargeTriangleDown: atlas.AddImage(Read("triangle-down-14.png")),
            LargeFolders: FolderIcons.Load(atlas, 20),
            Headphones: atlas.AddImage(Read("headphones-18.png")),
            Knob: atlas.AddImage(Read("knob-18.png")),
            LargeKnob: atlas.AddImage(Read("knob-32.png")),
            Dot: atlas.AddImage(Read("dot-8.png")),
            Pause: atlas.AddImage(Read("pause-14.png")),
            Timeline: atlas.AddImage(NodeLine()),
            Fixture: FixtureIcons.Load(atlas)
        );

    /// <summary>
    /// The space after <see cref="Timeline"/>'s line, before the title it's put in front of.
    /// </summary>
    public const int TIMELINE_GAP = 4;

    /// <summary>
    /// DJ Edit Mode's "has lighting timelines" mark: a little dimmer node line, three segments with a dot at each node,
    /// 16 x 12 pixels and a gap, white (tinted when drawn), drawn by coverage (4 x 4 samples a pixel) so it's smooth.
    /// sLight's <c>timeline_icon()</c>.
    /// </summary>
    static Png.Image NodeLine()
    {
        const int W = 16;
        const int H = 12;
        (float X, float Y)[] pts = [(1.6f, 9.6f), (5.6f, 3.4f), (10.2f, 7.2f), (14.4f, 2.2f)];
        static float Dist(float ax, float ay, float bx, float by) => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        bool On(float qx, float qy)
        {
            foreach (var p in pts)
                if (Dist(qx, qy, p.X, p.Y) <= 1.75f)
                    return true;
            for (var i = 0; i + 1 < pts.Length; i++)
            {
                var (a, b) = (pts[i], pts[i + 1]);
                var (dx, dy) = (b.X - a.X, b.Y - a.Y);
                var t = Math.Clamp(((qx - a.X) * dx + (qy - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
                if (Dist(qx, qy, a.X + dx * t, a.Y + dy * t) <= 0.6f)
                    return true;
            }
            return false;
        }
        var w = W + TIMELINE_GAP;
        var px = new byte[w * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var hit = 0;
                for (var sy = 0; sy < 4; sy++)
                    for (var sx = 0; sx < 4; sx++)
                        if (On(x + (sx + 0.5f) / 4, y + (sy + 0.5f) / 4))
                            hit++;
                var at = (y * w + x) * 4;
                px[at] = px[at + 1] = px[at + 2] = 255;
                px[at + 3] = (byte)(255 * hit / 16);
            }
        }
        return new Png.Image(w, H, px);
    }

    /// <summary>
    /// A PNG built into the app, decoded. Throws if it's missing, which would mean a broken build.
    /// </summary>
    public static Png.Image Read(string name)
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"The app is missing its image {name}");
        return Png.Decode(stream);
    }
}

/// <summary>
/// A patched fixture's header buttons' icons, sLight's line icons: Flash (a bulb), Home (a house), Remove (a bin) and
/// Leave out (a magic wand struck through).
/// </summary>
sealed record FixtureIcons(Image Flash, Image Home, Image Remove, Image LeaveOut)
{
    public static FixtureIcons Load(FontAtlas atlas) =>
        new(
            Flash: atlas.AddImage(Images.Read("icon-flash-16.png")),
            Home: atlas.AddImage(Images.Read("icon-home-16.png")),
            Remove: atlas.AddImage(Images.Read("icon-remove-16.png")),
            LeaveOut: atlas.AddImage(Images.Read("icon-leave-out-16.png"))
        );
}

/// <summary>
/// The folder list's icons, one for each kind of thing in it.
/// </summary>
sealed record FolderIcons(Image Library, Image Drive, Image Folder, Image Playlist, Image List, Image History, Image Filter)
{
    /// <summary>
    /// Adds the icons drawn <paramref name="size"/> pixels square to <paramref name="atlas"/>.
    /// </summary>
    public static FolderIcons Load(FontAtlas atlas, int size) =>
        new(
            Library: atlas.AddImage(Images.Read($"icon-library-{size}.png")),
            Drive: atlas.AddImage(Images.Read($"icon-drive-{size}.png")),
            Folder: atlas.AddImage(Images.Read($"icon-folder-{size}.png")),
            Playlist: atlas.AddImage(Images.Read($"icon-playlist-{size}.png")),
            List: atlas.AddImage(Images.Read($"icon-list-{size}.png")),
            History: atlas.AddImage(Images.Read($"icon-history-{size}.png")),
            Filter: atlas.AddImage(Images.Read($"icon-filter-{size}.png"))
        );
}
