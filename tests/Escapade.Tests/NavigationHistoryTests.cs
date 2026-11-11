using Escapade.Core;

namespace Escapade.Tests;

public class NavigationHistoryTests
{
    [Fact]
    public void Back_and_forward()
    {
        var h = new NavigationHistory();
        h.Push("A");
        h.Push("B");
        h.Push("C");

        Assert.Equal("B", h.GoBack()!.Location);
        Assert.Equal("A", h.GoBack()!.Location);
        Assert.Null(h.GoBack());
        Assert.Equal("B", h.GoForward()!.Location);
        Assert.True(h.CanGoForward);
    }

    [Fact]
    public void Push_truncates_forward_entries()
    {
        var h = new NavigationHistory();
        h.Push("A");
        h.Push("B");
        h.GoBack();
        h.Push("X");

        Assert.False(h.CanGoForward);
        Assert.Equal("A", h.GoBack()!.Location);
    }

    [Fact]
    public void RevertFailed_after_push_returns_to_origin()
    {
        var h = new NavigationHistory();
        h.Push("A");
        h.Push("Denied");

        Assert.Equal("A", h.RevertFailed()!.Location);
        Assert.Equal(1, h.Count);
        Assert.False(h.CanGoForward);
    }

    [Fact]
    public void RevertFailed_after_back_returns_to_where_user_was()
    {
        var h = new NavigationHistory();
        h.Push("A");
        h.Push("Deleted");
        h.Push("C");
        h.GoBack(); // to Deleted, which fails

        Assert.Equal("C", h.RevertFailed()!.Location);
        Assert.Equal("A", h.GoBack()!.Location);
    }

    [Fact]
    public void RevertFailed_twice_in_a_row_still_lands_somewhere()
    {
        var h = new NavigationHistory();
        h.Push("A");
        h.Push("B");
        h.Push("C");
        h.RevertFailed();                     // C failed -> B
        Assert.Equal("A", h.RevertFailed()!.Location); // B failed too -> A
    }

    [Fact]
    public void RevertFailed_on_only_entry_leaves_history_empty()
    {
        var h = new NavigationHistory();
        h.Push("Denied");
        Assert.Null(h.RevertFailed());
        Assert.Null(h.Current);
    }

    [Fact]
    public void SelectedName_is_kept_per_entry()
    {
        var h = new NavigationHistory();
        h.Push("A").SelectedName = "src";
        h.Push("A\\src");
        Assert.Equal("src", h.GoBack()!.SelectedName);
    }
}
