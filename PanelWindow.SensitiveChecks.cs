using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    internal static void SelfTestSensitive(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "passwords", "history.dat");
        var store = new HistoryStore(path);
        const string secret = "https://example.invalid/private-password";
        var now = DateTime.UtcNow;
        store.Add(secret, "old rtf", "old html", now);
        var item = store.Items[0];
        _ = item.Kind; _ = item.TileTitle;
        store.Add(secret, null, null, now.AddSeconds(1), sensitive: true);
        check(store.Items.Count == 1 && item.IsSensitive && item.Kind == "text"
            && item.Preview == "********" && item.TileTitle == "********" && item.TileSub == ""
            && !item.IsMapLink && !item.IsNodeGraph && !item.IsMono && item.Text == secret,
            "password metadata masks every card field without changing the paste text");
        store.Add(secret, "new rich text", "new html", now.AddSeconds(2));
        check(item.IsSensitive && item.Rtf == null && item.Html == null,
            "ordinary recopy cannot remove sensitivity or restore revealing rich formats");
        foreach (var text in new[] { "#aabbcc", "Begin Object Class=/Script/MaterialGraphNode\nCustomProperties Pin" })
        {
            var typed = new ClipItem { Text = text, IsSensitive = true };
            check(typed.Kind == "text" && typed.Preview == "********" && typed.Swatch == null && typed.GraphPreview == null,
                "sensitive values never reveal color or graph previews");
        }
        store.Save();
        var longSecret = new ClipItem { Text = new string('x', 10000), IsSensitive = true };
        check(longSecret.CardText.Length == 600 && longSecret.Text.Length == 10000 && longSecret.Preview == "********",
            "inline password display keeps the existing card length bound without truncating paste data");
        var loaded = new HistoryStore(path);
        loaded.Load(now.AddSeconds(3));
        check(loaded.Items[0].IsSensitive && loaded.Items[0].Text == secret && loaded.Items[0].Preview == "********"
            && File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0,
            "encrypted history roundtrip retains the password flag and original secret");
        var clipboard = Paster.TextClipboard(secret, sensitive: true);
        check(clipboard.GetData(DataFormats.UnicodeText) is string copied && copied == secret && ClipboardWatcher.Sensitive(clipboard)
            && clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream history
            && BitConverter.ToInt32(history.ToArray(), 0) == 0
            && clipboard.GetData("CanUploadToCloudClipboard") is MemoryStream cloud
            && BitConverter.ToInt32(cloud.ToArray(), 0) == 0,
            "password copy preserves text and prevents Windows history/cloud capture");
        long tick = Stopwatch.GetTimestamp();
        check(!SensitivePeekReady(0) && !SensitivePeekReady(tick)
            && !SensitivePeekReady(tick - Stopwatch.Frequency)
            && SensitivePeekReady(tick - Stopwatch.Frequency * 3),
            "password preview requires a full two-second monotonic hover delay");
        var spaced = new HistoryStore(Path.Combine(directory, "password-spaces", "history.dat"));
        foreach (var password in new[] { "pass", " pass", "pass ", " " }) spaced.Add(password, null, null, now, sensitive: true);
        spaced.Add("pass", null, null, now.AddSeconds(1));
        spaced.Save();
        var spacedLoaded = new HistoryStore(Path.Combine(directory, "password-spaces", "history.dat"));
        spacedLoaded.Load(now.AddSeconds(2));
        check(spacedLoaded.Items.Count == 4 && spacedLoaded.Items.All(i => i.IsSensitive)
            && spacedLoaded.Items.Any(i => i.Text == " "), "password deduplication and reload preserve significant leading/trailing spaces");
        check(!ClipboardWatcher.Sensitive(Paster.TextClipboard("ordinary text"))
            && new ClipItem { Text = "ordinary text" }.Preview == "ordinary text", "unmarked ordinary text remains unmasked");

        var panel = new PanelWindow(store, new SymbolCatalog(Path.Combine(directory, "passwords", "symbols.json")));
        try
        {
            DataTemplate[] templates = [panel.List.ItemTemplate, (DataTemplate)panel.FindResource("PinTileTemplate"), (DataTemplate)panel.FindResource("PinRowTemplate")];
            foreach (var template in templates)
            {
                var card = new ListBoxItem { Content = item, DataContext = item, ContentTemplate = template };
                card.Measure(new Size(340, 160)); card.Arrange(new Rect(0, 0, 340, 160)); card.UpdateLayout();
                var texts = Blocks(card).Select(block => block.Text).ToArray();
                check(texts.Contains("********") && texts.All(text => !text.Contains(secret, StringComparison.Ordinal)),
                    "history/pinned template renders stars without exposing password text");
                SetSensitiveRevealed(card, true);
                card.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                check(Blocks(card).Any(block => block.Text == secret) && item.Preview == "********",
                    "hovered card reveals inline while the model and other views remain masked");
                panel._peekHover = card;
                panel._peekItem = item;
                panel.ClosePeek();
                card.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                check(!GetSensitiveRevealed(card) && !panel.TextPeek.IsOpen
                    && Blocks(card).All(block => !block.Text.Contains(secret, StringComparison.Ordinal)),
                    "leaving each card immediately restores stars without a separate popup");
            }
            panel._peekItem = item;
            panel._peekHover = new ListBoxItem { DataContext = item };
            panel._peekStartedAt = tick;
            panel.PeekText.Text = secret;
            panel._peekTimer.Start();
            panel.ClosePeek();
            check(panel.PeekText.Text == "" && !panel.TextPeek.IsOpen && panel._peekItem == null
                && panel._peekStartedAt == 0 && !panel._peekTimer.IsEnabled,
                "leaving a password clears plaintext and cancels the reveal timer immediately");
            var dragged = new DataObject(typeof(ClipItem), item);
            check(panel.ReadValue(dragged) == null && panel.ReadValue(clipboard) == null,
                "automatic substitution previews cannot expose password sources");
        }
        finally { panel.Close(); }

        static IEnumerable<TextBlock> Blocks(DependencyObject root)
        {
            if (root is TextBlock block) yield return block;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Blocks(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
    }
}
