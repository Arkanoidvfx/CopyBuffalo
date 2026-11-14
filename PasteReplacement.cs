namespace ArkCopyBuffer;

sealed record PasteReplacement(bool Enabled = false, string From = "", string To = "")
{
    public const int MaxRuleLength = 256;
    internal const int MaxExpandedText = 2_000_000;

    internal sealed class LimitExceededException : Exception;

    public string? For(ClipItem item, int remainingGrowth = MaxExpandedText, string? textOverride = null)
    {
        if (!Enabled || item.IsSensitive || item.IsMedia || item.IsImage || item.IsNodeGraph)
            return null;

        string text = textOverride ?? item.Text;
        return ReplaceAll(text, From, To, Math.Min(Math.Max(text.Length, MaxExpandedText), (long)text.Length + remainingGrowth));
    }

    internal static string? ReplaceAll(string text, string from, string to, long limit)
    {
        if (from.Length == 0 || from == to) return null;
        int match = text.IndexOf(from, StringComparison.Ordinal);
        if (match < 0) return null;

        long outputLength = text.Length;
        if (to.Length != from.Length && (to.Length > from.Length || outputLength > limit))
        {
            do
            {
                outputLength += to.Length - from.Length;
                if (to.Length > from.Length && outputLength > limit) throw new LimitExceededException();
                match = text.IndexOf(from, match + from.Length, StringComparison.Ordinal);
            } while (match >= 0);
        }
        if (outputLength > limit) throw new LimitExceededException();
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var rule = new PasteReplacement(true, "-", "вн");
        var item = new ClipItem { Text = "ЯС42-000839-2", Html = "<b>ЯС42-000839-2</b>" };
        check(rule.For(item) == "ЯС42вн000839вн2" && item.Text == "ЯС42-000839-2", "paste replacement is literal and leaves history intact");
        check((rule with { Enabled = false }).For(item) == null && (rule with { From = "" }).For(item) == null
            && (rule with { From = "_" }).For(item) == null, "disabled, empty and unmatched rules keep original clipboard formats");
        check((rule with { From = "-2", To = "" }).For(item) == "ЯС42-000839" &&
            (rule with { To = "--" }).For(new ClipItem { Text = "-" }) == "--", "paste replacement handles sequences, deletion and nonrecursive output");
        check(rule.For(new ClipItem { Text = item.Text, IsSensitive = true }) == null &&
            rule.For(new ClipItem { Text = item.Text, MediaKind = "folder" }) == null, "paste replacement skips secrets and files");
        bool rejected = false;
        try { _ = rule.For(new ClipItem { Text = new string('-', 1_000_001) }); }
        catch (LimitExceededException) { rejected = true; }
        check(rejected, "paste replacement rejects oversized output before allocating it");
    }
}
