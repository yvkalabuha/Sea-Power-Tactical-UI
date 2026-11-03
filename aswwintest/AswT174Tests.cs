// T-174 W1 / W4: the mode row inside "Order" (asw-layout-stable) and the "Route" button of the area order (asw-area-route), ru and en.
using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    internal static class AswT174Tests
    {
        private static string T(string key) { return UiText.Get(key); }

        private static UiRec Sec(Win w, string id) { return w.P.Find("sec.asw." + id); }

        public static string Run()
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string r = LayoutStable(lang) ?? FourModes(lang) ?? AreaRoute(lang);
                    if (r != null) sb.Append(lang + ": " + r + "\n");
                }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>The mode is a row of "Order": switching it moves neither the groups, nor the members, nor the header of "Order"; sends nothing; drops the map input.</summary>
        private static string LayoutStable(string lang)
        {
            var w = new Win(1.0, 520f, AswWindowTests.Normal());
            w.Frame(); w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            if (w.Has("tab.0")) return "tab strip is back";
            if (!w.Has("mode.0") || !w.Has("mode.1") || !w.Has("mode.2") || !w.Has("mode.3")) return "no mode row";
            var order = Sec(w, "order"); var mode0 = w.P.Find("mode.0");
            if (order == null || mode0.R.Y <= order.R.Y) return "the mode row is not inside the Order section";
            var g0 = Sec(w, "groups"); var m0 = Sec(w, "members");
            if (g0 == null || m0 == null || order.R.Y <= m0.R.Y || m0.R.Y <= g0.R.Y) return "section order groups / members / order";
            w.Mode(1); w.Click("asrc.0");
            if (w.B.WantInput != "asw_rect") return "draw did not start (setup)";
            // columns of the members come from the order of the group: the same header texts in every mode
            string cols = string.Join("|", w.P.FindText(T("asw.col.tactic")) != null ? "tactic" : "none");
            for (int i = 0; i < 4; i++)
            {
                int n = w.Sent.Count;
                w.Mode(i);
                if (w.B.Mode != AswWindowModel.OrderModes[i]) return "mode " + i + " not set";
                if (w.Sent.Count != n) return "mode " + i + " sent " + w.Cmds();
                if (w.B.WantInput != null) return "WantInput not reset by mode " + i;
                var g = Sec(w, "groups"); var m = Sec(w, "members"); var o = Sec(w, "order");
                if (g == null || m == null || o == null) return "section missing in mode " + i;
                if (Math.Abs(g.R.Y - g0.R.Y) > 0.01f || Math.Abs(m.R.Y - m0.R.Y) > 0.01f || Math.Abs(o.R.Y - order.R.Y) > 0.01f) return "sections moved in mode " + i + ": " + g.R.Y + "/" + m.R.Y + "/" + o.R.Y;
                if ((w.P.FindText(T("asw.col.tactic")) != null ? "tactic" : "none") != cols) return "members columns changed in mode " + i;
                if (!w.Has("mode." + i) || w.P.Find("mode." + i).R.Y <= o.R.Y) return "mode row not in Order, mode " + i;
                if (!w.Env.Logs.Exists(s => s.StartsWith("asw window press mode." + i + " -> " + AswWindowModel.OrderModes[i], StringComparison.Ordinal)) && i != 0) return "press not logged, mode " + i;
            }
            w.Mode(1);
            if (!w.Has("pattern.0") || !w.Has("asrc.0")) return "area controls missing after the mode change";
            return null;
        }

        /// <summary>ADR-0074 will add a 4th mode ("Strike"): four labels share one row of a 520 px window.</summary>
        private static string FourModes(string lang)
        {
            foreach (var cfg in new[] { new[] { 1.0, 520.0 }, new[] { 1.33, 520.0 }, new[] { 1.33, 440.0 } })
            {
                double scale = cfg[0]; float width = (float)cfg[1];
                var w = new Win(scale, width, new AswView());
                w.P.NextFrame(); w.K.Begin(width);
                w.K.Row(T("asw.lbl.mode"));
                var l = new List<string>(AswWindowModel.OrderButtonTexts());   // T-173: the fourth mode is "Strike"; T-223: "ASW strike"
                if (l.Count != 4 || l[3] != (lang == "ru" ? "Удар ПЛ" : "ASW strike")) return "the four labels: " + string.Join("|", l.ToArray());
                w.K.Radio("m4", l, 0);
                w.K.End();
                float y = -1, right = 0;
                for (int i = 0; i < 4; i++)
                {
                    var it = w.P.Find("m4." + i);
                    if (it == null) return "button " + i + " missing (scale " + scale + ")";
                    if (it.Text != l[i]) return "label cut: " + it.Text + " (scale " + scale + ", width " + width + ")";
                    if (y < 0) y = it.R.Y; else if (Math.Abs(it.R.Y - y) > 0.01f) return "four mode buttons wrap to a second row (scale " + scale + ")";
                    right = Math.Max(right, it.R.X + it.R.W);
                }
                if (right > width) return "four mode buttons overflow at scale " + scale + ", width " + width + ": " + right;
            }
            return null;
        }

        private static AswGroupView Idle()
        {
            var g = new AswGroupView { Name = "ПЛО-7", Order = "none", Depth = "below", Status = "none" };
            g.Tactics.Add("rows");
            g.Members.Add(70);
            g.MemberRows.Add(new AswRow { Uid = 70, Name = "SH-3H 1", Type = "SH-3H", State = "air", Passive = 12, Dipping = true, Mad = true, Tactic = "rows", Loadout = "ASW" });
            return g;
        }

        private static string AreaRoute(string lang)
        {
            string saved = null;
            var old = UiWindowPrefs.State;
            UiWindowPrefs.State = new UiWindowStateStore("", s => saved = s);
            try
            {
                string show = T("asw.btn.route_show_short"), hide = T("asw.btn.route_hide_short");
                // 1) no area yet: grey, a click says why, nothing is sent, the flag stays off
                var v = new AswView(); v.Groups.Add(Idle());
                var w = new Win(1.0, 520f, v);
                w.Frame(); w.B.SelectedGroup = "ПЛО-7"; w.Frame(); w.Mode(1);
                var rb = w.P.Find("route");
                if (rb == null || rb.Enabled || rb.Text != show) return "no area: the route button is not grey";
                w.Click("route");
                if (w.B.Note != T("asw.hint.need_area") || w.Sent.Count != 0 || AswDrawPlan.AreaRouteOn()) return "no area: click -> " + w.B.Note + " / " + w.Cmds();
                // 2) a drawn area: the dry run of the draft, the flag is saved
                w.Click("asrc.0");
                w.B.OnRect(new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 });
                w.Frame();
                rb = w.P.Find("route");
                if (rb == null || !rb.Enabled) return "area drawn: the route button is grey";
                w.Click("route");
                if (w.Sent.Count != 1 || w.Sent[0] != "asw_preview") return "area drawn: sent " + w.Cmds();
                var a = w.Args[0];
                if (!(a.ContainsKey("area") && a["area"] is bool && (bool)a["area"]) || !a.ContainsKey("rect") || a.ContainsKey("target_uid") || a.ContainsKey("lead_nm") || a.ContainsKey("width_nm")) return "preview args: " + string.Join(",", a.Keys);
                if (!AswDrawPlan.AreaRouteOn() || !UiWindowStateStore.Parse(saved).Flag(AswDrawPlan.AreaRouteId, false)) return "the flag is not remembered: " + saved;
                w.Frame();
                if (w.P.Find("route").Text != hide) return "label after on: " + w.P.Find("route").Text;
                if (!w.Env.Logs.Exists(s => s == "asw window press route (area) -> on")) return "press not logged";
                // 3) off: the dry run goes, the flag goes
                w.V.Preview.Add(new AswLineView { Kind = "preview", Tactic = "area" });
                w.Frame(); w.Click("route");
                if (w.Sent.Count != 2 || w.Sent[1] != "asw_preview_clear" || AswDrawPlan.AreaRouteOn() || UiWindowStateStore.Parse(saved).Flag(AswDrawPlan.AreaRouteId, true)) return "off: " + w.Cmds() + " / " + saved;
                w.Frame();
                if (w.P.Find("route").Text != show) return "label after off";
                // 4) a group already on the area order and nothing edited: only the flag
                w = new Win(1.0, 520f, AswWindowTests.Normal());
                w.Frame(); w.B.SelectedGroup = "ПЛО-2"; w.Frame(); w.Frame();
                if (w.B.Mode != "area") return "area group does not open the area mode";
                rb = w.P.Find("route");
                if (rb == null || !rb.Enabled) return "area order: the route button is grey";
                w.Click("route");
                if (w.Sent.Count != 0 || !AswDrawPlan.AreaRouteOn()) return "area order: click sent " + w.Cmds();
                w.Click("route");
                if (w.Sent.Count != 0 || AswDrawPlan.AreaRouteOn()) return "area order: off sent " + w.Cmds();
                // 5) T-176 W6: the course order has a persistent flag of its own like the area one; a draft equal to the order needs no dry run, a changed one is sent (kept)
                w = new Win(1.0, 520f, AswWindowTests.Normal());
                w.Frame(); w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
                w.Click("route");
                if (w.Sent.Count != 0 || !AswDrawPlan.CourseRouteOn() || AswDrawPlan.AreaRouteOn()) return "course route: " + w.Cmds();
                if (!UiWindowStateStore.Parse(saved).Flag(AswDrawPlan.CourseRouteId, false)) return "the course flag is not remembered: " + saved;
                w.Click("tactic.2");
                if (w.Sent.Count != 1 || w.Sent[0] != "asw_preview" || w.Args[0].ContainsKey("area") || !(w.Args[0]["keep"] is bool)) return "course route with a changed draft: " + w.Cmds();
                w.Click("depth.1");
                if (w.Sent.Count != 2 || w.Sent[1] != "asw_preview") return "the preview follows the draft: " + w.Cmds();
                w.Click("route");
                if (AswDrawPlan.CourseRouteOn()) return "course route off";
                return null;
            }
            finally { UiWindowPrefs.State = old; }
        }
    }
}
