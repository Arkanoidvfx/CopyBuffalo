using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace ArkCopyBuffer;

public sealed record SymbolItem(string Char, string Group, bool Pinned)
{
    public string Code => string.Join(" ", Char.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));
}

/// <summary>
/// Вкладка «Символы»: набор как у Win+V плюс закреплённые пользователем — группой сверху.
/// Закреплённые символы не секрет, поэтому обычный JSON рядом с историей.
/// </summary>
sealed class SymbolCatalog
{
    public const string PinnedGroup = "Закреплённые";

    static readonly (string group, string chars)[] Groups =
    [
        ("Пунктуация", "« » „ “ ” ‘ ’ ‚ … — – ‐ • · ‹ › ¡ ¿ § ¶ † ‡ ‰ ′ ″ ‼ ‽ ※ © ® ™ ℗ № ° ¹ ² ³ ⁰ ⁴ ⁵ ⁶ ⁷ ⁸ ⁹ ₀ ₁ ₂ ₃ ¦ ¨ ¯ ´ ¸ ˜"),
        ("Валюты", "₽ $ € £ ¥ ¢ ₴ ₸ ₹ ₩ ₪ ₫ ₺ ₼ ₾ ₿ ƒ ¤ ₱ ₲ ₵ ₡ ₦ ₭ ₮ ₣ ₤ ₨ ₳"),
        ("Стрелки", "← ↑ → ↓ ↔ ↕ ↖ ↗ ↘ ↙ ⇐ ⇑ ⇒ ⇓ ⇔ ⇕ ↩ ↪ ↺ ↻ ⟵ ⟶ ⟷ ➜ ➔ ➤ ⤴ ⤵ ⇄ ⇆"),
        ("Математика", "± ∓ × ÷ − ≠ ≈ ≡ ≤ ≥ ≪ ≫ √ ∛ ∞ ∑ ∏ ∫ ∂ ∆ ∇ ∈ ∉ ∩ ∪ ⊂ ⊃ ⊆ ⊇ ∅ ∀ ∃ ¬ ∧ ∨ ⊕ ⊗ ∝ ∠ ⊥ ∥ ½ ⅓ ⅔ ¼ ¾ ⅛ π µ ℓ ℃ ℉"),
        ("Греческий", "Α Β Γ Δ Ε Ζ Η Θ Ι Κ Λ Μ Ν Ξ Ο Π Ρ Σ Τ Υ Φ Χ Ψ Ω α β γ δ ε ζ η θ ι κ λ μ ν ξ ο π ρ σ ς τ υ φ χ ψ ω"),
        ("Латиница", "À Á Â Ã Ä Å Æ Ç È É Ê Ë Ì Í Î Ï Ñ Ò Ó Ô Õ Ö Ø Ù Ú Û Ü Ý ß à á â ã ä å æ ç è é ê ë ì í î ï ñ ò ó ô õ ö ø ù ú û ü ý ÿ Œ œ Š š Ž ž Č č Ć ć Đ đ Ł ł Ń ń Ś ś Ź ź Ż ż Ğ ğ İ ı Ş ş"),
        ("Кириллица", "Ё ё Ї ї І і Є є Ґ ґ Ў ў Ј ј Љ љ Њ њ Ћ ћ Џ џ Ђ ђ Ѓ ѓ Ќ ќ"),
        ("Фигуры", "■ □ ▪ ▫ ▬ ▲ △ ▶ ▷ ▼ ▽ ◀ ◁ ◆ ◇ ○ ◎ ● ◐ ◑ ★ ☆ ♠ ♣ ♥ ♦ ♤ ♧ ♡ ♢ ✓ ✔ ✕ ✖ ✗ ✘ ☐ ☑ ☒ ✦ ✧ ❖"),
        ("Прочее", "♪ ♫ ☀ ☁ ☂ ☃ ☎ ☕ ☘ ☯ ☮ ☢ ☣ ⚠ ⚡ ✂ ✈ ✉ ✎ ✏ ⌘ ⌥ ⇧ ⌫ ⏎ ⎋ ⌨ ℮ ∴ ∵"),
    ];

    readonly string _path;
    readonly List<string> _pinned = [];

    /// <summary>Плоский список для ListBox с группировкой: сначала закреплённые, потом категории.</summary>
    public ObservableCollection<SymbolItem> Items { get; } = new();

    public SymbolCatalog(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _pinned.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []);
        }
        catch (JsonException) { } // битый файл закреплённых — не повод не запускаться; перезапишется при следующем закреплении
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

    void Rebuild()
    {
        Items.Clear();
        foreach (var ch in _pinned) Items.Add(new SymbolItem(ch, PinnedGroup, true));
        foreach (var (group, chars) in Groups)
            foreach (var ch in chars.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Items.Add(new SymbolItem(ch, group, _pinned.Contains(ch)));
    }

    /// <summary>Поиск: сам символ, его код (2014, U+2014) или название группы («валют»).</summary>
    public static bool Matches(SymbolItem s, string q)
    {
        if (q.Length == 0) return true;
        var hex = q.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? q[2..] : q;
        return s.Char == q
            || s.Char.EnumerateRunes().Any(r => r.Value.ToString("X4").Equals(hex, StringComparison.OrdinalIgnoreCase))
            || s.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }
}
