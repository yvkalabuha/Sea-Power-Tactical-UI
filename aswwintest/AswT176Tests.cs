using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    // T-176 W8 (asw-order-v3, ADR-0076 s.7): the order section of the ASW window works without a keyboard: no text field but the rename, every field is filled at the first opening,
    // "Start" is available at once, the last order of a mode comes back after a restart, ru / en x s 1 / 1.33 at 520 px stay inside the width, an open list does not break the layout.
    internal static class AswT176Tests
    {
        private static string T(string key) { return UiText.Get(key); }

        public static string Run()
        {
            var old = UiWindowPrefs.State;
            try { return Cases(); }
            finally { UiWindowPrefs.State = old; }
        }

        private static AswView Fresh()
        {
            var v = AswWindowTests.Normal();
            var g = new AswGroupView { Name = "ПЛО-9", Order = "none", Depth = null, Status = "none" };
            g.Members.Add(50);
            g.MemberRows.Add(new AswRow { Uid = 50, Name = "SH-3H 1", Type = "SH-3H", State = "air", ShipUid = 700, Passive = 12, ActiveBuoys = 8, Vlad = 4, Dipping = true, Mad = true });
            g.CourseTargets.Add(new AswTargetOption { Uid = 700, Name = "Kirov" });
            v.Groups.Add(g);
            v.SelectedShipUid = 700;
            return v;
        }

        private static string Cases()
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                    foreach (double s in new[] { 1.0, 1.33 })
                    {
                        string tag = lang + " s=" + s;
                        float w = 520f * (float)s;
                        UiWindowPrefs.State = new UiWindowStateStore("", null);
                        foreach (string group in new[] { "ПЛО-9", "ПЛО-1", "ПЛО-2" })
                            foreach (int mode in new[] { 0, 1 })
                            {
                                var win = new Win(s, w, Fresh());
                                win.Frame(); win.B.SelectedGroup = group; win.Frame(); win.Frame(); win.Mode(mode); win.Frame();
                                string t2 = tag + " " + group + " mode " + mode;
                                var fields = win.P.Of("Field");
                                foreach (var f in fields) sb.Append(t2 + ": a text field in the order section: " + f.Id + "\n");
                                var lint = win.P.Lint(UiTheme.For(s), false, new[] { "rename" });
                                if (lint.Count > 0) sb.Append(t2 + ": lint " + string.Join("|", lint) + "\n");
                                if (win.K.Overflows.Count > 0) sb.Append(t2 + ": overflow " + string.Join("|", win.K.Overflows) + "\n");
                                string r = win.P.CheckLayout(w, (float)(0.85 * 1080 * s));
                                if (r != null) sb.Append(t2 + ": " + r + "\n");
                                var d = win.B.Draft;
                                if (d == null) { sb.Append(t2 + ": no draft\n"); continue; }
                                if (mode == 0 && (d.Course.CorridorXNm <= 0 || d.Course.BarrierXNm <= 0 || d.Course.DipLNm <= 0 || d.Course.CorridorLNm <= 0 || d.Course.BarrierLNm <= 0 || string.IsNullOrEmpty(d.Depth))) sb.Append(t2 + ": an empty field\n");
                                if (mode == 1 && (d.DistNm <= 0 || d.SizeNm <= 0 || string.IsNullOrEmpty(d.AreaPattern))) sb.Append(t2 + ": an empty area field\n");
                                if (group == "ПЛО-9" && mode == 0)
                                {
                                    var st = win.P.Find("start_course");
                                    if (st == null || !st.Enabled) sb.Append(t2 + ": Start is not available at the first opening\n");
                                    if (d.TargetUid != 700) sb.Append(t2 + ": the default target is not the selected ship\n");
                                    if (!HasSummary(win)) sb.Append(t2 + ": no summary line\n");
                                }
                                // an open list does not break the layout
                                string id = mode == 0 ? (win.Has("dip_l") ? "dip_l" : win.Has("cor_l") ? "cor_l" : null) : (win.Has("dist") ? "dist" : null);
                                if (id != null)
                                {
                                    win.Click(id); win.Frame();
                                    r = win.P.CheckLayout(w, (float)(0.9 * 1080 * s));
                                    if (r != null) sb.Append(t2 + " list open: " + r + "\n");
                                }
                            }
                        // the SAR tab: a drop-down, no field
                        var ws = new Win(s, w, Fresh());
                        ws.Frame(); ws.Mode(2); ws.Frame();
                        if (ws.Has("sradius.text") || ws.P.Of("Field").Count > 0 || !ws.Has("sradius")) sb.Append(tag + ": the SAR radius is not a drop-down\n");
                        // the "Route" button exists in both modes and the flag is remembered separately
                        var wr = new Win(s, w, Fresh());
                        wr.Frame(); wr.B.SelectedGroup = "ПЛО-9"; wr.Frame(); wr.Frame();
                        if (!wr.Has("route")) sb.Append(tag + ": no route button\n");
                    }
            string e = Memory();
            if (e != null) sb.Append(e);
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static bool HasSummary(Win win)
        {
            foreach (var it in win.P.Of("Label"))
                if (it.Text != null && (it.Text.StartsWith(T("asw.sum.corridor").Split('{')[0].Trim(), StringComparison.Ordinal) || it.Text.StartsWith(T("asw.sum.dip").Split('{')[0].Trim(), StringComparison.Ordinal) || it.Text.StartsWith(T("asw.sum.barrier").Split('{')[0].Trim(), StringComparison.Ordinal))) return true;
            return false;
        }

        /// <summary>The last order of a mode survives a restart of the window state and starts the next order.</summary>
        private static string Memory()
        {
            using (UiText.Push("ru"))
            {
                string saved = null;
                UiWindowPrefs.State = new UiWindowStateStore("", s => saved = s);
                var win = new Win(1.0, 520f, Fresh());
                win.Frame(); win.B.SelectedGroup = "ПЛО-9"; win.Frame(); win.Frame();
                win.Click("tactic.2"); win.Pick("bar_gap", 2); win.Click("depth.2");   // T-229: the barrier has rows and an interval instead of the standoff
                win.Click("start_course");
                if (win.Sent.Count != 1 || win.Sent[0] != "asw_search_course") return "memory: the order was not sent: " + win.Cmds();
                if (saved == null || !saved.Contains("asw.last.course")) return "memory: the last order is not saved: " + saved;
                // "restart": a new store from the saved text, a group without an order
                UiWindowPrefs.State = new UiWindowStateStore(saved, x => saved = x);
                var win2 = new Win(1.0, 520f, Fresh());
                win2.Frame(); win2.B.SelectedGroup = "ПЛО-9"; win2.Frame(); win2.Frame();
                var d = win2.B.Draft;
                if (d == null || d.Combo != "barrier" || d.Course.BarrierGapNm != 8 || d.Depth != "below") return "memory: after the restart " + (d == null ? "null" : d.Combo + "/" + d.Course.BarrierGapNm + "/" + d.Depth);
                // the area order remembers the pattern and the size, not the bearing and the distance
                win2.Mode(1); win2.Frame();
                win2.Click("pattern.1"); win2.Click("asrc.1"); win2.Pick("size", 3); win2.Pick("dist", 5);
                win2.Click("start_area");
                if (win2.Sent.Count != 1 || win2.Sent[0] != "asw_search_area") return "memory: the area order was not sent: " + win2.Cmds();
                UiWindowPrefs.State = UiWindowStateStore.Parse(saved);
                var win3 = new Win(1.0, 520f, Fresh());
                win3.Frame(); win3.B.SelectedGroup = "ПЛО-9"; win3.Frame(); win3.Frame(); win3.Mode(1); win3.Frame();
                d = win3.B.Draft;
                if (d == null || d.AreaPattern != "grid" || d.SizeNm != 20) return "memory: the area order after the restart " + (d == null ? "null" : d.AreaPattern + "/" + d.SizeNm);
                if (d.DistNm != 20 || d.BearingDeg != 0) return "memory: the distance / bearing must not be remembered";
            }
            return null;
        }
    }
}
