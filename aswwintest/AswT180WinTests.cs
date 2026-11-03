using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    // T-180 D2: the retreat in the ASW window (texts, the group chip, the row state), the notice feed and the arrow head of the map arrow.
    internal static class AswT180WinTests
    {
        private sealed class RecToasts : IUiToasts
        {
            public readonly List<string> Calls = new List<string>();
            public readonly Dictionary<string, Action<UiKit>> Bodies = new Dictionary<string, Action<UiKit>>();
            public void Show(string key, string kind, Action<UiKit> body, double now, double hideAfterS, bool reshow) { Calls.Add("show:" + key + ":" + kind + ":" + reshow); Bodies[key] = body; }
            public void Update(string key, Action<UiKit> body) { Calls.Add("update:" + key); }
            public void Close(string key) { Calls.Add("close:" + key); }
        }

        public static string Run(string section)
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string r = null;
                    if (section == null || section == "texts") r = Texts(lang);
                    if (r == null && (section == null || section == "chips")) r = Chips();
                    if (r == null && (section == null || section == "toast")) r = Toast(lang);
                    if (r != null) sb.Append(lang + ": " + r + "\n");
                }
            string a = section == null || section == "arrow" ? Arrow() : null;
            if (a != null) sb.Append("arrow: " + a + "\n");
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static AswRetreatView Rv(int members, bool blocked, string when, double nm = 30, string cover = "Yorktown")
        {
            return new AswRetreatView { Members = members, LaunchBlocked = blocked, Nm = nm, Cover = cover, Why = "dist", ResumeWhen = when, ResumeNm = 60 };
        }

        private static string Texts(string lang)
        {
            bool ru = lang == "ru";
            string c = AswWindowModel.RetreatChipText(Rv(1, true, "recede"));
            if (c != (ru ? "Отход: угроза с воздуха, 30 миль; вернётся, когда цели удалятся" : "Retreat: air threat, 30 nm; will resume when the targets move away")) return "chip recede: " + c;
            c = AswWindowModel.RetreatChipText(Rv(1, true, "beyond"));
            if (c != (ru ? "Отход: угроза с воздуха, 30 миль; вернётся, когда цели будут дальше 60 миль" : "Retreat: air threat, 30 nm; will resume when the targets are beyond 60 nm")) return "chip beyond: " + c;
            c = AswWindowModel.RetreatChipText(Rv(0, true, "recede"));
            if (c != (ru ? "Вылеты задержаны: угроза с воздуха, 30 миль" : "Launches held: air threat, 30 nm")) return "chip blocked: " + c;
            string s = AswWindowModel.StateText(new AswRow { State = "air", RetreatTo = "Yorktown" });
            if (s != (ru ? "отход → Yorktown" : "retreating → Yorktown")) return "state: " + s;
            s = AswWindowModel.StateText(new AswRow { State = "air", RetreatTo = "" });
            if (s != (ru ? "отход от угрозы" : "retreating from the threat")) return "state away: " + s;
            if (AswWindowModel.StateText(new AswRow { State = "air" }) != (ru ? "в воздухе" : "airborne")) return "state air unchanged";
            return null;
        }

        private static AswGroupView Grp(string name, AswRetreatView r)
        {
            return new AswGroupView { Name = name, Order = "course", TargetName = "Kirov", Retreat = r };
        }

        private static string Chips()
        {
            var plain = AswWindowBody.GroupChips(Grp("G", null));
            var chips = AswWindowBody.GroupChips(Grp("G", Rv(1, true, "recede")));
            if (chips.Count != plain.Count + 1) return "chip count " + chips.Count + " vs " + plain.Count;
            var c = chips[1];
            if (c.Glyph != "!" || c.Tone != UiTone.Warn || c.Text != AswWindowModel.RetreatChipText(Rv(1, true, "recede"))) return "retreat chip is not second: " + c.Glyph + "/" + c.Tone + "/" + c.Text;
            if (chips[0].Text != plain[0].Text) return "the state chip must stay first";
            foreach (var p in plain) if (p.Text == c.Text) return "chip without Retreat";
            return null;
        }

        private static AswView V(params AswGroupView[] gs) { var v = new AswView(); v.Groups.AddRange(gs); return v; }

        private static string Toast(string lang)
        {
            bool ru = lang == "ru";
            var t = new RecToasts(); var f = new AswRetreatToastFeed();
            var g1 = Grp("ПЛО-1", null);
            f.Apply(V(g1), t, true, 8, 0);
            if (t.Calls.Count != 0) return "a toast without a retreat";
            g1.Retreat = Rv(1, true, "recede");
            f.Apply(V(g1), t, true, 8, 1);
            if (t.Calls.Count != 1 || t.Calls[0] != "show:asw_retreat:ПЛО-1:asw_retreat:True") return "enter: " + string.Join("|", t.Calls);
            f.Apply(V(g1), t, true, 8, 2);
            f.Apply(V(g1), t, true, 8, 3);
            if (t.Calls.Count != 1) return "the same state shown again";
            g1.Retreat = null;
            f.Apply(V(g1), t, true, 8, 4);
            if (t.Calls.Count != 2) return "no return notice: " + string.Join("|", t.Calls);
            // a text check of the return card
            string txt = Render(t.Bodies["asw_retreat:ПЛО-1"]);
            if (!txt.Contains(ru ? "возврат к задаче" : "back to the task")) return "return text: " + txt;
            // flapping: 10 s after the first notice no new one
            g1.Retreat = Rv(1, true, "recede");
            f.Apply(V(g1), t, true, 8, 11);
            if (t.Calls.Count != 2) return "a second notice within MinGapS";
            g1.Retreat = null;
            f.Apply(V(g1), t, true, 8, 12);
            if (t.Calls.Count != 2) return "a return notice without a shown retreat notice";
            g1.Retreat = Rv(1, true, "recede");
            f.Apply(V(g1), t, true, 8, 32);
            if (t.Calls.Count != 3 || !t.Calls[2].StartsWith("show:asw_retreat:ПЛО-1")) return "no notice after MinGapS: " + string.Join("|", t.Calls);
            // two groups, two keys
            var t2 = new RecToasts(); var f2 = new AswRetreatToastFeed();
            var a = Grp("A", Rv(1, true, "recede")); var b = Grp("B", Rv(1, true, "beyond", 20, null));
            f2.Apply(V(a, b), t2, true, 8, 0);
            if (t2.Calls.Count != 2 || t2.Calls[0] == t2.Calls[1]) return "two groups: " + string.Join("|", t2.Calls);
            // Toast = off
            var t3 = new RecToasts(); var f3 = new AswRetreatToastFeed();
            f3.Apply(V(Grp("A", Rv(1, true, "recede"))), t3, false, 8, 0);
            if (t3.Calls.Count != 0) return "Toast off still showed";
            // a launch hold without a retreating machine is not a retreat notice
            var t4 = new RecToasts(); var f4 = new AswRetreatToastFeed();
            f4.Apply(V(Grp("A", Rv(0, true, "recede"))), t4, true, 8, 0);
            if (t4.Calls.Count != 0) return "a launch hold showed a retreat notice";
            return null;
        }

        private static string Render(Action<UiKit> body)
        {
            var p = new RecordPainter(1.0);
            var k = new UiKit(p, UiTheme.For(1.0), new WinState(), "t");
            p.NextFrame(); k.Begin(320f); body(k); k.End();
            var sb = new StringBuilder();
            foreach (var i in p.Items) sb.Append(i.Text).Append(' ');
            return sb.ToString();
        }

        private static string Arrow()
        {
            var h = AswDrawPlan.ArrowHead(0, 0, 100, 0);
            if (h == null) return "null for a long segment";
            if (!(h[0] < 100 && h[2] < 100)) return "barbs must lie behind the tip: " + h[0] + "," + h[2];
            if (Math.Abs(h[1] + h[3]) > 1e-3 || h[1] == h[3]) return "barbs not symmetric in y: " + h[1] + "," + h[3];
            if (Math.Abs(Math.Sqrt((100 - h[0]) * (100 - h[0]) + h[1] * h[1]) - 12) > 1e-3) return "barb length";
            if (AswDrawPlan.ArrowHead(5, 5, 6, 5.5f) != null) return "a segment under 2 px must give null";
            return null;
        }
    }
}
