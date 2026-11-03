using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115 (D2): the sub datum card opens the window in the "In area" mode with the rectangle filled in; Start sends asw_search_area with it.
    internal static class AswT288WinTests
    {
        private static AswView Fresh()
        {
            var v = AswWindowTests.Normal();
            var g = new AswGroupView { Name = "ПЛО-9", Order = "none", Depth = null, Status = "none" };
            g.Members.Add(50);
            g.MemberRows.Add(new AswRow { Uid = 50, Name = "SH-3H 1", Type = "SH-3H", State = "air", ShipUid = 700, Passive = 12, ActiveBuoys = 8, Vlad = 4, Dipping = true, Mad = true });
            v.Groups.Add(g);
            v.SelectedShipUid = 700;
            return v;
        }

        public static string Run()
        {
            var old = UiWindowPrefs.State;
            try { return Draft(); }
            finally { UiWindowPrefs.State = old; }
        }

        private static string Draft()
        {
            var rect = new[] { 35.10, 20.00, 35.10, 20.10, 35.00, 20.10, 35.00, 20.00 };
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    UiWindowPrefs.State = new UiWindowStateStore("", null);
                    var win = new Win(1.0, 520f, Fresh());
                    win.Frame(); win.B.SelectedGroup = "ПЛО-9"; win.Frame(); win.Frame();
                    if (win.B.Mode == "area") return lang + ": the window must start in another mode";
                    win.B.DraftArea(rect);
                    win.Frame(); win.Frame();
                    if (win.B.Mode != "area") return lang + ": mode " + win.B.Mode;
                    var d = win.B.Draft;
                    if (d == null || d.Mode != "area" || d.AreaSource != "rect" || d.Rect == null || d.Rect.Length != 8) return lang + ": the draft has no rect";
                    for (int i = 0; i < 8; i++) if (Math.Abs(d.Rect[i] - rect[i]) > 1e-9) return lang + ": rect[" + i + "]";
                    if (win.Sent.Count != 0) return lang + ": opening the draft sent " + win.Cmds();
                    if (!win.Has("start_area")) return lang + ": no Start button";
                    win.Click("start_area");
                    if (win.Sent.Count != 1 || win.Sent[0] != "asw_search_area") return lang + ": Start sent " + win.Cmds();
                    var a = win.Args[0];
                    object grp, rc;
                    a.TryGetValue("group", out grp); a.TryGetValue("rect", out rc);
                    if (!(grp is string) || (string)grp != "ПЛО-9") return lang + ": group " + grp;
                    var flat = new List<double>();
                    var rl = rc as IEnumerable<object>;
                    var rd = rc as double[];
                    if (rd != null) flat.AddRange(rd);
                    else if (rl != null) foreach (object o in rl) { if (o is double) flat.Add((double)o); else { var p = o as IEnumerable<object>; if (p != null) foreach (object x in p) flat.Add((double)x); } }
                    if (flat.Count != 8) return lang + ": rect in the command has " + flat.Count + " numbers";
                    for (int i = 0; i < 8; i++) if (Math.Abs(flat[i] - rect[i]) > 1e-9) return lang + ": command rect[" + i + "]";
                    // a short or empty rect is ignored
                    var w2 = new Win(1.0, 520f, Fresh());
                    w2.Frame(); w2.B.SelectedGroup = "ПЛО-9"; w2.Frame(); w2.Frame();
                    w2.B.DraftArea(null); w2.B.DraftArea(new double[3]); w2.Frame();
                    if (w2.B.Mode == "area" && w2.B.Draft != null && w2.B.Draft.AreaSource == "rect") return lang + ": a bad rect was taken";
                }
            return null;
        }
    }
}
