using System.Windows;
using System.Windows.Controls;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    public event Action<string>? LanguageChanged;
    string _preferredLanguage = UiText.Language;

    void InitializeLanguage()
    {
        LanguageRestartHint.Text = UiText.T("Язык изменится после перезапуска.");
        ShowLanguageChoice(_preferredLanguage);
    }

    void ShowLanguageChoice(string language)
    {
        EnglishLanguage.Style = (Style)FindResource(language == "en" ? "AccentButton" : "StandardButton");
        RussianLanguage.Style = (Style)FindResource(language == "ru" ? "AccentButton" : "StandardButton");
        System.Windows.Automation.AutomationProperties.SetItemStatus(EnglishLanguage, language == "en" ? UiText.T("Выбрано") : "");
        System.Windows.Automation.AutomationProperties.SetItemStatus(RussianLanguage, language == "ru" ? UiText.T("Выбрано") : "");
    }

    void Language_Click(object sender, RoutedEventArgs e)
    {
        string language = (string)((Button)sender).Tag;
        if (language == _preferredLanguage) return;
        LanguageChanged?.Invoke(language);
    }

    public void SetLanguagePreference(string language)
    {
        _preferredLanguage = language;
        ShowLanguageChoice(language);
    }
}
