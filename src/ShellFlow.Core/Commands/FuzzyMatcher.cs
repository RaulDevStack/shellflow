namespace ShellFlow.Core.Commands;

/// <summary>Subsequence matcher for the command palette. Rewards word starts and consecutive characters.</summary>
public static class FuzzyMatcher
{
    /// <summary>Returns a score (higher is better) or null when <paramref name="query"/> does not match.</summary>
    public static int? Score(string query, string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        query = query.Trim();
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            var index = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            return 1000 - index + (IsWordStart(candidate, index) ? 200 : 0) + query.Length;
        }

        var score = 0;
        var position = 0;
        var previousMatch = -2;
        foreach (var qc in query)
        {
            if (qc == ' ')
            {
                continue;
            }

            var found = -1;
            for (var i = position; i < candidate.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(qc))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            score += 10;
            if (found == previousMatch + 1)
            {
                score += 15;
            }

            if (IsWordStart(candidate, found))
            {
                score += 25;
            }

            score -= Math.Min(found - position, 10);
            previousMatch = found;
            position = found + 1;
        }

        return score;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]) || (char.IsUpper(text[index]) && char.IsLower(text[index - 1]));
}
