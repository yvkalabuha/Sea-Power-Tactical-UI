using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C12 (v3-view): the plan summary, the line kinds, link_state, the depth "auto", the fixed target, the preview that stays.
    internal static partial class AswT176Tests
    {
        private static string ViewScenario()
        {
            var f = CorridorGame(60, 60, 60);
            f.Mem[0].Dipping = true;
            var c = StartCourse(f, "{\"tactics\":[\"dip\",\"corridor\"],\"corridor_x_nm\":30,\"standoff\":{\"dip\":30}}");
            Run(c, f, 10);
            var v = c.View(); var g = v.Groups[0];
            if (g.Plan.Tactic != "dip+corridor" || g.Plan.XNm != 30) return "plan " + g.Plan.Tactic + " x=" + g.Plan.XNm;
            if (g.Course.CorridorXNm != 30 || g.Course.DipLNm != 30 || g.Course.Tactics.Count != 2) return "course copy";
            if (g.Plan.RowsMax != 4) return "rows max " + g.Plan.RowsMax;
            if (v.Defaults.CorridorWidthNm != 20 || v.Defaults.BarrierWidthNm != 40) return "defaults";
            foreach (string kind in new[] { "corridor_row", "standoff", "dip_chain" })
                if (!g.Lines.Any(l => l.Kind == kind)) return "no line kind " + kind;
            if (!g.Lines.Any(l => l.Lane >= 0 && l.State == "done")) return "no lane on the laid lines";
            string st = c.StateJson();
            if (!st.Contains("\"plan\":{\"tactic\":\"dip+corridor\",\"x_nm\":30")) return "link_state plan: " + st.Substring(0, Math.Min(400, st.Length));
            // the depth "auto": below the layer for the corridor
            if (!f.Calls.Contains("depth:2:below") || !f.Calls.Contains("depth:3:below")) return "depth: " + string.Join(",", f.Calls.Where(x => x.StartsWith("depth")).ToArray());
            // T-334: the barrier is a group of its own now (corridor and barrier exclude each other): the line states, the lane, the depth
            var fb = CorridorGame(60, 14);
            fb.Mem[0].Dipping = true;
            var cb = StartCourse(fb, "{\"tactics\":[\"dip\",\"barrier\"],\"standoff\":{\"dip\":30}}");
            Run(cb, fb, 10);
            var gb = cb.View().Groups[0];
            if (gb.Plan.Tactic != "dip+barrier") return "barrier plan " + gb.Plan.Tactic;
            foreach (string kind in new[] { "barrier_line", "dip_chain" })
                if (!gb.Lines.Any(l => l.Kind == kind)) return "no line kind " + kind;
            if (!gb.Lines.Any(l => l.Kind == "barrier_line" && l.State == "done") || !gb.Lines.Any(l => l.Kind == "barrier_line" && l.State == "plan")) return "barrier states";
            if (!gb.Lines.Any(l => l.Lane >= 0 && l.State == "done")) return "no lane on the laid barrier lines";
            if (!fb.Calls.Contains("depth:2:below")) return "barrier depth: " + string.Join(",", fb.Calls.Where(x => x.StartsWith("depth")).ToArray());

            // an area: depth alternate, the sector paths by lane, the pattern
            var f2 = CorridorGame(60, 60);
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            GeoPt ctr;
            c2.SearchArea(P(AreaCmd(f2, 21.6, 12.96, "[\"grid\"]", out ctr)));
            Run(c2, f2, 2);
            if (!f2.Calls.Contains("depth:1:alternate")) return "area depth";
            var g2 = c2.View().Groups[0];
            if (g2.AreaPattern != "grid" || g2.Plan.NodesTotal != 15) return "area plan " + g2.AreaPattern + "/" + g2.Plan.NodesTotal;
            if (!c2.StateJson().Contains("\"area_pattern\":\"grid\"")) return "link_state area_pattern";

            // a fixed target: the ring of X/2 for the corridor, no rows
            var f3 = CorridorGame(60);
            f3.Tgt.SpeedKn = 1;
            var c3 = StartCourse(f3, "{\"tactics\":[\"corridor\"],\"corridor_x_nm\":20}");
            Run(c3, f3, 5);
            if (f3.Drops.Count == 0) return "fixed target: nothing laid";
            var ring = f3.Drops[0].Pts;
            foreach (var p in ring) { double dd = AswMath.DistNm(p, new GeoPt(f3.Tgt.Lat, f3.Tgt.Lon)); if (Math.Abs(dd - 10) > 0.3) return "ring radius " + dd; }
            if (ring.Count < 8) return "ring points " + ring.Count;

            // the preview stays while the window keeps it (keep), else it goes after 30 s
            var f4 = CorridorGame(60);
            var c4 = Make(f4);
            c4.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c4.PreviewCmd(P("{\"area\":false,\"tactics\":[\"dip\",\"corridor\"],\"keep\":true}"));
            if (c4.View().Preview.Count == 0) return "no preview";
            Run(c4, f4, 60);
            if (c4.View().Preview.Count == 0) return "a kept preview went";
            c4.PreviewCmd(P("{\"area\":false,\"tactics\":[\"corridor\"]}"));
            Run(c4, f4, 40);
            if (c4.View().Preview.Count != 0) return "a plain preview stays";
            return null;
        }
    }
}
