using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ArkCopyBuffer;

sealed class MergePart : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Text { get; }
    public string Title { get { int end = Text.IndexOfAny(['\r', '\n']); string first = end < 0 ? Text : Text[..end]; return first.Length > 90 ? first[..90] + "…" : first; } }
    public bool IsPath => TextSubstitution.IsPath(Text);
    public bool IsMono => IsPath || ClipItem.LooksLikeCode(Text);
    public int Number { get; internal set; }
    public string Detail { get; internal set; } = "";
    public MergePart(string text) => Text = text;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(null));
}

sealed class MergeSlot : INotifyPropertyChanged
{
    public MergePart Owner { get; }
    public TextSubstitution.Change Range { get; }
    public int Number { get; }
    public MergePart? Path { get; internal set; }
    public string Heading => $"{Owner.Number}. {Owner.Title}";
    public string Label => $"Слот {Number} · {(Range.Length == 0 ? "пустые кавычки" : Range.Label)}";
    public string Value => Path?.Text ?? "Перетащите сюда путь";
    public bool Assigned => Path != null;
    public MergeSlot(MergePart owner, TextSubstitution.Change range, int number) => (Owner, Range, Number) = (owner, range, number);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(null));
}

sealed class TextMerge
{
    public ObservableCollection<MergePart> Parts { get; } = [];
    public ObservableCollection<MergeSlot> Slots { get; } = [];
    public bool SubstitutePaths { get; set; } = true;
    public TextMerge(IEnumerable<string> texts)
    {
        foreach (string text in texts) Parts.Add(new(text));
        foreach (var part in Parts.Where(p => !p.IsPath && p.Text.Length <= 20000))
        {
            var slots = TextSubstitution.Find(part.Text, @"C:\merge-slot-probe.tmp")
                .Where(c => c.Priority == 0 || c.Length == 0).OrderBy(c => c.Start).ToList();
            for (int i = 0; i < slots.Count; i++) Slots.Add(new(part, slots[i], i + 1));
        }
        Refresh();
        var paths = Parts.Where(p => p.IsPath).ToArray();
        if (paths.Length == 1 && Slots.Count == 1 && Replacement(Slots[0], paths[0]) != null) Assign(paths[0], Slots[0]);
    }
    TextSubstitution.Change? Replacement(MergeSlot slot, MergePart path)
    {
        var change = TextSubstitution.Find(slot.Owner.Text, path.Text)
            .FirstOrDefault(c => c.Start == slot.Range.Start && c.Length == slot.Range.Length);
        if (change == null) return null;
        string value = change.Text;
        if (change.Quote == '\0' && !(value.StartsWith('"') && value.EndsWith('"')))
        {
            if (value.IndexOfAny(['"', '$', '`']) >= 0) return null;
            change = change with { Text = '"' + value + '"' };
        }
        return change;
    }
    public bool CanAssign(MergePart path, MergeSlot slot) => SubstitutePaths && Parts.Contains(path) && Slots.Contains(slot)
        && path.IsPath && Replacement(slot, path) != null;
    public void AssignInOrder()
    {
        var paths = Parts.Where(p => p.IsPath).ToArray();
        var slots = Slots.OrderBy(s => Parts.IndexOf(s.Owner)).ThenBy(s => s.Range.Start).ToArray();
        int count = Math.Min(paths.Length, slots.Length);
        for (int i = 0; i < count; i++)
            if (!CanAssign(paths[i], slots[i])) throw new InvalidOperationException($"Путь {i + 1} не подходит к слоту {i + 1}. Назначьте его вручную или оставьте строкой.");
        foreach (var slot in Slots) slot.Path = null;
        for (int i = 0; i < count; i++) slots[i].Path = paths[i];
        Refresh();
    }
    public sealed record State(MergePart[] Parts, MergeSlot[] Slots, MergePart?[] Paths, bool SubstitutePaths);
    public State Capture() => new(Parts.ToArray(), Slots.ToArray(), Slots.Select(s => s.Path).ToArray(), SubstitutePaths);
    public void Restore(State state)
    {
        Parts.Clear(); foreach (var part in state.Parts) Parts.Add(part);
        Slots.Clear(); for (int i = 0; i < state.Slots.Length; i++) { state.Slots[i].Path = state.Paths[i]; Slots.Add(state.Slots[i]); }
        SubstitutePaths = state.SubstitutePaths; Refresh();
    }
    public void Assign(MergePart path, MergeSlot slot)
    {
        if (!CanAssign(path, slot))
            throw new InvalidOperationException("Этот путь не подходит к кавычкам слота. Можно объединить его отдельной строкой.");
        var old = Slots.FirstOrDefault(s => s.Path == path);
        if (old == slot) return;
        if (old != null) old.Path = slot.Path is { } previous && Replacement(old, previous) != null ? previous : null;
        slot.Path = path;
        Refresh();
    }
    public void Clear(MergeSlot slot) { if (Slots.Contains(slot)) { slot.Path = null; Refresh(); } }
    public void Move(MergePart part, int index)
    {
        int old = Parts.IndexOf(part);
        if (old >= 0 && Parts.Count > 0) Parts.Move(old, Math.Clamp(index, 0, Parts.Count - 1));
        Refresh();
    }
    public void Remove(MergePart part)
    {
        Parts.Remove(part);
        foreach (var slot in Slots.Where(s => s.Owner == part).ToArray()) Slots.Remove(slot);
        foreach (var slot in Slots.Where(s => s.Path == part)) slot.Path = null;
        Refresh();
    }
    public void Refresh()
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i]; part.Number = i + 1;
            var assigned = SubstitutePaths ? Slots.FirstOrDefault(s => s.Path == part) : null;
            part.Detail = assigned != null ? $"Подставлен в текст {Parts.IndexOf(assigned.Owner) + 1}, слот {assigned.Number}"
                : part.IsPath ? "Путь · перетащите в слот или оставьте строкой" : $"{part.Text.Length:N0} символов · отдельный текст";
            part.Refresh();
        }
        foreach (var slot in Slots) slot.Refresh();
    }
    public string Build()
    {
        List<string> lines = [];
        foreach (var part in Parts)
        {
            if (SubstitutePaths && Slots.Any(s => s.Path == part)) continue;
            string text = part.Text;
            if (SubstitutePaths)
                foreach (var slot in Slots.Where(s => s.Owner == part && s.Path != null).OrderByDescending(s => s.Range.Start))
                    text = (Replacement(slot, slot.Path!) ?? throw new InvalidOperationException("Назначение пути больше не подходит к слоту.")).Apply(text);
            lines.Add(text.TrimEnd('\r', '\n'));
        }
        return string.Join(Environment.NewLine, lines);
    }
    internal static void SelfTest(Action<bool, string> check)
    {
        var plain = new TextMerge(["один\r\n", "два\n", "три"]);
        check(plain.Build() == string.Join(Environment.NewLine, "один", "два", "три"), "merge: построчное объединение без лишних граничных переносов");
        plain.Move(plain.Parts[2], 0);
        check(plain.Build().StartsWith("три" + Environment.NewLine), "merge: перетаскивание меняет порядок результата");
        const string a = @"D:\Первый клип\a.mp4", b = @"D:\Второй клип\b.mp4";
        var document = new TextMerge(["mpv \"\" \"<clip>\" --output=\"\"", a, b, "после команды"]);
        check(document.Slots.Count == 3 && document.Slots.All(s => s.Path == null), "merge: пустые кавычки, заглушки и параметры дают отдельные слоты без угадывания");
        document.Assign(document.Parts[1], document.Slots[0]); document.Assign(document.Parts[2], document.Slots[1]);
        check(document.Build() == "mpv \"" + a + "\" \"" + b + "\" --output=\"\"" + Environment.NewLine + "после команды", "merge: два пути в двух слотах без дублирования строками");
        document.Assign(document.Parts[1], document.Slots[1]);
        check(document.Slots[0].Path?.Text == b && document.Slots[1].Path?.Text == a, "merge: перенос между занятыми слотами меняет пути местами");
        document.SubstitutePaths = false;
        check(document.Build().Contains(a + Environment.NewLine + b) && document.Build().StartsWith("mpv \"\""), "merge: построчный режим сохраняет исходную команду и пути");
        document.SubstitutePaths = true; document.Clear(document.Slots[0]);
        check(document.Build().Contains(b + Environment.NewLine), "merge: снятие назначения возвращает путь строкой");
        document.Remove(document.Parts[1]);
        check(document.Slots.All(s => s.Path == null), "merge: удаление пути очищает назначения");
        var single = new TextMerge(["run {{path}}", a]);
        check(single.Build() == "run \"" + a + "\"", "merge: единственный путь назначается однозначно и получает кавычки");
        var repeated = new TextMerge(["run \"<x>\" \"<x>\"", a, b]);
        repeated.Assign(repeated.Parts[1], repeated.Slots[0]); repeated.Assign(repeated.Parts[2], repeated.Slots[1]);
        check(repeated.Build() == "run \"" + a + "\" \"" + b + "\"", "merge: одинаковые заглушки заменяются по позиции справа налево");
        var unsafeQuote = new TextMerge(["run '<path>'", @"D:\O'Brien\a.mp4"]);
        bool rejected = false; try { unsafeQuote.Assign(unsafeQuote.Parts[1], unsafeQuote.Slots[0]); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && unsafeQuote.Slots[0].Path == null, "merge: несовместимые кавычки не создают сломанную команду");
        repeated.Remove(repeated.Parts[0]);
        check(repeated.Slots.Count == 0 && repeated.Build().Contains(a), "merge: удаление команды освобождает пути");
        var ordered = new TextMerge(["run \"\" \"<clip>\"", a, b]); ordered.AssignInOrder();
        check(ordered.Slots[0].Path?.Text == a && ordered.Slots[1].Path?.Text == b, "merge: явная кнопка назначает несколько путей по порядку");
        var snapshot = ordered.Capture(); ordered.Remove(ordered.Parts[0]); ordered.SubstitutePaths = false; ordered.Restore(snapshot);
        check(ordered.Parts.Count == 3 && ordered.Slots.Count == 2 && ordered.SubstitutePaths && ordered.Build() == "run \"" + a + "\" \"" + b + "\"", "merge: Undo восстанавливает удалённую команду, порядок и назначения");
        var incompatible = new TextMerge(["run \"\" '<path>'", a, @"D:\O'Brien\a.mp4"]);
        rejected = false; try { incompatible.AssignInOrder(); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && incompatible.Slots.All(s => s.Path == null), "merge: несовместимое массовое назначение отвергается целиком");
        var extra = new TextMerge(["run \"\"", a, b]); extra.AssignInOrder();
        check(extra.Build() == "run \"" + a + "\"" + Environment.NewLine + b, "merge: лишний путь остаётся строкой, пустые слоты сохраняются");
    }
}
