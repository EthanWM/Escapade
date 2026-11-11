namespace Escapade.Core;

/// <summary>A fuzzy match: higher scores are better; positions are the matched character indexes, for highlighting.</summary>
public sealed record FuzzyMatch(int Score, int[] Positions);

/// <summary>
/// Fuzzy matching for the palettes. The query is split on spaces and every word must appear in
/// the text, in order, as a subsequence ("esc src" matches E:\repos\Escapade\src). Matches score
/// higher at the start of a word, in runs of consecutive characters, and in the last path
/// segment, the "name": typing a folder's name should find that folder rather than its children.
/// </summary>
public static class Fuzzy
{
    private const int Base = 1;
    private const int WordStart = 8;
    private const int CamelHump = 6;
    private const int Consecutive = 5;
    private const int InName = 2;
    private const int LastWordInName = 10;
    private const int NamePrefix = 10;
    private const int NameExact = 10;

    /// <param name="nameStart">Where the name begins in <paramref name="text"/> (the last path segment); 0 for plain titles.</param>
    public static FuzzyMatch? Match(string query, string text, int nameStart = 0)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return new FuzzyMatch(0, []);

        // How far right each word may end so that the words after it still fit: match them
        // from the right as late as possible. This also rules out texts that can't match at all.
        var latestEnd = new int[words.Length];
        int limit = text.Length - 1;
        for (int w = words.Length - 1; w >= 0; w--)
        {
            latestEnd[w] = limit;
            int j = limit;
            for (int i = words[w].Length - 1; i >= 0; i--)
            {
                while (j >= 0 && !Same(text[j], words[w][i]))
                    j--;
                if (j < 0)
                    return null;
                j--;
            }
            limit = j;
        }

        var positions = new List<int>();
        int score = 0;
        int from = 0;
        int[] lastWord = [];
        for (int w = 0; w < words.Length; w++)
        {
            var (wordScore, wordPositions) = BestAlignment(words[w], text, from, latestEnd[w], nameStart);
            score += wordScore;
            positions.AddRange(wordPositions);
            from = wordPositions[^1] + 1;
            lastWord = wordPositions;
        }

        // Prefer texts whose name is what the last word describes.
        if (lastWord[0] >= nameStart)
        {
            score += LastWordInName;
            var name = text.AsSpan(nameStart);
            var word = words[^1];
            if (name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                score += NamePrefix;
                if (name.Length == word.Length)
                    score += NameExact;
            }
        }

        score -= text.Length / 16; // all else equal, shorter is better
        return new FuzzyMatch(score, positions.ToArray());
    }

    /// <summary>The best-scoring way to match <paramref name="word"/> within text[from..to].</summary>
    private static (int Score, int[] Positions) BestAlignment(string word, string text, int from, int to, int nameStart)
    {
        int m = word.Length;
        int n = to - from + 1;
        const int None = int.MinValue / 2;
        var score = new int[m, n];
        var previous = new int[m, n];

        for (int i = 0; i < m; i++)
        {
            int bestEarlier = None; // best score[i-1, k] for k < j-1
            int bestEarlierAt = -1;
            for (int j = 0; j < n; j++)
            {
                if (i > 0 && j >= 2 && score[i - 1, j - 2] > bestEarlier)
                {
                    bestEarlier = score[i - 1, j - 2];
                    bestEarlierAt = j - 2;
                }

                score[i, j] = None;
                previous[i, j] = -1;
                if (!Same(text[from + j], word[i]))
                    continue;

                int bonus = CharBonus(text, from + j, nameStart);
                if (i == 0)
                {
                    score[i, j] = bonus;
                    continue;
                }

                int viaRun = j >= 1 && score[i - 1, j - 1] > None ? score[i - 1, j - 1] + Consecutive : None;
                if (viaRun > None && viaRun >= bestEarlier)
                {
                    score[i, j] = viaRun + bonus;
                    previous[i, j] = j - 1;
                }
                else if (bestEarlier > None)
                {
                    score[i, j] = bestEarlier + bonus;
                    previous[i, j] = bestEarlierAt;
                }
            }
        }

        int end = -1;
        for (int j = 0; j < n; j++)
        {
            if (score[m - 1, j] > None && (end < 0 || score[m - 1, j] > score[m - 1, end]))
                end = j;
        }
        if (end < 0)
            throw new InvalidOperationException("No alignment, though one was known to exist.");

        var positions = new int[m];
        for (int i = m - 1, j = end; i >= 0; i--)
        {
            positions[i] = from + j;
            j = previous[i, j];
        }
        return (score[m - 1, end], positions);
    }

    private static int CharBonus(string text, int index, int nameStart)
    {
        int bonus = Base;
        if (index == 0 || IsSeparator(text[index - 1]))
            bonus += WordStart;
        else if (char.IsLower(text[index - 1]) && char.IsUpper(text[index]))
            bonus += CamelHump;
        if (index >= nameStart)
            bonus += InName;
        return bonus;
    }

    private static bool IsSeparator(char c) => c is ' ' or '\\' or '/' or '-' or '_' or '.' or ':' or '(' or '[';

    private static bool Same(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
