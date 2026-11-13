using Cuedeck.Common;

namespace Cuedeck.Fixtures;

/// <summary>
/// The fixture profiles to patch from: the built-ins, then the custom ones saved in <see cref="Folder"/> (one JSON file
/// each, as sLight kept them in its fixture_profiles folder).
/// </summary>
sealed class ProfileStore
{
    readonly List<FixtureProfile> _profiles = [];

    public string Folder { get; }

    public ProfileStore(string folder)
    {
        Folder = folder;
        Reload();
    }

    public IReadOnlyList<FixtureProfile> Profiles => _profiles;

    /// <summary>
    /// Offers <paramref name="profile"/> (one from the library) to patch for the rest of the session, without saving
    /// it: it goes into the show file with the fixtures patched with it. Returns the one offered, which is the one
    /// already there if a profile with its id is.
    /// </summary>
    public FixtureProfile AddForSession(FixtureProfile profile)
    {
        if (_profiles.FirstOrDefault(p => p.Id == profile.Id) is { } existing)
            return existing;
        _profiles.Add(profile);
        return profile;
    }

    /// <summary>
    /// Reads the profiles again: the built-ins, then each readable profile file in the folder, by name. A file whose
    /// id is a built-in's replaces it.
    /// </summary>
    public void Reload()
    {
        _profiles.Clear();
        _profiles.AddRange(FixtureProfile.BuiltIn());
        if (!Directory.Exists(Folder))
            return;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Json.Parse(File.ReadAllText(file)) is not { } node)
                    continue;
                var profile = FixtureProfile.FromJson(node);
                if (profile.Id == "")
                    profile.Id = Path.GetFileNameWithoutExtension(file);
                _profiles.RemoveAll(p => p.Id == profile.Id);
                _profiles.Add(profile);
            }
            catch (IOException) { }
        }
    }
}
