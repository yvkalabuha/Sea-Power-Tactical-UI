using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 C3 (v3-reanchor): the anchor of the moving axis (turn / drift / speed / stand) and the fate of a package in flight.
    internal static partial class AswT176Tests
    {
        private static int Count(AswFakeGame f, string sub) { int n = 0; foreach (var l in f.Logs) if (l.Contains(sub)) n++; return n; }

        private static AswController CourseWorld(out AswFakeGame f, int helos = 2)
        {
            f = new AswFakeGame { DropDelayS = 600, DropEveryS = 60 };
            for (int u = 1; u <= helos; u++) { var h = AswFakeGame.Helo(u, "air", passive: 60); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h); }
            var c = Make(f);
            var ids = new List<string>(); for (int u = 1; u <= helos; u++) ids.Add(u.ToString());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + string.Join(",", ids.ToArray()) + "]}"));
            c.SearchCourse(P("{\"tactics\":[\"corridor\"]}"));
            return c;
        }

        private static string Reanchor()
        {
            AswFakeGame f; AswController c;
            // 20 degrees off the anchor: nothing
            c = CourseWorld(out f); Run(c, f, 120);
            f.Tgt.CourseDeg = 110; Run(c, f, 900);
            if (Count(f, "reanchor reason=") != 0) return "20 deg re-anchored";
            if (Count(f, " anchor axis=") != 1) return "anchor lines " + Count(f, " anchor axis=");
            // 35 degrees, held: a turn, the package is kept (below 45)
            c = CourseWorld(out f); Run(c, f, 120);
            f.Tgt.CourseDeg = 125; Run(c, f, 900);
            if (Count(f, "reanchor reason=turn") < 1) return "35 deg: no turn re-anchor";
            if (!Logged(f, "reanchor reason=turn") || !Logged(f, "packet=kept")) return "35 deg: packet not kept";
            // 50 degrees: the package is cut off
            c = CourseWorld(out f); Run(c, f, 120);
            f.Tgt.CourseDeg = 145; Run(c, f, 900);
            if (!Logged(f, "reanchor reason=turn") || !Logged(f, "packet=cancelled")) return "50 deg: packet not cancelled";
            // zigzag +-20 degrees every 10 min for 2 h: no re-anchor
            c = CourseWorld(out f); Run(c, f, 120);
            for (int k = 0; k < 12; k++) { f.Tgt.CourseDeg = 90 + (k % 2 == 0 ? 20 : -20); for (int i = 0; i < 120; i++) { f.Advance(5); f.Tick(c); } }
            if (Count(f, "reanchor reason=") != 0) return "zigzag re-anchored " + Count(f, "reanchor reason=");
            // 4 nm to the side at X = 20 (R 2.7, X/8 = 2.5): drift
            c = CourseWorld(out f); Run(c, f, 120);
            f.Tgt.Track.Clear(); f.Tgt.Lat -= 4.0 / 60;
            Run(c, f, 30);
            if (!Logged(f, "reanchor reason=drift")) return "no drift re-anchor";
            // speed +40 %
            c = CourseWorld(out f); Run(c, f, 120);
            f.Tgt.SpeedKn = 21; Run(c, f, 30);
            if (!Logged(f, "reanchor reason=speed")) return "no speed re-anchor";
            // slow down below 3 kn: stand, then back
            c = CourseWorld(out f); Run(c, f, 120);
            f.Tgt.SpeedKn = 1; Run(c, f, 30);
            if (!Logged(f, "reanchor reason=stand")) return "no stand re-anchor";
            f.Tgt.SpeedKn = 15; Run(c, f, 30);
            if (Count(f, "reanchor reason=stand") != 2) return "no re-anchor back from stand";
            return null;
        }
    }
}
