// T-178 Kc: tests of UiKit.StatusChips and UiKit.Footer.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class UiKitStatusTests
    {
        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        public static string Run()
        {
            _fail = null;
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "status@" + s + ": ";
                Chips(s, tag); Foot(s, tag);
                if (_fail != null) return _fail;
            }
            return _fail;
        }

        private static List<UiChip> Sample()
        {
            return new List<UiChip>
            {
                new UiChip { Glyph = "✔", Text = "действует с 10:12", Tone = UiTone.Ok },
                new UiChip { Glyph = "!", Text = "нет буёв", Tone = UiTone.Warn, Tip = "Нет буёв ни у одного борта" },
                new UiChip { Text = "цель Yorktown CG-48" },
                new UiChip { Text = "R 25 миль" },
                new UiChip { Text = "рубеж 40 миль" },
                new UiChip { Text = "ось 090°" },
                new UiChip { Text = "борты 3" },
                new UiChip { Text = "высота 500 фт" },
            };
        }

        private static void Chips(double s, string tag)
        {
            var c = new WidgetCtx(s, 300f);
            var chips = Sample();
            c.Frame(k => k.StatusChips("st", chips.GetRange(0, 3)));
            Eq(tag + "3 chips: all shown with glyphs, no +N", c.P.FindText("✔ действует с 10:12") != null && c.P.FindText("! нет буёв") != null && c.P.FindText("+1") == null);
            Eq(tag + "layout clean", c.P.CheckLayout(c.Width, 10000) == null);
            Eq(tag + "a chip with a tip keeps it", c.P.FindText("! нет буёв").Tip == "Нет буёв ни у одного борта");
            // many chips, 2 lines: the tail goes into +N, the warning stays
            var cn = new WidgetCtx(s, 200f);
            cn.Frame(k => k.StatusChips("st", chips, 2));
            var labels = cn.P.Of("Label");
            var ys = new HashSet<float>(); foreach (var l in labels) ys.Add(l.R.Y);
            Eq(tag + "at most 2 lines", ys.Count <= 2);
            Eq(tag + "the warning is never in +N", cn.P.FindText("! нет буёв") != null);
            Eq(tag + "the state chip is first", cn.P.FindText("✔ действует с 10:12") != null);
            var more = labels.Find(l => l.Text != null && l.Text.StartsWith("+"));
            Eq(tag + "+N chip with the full list in the tip", more != null && more.Tip.Contains("высота 500 фт") && more.Tip.Contains("нет буёв"));
            Eq(tag + "narrow: layout clean", cn.P.CheckLayout(cn.Width, 10000) == null);
            // only warnings: they never go away even beyond the line limit
            var warns = new List<UiChip>();
            warns.Add(new UiChip { Glyph = "●", Text = "изменено", Tone = UiTone.Accent });
            for (int i = 0; i < 6; i++) warns.Add(new UiChip { Glyph = "!", Text = "предупреждение номер " + i, Tone = UiTone.Warn });
            var cw = new WidgetCtx(s, 200f);
            cw.Frame(k => k.StatusChips("st", warns, 2));
            int shown = 0; foreach (var l in cw.P.Of("Label")) if (l.Text != null && l.Text.StartsWith("!")) shown++;
            Eq(tag + "6 warnings all stay visible", shown == 6 && cw.P.FindText("+1") == null);
            // a chip wider than the line is cut with the full text in the tip
            var cl = new WidgetCtx(s, 120f);
            cl.Frame(k => k.StatusChips("st", new[] { new UiChip { Text = "очень длинный текст чипа который не помещается в строку окна никак" } }, 2));
            var lg = cl.P.Of("Label")[0];
            Eq(tag + "a too wide chip is cut, the tip has the full text", lg.Truncated && lg.Tip != null && lg.Tip.Contains("никак") && cl.P.CheckLayout(cl.Width, 10000) == null);
        }

        private static void Foot(double s, string tag)
        {
            var c = new WidgetCtx(s, 320f);
            string pressed = null;
            Action<UiKit> body = k =>
            {
                pressed = k.Footer(new UiFooter
                {
                    Aux = new[] { new UiAction("aux", "Скрыть маршрут", "t") },
                    Stop = new UiAction("stop", "■ Стоп", "Остановить"),
                    Primary = new UiAction("go", "▶ Применить", "Применить приказ"),
                    Changed = true,
                });
            };
            c.Frame(body);
            var stop = c.P.Find("stop"); var go = c.P.Find("go"); var aux = c.P.Find("aux");
            Eq(tag + "Primary is the rightmost, Stop before it, Aux first", go.R.X > stop.R.X && stop.R.X > aux.R.X && Math.Abs(go.R.X + go.R.W - (c.Width - c.T.Pad)) < 0.6f);
            Eq(tag + "gap Stop-Primary >= 12 px x s", go.R.X - (stop.R.X + stop.R.W) >= 12f * (float)s - 0.6f);
            Eq(tag + "tones: Stop error, Primary accent", stop.Tone == UiTone.Error && go.Tone == UiTone.Accent);
            Eq(tag + "Changed label on the left", c.P.FindText("● Изменено") != null && c.P.FindText("● Изменено").R.X < aux.R.X);
            Eq(tag + "layout clean", c.P.CheckLayout(c.Width, 10000) == null);
            c.P.Click("go"); c.Frame(body);
            Eq(tag + "returns the pressed id", pressed == "go");
            // a grey primary: the reason below, equal to the tip
            var c2 = new WidgetCtx(s, 320f);
            c2.Frame(k => k.Footer(new UiFooter { Primary = new UiAction("go", "Начать", null, false), Reason = "Нужен хотя бы один борт в группе" }));
            var g2 = c2.P.Find("go");
            var reason = c2.P.FindText("Нужен хотя бы один борт в группе");
            Eq(tag + "grey primary: reason line below and the same text in the tip", !g2.Enabled && g2.Tip == "Нужен хотя бы один борт в группе" && reason != null && reason.R.Y > g2.R.Y);
            Eq(tag + "no Changed label when not changed", c2.P.FindText("● Изменено") == null);
            // narrow window: nothing past the width
            var c3 = new WidgetCtx(s, 150f);
            c3.Frame(body);
            Eq(tag + "narrow: layout clean, Primary still rightmost", c3.P.CheckLayout(c3.Width, 10000) == null && c3.P.Find("go").R.X > c3.P.Find("stop").R.X);
        }
    }
}
