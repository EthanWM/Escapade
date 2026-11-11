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
    public void Combine_handles_roots_and_drives()
    {
        Assert.Equal(@"C:\Windows", PathUtil.Combine(@"C:\", new FileEntry("Windows", EntryKind.Directory)));
        Assert.Equal(@"C:\a\b.txt", PathUtil.Combine(@"C:\a", new FileEntry("b.txt", EntryKind.File)));
        Assert.Equal(@"D:\", PathUtil.Combine(PathUtil.ComputerRoot, new FileEntry(@"D:\", EntryKind.Drive)));
    }
}
