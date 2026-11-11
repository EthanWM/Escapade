using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Escapade.Core;

/// <summary>Reading and writing Escapade's JSON files in %APPDATA%\Escapade.</summary>
public static class SettingsFile
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Write "é" and "+" as themselves rather than \u escapes, so hand-editing stays pleasant.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string DefaultFolder =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Escapade");

    /// <summary>The file's text, or null if it doesn't exist. IO errors (say, an editor holding it) propagate.</summary>
    public static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>
    /// Writes via a temporary file and a replacing rename, so the file is never seen half-written
    /// and a crash mid-save leaves the previous version intact.
    /// </summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    public static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Json);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
