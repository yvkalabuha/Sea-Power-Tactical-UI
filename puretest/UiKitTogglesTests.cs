// T-178 Kb1: click tests of UiKit.Toggles (chips).
using System;

namespace VoiceBotBridge
{
    internal static class UiKitTogglesTests
    {
        public static string Run()
        {
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "toggles@" + s + ": ";
                var c = new WidgetCtx(s, 320f);
                var on = new[] { true, false, false, false };
                bool changed = false;
                var labels = new[] { "Авто", "ГАС", "Коридор", "Барьер" };
                var en = new[] { true, true, true, false };
                var tips = new[] { "a", "b", "c", "Нет буёв ни у одного борта" };
                Action<UiKit> body = k => { changed = k.Toggles("tg", labels, on, tips, en, 0); };
                c.Frame(body);
                if (!c.P.Find("tg.0").Selected || !c.P.Find("tg.0").Text.StartsWith("■")) return tag + "lit chip: selected and marked";
                int acc = 0; foreach (var f in c.P.Of("Fill")) if (f.Id == "Accent") acc++;
                if (acc != 4) return tag + "lit chip: one Accent outline of four fills";
                if (c.P.CheckLayout(c.Width, 10000) != null) return tag + "layout: " + c.P.CheckLayout(c.Width, 10000);
                c.P.Click("tg.1"); c.Frame(body);
                if (!changed || on[0] || !on[1]) return tag + "another chip clears Auto";
                c.P.Click("tg.2"); c.Frame(body);
                if (!on[1] || !on[2]) return tag + "chips are independent";
                c.P.Click("tg.0"); c.Frame(body);
                if (!on[0] || on[1] || on[2]) return tag + "Auto clears the others";
                c.P.Click("tg.1"); c.Frame(body); c.P.Click("tg.1"); c.Frame(body);
                if (!on[0] || on[1]) return tag + "clearing the last one lights Auto";
                c.P.Click("tg.3");
                c.Frame(body);
                if (on[3] || changed || c.K.DisabledClick != "Нет буёв ни у одного борта") return tag + "grey chip: not toggled, reason given";
                if (c.P.Find("tg.3").Enabled) return tag + "grey chip is disabled";
            }
            return null;
        }
    }
}
