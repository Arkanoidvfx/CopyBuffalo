using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ArkCopyBuffer;

internal sealed partial class HistoryMapWindow
{
    readonly UniformGrid monthDays = new() { Columns = 7 };
    readonly TextBlock monthTitle = new() { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    DateTime month = DateTime.Today;
    string KindLabel(string kind) => T(kind switch { "image" => "Картинки", "video" => "Видео", "audio" => "Аудио", "files" => "Файлы", "folder" => "Папки", "graph" => "Схемы", "text" => "Текст", _ => "Другое" });
    Button SelectorButton(ComboBox model, string name)
    {
        var button = Button(name, () => { });
        string Caption(string value, int index) => index == 0 ? value : model == kinds ? KindLabel(value) : model == sources ? SourceLabel(value) : value;
        model.SelectionChanged += (_, _) => button.Content = model.SelectedIndex < 0 ? T(name) : Caption((string)model.SelectedItem, model.SelectedIndex);
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (string value in model.Items)
            {
                var item = new MenuItem { Header = Caption(value, model.Items.IndexOf(value)), IsCheckable = true, IsChecked = Equals(model.SelectedItem, value) };
                item.Click += (_, _) => model.SelectedItem = value; menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return button;
    }
    void StyleResults()
    {
        results.SetResourceReference(Control.BackgroundProperty, "WindowSurface"); results.SetResourceReference(Control.ForegroundProperty, "TextPrimary"); results.BorderThickness = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        results.ItemContainerStyle = new Style(typeof(ListBoxItem), (Style)FindResource("ClipItemContainer"));
        results.ItemContainerStyle.Setters.Add(new Setter(Control.FocusableProperty, true));
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MapEntry.Preview))); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(TextBlock.LineHeightProperty, 20.0); text.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight); text.SetValue(FrameworkElement.MaxHeightProperty, 60.0); template.VisualTree = text;
        var typography = new Style(typeof(TextBlock));
        var mono = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(MapEntry.Mono)), Value = true };
        mono.Setters.Add(new Setter(Control.FontFamilyProperty, FindResource("MonoFont"))); typography.Triggers.Add(mono);
        text.SetValue(FrameworkElement.StyleProperty, typography);
        results.DisplayMemberPath = ""; results.ItemTemplate = template;
    }
    void BuildCalendar()
    {
        var header = new DockPanel();
        var previous = Button("Предыдущий месяц", () => { month = month.AddMonths(-1); DrawCalendar(); });
        var next = Button("Следующий месяц", () => { month = month.AddMonths(1); DrawCalendar(); });
        previous.ToolTip = previous.Content; next.ToolTip = next.Content;
        previous.Content = new TextBlock { Text = "\uE76B", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 12 };
        next.Content = new TextBlock { Text = "\uE76C", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 12 };
        DockPanel.SetDock(previous, Dock.Left); DockPanel.SetDock(next, Dock.Right);
        header.Children.Add(previous); header.Children.Add(next); header.Children.Add(monthTitle);
        rail.Children.Add(header); rail.Children.Add(monthDays);
    }
    void DrawCalendar()
    {
        monthDays.Children.Clear();
        var culture = CultureInfo.GetCultureInfo(UiText.Language);
        monthTitle.Text = month.ToString("MMMM yyyy", culture);
        var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++) monthDays.Children.Add(new TextBlock { Text = culture.DateTimeFormat.ShortestDayNames[((int)firstDay + i) % 7], TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0, 8, 0, 8) });
        var start = new DateTime(month.Year, month.Month, 1);
        start = start.AddDays(-((7 + (int)start.DayOfWeek - (int)firstDay) % 7));
        var counts = snapshot.Where(e => !invalid.Contains(e.Id)).GroupBy(e => e.Date(First).Date).ToDictionary(g => g.Key, g => g.Count());
        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i); counts.TryGetValue(day, out int total);
            var cell = new Button { Content = day.Day.ToString(), Style = (Style)FindResource("StandardButton"), Focusable = true, Padding = new Thickness(0), MinWidth = 24, Height = 28, Margin = new Thickness(1), Opacity = day.Month == month.Month ? 1 : .45, FontWeight = total > 0 ? FontWeights.Bold : FontWeights.Normal };
            cell.ToolTip = day.ToString("D", culture) + " · " + UiText.F($"Записей: {total}"); AutomationProperties.SetName(cell, (string)cell.ToolTip);
            if (calendar.SelectedDates.Contains(day)) { cell.Background = Brush("Accent"); cell.Foreground = Brush("TextOnAccent"); }
            else if (total > 0) { cell.BorderBrush = Brush("Accent"); cell.BorderThickness = new Thickness(0, 0, 0, 2); }
            cell.Click += (_, _) => { var anchor = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && calendar.SelectedDates.Count > 0 ? calendar.SelectedDates[0] : day; calendar.SelectedDates.Clear(); calendar.SelectedDates.AddRange(anchor < day ? anchor : day, anchor > day ? anchor : day); };
            monthDays.Children.Add(cell);
        }
    }
}
