using System.Windows;
using System.Windows.Controls;

namespace ArkCopyBuffer;

/// <summary>Подпись настройки: название и, если нужно, одна поясняющая фраза под ним.</summary>
sealed class SettingText : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingText), new PropertyMetadata("", (d, _) => ((SettingText)d).Fill()));
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SettingText), new PropertyMetadata("", (d, _) => ((SettingText)d).Fill()));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    bool _strong;
    /// <summary>Подпись служит заголовком раздела.</summary>
    public bool Strong { get => _strong; set { _strong = value; Fill(); } }

    public SettingText() => VerticalAlignment = VerticalAlignment.Center;

    void Fill()
    {
        Children.Clear();
        Children.Add(new TextBlock { Text = Title, TextWrapping = TextWrapping.Wrap, FontWeight = Strong ? FontWeights.SemiBold : FontWeights.Normal });
        if (Hint.Length == 0) return;
        var hint = new TextBlock { Text = Hint, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Children.Add(hint);
    }
}

/// <summary>Строка настройки: подпись слева, управление справа.</summary>
sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public SettingRow() => Focusable = false;
}
