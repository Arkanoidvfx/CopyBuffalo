using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    public event Action<string>? LanguageChanged;
    string _preferredLanguage = UiText.Language;
    // Задаются в InitializeLanguage; проверки подменяют модальный вопрос: без человека он ждал бы вечно.
    internal Func<bool> ConfirmLanguageRestart = null!;
    internal Func<int, bool> ConfirmClear = null!;

    void InitializeLanguage()
    {
        ConfirmLanguageRestart = ConfirmRestart;
        ConfirmClear = count => Confirm(UiText.F($"Удалить {count} {Records(count)}?"), UiText.T("Закреплённые останутся."), UiText.T("Очистить"), enterAgrees: false);
        foreach (string language in UiText.Languages)
        {
            var choice = new Button { Content = UiText.NativeName(CultureInfo.GetCultureInfo(language)), Tag = language, Width = 128, Margin = new Thickness(4, 0, 4, 8) };
            choice.Click += Language_Click;
            AutomationProperties.SetName(choice, choice.Content.ToString());
            LanguageChoices.Children.Add(choice);
        }
        ShowLanguageChoice(_preferredLanguage);
    }

    void ShowLanguageChoice(string language)
    {
        foreach (var choice in LanguageChoices.Children.OfType<Button>())
        {
            bool selected = (string)choice.Tag == language;
            choice.Style = (Style)FindResource(selected ? "AccentButton" : "StandardButton");
            AutomationProperties.SetItemStatus(choice, selected ? UiText.T("Выбрано") : "");
        }
    }

    void Language_Click(object sender, RoutedEventArgs e)
    {
        string language = (string)((Button)sender).Tag;
        if (language == _preferredLanguage) return;
        if (ConfirmLanguageRestart()) LanguageChanged?.Invoke(language);
    }

    bool ConfirmRestart() => Confirm("CopyBuffalo", UiText.T("Перезапустить CopyBuffalo сейчас, чтобы сменить язык?"), UiText.T("Перезапустить"), enterAgrees: true);

    /// <summary>Отдельное окно-вопрос в теме приложения вместо системного MessageBox; фокус на «Отмена», Esc — отмена.</summary>
    bool Confirm(string title, string message, string agree, bool enterAgrees)
    {
        var yes = new Button { Content = agree, Style = (Style)FindResource("AccentButton"), MinWidth = 110, IsDefault = enterAgrees };
        var cancel = new Button { Content = UiText.T("Отмена"), Style = (Style)FindResource("StandardButton"), MinWidth = 110,
            Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var question = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18) };
        question.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var card = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(20, 16, 20, 16), Margin = new Thickness(ShadowMargin),
            MinWidth = 300, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    question,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, cancel } },
                },
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "MenuSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "WindowStroke");
        var dialog = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 420 + 2 * ShadowMargin, Topmost = true, ShowInTaskbar = false,
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, FontFamily = FontFamily, FontSize = FontSize, Content = card,
        };
        if (IsVisible) dialog.Owner = this; // вопрос появляется над панелью, а не посреди другого экрана
        dialog.SetResourceReference(ForegroundProperty, "TextPrimary");
        AutomationProperties.SetName(dialog, title + " " + message);
        yes.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            cancel.Focus();
            var scale = Motion.Scale(card);
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Slow, from: 1.05);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Slow, from: 1.05);
            Motion.To(card, OpacityProperty, 1, Motion.Slow, from: 0);
        };
        _menuOpen = true; // панель не прячется, пока открыт вопрос
        _outsideTimer.Stop();
        try { return dialog.ShowDialog() == true; }
        finally { _menuOpen = false; StartOutsideTimer(); }
    }

    public void SetLanguagePreference(string language)
    {
        if (!UiText.Languages.Contains(language)) throw new ArgumentOutOfRangeException(nameof(language));
        _preferredLanguage = language;
        ShowLanguageChoice(language);
    }
}
