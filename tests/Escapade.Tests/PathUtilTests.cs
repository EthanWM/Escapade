using Escapade.Core;

namespace Escapade.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"c:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"c:\windows\", @"C:\windows")]
    [InlineData(@"C:/Windows/System32", @"C:\Windows\System32")]
    [InlineData("  \"C:\\Program Files\"  ", @"C:\Program Files")]
    [InlineData(@"C:\Windows\..\Users", @"C:\Users")]
    public void Normalize_absolute_paths(string input, string expected) =>
        Assert.Equal(expected, PathUtil.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("This PC")]
    [InlineData("this pc")]
    public void Normalize_computer_root(string input) =>
        Assert.Equal(PathUtil.ComputerRoot, PathUtil.Normalize(input));

    [Fact]
    public void Normalize_relative_needs_a_base()
    {
        Assert.Null(PathUtil.Normalize("src"));
        Assert.Equal(@"C:\repo\src", PathUtil.Normalize("src", @"C:\repo"));
        Assert.Equal(@"C:\other", PathUtil.Normalize(@"..\other", @"C:\repo"));
    }

    [Fact]
    public void Normalize_expands_home_and_env_vars()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, PathUtil.Normalize("~"));
        Assert.Equal(Path.Join(home, "Documents"), PathUtil.Normalize("~/Documents"));
        Assert.Equal(PathUtil.Normalize(home), PathUtil.Normalize("%USERPROFILE%"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32", @"C:\Windows")]
    [InlineData(@"C:\Windows", @"C:\")]
    [InlineData(@"C:\", "")]
    public void GetParent_walks_up_to_computer_root(string location, string expected) =>
        Assert.Equal(expected, PathUtil.GetParent(location));

    [Fact]
    public void GetParent_of_computer_root_is_null() =>
        Assert.Null(PathUtil.GetParent(PathUtil.ComputerRoot));

    [Theory]
    [InlineData(@"C:\Windows\System32", "System32")]
    [InlineData(@"C:\", @"C:\")] // drives are listed under their root path
    public void GetNameInParent_matches_parent_listing(string location, string expected) =>
        Assert.Equal(expected, PathUtil.GetNameInParent(location));

    [Fact]
    public void UniqueName_counts_up_like_explorer()
    {
        Assert.Equal("New folder", PathUtil.UniqueName("New folder", _ => false));

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "New folder", "new folder (2)" };
        Assert.Equal("New folder (3)", PathUtil.UniqueName("New folder", taken.Contains));
    }

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData(".gitignore", true)]
    [InlineData("a b c", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("what?", false)]
    [InlineData("x:y", false)]
    public void IsValidFileName(string name, bool expected) =>
        Assert.Equal(expected, PathUtil.IsValidFileName(name));

    [Fact]
    public void SameLocation_ignores_case_and_nulls_never_match()
    {
        Assert.True(PathUtil.SameLocation(@"C:\Repo", @"c:\repo"));
        Assert.False(PathUtil.SameLocation(@"C:\Repo", @"C:\Repo2"));
        Assert.False(PathUtil.SameLocation(null, @"C:\"));
        Assert.True(PathUtil.SameLocation(PathUtil.ComputerRoot, PathUtil.ComputerRoot));
    }

    [Fact]
    public void Combine_handles_roots_and_drives()
    {
        Assert.Equal(@"C:\Windows", PathUtil.Combine(@"C:\", new FileEntry("Windows", EntryKind.Directory)));
        Assert.Equal(@"C:\a\b.txt", PathUtil.Combine(@"C:\a", new FileEntry("b.txt", EntryKind.File)));
        Assert.Equal(@"D:\", PathUtil.Combine(PathUtil.ComputerRoot, new FileEntry(@"D:\", EntryKind.Drive)));
    }
}
