using System.Runtime.CompilerServices;
using System.Text;

namespace Cuedeck.Tests;

/// <summary>
/// Checks on the source files themselves.
/// </summary>
public class SourceTextTests
{
    static string RepositoryRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    /// <summary>
    /// UTF-8 read as Windows-1252 and saved again turns an arrow or a dash into a lower-case a-circumflex followed by
    /// odd punctuation, as one PowerShell edit did to the Cues tab. Those pairs never occur in text written on purpose.
    /// </summary>
    [Fact]
    public void No_source_file_has_double_encoded_characters()
    {
        // Built from their codes, so this file doesn't hold them itself.
        int[][] codes = [[0xE2, 0x20AC], [0xE2, 0x2013], [0xE2, 0x2020], [0xE2, 0x2014], [0xC3, 0xA9], [0xC2, 0xB7]];
        var signs = codes.Select(pair => new string([.. pair.Select(c => (char)c)])).ToArray();
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepositoryRoot(), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                continue;
            if (Path.GetExtension(file) is not (".cs" or ".md" or ".gd"))
                continue;
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (signs.Any(text.Contains))
                bad.Add(Path.GetRelativePath(RepositoryRoot(), file));
        }
        Assert.Empty(bad);
    }
}
