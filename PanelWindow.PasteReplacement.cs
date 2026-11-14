using System.Windows;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    bool _loadingPasteReplacement;
    internal event Action<PasteReplacement>? PasteReplacementChanged;

    internal PasteReplacement PasteReplacementRule
    {
        get => new(PasteReplacementToggle.IsChecked == true, PasteReplacementFrom.Text, PasteReplacementTo.Text);
        set
        {
            if (value.From.Length > PasteReplacement.MaxRuleLength || value.To.Length > PasteReplacement.MaxRuleLength)
                value = new();
            _loadingPasteReplacement = true;
            PasteReplacementFrom.Text = value.From;
            PasteReplacementTo.Text = value.To;
            PasteReplacementToggle.IsChecked = value.Enabled;
            _loadingPasteReplacement = false;
        }
    }

    void PasteReplacement_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingPasteReplacement && PasteReplacementFrom != null && PasteReplacementTo != null)
            PasteReplacementChanged?.Invoke(PasteReplacementRule);
    }
}
