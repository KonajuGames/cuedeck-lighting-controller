using System.Xml;
using Cuedeck.Common;

namespace Cuedeck.Dj;

/// <summary>
/// One track in a list: the DJ program's copy of its details when it was added (the database, through
/// <see cref="DjList.Resolve"/>, has the full and current entry).
/// </summary>
sealed class DjListEntry
{
    /// <summary>
    /// The file, with forward slashes.
    /// </summary>
    public string Path { get; set; } = "";
    public long FileSize { get; set; }
    public double Length { get; set; }

    /// <summary>
    /// Beats a minute (0 for unknown).
    /// </summary>
    public double Bpm { get; set; }
    public string Key { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Remix { get; set; } = "";

    /// <summary>
    /// Its place, as stored.
    /// </summary>
    public int Index { get; set; }

    public string DisplayName => Artist != "" && Title != "" ? $"{Artist} - {Title}" : Title != "" ? Title : Gd.BaseName(Path);
}

/// <summary>
/// A named, ordered list of tracks: one of VirtualDJ's Virtual Folders (a <c>.vdjfolder</c> in a <c>MyLists</c> folder,
/// next to each database), or one of Cuepoint's (<see cref="CuepointLibrary.LoadM3u8"/>). sLight's
/// <c>VirtualDjList</c>.
/// </summary>
sealed class DjList
{
    public const string LISTS_DIR = "MyLists";
    public const string EXT = "vdjfolder";

    /// <summary>
    /// The plain-text file VirtualDJ keeps in MyLists: a list's name a line, in the order it shows them.
    /// </summary>
    public const string ORDER_FILE = "order";

    /// <summary>
    /// The file's name without its extension.
    /// </summary>
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>
    /// The folder it's in inside MyLists ("" at the top), "/"-separated.
    /// </summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// The &lt;VirtualFolder&gt;'s attributes as stored (seen: ordered, singleDrive, noDuplicates; "yes" or "no").
    /// </summary>
    public Dictionary<string, string> Options { get; } = [];
    public List<DjListEntry> Entries { get; set; } = [];

    /// <summary>
    /// Its running time in seconds.
    /// </summary>
    public double TotalLength => Entries.Sum(e => e.Length);

    /// <summary>
    /// Each entry's song in <paramref name="find"/> (a database's or library's lookup), in order; null where there's
    /// none.
    /// </summary>
    public List<DjSong?> Resolve(Func<string, DjSong?> find) => [.. Entries.Select(e => find(e.Path))];

    /// <summary>
    /// Every MyLists folder here, beside each VirtualDJ database, Documents first.
    /// </summary>
    public static List<string> FindListDirs() =>
        [.. VirtualDjDatabase.FindDatabaseFiles().Select(f => Gd.PathJoin(System.IO.Path.GetDirectoryName(f)!.Replace('\\', '/'), LISTS_DIR)).Where(Directory.Exists)];

    /// <summary>
    /// Every list in every MyLists folder, in VirtualDJ's order: names in an <c>order</c> file first (the first to
    /// mention a name places it; the Documents one covers lists on every drive), then the rest by group and name.
    /// Lists that don't load are left out, with why added to <paramref name="errors"/>.
    /// </summary>
    public static List<DjList> LoadAll(List<string>? errors = null)
    {
        errors ??= [];
        var lists = new List<DjList>();
        var order = new Dictionary<string, int>();
        foreach (var d in FindListDirs())
        {
            foreach (var n in ReadOrder(Gd.PathJoin(d, ORDER_FILE)))
                order.TryAdd(n, order.Count);
            LoadDir(d, "", lists, errors);
        }
        return
        [
            .. lists
                .OrderBy(l => order.GetValueOrDefault(l.Name, 1 << 30))
                .ThenBy(l => l.Group, Comparer<string>.Create(Gd.NaturalCompare))
                .ThenBy(l => l.Name, Comparer<string>.Create(Gd.NaturalCompare)),
        ];
    }

    /// <summary>
    /// Reads one <c>.vdjfolder</c>; null (and why added to <paramref name="errors"/>) if it can't be read or isn't one.
    /// </summary>
    public static DjList? LoadFile(string filePath, List<string>? errors = null)
    {
        errors ??= [];
        if (!File.Exists(filePath))
        {
            errors.Add($"{filePath}: not found");
            return null;
        }
        DjList? list = null;
        try
        {
            using var reader = XmlReader.Create(filePath, new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                switch (reader.Name)
                {
                    case "VirtualFolder":
                        list = new DjList { Path = filePath, Name = Gd.BaseName(filePath) };
                        while (reader.MoveToNextAttribute())
                            list.Options[reader.Name] = reader.Value;
                        reader.MoveToElement();
                        break;
                    case "song":
                        list?.Entries.Add(ReadEntry(reader, list.Entries.Count));
                        break;
                }
            }
        }
        catch (XmlException e)
        {
            errors.Add($"{filePath}: XML parse error near line {e.LineNumber}");
        }
        catch (IOException e)
        {
            errors.Add($"{filePath}: can't open ({e.Message})");
            return null;
        }
        if (list is null)
        {
            errors.Add($"{filePath}: not a VirtualDJ Virtual Folder");
            return null;
        }
        list.Entries = [.. list.Entries.OrderBy(e => e.Index)]; // a stable sort: ties keep their places in the file
        return list;
    }

    static DjListEntry ReadEntry(XmlReader r, int filePosition)
    {
        string Attr(string name) => r.GetAttribute(name) ?? "";
        var idx = Attr("idx");
        return new DjListEntry
        {
            Path = Attr("path").Replace('\\', '/'),
            FileSize = Gd.ToInt(Attr("size")),
            Length = Gd.ToFloat(Attr("songlength")),
            Bpm = Gd.ToFloat(Attr("bpm")),
            Key = Attr("key"),
            Artist = Attr("artist"),
            Title = Attr("title"),
            Remix = Attr("remix"),
            Index = Gd.IsValidInt(idx) ? Gd.ToInt(idx) : filePosition,
        };
    }

    /// <summary>
    /// The names in an <c>order</c> file, blank lines left out.
    /// </summary>
    public static List<string> ReadOrder(string orderPath)
    {
        if (!File.Exists(orderPath))
            return [];
        return [.. File.ReadAllText(orderPath).Split('\n').Select(l => l.Trim()).Where(l => l != "")];
    }

    /// <summary>
    /// Every .vdjfolder under <paramref name="dir"/>, into its subfolders (a "&lt;name&gt;.subfolders" folder shows in
    /// VirtualDJ as folder &lt;name&gt;).
    /// </summary>
    public static void LoadDir(string dir, string group, List<DjList> lists, List<string> errors)
    {
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(dir);
            dirs = Directory.GetDirectories(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{dir}: can't open directory");
            return;
        }
        foreach (var f in files.Order(StringComparer.Ordinal))
        {
            if (!System.IO.Path.GetExtension(f).Equals("." + EXT, StringComparison.OrdinalIgnoreCase))
                continue;
            if (LoadFile(f.Replace('\\', '/'), errors) is { } list)
            {
                list.Group = group;
                lists.Add(list);
            }
        }
        foreach (var sub in dirs.Order(StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(sub);
            var label = name.EndsWith(".subfolders", StringComparison.Ordinal) ? name[..^".subfolders".Length] : name;
            LoadDir(sub.Replace('\\', '/'), group == "" ? label : group + "/" + label, lists, errors);
        }
    }
}
