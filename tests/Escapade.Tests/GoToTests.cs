using Escapade.Core;

namespace Escapade.Tests;

public class FuzzyTests
{
    private static int NameStart(string path) => path.Length - PathUtil.ShortName(path).Length;

    private static FuzzyMatch? MatchPath(string query, string path) => Fuzzy.Match(query, path, NameStart(path));

    [Fact]
    public void Empty_query_matches_everything_without_marks()
    {
        var match = Fuzzy.Match("  ", "anything");
        Assert.NotNull(match);
        Assert.Empty(match.Positions);
    }

    [Theory]
    [InlineData("esc", @"E:\repos\Escapade")]
    [InlineData("ESC", @"E:\repos\Escapade")]
    [InlineData("rpesc", @"E:\repos\Escapade")]
    [InlineData("esc src", @"E:\repos\Escapade\src")]
    [InlineData("repos src", @"E:\repos\Escapade\src")]
    public void Matches_subsequences_word_by_word(string query, string path) =>
        Assert.NotNull(MatchPath(query, path));

    [Theory]
    [InlineData("xyz", @"E:\repos\Escapade")]
    [InlineData("src esc", @"E:\repos\Escapade\src")] // words must appear in order
    [InlineData("escapadee", @"E:\repos\Escapade")]
    public void Rejects_non_matches(string query, string path) => Assert.Null(MatchPath(query, path));

    [Fact]
    public void Positions_point_at_the_matched_characters()
    {
        var path = @"E:\repos\Escapade\src";
        var match = MatchPath("esc src", path)!;
        var matched = new string(match.Positions.Select(p => path[p]).ToArray());
        Assert.Equal("escsrc", matched.ToLowerInvariant());
        Assert.Equal(match.Positions.OrderBy(p => p), match.Positions); // in order, no repeats
        Assert.Equal(match.Positions.Length, match.Positions.Distinct().Count());
        Assert.Equal(path.Length - 3, match.Positions[3]); // "src" is the name, not the s/r/c scattered earlier
    }

    [Fact]
    public void Prefers_the_folder_whose_name_is_typed_over_its_children_and_relatives()
    {
        var target = MatchPath("src", @"E:\repos\Escapade\src")!.Score;
        Assert.True(target > MatchPath("src", @"E:\repos\Escapade\src\Escapade\UI")!.Score);
        Assert.True(target > MatchPath("src", @"E:\src-old\notes")!.Score);
        Assert.True(target > MatchPath("src", @"E:\sources\rc")!.Score);
    }

    [Fact]
    public void Prefers_word_starts_and_runs()
    {
        // "dl" as the start of Downloads beats letters buried mid-word.
        Assert.True(MatchPath("down", @"C:\Users\me\Downloads")!.Score > MatchPath("down", @"C:\Users\me\Lowdown")!.Score);
        Assert.True(Fuzzy.Match("rename", "Rename")!.Score > Fuzzy.Match("rename", "Reveal in Explorer, then name")?.Score);
    }

    [Fact]
    public void Finds_a_later_occurrence_when_the_first_would_starve_the_next_word()
    {
        // Greedy on "a" would take the first 'a' and still work; this checks the alignment that
        // must leave room: "ab" then "b" needs the second b.
        var match = Fuzzy.Match("ab b", "a b b");
        Assert.NotNull(match);
        Assert.Equal([0, 2, 4], match.Positions);
    }
}

