using Escapade.Core;

namespace Escapade.UI;

/// <summary>
/// One self-contained file pane: path bar, virtual list, filter, history and background loading.
/// Phase 0.2 adds a second instance for dual-pane.
/// </summary>
internal sealed class FilePane : UserControl
{
    private const int BatchIntervalMs = 50;
    private const int OpenManyThreshold = 10;

    private static readonly string[] ColumnNames = ["Name", "Date modified", "Type", "Size"];
    private static readonly Color FilterBackColor = Color.FromArgb(255, 241, 178);
    private static readonly Color ErrorColor = Color.Firebrick;

    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly Button _upButton;
    private readonly TextBox _pathBox;
    private readonly FileListView _list;
    private readonly Label _filterLabel;
    private readonly Label _statusLabel;
    private readonly ToolTip _toolTip = new();
    private readonly IconCache _icons;
    private readonly NavigationHistory _history = new();

    private string _location = PathUtil.ComputerRoot;
    private List<FileEntry> _all = [];   // everything loaded so far, sorted; never mutated in place
    private List<FileEntry> _view = [];  // _all after filtering; may be the same instance
    private EntryComparer _comparer = EntryComparer.Default;
    private string _filter = "";
    private bool _showHidden = true;

    // Until the user moves the selection themselves, the pane keeps choosing it: the first item,
    // the folder we just came up out of, or the first filter match, even as batches reorder rows.
    private bool _autoSelect;
    private FileEntry? _autoTarget;
    private string? _pendingSelectName;
    private int _lastAutoIndex = -1;

    private CancellationTokenSource? _loadCts;
    private int _generation;
    private bool _loading;
    private string? _error;
    private bool _statusUpdateQueued;

