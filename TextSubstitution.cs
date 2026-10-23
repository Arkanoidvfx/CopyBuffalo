using System.Text.RegularExpressions;

namespace ArkCopyBuffer;

// Shared by the editor and direct drops. This edits text; it never runs the command.
static class TextSubstitution
{
    internal sealed record Change(int Start, int Length, string Text, string Label, int Priority, bool CanApply)
    {
        public char Quote { get; init; }
        public string Preview => Text;
        public string Apply(string original) => original[..Start] + Text + original[(Start + Length)..];
    }

    static readonly Regex Tokens = new("""(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s"']+)+""");
    static readonly Regex Placeholders = new(@"<[^<>\r\n]{1,120}>|\{\{[^{}\r\n]{1,120}\}\}");
    static readonly Regex PathStart = new(@"^(?:[A-Za-z]:[\\/]|\\\\|\.{1,2}[\\/]|/|~[\\/])");
    static readonly Regex BareValue = new(@"^[\p{L}\p{N}_./\\:~@%+=-]+$");

    internal static bool IsPath(string source)
    {
        string value = Unquote(source);
        return PathStart.IsMatch(value) && value.IndexOfAny(['\r', '\n', '\0', '"', '<', '>']) < 0
            && !Regex.IsMatch(value, @"\s--?[\p{L}\p{N}_-]+(?:=|\s|$)");
    }
    static string Unquote(string source)
    {
        string value = source.Trim();
        return value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;
    }

    internal static List<Change> Find(string original, string source)
    {
        string value = Unquote(source);
        List<Change> changes = [];
        if (value.Length == 0 || value.IndexOfAny(['\r', '\n', '\0']) >= 0) return changes;
        var placeholders = Placeholders.Matches(original);
        var tokens = Tokens.Matches(original);
        int end = 0;
        foreach (Match token in tokens)
        {
            if (!string.IsNullOrWhiteSpace(original[end..token.Index])) return changes;
            end = token.Index + token.Length;
        }
        if (!string.IsNullOrWhiteSpace(original[end..])) return changes; // unmatched quote
        foreach (Match marker in placeholders)
        {
            var containing = tokens.Cast<Match>().FirstOrDefault(t => t.Index <= marker.Index && t.Index + t.Length >= marker.Index + marker.Length);
            char quote = '\0';
            if (containing != null)
            {
                int offset = ValueOffset(containing.Value);
                if (offset < containing.Length && containing.Value[offset] is '"' or '\'') quote = containing.Value[offset];
            }
            Add(marker.Index, marker.Length, quote, marker.Value, 0, literal: quote == '\0' && marker.Value.StartsWith("{{"));
        }
        foreach (Match token in tokens)
        {
            int offset = ValueOffset(token.Value);
            bool option = offset > 0;
            if (offset >= token.Length || !option && token == tokens[0]) continue; // executable
            string body = token.Value[offset..];
            char quote = body.Length >= 2 && body[0] is '"' or '\'' && body[^1] == body[0] ? body[0] : '\0';
            int start = token.Index + offset + (quote == '\0' ? 0 : 1);
            int length = body.Length - (quote == '\0' ? 0 : 2);
            if (quote != '\0' && body[1..^1].Contains(quote)) continue;
            if (placeholders.Cast<Match>().Any(p => p.Index >= start && p.Index + p.Length <= start + length)) continue;
            string content = original.Substring(start, length);
            if (option || quote != '\0' || PathStart.IsMatch(content))
                Add(start, length, quote, option ? token.Value[..offset] + content : content,
                    option ? 3 : PathStart.IsMatch(content) ? 1 : 2);
        }
        return changes;

        void Add(int start, int length, char quote, string label, int priority, bool literal = false)
        {
            string replacement = value;
            if (quote != '\0')
            {
                if (value.Contains(quote) || quote == '"' && value.IndexOfAny(['$', '`']) >= 0) return;
            }
            else if (!literal && !BareValue.IsMatch(value))
            {
                if (value.IndexOfAny(['"', '$', '`']) >= 0) return;
                replacement = '"' + value + '"';
            }
            changes.Add(new(start, length, replacement, label, priority, original.Substring(start, length) != replacement) { Quote = quote });
        }
    }

    static int ValueOffset(string token)
    {
        int equals = token.IndexOf('=');
        return token.StartsWith('-') && equals > 1 ? equals + 1 : 0;
    }

    internal static Change? Automatic(IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return null;
        int priority = changes.Min(c => c.Priority);
        var best = changes.Where(c => c.Priority == priority).ToArray();
        // Named options are explicit choices, even when only one is available.
        return best.Length == 1 && priority < 3 && best[0].CanApply ? best[0] : null;
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        const string command = ".\\build\\mpv.exe --config-dir=.tmp\\tip-check --ao=null --focus-on=never --log-file=.tmp\\tip-check.log \"<путь к клипу>\"";
        const string path = @"D:\Shadow Play\клип.mp4";
        check(!IsPath(command) && IsPath(path) && !IsPath(path + "\nещё"), "команда с относительным exe не считается отдельным путём; одиночный путь распознаётся");
        check(Automatic(Find(command, path))?.Apply(command) == command.Replace("<путь к клипу>", path),
            "подстановка маркера в исходной команде mpv сохраняет остальные параметры");
        foreach (string old in new[] { @".\old.mp4", @"..\old.mp4", "./old.mp4", "../old.mp4", @"\\server\clips\old.mp4", "D:/old.mp4" })
        {
            string text = "mpv \"" + old + "\"";
            check(Automatic(Find(text, path))?.Apply(text) == "mpv \"" + path + "\"", "подстановка пути: " + old);
        }
        check(Automatic(Find("mpv <clip>", path))?.Apply("mpv <clip>") == "mpv \"" + path + "\"",
            "путь с пробелами получает кавычки вместо голого маркера");
        check(Automatic(Find("Hello {{name}}!", "Иван Иванов"))?.Apply("Hello {{name}}!") == "Hello Иван Иванов!",
            "обычный текст подставляется в шаблон");
        check(Automatic(Find("mpv \"D:/a.mp4\" \"D:/b.mp4\"", path)) == null
            && Find("mpv \"D:/a.mp4\" \"D:/b.mp4\"", path).Count == 2
            && Automatic(Find("run --output=\"D:/a\"", path)) == null,
            "несколько аргументов и именованные параметры требуют точного выбора");
        var option = Find("run --output=\"D:/a\"", path).Single();
        check(option.Apply("run --output=\"D:/a\"") == "run --output=\"" + path + "\"", "точная замена именованного параметра");
        check(Automatic(Find("mpv \"<clip>\"", @"D:\O'Brien\clip.mp4")) != null
            && Find("mpv '<clip>'", @"D:\O'Brien\clip.mp4").Count == 0
            && Find(command, "a\nb").Count == 0 && Find(command, "\"broken").Count == 0
            && Find(command, "$unsafe").Count == 0,
            "кавычки, апострофы, переносы строк и интерполяция проверяются до подстановки");
        check(Find("\"D:/mpv.exe\" --flag", path).Count == 0
            && Automatic(Find("mpv \"" + path + "\"", path)) == null
            && Automatic(Find("mpv \"D:/a.mp4\" \"D:/b.mp4\"", "D:/a.mp4")) == null
            && Find("mpv \"D:/unfinished.mp4", path).Count == 0,
            "исполняемый файл и неизменившееся значение не предлагаются как цель");
    }
}
