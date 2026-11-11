using System.Text;
using System.Text.Json;

namespace Escapade.Core;

/// <summary>
/// Which key runs which command: the catalog's defaults, with the user's keybindings file on top.
/// The file maps command ids to their keys; a command listed there gets exactly the keys given
/// (its defaults are replaced), and <c>[]</c> or <c>null</c> unbinds it. A key the user gives one
/// command is taken from whichever command had it by default.
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<Keys, string> _byKey;
    private readonly Dictionary<string, List<Keys>> _byCommand;

    private Keymap(Dictionary<Keys, string> byKey, Dictionary<string, List<Keys>> byCommand)
    {
        _byKey = byKey;
        _byCommand = byCommand;
    }

    public static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public bool TryGetCommand(Keys keys, out string commandId) => _byKey.TryGetValue(keys, out commandId!);

    public IReadOnlyList<Keys> KeysFor(string commandId) =>
        _byCommand.TryGetValue(commandId, out var keys) ? keys : [];

    /// <summary>The first key bound to the command, formatted ("Ctrl+Shift+P"), or null if it has none.</summary>
    public string? ShortcutFor(string commandId) =>
        KeysFor(commandId) is [var first, ..] ? KeyChord.Format(first) : null;

    public static Keymap Defaults(IReadOnlyList<CommandInfo> catalog) => Build(catalog, null).Map!;

    /// <summary>
    /// Builds the keymap from the catalog and the keybindings file's text (null when there's no
    /// file). Problems with individual entries are reported and those entries skipped. Map is
    /// null only when the file can't be read as JSON at all; keep the previous keymap then.
    /// </summary>
    public static (Keymap? Map, IReadOnlyList<string> Problems) Build(IReadOnlyList<CommandInfo> catalog, string? userJson)
    {
        var problems = new List<string>();
        var known = catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var overrides = new List<(string Id, List<Keys> Keys)>();

        if (!string.IsNullOrWhiteSpace(userJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(userJson, JsonOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return (null, ["The file must contain one { … } object."]);

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (!known.TryGetValue(property.Name, out var command))
                    {
                        problems.Add($"unknown command “{property.Name}”");
                        continue;
                    }
                    if (overrides.Any(o => o.Id == command.Id))
                        problems.Add($"“{command.Id}” is listed twice; the last one counts");
                    overrides.RemoveAll(o => o.Id == command.Id);
                    overrides.Add((command.Id, ReadKeys(property, command.Id, problems)));
                }
            }
            catch (JsonException ex)
            {
                var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
                return (null, [$"not valid JSON{where}: {FirstSentence(ex.Message)}"]);
            }
        }

        var byKey = new Dictionary<Keys, string>();
        var byCommand = new Dictionary<string, List<Keys>>(StringComparer.OrdinalIgnoreCase);

        // The user's bindings first, so they win any key they share with a default.
        foreach (var (id, keys) in overrides)
        {
            foreach (var key in keys)
            {
                if (byKey.TryGetValue(key, out var other))
                {
                    problems.Add($"{KeyChord.Format(key)} is given to both “{other}” and “{id}”; “{id}” gets it");
                    byCommand[other].Remove(key);
                }
                byKey[key] = id;
                Add(byCommand, id, key);
            }
            byCommand.TryAdd(id, []);
        }

        var overridden = overrides.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var command in catalog)
        {
            if (overridden.Contains(command.Id))
                continue;
            foreach (var text in command.DefaultKeys)
            {
                if (!KeyChord.TryParse(text, out var key))
                    throw new InvalidOperationException($"Bad default key “{text}” for {command.Id}.");
                if (byKey.TryAdd(key, command.Id))
                    Add(byCommand, command.Id, key);
            }
        }

        return (new Keymap(byKey, byCommand), problems);
    }

    private static List<Keys> ReadKeys(JsonProperty property, string id, List<string> problems)
    {
        var result = new List<Keys>();
        var value = property.Value;
        IEnumerable<JsonElement> items = value.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.String => [value],
            JsonValueKind.Array => value.EnumerateArray(),
            _ => Invalid(),
        };
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                problems.Add($"“{id}”: keys must be text, like \"ctrl+shift+p\"");
                continue;
            }
            var text = item.GetString();
            if (!KeyChord.TryParse(text, out var key))
                problems.Add($"“{id}”: can't read the key “{text}”");
            else if (KeyChord.IsTypingKey(key))
                problems.Add($"“{id}”: {KeyChord.Format(key)} types a character (it filters the list), so it can't be a shortcut");
            else if (KeyChord.IsFixedKey(key))
                problems.Add($"“{id}”: {KeyChord.Format(key)} has a fixed meaning (open, or edit the filter), so it can't be a shortcut");
            else if (!result.Contains(key))
                result.Add(key);
        }
        return result;

        IEnumerable<JsonElement> Invalid()
        {
            problems.Add($"“{id}”: expected a key, a list of keys, or null");
            return [];
        }
    }

    private static void Add(Dictionary<string, List<Keys>> byCommand, string id, Keys key)
    {
        if (!byCommand.TryGetValue(id, out var list))
            byCommand[id] = list = [];
        list.Add(key);
    }

    private static string FirstSentence(string message)
    {
        int end = message.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? message[..end] : message.TrimEnd('.');
    }

    /// <summary>
    /// The keybindings file written when the user first asks to edit it: every command with its
    /// default keys, commented out, ready to uncomment and change.
    /// </summary>
    public static string Template(IReadOnlyList<CommandInfo> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Escapade keybindings. Changes apply as soon as you save.");
        sb.AppendLine("//");
        sb.AppendLine("// To change a command's keys, uncomment its line and edit it. A command listed here gets");
        sb.AppendLine("// exactly the keys you give it, replacing its defaults: one key, a list [\"f5\", \"ctrl+c\"],");
        sb.AppendLine("// or [] to unbind it. A key you give one command is taken from whichever command had it.");
        sb.AppendLine("//");
        sb.AppendLine("// Keys: ctrl, shift, alt, plus a-z, 0-9, f1-f24, enter, esc, tab, space, backspace,");
        sb.AppendLine("// delete, insert, home, end, pageup, pagedown, up, down, left, right, or one of ` - = [ ] \\ ; ' , . /");
        sb.AppendLine("// A letter, digit or symbol alone, with Shift, or with Ctrl+Alt (AltGr) can't be a shortcut:");
        sb.AppendLine("// typing filters the list. Fixed keys: Enter opens, Backspace goes up (or edits the filter),");
        sb.AppendLine("// Esc and Ctrl+Backspace clear the filter.");
        sb.AppendLine("{");
        foreach (var command in catalog)
        {
            var keys = command.DefaultKeys.Length switch
            {
                0 => "[]",
                1 => Quote(command.DefaultKeys[0]),
                _ => "[" + string.Join(", ", command.DefaultKeys.Select(Quote)) + "]",
            };
            sb.AppendLine($"  // {command.Title}");
            sb.AppendLine($"  // \"{command.Id}\": {keys},");
        }
        sb.AppendLine("}");
        return sb.ToString();

        // Not JsonSerializer: its default encoder would write "alt+left" as "alt+left".
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
