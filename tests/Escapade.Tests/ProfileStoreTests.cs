using Escapade.Core;

namespace Escapade.Tests;

public class ProfileStoreTests
{
    [Fact]
    public void Initializes_a_default_profile()
    {
        var folder = NewFolder();
        try
        {
            var store = new ProfileStore(folder);

            Assert.Equal("default", store.DefaultProfile.Id);
            Assert.Equal("Default", store.DefaultProfile.Name);
            Assert.Null(store.Initialize());
            Assert.True(Directory.Exists(store.ProfileFolder(store.DefaultProfile)));
            Assert.Equal(store.DefaultProfile, SettingsFile.Deserialize<UserProfile>(
                SettingsFile.ReadText(store.ProfileMetadataPath(store.DefaultProfile))!));
            Assert.Equal(store.KeybindingsPathFor(store.DefaultProfile), store.KeybindingsPath);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    [Fact]
    public void Migrates_legacy_keybindings_without_changing_the_source()
    {
        var folder = NewFolder();
        try
        {
            var store = new ProfileStore(folder);
            const string legacy = """{ "file.rename": "f2" }""";
            File.WriteAllText(store.LegacyKeybindingsPath, legacy);

            Assert.Null(store.Initialize());

            Assert.Equal(legacy, SettingsFile.ReadText(store.KeybindingsPath));
            Assert.Equal(legacy, SettingsFile.ReadText(store.LegacyKeybindingsPath));
            var (map, problems) = Keymap.Build(CommandCatalog.All, SettingsFile.ReadText(store.KeybindingsPath));
            Assert.Empty(problems);
            Assert.Equal("F2", map!.ShortcutFor(CommandCatalog.Rename));
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    [Fact]
    public void Existing_profile_keybindings_are_not_overwritten_by_legacy_data()
    {
        var folder = NewFolder();
        try
        {
            var store = new ProfileStore(folder);
            Directory.CreateDirectory(store.ProfileFolder(store.DefaultProfile));
            const string profile = """{ "file.rename": "f3" }""";
            const string legacy = """{ "file.rename": "f2" }""";
            File.WriteAllText(store.KeybindingsPathFor(store.DefaultProfile), profile);
            File.WriteAllText(store.LegacyKeybindingsPath, legacy);

            Assert.Null(store.Initialize());

            Assert.Equal(profile, SettingsFile.ReadText(store.KeybindingsPath));
            Assert.Equal(legacy, SettingsFile.ReadText(store.LegacyKeybindingsPath));
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    [Fact]
    public void Reads_the_profile_name_from_metadata()
    {
        var folder = NewFolder();
        try
        {
            var store = new ProfileStore(folder);
            Directory.CreateDirectory(store.ProfileFolder(store.DefaultProfile));
            File.WriteAllText(store.ProfileMetadataPath(store.DefaultProfile),
                """{ "id": "default", "name": "Development" }""");

            Assert.Null(store.Initialize());

            Assert.Equal("Development", store.DefaultProfile.Name);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    [Fact]
    public void Malformed_profile_metadata_is_preserved_and_reported()
    {
        var folder = NewFolder();
        try
        {
            var store = new ProfileStore(folder);
            Directory.CreateDirectory(store.ProfileFolder(store.DefaultProfile));
            const string malformed = """{ "id": "default" """;
            File.WriteAllText(store.ProfileMetadataPath(store.DefaultProfile), malformed);

            var problem = store.Initialize();

            Assert.Contains("profile.json isn't valid JSON", problem);
            Assert.Equal(malformed, SettingsFile.ReadText(store.ProfileMetadataPath(store.DefaultProfile)));
            Assert.Equal("Default", store.DefaultProfile.Name);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    [Fact]
    public void Malformed_legacy_keybindings_are_migrated_verbatim_for_existing_validation()
    {
        var folder = NewFolder();
        try
        {
            var store = new ProfileStore(folder);
            const string malformed = """{ "file.rename": "f2" """;
            File.WriteAllText(store.LegacyKeybindingsPath, malformed);

            Assert.Null(store.Initialize());

            Assert.Equal(malformed, SettingsFile.ReadText(store.KeybindingsPath));
            Assert.Equal(malformed, SettingsFile.ReadText(store.LegacyKeybindingsPath));
            var (map, problems) = Keymap.Build(CommandCatalog.All, SettingsFile.ReadText(store.KeybindingsPath));
            Assert.Null(map);
            Assert.Single(problems);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    private static string NewFolder()
    {
        var folder = Path.Join(Path.GetTempPath(), "escapade-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}