    public FilePane()
    {
        SuspendLayout();

        _icons = new IconCache(SystemInformation.SmallIconSize);

        _backButton = MakeNavButton("←", "Back (Alt+Left)", (_, _) => GoBack());
        _forwardButton = MakeNavButton("→", "Forward (Alt+Right)", (_, _) => GoForward());
        _upButton = MakeNavButton("↑", "Up (Backspace, Alt+Up)", (_, _) => GoUp());

        _pathBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 3, 3, 3),
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
            Padding = new Padding(2, 2, 2, 0),
        };
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathBar.Controls.Add(_backButton, 0, 0);
        pathBar.Controls.Add(_forwardButton, 1, 0);
        pathBar.Controls.Add(_upButton, 2, 0);
        pathBar.Controls.Add(_pathBox, 3, 0);

        _list = new FileListView
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BorderStyle = BorderStyle.None,
            SmallImageList = _icons.ImageList,
        };
        _list.Columns.Add(ColumnNames[0], 340);
        _list.Columns.Add(ColumnNames[1], 140);
        _list.Columns.Add(ColumnNames[2], 120);
        _list.Columns.Add(ColumnNames[3], 90, HorizontalAlignment.Right);
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.KeyDown += OnListKeyDown;
        _list.KeyPress += OnListKeyPress;
        _list.MouseDown += OnListMouseDown;
        _list.MouseDoubleClick += OnListMouseDoubleClick;
        _list.ColumnClick += OnColumnClick;
        _list.SelectedIndexChanged += (_, _) => QueueStatusUpdate();
        _list.VirtualItemsSelectionRangeChanged += (_, _) => QueueStatusUpdate();

        _filterLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 0, 6, 0),
            Padding = new Padding(4, 2, 4, 2),
            BackColor = FilterBackColor,
            ForeColor = Color.Black,
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
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusBar.Controls.Add(_filterLabel, 0, 0);
        statusBar.Controls.Add(_statusLabel, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(pathBar, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(statusBar, 0, 2);

        Controls.Add(layout);
        UpdateColumnHeaders();
        UpdateNavButtons();
        ResumeLayout(false);
    }

    public event EventHandler? CurrentPathChanged;

    public string CurrentPath => _location;

    // ------------------------------------------------------------------
    // Navigation
    // ------------------------------------------------------------------

    /// <summary>
    /// Navigates to user-supplied text. A file path navigates to its folder and selects the file.
    /// Returns false (and shows why) if the path is invalid or doesn't exist.
    /// </summary>
    public bool Navigate(string input)
    {
        var path = PathUtil.Normalize(input, PathUtil.IsComputerRoot(_location) ? null : _location);
        if (path is null)
        {
            ShowError($"Not a valid path: {input}");
            return false;
        }

        string? select = null;
        if (!PathUtil.IsComputerRoot(path))
        {
            if (File.Exists(path))
            {
                select = Path.GetFileName(path);
                path = Path.GetDirectoryName(path)!;
            }
            else if (!Directory.Exists(path))
            {
                ShowError($"Folder not found: {path}");
                return false;
            }
        }

        if (_history.Current is { } current && SameLocation(current.Location, path))
        {
            // Re-entering the current folder acts as a refresh rather than a new history entry.
            LoadLocation(path, select ?? FocusedEntry()?.Name, keepFilter: select is null);
            return true;
        }

        RememberSelection();
        _history.Push(path);
        LoadLocation(path, select);
        return true;
    }

    public void GoBack()
    {
        RememberSelection();
        if (_history.GoBack() is { } entry)
            LoadLocation(entry.Location, entry.SelectedName);
    }

    public void GoForward()
    {
        RememberSelection();
        if (_history.GoForward() is { } entry)
            LoadLocation(entry.Location, entry.SelectedName);
    }

    public void GoUp()
    {
        if (PathUtil.GetParent(_location) is not { } parent)
            return;
        RememberSelection();
        var cameFrom = PathUtil.GetNameInParent(_location);
        _history.Push(parent);
        LoadLocation(parent, cameFrom);
    }

    public void RefreshListing() =>
        LoadLocation(_location, FocusedEntry()?.Name, keepFilter: true);

    public void FocusList() => _list.Focus();

    public void FocusPathBar()
    {
        _pathBox.Focus();
        _pathBox.SelectAll();
    }

    private void NavigateInto(FileEntry entry)
    {
        RememberSelection();
        var path = PathUtil.Combine(_location, entry);
        _history.Push(path);
        LoadLocation(path, null);
    }

    private void RememberSelection()
    {
        if (_history.Current is { } current)
            current.SelectedName = FocusedEntry()?.Name;
    }

    private static bool SameLocation(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    private void LoadLocation(string location, string? selectName, bool keepFilter = false)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        int generation = ++_generation;

        bool changed = !SameLocation(_location, location);
        _location = location;
        _all = [];
        if (!keepFilter)
            _filter = "";
        _error = null;
        _loading = true;
        _autoSelect = true;
        _autoTarget = null;
        _pendingSelectName = selectName;
        _lastAutoIndex = -1;

        if (!_pathBox.Focused)
            _pathBox.Text = PathUtil.ToDisplay(location);
        UpdateNavButtons();
        RebuildView(preserveScroll: false);

        DirectoryLoader.Start(location, BatchIntervalMs, token, (batch, completed, error) =>
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

        if (changed)
            CurrentPathChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBatch(int generation, List<FileEntry> batch, bool completed, Exception? error)
    {
        // A batch from a folder the user has already left. Cancellation stops the loader, but a
        // batch may already be queued on the message loop when that happens.
        if (generation != _generation)
            return;

        if (batch.Count > 0)
        {
            batch.Sort(_comparer);
            _all = EntryComparer.Merge(_all, batch, _comparer);
        }
        if (completed)
            _loading = false;

        if (error is not null)
        {
            var message = DescribeLoadError(error, _location);
            if (_all.Count == 0 && _history.RevertFailed() is { } previous)
            {
                // Nothing to show here, so put the user back where they were and explain why.
                LoadLocation(previous.Location, previous.SelectedName);
                ShowError(message);
                return;
            }
            _error = _all.Count == 0 ? message : $"Listing incomplete. {message}";
        }

        RebuildView(preserveScroll: true);
    }

    private static string DescribeLoadError(Exception error, string location) => error switch
    {
        UnauthorizedAccessException => $"Access denied: {location}",
        DirectoryNotFoundException => $"Folder not found: {location}",
        _ => $"{location}: {error.Message}",
    };

    // ------------------------------------------------------------------
    // View: filter, sort, selection preservation
    // ------------------------------------------------------------------

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
    /// Recomputes the visible rows and swaps them into the list. Selection is tracked by entry,
    /// not by index, because each merged batch shifts indices.
    /// </summary>
    private void RebuildView(bool preserveScroll)
    {
        var oldView = _view;

        // Capture what the user is looking at, by entry.
        FileEntry? focused = null;
        HashSet<FileEntry>? selected = null;
        bool allSelected = false;
        FileEntry? topEntry = null;
        if (!_autoSelect && _list.IsHandleCreated)
        {
            focused = EntryAt(oldView, _list.FocusedIndex);
            int selectedCount = _list.SelectedIndices.Count;
            if (selectedCount > 1 && selectedCount == oldView.Count)
            {
                allSelected = true;
            }
            else if (selectedCount > 0)
            {
                selected = new HashSet<FileEntry>(ReferenceEqualityComparer.Instance);
                foreach (int i in _list.SelectedIndices)
                {
                    if (EntryAt(oldView, i) is { } e)
                        selected.Add(e);
                }
            }
            if (preserveScroll && _list.TopIndex > 0)
                topEntry = EntryAt(oldView, _list.TopIndex);
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
            {
                ensureVisible = ApplyAutoSelection();
            }
            else
            {
                if (allSelected)
                {
                    _list.SelectAllItems();
                }
                else if (selected is not null)
                {
                    for (int i = 0; i < _view.Count; i++)
                    {
                        if (selected.Contains(_view[i]))
                            _list.SetSelected(i);
                    }
                }

                int focusIndex = focused is null ? -1 : _view.IndexOf(focused);
                if (focusIndex >= 0)
                {
                    _list.SetFocused(focusIndex);
                    if (!preserveScroll)
                        ensureVisible = focusIndex;
                }

                if (topEntry is not null && _view.IndexOf(topEntry) is var top and >= 0)
                {
                    try
                    {
                        _list.TopItem = _list.Items[top];
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        if (ensureVisible >= 0)
            _list.EnsureVisible(ensureVisible);
        UpdateStatus();
    }

    /// <summary>Selects the auto target (or the first row). Returns an index to scroll to, or -1.</summary>
    private int ApplyAutoSelection()
    {
        if (_pendingSelectName is not null)
        {
            var match = _all.Find(e => string.Equals(e.Name, _pendingSelectName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                _autoTarget = match;
                _pendingSelectName = null;
            }
        }

        int index = _autoTarget is null ? -1 : _view.IndexOf(_autoTarget);
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

    private void SetFilter(string filter)
    {
        if (filter == _filter)
            return;

        // Narrowing jumps to the first match (so type + Enter works); clearing stays on the
        // item you'd found.
        var keep = filter.Length == 0 ? FocusedEntry() : null;
        _filter = filter;
        _autoSelect = true;
        _autoTarget = keep;
        _pendingSelectName = null;
        _lastAutoIndex = -1;
        RebuildView(preserveScroll: false);
    }

    private void ToggleHidden()
    {
        _showHidden = !_showHidden;
        RebuildView(preserveScroll: false);
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var column = (SortColumn)e.Column;
        bool descending = _comparer.Column == column && !_comparer.Descending;
        _comparer = new EntryComparer(column, descending);

        // Sort a copy: the old list is still the current view until RebuildView captures selection.
        var sorted = new List<FileEntry>(_all);
        sorted.Sort(_comparer);
        _all = sorted;

        UpdateColumnHeaders();
        RebuildView(preserveScroll: false);
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
            item.ForeColor = SystemColors.GrayText;
        e.Item = item;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
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

            case Keys.Control | Keys.A:
                _autoSelect = false;
                _list.SelectAllItems();
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Alt | Keys.Left:
            case Keys.BrowserBack:
                GoBack();
                return true;
            case Keys.Alt | Keys.Right:
            case Keys.BrowserForward:
                GoForward();
                return true;
            case Keys.Alt | Keys.Up:
                GoUp();
                return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F4:
                FocusPathBar();
                return true;
            case Keys.Control | Keys.R:
            case Keys.BrowserRefresh:
                RefreshListing();
                return true;
            case Keys.Control | Keys.H:
                ToggleHidden();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------------
    // Opening
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static FileEntry? EntryAt(List<FileEntry> list, int index) =>
        (uint)index < (uint)list.Count ? list[index] : null;

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

    private void ShowError(string message)
    {
        _error = message;
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
            _statusLabel.ForeColor = ErrorColor;
            _statusLabel.Text = _error;
            return;
        }

        _statusLabel.ForeColor = SystemColors.ControlText;
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
            _icons.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
