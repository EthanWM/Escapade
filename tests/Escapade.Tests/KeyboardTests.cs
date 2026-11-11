using System.Windows.Forms;
using Escapade.Core;

namespace Escapade.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("ctrl+shift+p", Keys.Control | Keys.Shift | Keys.P)]
    [InlineData("Ctrl+P", Keys.Control | Keys.P)]
    [InlineData(" alt + left ", Keys.Alt | Keys.Left)]
    [InlineData("f5", Keys.F5)]
    [InlineData("F24", Keys.F24)]
    [InlineData("del", Keys.Delete)]
    [InlineData("shift+delete", Keys.Shift | Keys.Delete)]
    [InlineData("alt+1", Keys.Alt | Keys.D1)]
    [InlineData("ctrl+`", Keys.Control | Keys.Oemtilde)]
    [InlineData("ctrl+\\", Keys.Control | Keys.OemPipe)]
    [InlineData("ctrl+,", Keys.Control | Keys.Oemcomma)]
    [InlineData("control+pgdn", Keys.Control | Keys.PageDown)]
    [InlineData("browserback", Keys.BrowserBack)]
    [InlineData("ctrl+numpad1", Keys.Control | Keys.NumPad1)]
    public void Parses(string text, Keys expected)
    {
        Assert.True(KeyChord.TryParse(text, out var keys));
        Assert.Equal(expected, keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl")]
    [InlineData("ctrl+shift")]
    [InlineData("ctrl+a+b")]
    [InlineData("ctrl++")]
    [InlineData("hyper+x")]
    [InlineData("f25")]
    [InlineData("65")]
    [InlineData("ctrl+a,b")]
    public void Rejects(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Theory]
    [InlineData(Keys.Control | Keys.Shift | Keys.P, "Ctrl+Shift+P")]
    [InlineData(Keys.Alt | Keys.Left, "Alt+Left")]
    [InlineData(Keys.Delete, "Del")]
    [InlineData(Keys.Shift | Keys.Delete, "Shift+Del")]
    [InlineData(Keys.Alt | Keys.D3, "Alt+3")]
    [InlineData(Keys.F2, "F2")]
    [InlineData(Keys.Control | Keys.Oemtilde, "Ctrl+`")]
    public void Formats(Keys keys, string expected) => Assert.Equal(expected, KeyChord.Format(keys));

    [Theory]
    [InlineData("a", true)]
    [InlineData("shift+a", true)]
    [InlineData("5", true)]
    [InlineData("shift+/", true)]
    [InlineData("space", true)]
    [InlineData("ctrl+alt+q", true)] // AltGr+Q is @ on a German keyboard
    [InlineData("ctrl+alt+shift+7", true)]
    [InlineData("ctrl+q", false)]
    [InlineData("ctrl+alt+f5", false)]
    [InlineData("ctrl+a", false)]
    [InlineData("alt+1", false)]
    [InlineData("f2", false)]
    [InlineData("delete", false)]
    [InlineData("tab", false)]
    public void Knows_which_keys_type_characters(string text, bool typing)
    {
        Assert.True(KeyChord.TryParse(text, out var keys));
        Assert.Equal(typing, KeyChord.IsTypingKey(keys));
    }
}

public class KeymapTests
{
    private static readonly IReadOnlyList<CommandInfo> Catalog = CommandCatalog.All;

    [Fact]
    public void Defaults_parse_and_never_share_a_key()
    {
        var seen = new Dictionary<Keys, string>();
        foreach (var command in Catalog)
        {
            foreach (var text in command.DefaultKeys)
            {
                Assert.True(KeyChord.TryParse(text, out var key), $"{command.Id}: {text}");
                Assert.False(KeyChord.IsTypingKey(key), $"{command.Id}: {text} types");
                Assert.True(seen.TryAdd(key, command.Id), $"{text} is on both {seen.GetValueOrDefault(key)} and {command.Id}");
            }
        }
    }

    [Fact]
    public void Command_ids_are_unique() =>
        Assert.Equal(Catalog.Count, Catalog.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void Defaults_apply_without_a_file()
    {
        var (map, problems) = Keymap.Build(Catalog, null);
        Assert.Empty(problems);
        Assert.True(map!.TryGetCommand(Keys.F5, out var id));
        Assert.Equal(CommandCatalog.CopyToOther, id);
        Assert.Equal("Ctrl+Y", map.ShortcutFor(CommandCatalog.Redo));
        Assert.Null(map.ShortcutFor(CommandCatalog.WindowsMenu));
    }

    [Fact]
    public void A_listed_command_gets_exactly_the_keys_given()
    {
        var (map, problems) = Keymap.Build(Catalog, """{ "edit.redo": "ctrl+shift+y" }""");
        Assert.Empty(problems);
        Assert.Equal([Keys.Control | Keys.Shift | Keys.Y], map!.KeysFor(CommandCatalog.Redo));
        Assert.False(map.TryGetCommand(Keys.Control | Keys.Y, out _)); // its old defaults are gone
        Assert.False(map.TryGetCommand(Keys.Control | Keys.Shift | Keys.Z, out _));
    }

    [Fact]
    public void A_users_key_is_taken_from_the_command_that_had_it_by_default()
    {
        // Ctrl+B is the left-pane toggle by default; give it to bookmarks.
        var (map, problems) = Keymap.Build(Catalog, """{ "bookmarks.toggle": ["ctrl+b"] }""");
        Assert.Empty(problems);
        Assert.True(map!.TryGetCommand(Keys.Control | Keys.B, out var id));
        Assert.Equal(CommandCatalog.ToggleBookmark, id);
        Assert.Empty(map.KeysFor(CommandCatalog.ToggleLeftPane));
        Assert.False(map.TryGetCommand(Keys.Control | Keys.D, out _));
    }

    [Theory]
    [InlineData("""{ "pane.toggleLeft": [] }""")]
    [InlineData("""{ "pane.toggleLeft": null }""")]
    public void Empty_or_null_unbinds(string json)
    {
        var (map, problems) = Keymap.Build(Catalog, json);
        Assert.Empty(problems);
        Assert.False(map!.TryGetCommand(Keys.Control | Keys.B, out _));
        Assert.Null(map.ShortcutFor(CommandCatalog.ToggleLeftPane));
    }

    [Fact]
    public void Bad_entries_are_reported_and_skipped_but_good_ones_apply()
    {
        var (map, problems) = Keymap.Build(Catalog, """
            {
              // comments and trailing commas are fine
              "no.such.command": "f9",
              "file.rename": ["f2", "ctrl+?"],
              "file.newFolder": "shift+n",
              "file.windowsMenu": "ctrl+shift+f10",
            }
            """);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("no.such.command"));
        Assert.Contains(problems, p => p.Contains("ctrl+?"));
        Assert.Contains(problems, p => p.Contains("Shift+N"));
        Assert.Equal("F2", map!.ShortcutFor(CommandCatalog.Rename));
        Assert.Empty(map.KeysFor(CommandCatalog.NewFolder)); // its only key was refused
        Assert.Equal("Ctrl+Shift+F10", map.ShortcutFor(CommandCatalog.WindowsMenu));
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("esc")]
    [InlineData("backspace")]
    [InlineData("ctrl+backspace")]
    public void Fixed_keys_are_refused(string key)
    {
        var (map, problems) = Keymap.Build(Catalog, $$"""{ "nav.up": "{{key}}" }""");
        Assert.Single(problems);
        Assert.Contains("fixed", problems[0]);
        Assert.Empty(map!.KeysFor(CommandCatalog.Up));
    }

    [Fact]
    public void Fixed_keys_with_other_modifiers_are_allowed()
    {
        var (map, problems) = Keymap.Build(Catalog, """{ "nav.up": ["alt+enter", "shift+backspace"] }""");
        Assert.Empty(problems);
        Assert.Equal(2, map!.KeysFor(CommandCatalog.Up).Count);
    }

    [Fact]
    public void Two_user_bindings_for_one_key_is_a_problem_and_the_later_wins()
    {
        var (map, problems) = Keymap.Build(Catalog, """{ "file.rename": "f9", "nav.up": "f9" }""");
        Assert.Contains(problems, p => p.Contains("F9"));
        Assert.True(map!.TryGetCommand(Keys.F9, out var id));
        Assert.Equal(CommandCatalog.Up, id);
        Assert.Empty(map.KeysFor(CommandCatalog.Rename));
    }

    [Fact]
    public void Unreadable_json_gives_no_map_so_the_old_one_is_kept()
    {
        var (map, problems) = Keymap.Build(Catalog, """{ "file.rename": "f2" """);
        Assert.Null(map);
        Assert.Single(problems);

        (map, problems) = Keymap.Build(Catalog, """["f2"]""");
        Assert.Null(map);
        Assert.Single(problems);
    }

    [Fact]
    public void The_template_is_valid_and_changes_nothing()
    {
        var template = Keymap.Template(Catalog);
        Assert.Contains("\"file.copyToOther\": \"f5\"", template);
        Assert.Contains("\"edit.redo\": [\"ctrl+y\", \"ctrl+shift+z\"]", template);
        var (map, problems) = Keymap.Build(Catalog, template);
        Assert.Empty(problems);
        Assert.Equal("F5", map!.ShortcutFor(CommandCatalog.CopyToOther));

        // Uncommenting a line as written is still valid.
        var line = template.Split('\n').First(l => l.Contains("\"file.rename\"")).Replace("//", "").Trim();
        (map, problems) = Keymap.Build(Catalog, "{ " + line + " }");
        Assert.Empty(problems);
        Assert.Equal("F2", map!.ShortcutFor(CommandCatalog.Rename));
    }
}
