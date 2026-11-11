using Escapade.Core;

namespace Escapade.Tests;

public class EntryComparerTests
{
    private static FileEntry File(string name, long size = 0, int day = 1) =>
        new(name, EntryKind.File, 0, size, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));

    private static FileEntry Dir(string name) => new(name, EntryKind.Directory);

    private static string[] Names(IEnumerable<FileEntry> entries) => entries.Select(e => e.Name).ToArray();

    [Fact]
    public void Folders_first_then_natural_order()
    {
        var list = new List<FileEntry> { File("file10.txt"), Dir("zeta"), File("file2.txt"), Dir("Alpha"), File("File1.txt") };
        list.Sort(EntryComparer.Default);
        Assert.Equal(["Alpha", "zeta", "File1.txt", "file2.txt", "file10.txt"], Names(list));
    }

    [Fact]
    public void Descending_keeps_folders_on_top()
    {
        var list = new List<FileEntry> { File("a"), Dir("d1"), File("b"), Dir("d2") };
        list.Sort(new EntryComparer(SortColumn.Name, descending: true));
        Assert.Equal(["d2", "d1", "b", "a"], Names(list));
    }

    [Fact]
    public void Sort_by_size_breaks_ties_by_name()
    {
        var list = new List<FileEntry> { File("b", 10), File("a", 10), File("c", 5) };
        list.Sort(new EntryComparer(SortColumn.Size, descending: false));
        Assert.Equal(["c", "a", "b"], Names(list));
    }

    [Fact]
    public void Merge_of_batches_equals_full_sort()
    {
        var rng = new Random(42);
        var all = Enumerable.Range(0, 500)
            .Select(i => rng.Next(4) == 0 ? Dir($"dir{rng.Next(1000)}_{i}") : File($"f{rng.Next(1000)}_{i}.txt", rng.Next(10000), rng.Next(1, 28)))
            .ToList();

        foreach (var comparer in new[] { EntryComparer.Default, new EntryComparer(SortColumn.Modified, true), new EntryComparer(SortColumn.Size, false) })
        {
            var merged = new List<FileEntry>();
            foreach (var chunk in all.Chunk(37))
            {
                var batch = chunk.ToList();
                batch.Sort(comparer);
                merged = EntryComparer.Merge(merged, batch, comparer);
            }

            var expected = new List<FileEntry>(all);
            expected.Sort(comparer);
            Assert.Equal(Names(expected), Names(merged));
        }
    }
}
