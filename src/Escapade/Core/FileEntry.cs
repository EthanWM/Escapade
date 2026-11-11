namespace Escapade.Core;

public enum EntryKind
{
    Drive,
    Directory,
    File,
}

/// <summary>
/// One row in a pane. Deliberately stores only the name (not the full path) to keep
/// huge listings cheap; the pane combines it with its current location on demand.
/// </summary>
public sealed class FileEntry
{
    private string? _typeName;

    public FileEntry(
        string name,
        EntryKind kind,
        FileAttributes attributes = 0,
        long size = -1,
        DateTime lastWriteUtc = default,
        string? displayName = null,
        string? typeName = null)
    {
        Name = name;
        Kind = kind;
        Attributes = attributes;
        Size = size;
        LastWriteUtc = lastWriteUtc;
        DisplayName = displayName ?? name;
        _typeName = typeName;
    }

    /// <summary>Path segment within the parent location. For drives this is the root, e.g. <c>C:\</c>.</summary>
    public string Name { get; }

    /// <summary>Text shown in the list and matched by the filter.</summary>
    public string DisplayName { get; }

    public EntryKind Kind { get; }

    public FileAttributes Attributes { get; }

    /// <summary>Bytes for files, total capacity for drives, -1 when unknown or not applicable.</summary>
    public long Size { get; }

    /// <summary><see cref="DateTime.MinValue"/> when unknown.</summary>
    public DateTime LastWriteUtc { get; }

    public bool IsContainer => Kind != EntryKind.File;

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    public string TypeName => _typeName ??= Kind switch
    {
        EntryKind.Drive => "Drive",
        EntryKind.Directory => "File folder",
        _ => Path.GetExtension(Name) is { Length: > 1 } ext
            ? ext[1..].ToUpperInvariant() + " File"
            : "File",
    };

    public override string ToString() => DisplayName;
}
