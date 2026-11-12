namespace Escapade.Core;

public enum ThemeChoice
{
    /// <summary>Light or dark, whichever Windows is set to (when Escapade starts).</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// General preferences in settings.json. Read once at start-up, before any window exists,
/// because the colour mode can only be chosen then.
/// </summary>
public sealed record AppSettings(string? Theme)
{
    public const string FileName = "settings.json";

    public static AppSettings Default { get; } = new(Theme: null);

    public ThemeChoice ThemeChoice => Theme?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeChoice.Light,
        "dark" => ThemeChoice.Dark,
        _ => ThemeChoice.System,
    };

    public AppSettings WithTheme(ThemeChoice choice) => this with { Theme = choice.ToString().ToLowerInvariant() };

    /// <summary>The settings, or the defaults if the file is missing or unreadable. Never throws.</summary>
    public static AppSettings Load(string folder)
    {
        try
        {
            var text = SettingsFile.ReadText(Path.Join(folder, FileName));
            return text is null ? Default : SettingsFile.Deserialize<AppSettings>(text) ?? Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Default;
        }
    }

    public void Save(string folder) => SettingsFile.WriteAtomic(Path.Join(folder, FileName), SettingsFile.Serialize(this));
}
