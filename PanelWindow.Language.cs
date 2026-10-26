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

    void InitializeLanguage()
    {
        foreach (string language in UiText.Languages)
        {
            var choice = new Button { Content = CultureInfo.GetCultureInfo(language).NativeName, Tag = language, Margin = new Thickness(4, 0, 4, 6) };
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
        if (ConfirmRestart()) LanguageChanged?.Invoke(language);
    }

    /// <summary>Отдельное окно-уведомление в теме приложения вместо системного MessageBox; фокус на «Отмена», Esc — отмена.</summary>
    bool ConfirmRestart()
    {
        var restart = new Button { Content = UiText.T("Перезапустить"), Style = (Style)FindResource("AccentButton"), MinWidth = 110, IsDefault = true };
        var cancel = new Button { Content = UiText.T("Отмена"), Style = (Style)FindResource("StandardButton"), MinWidth = 110,
            Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var question = new TextBlock { Text = UiText.T("Перезапустить CopyBuffalo сейчас, чтобы сменить язык?"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18) };
        question.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var card = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(20, 16, 20, 16), Margin = new Thickness(ShadowMargin),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "CopyBuffalo", FontSize = 15, FontWeight = FontWeights.SemiBold },
                    question,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { restart, cancel } },
                },
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "MenuSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "WindowStroke");
        var dialog = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 420 + 2 * ShadowMargin, Topmost = true, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, FontFamily = FontFamily, FontSize = FontSize, Content = card,
        };
        dialog.SetResourceReference(ForegroundProperty, "TextPrimary");
        AutomationProperties.SetName(dialog, question.Text);
        restart.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => cancel.Focus();
        _menuOpen = true; // панель не прячется, пока открыт вопрос
        try { return dialog.ShowDialog() == true; }
        finally { _menuOpen = false; }
    }

    public void SetLanguagePreference(string language)
    {
        if (!UiText.Languages.Contains(language)) throw new ArgumentOutOfRangeException(nameof(language));
        _preferredLanguage = language;
        ShowLanguageChoice(language);
    }
}
