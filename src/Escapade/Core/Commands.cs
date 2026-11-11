namespace Escapade.Core;

/// <summary>Where a command's key binding is live.</summary>
public enum CommandContext
{
    /// <summary>Wherever focus is in the window, including the path bar (but never while renaming).</summary>
    Anywhere,

    /// <summary>Only while a file list has focus, so the path bar keeps the key's text-editing meaning (Ctrl+Z, Ctrl+A, Tab).</summary>
    FileList,
}

/// <summary>A named command: what the palette lists and what keybindings point at.</summary>
public sealed record CommandInfo(string Id, string Title, CommandContext Context, params string[] DefaultKeys);

/// <summary>Every command, with its default keys. The order is the command palette's order.</summary>
public static class CommandCatalog
{
    public const string GoTo = "palette.goto";
    public const string Commands = "palette.commands";
    public const string Back = "nav.back";
    public const string Forward = "nav.forward";
    public const string Up = "nav.up";
    public const string EditPath = "nav.editPath";
    public const string SwitchPane = "pane.switch";
    public const string ShowInOtherPane = "pane.showInOther";
    public const string ToggleLeftPane = "pane.toggleLeft";
    public const string Refresh = "view.refresh";
    public const string ToggleHidden = "view.toggleHidden";
    public const string CopyToOther = "file.copyToOther";
    public const string MoveToOther = "file.moveToOther";
    public const string Rename = "file.rename";
    public const string Delete = "file.delete";
    public const string DeletePermanently = "file.deletePermanently";
    public const string NewFolder = "file.newFolder";
    public const string SelectAll = "file.selectAll";
    public const string Reveal = "file.revealInExplorer";
    public const string WindowsMenu = "file.windowsMenu";
    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string ToggleBookmark = "bookmarks.toggle";
    public const string OpenBookmarkPrefix = "bookmarks.open";
    public const string EditKeybindings = "settings.keybindings";
    public const string EditBookmarks = "settings.bookmarks";
    public const string OpenSettingsFolder = "settings.folder";

    public const int BookmarkSlots = 9;

    public static string OpenBookmark(int number) => OpenBookmarkPrefix + number;

    public static IReadOnlyList<CommandInfo> All { get; } = Build();

    private static List<CommandInfo> Build()
    {
        var A = CommandContext.Anywhere;
        var L = CommandContext.FileList;
        var list = new List<CommandInfo>
        {
            new(GoTo, "Go to folder…", A, "ctrl+p"),
            new(Commands, "Show all commands", A, "ctrl+shift+p"),
            new(Back, "Go back", A, "alt+left", "browserback"),
            new(Forward, "Go forward", A, "alt+right", "browserforward"),
            new(Up, "Go up to the parent folder", A, "alt+up"),
            new(EditPath, "Edit the path", A, "ctrl+l", "alt+d", "f4"),
            new(SwitchPane, "Switch to the other pane", L, "tab", "shift+tab"),
            new(ShowInOtherPane, "Show this folder in the other pane", A),
            new(ToggleLeftPane, "Show or hide the left pane", A, "ctrl+b"),
            new(Refresh, "Refresh", A, "ctrl+r", "browserrefresh"),
            new(ToggleHidden, "Show or hide hidden items", A, "ctrl+h"),
            new(CopyToOther, "Copy to the other pane", L, "f5"),
            new(MoveToOther, "Move to the other pane", L, "f6"),
            new(Rename, "Rename", L, "f2"),
            new(Delete, "Delete (to the Recycle Bin)", L, "delete"),
            new(DeletePermanently, "Delete permanently", L, "shift+delete"),
            new(NewFolder, "New folder", L, "ctrl+shift+n"),
            new(SelectAll, "Select all", L, "ctrl+a"),
            new(Reveal, "Reveal in Explorer", L, "ctrl+shift+e"),
            new(WindowsMenu, "Show the Windows context menu", L),
            new(Undo, "Undo", L, "ctrl+z"),
            new(Redo, "Redo", L, "ctrl+y", "ctrl+shift+z"),
            new(ToggleBookmark, "Bookmark this folder, or remove its bookmark", A, "ctrl+d"),
        };
        for (int i = 1; i <= BookmarkSlots; i++)
            list.Add(new(OpenBookmark(i), $"Open bookmark {i}", A, $"alt+{i}"));
        list.Add(new(EditKeybindings, "Edit keybindings", A));
        list.Add(new(EditBookmarks, "Edit the bookmarks file", A));
        list.Add(new(OpenSettingsFolder, "Open the settings folder", A));
        return list;
    }
}
