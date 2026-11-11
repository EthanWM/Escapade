namespace Escapade.Core;

/// <summary>
/// Key chords as text, for the keybindings file and for showing shortcuts: "ctrl+shift+p" parses
/// to <c>Keys.Control | Keys.Shift | Keys.P</c>, which formats as "Ctrl+Shift+P". Deliberately
/// not KeysConverter, which is locale-dependent and spells Ctrl as "Control".
/// </summary>
public static class KeyChord
{
    private static readonly (string Name, Keys Key, string Display)[] Named =
    [
        ("enter", Keys.Enter, "Enter"),
        ("return", Keys.Enter, "Enter"),
        ("esc", Keys.Escape, "Esc"),
        ("escape", Keys.Escape, "Esc"),
        ("tab", Keys.Tab, "Tab"),
        ("space", Keys.Space, "Space"),
        ("backspace", Keys.Back, "Backspace"),
        ("delete", Keys.Delete, "Del"),
        ("del", Keys.Delete, "Del"),
        ("insert", Keys.Insert, "Ins"),
        ("ins", Keys.Insert, "Ins"),
        ("home", Keys.Home, "Home"),
        ("end", Keys.End, "End"),
        ("pageup", Keys.PageUp, "PgUp"),
        ("pgup", Keys.PageUp, "PgUp"),
        ("pagedown", Keys.PageDown, "PgDn"),
        ("pgdn", Keys.PageDown, "PgDn"),
        ("up", Keys.Up, "Up"),
        ("down", Keys.Down, "Down"),
        ("left", Keys.Left, "Left"),
        ("right", Keys.Right, "Right"),
        ("`", Keys.Oemtilde, "`"),
        ("-", Keys.OemMinus, "-"),
        ("minus", Keys.OemMinus, "-"),
        ("=", Keys.Oemplus, "="),
        ("plus", Keys.Oemplus, "="),
        ("[", Keys.OemOpenBrackets, "["),
        ("]", Keys.OemCloseBrackets, "]"),
        ("\\", Keys.OemPipe, "\\"),
        (";", Keys.OemSemicolon, ";"),
        ("'", Keys.OemQuotes, "'"),
        (",", Keys.Oemcomma, ","),
        (".", Keys.OemPeriod, "."),
        ("/", Keys.OemQuestion, "/"),
        ("browserback", Keys.BrowserBack, "Browser Back"),
        ("browserforward", Keys.BrowserForward, "Browser Forward"),
        ("browserrefresh", Keys.BrowserRefresh, "Browser Refresh"),
        ("apps", Keys.Apps, "Menu"),
    ];

    /// <summary>Parses "ctrl+shift+p", "alt+left", "f5", "del"... Names are case-insensitive.</summary>
    public static bool TryParse(string? text, out Keys keys)
    {
        keys = Keys.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = Keys.None;
        Keys? key = null;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim().ToLowerInvariant();
            switch (token)
            {
                case "ctrl" or "control":
                    modifiers |= Keys.Control;
                    continue;
                case "shift":
                    modifiers |= Keys.Shift;
                    continue;
                case "alt":
                    modifiers |= Keys.Alt;
                    continue;
            }
            if (key is not null || ParseKey(token) is not { } parsed)
                return false; // two keys ("a+b"), an empty token ("ctrl++"), or an unknown name
            key = parsed;
        }

        if (key is null)
            return false; // modifiers alone
        keys = modifiers | key.Value;
        return true;
    }

    private static Keys? ParseKey(string token)
    {
        if (token.Length == 0)
            return null;
        if (token.Length == 1 && token[0] is >= 'a' and <= 'z')
            return Keys.A + (token[0] - 'a');
        if (token.Length == 1 && token[0] is >= '0' and <= '9')
            return Keys.D0 + (token[0] - '0');
        if (token[0] == 'f' && int.TryParse(token.AsSpan(1), out int f) && f is >= 1 and <= 24)
            return Keys.F1 + (f - 1);
        foreach (var (name, key, _) in Named)
        {
            if (name == token)
                return key;
        }
        // Anything else Keys names, such as "numpad1" or "oem8".
        if (!char.IsDigit(token[0]) && !token.Contains(',') && Enum.TryParse<Keys>(token, ignoreCase: true, out var other)
            && (other & Keys.Modifiers) == 0 && other is not (Keys.None or Keys.ShiftKey or Keys.ControlKey or Keys.Menu))
        {
            return other;
        }
        return null;
    }

    /// <summary>"Ctrl+Shift+P", "Alt+Left", "Del".</summary>
    public static string Format(Keys keys)
    {
        var parts = new List<string>(4);
        if ((keys & Keys.Control) != 0)
            parts.Add("Ctrl");
        if ((keys & Keys.Shift) != 0)
            parts.Add("Shift");
        if ((keys & Keys.Alt) != 0)
            parts.Add("Alt");
        parts.Add(KeyName(keys & Keys.KeyCode));
        return string.Join("+", parts);
    }

    private static string KeyName(Keys key)
    {
        if (key is >= Keys.A and <= Keys.Z)
            return ((char)('A' + (key - Keys.A))).ToString();
        if (key is >= Keys.D0 and <= Keys.D9)
            return ((char)('0' + (key - Keys.D0))).ToString();
        if (key is >= Keys.F1 and <= Keys.F24)
            return $"F{key - Keys.F1 + 1}";
        foreach (var (_, k, display) in Named)
        {
            if (k == key)
                return display;
        }
        return key.ToString();
    }

    /// <summary>
    /// Keys that type a character in the file list (a letter, digit or symbol, alone or with
    /// Shift, or with Ctrl+Alt, which is AltGr: @ \ { } on many layouts). Those drive
    /// type-to-filter and the path bar, so they can't be bound to commands.
    /// </summary>
    public static bool IsTypingKey(Keys keys)
    {
        var chord = keys & (Keys.Control | Keys.Alt);
        if (chord is Keys.Control or Keys.Alt)
            return false;
        var key = keys & Keys.KeyCode;
        return key is (>= Keys.A and <= Keys.Z) or (>= Keys.D0 and <= Keys.D9) or Keys.Space
            or (>= Keys.NumPad0 and <= Keys.Divide)
            or (>= Keys.OemSemicolon and <= Keys.Oemtilde) or (>= Keys.OemOpenBrackets and <= Keys.OemQuotes)
            or Keys.OemBackslash;
    }

    /// <summary>Keys with fixed meanings in the list: Enter opens, Esc and Backspace work the filter.</summary>
    public static bool IsFixedKey(Keys keys) =>
        keys is Keys.Enter or Keys.Escape or Keys.Back or (Keys.Control | Keys.Back);
}
