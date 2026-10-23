using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArkCopyBuffer;

static class OcrText
{
    internal sealed record Region(string Text, double Confidence, double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public double CenterY => Y + Height / 2;
    }
    internal sealed record Result(string Text, bool ReviewNeeded, int CorrectedCharacters, bool Columns)
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public IReadOnlyList<Region> Regions { get; init; } = [];
    }
    static readonly Regex Words = new(@"[\p{L}\p{M}\p{Nd}]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    const string Cyrillic = "АВСЕНКМОРТХУаеорсухіѕһӏӀԁ";
    const string Latin =    "ABCEHKMOPTXYaeopcyxishlId";

    internal static Result Parse(string json, long maxPixels = 80_000_000)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = doc.RootElement;
        int w = root.GetProperty("width").GetInt32(), h = root.GetProperty("height").GetInt32();
        if (w <= 0 || h <= 0 || (long)w * h > Math.Min(maxPixels, 80_000_000)) throw new InvalidDataException("OCR image dimensions.");
        var entries = root.GetProperty("regions");
        if (entries.GetArrayLength() > 1000) throw new InvalidDataException("OCR region count.");
        var regions = new List<Region>();
        int total = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var text = entry.GetProperty("text").GetString() ?? throw new InvalidDataException("OCR text missing.");
            if ((total += text.Length) > 2_000_000 || text.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c)))
                throw new InvalidDataException("OCR text size or control characters.");
            double confidence = entry.GetProperty("confidence").GetDouble();
            var box = entry.GetProperty("box");
            if (box.GetArrayLength() != 4) throw new InvalidDataException("OCR box.");
            var r = new Region(text.Trim(), confidence, box[0].GetDouble(), box[1].GetDouble(), box[2].GetDouble(), box[3].GetDouble());
            if (!double.IsFinite(confidence) || confidence is < 0 or > 1 ||
                !double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height) ||
                r.Width <= 0 || r.Height <= 0 || r.X < -w || r.Y < -h || r.Right > 2d * w || r.Bottom > 2d * h)
                throw new InvalidDataException("OCR geometry.");
            if (r.Text.Length > 0) regions.Add(r);
        }
        return Build(regions) with { Width = w, Height = h };
    }

    internal static Result Build(IReadOnlyList<Region> regions)
    {
        bool columns = false, review = regions.Any(r => r.Confidence < .85);
        int corrected = 0;
        var lines = Order(regions.ToList(), ref columns);
        var output = new List<string>();
        foreach (var line in lines)
        {
            var text = NormalizeScripts(string.Join(" ", line.Select(r => r.Text)), out int changes, out bool uncertain);
            corrected += changes;
            review |= uncertain;
            output.Add(text);
        }
        return new(string.Join(Environment.NewLine, output), review || columns, corrected, columns)
        {
            Regions = regions.Select(r => r with { Text = NormalizeScripts(r.Text, out _, out _) }).ToArray()
        };
    }

    internal static string NormalizeScripts(string text, out int corrections, out bool ambiguous)
    {
        int changed = 0;
        bool uncertain = false;
        text = text.Normalize(NormalizationForm.FormC);
        var result = Words.Replace(text, match =>
        {
            string word = match.Value;
            bool hasLatin = word.Any(char.IsAsciiLetter), hasCyrillic = word.Any(IsCyrillic);
            bool strongLatin = word.Any(c => char.IsAsciiLetter(c) && !Latin.Contains(c));
            bool strongCyrillic = word.Any(c => IsCyrillic(c) && !Cyrillic.Contains(c));
            if (hasLatin && hasCyrillic && strongLatin != strongCyrillic)
            {
                string from = strongLatin ? Cyrillic : Latin, to = strongLatin ? Latin : Cyrillic;
                var chars = word.ToCharArray();
                for (int i = 0; i < chars.Length; i++)
                {
                    int index = from.IndexOf(chars[i]);
                    if (index < 0 || !strongLatin && index >= 19) continue;
                    chars[i] = to[index]; changed++;
                }
                word = new(chars);
            }
            // All-lookalike words have no reliable script evidence; never guess from their neighbours.
            uncertain |= word.Any(char.IsAsciiLetter) && word.Any(IsCyrillic)
                || word.Any(c => "іѕһӏӀԁ".Contains(c))
                || word.Length >= 3 && word.All(c => IsCyrillic(c) && char.IsUpper(c) && Cyrillic.Contains(c));
            int end = match.Index + match.Length;
            bool identifier = match.Index > 0 && "\\/_@".Contains(text[match.Index - 1])
                || end < text.Length && "\\/_@#+".Contains(text[end])
                || end + 1 < text.Length && text[end] == ':' && "\\/".Contains(text[end + 1]);
            uncertain |= identifier && word.Any(IsCyrillic) && word.All(c => char.IsDigit(c) || Cyrillic.Contains(c));
            return word;
        });
        corrections = changed; ambiguous = uncertain;
        return result;
    }

    static bool IsCyrillic(char c) => c is >= '\u0400' and <= '\u052f';

    static List<List<Region>> Order(List<Region> regions, ref bool columns, int depth = 0)
    {
        if (regions.Count == 0) return [];
        if (regions.Count == 1) return [[regions[0]]];
        var heights = regions.Select(r => r.Height).Order().ToArray();
        double height = heights[heights.Length / 2];
        double extent = regions.Max(r => r.Right) - regions.Min(r => r.X);
        var narrow = regions.Where(r => r.Width < extent * .7).OrderBy(r => r.X).ToList();
        var (xGap, xCut) = Gap(narrow.Select(r => (r.X, r.Right)));
        if (depth < 32 && xGap >= Math.Max(height * 2, extent * .03))
        {
            var left = regions.Where(r => r.Right <= xCut).ToList();
            var right = regions.Where(r => r.X >= xCut).ToList();
            var spanning = regions.Except(left).Except(right).OrderBy(r => r.CenterY).ToList();
            double overlap = left.Count == 0 || right.Count == 0 ? 0 :
                Math.Min(left.Max(r => r.Bottom), right.Max(r => r.Bottom)) - Math.Max(left.Min(r => r.Y), right.Min(r => r.Y));
            if (left.Count >= 2 && right.Count >= 2 && overlap >= height * 2)
            {
                columns = true;
                if (spanning.Count == 0)
                {
                    var ordered = Order(left, ref columns, depth + 1);
                    ordered.AddRange(Order(right, ref columns, depth + 1));
                    return ordered;
                }
                var body = left.Concat(right).ToList();
                var output = new List<List<Region>>();
                foreach (var banner in spanning)
                {
                    var before = body.Where(r => r.CenterY < banner.CenterY).ToList();
                    output.AddRange(Order(before, ref columns, depth + 1));
                    body.RemoveAll(before.Contains);
                    output.Add([banner]);
                }
                output.AddRange(Order(body, ref columns, depth + 1));
                return output;
            }
        }
        var (yGap, yCut) = Gap(regions.OrderBy(r => r.Y).Select(r => (r.Y, r.Bottom)));
        if (depth < 32 && yGap > height * .8)
        {
            var output = Order(regions.Where(r => r.Bottom <= yCut).ToList(), ref columns, depth + 1);
            output.AddRange(Order(regions.Where(r => r.Y >= yCut).ToList(), ref columns, depth + 1));
            return output;
        }
        var rows = new List<List<Region>>();
        foreach (var region in regions.OrderBy(r => r.CenterY).ThenBy(r => r.X))
        {
            var row = rows.LastOrDefault();
            if (row == null || Math.Abs(row[0].CenterY - region.CenterY) > Math.Min(row[0].Height, region.Height) * .5)
                rows.Add([region]);
            else row.Add(region);
        }
        foreach (var row in rows) row.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;
    }

    static (double Gap, double Cut) Gap(IEnumerable<(double Start, double End)> intervals)
    {
        double right = double.NegativeInfinity, best = 0, cut = 0;
        foreach (var (start, end) in intervals)
        {
            if (double.IsFinite(right) && start - right > best) { best = start - right; cut = (start + right) / 2; }
            right = Math.Max(right, end);
        }
        return (best, cut);
    }
}
