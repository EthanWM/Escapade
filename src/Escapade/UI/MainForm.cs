using Escapade.Core;
using Vanara.Windows.Shell;
using C = Escapade.Core.CommandCatalog;
using KNOWNFOLDERID = Vanara.PInvoke.Shell32.KNOWNFOLDERID;

namespace Escapade.UI;

internal sealed class MainForm : Form
{
    private const int HistoryMenuLimit = 25;
    private const int PaletteLimit = 50;
    private const int BookmarkBonus = 15;

    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _paneToggle;
    private readonly ToolStripSplitButton _undoButton;
    private readonly ToolStripSplitButton _redoButton;
    private readonly ToolStripButton _goToButton;
    private readonly ToolStripDropDownButton _bookmarksButton;
    private readonly ToolStripButton _commandsButton;
    private readonly ToolStrip _rail;
    private readonly List<Image> _railImages = [];
    private readonly SplitContainer _split;
    private readonly FilePane _left;
    private readonly FilePane _right;
    private readonly string _leftStart;
    private readonly string _rightStart;
    private readonly UndoHistory<FileAction> _history = new();
    private readonly UserData _data = new();
    private readonly Dictionary<string, Action> _commands;
    private readonly Dictionary<string, CommandInfo> _commandInfo;
    private FilePane _active;
    private CommandPalette? _palette;
    private bool _undoBusy;
    private int _savedSplitterDistance;

    /// <param name="args">Optional start folders: left pane, then right pane.</param>
    public MainForm(string[] args)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _leftStart = args.ElementAtOrDefault(0) ?? home;
        _rightStart = args.ElementAtOrDefault(1) ?? _leftStart;

        SuspendLayout();