public class FrecencyTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Visits_add_up()
    {
        var frecency = new Frecency();
        frecency.Visit(@"C:\a", Now);
        frecency.Visit(@"c:\A", Now); // same folder, different case
        Assert.Equal(1, frecency.Count);
        Assert.Equal(2 * 4, frecency.Score(@"C:\a", Now)); // rank 2, visited this hour
    }

    [Fact]
    public void Recency_weights_the_rank()
    {
        var entry = new FrecencyEntry(@"C:\a", 10, Now);
        Assert.Equal(40, Frecency.Score(entry, Now.AddMinutes(30)));
        Assert.Equal(20, Frecency.Score(entry, Now.AddHours(5)));
        Assert.Equal(5, Frecency.Score(entry, Now.AddDays(3)));
        Assert.Equal(2.5, Frecency.Score(entry, Now.AddDays(30)));
    }

    [Fact]
    public void Ranks_frequent_recent_folders_first()
    {
        var frecency = new Frecency(
        [
            new(@"C:\old-favourite", 50, Now.AddDays(-30)), // 12.5
            new(@"C:\today", 5, Now.AddHours(-2)),          // 10
            new(@"C:\just-now", 4, Now.AddMinutes(-5)),     // 16
        ]);
        Assert.Equal([@"C:\just-now", @"C:\old-favourite", @"C:\today"], frecency.Ranked(Now).Select(r => r.Entry.Path));
    }

    [Fact]
    public void Ageing_scales_everything_down_and_forgets_faded_entries()
    {
        var frecency = new Frecency(
        [
            new(@"C:\big", Frecency.MaxTotal - 1, Now),
            new(@"C:\faint", 1, Now),
        ]);
        frecency.Visit(@"C:\big", Now); // total passes MaxTotal
        Assert.Equal(1, frecency.Count);
        Assert.InRange(frecency.Entries.Single().Rank, 0.89 * Frecency.MaxTotal, 0.91 * Frecency.MaxTotal);
    }

    [Fact]
    public void Remove_forgets_a_folder()
    {
        var frecency = new Frecency();
        frecency.Visit(@"C:\a", Now);
        Assert.True(frecency.Remove(@"C:\A"));
        Assert.Equal(0, frecency.Count);
        Assert.False(frecency.Remove(@"C:\a"));
    }

    [Fact]
    public void Survives_a_round_trip_through_json()
    {
        var frecency = new Frecency();
        frecency.Visit(@"C:\Users\me\Ünïcode", Now);
        var json = SettingsFile.Serialize(frecency.Entries.ToList());
        Assert.Contains("Ünïcode", json); // written readably, not \u-escaped

        var back = new Frecency(SettingsFile.Deserialize<List<FrecencyEntry>>(json));
        Assert.Equal(frecency.Score(@"C:\Users\me\Ünïcode", Now), back.Score(@"C:\Users\me\Ünïcode", Now));
    }
}

public class BookmarkListTests
{
    [Fact]
    public void Toggle_adds_then_removes()
    {
        var list = new BookmarkList();
        Assert.True(list.Toggle(@"E:\repos\Escapade"));
        Assert.Equal([new Bookmark("Escapade", @"E:\repos\Escapade")], list.Items);
        Assert.False(list.Toggle(@"e:\REPOS\escapade"));
        Assert.Empty(list.Items);
    }

    [Fact]
    public void Drives_are_named_by_their_root()
    {
        var list = new BookmarkList();
        list.Toggle(@"D:\");
        Assert.Equal(@"D:\", list.Items[0].Name);
    }

    [Fact]
    public void Hand_edited_files_are_tidied()
    {
        var list = new BookmarkList(
        [
            new("Work", "c:/work/"),
            new("", @"C:\Tools"),
            new("dup", @"C:\work"),
            new("blank", "  "),
        ]);
        Assert.Equal([new Bookmark("Work", @"C:\work"), new Bookmark("Tools", @"C:\Tools")], list.Items);
    }

    [Fact]
    public void Survives_a_round_trip_through_json_with_comments()
    {
        var json = """
            // my bookmarks
            [
              { "name": "Repos", "path": "E:\\repos" },
            ]
            """;
        var list = new BookmarkList(SettingsFile.Deserialize<List<Bookmark>>(json));
        Assert.Equal([new Bookmark("Repos", @"E:\repos")], list.Items);
        Assert.Contains("\"path\": \"E:\\\\repos\"", SettingsFile.Serialize(list.Items));
    }
}

public class SettingsFileTests
{
    [Fact]
    public void Writes_atomically_and_reads_back()
    {
        var folder = Path.Join(Path.GetTempPath(), "escapade-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Join(folder, "x.json");
            Assert.Null(SettingsFile.ReadText(path));
            SettingsFile.WriteAtomic(path, "one");
            SettingsFile.WriteAtomic(path, "two");
            Assert.Equal("two", SettingsFile.ReadText(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]); // no BOM
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
