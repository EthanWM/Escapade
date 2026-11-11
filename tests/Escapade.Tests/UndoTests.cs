using Escapade.Core;

namespace Escapade.Tests;

public class UndoHistoryTests
{
    [Fact]
    public void Undo_then_redo_round_trips()
    {
        var history = new UndoHistory<string>();
        history.Record("a");
        history.Record("b");

        Assert.Equal("b", history.TakeUndo());
        history.PutRedo("b");
        Assert.Equal(["a"], history.UndoItems);
        Assert.Equal(["b"], history.RedoItems);

        Assert.Equal("b", history.TakeRedo());
        history.PutUndo("b");
        Assert.Equal(["b", "a"], history.UndoItems); // newest first
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void A_new_action_discards_redo_but_putting_back_does_not()
    {
        var history = new UndoHistory<string>();
        history.Record("a");
        history.Record("b");
        history.PutRedo(history.TakeUndo()!);

        history.PutUndo(history.TakeUndo()!); // a failed undo goes back where it was
        Assert.True(history.CanRedo);

        history.Record("c");
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Oldest_entries_fall_off_at_capacity()
    {
        var history = new UndoHistory<string>(capacity: 2);
        history.Record("a");
        history.Record("b");
        history.Record("c");
        Assert.Equal(["c", "b"], history.UndoItems);
    }

    [Fact]
    public void Taking_from_an_empty_stack_returns_null()
    {
        var history = new UndoHistory<string>();
        Assert.Null(history.TakeUndo());
        Assert.Null(history.TakeRedo());
    }
}

public class FileActionTests
{
    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void Copy_keeps_only_top_level_items()
    {
        var action = FileAction.Copy([@"C:\src\dir", @"C:\src\a.txt"], @"D:\dst");
        action.Record(
        [
            new(@"C:\src\dir\inner.txt", @"D:\dst\dir\inner.txt"), // nested: the shell reports these too
            new(@"C:\src\dir", @"D:\dst\dir"),
            new(@"C:\src\a.txt", @"D:\dst\a (2).txt"),            // "keep both" renamed it
        ]);

        Assert.Equal(
            [new ItemChange(@"C:\src\dir", @"D:\dst\dir"), new ItemChange(@"C:\src\a.txt", @"D:\dst\a (2).txt")],
            action.Changes);
        Assert.False(action.Partial);
    }

    [Fact]
    public void Copy_never_undoes_into_items_that_already_existed()
    {
        // Copying folder "dir" onto an existing "dir" merges into it. Undoing must not recycle the
        // user's original folder.
        var action = FileAction.Copy([@"C:\src\dir", @"C:\src\new.txt"], @"D:\dst");
        var preexisting = action.ExistingDestinations(Existing(@"D:\dst\dir"));
        Assert.Equal([@"D:\dst\dir"], preexisting);

        action.Record([new(@"C:\src\dir", @"D:\dst\dir"), new(@"C:\src\new.txt", @"D:\dst\new.txt")], preexisting);

        Assert.Equal([new ItemChange(@"C:\src\new.txt", @"D:\dst\new.txt")], action.Changes);
        Assert.True(action.Partial);
    }

    [Fact]
    public void Undo_of_copy_recycles_copies_that_still_exist()
    {
        var action = FileAction.Copy([@"C:\a.txt", @"C:\b.txt"], @"D:\");
        action.Record([new(@"C:\a.txt", @"D:\a.txt"), new(@"C:\b.txt", @"D:\b.txt")]);

        var plan = action.PlanUndo(Existing(@"D:\b.txt"));

        Assert.Equal(UndoStepKind.Recycle, plan.Kind);
        Assert.Equal([new ItemChange(@"C:\b.txt", @"D:\b.txt")], plan.Items);
        Assert.Equal(1, plan.Missing);
    }

    [Fact]
    public void Undo_of_move_needs_the_item_and_its_original_folder()
    {
        var action = FileAction.Move([@"C:\src\a.txt", @"C:\gone\b.txt"], @"D:\dst");
        action.Record([new(@"C:\src\a.txt", @"D:\dst\a.txt"), new(@"C:\gone\b.txt", @"D:\dst\b.txt")]);

        var plan = action.PlanUndo(Existing(@"D:\dst\a.txt", @"D:\dst\b.txt", @"C:\src"));

        Assert.Equal(UndoStepKind.MoveBack, plan.Kind);
        Assert.Equal([new ItemChange(@"C:\src\a.txt", @"D:\dst\a.txt")], plan.Items);
        Assert.Equal(1, plan.Missing);
        Assert.Equal([@"D:\dst", @"C:\src", @"C:\gone"], action.AffectedFolders);
    }

    [Fact]
    public void Rename_undoes_by_renaming_back()
    {
        var action = FileAction.Rename(@"C:\dir\old.txt", "new.txt");
        action.Record([new(@"C:\dir\old.txt", @"C:\dir\new.txt")]);

        var plan = action.PlanUndo(Existing(@"C:\dir\new.txt"));

        Assert.Equal(UndoStepKind.RenameBack, plan.Kind);
        Assert.Single(plan.Items);
        Assert.Equal("Rename “old.txt” to “new.txt”", action.Description);
        Assert.Equal([@"C:\dir"], action.AffectedFolders);
    }

    [Fact]
    public void Recycle_keeps_bin_locations_and_flags_items_deleted_outright()
    {
        var action = FileAction.Recycle([@"C:\dir\a.txt", @"C:\dir\huge.iso"]);
        action.Record(
        [
            new(@"C:\dir\a.txt", @"C:\$Recycle.Bin\S-1-5-21\$RABC123.txt"),
            new(@"C:\dir\huge.iso", ""), // too big for the bin: no new item
        ]);

        Assert.Equal([new ItemChange(@"C:\dir\a.txt", @"C:\$Recycle.Bin\S-1-5-21\$RABC123.txt")], action.Changes);
        Assert.True(action.Partial);
        Assert.Equal(UndoStepKind.Restore, action.PlanUndo(_ => false).Kind);
        Assert.Equal("Delete 2 items", action.Description);
    }

    [Fact]
    public void New_folder_records_the_created_folder_under_its_parent()
    {
        var action = FileAction.NewFolder(@"C:\dir", "New folder");
        action.Record([new("", @"C:\dir\New folder (2)"), new("", @"C:\elsewhere\x")]);

        Assert.Equal([new ItemChange("", @"C:\dir\New folder (2)")], action.Changes);
        Assert.Equal("New folder “New folder (2)”", action.Description);
        Assert.Equal(UndoStepKind.Recycle, action.PlanUndo(_ => true).Kind);
    }

    [Fact]
    public void Redo_repeats_with_the_sources_that_still_exist()
    {
        var action = FileAction.Copy([@"C:\a.txt", @"C:\b.txt"], @"D:\");
        Assert.Equal([@"C:\b.txt"], action.RedoSources(Existing(@"C:\b.txt")));
    }

    [Fact]
    public void Recording_again_replaces_the_previous_run()
    {
        var action = FileAction.Copy([@"C:\a.txt"], @"D:\");
        action.Record([new(@"C:\a.txt", @"D:\a.txt")], new HashSet<string> { @"D:\a.txt" });
        Assert.True(action.Partial);

        action.Record([new(@"C:\a.txt", @"D:\a.txt")]);
        Assert.False(action.Partial);
        Assert.Single(action.Changes);
    }
}
