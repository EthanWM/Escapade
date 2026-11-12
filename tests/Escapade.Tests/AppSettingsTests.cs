using Escapade.Core;

namespace Escapade.Tests;

public class AppSettingsTests
{
    [Theory]
    [InlineData(null, ThemeChoice.System)]
    [InlineData("dark", ThemeChoice.Dark)]
    [InlineData(" Light ", ThemeChoice.Light)]
    [InlineData("system", ThemeChoice.System)]
    [InlineData("purple", ThemeChoice.System)] // a typo falls back to following Windows
    public void Reads_the_theme(string? text, ThemeChoice expected) =>
        Assert.Equal(expected, new AppSettings(text).ThemeChoice);

    [Fact]
    public void Saves_and_loads_and_survives_a_broken_file()
    {
        var folder = Path.Join(Path.GetTempPath(), "escapade-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(ThemeChoice.System, AppSettings.Load(folder).ThemeChoice); // no file yet

            AppSettings.Default.WithTheme(ThemeChoice.Dark).Save(folder);
            Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(Path.Join(folder, AppSettings.FileName)));
            Assert.Equal(ThemeChoice.Dark, AppSettings.Load(folder).ThemeChoice);

            File.WriteAllText(Path.Join(folder, AppSettings.FileName), "{ not json");
            Assert.Equal(ThemeChoice.System, AppSettings.Load(folder).ThemeChoice);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
