namespace CarsControl.Core.Palette;

/// <summary>
/// Scores how well what you typed matches a result, the way command palettes do: a whole-word prefix beats
/// initials ("ue" → "Update everything"), which beat letters scattered through a word; one typo is forgiven
/// in words of four letters or more ("updte" → "Update"). Null means no match. Higher is better.
/// </summary>
public static class FuzzyMatcher
{
    /// <param name="loose">False for keyword lists: only prefixes, word starts and substrings count there
    /// (scattered letters and typos in a list of synonyms found nonsense).</param>
    public static int? Score(string query, string text, bool loose = true)
    {
        var q = Normalize(query);
        var t = Normalize(text);
        if (q.Length == 0) return 0;
        if (t.Length == 0) return null;

        if (t == q) return 1000;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 900 - Math.Min(100, t.Length - q.Length);

        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Every typed word starts some word in the text ("upd ev" → "Update everything").
        if (qWords.All(qw => words.Any(w => w.StartsWith(qw, StringComparison.Ordinal))))
            return 800 - Math.Min(100, words.TakeWhile(w => !w.StartsWith(qWords[0], StringComparison.Ordinal)).Count() * 10);

        if (t.Contains(q, StringComparison.Ordinal)) return 650 - Math.Min(100, t.IndexOf(q, StringComparison.Ordinal));

        if (!loose) return null;

        // Initials ("ue", "cfu" → "Check for updates").
        var initials = new string(words.Select(w => w[0]).ToArray());
        if (q.Length >= 2 && !q.Contains(' ') && initials.StartsWith(q, StringComparison.Ordinal)) return 600;

        // Letters in order, preferring word starts and runs.
        if (Subsequence(q.Replace(" ", ""), t) is { } sub) return 300 + sub;

        // One typo per word of 4+ letters (two for 8+).
        if (qWords.All(qw => words.Any(w => CloseEnough(qw, w)))) return 200;
        return null;
    }

    private static string Normalize(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The letters in order, starting at a word start and within a short stretch of text: random
    /// names (encrypted vault files) otherwise contain almost any short query somewhere.</summary>
    private static int? Subsequence(string q, string t)
    {
        int ti = 0, score = 0, run = 0, first = -1;
        foreach (var c in q)
        {
            var found = false;
            while (ti < t.Length)
            {
                var atWordStart = ti == 0 || t[ti - 1] == ' ';
                if (t[ti] == c)
                {
                    if (first < 0)
                    {
                        if (!atWordStart)
                        {
                            ti++;
                            continue;
                        }
                        first = ti;
                    }
                    run++;
                    score += (atWordStart ? 12 : 2) + run * 3;
                    ti++;
                    found = true;
                    break;
                }
                run = 0;
                ti++;
            }
            if (!found) return null;
        }
        return ti - first > q.Length * 3 + 2 ? null : Math.Min(score, 250);
    }

    private static bool CloseEnough(string typed, string word)
    {
        if (typed.Length < 4) return false;
        var allowed = typed.Length >= 8 ? 2 : 1;
        // Compare with the word's start of about the same length (typing "updte" for "updates").
        for (var len = Math.Max(1, typed.Length - allowed); len <= Math.Min(word.Length, typed.Length + allowed); len++)
            if (Distance(typed, word[..len]) <= allowed) return true;
        return false;
    }

    /// <summary>Optimal string alignment distance (Levenshtein plus adjacent swaps).</summary>
    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[a.Length, b.Length];
    }

    /// <summary>Things you pick often rank higher (capped, so a match still matters more than habit).</summary>
    public static int UsageBoost(int timesChosen) => Math.Min(timesChosen, 25) * 4;
}
