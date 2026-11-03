using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-323: "Return control" of a CAP aircraft: the CAP base is the LAST waypoint, ControlMemory.DecideRoute compares the FIRST one.
    // Reproduces the CapGame.Restore path with the real ControlMemory.
    internal static class CapT323Tests
    {
        private static double Nm(double[] a, double[] b) { return Math.Abs(a[0] - b[0]) * 60 + Math.Abs(a[1] - b[1]) * 60; }
        private static double Nm4(double a, double b, double c, double d) { return Math.Abs(a - c) * 60 + Math.Abs(b - d) * 60; }
        private static double[] P(double lat) { return new[] { lat, 0.0 }; }

        /// <summary>saved route -> patrol placed (last) -> optional ai_drift rebase to the game's last point -> [RestoreBase] -> DecideRoute.</summary>
        private static string Flow(List<double[]> saved, double[] patrolLast, double[] driftLast, List<double[]> wpsNow, bool rebase)
        {
            var m = new ControlMemory(); var u = new object();
            m.RememberRoute(u, saved, null);
            m.NoteRoutePlaced(u, patrolLast[0], patrolLast[1]);
            if (driftLast != null) m.NoteRoutePlaced(u, driftLast[0], driftLast[1]);
            var e = m.Peek(u);
            if (rebase)
            {
                var p = CapTakeoverMath.RestoreBase(wpsNow, e.PlacedByMod, Nm4);
                if (p != null) m.NoteRoutePlaced(u, p[0], p[1]);
            }
            return ControlMemory.DecideRoute(e, wpsNow.Count > 0 ? wpsNow[0] : null, Nm);
        }

        private static List<double[]> L(params double[] lats) { var l = new List<double[]>(); foreach (double x in lats) l.Add(P(x)); return l; }

        private static string DriftRestore()
        {
            var saved = L(50, 51);
            var now = L(10, 11, 12);
            if (Flow(saved, P(3), P(12), now, false) != "player_changed") return "baseline must reproduce player_changed";
            string r = Flow(saved, P(3), P(12), now, true);
            return r == "restore" ? null : "after ai_drift: " + r;
        }

        private static string PassedPoints()
        {
            string r = Flow(L(50), P(3), null, L(2, 3), true);
            if (r != "restore") return "passed point: " + r;
            return Flow(L(50), P(3), null, L(2, 3), false) == "player_changed" ? null : "baseline must be player_changed";
        }

        private static string PlayerRoute()
        {
            string r = Flow(L(50), P(3), null, L(20, 21), true);
            return r == "player_changed" ? null : "player route: " + r;
        }

        private static string SinglePoint()
        {
            string r = Flow(L(50), P(3), null, L(3), true);
            if (r != "restore") return "single point: " + r;
            r = Flow(L(50), P(3), null, L(), true);
            return r == "restore" ? null : "empty route: " + r;
        }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("math") && (e = CapTakeoverMath.SelfTestRestoreBase()) != null) return "math: " + e;
            if (want("drift-restore") && (e = DriftRestore()) != null) return "drift-restore: " + e;
            if (want("passed-points") && (e = PassedPoints()) != null) return "passed-points: " + e;
            if (want("player-route") && (e = PlayerRoute()) != null) return "player-route: " + e;
            if (want("single-point") && (e = SinglePoint()) != null) return "single-point: " + e;
            return null;
        }
    }
}
