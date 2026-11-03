using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    // T-229 (ADR-0094): the sliding barrier in the order section: rows and interval lists instead of the standoff, the command arguments, the memory of the last order
    // (9 fields; an old 7-field line reads the defaults), the summary line.
    internal static class AswT229WinTests
    {
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

        public static string Run()
        {
            var old = UiWindowPrefs.State;
            try { return Cases(); }
            finally { UiWindowPrefs.State = old; }
        }

        private static string Cases()
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string saved = null;
                    UiWindowPrefs.State = new UiWindowStateStore("", s => saved = s);
                    var win = new Win(1.0, 520f, Fresh());
                    win.Frame(); win.B.SelectedGroup = "ПЛО-9"; win.Frame(); win.Frame();
                    win.Click("tactic.2");
                    if (win.B.Draft == null || win.B.Draft.Combo != "barrier") { sb.Append(lang + ": no barrier draft\n"); continue; }
                    var c = win.B.Draft.Course;
                    if (c.BarrierRows != 2 || c.BarrierGapNm != 5) sb.Append(lang + ": the draft defaults " + c.BarrierRows + "/" + c.BarrierGapNm + "\n");
                    if (win.Has("bar_l")) sb.Append(lang + ": the standoff of the barrier is still offered\n");
                    if (!win.Has("bar_n.0") || !win.Has("bar_gap")) sb.Append(lang + ": no rows / interval selectors\n");
                    bool sum = false;
                    foreach (var it in win.P.Of("Label")) if (it.Text != null && it.Text.Contains(UiText.Get("asw.sum.barrier").Split('{')[0].Trim())) sum = it.Text.Contains(lang == "ru" ? "рубежей" : "rows");
                    if (!sum) sb.Append(lang + ": the summary does not name the rows\n");
                    win.Click("bar_n.2"); win.Pick("bar_gap", 2);
                    if (win.B.Draft.Course.BarrierRows != 3 || win.B.Draft.Course.BarrierGapNm != 8) sb.Append(lang + ": the draft after the edit " + win.B.Draft.Course.BarrierRows + "/" + win.B.Draft.Course.BarrierGapNm + "\n");
                    if (win.Sent.Count != 0) sb.Append(lang + ": the edit sent a command: " + win.Cmds() + "\n");
                    win.Click("start_course");
                    if (win.Sent.Count != 1 || win.Sent[0] != "asw_search_course") { sb.Append(lang + ": Start " + win.Cmds() + "\n"); continue; }
                    object rows, gap;
                    win.Args[0].TryGetValue("barrier_rows", out rows); win.Args[0].TryGetValue("barrier_gap_nm", out gap);
                    if (!(rows is double) || (double)rows != 3 || !(gap is double) || (double)gap != 8) sb.Append(lang + ": command arguments rows=" + rows + " gap=" + gap + "\n");
                    if (saved == null || !saved.Contains("/3/8")) sb.Append(lang + ": the memory line does not hold rows / interval: " + saved + "\n");
                    // after a restart the next order starts from them
                    UiWindowPrefs.State = new UiWindowStateStore(saved, x => saved = x);
                    var win2 = new Win(1.0, 520f, Fresh());
                    win2.Frame(); win2.B.SelectedGroup = "ПЛО-9"; win2.Frame(); win2.Frame();
                    var d = win2.B.Draft;
                    if (d == null || d.Course.BarrierRows != 3 || d.Course.BarrierGapNm != 8) sb.Append(lang + ": after the restart " + (d == null ? "null" : d.Course.BarrierRows + "/" + d.Course.BarrierGapNm) + "\n");
                }
            // an old 7-field line of the memory still reads: the rows and the interval stay at the defaults of the draft
            var od = new AswOrderDraft { Mode = "course" };
            if (!AswOrderPanel.ParseLast("course", "barrier/20/50/20/20/60/below", od) || od.Course.BarrierRows != 2 || od.Course.BarrierGapNm != 5 || od.Course.BarrierXNm != 50 || od.Depth != "below") sb.Append("an old memory line: " + od.Course.BarrierRows + "/" + od.Course.BarrierGapNm + "\n");
            if (AswOrderPanel.ParseLast("course", "barrier/20/50/20/20/60/below/9", od)) sb.Append("a junk memory line was accepted\n");
            if (!AswOrderPanel.ParseLast("course", "barrier/20/50/20/20/60/below/9/7", od) || od.Course.BarrierRows != 5 || od.Course.BarrierGapNm != 8) sb.Append("rows / interval are not clamped to the lists: " + od.Course.BarrierRows + "/" + od.Course.BarrierGapNm + "\n");
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}
