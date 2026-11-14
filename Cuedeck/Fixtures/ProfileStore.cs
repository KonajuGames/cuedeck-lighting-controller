using Cuedeck.Common;

namespace Cuedeck.Fixtures;

/// <summary>
/// The fixture profiles to patch from: the built-ins, then the custom ones saved in <see cref="Folder"/> (one JSON file
/// each, as sLight kept them in its fixture_profiles folder). A saved profile with a built-in's id replaces it in its
/// place: that's how an edited built-in sticks.
/// </summary>
sealed class ProfileStore
{
    readonly List<FixtureProfile> _profiles = [];
    readonly HashSet<string> _builtInIds = [];

    public string Folder { get; }

    /// <summary>
    /// Where imported GDTF fixtures' 3D models go: a folder per profile, by its id.
    /// </summary>
    public string ModelsFolder { get; }

    public ProfileStore(string folder, string? modelsFolder = null)
    {
        Folder = folder;
        ModelsFolder = modelsFolder ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder)) ?? folder, "fixture_models");
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
    /// id is a built-in's replaces it where it is; the others go after.
    /// </summary>
    public void Reload()
    {
        _profiles.Clear();
        _builtInIds.Clear();
        foreach (var p in FixtureProfile.BuiltIn())
        {
            _profiles.Add(p);
            _builtInIds.Add(p.Id);
        }
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
                Put(profile);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Puts <paramref name="profile"/> in place of the one with its id, or after the rest.
    /// </summary>
    void Put(FixtureProfile profile)
    {
        var at = _profiles.FindIndex(p => p.Id == profile.Id);
        if (at >= 0)
            _profiles[at] = profile;
        else
            _profiles.Add(profile);
    }

    public bool IsBuiltIn(string id) => _builtInIds.Contains(id);

    public string FilePath(string id) => Path.Combine(Folder, id + ".json");

    /// <summary>
    /// Whether the profile has a file of its own: a custom one, or a built-in that's been edited.
    /// </summary>
    public bool HasFile(FixtureProfile p) => File.Exists(FilePath(p.Id));

    /// <summary>
    /// A file-safe id from a profile's name: lower case letters, digits and underscores (spaces and dashes become
    /// underscores), or "profile".
    /// </summary>
    public static string SafeId(string name)
    {
        var s = new System.Text.StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                s.Append(ch);
            else if (ch is ' ' or '-')
                s.Append('_');
        }
        var id = s.ToString().Trim('_');
        return id != "" ? id : "profile";
    }

    /// <summary>
    /// Whether a profile has <paramref name="id"/>, or a file's named for it.
    /// </summary>
    public bool IdInUse(string id) => File.Exists(FilePath(id)) || _profiles.Any(p => p.Id == id);

    /// <summary>
    /// <paramref name="baseId"/>, or with "_2", "_3"… after it if that's in use.
    /// </summary>
    public string UniqueId(string baseId)
    {
        var id = baseId;
        for (var n = 2; IdInUse(id); n++)
            id = $"{baseId}_{n}";
        return id;
    }

    /// <summary>
    /// Writes <paramref name="profile"/> to its file and offers it to patch, in place of the one with its id.
    /// </summary>
    public void Save(FixtureProfile profile)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath(profile.Id), Json.Stringify(profile.ToJson()));
        Put(profile);
    }

    /// <summary>
    /// Deletes a profile's file. A built-in goes back to how it was; a custom one goes. Says what happened, or why
    /// nothing did (a built-in that hasn't been edited can't be).
    /// </summary>
    public string Delete(FixtureProfile p)
    {
        if (IsBuiltIn(p.Id) && !HasFile(p))
            return $"'{p.Model}' is a built-in profile and can't be deleted.";
        if (HasFile(p))
            File.Delete(FilePath(p.Id));
        var at = _profiles.FindIndex(x => x.Id == p.Id);
        if (IsBuiltIn(p.Id))
        {
            if (at >= 0)
                _profiles[at] = FixtureProfile.BuiltIn().First(b => b.Id == p.Id);
            return $"Reset '{p.Model}' to its built-in default.";
        }
        if (at >= 0)
            _profiles.RemoveAt(at);
        return $"Deleted profile '{p.Model}'.";
    }

    /// <summary>
    /// Imports a fixture definition (see <see cref="FixtureImport.FromPath"/>) and saves it as a custom profile, with
    /// an id of its own and any 3D models beside it. Returns the profile and what to say, or null and why it failed.
    /// </summary>
    public (FixtureProfile? Profile, string Message) Import(string path)
    {
        var result = FixtureImport.FromPath(path);
        if (result.Profile is not { } profile)
            return (null, $"Import failed: {result.Error}");
        var baseId = SafeId(profile.Id.Trim() != "" ? profile.Id : profile.FullName);
        profile.Id = IdInUse(baseId) ? UniqueId(baseId) : baseId;
        SaveModels(profile, result.ModelBytes);
        Save(profile);

        var message = $"Imported '{profile.Model}' — {profile.ModeCount} mode(s), {profile.ChannelCount(0)} ch.";
        if (result.Warnings.Count > 0)
            message += $"  {result.Warnings.Count} approximation(s); check it in Edit...";
        return (profile, message);
    }

    /// <summary>
    /// Keeps a profile read from elsewhere (an MVR's GDTF) as a custom profile, under an id of its own if its own's
    /// taken, with its 3D models beside it.
    /// </summary>
    public void AddImported(FixtureProfile profile, IReadOnlyDictionary<string, byte[]> models)
    {
        if (IdInUse(profile.Id))
            profile.Id = UniqueId(SafeId(profile.Id));
        SaveModels(profile, models);
        Save(profile);
    }

    /// <summary>
    /// Writes a GDTF's model files into the profile's own models folder, and notes them in its geometry.
    /// </summary>
    void SaveModels(FixtureProfile profile, IReadOnlyDictionary<string, byte[]> models)
    {
        if (models.Count == 0 || !profile.Geometry.ContainsKey("tree"))
            return;
        var folder = Path.Combine(ModelsFolder, profile.Id);
        Directory.CreateDirectory(folder);
        var map = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, bytes) in models)
        {
            var file = SafeId(name) + ".glb";
            File.WriteAllBytes(Path.Combine(folder, file), bytes);
            map[name] = file;
        }
        profile.Geometry["models"] = map;
        profile.Geometry["models_dir"] = profile.Id;
    }
}
