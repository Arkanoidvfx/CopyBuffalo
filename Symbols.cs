using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ArkCopyBuffer;

public sealed record SymbolItem(string Char, string Group, bool Pinned)
{
    public string Code => string.Join(" ", Char.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));

    /// <summary>Буква без диакритики: ć, Č → c; æ → ae. По ней символ находится с обычной клавиатуры.</summary>
    public string Base { get; } = string.Concat(Char.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
        // Эти буквы Unicode не раскладывает на основу и знак.
        .Select(c => char.ToLowerInvariant(c) switch
        {
            'ß' or 'ẞ' => "ss", 'æ' => "ae", 'œ' => "oe", 'þ' => "th", 'ø' => "o", 'đ' or 'ð' => "d", 'ł' => "l",
            'ı' => "i", 'ħ' => "h", 'ŧ' => "t", 'ŋ' => "n", 'ə' => "e", 'ґ' => "г", var plain => plain.ToString(),
        }));
}

/// <summary>
/// Вкладка «Символы»: набор как у Win+V плюс закреплённые пользователем — группой сверху.
/// Закреплённые символы не секрет, поэтому обычный JSON рядом с историей.
/// </summary>
sealed class SymbolCatalog
{
    public static readonly string PinnedGroup = UiText.T("Закреплённые");

    static readonly (string group, string chars)[] Groups =
    [
        (UiText.T("Пунктуация"), "« » „ “ ” ‘ ’ ‚ … — – ‐ • · ‹ › ¡ ¿ § ¶ † ‡ ‰ ′ ″ ‼ ‽ ※ © ® ™ ℗ № ° ¹ ² ³ ⁰ ⁴ ⁵ ⁶ ⁷ ⁸ ⁹ ₀ ₁ ₂ ₃ ¦ ¨ ¯ ´ ¸ ˜"),
        (UiText.T("Валюты"), "₽ $ € £ ¥ ¢ ₴ ₸ ₹ ₩ ₪ ₫ ₺ ₼ ₾ ₿ ƒ ¤ ₱ ₲ ₵ ₡ ₦ ₭ ₮ ₣ ₤ ₨ ₳"),
        (UiText.T("Стрелки"), "← ↑ → ↓ ↔ ↕ ↖ ↗ ↘ ↙ ⇐ ⇑ ⇒ ⇓ ⇔ ⇕ ↩ ↪ ↺ ↻ ⟵ ⟶ ⟷ ➜ ➔ ➤ ⤴ ⤵ ⇄ ⇆"),
        (UiText.T("Математика"), "± ∓ × ÷ − ≠ ≈ ≡ ≤ ≥ ≪ ≫ √ ∛ ∞ ∑ ∏ ∫ ∂ ∆ ∇ ∈ ∉ ∩ ∪ ⊂ ⊃ ⊆ ⊇ ∅ ∀ ∃ ¬ ∧ ∨ ⊕ ⊗ ∝ ∠ ⊥ ∥ ½ ⅓ ⅔ ¼ ¾ ⅛ π µ ℓ ℃ ℉"),
        ($"{UiText.T("Греческий")} α–ω", "α β γ δ ε ζ η θ ι κ λ μ ν ξ ο π ρ σ ς τ υ φ χ ψ ω"),
        ($"{UiText.T("Греческий")} Α–Ω", "Α Β Γ Δ Ε Ζ Η Θ Ι Κ Λ Μ Ν Ξ Ο Π Ρ Σ Τ Υ Φ Χ Ψ Ω"),
        ($"{UiText.T("Латиница")} a–z", "à á â ã ä å æ ā ă ą ǎ ạ ả ấ ầ ẩ ẫ ậ ắ ằ ẳ ẵ ặ ç ć ĉ ċ č ð ď đ ḍ è é ê ë ē ĕ ė ę ě ə ẹ ẻ ẽ ế ề ể ễ ệ ĝ ğ ġ ģ ǧ ĥ ħ ḥ ì í î ï ĩ ī ĭ į ı ǐ ỉ ị ĵ ķ ǩ ḳ ĺ ļ ľ ł ñ ń ņ ň ŋ ò ó ô õ ö ø ō ŏ ő œ ơ ǒ ǫ ọ ỏ ố ồ ổ ỗ ộ ớ ờ ở ỡ ợ ŕ ŗ ř ß ś ŝ ş š ș ṣ þ ţ ť ŧ ț ṭ ù ú û ü ũ ū ŭ ů ű ų ư ǔ ǖ ǘ ǚ ǜ ụ ủ ứ ừ ử ữ ự ŵ ẁ ẃ ẅ ý ÿ ŷ ỳ ỵ ỷ ỹ ź ż ž"),
        ($"{UiText.T("Латиница")} A–Z", "À Á Â Ã Ä Å Æ Ā Ă Ą Ǎ Ạ Ả Ấ Ầ Ẩ Ẫ Ậ Ắ Ằ Ẳ Ẵ Ặ Ç Ć Ĉ Ċ Č Ð Ď Đ Ḍ È É Ê Ë Ē Ĕ Ė Ę Ě Ə Ẹ Ẻ Ẽ Ế Ề Ể Ễ Ệ Ĝ Ğ Ġ Ģ Ǧ Ĥ Ħ Ḥ Ì Í Î Ï Ĩ Ī Ĭ Į İ Ǐ Ỉ Ị Ĵ Ķ Ǩ Ḳ Ĺ Ļ Ľ Ł Ñ Ń Ņ Ň Ŋ Ò Ó Ô Õ Ö Ø Ō Ŏ Ő Œ Ơ Ǒ Ǫ Ọ Ỏ Ố Ồ Ổ Ỗ Ộ Ớ Ờ Ở Ỡ Ợ Ŕ Ŗ Ř ẞ Ś Ŝ Ş Š Ș Ṣ Þ Ţ Ť Ŧ Ț Ṭ Ù Ú Û Ü Ũ Ū Ŭ Ů Ű Ų Ư Ǔ Ǖ Ǘ Ǚ Ǜ Ụ Ủ Ứ Ừ Ử Ữ Ự Ŵ Ẁ Ẃ Ẅ Ý Ÿ Ŷ Ỳ Ỵ Ỷ Ỹ Ź Ż Ž"),
        ($"{UiText.T("Кириллица")} а–я", "ё ї і є ґ ў ј љ њ ћ џ ђ ѓ ќ"),
        ($"{UiText.T("Кириллица")} А–Я", "Ё Ї І Є Ґ Ў Ј Љ Њ Ћ Џ Ђ Ѓ Ќ"),
        (UiText.T("Фигуры"), "■ □ ▪ ▫ ▬ ▲ △ ▶ ▷ ▼ ▽ ◀ ◁ ◆ ◇ ○ ◎ ● ◐ ◑ ★ ☆ ♠ ♣ ♥ ♦ ♤ ♧ ♡ ♢ ✓ ✔ ✕ ✖ ✗ ✘ ☐ ☑ ☒ ✦ ✧ ❖"),
        (UiText.T("Прочее"), "♪ ♫ ☀ ☁ ☂ ☃ ☎ ☕ ☘ ☯ ☮ ☢ ☣ ⚠ ⚡ ✂ ✈ ✉ ✎ ✏ ⌘ ⌥ ⇧ ⌫ ⏎ ⎋ ⌨ ℮ ∴ ∵"),
    ];

