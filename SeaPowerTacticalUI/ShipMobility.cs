using System;

namespace VoiceBotBridge
{
    /// <summary>T-266 / ADR-0106: a ship that cannot move (destroyed, sinking, engines out, or no way for SlowHoldS while the telegraph asks for it).</summary>
    internal static class ShipMobility
    {
        public const double SlowKn = 0.5, SlowHoldS = 60;
        /// <summary>T-274: the way below which the anchor of a pattern is "stand" (AswPatternMath.Reanchor).</summary>
        public const double StandKn = 3;

        /// <summary>T-274: telegraph known and != 0, |speedKn| &lt; standKn (NaN speed = false): the ship should move but stands.</summary>
        public static bool WayPending(double speedKn, int? telegraph, double standKn = StandKn)
        {
            return telegraph.HasValue && telegraph.Value != 0 && !double.IsNaN(speedKn) && Math.Abs(speedKn) < standKn;
        }

        /// <summary>null = can move; else "destroyed" | "sinking" | "engine" | "no_way". slowSinceS: mission time the ship was first seen without way under a telegraph (-1 = none), kept by the caller.</summary>
        public static string Immobile(bool destroyed, bool? sinking, bool? immobileFlag, double speedKn, int? telegraph, double now, ref double slowSinceS, double slowKn = SlowKn, double holdS = SlowHoldS)
        {
            if (destroyed) return "destroyed";
            if (sinking == true) return "sinking";
            if (immobileFlag == true) return "engine";
            bool slow = telegraph.HasValue && telegraph.Value != 0 && !double.IsNaN(speedKn) && Math.Abs(speedKn) < slowKn && !double.IsNaN(now);
            if (!slow) { slowSinceS = -1; return null; }
            if (slowSinceS < 0 || slowSinceS > now) slowSinceS = now;
            return now - slowSinceS >= holdS ? "no_way" : null;
        }

        public static string SelfTest()
        {
            double since = -1;
            if (Immobile(true, null, null, 10, 5, 0, ref since) != "destroyed") return "destroyed";
            since = -1;
            if (Immobile(false, true, false, 10, 5, 0, ref since) != "sinking") return "sinking";
            since = -1;
            if (Immobile(false, false, true, 10, 5, 0, ref since) != "engine") return "engine";
            since = -1;
            if (Immobile(false, false, false, 0, 5, 0, ref since) != null || since != 0) return "no_way t=0 since=" + since;
            if (Immobile(false, false, false, 0, 5, 59, ref since) != null) return "no_way t=59";
            if (Immobile(false, false, false, 0, 5, 60, ref since) != "no_way") return "no_way t=60";
            if (Immobile(false, false, false, 0.6, 5, 61, ref since) != null || since != -1) return "moving resets";
            since = -1;
            for (double t = 0; t <= 600; t += 10)
                if (Immobile(false, false, false, 0, 0, t, ref since) != null) return "player stop t=" + t;
            since = -1;
            if (Immobile(false, false, false, 0, null, 100, ref since) != null) return "unknown telegraph";
            since = -1;
            if (Immobile(false, false, false, 0, 5, double.NaN, ref since) != null || since != -1) return "nan now";
            since = -1;
            if (Immobile(false, null, null, 12, 5, 0, ref since) != null) return "flags unknown, moving";
            if (!WayPending(0, 2) || !WayPending(2.9, 1) || WayPending(3, 2) || WayPending(0, 0) || WayPending(0, null) || WayPending(double.NaN, 2) || !WayPending(-1, 2)) return "way pending";
            return null;
        }
    }
}
