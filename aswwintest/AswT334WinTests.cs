using System;
using System.Text;

namespace VoiceBotBridge
{
    // T-334 (ADR-0130): the chips "Corridor" and "Barrier" exclude each other (the pressed one stays, the sonar chip is not touched); the memory of the last order and the
    // [Asw] CourseTactic default with both read as the barrier.
    internal static class AswT334WinTests
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
                    UiWindowPrefs.State = new UiWindowStateStore("", s => { });
                    var win = new Win(1.0, 520f, Fresh());
                    win.Frame(); win.B.SelectedGroup = "ПЛО-9"; win.Frame(); win.Frame();
                    win.Click("tactic.1");
                    if (win.B.Draft == null || win.B.Draft.Combo != "corridor") { sb.Append(lang + ": no corridor draft\n"); continue; }
                    win.Click("tactic.2");
                    if (win.B.Draft.Combo != "barrier") sb.Append(lang + ": Barrier pressed with Corridor on: " + win.B.Draft.Combo + "\n");
                    win.Click("tactic.1");
                    if (win.B.Draft.Combo != "corridor") sb.Append(lang + ": Corridor pressed with Barrier on: " + win.B.Draft.Combo + "\n");
                    win.Click("tactic.0");   // the sonar is not touched
                    win.Click("tactic.2");
                    if (win.B.Draft.Combo != "dip+barrier") sb.Append(lang + ": Barrier pressed with sonar + Corridor on: " + win.B.Draft.Combo + "\n");
                    win.Click("tactic.1");
                    if (win.B.Draft.Combo != "dip+corridor") sb.Append(lang + ": Corridor pressed with sonar + Barrier on: " + win.B.Draft.Combo + "\n");
                    if (win.Sent.Count != 0) sb.Append(lang + ": a chip press sent a command: " + win.Cmds() + "\n");
                    // the tips tell it
                    string tipCorridor = UiText.Get("asw.tip.tactic.corridor"), tipBarrier = UiText.Get("asw.tip.tactic.barrier");
                    if (!tipCorridor.Contains(lang == "ru" ? "Исключает «Барьер»" : "Turns off Barrier") || !tipBarrier.Contains(lang == "ru" ? "Исключает «Коридор»" : "Turns off Corridor")) sb.Append(lang + ": the chip tips do not say it\n");
                }
            // the memory of the last order with both: the barrier
            var od = new AswOrderDraft { Mode = "course" };
            if (!AswOrderPanel.ParseLast("course", "corridor+barrier/20/50/20/20/60/below/2/5", od) || od.Combo != "barrier") sb.Append("memory corridor+barrier -> " + od.Combo + "\n");
            if (!AswOrderPanel.ParseLast("course", "dip+corridor+barrier/20/50/20/20/60/below", od) || od.Combo != "dip+barrier") sb.Append("memory dip+corridor+barrier -> " + od.Combo + "\n");
            // the [Asw] default with both: the barrier
            var g = Fresh().Groups[Fresh().Groups.Count - 1];
            var dd = AswOrderPanel.DraftFrom(g, "course", null, new AswPatternParams { CourseTactic = "dip+corridor+barrier" });
            if (dd.Combo != "dip+barrier") sb.Append("cfg dip+corridor+barrier -> " + dd.Combo + "\n");
            dd = AswOrderPanel.DraftFrom(g, "course", null, new AswPatternParams { CourseTactic = "corridor+barrier" });
            if (dd.Combo != "barrier") sb.Append("cfg corridor+barrier -> " + dd.Combo + "\n");
            // ApplyExclusive on its own
            var ex = new AswOrderDraft { Mode = "course" };
            ex.On[0] = true; ex.On[1] = true; ex.On[2] = true;
            ex.ApplyExclusive(new[] { true, true, false });
            if (ex.Combo != "dip+barrier") sb.Append("ApplyExclusive -> " + ex.Combo + "\n");
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}