        _left = CreatePane();
        _right = CreatePane();
        _active = _left;
        _left.IsActive = true;
        _left.CurrentPathChanged += (_, _) =>
        {
            if (LeftCollapsed)
                BuildRail(); // the rail's first button shows where the hidden pane is
        };

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5,
            TabStop = false,
        };
        _split.Panel1.Controls.Add(_left);
        _split.Panel2.Controls.Add(_right);

        _paneToggle = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
        _paneToggle.Click += (_, _) => ToggleLeftPane();

        _undoButton = MakeHistoryButton("Undo", () => _history.UndoItems, "Nothing to undo", n => StepHistory(n, undo: true));
        _redoButton = MakeHistoryButton("Redo", () => _history.RedoItems, "Nothing to redo", n => StepHistory(n, undo: false));

        _goToButton = new ToolStripButton("Go to…") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
        _goToButton.Click += (_, _) => ShowPalette("");

        _bookmarksButton = new ToolStripDropDownButton("Bookmarks") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
        _bookmarksButton.DropDown.ShowItemToolTips = true;
        _bookmarksButton.DropDownItems.Add(new ToolStripMenuItem("…")); // replaced on opening
        _bookmarksButton.DropDownOpening += (_, _) => BuildBookmarksMenu();

        _commandsButton = new ToolStripButton("Commands")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoToolTip = false,
            Alignment = ToolStripItemAlignment.Right,
        };
        _commandsButton.Click += (_, _) => ShowPalette(">");

        _toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            CanOverflow = false,
            Padding = new Padding(4, 1, 4, 1),
        };
        _toolbar.Items.AddRange([
            _paneToggle, new ToolStripSeparator(),
            _undoButton, _redoButton, new ToolStripSeparator(),
            _goToButton, _bookmarksButton,
            _commandsButton,
        ]);

        _rail = new ToolStrip
        {
            Dock = DockStyle.Left,
            LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow,
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(2, 4, 2, 4),
            Visible = false,
        };
        _rail.ImageScalingSize = SystemInformation.SmallIconSize;

        _history.Changed += UpdateHistoryButtons;

        _commandInfo = C.All.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _commands = BuildCommands();
        _data.KeymapChanged += UpdateShortcutHints;
        _data.BookmarksChanged += () =>
        {
            if (LeftCollapsed)
                BuildRail();
        };
        _data.Problem += message => _active.ShowError(message);
        _data.Notice += message => _active.ShowInfo(message);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 720);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        Text = "Escapade";
        // Docking runs in reverse z-order: the toolbar claims the full top edge, then the rail
        // the left edge below it, and the panes fill what's left.
        Controls.Add(_split);
        Controls.Add(_rail);
        Controls.Add(_toolbar);
        UpdateShortcutHints();
        ResumeLayout(false);
    }

    private FilePane CreatePane()
    {
        var pane = new FilePane { Dock = DockStyle.Fill };
        pane.Enter += (_, _) => SetActive(pane);
        pane.CurrentPathChanged += (_, _) => UpdateTitle();
        pane.TransferRequested += OnTransferRequested;
        pane.ActionRecorded += _history.Record;
        pane.Visited += path => _data.RecordVisit(path);
        pane.Shortcut = id => _data.Keymap.ShortcutFor(id);
        return pane;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = _split.Width / 2;

        // Start-up folders aren't visits: they'd otherwise top the Ctrl+P history forever.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!_left.Navigate(_leftStart, countVisit: false))
            _left.Navigate(home, countVisit: false);
        if (!_right.Navigate(_rightStart, countVisit: false))
            _right.Navigate(home, countVisit: false);

        // After navigating, which clears the status bar, so settings-file problems stay visible.
        _data.Load(this);
        ActiveControl = null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _left.FocusList();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel)
            _data.Flush();
    }

    // ------------------------------------------------------------------
    // Commands and keys
    // ------------------------------------------------------------------

    private Dictionary<string, Action> BuildCommands()
    {
        var commands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
        {
            [C.GoTo] = () => ShowPalette(""),
            [C.Commands] = () => ShowPalette(">"),
            [C.Back] = () => _active.GoBack(),
            [C.Forward] = () => _active.GoForward(),
            [C.Up] = () => _active.GoUp(),
            [C.EditPath] = () => _active.FocusPathBar(),
            [C.SwitchPane] = () =>
            {
                if (!LeftCollapsed)
                    OtherPane(_active).FocusList(); // Commander style: flip between the panes
            },
            [C.ShowInOtherPane] = ShowInOtherPane,
            [C.ToggleLeftPane] = ToggleLeftPane,
            [C.Refresh] = () => _active.Reload(),
            [C.ToggleHidden] = () => _active.ToggleHidden(),
            [C.CopyToOther] = () => _active.RequestTransfer(TransferKind.Copy),
            [C.MoveToOther] = () => _active.RequestTransfer(TransferKind.Move),
            [C.Rename] = () => _active.BeginRename(),
            [C.Delete] = () => _active.DeleteSelection(permanent: false),
            [C.DeletePermanently] = () => _active.DeleteSelection(permanent: true),
            [C.NewFolder] = () => _active.CreateFolder(),
            [C.SelectAll] = () => _active.SelectAll(),
            [C.Reveal] = () => _active.RevealInExplorer(),
            [C.WindowsMenu] = () => _active.ShowWindowsMenu(),
            [C.Undo] = () => StepHistory(1, undo: true),
            [C.Redo] = () => StepHistory(1, undo: false),
            [C.ToggleBookmark] = ToggleBookmark,
            [C.EditKeybindings] = () => _data.EditKeybindings(this),
            [C.EditBookmarks] = () => _data.EditBookmarks(this),
            [C.OpenSettingsFolder] = () =>
            {
                if (_data.OpenFolder() is { } error)
                    _active.ShowError(error);
            },
        };
        for (int i = 1; i <= C.BookmarkSlots; i++)
        {
            int index = i - 1;
            commands[C.OpenBookmark(i)] = () => OpenBookmark(index);
        }

        var missing = C.All.Where(c => !commands.ContainsKey(c.Id)).Select(c => c.Id).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"No handler for: {string.Join(", ", missing)}");
        return commands;
    }

    /// <summary>The single place keys become commands, using the (user-configurable) keymap.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // While renaming, every key belongs to the edit box: Ctrl+Left moves by word, Ctrl+Z
        // undoes typing, Delete deletes characters. The edit box is a child of the list, so its
        // keys come through here too.
        if (_left.IsEditingName || _right.IsEditingName)
            return base.ProcessCmdKey(ref msg, keyData);

        if (_data.Keymap.TryGetCommand(keyData, out var id) && _commandInfo.TryGetValue(id, out var info))
        {
            // File-list commands leave the path bar alone, so there Ctrl+Z, Ctrl+A and Delete still edit text.
            if (info.Context == CommandContext.Anywhere || _active.IsListFocused)
            {
                _commands[id]();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private string? Shortcut(string commandId) => _data.Keymap.ShortcutFor(commandId);

    private string WithShortcut(string text, string commandId) =>
        Shortcut(commandId) is { } key ? $"{text} ({key})" : text;

    /// <summary>Every visible mention of a shortcut, after the keymap loads or changes.</summary>
    private void UpdateShortcutHints()
    {
        _left.RefreshShortcutHints();
        _right.RefreshShortcutHints();
        _goToButton.ToolTipText = WithShortcut("Go to a bookmarked or recent folder", C.GoTo);
        _bookmarksButton.ToolTipText = WithShortcut("Bookmarks. Bookmark this folder", C.ToggleBookmark);
        _commandsButton.ToolTipText = WithShortcut("Every command, with its shortcut", C.Commands);
        UpdatePaneToggle();
        UpdateHistoryButtons();
        if (LeftCollapsed)
            BuildRail();
    }

    private FilePane OtherPane(FilePane pane) => pane == _left ? _right : _left;

    private void SetActive(FilePane pane)
    {
        if (_active == pane)
            return;
        _active.IsActive = false;
        _active = pane;
        _active.IsActive = true;
        UpdateTitle();
    }

    private void UpdateTitle() => Text = $"{PathUtil.ShortName(_active.CurrentPath)} — Escapade";

    /// <summary>Navigates the active pane, or the other one, and puts focus there.</summary>
    private void GoTo(string path, bool otherPane)
    {
        var pane = otherPane ? OtherPane(_active) : _active;
        if (pane == _left && LeftCollapsed)
            ExpandLeft(); // sending a folder to the hidden pane brings it back
        if (pane.Navigate(path))
            pane.FocusList();
    }

    private void ShowInOtherPane()
    {
        var path = _active.CurrentPath;
        var other = OtherPane(_active);
        if (other == _left && LeftCollapsed)
            ExpandLeft();
        other.Navigate(path);
    }

    // ------------------------------------------------------------------
    // Palette: Ctrl+P folders, Ctrl+Shift+P (or a leading ">") commands
    // ------------------------------------------------------------------

    private void ShowPalette(string text)
    {
        if (_palette is { IsDisposed: false } open)
        {
            open.SetQuery(text);
            return;
        }
        var palette = new CommandPalette(this, PaletteQuery);
        palette.FormClosed += (_, _) =>
        {
            if (_palette == palette)
                _palette = null;
        };
        _palette = palette;
        palette.Open(text, _toolbar.Bottom + LogicalToDeviceUnits(2));
    }

    private PaletteResults PaletteQuery(string text) =>
        text.StartsWith('>') ? CommandResults(text[1..]) : FolderResults(text);

    private PaletteResults CommandResults(string query)
    {
        var found = new List<(int Score, int Order, PaletteItem Item)>();
        for (int order = 0; order < C.All.Count; order++)
        {
            var info = C.All[order];
            if (info.Id == C.Commands || CommandTitle(info) is not { } title)
                continue;
            if (Fuzzy.Match(query, title) is not { } match)
                continue;
            var id = info.Id;
            found.Add((match.Score, order, new PaletteItem(title, null, Shortcut(id), match.Positions, [], _ =>
            {
                // From the palette the target is the active pane's list, wherever focus was before.
                _active.FocusList();
                _commands[id]();
            })));
        }

        var items = found
            .OrderByDescending(f => query.Trim().Length > 0 ? f.Score : 0)
            .ThenBy(f => f.Order)
            .Select(f => f.Item)
            .ToList();
        return new PaletteResults(items, "Enter runs the command   ·   Delete the > to search folders");
    }

    /// <summary>The palette's name for a command, reflecting the current state; null to leave it out.</summary>
    private string? CommandTitle(CommandInfo info)
    {
        if (info.Id == C.ToggleBookmark)
        {
            var path = _active.CurrentPath;
            if (PathUtil.IsComputerRoot(path))
                return null;
            return _data.Bookmarks.Contains(path)
                ? $"Remove the bookmark for “{PathUtil.ShortName(path)}”"
                : $"Bookmark “{PathUtil.ShortName(path)}”";
        }
        if (info.Id.StartsWith(C.OpenBookmarkPrefix, StringComparison.Ordinal)
            && int.TryParse(info.Id.AsSpan(C.OpenBookmarkPrefix.Length), out int number))
        {
            return number <= _data.Bookmarks.Items.Count
                ? $"Open bookmark {number}: {_data.Bookmarks.Items[number - 1].Name}"
                : null; // empty slots would just be noise
        }
        return info.Title;
    }

    private PaletteResults FolderResults(string query)
    {
        var now = DateTime.UtcNow;
        var trimmed = query.Trim();
        var found = new List<(double Score, int Order, PaletteItem Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bookmarks = _data.Bookmarks.Items;
        for (int i = 0; i < bookmarks.Count; i++)
        {
            var bookmark = bookmarks[i];
            seen.Add(bookmark.Path);
            var hint = i < C.BookmarkSlots ? Shortcut(C.OpenBookmark(i + 1)) ?? "Bookmark" : "Bookmark";
            var path = bookmark.Path;
            if (FolderItem(trimmed, path, bookmark.Name, hint, () => _data.RemoveBookmark(path)) is { } match)
                found.Add((match.Score + BookmarkBonus + FrecencyBoost(_data.History.Score(path, now)), i, match.Item));
        }

        int order = bookmarks.Count;
        foreach (var (entry, frecency) in _data.History.Ranked(now))
        {
            if (!seen.Add(entry.Path))
                continue;
            var path = entry.Path;
            var forget = () =>
            {
                _data.ForgetVisits(path);
                return $"Forgot {path}.";
            };
            if (FolderItem(trimmed, path, null, null, forget) is { } match)
                found.Add((match.Score + FrecencyBoost(frecency), order++, match.Item));
        }

        // With no query: bookmarks in their order, then history by frecency. Otherwise best match first.
        var items = found
            .OrderByDescending(f => trimmed.Length > 0 ? f.Score : 0)
            .ThenBy(f => f.Order)
            .Take(PaletteLimit)
            .Select(f => f.Item)
            .ToList();

        // A typed absolute path goes straight there. No disk check: that could stall on every keystroke.
        if (LooksRooted(trimmed) && PathUtil.Normalize(trimmed) is { Length: > 0 } typed)
            items.Insert(0, new PaletteItem($"Go to {typed}", null, null, [], [], other => GoTo(typed, other)));

        return new PaletteResults(items,
            "Enter goes there   ·   Ctrl+Enter in the other pane   ·   Shift+Del forgets it   ·   > for commands");
    }

    private static double FrecencyBoost(double frecency) => 8 * Math.Log2(1 + frecency);

    private static bool LooksRooted(string text) =>
        text.Length >= 2 && (text[1] == ':' || text.StartsWith(@"\\", StringComparison.Ordinal))
        || text.StartsWith('~') || text.StartsWith('%');

    /// <summary>
    /// A folder as a palette row: its name, then the folder it's in. A bookmark with a name of its
    /// own shows that name, then the full path, and matches on either.
    /// </summary>
    private (int Score, PaletteItem Item)? FolderItem(string query, string path, string? name, string? hint, Func<string> remove)
    {
        var folderName = PathUtil.ShortName(path);
        int nameStart = path.Length - folderName.Length; // 0 for roots, whose "name" is the whole path
        bool customName = name is not null && name != folderName;
        var pathMatch = Fuzzy.Match(query, path, nameStart);

        if (customName)
        {
            var nameMatch = Fuzzy.Match(query, name!);
            if (nameMatch is not null && (pathMatch is null || nameMatch.Score >= pathMatch.Score))
                return (nameMatch.Score, new PaletteItem(name!, path, hint, nameMatch.Positions, [], Go, remove));
            if (pathMatch is null)
                return null;
            return (pathMatch.Score, new PaletteItem(name!, path, hint, [], pathMatch.Positions, Go, remove));
        }

        if (pathMatch is null)
            return null;
        var parent = nameStart > 0 ? path[..nameStart].TrimEnd('\\') : null;
        if (parent is { Length: 2 } && parent[1] == ':')
            parent += "\\"; // "C:" alone reads oddly
        var titleMarks = pathMatch.Positions.Where(p => p >= nameStart).Select(p => p - nameStart).ToArray();
        var detailMarks = parent is null ? [] : pathMatch.Positions.Where(p => p < parent.Length).ToArray();
        return (pathMatch.Score, new PaletteItem(folderName, parent, hint, titleMarks, detailMarks, Go, remove));

        void Go(bool other) => GoTo(path, other);
    }

    // ------------------------------------------------------------------
    // Bookmarks
    // ------------------------------------------------------------------

    private void ToggleBookmark()
    {
        var path = _active.CurrentPath;
        if (PathUtil.IsComputerRoot(path))
        {
            _active.ShowError("This PC can't be bookmarked; its drives can.");
            return;
        }
        _active.ShowInfo(_data.ToggleBookmark(path));
    }

    private void OpenBookmark(int index)
    {
        var bookmarks = _data.Bookmarks.Items;
        if (index < bookmarks.Count)
        {
            GoTo(bookmarks[index].Path, otherPane: false);
            return;
        }
        var add = Shortcut(C.ToggleBookmark) is { } key ? $" Press {key} to bookmark this folder." : "";
        _active.ShowInfo($"There's no bookmark {index + 1} yet.{add}");
    }

    private void BuildBookmarksMenu()
    {
        var items = _bookmarksButton.DropDownItems;
        foreach (ToolStripItem old in items.Cast<ToolStripItem>().ToList())
            old.Dispose();
        items.Clear();

        var current = _active.CurrentPath;
        if (!PathUtil.IsComputerRoot(current))
        {
            var text = _data.Bookmarks.Contains(current)
                ? $"Remove the bookmark for “{PathUtil.ShortName(current)}”"
                : $"Bookmark “{PathUtil.ShortName(current)}”";
            var toggle = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = Shortcut(C.ToggleBookmark) ?? "" };
            toggle.Click += (_, _) => ToggleBookmark();
            items.Add(toggle);
        }

        var bookmarks = _data.Bookmarks.Items;
        if (bookmarks.Count > 0)
            items.Add(new ToolStripSeparator());
        for (int i = 0; i < bookmarks.Count; i++)
        {
            var bookmark = bookmarks[i];
            var item = new ToolStripMenuItem(bookmark.Name.Replace("&", "&&"))
            {
                ToolTipText = bookmark.Path,
                ShortcutKeyDisplayString = i < C.BookmarkSlots ? Shortcut(C.OpenBookmark(i + 1)) ?? "" : "",
            };
            item.Click += (_, _) => GoTo(bookmark.Path, otherPane: false);
            items.Add(item);
        }

        items.Add(new ToolStripSeparator());
        var edit = new ToolStripMenuItem("Edit the bookmarks file…");
        edit.Click += (_, _) => _data.EditBookmarks(this);
        items.Add(edit);
    }

    // ------------------------------------------------------------------
    // Copy / move between panes
    // ------------------------------------------------------------------

    /// <summary>F5 / F6: copy or move the source pane's selection into the folder shown in the other pane.</summary>
    private async void OnTransferRequested(FilePane source, TransferKind kind)
    {
        var target = OtherPane(source);
        var sourceFolder = source.CurrentPath;
        var destination = target.CurrentPath;

        if (PathUtil.IsComputerRoot(sourceFolder))
            return;
        if (target == _left && LeftCollapsed)
        {
            // Never send files somewhere the user can't see.
            var show = Shortcut(C.ToggleLeftPane) is { } key ? $" Show it ({key}) to copy or move into it." : "";
            source.ShowError($"The left pane is hidden.{show}");
            return;
        }
        if (PathUtil.IsComputerRoot(destination))
        {
            source.ShowError("The other pane isn't showing a folder.");
            return;
        }
        if (PathUtil.SameLocation(sourceFolder, destination))
        {
            source.ShowError("Both panes show the same folder.");
            return;
        }

        var paths = source.GetCommandPaths();
        if (paths.Count == 0)
            return;

        var action = kind == TransferKind.Move ? FileAction.Move(paths, destination) : FileAction.Copy(paths, destination);
        var outcome = await FileActionRunner.DoAsync(action, Handle);
        if (IsDisposed)
            return;
        source.Finish(action, outcome, kind == TransferKind.Move ? "Move" : "Copy");
        ApplyOutcome(outcome); // selects what arrived in the destination
    }

    /// <summary>Refreshes every pane showing an affected folder, selecting the outcome's items there.</summary>
    private void ApplyOutcome(ActionOutcome outcome)
    {
        foreach (var folder in outcome.Folders)
        {
            var names = FilePane.NamesIn(outcome.Select, folder);
            _left.NotifyChanged(folder, names);
            _right.NotifyChanged(folder, names);
        }
    }

    // ------------------------------------------------------------------
    // Undo history (shared by both panes, across all folders)
    // ------------------------------------------------------------------

    /// <summary>Undoes (or redoes) the most recent <paramref name="count"/> actions, stopping at the first that fails.</summary>
    private async void StepHistory(int count, bool undo)
    {
        if (_undoBusy)
            return;
        if (FileOperationRunner.ActiveCount > 0)
        {
            // The running operation isn't on the history yet, so Ctrl+Z would skip past it.
            _active.ShowInfo("Wait for the current file operation to finish.");
            return;
        }

        _undoBusy = true;
        UpdateHistoryButtons();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var action = undo ? _history.TakeUndo() : _history.TakeRedo();
                if (action is null)
                    break;

                var description = action.Description;
                ActionOutcome outcome;
                try
                {
                    outcome = undo
                        ? await FileActionRunner.UndoAsync(action, Handle)
                        : await FileActionRunner.DoAsync(action, Handle);
                }
                catch (Exception ex)
                {
                    outcome = new ActionOutcome(ex.Message, false, false, action.AffectedFolders, [], null);
                }
                if (IsDisposed)
                    return;

                if (undo)
                {
                    // A failed or cancelled undo stays put so it can be retried (finished items are
                    // skipped next time). One with nothing left to act on is dropped.
                    if (outcome.Failed)
                        _history.PutUndo(action);
                    else if (outcome.DidSomething)
                        _history.PutRedo(action);
                }
                else
                {
                    // Whatever a redo did, even partly, is now the thing to undo.
                    if (outcome.DidSomething)
                        _history.PutUndo(action);
                    else if (outcome.Failed)
                        _history.PutRedo(action);
                }

                ApplyOutcome(outcome);
                Report(outcome, undo, description);
                if (outcome.Failed || !outcome.DidSomething)
                    break;
            }
        }
        finally
        {
            _undoBusy = false;
            UpdateHistoryButtons();
        }
    }

    private void Report(ActionOutcome outcome, bool undo, string description)
    {
        var verb = undo ? "Undo" : "Redo";
        var suffix = outcome.Note is null ? "" : $" {outcome.Note}";
        if (outcome.Error is not null)
            _active.ShowError($"{verb} failed: {outcome.Error}{suffix}");
        else if (outcome.Aborted)
            _active.ShowInfo($"{verb} cancelled: {description}.{suffix}");
        else if (!outcome.DidSomething)
            _active.ShowError($"Couldn't {verb.ToLowerInvariant()} “{description}”.{suffix}");
        else
            _active.ShowInfo($"{(undo ? "Undid" : "Redid")}: {description}.{suffix}");
    }

    private ToolStripSplitButton MakeHistoryButton(string text, Func<IReadOnlyList<FileAction>> items, string empty, Action<int> step)
    {
        var button = new ToolStripSplitButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
        button.ButtonClick += (_, _) => step(1);
        // The dropdown lists the history, newest first; picking an entry steps back to (and including) it.
        button.DropDownItems.Add(new ToolStripMenuItem(empty) { Enabled = false });
        button.DropDownOpening += (_, _) =>
        {
            button.DropDownItems.Clear();
            var list = items();
            if (list.Count == 0)
                button.DropDownItems.Add(new ToolStripMenuItem(empty) { Enabled = false });
            for (int i = 0; i < Math.Min(list.Count, HistoryMenuLimit); i++)
            {
                int count = i + 1;
                var item = new ToolStripMenuItem(list[i].Description);
                item.Click += (_, _) => step(count);
                button.DropDownItems.Add(item);
            }
            if (list.Count > HistoryMenuLimit)
                button.DropDownItems.Add(new ToolStripMenuItem($"… {list.Count - HistoryMenuLimit} older") { Enabled = false });
        };
        return button;
    }

    private void UpdateHistoryButtons()
    {
        _undoButton.Enabled = !_undoBusy && _history.CanUndo;
        _redoButton.Enabled = !_undoBusy && _history.CanRedo;
        _undoButton.ToolTipText = _history.UndoItems.FirstOrDefault() is { } u ? WithShortcut($"Undo {u.Description}", C.Undo) : "Nothing to undo";
        _redoButton.ToolTipText = _history.RedoItems.FirstOrDefault() is { } r ? WithShortcut($"Redo {r.Description}", C.Redo) : "Nothing to redo";
    }

    // ------------------------------------------------------------------
    // Collapsible left pane
    // ------------------------------------------------------------------

    private bool LeftCollapsed => _split.Panel1Collapsed;

    private void ToggleLeftPane()
    {
        if (LeftCollapsed)
            ExpandLeft();
        else
            CollapseLeft();
    }

    private void CollapseLeft()
    {
        _savedSplitterDistance = _split.SplitterDistance;
        BuildRail();
        _split.Panel1Collapsed = true;
        _rail.Visible = true;
        SetActive(_right);
        _right.FocusList();
        UpdatePaneToggle();
    }

    private void ExpandLeft()
    {
        _rail.Visible = false;
        _split.Panel1Collapsed = false;
        int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
        if (_savedSplitterDistance > 0 && max > _split.Panel1MinSize)
            _split.SplitterDistance = Math.Clamp(_savedSplitterDistance, _split.Panel1MinSize, max);
        _left.FocusList();
        UpdatePaneToggle();
    }

    private void UpdatePaneToggle()
    {
        _paneToggle.Text = LeftCollapsed ? "» Left pane" : "« Left pane";
        _paneToggle.ToolTipText = WithShortcut(LeftCollapsed ? "Show the left pane" : "Hide the left pane", C.ToggleLeftPane);
    }

    /// <summary>
    /// The icon rail that stands in for the hidden left pane: the left pane's folder (click to bring
    /// it back), then bookmarks, places and drives, which open in the right pane.
    /// </summary>
    private void BuildRail()
    {
        _rail.SuspendLayout();
        foreach (ToolStripItem item in _rail.Items.Cast<ToolStripItem>().ToList())
            item.Dispose();
        _rail.Items.Clear();
        _railImages.ForEach(i => i.Dispose());
        _railImages.Clear();

        var left = _left.CurrentPath;
        var show = Shortcut(C.ToggleLeftPane) is { } key ? $"Click or press {key} to show it" : "Click to show it";
        _rail.Items.Add(RailButton(left, $"Left pane: {PathUtil.ToDisplay(left)}\n{show}", ExpandLeft));

        var bookmarks = _data.Bookmarks.Items;
        if (bookmarks.Count > 0)
            _rail.Items.Add(new ToolStripSeparator());
        for (int i = 0; i < bookmarks.Count; i++)
        {
            var bookmark = bookmarks[i];
            var shortcut = i < C.BookmarkSlots && Shortcut(C.OpenBookmark(i + 1)) is { } k ? $"\n{k}" : "";
            _rail.Items.Add(RailButton(bookmark.Path, $"{bookmark.Name}\n{bookmark.Path}{shortcut}", () => OpenInRight(bookmark.Path)));
        }

        _rail.Items.Add(new ToolStripSeparator());
        foreach (var (name, path) in Places())
            _rail.Items.Add(RailButton(path, $"{name}\n{path}", () => OpenInRight(path)));
        _rail.Items.Add(new ToolStripSeparator());

        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.Name;
            _rail.Items.Add(RailButton(root, root.TrimEnd('\\'), () => OpenInRight(root)));
        }
        _rail.ResumeLayout();
    }

    private ToolStripButton RailButton(string location, string tip, Action onClick)
    {
        var button = new ToolStripButton
        {
            ToolTipText = tip,
            AutoToolTip = false,
            Padding = new Padding(3),
            Margin = new Padding(0, 1, 0, 1),
        };
        if (ShellIcons.Small(location) is { } image)
        {
            _railImages.Add(image);
            button.Image = image;
            button.DisplayStyle = ToolStripItemDisplayStyle.Image;
        }
        else
        {
            button.Text = PathUtil.ShortName(location) is { Length: > 0 } s ? s[..1] : "?";
            button.DisplayStyle = ToolStripItemDisplayStyle.Text;
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OpenInRight(string path)
    {
        if (_right.Navigate(path))
            _right.FocusList();
    }

    private static IEnumerable<(string Name, string Path)> Places()
    {
        var places = new List<(string, string)>
        {
            ("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", DownloadsFolder()),
        };
        return places.Where(p => p.Item2.Length > 0 && Directory.Exists(p.Item2));
    }

    /// <summary>.NET has no SpecialFolder for Downloads, and it can be relocated, so ask the shell.</summary>
    private static string DownloadsFolder()
    {
        try
        {
            using var folder = new ShellFolder(KNOWNFOLDERID.FOLDERID_Downloads);
            return folder.FileSystemPath ?? "";
        }
        catch (Exception)
        {
            return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _railImages.ForEach(i => i.Dispose());
            _data.Dispose();
        }
        base.Dispose(disposing);
    }
}
