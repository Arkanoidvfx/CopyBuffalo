using System.IO;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    internal static void SelfTestAnimation(string directory, Action<bool, string> check)
    {
        var panel = new PanelWindow(new HistoryStore(Path.Combine(directory, "animation-history.dat")),
            new SymbolCatalog(Path.Combine(directory, "animation-symbols.json")));
        try
        {
            int calls = 0;
            panel.AfterFrames(2, () => calls++);
            panel.AdvanceAnimationFrame(TimeSpan.Zero);
            panel.AdvanceAnimationFrame(TimeSpan.Zero);
            check(calls == 0, "animation: repeated Rendering for one frame cannot finish warmup");
            panel.AdvanceAnimationFrame(TimeSpan.FromMilliseconds(180));
            panel.AdvanceAnimationFrame(TimeSpan.FromMilliseconds(196));
            check(calls == 1 && panel._afterAnimationFrames == null,
                "animation: slow first frame completes before one-shot entrance callback");

            panel.AfterFrames(2, () => calls += 100);
            panel.AdvanceAnimationFrame(TimeSpan.Zero);
            panel.AfterFrames(2, () => calls++);
            panel.AdvanceAnimationFrame(TimeSpan.Zero);
            check(calls == 1, "animation: new transition starts its own frame wait");
            panel.AdvanceAnimationFrame(TimeSpan.FromMilliseconds(16));
            check(calls == 2, "animation: superseded transition never runs");

            panel.AfterFrames(2, () => calls += 100);
            panel.CancelFrameWait();
            panel.AdvanceAnimationFrame(TimeSpan.Zero);
            panel.AdvanceAnimationFrame(TimeSpan.FromMilliseconds(16));
            check(calls == 2, "animation: cancelled entrance cannot restart during dismissal");
            panel.AfterFrames(2, () => calls += 100);
        }
        finally { panel.Close(); }
        check(panel._afterAnimationFrames == null, "animation: closing releases the static Rendering subscription");
    }
}
