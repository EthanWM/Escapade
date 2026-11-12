using System.ComponentModel;
using System.Diagnostics;
using Escapade.Core;
using static Escapade.Interop.NativeMethods;
using CMF = Vanara.PInvoke.Shell32.CMF;

namespace Escapade.UI;

internal enum TransferKind
{
    Copy,
    Move,
}

/// <summary>
/// One self-contained file pane: path bar, virtual list, filter, history, background loading and
/// single-pane file commands. Commands that involve both panes (F5/F6) are raised as events.
/// </summary>
internal sealed class FilePane : UserControl
{
    private const int BatchIntervalMs = 50;
    private const int OpenManyThreshold = 10;
    private const int WatchQuietMs = 300;
    private const int WatchMaxDelayMs = 1500;

    private static readonly string[] ColumnNames = ["Name", "Date modified", "Type", "Size"];

    private readonly Panel _activeStrip;
    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly Button _upButton;
    private readonly TextBox _pathBox;
    private readonly FileListView _list;
    private readonly Label _filterLabel;
    private readonly Label _statusLabel;
    private readonly ContextMenuStrip _menu;
    private readonly ToolTip _toolTip = new();
    private readonly IconCache _icons;
    private readonly TabStrip _tabStrip;
    private readonly ContextMenuStrip _tabMenu;
    private readonly System.Windows.Forms.Timer _watchTimer;

    private PaneTabs _tabs = new(PathUtil.ComputerRoot);
    private TabState? _restoring;           // the tab whose saved selection and scroll apply once it has listed
    private NavigationHistory _history => _tabs.Active.History;
    private string _location = PathUtil.ComputerRoot;
    private List<FileEntry> _all = [];   // everything loaded so far, sorted; never mutated in place
    private List<FileEntry> _view = [];  // _all after filtering; may be the same instance
    private EntryComparer _comparer = EntryComparer.Default;
    private string _filter = "";
    private bool _showHidden = true;
    private bool _isActive;

    // Until the user moves the selection themselves, the pane keeps choosing it: the first item,
    // the folder we just came up out of, or the first filter match, even as batches reorder rows.
    private bool _autoSelect;
    private string? _autoTargetName;
    private int _lastAutoIndex = -1;

    private CancellationTokenSource? _loadCts;
    private int _generation;
    private bool _loading;
    private string? _error;
    private bool _statusUpdateQueued;

    // A reload re-lists the current folder in the background and swaps the result in all at once.
    private bool _isReload;
    private List<FileEntry>? _reloadBuffer;
    private SelectionSnapshot? _reloadSelection;
    private Action? _afterReload;

    private FileSystemWatcher? _watcher;
    private long _firstWatchEventTick;

    private bool _renameArmed;
    private bool _editing;
    private bool _reloadAfterEdit;

    private bool _pendingVisit;           // the current navigation counts as a visit once it lists cleanly
    private bool _messageIsInfo;          // _error holds a notice ("Undid: ...") rather than an error
    private Point _menuPoint;             // where the app's context menu last opened (screen)
    private bool _menuOnBackground;       // ...and whether it was for empty space rather than items
    private ShellContextMenuHost? _nativeMenu; // the Windows menu, while it's open

    public FilePane()
    {
        SuspendLayout();

        _icons = new IconCache(SystemInformation.SmallIconSize);

        _activeStrip = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };

