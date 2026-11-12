namespace Escapade.Core;

internal sealed record UserProfile(string Id, string Name);

internal sealed class ProfileStore
{
    private const string DefaultProfileId = "default";
    private const string ProfilesFolderName = "profiles";
    private const string ProfileMetadataFileName = "profile.json";
    private const string KeybindingsFileName = "keybindings.json";

    private readonly string _userDataFolder;
    private string _keybindingsPath;

    public ProfileStore(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        DefaultProfile = new UserProfile(DefaultProfileId, "Default");
        _keybindingsPath = KeybindingsPathFor(DefaultProfile);
    }

    public UserProfile DefaultProfile { get; private set; }

    public string KeybindingsPath => _keybindingsPath;

    public string LegacyKeybindingsPath => Path.Join(_userDataFolder, KeybindingsFileName);

    public string ProfileFolder(UserProfile profile) =>
        Path.Join(_userDataFolder, ProfilesFolderName, profile.Id);

    public string ProfileMetadataPath(UserProfile profile) =>
        Path.Join(ProfileFolder(profile), ProfileMetadataFileName);

    public string KeybindingsPathFor(UserProfile profile) =>
        Path.Join(ProfileFolder(profile), KeybindingsFileName);

    /// <summary>Creates the default profile and copies legacy keybindings without removing the source.</summary>
    public string? Initialize()
    {
        var keybindingsPath = KeybindingsPathFor(DefaultProfile);
        _keybindingsPath = keybindingsPath;
        string? problem = null;
        try
        {
            Directory.CreateDirectory(ProfileFolder(DefaultProfile));
            problem = LoadOrCreateDefaultProfile();
            if (!File.Exists(keybindingsPath) &&
                File.Exists(LegacyKeybindingsPath) &&
                SettingsFile.ReadText(LegacyKeybindingsPath) is { } legacy)
            {
                SettingsFile.WriteAtomic(keybindingsPath, legacy);
            }
            return problem;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(keybindingsPath) && File.Exists(LegacyKeybindingsPath))
                _keybindingsPath = LegacyKeybindingsPath;
            var failure = $"Couldn't prepare the default profile: {ex.Message}";
            return problem is null ? failure : $"{problem} {failure}";
        }
    }

    private string? LoadOrCreateDefaultProfile()
    {
        var path = ProfileMetadataPath(DefaultProfile);
        if (!File.Exists(path))
        {
            SettingsFile.WriteAtomic(path, SettingsFile.Serialize(DefaultProfile));
            return null;
        }

        var text = SettingsFile.ReadText(path);
        if (text is null)
        {
            SettingsFile.WriteAtomic(path, SettingsFile.Serialize(DefaultProfile));
            return null;
        }

        UserProfile? profile;
        try
        {
            profile = SettingsFile.Deserialize<UserProfile>(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            return $"{ProfileMetadataFileName} isn't valid JSON{where}; using the built-in Default profile.";
        }

        if (profile is null || profile.Id != DefaultProfileId || string.IsNullOrWhiteSpace(profile.Name))
            return $"{ProfileMetadataFileName} doesn't describe a valid default profile; using the built-in Default profile.";

        DefaultProfile = profile;
        return null;
    }
}
