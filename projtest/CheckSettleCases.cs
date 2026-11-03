// T-312 A1: the settled-view policy of MapProjectorCheck (zoom animation is not a mismatch) and the check counters.
using System;

namespace VoiceBotBridge.ProjTest
{
    internal static class CheckSettleCases
    {
        private static MapPxKey Key(double zoom, double dvx = 0)
        {
            double scale = GameRef.ZoomLevelToScale((float)zoom);
            MapViewSnap s = MapViewSnap.Ortho(30, 40, scale, 10, 30, 40, 960 + dvx, 540, 1920, 1080);
            return MapPx.Create(in s, new double[] { 1, 0, 0, 0, 1, 0 }, 1920, 1080).Key;
        }

        public static string Run()
        {
            // (a) first frame is never settled; the same key on the next frame is
            var c = new MapProjectorCheck();
            MapPxKey k = Key(10);
            if (c.Settled(in k, false)) return "first frame settled";
            if (!c.Settled(in k, false)) return "same key not settled";
            if (c.Settled(in k, true)) return "moving flag ignored";

            // (b) 72-frame zoom animation 11.1 -> 8.29 with easing: never due, no report, one skip
            c = new MapProjectorCheck();
            MapPxKey last = default(MapPxKey);
            for (int i = 0; i < 72; i++)
            {
                double t = i / 71.0, e = t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t);
                last = Key(11.1 + (8.29 - 11.1) * e);
                if (c.DueSettled(i / 24.0, in last, false)) return "animation frame " + i + " was due";
            }
            if (c.Checks != 0 || c.Mismatches != 0 || c.Reflect || c.Skips != 1) return "animation: checks " + c.Checks + " skips " + c.Skips;
            MapPxKey stop = Key(8.2);
            if (c.DueSettled(3.1, in stop, false)) return "first still frame must not be due";
            if (!c.DueSettled(3.2, in stop, false)) return "second still frame must be due";

            // (c) a real mismatch on settled views is caught
            c = new MapProjectorCheck();
            for (int i = 0; i < 3; i++)
            {
                MapPxKey ki = Key(10, 20 * i);
                double t0 = 10 * i;
                c.DueSettled(t0, in ki, false);
                if (!c.DueSettled(t0 + 0.1, in ki, false)) return "settled view " + i + " not due";
                c.Report(t0 + 0.1, 5.0);
            }
            if (!c.Reflect) return "three settled mismatches must switch to reflect";

            // (d) Defer: the check stays due, one more skip
            c = new MapProjectorCheck();
            c.DueSettled(0, in k, false);
            if (!c.DueSettled(0.1, in k, false)) return "defer: not due";
            int before = c.Skips;
            c.Defer();
            if (c.Skips != before + 1) return "defer skips " + c.Skips;
            if (!c.DueSettled(0.2, in k, false)) return "defer: not due again";

            // (e) counters
            c.Report(1, 0.3); c.Report(2, double.NaN); c.Report(3, 0.1);
            if (c.Checks != 3) return "checks " + c.Checks;
            if (c.MaxDpx != 0.3) return "maxdpx " + c.MaxDpx;
            c.Reset();
            if (c.Checks != 0 || c.Skips != 0 || c.MaxDpx != 0) return "Reset counters";
            if (c.Settled(in k, false)) return "Reset: first frame settled";

            // (f) zoom >= 18 stays log-only, also on settled views
            c = new MapProjectorCheck();
            for (int i = 0; i < 3; i++)
            {
                MapPxKey ki = Key(19, 20 * i);
                c.DueSettled(10 * i, in ki, false);
                c.DueSettled(10 * i + 0.1, in ki, false);
                c.Report(10 * i + 0.1, 4.0);
            }
            if (c.Reflect || !c.LogOnly) return "zoom 19 must be log only";
            return null;
        }
    }
}
