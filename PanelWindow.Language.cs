using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Automation;

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
        LanguageRestartHint.Text = UiText.T("Язык изменится после перезапуска.");
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
        LanguageChanged?.Invoke(language);
    }

    public void SetLanguagePreference(string language)
    {
        if (!UiText.Languages.Contains(language)) throw new ArgumentOutOfRangeException(nameof(language));
        _preferredLanguage = language;
        ShowLanguageChoice(language);
    }
}
