using Cuedeck.Common;

namespace Cuedeck.Music;

/// <summary>
/// The music on this machine's drives, for DJ Edit Mode's folder tree: its drives, a folder's subfolders and its audio
/// files, each in natural order (Track 2 before Track 10), hidden and system ones left out. Paths use forward
/// slashes, as sLight's did, so a show's timelines index reads the same in both.
/// </summary>
static class MusicFolders
{
    /// <summary>
    /// The audio files listed (lower-case extensions).
    /// </summary>
    public static readonly string[] AudioExtensions = ["mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "wma", "aif", "aiff"];

    /// <summary>
    /// This machine's drives that are ready, as "C:/".
    /// </summary>
    public static List<string> Drives()
    {
        var output = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady)
                    output.Add(Slashes(d.RootDirectory.FullName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return output;
    }

    /// <summary>
    /// A path with forward slashes, a folder's ending in one only for a drive's root.
    /// </summary>
    public static string Slashes(string path) => path.Replace('\\', '/');

    /// <summary>
    /// A folder's visible subfolders' names, in natural order; none if it can't be read.
    /// </summary>
    public static List<string> Subfolders(string path)
    {
        try
        {
            var names = new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !d.Name.StartsWith('.'))
                .Select(d => d.Name)
                .ToList();
            names.Sort(Gd.NaturalCompare);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// A folder's audio files' names, in natural order; none if it can't be read.
    /// </summary>
    public static List<string> AudioFiles(string path)
    {
        try
        {
            var names = new DirectoryInfo(path)
                .EnumerateFiles()
                .Where(f => (f.Attributes & FileAttributes.Hidden) == 0 && !f.Name.StartsWith('.'))
                .Select(f => f.Name)
                .Where(n => AudioExtensions.Contains(Path.GetExtension(n).TrimStart('.').ToLowerInvariant()))
                .ToList();
            names.Sort(Gd.NaturalCompare);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}
