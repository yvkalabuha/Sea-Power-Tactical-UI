// T-178 Kd1: tests of RecordPainter.Lint (the layout rules of ADR-0077 §2.6).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class UiLintTests
    {
        public static string Run()
        {
            foreach (double s in new[] { 1.0, 0.667, 1.33 })
            {
                string tag = "lint@" + s + ": ";
                // a good layout: tips everywhere, a grey one with the reason, an allowed field
                var g = new WidgetCtx(s, 320f);
                Action<UiKit> good = k =>
                {
                    k.Row("Имя"); k.Number("name", "ПЛО-1", 8);
                    k.Row("Режим"); k.Segments("m", new[] { "A", "B" }, 0, new[] { "a", "b" });
                    k.Row("Число"); k.Dropdown("n", new[] { "1", "2" }, 0, "Число бортов");
                    k.Row("Порог"); k.Slider("th", 0, 100, 5, 50, v => v + "%", "Порог", 50);
                    k.Row("Пеленг"); k.Compass("c", 90, null, "Пеленг");
                    k.Row("Тактика"); bool[] on = { true, false }; k.Toggles("tg", new[] { "Авто", "ГАС" }, on, new[] { "a", "b" }, null, 0);
                    k.Footer(new UiFooter { Stop = new UiAction("stop", "Стоп", "Остановить"), Primary = new UiAction("go", "Начать", null, false), Reason = "Нужен борт" });
                    k.StatusChips("st", new[] { new UiChip { Text = "нет приказа", Glyph = "■" } });
                };
                g.Frame(good); g.Frame(good);
                var errs = g.P.Lint(g.T, true, new[] { "name" });
                if (errs.Count != 0) return tag + "a good layout has no errors: " + string.Join("; ", errs);
                // a deliberately bad one
                var b = new WidgetCtx(s, 320f);
                Action<UiKit> bad = k =>
                {
                    k.Button("nt", "Без подсказки");
                    k.Button("grey", "Серая", null, false);
                    k.Number("hours", "5");
                    k.IconButton("a", "+", "t"); k.IconButton("b", "−", "t");
                };
                b.Frame(bad); b.Frame(bad);
                var hits = new Dictionary<string, UiRect> { { "p", new UiRect(0, 0, 40, 18) }, { "q", new UiRect(30, 0, 40, 18) } };
                b.P.SetHitRects(hits);
                var e2 = b.P.Lint(b.T, true, new[] { "name" });
                bool tip = e2.Exists(x => x == "no tip: nt"), grey = e2.Exists(x => x.StartsWith("grey without a reason tip: grey")), fld = e2.Exists(x => x.Contains("hours")), ov = e2.Exists(x => x.StartsWith("hit zones overlap"));
                if (!(tip && grey && fld && ov) || e2.Count != 4) return tag + "a bad layout: tip/grey/field/overlap in " + e2.Count + " errors: " + string.Join("; ", e2);
                // a control lower than 15 px
                var low = new RecordPainter(s);
                low.NextFrame(); low.Button(new UiRect(0, 0, 40, 12), "tiny", "x", "tip", true, false);
                var e3 = low.Lint(b.T, true);
                if (e3.Count != 1 || !e3[0].StartsWith("height")) return tag + "height below 15 px: " + string.Join("; ", e3);
                // the real controls are never lower than 15 px (the floor 0.85 of UiScale)
                foreach (var it in g.P.Items) if ((it.Kind == "Button" || it.Kind == "Check") && it.R.H + 0.01f < 15f) return tag + "control lower than 15 px: " + it;
                // requireTips = false skips the tip rules only
                if (b.P.Lint(b.T, false, new[] { "name" }).Count != 2) return tag + "requireTips=false keeps the field and the zones";
            }
            return null;
        }
    }
}
