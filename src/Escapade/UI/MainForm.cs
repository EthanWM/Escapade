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
    private const int CurrentTabBonus = 20;
    private const int OtherWorkspaceTabBonus = 5;
    private const int SessionSaveDelayMs = 2000;
    private const int TabFilesPerFolder = 5000;
    private const int TabFilesTotal = 50_000;

    private readonly ToolStrip _toolbar;
    private readonly ToolStripDropDownButton _workspaceButton;
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
    private readonly string[] _args;
    private readonly UndoHistory<FileAction> _history = new();
    private readonly UserData _data = new();
    private readonly Dictionary<string, Action> _commands;
    private readonly Dictionary<string, CommandInfo> _commandInfo;
    private readonly Session _session;
    private readonly bool _firstRun;
    private readonly string? _sessionProblem;
    private readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = SessionSaveDelayMs };
    private readonly CancellationTokenSource _listening = new();
    private readonly Dictionary<string, string?> _repoRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _repoHinted = new(StringComparer.OrdinalIgnoreCase);
    private FilePane _active;
    private CommandPalette? _palette;
    private CancellationTokenSource? _tabFilesScan;
    private List<TabFile> _tabFiles = [];
    private bool _undoBusy;
    private bool _showingWorkspace;
    private int _savedSplitterDistance;

    /// <summary>A file or folder inside one of the current workspace's tabs, for Ctrl+P.</summary>
    private sealed record TabFile(string Name, string Folder, int Pane, TabState Tab);

    /// <param name="args">Folders to open. On a first run, the left then right pane's folders; after that, new tabs.</param>
    public MainForm(string[] args)
    {
        _args = args;
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
        _split.SplitterMoved += (_, _) => ScheduleSessionSave();

        _workspaceButton = new ToolStripDropDownButton { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
        _workspaceButton.DropDownItems.Add(new ToolStripMenuItem("…")); // replaced on opening
        _workspaceButton.DropDownOpening += (_, _) => BuildWorkspaceMenu();

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
            _workspaceButton, new ToolStripSeparator(),
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

        // The session decides what the panes show. A first run starts from the arguments, or home.
        var (session, problem) = _data.LoadSession();
        _sessionProblem = problem;
        _firstRun = session is null;
        _session = session ?? Session.CreateDefault(StartFolder(args.ElementAtOrDefault(0)), StartFolder(args.ElementAtOrDefault(1) ?? args.ElementAtOrDefault(0)));
        _sessionTimer.Tick += (_, _) =>
        {
            _sessionTimer.Stop();
            SaveSession();
        };

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 720);
        StartPosition = _session.Window is null ? FormStartPosition.WindowsDefaultLocation : FormStartPosition.Manual;
        Text = "Escapade";
        // Docking runs in reverse z-order: the toolbar claims the full top edge, then the rail
        // the left edge below it, and the panes fill what's left.
        Controls.Add(_split);
        Controls.Add(_rail);
        Controls.Add(_toolbar);
        UpdateShortcutHints();
        ResumeLayout(false);
    }

    /// <summary>A command-line folder for a first run: a file means its folder; anything unusable means home.</summary>
    private static string StartFolder(string? arg)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (arg is null || PathUtil.Normalize(arg, Environment.CurrentDirectory) is not { } path)
            return home;
        if (File.Exists(path))
            return Path.GetDirectoryName(path) ?? home;
        return Directory.Exists(path) || PathUtil.IsComputerRoot(path) ? path : home;
    }

    private FilePane CreatePane()
    {
        var pane = new FilePane { Dock = DockStyle.Fill };
        pane.Enter += (_, _) => SetActive(pane);
        pane.CurrentPathChanged += (_, _) => UpdateTitle();
        pane.TransferRequested += OnTransferRequested;
        pane.ActionRecorded += _history.Record;
        pane.Visited += path =>
        {
            _data.RecordVisit(path);
            CheckRepoHint(pane, path);
        };
        pane.Shortcut = id => _data.Keymap.ShortcutFor(id);
        pane.TabsChanged += OnTabsChanged;
        pane.TabMoveRequested += (from, tab) =>
        {
            var to = OtherPane(from);
            if (to == _left && LeftCollapsed)
                SetLeftCollapsed(false, focus: false);
            to.AddTab(tab);
            to.FocusList();
        };
        return pane;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RestoreWindow();
        _split.SplitterDistance = _split.Width / 2;

        ShowWorkspace(_session.Current, focus: false);
        if (!_firstRun && _args.Length > 0)
            OpenFromCommandLine(Environment.CurrentDirectory, _args, activate: false);

        // After the panes have loaded, which clears their status bars, so problems stay visible.
        _data.Load(this);
        if (_sessionProblem is not null)
            _active.ShowError(_sessionProblem);
        SingleInstance.Listen(this, (cwd, args) => OpenFromCommandLine(cwd, args, activate: true), _listening.Token);
        ActiveControl = null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _active.FocusList();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        ScheduleSessionSave();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
            return;
        // Now, while the lists still exist to read selections and scroll positions from.
        _sessionTimer.Stop();
        SaveSession();
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
            [C.OpenInNewTab] = () => _active.OpenFocusedInNewTab(),
            [C.Undo] = () => StepHistory(1, undo: true),
            [C.Redo] = () => StepHistory(1, undo: false),
            [C.ToggleBookmark] = ToggleBookmark,
            [C.NewTab] = () =>
            {
                _active.NewTab();
                _active.FocusList();
            },
            [C.CloseTab] = () => _active.CloseTab(),
            [C.ReopenTab] = () => _active.ReopenClosedTab(),
            [C.NextTab] = () => _active.CycleTab(1),
            [C.PreviousTab] = () => _active.CycleTab(-1),
            [C.TabOverview] = ShowTabOverview,
            [C.MoveTabToOther] = () => _active.MoveActiveTabToOther(),
            [C.SwitchWorkspace] = () => ShowPalette(WorkspaceResults, ""),
            [C.NewWorkspace] = () => NewWorkspace(null),
            [C.RenameWorkspace] = RenameWorkspace,
            [C.DeleteWorkspace] = () => DeleteWorkspace(_session.Current),
            [C.RepoWorkspace] = OpenRepoWorkspace,
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
        for (int i = 1; i <= C.TabSlots; i++)
        {
            // As in browsers, the last number means the last tab, however many there are.
            int index = i == C.TabSlots ? int.MaxValue : i - 1;
            commands[C.SelectTab(i)] = () => _active.SelectTab(Math.Min(index, _active.Tabs.Count - 1));
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
        _workspaceButton.ToolTipText = WithShortcut("Workspaces: switch, create, rename", C.SwitchWorkspace);
        _goToButton.ToolTipText = WithShortcut("Go to a tab, bookmark, recent folder or file in a tab", C.GoTo);
        _bookmarksButton.ToolTipText = WithShortcut("Bookmarks. Bookmark this folder", C.ToggleBookmark);
        _commandsButton.ToolTipText = WithShortcut("Every command, with its shortcut", C.Commands);
        UpdatePaneToggle();
        UpdateHistoryButtons();
        if (LeftCollapsed)
            BuildRail();
    }

    private FilePane OtherPane(FilePane pane) => pane == _left ? _right : _left;

    private FilePane PaneAt(int side) => side == 0 ? _left : _right;

    private int SideOf(FilePane pane) => pane == _left ? 0 : 1;

    private void SetActive(FilePane pane)
    {
        if (_active == pane)
            return;
        _active.IsActive = false;
        _active = pane;
        _active.IsActive = true;
        Touch(pane);
        UpdateTitle();
        ScheduleSessionSave();
    }

    /// <summary>Marks the pane's current tab as the one in use, for Ctrl+Tab's most-recent-first order.</summary>
    private static void Touch(FilePane pane) => pane.Tabs.Active.LastActiveUtc = DateTime.UtcNow;

    private void OnTabsChanged(FilePane pane)
    {
        if (pane == _active)
            Touch(pane);
        if (!_showingWorkspace)
            ScheduleSessionSave();
    }

    private void UpdateTitle()
    {
        var workspace = _session is { Workspaces.Count: > 1 } ? $" — {_session.Current.Name}" : "";
        Text = $"{PathUtil.ShortName(_active.CurrentPath)}{workspace} — Escapade";
    }

    // ------------------------------------------------------------------
    // Going places, without duplicating tabs
    // ------------------------------------------------------------------

    /// <summary>
    /// Goes to a folder from somewhere that isn't browsing (Ctrl+P, a bookmark, the rail, the
    /// command line). If a tab in this workspace already shows it, that tab comes forward instead
    /// of a duplicate opening. Otherwise it opens in the active (or other) pane, or a new tab.
    /// </summary>
    /// <param name="onlyIn">Only look for, and open in, this pane (the rail opens in the right pane).</param>
    private void JumpTo(string path, PaletteTarget target = PaletteTarget.Here, FilePane? onlyIn = null)
    {
        var location = PathUtil.Normalize(path) ?? path;
        if (_session.Current.FindTab(location, onlyIn is null ? null : SideOf(onlyIn)) is var (side, index))
        {
            ShowTab(side, index);
            return;
        }

        var pane = onlyIn ?? (target == PaletteTarget.OtherPane ? OtherPane(_active) : _active);
        if (pane == _left && LeftCollapsed)
            SetLeftCollapsed(false, focus: false); // sending a folder to the hidden pane brings it back
        bool ok = target == PaletteTarget.NewTab ? pane.NavigateInNewTab(location) : pane.Navigate(location);
        if (ok)
            pane.FocusList();
    }

    /// <summary>Brings a tab of the current workspace forward and focuses it.</summary>
    private void ShowTab(int side, int index)
    {
        var pane = PaneAt(side);
        if (pane == _left && LeftCollapsed)
            SetLeftCollapsed(false, focus: false);
        pane.SelectTab(index);
        pane.FocusList();
    }

    /// <summary>Goes to a tab in any workspace, switching workspace first if needed.</summary>
    private void GoToTab(Workspace workspace, int side, TabState tab)
    {
        if (workspace != _session.Current)
        {
            // Arrange for the switch to land on the tab directly, rather than loading another first.
            var tabs = workspace.Pane(side);
            if (tabs.IndexOf(tab) is var i and >= 0)
                tabs.ActiveIndex = i;
            workspace.ActivePane = side;
            if (side == 0)
                workspace.LeftCollapsed = false;
            SwitchWorkspace(workspace);
            return;
        }
        if (PaneAt(side).Tabs.IndexOf(tab) is var index and >= 0)
            ShowTab(side, index);
    }

    private void ShowInOtherPane()
    {
        var path = _active.CurrentPath;
        var other = OtherPane(_active);
        if (other == _left && LeftCollapsed)
            SetLeftCollapsed(false, focus: false);
        other.Navigate(path);
    }

    /// <summary>Folders from a later launch (or this one's command line): each opens as a tab, or comes forward if open.</summary>
    private void OpenFromCommandLine(string workingFolder, string[] args, bool activate)
    {
        foreach (var arg in args)
        {
            if (PathUtil.Normalize(arg, workingFolder) is { } path)
                JumpTo(path, PaletteTarget.NewTab);
        }
        if (activate)
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    // ------------------------------------------------------------------
    // Workspaces
    // ------------------------------------------------------------------

    /// <summary>Puts a workspace's tabs and layout on screen.</summary>
    private void ShowWorkspace(Workspace workspace, bool focus)
    {
        _showingWorkspace = true;
        try
        {
            _left.LoadTabs(workspace.Left);
            _right.LoadTabs(workspace.Right);

            int width = _split.Width;
            int distance = (int)(workspace.SplitRatio * width);
            if (workspace.LeftCollapsed)
            {
                SetLeftCollapsed(true, focus: false);
                _savedSplitterDistance = distance; // after collapsing, which saves the current width
            }
            else
            {
                SetLeftCollapsed(false, focus: false);
                SetSplitterDistance(distance);
            }

            var pane = workspace.ActivePane == 1 || workspace.LeftCollapsed ? _right : _left;
            SetActive(pane);
            Touch(pane);
            if (focus)
                pane.FocusList();
            if (LeftCollapsed)
                BuildRail();
            UpdateWorkspaceButton();
            UpdateTitle();
        }
        finally
        {
            _showingWorkspace = false;
        }
    }

    /// <summary>Writes the on-screen state (selections, layout) back into the current workspace.</summary>
    private void CaptureWorkspace()
    {
        var workspace = _session.Current;
        _left.CaptureActiveTab();
        _right.CaptureActiveTab();
        workspace.LeftCollapsed = LeftCollapsed;
        int distance = LeftCollapsed ? _savedSplitterDistance : _split.SplitterDistance;
        if (WindowState != FormWindowState.Minimized && _split.Width > 0 && distance > 0)
            workspace.SplitRatio = Math.Clamp((double)distance / _split.Width, 0.1, 0.9);
        workspace.ActivePane = SideOf(_active);
    }

    private void SwitchWorkspace(Workspace workspace)
    {
        if (workspace == _session.Current)
            return;
        CaptureWorkspace();
        _session.Current = workspace;
        ShowWorkspace(workspace, focus: true);
        ScheduleSessionSave();
        _active.ShowInfo($"Workspace: {workspace.Name}");
    }

    /// <summary>A new workspace starting from the folders on screen now. Asks for a name if not given.</summary>
    private void NewWorkspace(string? name)
    {
        name ??= TextPrompt.Ask(this, "New workspace", "Name:", _session.UniqueName(PathUtil.ShortName(_active.CurrentPath)),
            n => n.Length == 0 ? "Give it a name." : _session.NameTaken(n) ? "There's already a workspace with that name." : null);
        if (name is null)
            return;
        var workspace = _session.Add(new Workspace(name, new PaneTabs(_left.CurrentPath), new PaneTabs(_right.CurrentPath))
        {
            ActivePane = SideOf(_active),
        });
        SwitchWorkspace(workspace);
    }

    private void RenameWorkspace()
    {
        var workspace = _session.Current;
        var name = TextPrompt.Ask(this, "Rename workspace", "Name:", workspace.Name,
            n => n.Length == 0 ? "Give it a name." : _session.NameTaken(n, workspace) ? "There's already a workspace with that name." : null);
        if (name is null || !_session.Rename(workspace, name))
            return;
        UpdateWorkspaceButton();
        UpdateTitle();
        ScheduleSessionSave();
    }

    /// <summary>Deletes a workspace after asking. Returns a message for the palette's footer.</summary>
    private string DeleteWorkspace(Workspace workspace)
    {
        if (_session.Workspaces.Count == 1)
        {
            _active.ShowInfo("That's the only workspace, so it stays.");
            return "The only workspace stays.";
        }
        int tabs = workspace.Left.Count + workspace.Right.Count;
        if (MessageBox.Show(this, $"Delete the workspace “{workspace.Name}” and its {tabs} tabs? The folders themselves aren't touched.",
                "Escapade", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return "Kept it.";
        }

        bool wasCurrent = workspace == _session.Current;
        if (!wasCurrent)
            CaptureWorkspace();
        _session.Remove(workspace);
        if (wasCurrent)
            ShowWorkspace(_session.Current, focus: true);
        UpdateWorkspaceButton();
        UpdateTitle();
        ScheduleSessionSave();
        var message = $"Deleted the workspace “{workspace.Name}”.";
        _active.ShowInfo(message);
        return message;
    }

    private void UpdateWorkspaceButton() => _workspaceButton.Text = _session.Current.Name.Replace("&", "&&");

    private void BuildWorkspaceMenu()
    {
        var items = _workspaceButton.DropDownItems;
        foreach (ToolStripItem old in items.Cast<ToolStripItem>().ToList())
            old.Dispose();
        items.Clear();

        foreach (var workspace in _session.Workspaces)
        {
            var item = new ToolStripMenuItem(workspace.Name.Replace("&", "&&"))
            {
                Checked = workspace == _session.Current,
                ShortcutKeyDisplayString = $"{workspace.Left.Count + workspace.Right.Count} tabs",
            };
            item.Click += (_, _) => SwitchWorkspace(workspace);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        Add("New workspace…", C.NewWorkspace, () => NewWorkspace(null));
        Add($"Rename “{_session.Current.Name}”…", C.RenameWorkspace, RenameWorkspace);
        Add($"Delete “{_session.Current.Name}”…", C.DeleteWorkspace, () => DeleteWorkspace(_session.Current)).Enabled =
            _session.Workspaces.Count > 1;
        items.Add(new ToolStripSeparator());
        Add("Open this git repo's workspace", C.RepoWorkspace, OpenRepoWorkspace);

        ToolStripMenuItem Add(string text, string command, Action action)
        {
            var item = new ToolStripMenuItem(text.Replace("&", "&&")) { ShortcutKeyDisplayString = Shortcut(command) ?? "" };
            item.Click += (_, _) => action();
            items.Add(item);
            return item;
        }
    }

    private PaletteResults WorkspaceResults(string query)
    {
        var words = Fuzzy.Words(query);
        var found = new List<(int Score, int Order, PaletteItem Item)>();
        for (int i = 0; i < _session.Workspaces.Count; i++)
        {
            var workspace = _session.Workspaces[i];
            if (Fuzzy.Match(words, workspace.Name) is not { } match)
                continue;
            var detail = workspace.Root ?? $"{workspace.Left.Count + workspace.Right.Count} tabs";
            var hint = workspace == _session.Current ? "current" : null;
            // Deleting asks first, in a dialog that closes the palette, so it runs after the palette's key handling.
            found.Add((match.Score, i, new PaletteItem(workspace.Name, detail, hint, match.Positions, [],
                _ => SwitchWorkspace(workspace), () =>
                {
                    BeginInvoke(() => DeleteWorkspace(workspace));
                    return "";
                })));
        }

        var items = found
            .OrderByDescending(f => words.Length > 0 ? f.Score : 0)
            .ThenBy(f => f.Order)
            .Select(f => f.Item)
            .ToList();

        var name = query.Trim();
        if (name.Length > 0 && !_session.NameTaken(name))
            items.Add(new PaletteItem($"Create the workspace “{name}”", "from the folders open now", null, [], [], _ => NewWorkspace(name)));
        else if (name.Length == 0)
            items.Add(new PaletteItem("New workspace…", "from the folders open now", Shortcut(C.NewWorkspace), [], [], _ => NewWorkspace(null)));

        return new PaletteResults(items, "Enter switches   ·   Shift+Del deletes   ·   type a new name to create one");
    }

    // ------------------------------------------------------------------
    // Git repos: a workspace per repo, offered rather than imposed
    // ------------------------------------------------------------------

    /// <summary>The repo root for a folder, looked up off the UI thread (it checks the disk) and cached.</summary>
    private async Task<string?> FindRepoRootAsync(string folder)
    {
        if (PathUtil.IsComputerRoot(folder))
            return null;
        if (_repoRoots.TryGetValue(folder, out var cached))
            return cached;
        var root = await Task.Run(() => GitRepo.FindRoot(folder, Path.Exists));
        _repoRoots[folder] = root;
        return root;
    }

    /// <summary>On first visiting a repo (per run), mention that it can have its own workspace.</summary>
    private async void CheckRepoHint(FilePane pane, string folder)
    {
        string? root;
        try
        {
            root = await FindRepoRootAsync(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (root is null || IsDisposed || Shortcut(C.RepoWorkspace) is not { } key)
            return;
        if (_session.Current.Root is { } current && PathUtil.SameLocation(current, root))
            return;
        if (!PathUtil.SameLocation(pane.CurrentPath, folder) || !_repoHinted.Add(root))
            return;
        var action = _session.FindByRoot(root) is not null ? "switches to its workspace" : "makes it a workspace";
        pane.ShowInfo($"This is the git repo “{PathUtil.ShortName(root)}”. {key} {action}.");
    }

    /// <summary>Switches to the workspace for the active folder's git repo, creating it if there isn't one.</summary>
    private async void OpenRepoWorkspace()
    {
        var folder = _active.CurrentPath;
        string? root;
        try
        {
            root = await FindRepoRootAsync(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            root = null;
        }
        if (IsDisposed)
            return;
        if (root is null)
        {
            _active.ShowInfo("This folder isn't in a git repository.");
            return;
        }

        if (_session.FindByRoot(root) is { } existing)
        {
            SwitchWorkspace(existing);
            return;
        }
        // The repo root on the left, and where you were on the right.
        var workspace = _session.Add(new Workspace(PathUtil.ShortName(root), new PaneTabs(root), new PaneTabs(folder))
        {
            Root = root,
        });
        SwitchWorkspace(workspace);
        _active.ShowInfo($"Made a workspace for the repo “{workspace.Name}”.");
    }

    // ------------------------------------------------------------------
    // Session: saved shortly after changes and on exit
    // ------------------------------------------------------------------

    private void ScheduleSessionSave()
    {
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSession()
    {
        CaptureWorkspace();
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _session.Window = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized);
        _data.SaveSession(_session);
    }

    /// <summary>Puts the window where it was, if that's still on a connected screen.</summary>
    private void RestoreWindow()
    {
        if (_session.Window is not { } saved)
            return;
        var rect = new Rectangle(saved.X, saved.Y, Math.Max(saved.Width, 400), Math.Max(saved.Height, 300));
        bool visible = Screen.AllScreens.Any(s =>
        {
            var overlap = Rectangle.Intersect(s.WorkingArea, rect);
            return overlap.Width >= 200 && overlap.Height >= 100;
        });
        if (visible)
        {
            // Twice: if the first move lands on a monitor with a different DPI, Windows rescales the
            // window as it arrives; the second sets the saved size there exactly.
            Bounds = rect;
            Bounds = rect;
        }
        else
        {
            StartPosition = FormStartPosition.WindowsDefaultLocation;
        }
        if (saved.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    // ------------------------------------------------------------------
    // Palette: Ctrl+P (tabs, folders, files in tabs), ">" commands, Ctrl+Tab, Alt+W
    // ------------------------------------------------------------------

    private void ShowPalette(string text) => ShowPalette(PaletteQuery, text);

    private void ShowPalette(Func<string, PaletteResults> source, string text, int initialIndex = 0, bool commitOnControlRelease = false)
    {
        _palette?.Close();
        var palette = new CommandPalette(this, source);
        palette.FormClosed += (_, _) =>
        {
            if (_palette == palette)
            {
                _palette = null;
                _tabFilesScan?.Cancel();
                _tabFiles = [];
            }
        };
        _palette = palette;
        if (source == PaletteQuery)
            ScanTabFiles();
        palette.Open(text, _toolbar.Bottom + LogicalToDeviceUnits(2), initialIndex, commitOnControlRelease);
    }

    private PaletteResults PaletteQuery(string text) =>
        text.StartsWith('>') ? CommandResults(text[1..]) : FolderResults(text);

    private PaletteResults CommandResults(string query)
    {
        var words = Fuzzy.Words(query);
        var found = new List<(int Score, int Order, PaletteItem Item)>();
        for (int order = 0; order < C.All.Count; order++)
        {
            var info = C.All[order];
            if (info.Id == C.Commands || CommandTitle(info) is not { } title)
                continue;
            if (Fuzzy.Match(words, title) is not { } match)
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
            .OrderByDescending(f => words.Length > 0 ? f.Score : 0)
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
        if (info.Id.StartsWith(C.SelectTabPrefix, StringComparison.Ordinal))
            return null; // nine "Go to tab N" rows would crowd the list; Ctrl+Tab covers it
        if (info.Id is C.RenameWorkspace)
            return $"Rename the workspace “{_session.Current.Name}”…";
        if (info.Id is C.DeleteWorkspace)
            return _session.Workspaces.Count > 1 ? $"Delete the workspace “{_session.Current.Name}”…" : null;
        return info.Title;
    }

    private PaletteResults FolderResults(string query)
    {
        var now = DateTime.UtcNow;
        var words = Fuzzy.Words(query);
        bool searching = words.Length > 0;
        var found = new List<(double Score, int Order, PaletteItem Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int order = 0;

        // Open tabs: this workspace's first. Without a query, the tab you're on is left out.
        var current = _session.Current;
        foreach (var (workspace, side, tab) in AllTabsByRecency())
        {
            bool here = workspace == current;
            if (!searching && (!here || tab == PaneAt(side).Tabs.Active && PaneAt(side) == _active))
                continue;
            if (here)
                seen.Add(tab.Location);
            var where = here ? SideName(side) : $"{workspace.Name} · {SideName(side)}";
            var (w, s, t) = (workspace, side, tab);
            if (FolderItem(words, tab.Location, null, $"Tab · {where}", null, _ => GoToTab(w, s, t)) is { } match)
            {
                double bonus = here ? CurrentTabBonus : OtherWorkspaceTabBonus;
                found.Add((match.Score + bonus + FrecencyBoost(_data.History.Score(tab.Location, now)), order++, match.Item));
            }
        }

        var bookmarks = _data.Bookmarks.Items;
        for (int i = 0; i < bookmarks.Count; i++)
        {
            var bookmark = bookmarks[i];
            seen.Add(bookmark.Path);
            var hint = i < C.BookmarkSlots ? Shortcut(C.OpenBookmark(i + 1)) ?? "Bookmark" : "Bookmark";
            var path = bookmark.Path;
            if (FolderItem(words, path, bookmark.Name, hint, () => _data.RemoveBookmark(path), t => JumpTo(path, t)) is { } match)
                found.Add((match.Score + BookmarkBonus + FrecencyBoost(_data.History.Score(path, now)), order++, match.Item));
        }

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
            if (FolderItem(words, path, null, null, forget, t => JumpTo(path, t)) is { } match)
                found.Add((match.Score + FrecencyBoost(frecency), order++, match.Item));
        }

        // Files and folders inside this workspace's tabs, once there's enough of a query to be selective.
        if (query.Trim().Length >= 2)
        {
            foreach (var file in _tabFiles)
            {
                if (Fuzzy.Match(words, file.Name) is not { } match)
                    continue;
                var f = file;
                var item = new PaletteItem(file.Name, file.Folder, $"in a tab · {SideName(file.Pane)}", match.Positions, [], _ =>
                {
                    GoToTab(_session.Current, f.Pane, f.Tab);
                    PaneAt(f.Pane).NotifyChanged(f.Folder, [f.Name]); // selects it once the tab has listed
                });
                found.Add((match.Score - 5, order++, item));
            }
        }

        var items = found
            .OrderByDescending(f => searching ? f.Score : 0)
            .ThenBy(f => f.Order)
            .Take(PaletteLimit)
            .Select(f => f.Item)
            .ToList();

        // A typed absolute path goes straight there. No disk check: that could stall on every keystroke.
        var trimmed = query.Trim();
        if (LooksRooted(trimmed) && PathUtil.Normalize(trimmed) is { Length: > 0 } typed)
            items.Insert(0, new PaletteItem($"Go to {typed}", null, null, [], [], t => JumpTo(typed, t)));

        return new PaletteResults(items,
            "Enter goes there   ·   Ctrl+Enter other pane   ·   Shift+Enter new tab   ·   Shift+Del forgets   ·   > commands");
    }

    private static string SideName(int side) => side == 0 ? "Left" : "Right";

    /// <summary>Every tab in every workspace, most recently used first.</summary>
    private List<(Workspace Workspace, int Side, TabState Tab)> AllTabsByRecency() =>
        _session.Workspaces
            .SelectMany(w => w.AllTabs().Select(t => (w, t.Pane, t.Tab)))
            .OrderByDescending(t => t.Tab.LastActiveUtc)
            .ToList();

    private static double FrecencyBoost(double frecency) => 8 * Math.Log2(1 + frecency);

    private static bool LooksRooted(string text) =>
        text.Length >= 2 && (text[1] == ':' || text.StartsWith(@"\\", StringComparison.Ordinal))
        || text.StartsWith('~') || text.StartsWith('%');

    /// <summary>
    /// A folder as a palette row: its name, then the folder it's in. A bookmark with a name of its
    /// own shows that name, then the full path, and matches on either.
    /// </summary>
    private static (int Score, PaletteItem Item)? FolderItem(
        string[] words, string path, string? name, string? hint, Func<string>? remove, Action<PaletteTarget> go)
    {
        var folderName = PathUtil.ShortName(path);
        int nameStart = PathUtil.IsComputerRoot(path) ? 0 : path.Length - folderName.Length; // 0 for roots too
        var text = PathUtil.ToDisplay(path);
        bool customName = name is not null && name != folderName;
        var pathMatch = Fuzzy.Match(words, text, nameStart);

        if (customName)
        {
            var nameMatch = Fuzzy.Match(words, name!);
            if (nameMatch is not null && (pathMatch is null || nameMatch.Score >= pathMatch.Score))
                return (nameMatch.Score, new PaletteItem(name!, path, hint, nameMatch.Positions, [], go, remove));
            if (pathMatch is null)
                return null;
            return (pathMatch.Score, new PaletteItem(name!, path, hint, [], pathMatch.Positions, go, remove));
        }

        if (pathMatch is null)
            return null;
        var parent = nameStart > 0 ? text[..nameStart].TrimEnd('\\') : null;
        if (parent is { Length: 2 } && parent[1] == ':')
            parent += "\\"; // "C:" alone reads oddly
        var titleMarks = pathMatch.Positions.Where(p => p >= nameStart).Select(p => p - nameStart).ToArray();
        var detailMarks = parent is null ? [] : pathMatch.Positions.Where(p => p < parent.Length).ToArray();
        return (pathMatch.Score, new PaletteItem(folderName, parent, hint, titleMarks, detailMarks, go, remove));
    }

    /// <summary>
    /// Lists what's in each of this workspace's tabs, for Ctrl+P's file search. Visible tabs use
    /// what their panes already listed; the rest are read in the background (capped), and the
    /// palette refreshes when they arrive.
    /// </summary>
    private void ScanTabFiles()
    {
        _tabFilesScan?.Cancel();
        var scan = _tabFilesScan = new CancellationTokenSource();
        var token = scan.Token;

        var known = new List<TabFile>();
        var toRead = new List<(int Side, TabState Tab, string Folder)>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (side, _, tab) in _session.Current.AllTabs().OrderByDescending(t => t.Tab.LastActiveUtc))
        {
            var folder = tab.Location;
            if (PathUtil.IsComputerRoot(folder) || !folders.Add(folder))
                continue;
            var pane = PaneAt(side);
            if (pane.Tabs.Active == tab && !pane.IsLoading && PathUtil.SameLocation(pane.CurrentPath, folder))
                known.AddRange(pane.Entries.Take(TabFilesPerFolder).Select(e => new TabFile(e.Name, folder, side, tab)));
            else
                toRead.Add((side, tab, folder));
        }
        _tabFiles = known;
        if (toRead.Count == 0)
            return;

        int budget = TabFilesTotal - known.Count;
        Task.Run(() =>
        {
            var read = new List<TabFile>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var (side, tab, folder) in toRead)
            {
                if (token.IsCancellationRequested || read.Count >= budget)
                    break;
                try
                {
                    foreach (var name in Directory.EnumerateFileSystemEntries(folder, "*", options).Take(TabFilesPerFolder))
                    {
                        if (token.IsCancellationRequested || read.Count >= budget)
                            break;
                        read.Add(new TabFile(Path.GetFileName(name), folder, side, tab));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A tab whose folder has gone or can't be read just has no files to offer.
                }
            }
            return read;
        }, token).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && !token.IsCancellationRequested && _tabFilesScan == scan)
            {
                _tabFiles = [.. known, .. task.Result];
                _palette?.Requery();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Ctrl+Tab: every tab in every workspace, most recent first, starting on the previous one.
    /// Held Ctrl picks on release, so a quick Ctrl+Tab flips between the last two tabs.
    /// </summary>
    private void ShowTabOverview()
    {
        bool held = (ModifierKeys & Keys.Control) != 0;
        ShowPalette(TabResults, "", initialIndex: held ? 1 : 0, commitOnControlRelease: held);
    }

    private PaletteResults TabResults(string query)
    {
        var words = Fuzzy.Words(query);
        bool several = _session.Workspaces.Count > 1;
        var found = new List<(int Score, int Order, PaletteItem Item)>();
        int order = 0;
        foreach (var (workspace, side, tab) in AllTabsByRecency())
        {
            var hint = several ? $"{workspace.Name} · {SideName(side)}" : SideName(side);
            var (w, s, t) = (workspace, side, tab);
            Func<string> close = () =>
            {
                var tabs = w.Pane(s);
                if (tabs.Count <= 1)
                    return "That's the only tab in its pane, so it stays.";
                if (w == _session.Current)
                    PaneAt(s).CloseTab(tabs.IndexOf(t));
                else
                    tabs.Close(tabs.IndexOf(t));
                ScheduleSessionSave();
                return $"Closed {PathUtil.ToDisplay(t.Location)}.";
            };
            if (FolderItem(words, tab.Location, null, hint, close, _ => GoToTab(w, s, t)) is { } match)
                found.Add((match.Score, order++, match.Item));
        }
        var items = found
            .OrderByDescending(f => words.Length > 0 ? f.Score : 0)
            .ThenBy(f => f.Order)
            .Select(f => f.Item)
            .ToList();
        return new PaletteResults(items, "Release Ctrl or press Enter to switch   ·   Shift+Del closes the tab");
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
            JumpTo(bookmarks[index].Path);
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
            var toggle = new ToolStripMenuItem(text.Replace("&", "&&")) { ShortcutKeyDisplayString = Shortcut(C.ToggleBookmark) ?? "" };
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
            item.Click += (_, _) => JumpTo(bookmark.Path);
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
    // Undo history (shared by both panes, across all folders and workspaces)
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
        SetLeftCollapsed(!LeftCollapsed, focus: true);
        ScheduleSessionSave();
    }

    private void SetLeftCollapsed(bool collapsed, bool focus)
    {
        if (collapsed == LeftCollapsed)
            return;
        if (collapsed)
        {
            _savedSplitterDistance = _split.SplitterDistance;
            BuildRail();
            _split.Panel1Collapsed = true;
            _rail.Visible = true;
            SetActive(_right);
            if (focus)
                _right.FocusList();
        }
        else
        {
            _rail.Visible = false;
            _split.Panel1Collapsed = false;
            if (_savedSplitterDistance > 0)
                SetSplitterDistance(_savedSplitterDistance);
            if (focus)
                _left.FocusList();
        }
        UpdatePaneToggle();
    }

    private void SetSplitterDistance(int distance)
    {
        int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
        if (max > _split.Panel1MinSize)
            _split.SplitterDistance = Math.Clamp(distance, _split.Panel1MinSize, max);
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
        _rail.Items.Add(RailButton(left, $"Left pane: {PathUtil.ToDisplay(left)}\n{show}", () =>
        {
            SetLeftCollapsed(false, focus: true);
            ScheduleSessionSave();
        }));

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

    /// <summary>The rail's places open in the right pane, or bring forward a right-pane tab already there.</summary>
    private void OpenInRight(string path) => JumpTo(path, onlyIn: _right);

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
            _listening.Cancel();
            _listening.Dispose();
            _tabFilesScan?.Cancel();
            _sessionTimer.Dispose();
            _railImages.ForEach(i => i.Dispose());
            _data.Dispose();
        }
        base.Dispose(disposing);
    }
}
