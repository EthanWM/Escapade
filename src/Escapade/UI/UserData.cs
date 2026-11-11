using System.ComponentModel;
using System.Diagnostics;
using Escapade.Core;

namespace Escapade.UI;

/// <summary>
/// The user's files in %APPDATA%\Escapade: keybindings.json and bookmarks.json (hand-editable,
/// reloaded as soon as they're saved) and history.json (folder visits for Ctrl+P, saved a few
/// seconds after changing and on exit). UI thread only.
/// </summary>
internal sealed class UserData : IDisposable
{
    private const string KeybindingsName = "keybindings.json";
    private const string BookmarksName = "bookmarks.json";
    private const string HistoryName = "history.json";
    private const int ReloadDelayMs = 250;
    private const int ReloadAttempts = 5;
    private const int HistorySaveDelayMs = 5000;

    private readonly string _folder;
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = ReloadDelayMs };
    private readonly System.Windows.Forms.Timer _historyTimer = new() { Interval = HistorySaveDelayMs };
    private FileSystemWatcher? _watcher;
    private int _reloadAttempt;

    // The text each file had when last read or written, to skip reloads of unchanged files
    // (including the change notifications for our own writes).
    private string? _keymapText;
    private string? _bookmarksText;
    private bool _bookmarksUnreadable;

    public UserData(string? folder = null)
    {
        _folder = folder ?? SettingsFile.DefaultFolder;
        Keymap = Keymap.Defaults(CommandCatalog.All);
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            ReloadChangedFiles();
        };
        _historyTimer.Tick += (_, _) =>
        {
            _historyTimer.Stop();
            SaveHistory();
        };
    }

    public Keymap Keymap { get; private set; }

    public BookmarkList Bookmarks { get; private set; } = new();

    public Frecency History { get; private set; } = new();

    public string Folder => _folder;

    public event Action? KeymapChanged;

    public event Action? BookmarksChanged;

    /// <summary>Something's wrong with a settings file. The message is for the status bar.</summary>
    public event Action<string>? Problem;

    /// <summary>A settings file was reloaded without problems. The message is for the status bar.</summary>
    public event Action<string>? Notice;

    private string PathOf(string name) => Path.Join(_folder, name);

    /// <summary>Reads everything and starts watching for edits. Problems are reported through <see cref="Problem"/>.</summary>
    public void Load(ISynchronizeInvoke ui)
    {
        LoadKeymap();
        LoadBookmarks();
        LoadHistory();
        StartWatching(ui);
    }

    // ------------------------------------------------------------------
    // Keybindings
    // ------------------------------------------------------------------

    private void LoadKeymap(bool reloading = false)
    {
        var text = SettingsFile.ReadText(PathOf(KeybindingsName));
        _keymapText = text;
        var (map, problems) = Keymap.Build(CommandCatalog.All, text);
        if (map is not null)
        {
            Keymap = map;
            KeymapChanged?.Invoke();
        }
        if (problems.Count > 0)
        {
            var kept = map is null ? " Keeping the previous keys." : "";
            Problem?.Invoke($"{KeybindingsName}: {string.Join("; ", problems)}.{kept}");
        }
        else if (reloading)
        {
            Notice?.Invoke($"Reloaded {KeybindingsName}.");
        }
    }

    /// <summary>Opens keybindings.json in the user's editor, first writing one listing every default if there's none.</summary>
    public void EditKeybindings(IWin32Window owner)
    {
        var path = PathOf(KeybindingsName);
        if (TryCreate(path, () => Keymap.Template(CommandCatalog.All), ref _keymapText, owner))
            OpenInEditor(path, owner);
    }

    // ------------------------------------------------------------------
    // Bookmarks
    // ------------------------------------------------------------------

    private void LoadBookmarks()
    {
        var text = SettingsFile.ReadText(PathOf(BookmarksName));
        _bookmarksText = text;
        if (text is null)
        {
            _bookmarksUnreadable = false;
            Bookmarks = new BookmarkList();
            BookmarksChanged?.Invoke();
            return;
        }
        try
        {
            Bookmarks = new BookmarkList(SettingsFile.Deserialize<List<Bookmark>>(text));
            _bookmarksUnreadable = false;
            BookmarksChanged?.Invoke();
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Keep what we had, and don't overwrite the file until it's fixed: it's the user's data.
            _bookmarksUnreadable = true;
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            Problem?.Invoke($"{BookmarksName} isn't valid JSON{where}, so bookmarks can't be changed until it's fixed.");
        }
    }

    /// <summary>Bookmarks or un-bookmarks <paramref name="path"/>. Returns the status message to show.</summary>
    public string ToggleBookmark(string path)
    {
        if (_bookmarksUnreadable)
            return $"Fix {BookmarksName} first: it isn't valid JSON.";
        bool added = Bookmarks.Toggle(path);
        if (SaveBookmarks() is { } error)
            return error;
        var name = PathUtil.ShortName(path);
        if (!added)
            return $"Removed the bookmark for “{name}”. Press {Keymap.ShortcutFor(CommandCatalog.ToggleBookmark) ?? "Ctrl+D"} to add it back.";
        int number = Bookmarks.IndexOf(path) + 1;
        var shortcut = number <= CommandCatalog.BookmarkSlots ? Keymap.ShortcutFor(CommandCatalog.OpenBookmark(number)) : null;
        return shortcut is null ? $"Bookmarked “{name}”." : $"Bookmarked “{name}” as {shortcut}.";
    }

    public string RemoveBookmark(string path)
    {
        if (_bookmarksUnreadable)
            return $"Fix {BookmarksName} first: it isn't valid JSON.";
        if (Bookmarks.Remove(path) is not { } removed)
            return "";
        return SaveBookmarks() ?? $"Removed the bookmark “{removed.Name}”.";
    }

    private string? SaveBookmarks()
    {
        try
        {
            Write(PathOf(BookmarksName), SettingsFile.Serialize(Bookmarks.Items), ref _bookmarksText);
            BookmarksChanged?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BookmarksChanged?.Invoke();
            return $"Couldn't save bookmarks: {ex.Message}";
        }
    }

    public void EditBookmarks(IWin32Window owner)
    {
        var path = PathOf(BookmarksName);
        if (TryCreate(path, () => SettingsFile.Serialize(Bookmarks.Items), ref _bookmarksText, owner))
            OpenInEditor(path, owner);
    }

    // ------------------------------------------------------------------
    // Folder history
    // ------------------------------------------------------------------

    private void LoadHistory()
    {
        try
        {
            if (SettingsFile.ReadText(PathOf(HistoryName)) is { } text)
                History = new Frecency(SettingsFile.Deserialize<List<FrecencyEntry>>(text));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            History = new Frecency(); // only history: start afresh rather than nag
        }
    }

    public void RecordVisit(string path)
    {
        History.Visit(path, DateTime.UtcNow);
        _historyTimer.Stop();
        _historyTimer.Start();
    }

    public void ForgetVisits(string path)
    {
        if (History.Remove(path))
        {
            _historyTimer.Stop();
            _historyTimer.Start();
        }
    }

    /// <summary>Saves anything pending. Call on exit.</summary>
    public void Flush()
    {
        if (_historyTimer.Enabled)
        {
            _historyTimer.Stop();
            SaveHistory();
        }
    }

    private void SaveHistory()
    {
        try
        {
            var entries = History.Entries.OrderByDescending(e => e.LastVisitUtc).ToList();
            SettingsFile.WriteAtomic(PathOf(HistoryName), SettingsFile.Serialize(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting anyone over; the next visit tries again.
        }
    }

    // ------------------------------------------------------------------
    // Watching for edits
    // ------------------------------------------------------------------

    private void StartWatching(ISynchronizeInvoke ui)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var watcher = new FileSystemWatcher(_folder, "*.json")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                SynchronizingObject = ui, // raise events on the UI thread
            };
            // Editors often save by writing a temporary file and renaming it over the original,
            // which shows up as Renamed (or Created) rather than Changed.
            watcher.Changed += (_, _) => ScheduleReload();
            watcher.Created += (_, _) => ScheduleReload();
            watcher.Deleted += (_, _) => ScheduleReload();
            watcher.Renamed += (_, _) => ScheduleReload();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Problem?.Invoke($"Can't watch {_folder} for changes: {ex.Message}");
        }
    }

    private void ScheduleReload()
    {
        _reloadAttempt = 0;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void ReloadChangedFiles()
    {
        try
        {
            if (SettingsFile.ReadText(PathOf(KeybindingsName)) != _keymapText)
                LoadKeymap(reloading: true);
            if (SettingsFile.ReadText(PathOf(BookmarksName)) != _bookmarksText)
                LoadBookmarks();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usually the editor still has the file open mid-save. Try again shortly.
            if (++_reloadAttempt < ReloadAttempts)
                _reloadTimer.Start();
            else
                Problem?.Invoke($"Couldn't read a settings file: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void Write(string path, string text, ref string? remembered)
    {
        remembered = text; // set first: the watcher may fire before WriteAtomic returns
        SettingsFile.WriteAtomic(path, text);
    }

    /// <summary>Creates the file with <paramref name="initial"/> content if it doesn't exist yet.</summary>
    private static bool TryCreate(string path, Func<string> initial, ref string? remembered, IWin32Window owner)
    {
        if (File.Exists(path))
            return true;
        try
        {
            Write(path, initial(), ref remembered);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, $"Couldn't create {path}: {ex.Message}", "Escapade", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static void OpenInEditor(string path, IWin32Window owner)
    {
        // .json may have no default app; Notepad always exists.
        if (ShellLauncher.Open(path, owner: null) is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, $"Couldn't open {path}: {ex.Message}", "Escapade", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public string? OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = false })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return $"Couldn't open {_folder}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        Flush();
        _watcher?.Dispose();
        _reloadTimer.Dispose();
        _historyTimer.Dispose();
    }
}