    readonly string _path;
    readonly string _usagePath;
    readonly List<string> _pinned = [];
    readonly Dictionary<string, int> _uses = []; // сколько раз символ вставили: частые идут первыми в своей группе
    bool _usageChanged, _byUse = true;

    /// <summary>Частые первыми. Выключено: обычный порядок, но счёт идёт, и включённая снова сортировка знает привычки.</summary>
    public bool ByUse { get => _byUse; set { if (_byUse != value) { _byUse = value; _usageChanged = true; } } }

    /// <summary>Плоский список для ListBox с группировкой: сначала закреплённые, потом категории.</summary>
    public ObservableCollection<SymbolItem> Items { get; } = new();

    public SymbolCatalog(string path)
    {
        _path = path;
        _usagePath = Path.ChangeExtension(path, ".usage.json");
        try
        {
            if (File.Exists(path)) _pinned.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []);
        }
        catch (JsonException) { } // битый файл закреплённых — не повод не запускаться; перезапишется при следующем закреплении
        try
        {
            if (File.Exists(_usagePath)) _uses = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_usagePath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { } // без счётчиков порядок обычный
        Rebuild();
    }

    public bool IsPinned(string ch) => _pinned.Contains(ch);

    public void TogglePin(string ch)
    {
        if (!_pinned.Remove(ch)) _pinned.Add(ch);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_pinned));
        Rebuild();
    }

    /// <summary>Символ вставили или скопировали. Порядок меняет Reorder, чтобы клетки не прыгали под мышью.</summary>
    public void Use(string ch)
    {
        // shortcut: счётчики не стареют; если привычки сменятся и старые символы будут мешать — ввести затухание.
        _uses[ch] = _uses.GetValueOrDefault(ch) + 1;
        _usageChanged = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_usagePath)!);
            File.WriteAllText(_usagePath, JsonSerializer.Serialize(_uses));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // счётчик не должен мешать вставке
    }

    /// <summary>Двигает только изменившиеся символы: пересборка всей сетки задержала бы следующий показ.</summary>
    public void Reorder()
    {
        if (!_usageChanged) return;
        _usageChanged = false;
        var want = Ordered().ToList();
        // Частые встают в начало группы, а возвращаются вглубь: идём с того края, где двигать придётся только их.
        for (int n = 0; n < want.Count; n++)
        {
            int i = _byUse ? n : want.Count - 1 - n;
            if (Items[i] != want[i]) Items.Move(Items.IndexOf(want[i]), i);
        }
    }

    void Rebuild()
    {
        Items.Clear();
        foreach (var item in Ordered()) Items.Add(item);
    }

    IEnumerable<SymbolItem> Ordered()
    {
        foreach (var ch in _pinned) yield return new SymbolItem(ch, PinnedGroup, true);
        foreach (var (group, chars) in Groups)
            foreach (var ch in chars.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(c => _byUse ? _uses.GetValueOrDefault(c) : 0))
                yield return new SymbolItem(ch, group, _pinned.Contains(ch));
    }

    /// <summary>Поиск: сам символ, буква без диакритики (c → ć, č), код (2014, U+2014) или название группы («валют»).</summary>
    public static bool Matches(SymbolItem s, string q)
    {
        if (q.Length == 0) return true;
        var hex = q.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? q[2..] : q;
        // Одна буква есть почти в каждом названии группы и завалила бы найденные буквы; иероглиф же значим и один.
        bool groupQuery = q.Length > 1 || char.GetUnicodeCategory(q[0]) == UnicodeCategory.OtherLetter;
        return s.Char == q
            || s.Base.StartsWith(q, StringComparison.OrdinalIgnoreCase)
            || s.Char.EnumerateRunes().Any(r => r.Value.ToString("X4").Equals(hex, StringComparison.OrdinalIgnoreCase))
            || groupQuery && s.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }
}
