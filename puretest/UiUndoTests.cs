// T-178 U2: click test of the undo card body on the RecordPainter.
using System;

namespace VoiceBotBridge
{
    internal static class UiUndoTests
    {
        public static string Run()
        {
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "undo@" + s + ": ";
                var c = new WidgetCtx(s, 260f);
                int undone = 0; string res = null; double left = 8.2;
                var body = UiUndoCard.Body("Остановлено: 3 группы ПЛО", () => left, () => { undone++; res = "Возвращено 2 из 3; Hawk 21 — в ПВО-2"; }, () => res);
                c.Frame(body);
                var btn = c.P.Find("undo.btn");
                if (btn == null || btn.Text != "↶ Вернуть" || !btn.Enabled || string.IsNullOrEmpty(btn.Tip)) return tag + "the Undo button with a tip";
                if (c.P.FindText("Остановлено: 3 группы ПЛО") == null) return tag + "title";
                if (c.P.FindText("осталось 9 с") == null) return tag + "seconds left (rounded up): " + (c.P.Of("Label").Count);
                if (c.P.CheckLayout(c.Width, 10000) != null) return tag + "layout: " + c.P.CheckLayout(c.Width, 10000);
                if (c.P.Lint(c.T, true).Count != 0) return tag + "lint: " + string.Join("; ", c.P.Lint(c.T, true));
                c.P.Click("undo.btn"); c.Frame(body);
                if (undone != 1) return tag + "the press calls onUndo once";
                if (c.P.FindText("Возвращено 2 из 3; Hawk 21 — в ПВО-2") == null) return tag + "result line after the press";
                c.Frame(body);
                if (c.P.Find("undo.btn") != null) return tag + "no button after the press";
                c.P.Click("undo.btn"); c.Frame(body); c.Frame(body);
                if (undone != 1) return tag + "never a second restore";
                if (c.P.CheckLayout(c.Width, 10000) != null) return tag + "layout after: " + c.P.CheckLayout(c.Width, 10000);
            }
            return Banner();
        }

        // T-199 B1 (ADR-0084): the strip in the window body instead of a card of the stack: 10 s before the press, 6 s of the result after it
        private static string Banner()
        {
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "banner@" + s + ": ";
                var c = new WidgetCtx(s, 260f);
                int undone = 0; string res = null; double t0 = 100, now = 100;
                var b = new UiUndoBanner();
                Func<double> left = () => Math.Max(0, 10 - (now - t0));
                Action<Action<UiKit>> frame = body => c.Frame(body);
                if (b.Visible(now)) return tag + "visible before Show";
                b.Show("Остановлено: 3 группы ПЛО", left, () => { undone++; res = "Возвращено 2 из 3"; }, () => res, now);
                if (!b.Visible(now)) return tag + "not visible after Show";
                c.Frame(k => b.Draw(k, now));
                if (c.P.Find("undo.btn") == null || c.P.FindText("Остановлено: 3 группы ПЛО") == null) return tag + "title and button";
                if (c.P.CheckLayout(c.Width, 10000) != null) return tag + "layout: " + c.P.CheckLayout(c.Width, 10000);
                if (c.P.Lint(c.T, true).Count != 0) return tag + "lint: " + string.Join("; ", c.P.Lint(c.T, true));
                now = 109.5; if (!b.Visible(now)) return tag + "must be visible at 9.5 s";
                now = 110.5; if (b.Visible(now)) return tag + "must be hidden after 10 s";
                c.Frame(k => b.Draw(k, now));
                if (c.P.Find("undo.btn") != null) return tag + "drawn while hidden";
                // press
                now = 102; b.Show("Второе", left, () => { undone++; res = "Возвращено 1 из 1"; }, () => res, now); res = null; t0 = 102;
                c.Frame(k => b.Draw(k, now));
                if (c.P.FindText("Второе") == null) return tag + "second Show must replace the title";
                if (c.P.FindText("Остановлено: 3 группы ПЛО") != null) return tag + "old title stays";
                c.P.Click("undo.btn"); c.Frame(k => b.Draw(k, now));
                if (undone != 1) return tag + "press calls onUndo once";
                c.Frame(k => b.Draw(k, now));
                if (c.P.FindText("Возвращено 1 из 1") == null) return tag + "result line";
                now = 107; if (!b.Visible(now)) return tag + "result must be visible 5 s after the press";
                now = 108.5; if (b.Visible(now)) return tag + "result must be hidden after " + UiUndoBanner.ResultS + " s";
                // a result made without the button (dev command ui_undo) starts the 6 s too
                now = 120; t0 = 120; res = null;
                b.Show("Третье", left, () => { }, () => res, now);
                res = "готово"; if (!b.Visible(now)) return tag + "visible with result";
                now = 127; if (b.Visible(now)) return tag + "result of PressUndo must hide after 6 s";
                b.Show("Четвёртое", left, () => { }, () => null, now = 130); t0 = 130;
                b.Hide(); if (b.Visible(now)) return tag + "Hide";
            }
            return null;
        }
    }
}