        _tabStrip = new TabStrip { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _tabStrip.TabClicked += index =>
        {
            SelectTab(index);
            FocusList(); // the strip never takes focus, so make this the active pane explicitly
        };
        _tabStrip.CloseClicked += index => CloseTab(index);
        _tabStrip.NewTabClicked += () =>
        {
            NewTab();
            FocusList();
        };
        _tabMenu = new ContextMenuStrip();
        _tabStrip.MenuRequested += ShowTabMenu;

        _backButton = MakeNavButton("←", "Back (Alt+Left)", (_, _) => GoBack());
        _forwardButton = MakeNavButton("→", "Forward (Alt+Right)", (_, _) => GoForward());
        _upButton = MakeNavButton("↑", "Up (Backspace, Alt+Up)", (_, _) => GoUp());

        _pathBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 3, 3, 3),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Content,
            ForeColor = Theme.Text,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.FileSystemDirectories,
        };
        _toolTip.SetToolTip(_pathBox, "Ctrl+L, Alt+D or F4 to edit");
        _pathBox.KeyDown += OnPathBoxKeyDown;
        _pathBox.Enter += (_, _) => BeginInvoke(_pathBox.SelectAll);
        _pathBox.Leave += (_, _) => _pathBox.Text = PathUtil.ToDisplay(_location);

        var pathBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(2, 2, 2, 2),
            BackColor = Theme.Content, // continues the active tab down to the list, as in a browser
        };
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathBar.Controls.Add(_backButton, 0, 0);
        pathBar.Controls.Add(_forwardButton, 1, 0);
        pathBar.Controls.Add(_upButton, 2, 0);
        pathBar.Controls.Add(_pathBox, 3, 0);

        _menu = BuildContextMenu();

        _list = new FileListView
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BorderStyle = BorderStyle.None,
            SmallImageList = _icons.ImageList,
            LabelEdit = true, // edits only start via BeginRename; stray click-to-rename is cancelled
            ContextMenuStrip = _menu,
            BackColor = Theme.Content,
            ForeColor = Theme.Text,
        };
        if (Theme.IsDark)
            _list.UseThemedRows(); // so selected rows are the theme's orange, not the native theme's colour
        _list.Columns.Add(ColumnNames[0], 260);
        _list.Columns.Add(ColumnNames[1], 130);
        _list.Columns.Add(ColumnNames[2], 100);
        _list.Columns.Add(ColumnNames[3], 80, HorizontalAlignment.Right);
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.KeyDown += OnListKeyDown;
        _list.KeyPress += OnListKeyPress;
        _list.MouseDown += OnListMouseDown;
        _list.MouseDoubleClick += OnListMouseDoubleClick;
        _list.ColumnClick += OnColumnClick;
        _list.BeforeLabelEdit += OnBeforeLabelEdit;
        _list.AfterLabelEdit += OnAfterLabelEdit;
        _list.SelectedIndexChanged += (_, _) => QueueStatusUpdate();
        _list.VirtualItemsSelectionRangeChanged += (_, _) => QueueStatusUpdate();

        _filterLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 0, 6, 0),
            Padding = new Padding(4, 2, 4, 2),
            BackColor = Theme.FilterBack,
            ForeColor = Theme.FilterText,
            Visible = false,
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };

        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(2, 0, 2, 0),
            BackColor = Theme.StatusBar,
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusBar.Controls.Add(_filterLabel, 0, 0);
        statusBar.Controls.Add(_statusLabel, 1, 0);

        // Top to bottom: accent strip, tabs, path bar, divider, list, divider, status bar. The pane's
        // own padding and back colour draw a 1px frame around it all (accented when active).
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty,
            BackColor = Theme.Surface, // explicit, or it would inherit the frame colour
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(_activeStrip, 0, 0);
        layout.Controls.Add(_tabStrip, 0, 1);
        layout.Controls.Add(pathBar, 0, 2);
        layout.Controls.Add(Divider(), 0, 3);
        layout.Controls.Add(_list, 0, 4);
        layout.Controls.Add(Divider(), 0, 5);
        layout.Controls.Add(statusBar, 0, 6);

        Padding = new Padding(1);

        _watchTimer = new System.Windows.Forms.Timer();
        _watchTimer.Tick += (_, _) =>
        {
            _watchTimer.Stop();
            Reload();
        };

        Controls.Add(layout);
        UpdateTabStrip();
        UpdateColumnHeaders();
        UpdateNavButtons();
        UpdateActiveStrip();
        ResumeLayout(false);
    }

    public event EventHandler? CurrentPathChanged;

    /// <summary>Tabs were added, closed, moved or switched, or the active tab went somewhere: worth saving.</summary>
    public event Action<FilePane>? TabsChanged;

    /// <summary>The user asked to move a tab to the other pane (it has already been taken out of this one).</summary>
    public event Action<FilePane, TabState>? TabMoveRequested;

    /// <summary>The user asked to copy or move this pane's selection to the other pane.</summary>
    public event Action<FilePane, TransferKind>? TransferRequested;

    /// <summary>An undoable file operation finished (rename, delete to Recycle Bin, new folder).</summary>
    public event Action<FileAction>? ActionRecorded;

    /// <summary>
    /// The user went to a folder and it listed without error: a visit for Ctrl+P's history.
    /// Not raised for being bounced back from a folder that failed, or for the start-up folder.
    /// </summary>
    public event Action<string>? Visited;

    /// <summary>The current shortcut for a command id ("F2"), or null if it has none. Set by the main window.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, string?> Shortcut { get; set; } = _ => null;

    /// <summary>Re-reads <see cref="Shortcut"/> for the tooltips, after the keymap changes.</summary>
    public void RefreshShortcutHints()
    {
        _toolTip.SetToolTip(_backButton, WithKey("Back", CommandCatalog.Back));
        _toolTip.SetToolTip(_forwardButton, WithKey("Forward", CommandCatalog.Forward));
        _toolTip.SetToolTip(_upButton, Shortcut(CommandCatalog.Up) is { } up ? $"Up (Backspace, {up})" : "Up (Backspace)");
        _toolTip.SetToolTip(_pathBox, Shortcut(CommandCatalog.EditPath) is { } edit ? $"{edit} to edit" : "Click to edit");

        string WithKey(string text, string command) => Shortcut(command) is { } key ? $"{text} ({key})" : text;
    }

    public string CurrentPath => _location;

    public bool IsEditingName => _editing;

    public bool IsListFocused => _list.Focused;

    public PaneTabs Tabs => _tabs;

    /// <summary>What's listed so far in the current folder (all of it once <see cref="IsLoading"/> is false).</summary>
    public IReadOnlyList<FileEntry> Entries => _all;

    public bool IsLoading => _loading;

    /// <summary>Whether this is the pane commands act on. Shown as an accent strip along the top.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            _tabStrip.PaneActive = value;
            UpdateActiveStrip();
        }
    }

    // ------------------------------------------------------------------
    // Navigation
    // ------------------------------------------------------------------

    /// <summary>
    /// Navigates to user-supplied text. A file path navigates to its folder and selects the file.
    /// Returns false (and shows why) if the path is invalid or doesn't exist.
    /// </summary>
    /// <param name="countVisit">Whether this counts as a visit for <see cref="Visited"/>; false for the start-up folders.</param>
    public bool Navigate(string input, bool countVisit = true)
    {
        if (!Resolve(input, out var path, out var select))
            return false;

        if (_history.Current is { } current && PathUtil.SameLocation(current.Location, path))
        {
            // Re-entering the current folder acts as a refresh rather than a new history entry.
            if (select is null)
                Reload();
            else
                NotifyChanged(path, [select]);
            return true;
        }

        RememberSelection();
        _history.Push(path);
        LoadLocation(path, select, countVisit);
        return true;
    }

    /// <summary>Like <see cref="Navigate"/>, but in a new tab after the current one.</summary>
    public bool NavigateInNewTab(string input)
    {
        if (!Resolve(input, out var path, out var select))
            return false;
        EndRename();
        CaptureActiveTab();
        _tabs.Add(path);
        LoadLocation(path, select, countVisit: true);
        return true;
    }

    /// <summary>User text to a folder (and a file in it to select). Shows why and returns false if it isn't one.</summary>
    private bool Resolve(string input, out string path, out string? select)
    {
        select = null;
        path = PathUtil.ComputerRoot;
        if (PathUtil.Normalize(input, PathUtil.IsComputerRoot(_location) ? null : _location) is not { } normalized)
        {
            ShowError($"Not a valid path: {input}");
            return false;
        }
        path = normalized;
        if (PathUtil.IsComputerRoot(path))
            return true;
        if (File.Exists(path))
        {
            select = Path.GetFileName(path);
            path = Path.GetDirectoryName(path)!;
            return true;
        }
        if (Directory.Exists(path))
            return true;
        ShowError($"Folder not found: {path}");
        return false;
    }

    public void GoBack()
    {
        RememberSelection();
        if (_history.GoBack() is { } entry)
            LoadLocation(entry.Location, entry.SelectedName, countVisit: true);
    }

    public void GoForward()
    {
        RememberSelection();
        if (_history.GoForward() is { } entry)
            LoadLocation(entry.Location, entry.SelectedName, countVisit: true);
    }

    public void GoUp()
    {
        if (PathUtil.GetParent(_location) is not { } parent)
            return;
        RememberSelection();
        var cameFrom = PathUtil.GetNameInParent(_location);
        _history.Push(parent);
        LoadLocation(parent, cameFrom, countVisit: true);
    }

    public void FocusList() => _list.Focus();

    public void FocusPathBar()
    {
        _pathBox.Focus();
        _pathBox.SelectAll();
    }

    /// <summary>
    /// Something changed in <paramref name="folder"/>. If this pane shows it, reload quietly and
    /// optionally select <paramref name="select"/> (e.g. the items just copied here).
    /// </summary>
    public void NotifyChanged(string folder, IReadOnlyCollection<string>? select = null, Action? after = null)
    {
        if (!PathUtil.SameLocation(folder, _location))
            return;

        SelectionSnapshot? selection = null;
        if (select is { Count: > 0 })
        {
            selection = CaptureSelection();
            selection.All = false;
            selection.Names.Clear();
            selection.Names.UnionWith(select);
            selection.Focus = select.First();
        }
        Reload(selection, after);
    }

    private void NavigateInto(FileEntry entry)
    {
        RememberSelection();
        var path = PathUtil.Combine(_location, entry);
        _history.Push(path);
        LoadLocation(path, null, countVisit: true);
    }

    private void RememberSelection()
    {
        if (_history.Current is { } current)
            current.SelectedName = FocusedEntry()?.Name;
    }

    // ------------------------------------------------------------------
    // Tabs. Only the active tab is live; the rest are saved state (folder, history, selection,
    // scroll) that's loaded back when switched to. Switching isn't navigating: no history entry,
    // no visit, and no disk check on the UI thread; the background loader reports a missing folder.
    // ------------------------------------------------------------------

    /// <summary>Shows a different set of tabs (another workspace, or the restored session).</summary>
    public void LoadTabs(PaneTabs tabs)
    {
        EndRename();
        _tabs = tabs;
        ShowActiveTab(countVisit: false);
    }

    /// <summary>
    /// Saves the active tab's selection and scroll position into it, before switching away from it
    /// or saving the session. Skipped while its saved state is still waiting to be applied.
    /// </summary>
    public void CaptureActiveTab()
    {
        var tab = _tabs.Active;
        if (_restoring == tab || !_list.IsHandleCreated || !PathUtil.SameLocation(tab.Location, _location))
            return;
        RememberSelection();
        var snapshot = CaptureSelection();
        tab.SelectedAll = snapshot.All;
        tab.Selected = snapshot.All ? [] : snapshot.Names.ToList();
        tab.Focus = snapshot.Focus;
        tab.Top = snapshot.Top;
    }

    private void ShowActiveTab(bool countVisit)
    {
        var tab = _tabs.Active;
        LoadLocation(tab.Location, tab.History.Current?.SelectedName, countVisit, restore: tab);
    }

    public void SelectTab(int index)
    {
        if ((uint)index >= (uint)_tabs.Count || index == _tabs.ActiveIndex)
            return;
        EndRename();
        CaptureActiveTab();
        _tabs.ActiveIndex = index;
        ShowActiveTab(countVisit: false);
    }

    public void CycleTab(int delta)
    {
        int count = _tabs.Count;
        if (count > 1)
            SelectTab(((_tabs.ActiveIndex + delta) % count + count) % count);
    }

    /// <summary>Opens a tab after the current one: at <paramref name="location"/>, or a copy of the current folder.</summary>
    public void NewTab(string? location = null)
    {
        EndRename();
        CaptureActiveTab();
        _tabs.Add(location ?? _location);
        ShowActiveTab(countVisit: location is not null);
    }

    public int ImportTabs(IReadOnlyList<string> locations)
    {
        if (locations.Count == 0)
            return 0;
        EndRename();
        CaptureActiveTab();
        int imported = _tabs.AddRange(locations);
        ShowActiveTab(countVisit: true);
        return imported;
    }

    /// <summary>Ctrl+Enter or middle-click: the focused folder in a new tab.</summary>
    public void OpenFocusedInNewTab()
    {
        if (FocusedEntry() is { IsContainer: true } entry)
            NewTab(PathUtil.Combine(_location, entry));
    }

    public void CloseTab(int? index = null)
    {
        int i = index ?? _tabs.ActiveIndex;
        if (_tabs.Count <= 1)
        {
            ShowInfo("That's the only tab in this pane.");
            return;
        }
        bool active = i == _tabs.ActiveIndex;
        if (active)
        {
            EndRename();
            CaptureActiveTab(); // so reopening it brings back the selection too
        }
        _tabs.Close(i);
        if (active)
        {
            ShowActiveTab(countVisit: false);
        }
        else
        {
            UpdateTabStrip();
            TabsChanged?.Invoke(this);
        }
    }

    private void CloseOtherTabs(int keepIndex)
    {
        var keep = _tabs.Tabs[keepIndex];
        EndRename();
        CaptureActiveTab();
        bool wasActive = _tabs.Active == keep;
        for (int i = _tabs.Count - 1; i >= 0; i--)
        {
            if (_tabs.Tabs[i] != keep)
                _tabs.Close(i);
        }
        _tabs.ActiveIndex = _tabs.IndexOf(keep);
        if (wasActive)
        {
            UpdateTabStrip();
            TabsChanged?.Invoke(this);
        }
        else
        {
            ShowActiveTab(countVisit: false);
        }
    }

    public void ReopenClosedTab()
    {
        if (!_tabs.HasClosed)
        {
            ShowInfo("There are no closed tabs to reopen.");
            return;
        }
        EndRename();
        CaptureActiveTab();
        _tabs.ReopenClosed();
        ShowActiveTab(countVisit: false);
    }

    /// <summary>Takes the active tab out and hands it to the main window for the other pane.</summary>
    public void MoveActiveTabToOther()
    {
        if (_tabs.Count <= 1)
        {
            ShowInfo("That's the only tab in this pane, so it stays.");
            return;
        }
        EndRename();
        CaptureActiveTab();
        var tab = _tabs.Remove(_tabs.ActiveIndex)!;
        ShowActiveTab(countVisit: false);
        TabMoveRequested?.Invoke(this, tab);
    }

    /// <summary>Adds an existing tab (moved from the other pane) and shows it.</summary>
    public void AddTab(TabState tab)
    {
        EndRename();
        CaptureActiveTab();
        _tabs.Add(tab);
        ShowActiveTab(countVisit: false);
    }

    /// <summary>Cancels an open rename: the view is about to be swapped out from under it.</summary>
    private void EndRename()
    {
        if (_editing && _list.IsHandleCreated)
            SendMessage(_list.Handle, LVM_CANCELEDITLABEL, 0, 0);
    }

    private void UpdateTabStrip() =>
        _tabStrip.SetTabs(
            _tabs.Tabs.Select(t => (PathUtil.ShortName(t.Location), PathUtil.ToDisplay(t.Location))).ToList(),
            _tabs.ActiveIndex);

    private void ShowTabMenu(int index, Point location)
    {
        foreach (ToolStripItem old in _tabMenu.Items.Cast<ToolStripItem>().ToList())
            old.Dispose();
        _tabMenu.Items.Clear();

        if (index >= 0)
        {
            Add("Close tab", CommandCatalog.CloseTab, () => CloseTab(index)).Enabled = _tabs.Count > 1;
            Add("Close other tabs", null, () => CloseOtherTabs(index)).Enabled = _tabs.Count > 1;
            Add("Move to the other pane", CommandCatalog.MoveTabToOther, () =>
            {
                SelectTab(index);
                MoveActiveTabToOther();
            }).Enabled = _tabs.Count > 1;
            _tabMenu.Items.Add(new ToolStripSeparator());
        }
        Add("New tab", CommandCatalog.NewTab, () =>
        {
            NewTab();
            FocusList();
        });
        Add("Reopen the closed tab", CommandCatalog.ReopenTab, ReopenClosedTab).Enabled = _tabs.HasClosed;
        _tabMenu.Show(_tabStrip, location);

        ToolStripMenuItem Add(string text, string? command, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = command is null ? "" : Shortcut(command) ?? "" };
            item.Click += (_, _) => action();
            _tabMenu.Items.Add(item);
            return item;
        }
    }

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    /// <summary>Navigation load: clears the list and streams the new folder in as it's enumerated.</summary>
    /// <param name="countVisit">Raise <see cref="Visited"/> once it has listed without error.</param>
    /// <param name="restore">A tab being shown again: its saved selection and scroll position apply once listed.</param>
    private void LoadLocation(string location, string? selectName, bool countVisit = false, TabState? restore = null)
    {
        StopWatcher();
        bool changed = !PathUtil.SameLocation(_location, location);
        _location = location;
        _pendingVisit = countVisit && !PathUtil.IsComputerRoot(location);
        _restoring = restore;
        _all = [];
        _filter = "";
        _error = null;
        _loading = true;
        _autoSelect = true;
        _autoTargetName = restore?.Focus ?? selectName;
        _lastAutoIndex = -1;
        CancelReload();

        if (!_pathBox.Focused)
            _pathBox.Text = PathUtil.ToDisplay(location);
        UpdateNavButtons();
        UpdateTabStrip();
        RebuildView(preserveScroll: false);
        StartLoader();

        if (changed)
            CurrentPathChanged?.Invoke(this, EventArgs.Empty);
        TabsChanged?.Invoke(this);
    }

    /// <summary>The restoring tab's saved selection, once, if the user hasn't started moving around.</summary>
    private SelectionSnapshot? TakeRestoreSelection()
    {
        var tab = _restoring;
        _restoring = null;
        if (tab is null || !_autoSelect || (tab.Selected.Count == 0 && !tab.SelectedAll && tab.Top is null))
            return null;
        var snapshot = new SelectionSnapshot { All = tab.SelectedAll, Focus = tab.Focus, Top = tab.Top };
        snapshot.Names.UnionWith(tab.Selected);
        return snapshot;
    }

    /// <summary>Reload: re-lists the current folder in the background and swaps it in when complete.</summary>
    public void Reload() => Reload(null, null);

    private void Reload(SelectionSnapshot? selection, Action? after)
    {
        if (_editing)
        {
            // Swapping rows under an open edit box would cancel the rename; do it afterwards.
            _reloadAfterEdit = true;
            return;
        }

        // If a reload is already running (say, the watcher fires while the reload that selects freshly
        // copied items is still listing), restart it but keep the pending selection and follow-up.
        bool wasReloading = _isReload;
        _isReload = true;
        _reloadBuffer = [];
        _reloadSelection = selection ?? (wasReloading ? _reloadSelection : null);
        _afterReload = after ?? (wasReloading ? _afterReload : null);
        StartLoader();
    }

    private void CancelReload()
    {
        _isReload = false;
        _reloadBuffer = null;
        _reloadSelection = null;
        _afterReload = null;
    }

    private void StartLoader()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        int generation = ++_generation;

        DirectoryLoader.Start(_location, BatchIntervalMs, token, (batch, completed, error) =>
        {
            if (token.IsCancellationRequested)
                return;
            try
            {
                BeginInvoke(() => OnBatch(generation, batch, completed, error));
            }
            catch (InvalidOperationException)
            {
                // Handle destroyed (app closing) between the check and the post.
            }
        });
    }

    private void OnBatch(int generation, List<FileEntry> batch, bool completed, Exception? error)
    {
        // A batch from a superseded load. Cancellation stops the loader, but a batch may already
        // be queued on the message loop when that happens.
        if (generation != _generation)
            return;

        if (_isReload)
        {
            OnReloadBatch(batch, completed, error);
            return;
        }

        if (batch.Count > 0)
        {
            batch.Sort(_comparer);
            _all = EntryComparer.Merge(_all, batch, _comparer);
        }
        if (completed)
            _loading = false;

        if (error is not null)
        {
            _pendingVisit = false; // a folder that couldn't be listed (fully) isn't somewhere to come back to
            _restoring = null;
            var message = DescribeLoadError(error, _location);
            if (_all.Count == 0 && _history.RevertFailed() is { } previous)
            {
                // Nothing to show here, so put the user back where they were and explain why.
                LoadLocation(previous.Location, previous.SelectedName);
                ShowError(message);
                return;
            }
            if (_all.Count == 0 && error is DirectoryNotFoundException)
            {
                // Nowhere to go back to, as with a restored tab whose folder has since gone.
                LeaveVanishedFolder();
                return;
            }
            _error = _all.Count == 0 ? message : $"Listing incomplete. {message}";
        }
        else if (completed)
        {
            StartWatcher();
            RaiseVisit();
            if (TakeRestoreSelection() is { } restored)
            {
                RebuildView(preserveScroll: true, restored);
                return;
            }
        }

        RebuildView(preserveScroll: true);
    }

    private void RaiseVisit()
    {
        if (!_pendingVisit)
            return;
        _pendingVisit = false;
        Visited?.Invoke(_location);
    }

    private void OnReloadBatch(List<FileEntry> batch, bool completed, Exception? error)
    {
        if (batch.Count > 0)
        {
            batch.Sort(_comparer);
            _reloadBuffer = EntryComparer.Merge(_reloadBuffer ?? [], batch, _comparer);
        }
        if (!completed)
            return;

        var buffer = _reloadBuffer ?? [];
        // Always consume a pending tab restore here, even when this reload brings its own selection.
        var restored = TakeRestoreSelection();
        var selection = _reloadSelection ?? (error is null ? restored : null);
        var after = _afterReload;
        CancelReload();

        if (error is null)
            RaiseVisit(); // this reload superseded a navigation load before it finished
        else
            _pendingVisit = false;

        if (error is not null && buffer.Count == 0)
        {
            if (error is DirectoryNotFoundException)
                LeaveVanishedFolder();
            else
                ShowError(DescribeLoadError(error, _location));
            return;
        }

        _all = buffer;
        _loading = false;
        RebuildView(preserveScroll: selection is null, selection);
        if (_watcher is null && error is null)
            StartWatcher(); // this reload may have superseded a navigation load before it could start one
        after?.Invoke();
    }

    /// <summary>The folder on screen was deleted or renamed elsewhere: go to its nearest surviving ancestor.</summary>
    private void LeaveVanishedFolder()
    {
        var missing = _location;
        var ancestor = PathUtil.GetParent(missing);
        while (ancestor is not null && !PathUtil.IsComputerRoot(ancestor) && !Directory.Exists(ancestor))
            ancestor = PathUtil.GetParent(ancestor);
        ancestor ??= PathUtil.ComputerRoot;

        // Off the UI thread would be kinder to network paths, but this is rare: the folder is gone.
        _history.Push(ancestor);
        LoadLocation(ancestor, null);
        ShowError($"Folder no longer exists: {missing}");
    }

    private static string DescribeLoadError(Exception error, string location) => error switch
    {
        UnauthorizedAccessException => $"Access denied: {location}",
        DirectoryNotFoundException => $"Folder not found: {location}",
        _ => $"{location}: {error.Message}",
    };

    // ------------------------------------------------------------------
    // Watching for external changes
    // ------------------------------------------------------------------

    private void StartWatcher()
    {
        StopWatcher();
        if (PathUtil.IsComputerRoot(_location))
            return;

        try
        {
            var watcher = new FileSystemWatcher(_location)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes
                    | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, _) => OnWatcherEvent();
            watcher.Deleted += (_, _) => OnWatcherEvent();
            watcher.Changed += (_, _) => OnWatcherEvent();
            watcher.Renamed += (_, _) => OnWatcherEvent();
            watcher.Error += (_, _) => OnWatcherEvent(); // buffer overflow etc.: a full reload covers it
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some locations (certain network shares) can't be watched; Ctrl+R still works.
        }
    }

    private void StopWatcher()
    {
        _watchTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnWatcherEvent()
    {
        // Thread-pool thread.
        try
        {
            BeginInvoke(ScheduleWatchReload);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Debounce: reload after a quiet period, but never later than WatchMaxDelayMs after the first change.</summary>
    private void ScheduleWatchReload()
    {
        if (_watcher is null)
            return;
        long now = Environment.TickCount64;
        if (!_watchTimer.Enabled)
            _firstWatchEventTick = now;
        _watchTimer.Stop();
        _watchTimer.Interval = now - _firstWatchEventTick >= WatchMaxDelayMs ? 1 : WatchQuietMs;
        _watchTimer.Start();
    }

    // ------------------------------------------------------------------
    // View: filter, sort, selection preservation
    // ------------------------------------------------------------------

    /// <summary>What the user had selected, by name, so it survives reloads that create new entry objects.</summary>
    private sealed class SelectionSnapshot
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool All { get; set; }
        public string? Focus { get; set; }
        public int FocusIndex { get; set; } = -1;
        public string? Top { get; set; }
    }

    private SelectionSnapshot CaptureSelection()
    {
        var snapshot = new SelectionSnapshot();
        if (!_list.IsHandleCreated)
            return snapshot;

        snapshot.FocusIndex = _list.FocusedIndex;
        snapshot.Focus = EntryAt(_view, snapshot.FocusIndex)?.Name;

        int selectedCount = _list.SelectedIndices.Count;
        if (selectedCount > 1 && selectedCount == _view.Count)
        {
            snapshot.All = true;
        }
        else if (selectedCount > 0)
        {
            foreach (int i in _list.SelectedIndices)
            {
                if (EntryAt(_view, i) is { } entry)
                    snapshot.Names.Add(entry.Name);
            }
        }

        int top = _list.TopIndex;
        if (top > 0)
            snapshot.Top = EntryAt(_view, top)?.Name;
        return snapshot;
    }

    private List<FileEntry> ComputeView()
    {
        if (_filter.Length == 0 && _showHidden)
            return _all;

        var result = new List<FileEntry>();
        foreach (var entry in _all)
        {
            if (!_showHidden && entry.IsHidden)
                continue;
            if (_filter.Length > 0 && !entry.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// Recomputes the visible rows and swaps them into the list, keeping the selection (by name)
    /// or, in auto mode, re-applying the auto target. <paramref name="explicitSelection"/>
    /// replaces the user's selection, e.g. to select freshly copied items.
    /// </summary>
    private void RebuildView(bool preserveScroll, SelectionSnapshot? explicitSelection = null)
    {
        SelectionSnapshot? snapshot = null;
        if (explicitSelection is not null)
        {
            _autoSelect = false;
            snapshot = explicitSelection;
        }
        else if (!_autoSelect)
        {
            snapshot = CaptureSelection();
        }

        // Clear before swapping and shrinking, so the native control never holds selection
        // state for indices that no longer exist.
        _list.ClearSelection();
        _view = ComputeView();

        int ensureVisible = -1;
        _list.BeginUpdate();
        try
        {
            _list.VirtualListSize = _view.Count;
            if (_autoSelect)
                ensureVisible = ApplyAutoSelection();
            else if (snapshot is not null)
                ensureVisible = ApplySnapshot(snapshot, preserveScroll);
        }
        finally
        {
            _list.EndUpdate();
        }

        if (ensureVisible >= 0)
            _list.EnsureVisible(ensureVisible);
        UpdateStatus();
    }

    /// <summary>Selects the auto target, or the first row. Returns an index to scroll to, or -1.</summary>
    private int ApplyAutoSelection()
    {
        int index = _autoTargetName is null ? -1 : IndexOfName(_autoTargetName);
        if (index < 0 && _view.Count > 0)
            index = 0;
        if (index < 0)
        {
            _lastAutoIndex = -1;
            return -1;
        }

        _list.SelectOnly(index);
        // Only scroll when the target row actually moved, so someone reading the list with the
        // mouse wheel while a big folder loads isn't yanked back to the top on every batch.
        bool moved = index != _lastAutoIndex;
        _lastAutoIndex = index;
        return moved ? index : -1;
    }

    private int ApplySnapshot(SelectionSnapshot snapshot, bool preserveScroll)
    {
        if (_view.Count == 0)
            return -1;

        if (snapshot.All)
        {
            _list.SelectAllItems();
        }
        else if (snapshot.Names.Count > 0)
        {
            for (int i = 0; i < _view.Count; i++)
            {
                if (snapshot.Names.Contains(_view[i].Name))
                    _list.SetSelected(i);
            }
        }

        bool scrollToFocus = !preserveScroll;
        int focus = snapshot.Focus is null ? -1 : IndexOfName(snapshot.Focus);
        if (focus < 0 && snapshot.FocusIndex >= 0 && _list.SelectedIndices.Count == 0)
        {
            // The focused item is gone (deleted, moved away): land on its neighbour, like Explorer.
            focus = Math.Min(snapshot.FocusIndex, _view.Count - 1);
            _list.SetSelected(focus);
            scrollToFocus = true;
        }
        if (focus >= 0)
            _list.SetFocused(focus);

        if (preserveScroll && snapshot.Top is not null && IndexOfName(snapshot.Top) is var top and >= 0)
        {
            try
            {
                _list.TopItem = _list.Items[top];
            }
            catch (ArgumentException)
            {
            }
        }

        return scrollToFocus ? focus : -1;
    }

    private void SetFilter(string filter)
    {
        if (filter == _filter)
            return;

        // Narrowing jumps to the first match (so type + Enter works); clearing stays on the
        // item you'd found.
        _autoTargetName = filter.Length == 0 ? FocusedEntry()?.Name : null;
        _filter = filter;
        _autoSelect = true;
        _lastAutoIndex = -1;
        RebuildView(preserveScroll: false);
    }

    public void ToggleHidden()
    {
        _showHidden = !_showHidden;
        RebuildView(preserveScroll: false);
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var column = (SortColumn)e.Column;
        bool descending = _comparer.Column == column && !_comparer.Descending;
        _comparer = new EntryComparer(column, descending);

        // Sort copies: the old list is still the current view until RebuildView captures selection.
        _all = Sorted(_all);
        if (_reloadBuffer is not null)
            _reloadBuffer = Sorted(_reloadBuffer);

        UpdateColumnHeaders();
        RebuildView(preserveScroll: false);

        List<FileEntry> Sorted(List<FileEntry> source)
        {
            var copy = new List<FileEntry>(source);
            copy.Sort(_comparer);
            return copy;
        }
    }

    private void UpdateColumnHeaders()
    {
        for (int i = 0; i < _list.Columns.Count; i++)
        {
            var arrow = (int)_comparer.Column == i ? (_comparer.Descending ? "  ▾" : "  ▴") : "";
            _list.Columns[i].Text = ColumnNames[i] + arrow;
        }
    }

    // ------------------------------------------------------------------
    // List events
    // ------------------------------------------------------------------

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if ((uint)e.ItemIndex >= (uint)_view.Count)
        {
            // Should not happen, but the control requires an item for every index it asks for.
            e.Item = new ListViewItem(["", "", "", ""]);
            return;
        }

        var entry = _view[e.ItemIndex];
        var item = new ListViewItem(entry.DisplayName, _icons.GetIconIndex(entry, PathUtil.Combine(_location, entry)));
        item.SubItems.Add(Format.Date(entry));
        item.SubItems.Add(entry.TypeName);
        item.SubItems.Add(Format.Size(entry));
        if (entry.IsHidden)
            item.ForeColor = Theme.DimText;
        e.Item = item;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        // Only the fixed keys that belong to the list itself. Everything rebindable (F2, F5, Del...)
        // is a command, dispatched by the main window from the keymap before it gets here.
        switch (e.KeyData)
        {
            case Keys.Enter:
                OpenSelection();
                break;

            case Keys.Back:
                // With a filter active, Backspace edits it. Otherwise it goes up, like classic Explorer.
                if (_filter.Length > 0)
                    SetFilter(_filter[..^1]);
                else
                    GoUp();
                break;

            case Keys.Control | Keys.Back:
                SetFilter("");
                break;

            case Keys.Escape:
                if (_filter.Length == 0)
                    return;
                SetFilter("");
                break;

            default:
                if (e.KeyCode is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu))
                    _autoSelect = false; // the user is driving the selection now
                return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void OnListKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (char.IsControl(e.KeyChar))
            return;

        // Plain Ctrl or Alt chords are shortcuts. Ctrl+Alt together is AltGr, which types
        // characters such as @ or \ on many non-US layouts.
        var modifiers = ModifierKeys & (Keys.Control | Keys.Alt);
        if (modifiers is Keys.Control or Keys.Alt)
            return;

        // Handling the char here also suppresses the ListView's built-in incremental search,
        // which would otherwise fight the filter.
        e.Handled = true;
        SetFilter(_filter + e.KeyChar);
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        switch (e.Button)
        {
            case MouseButtons.XButton1:
                GoBack();
                break;
            case MouseButtons.XButton2:
                GoForward();
                break;
            case MouseButtons.Middle:
                // Middle-click a folder: open it in a new tab, as in a browser.
                if (_list.HitTest(e.Location).Item is { } hit && EntryAt(_view, hit.Index) is { IsContainer: true } entry)
                    NewTab(PathUtil.Combine(_location, entry));
                break;
            default:
                _autoSelect = false;
                break;
        }
    }

    private void OnListMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _list.HitTest(e.Location).Item is not null)
            OpenSelection();
    }

    private void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Enter:
                if (Navigate(_pathBox.Text))
                    FocusList();
                else
                    _pathBox.SelectAll();
                break;
            case Keys.Escape:
                _pathBox.Text = PathUtil.ToDisplay(_location);
                FocusList();
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true; // no ding
    }

    // ------------------------------------------------------------------
    // Context menu
    // ------------------------------------------------------------------

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var commandItems = new List<(ToolStripMenuItem Item, string Command)>();
        var open = Add("Open", null, OpenSelection);
        open.ShortcutKeyDisplayString = "Enter";
        menu.Items.Add(new ToolStripSeparator());
        var copy = Add("Copy to other pane", CommandCatalog.CopyToOther, () => RequestTransfer(TransferKind.Copy));
        var move = Add("Move to other pane", CommandCatalog.MoveToOther, () => RequestTransfer(TransferKind.Move));
        menu.Items.Add(new ToolStripSeparator());
        var rename = Add("Rename", CommandCatalog.Rename, BeginRename);
        var delete = Add("Delete", CommandCatalog.Delete, () => DeleteSelection(permanent: false));
        menu.Items.Add(new ToolStripSeparator());
        var newFolder = Add("New folder", CommandCatalog.NewFolder, CreateFolder);
        Add("Reveal in Explorer", CommandCatalog.Reveal, RevealInExplorer);
        menu.Items.Add(new ToolStripSeparator());
        var more = Add("Show more options", null, () =>
        {
            // Runs as the strip closes; show the Windows menu once it's gone, where the strip was.
            var point = _menuPoint;
            bool background = _menuOnBackground;
            BeginInvoke(() => ShowNativeMenu(point, background, extended: (ModifierKeys & Keys.Shift) != 0));
        });
        more.ShortcutKeyDisplayString = "Shift+Right-click";

        menu.Opened += (_, _) => _menuPoint = menu.Bounds.Location;
        menu.Opening += (_, e) =>
        {
            // Shortcuts come from the keymap, which the user can change at any time.
            foreach (var (item, command) in commandItems)
                item.ShortcutKeyDisplayString = Shortcut(command) ?? "";

            bool fromMouse = _list.ContextMenuFromMouse;
            _menuOnBackground = fromMouse && _list.HitTest(_list.PointToClient(Cursor.Position)).Item is null;

            // Shift+right-click goes straight to the Windows menu, with its extended verbs ("Copy as
            // path", "Open PowerShell window here"), as Shift does in Explorer. Shift+F10 is the
            // keyboard's plain context-menu key, so it still opens this menu.
            if (fromMouse && (ModifierKeys & Keys.Shift) != 0)
            {
                e.Cancel = true;
                var point = Cursor.Position;
                bool background = _menuOnBackground;
                BeginInvoke(() => ShowNativeMenu(point, background, extended: true));
                return;
            }

            bool inFolder = !PathUtil.IsComputerRoot(_location);
            int targets = CommandTargets().Count;
            open.Enabled = FocusedEntry() is not null;
            copy.Enabled = move.Enabled = delete.Enabled = inFolder && targets > 0;
            rename.Enabled = inFolder && FocusedEntry() is { Kind: not EntryKind.Drive };
            newFolder.Enabled = inFolder;
        };
        return menu;

        ToolStripMenuItem Add(string text, string? command, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            if (command is not null)
                commandItems.Add((item, command));
            return item;
        }
    }

    // ------------------------------------------------------------------
    // The Windows (shell) context menu
    // ------------------------------------------------------------------

    /// <summary>
    /// Shows Explorer's own context menu for the selection, or for the folder itself when
    /// <paramref name="background"/> is set or nothing is selected.
    /// </summary>
    private void ShowNativeMenu(Point screen, bool background, bool extended)
    {
        if (IsDisposed || _editing || _nativeMenu is not null)
            return;

        var paths = background ? [] : NativeMenuTargets();
        var directory = PathUtil.IsComputerRoot(_location) ? null : _location;
        var flags = CMF.CMF_NORMAL;
        if (paths.Count > 0)
            flags |= CMF.CMF_ITEMMENU | CMF.CMF_CANRENAME; // "Rename" is offered; we do it in place
        if (extended)
            flags |= CMF.CMF_EXTENDEDVERBS;

        try
        {
            using var host = paths.Count > 0
                ? ShellContextMenuHost.ForPaths(paths, Handle)
                : ShellContextMenuHost.ForBackground(_location, Handle);
            _nativeMenu = host;
            try
            {
                var hr = host.ShowAndInvoke(Handle, screen, flags, directory, verb => HandleNativeVerb(verb, paths));
                if (hr.Failed && hr != unchecked((int)0x800704C7)) // ERROR_CANCELLED: the user backed out
                    ShowError($"That command failed ({hr}).");
            }
            finally
            {
                _nativeMenu = null;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                   or ArgumentException or FileNotFoundException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't show the Windows menu: {ex.Message}");
        }
    }

    /// <summary>Paths for the Windows menu: like <see cref="CommandTargets"/>, but drives count too.</summary>
    private List<string> NativeMenuTargets()
    {
        var targets = SelectedEntries();
        if (targets.Count == 0 && FocusedEntry() is { } focused)
            targets.Add(focused);
        return targets.Select(e => PathUtil.Combine(_location, e)).ToList();
    }

    /// <summary>
    /// Some shell commands are better done by Escapade: rename needs an Explorer view to call back
    /// into (from here it would do nothing), delete should land on Escapade's undo history, and
    /// opening a folder should browse into it rather than open an Explorer window.
    /// </summary>
    private bool HandleNativeVerb(string? verb, List<string> paths)
    {
        bool inFolder = !PathUtil.IsComputerRoot(_location);
        switch (verb?.ToLowerInvariant())
        {
            case "rename" when inFolder:
                BeginInvoke(BeginRename);
                return true;
            case "delete" when inFolder:
                bool permanent = (ModifierKeys & Keys.Shift) != 0;
                BeginInvoke(() => DeleteSelection(permanent));
                return true;
            case "open" or "explore" when paths.Count == 1 && Directory.Exists(paths[0]):
                var target = paths[0];
                BeginInvoke(() => Navigate(target));
                return true;
            default:
                return false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (_nativeMenu?.HandleMenuMessage(ref m) == true)
            return;
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    /// <summary>Full paths of what a command should act on: the selection, or the focused item.</summary>
    public List<string> GetCommandPaths() =>
        CommandTargets().Select(e => PathUtil.Combine(_location, e)).ToList();

    /// <summary>F5 / F6: hands the selection to the main window, which knows the other pane.</summary>
    public void RequestTransfer(TransferKind kind) => TransferRequested?.Invoke(this, kind);

    public void SelectAll()
    {
        _autoSelect = false;
        _list.SelectAllItems();
    }

    /// <summary>The Windows menu from the keyboard: for the selection, shown under the focused row.</summary>
    public void ShowWindowsMenu()
    {
        bool background = NativeMenuTargets().Count == 0;
        var point = new Point(0, 0);
        if (!background && _list.FocusedIndex is var index and >= 0 && index < _view.Count)
        {
            var row = _list.GetItemRect(index, ItemBoundsPortion.Label);
            point = new Point(row.Left, row.Bottom);
        }
        ShowNativeMenu(_list.PointToScreen(point), background, extended: false);
    }

    private List<FileEntry> CommandTargets()
    {
        if (PathUtil.IsComputerRoot(_location))
            return [];
        var targets = SelectedEntries();
        if (targets.Count == 0 && FocusedEntry() is { } focused)
            targets.Add(focused);
        targets.RemoveAll(e => e.Kind == EntryKind.Drive);
        return targets;
    }

    private void OpenSelection()
    {
        var selected = SelectedEntries();
        var focused = FocusedEntry();
        var primary = focused is not null && (selected.Count == 0 || selected.Contains(focused))
            ? focused
            : selected.FirstOrDefault();
        if (primary is null)
            return;

        if (primary.IsContainer)
        {
            NavigateInto(primary);
            return;
        }

        var files = selected.Where(e => !e.IsContainer).ToList();
        if (files.Count == 0)
            files.Add(primary);

        if (files.Count > OpenManyThreshold &&
            MessageBox.Show(this, $"Open {files.Count} files?", "Escapade",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        foreach (var file in files)
        {
            if (ShellLauncher.Open(PathUtil.Combine(_location, file), this) is { } error)
            {
                ShowError(error);
                break;
            }
        }
    }

    public void BeginRename()
    {
        if (PathUtil.IsComputerRoot(_location))
            return;
        int index = _list.FocusedIndex;
        if (EntryAt(_view, index) is not { Kind: not EntryKind.Drive })
            return;

        _autoSelect = false;
        _list.SelectOnly(index);
        _list.EnsureVisible(index);
        _renameArmed = true;
        _list.Items[index].BeginEdit();
    }

    private void OnBeforeLabelEdit(object? sender, LabelEditEventArgs e)
    {
        // LabelEdit is on, so the ListView would also start editing on a slow second click.
        // Only edits started by BeginRename are allowed.
        if (!_renameArmed || EntryAt(_view, e.Item) is not { } entry)
        {
            e.CancelEdit = true;
            return;
        }
        _renameArmed = false;
        _editing = true;

        // Like Explorer, preselect the name without its extension (".gitignore" selects all).
        int length = entry.Kind == EntryKind.File && Path.GetFileNameWithoutExtension(entry.Name) is { Length: > 0 } stem
            ? stem.Length
            : entry.Name.Length;
        BeginInvoke(() =>
        {
            var edit = SendMessage(_list.Handle, LVM_GETEDITCONTROL, 0, 0);
            if (edit != 0)
                SendMessage(edit, EM_SETSEL, 0, length);
        });
    }

    private void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        _editing = false;
        // Always cancel: the row text comes from our data, which reloads once the rename lands.
        e.CancelEdit = true;

        if (EntryAt(_view, e.Item) is { } entry && e.Label?.Trim() is { Length: > 0 } newName && newName != entry.Name)
            Rename(entry, newName);

        if (_reloadAfterEdit)
        {
            _reloadAfterEdit = false;
            Reload();
        }
    }

    private async void Rename(FileEntry entry, string newName)
    {
        if (!PathUtil.IsValidFileName(newName))
        {
            ShowError("A name can't contain any of these characters: \\ / : * ? \" < > |");
            return;
        }

        var folder = _location;
        var action = FileAction.Rename(PathUtil.Combine(folder, entry), newName);
        ClearError();
        var outcome = await FileActionRunner.DoAsync(action, OwnerHandle());
        if (IsDisposed)
            return;
        Finish(action, outcome, "Rename");
        var renamed = NamesIn(outcome.Select, folder);
        NotifyChanged(folder, renamed.Count > 0 ? renamed : [newName]);
    }

    public async void DeleteSelection(bool permanent)
    {
        var paths = GetCommandPaths();
        if (paths.Count == 0)
            return;

        var folder = _location;
        ClearError();
        if (permanent)
        {
            // Not undoable, so not recorded. No flags at all: on a delete, FOF_ALLOWUNDO itself means
            // "recycle". With no flags the shell asks for confirmation itself.
            var result = await FileOperationRunner.RunAsync(OwnerHandle(), 0,
                b => b.Operations.QueueDeleteOperation(paths.Select(b.Item).ToList()));
            if (IsDisposed)
                return;
            ReportResult(result, "Delete");
            NotifyChanged(folder);
            return;
        }

        var action = FileAction.Recycle(paths);
        var outcome = await FileActionRunner.DoAsync(action, OwnerHandle());
        if (IsDisposed)
            return;
        Finish(action, outcome, "Delete");
        NotifyChanged(folder);
    }

    public async void CreateFolder()
    {
        if (PathUtil.IsComputerRoot(_location))
            return;

        var folder = _location;
        var action = FileAction.NewFolder(folder, "New folder");
        ClearError();
        var outcome = await FileActionRunner.DoAsync(action, OwnerHandle());
        if (IsDisposed)
            return;
        Finish(action, outcome, "New folder");
        if (outcome.Failed || NamesIn(outcome.Select, folder) is not [.., var created])
            return;

        NotifyChanged(folder, [created], after: () =>
        {
            // Straight into naming it, as in Explorer. Only if the user is still looking at it.
            if (PathUtil.SameLocation(folder, _location) && FocusedEntry()?.Name == created)
                BeginRename();
        });
    }

    public void RevealInExplorer()
    {
        try
        {
            string arguments;
            if (PathUtil.IsComputerRoot(_location))
                arguments = "shell:MyComputerFolder";
            else if (FocusedEntry() is { Kind: not EntryKind.Drive } entry)
                arguments = $"/select,\"{PathUtil.Combine(_location, entry)}\"";
            else
                arguments = $"\"{_location}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError($"Couldn't open Explorer: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private nint OwnerHandle() => (FindForm() ?? (Control)this).Handle;

    /// <summary>Names of those <paramref name="paths"/> that sit directly inside <paramref name="folder"/>.</summary>
    public static List<string> NamesIn(IEnumerable<string> paths, string folder) =>
        paths.Where(p => PathUtil.SameLocation(Path.GetDirectoryName(p), folder))
            .Select(p => Path.GetFileName(p))
            .Where(n => n.Length > 0)
            .ToList();

    /// <summary>Reports a finished operation and, if it changed anything, offers it for undo.</summary>
    public void Finish(FileAction action, ActionOutcome outcome, string operation)
    {
        if (outcome.Error is not null)
            ShowError($"{operation} failed: {outcome.Error}");
        if (outcome.DidSomething)
            ActionRecorded?.Invoke(action);
    }

    public void ReportResult(FileOpResult result, string operation)
    {
        if (result.Error is not null)
            ShowError($"{operation} failed: {result.Error}");
    }

    private static FileEntry? EntryAt(List<FileEntry> list, int index) =>
        (uint)index < (uint)list.Count ? list[index] : null;

    private int IndexOfName(string name) =>
        _view.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private FileEntry? FocusedEntry() =>
        EntryAt(_view, _list.FocusedIndex) ?? SelectedEntries().FirstOrDefault();

    private List<FileEntry> SelectedEntries()
    {
        var result = new List<FileEntry>();
        if (!_list.IsHandleCreated)
            return result;
        foreach (int i in _list.SelectedIndices)
        {
            if (EntryAt(_view, i) is { } entry)
                result.Add(entry);
        }
        return result;
    }

    public void ShowError(string message)
    {
        _error = message;
        _messageIsInfo = false;
        UpdateStatus();
    }

    /// <summary>A notice in the status bar ("Undid: ..."), in normal text rather than error red.</summary>
    public void ShowInfo(string message)
    {
        _error = message;
        _messageIsInfo = true;
        UpdateStatus();
    }

    private void ClearError()
    {
        if (_error is null)
            return;
        _error = null;
        UpdateStatus();
    }

    private void QueueStatusUpdate()
    {
        // Selection changes arrive one notification per item; coalesce them into one repaint.
        if (_statusUpdateQueued || !IsHandleCreated)
            return;
        _statusUpdateQueued = true;
        BeginInvoke(() =>
        {
            _statusUpdateQueued = false;
            UpdateStatus();
        });
    }

    private void UpdateStatus()
    {
        _filterLabel.Visible = _filter.Length > 0;
        _filterLabel.Text = $"Filter: {_filter}";

        if (_error is not null)
        {
            _statusLabel.ForeColor = _messageIsInfo ? Theme.ChromeText : Theme.ErrorText;
            _statusLabel.Text = _error;
            return;
        }

        _statusLabel.ForeColor = Theme.ChromeText;
        bool narrowed = _view.Count != _all.Count;
        var text = narrowed
            ? $"{_view.Count:N0} of {_all.Count:N0} items"
            : $"{_all.Count:N0} items";
        if (_loading)
            text += "  (loading…)";
        if (_list.IsHandleCreated && _list.SelectedIndices.Count is > 1 and var count)
            text += $"  ·  {count:N0} selected";
        if (!_showHidden)
            text += "  ·  hidden items not shown (Ctrl+H)";
        if (_filter.Length > 0)
            text += "  ·  Esc clears filter";
        _statusLabel.Text = text;
    }

    private void UpdateNavButtons()
    {
        _backButton.Enabled = _history.CanGoBack;
        _forwardButton.Enabled = _history.CanGoForward;
        _upButton.Enabled = PathUtil.GetParent(_location) is not null;
    }

    /// <summary>The active pane gets an accent frame and a thicker accent edge along the top.</summary>
    private void UpdateActiveStrip()
    {
        BackColor = _isActive ? Theme.ActiveFrame : Theme.Frame;
        _activeStrip.BackColor = _isActive ? Theme.ActiveFrame : Theme.TabRow;
    }

    private static Panel Divider() =>
        new() { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Divider };

    private Button MakeNavButton(string text, string tip, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(30, 25),
            Margin = new Padding(1, 2, 1, 2),
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
        };
        button.FlatAppearance.BorderSize = 0;
        if (Theme.IsDark)
        {
            button.ForeColor = Theme.ChromeText;
            button.FlatAppearance.MouseOverBackColor = Theme.Hover;
            button.FlatAppearance.MouseDownBackColor = Theme.SelectionBack;
        }
        button.Click += onClick;
        button.Click += (_, _) => FocusList();
        _toolTip.SetToolTip(button, tip);
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            StopWatcher();
            _watchTimer.Dispose();
            _menu.Dispose();
            _tabMenu.Dispose();
            _icons.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
