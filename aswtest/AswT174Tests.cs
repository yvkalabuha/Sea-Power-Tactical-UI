using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-174 W3: the snapshot carries the rest of the area plan (AswGroupView.AreaPlan) for the overlay.
    internal static class AswT174Tests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static string RectJson(double[] r) { return "[" + string.Join(",", r.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"; }

        public static string Run()
        {
            var f = new AswFakeGame { DropDelayS = 60, DropEveryS = 30, SimBuoys = true };
            for (int u = 1; u <= 2; u++) { var h = AswFakeGame.Helo(u, "air", passive: 100); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Vlad = 0; h.ActiveBuoys = 0; f.Mem.Add(h); }
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            var ctr = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchArea(P("{\"group\":\"G\",\"rect\":" + RectJson(AswController.RectAround(ctr, 60, 40)) + ",\"tactics\":[\"rows\"]}"));
            for (int i = 0; i < 5; i++) { f.Advance(1); f.Tick(c); }
            var g = c.View().Groups[0];
            if (g.Order != "area") return "not an area order: " + g.Order;
            int total = 0;
            for (int i = 0; i < 3600 && g.AreaPlan.Count == 0; i++) { f.Advance(1); f.Tick(c); g = c.View().Groups[0]; }
            if (g.AreaPlan.Count != 1 || g.AreaPlan[0].Kind != "plan" || g.AreaPlan[0].Tactic != "area") return "no plan line after the start: " + g.AreaPlan.Count + " lines=" + g.Lines.Count + " status=" + g.Status + " tactics=" + string.Join(",", g.Tactics) + " buoys=" + g.Buoys.Count + " rect=" + (g.Rect != null);
            total = g.AreaPlan[0].Points.Count;
            int before = total;
            for (int i = 0; i < 3000; i++) { f.Advance(1); f.Tick(c); }
            g = c.View().Groups[0];
            int left = g.AreaPlan.Count == 0 ? 0 : g.AreaPlan[0].Points.Count;
            if (left >= before) return "the plan did not shrink after two packages: " + before + " -> " + left;
            // the rest is the tail of the grid: its last point is the last point of the grid
            if (g.AreaPlan.Count == 1 && g.Lines.Count == 1 && g.Lines[0].Points.Count > 0)
            {
                var last = g.Lines[0].Points[g.Lines[0].Points.Count - 1]; var pl = g.AreaPlan[0].Points[g.AreaPlan[0].Points.Count - 1];
                if (Math.Abs(last.Lat - pl.Lat) > 1e-9 || Math.Abs(last.Lon - pl.Lon) > 1e-9) return "the rest is not the tail of the grid";
            }
            // a course order has no area plan
            var f2 = new AswFakeGame { DropDelayS = 60, DropEveryS = 30, SimBuoys = true };
            { var h = AswFakeGame.Helo(1, "air", passive: 12); h.EnduranceS = 1e7; h.FuelFrac = 1; f2.Mem.Add(h); }
            var c2 = new AswController(f2, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            c2.Group(P("{\"op\":\"create\",\"group\":\"C\",\"uids\":[1]}"));
            c2.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            for (int i = 0; i < 60; i++) { f2.Advance(1); f2.Tick(c2); }
            if (c2.View().Groups[0].AreaPlan.Count != 0) return "a course order carries an area plan";
            // stop clears it
            c.Stop(P("{\"group\":\"G\"}"));
            f.Advance(1); f.Tick(c);
            if (c.View().Groups[0].AreaPlan.Count != 0) return "stop leaves the plan";
            return null;
        }
    }
}
