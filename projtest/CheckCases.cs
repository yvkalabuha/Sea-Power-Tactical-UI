// T-309 B1: the run-time check policy (MapProjectorCheck): due times, three strikes -> reflect, the zoom guard, probes, the log line.
using System;
using System.Globalization;

namespace VoiceBotBridge.ProjTest
{
    internal static class CheckCases
    {
        private static MapPxKey Key(double zoom, double dvx = 0, double lat = 30)
        {
            double scale = GameRef.ZoomLevelToScale((float)zoom);
            MapViewSnap s = MapViewSnap.Ortho(lat, 40, scale, 10, lat, 40, 960 + dvx, 540, 1920, 1080);
            return MapPx.Create(in s, new double[] { 1, 0, 0, 0, 1, 0 }, 1920, 1080).Key;
        }

        public static string Run()
        {
            // Due: the first snapshot; the same key never; a new key only after 2 s
            var c = new MapProjectorCheck();
            MapPxKey k1 = Key(10), k2 = Key(10, 5);
            if (!c.Due(0, in k1)) return "first snapshot must be due";
            if (c.Due(0.5, in k1) || c.Due(10, in k1)) return "same key must not be due";
            if (c.Due(1.0, in k2)) return "new key before 2 s must not be due";
            if (!c.Due(2.5, in k2)) return "new key after 2 s must be due";
            if (c.Due(3.0, in k2)) return "just checked";

            // three mismatches in a row -> Reflect; two and a good one -> no
            c = new MapProjectorCheck();
            c.Due(0, in k1);
            c.Report(0, 1.5); c.Report(2, 2.0);
            if (c.Reflect || c.Mismatches != 2) return "two strikes switched or miscounted: " + c.Mismatches;
            c.Report(4, 0.2);
            if (c.Reflect || c.LastDpx != 0.2) return "a good check must reset";
            c.Report(6, 1.1); c.Report(8, 1.1);
            if (c.Reflect) return "strikes were not reset by the good check";
            c.Report(10, 3.0);
            if (!c.Reflect) return "three strikes must switch to reflect";
            if (c.Due(100, in k2)) return "no checks after the switch";
            c.Reset();
            if (c.Reflect || c.Mismatches != 0 || !c.Due(0, in k1)) return "Reset";
            // exactly 1.0 px is fine; NaN is a mismatch
            c.Report(0, 1.0); c.Report(0, 1.0); c.Report(0, 1.0);
            if (c.Reflect || c.Mismatches != 0) return "1.0 px counted as mismatch";
            c.Report(0, double.NaN); c.Report(0, double.NaN); c.Report(0, double.NaN);
            if (!c.Reflect) return "NaN not a mismatch";

            // zoom >= 18 (Q3): log only, never reflect
            var z = new MapProjectorCheck();
            MapPxKey kz = Key(19);
            z.Due(0, in kz);
            z.Report(0, 2); z.Report(2, 2); z.Report(4, 2); z.Report(6, 2);
            if (z.Reflect || !z.LogOnly || z.Mismatches != 4) return "zoom 19: reflect " + z.Reflect + " logonly " + z.LogOnly;
            var y = new MapProjectorCheck();
            MapPxKey ky = Key(17.9);
            y.Due(0, in ky); y.Report(0, 2); y.Report(2, 2); y.Report(4, 2);
            if (!y.Reflect) return "zoom 17.9 must still switch";

            // Probes: the view centre and two points at 40 % of the view radius; all on the disc and in the view
            MapViewSnap s = MapViewSnap.Ortho(30, 40, GameRef.ZoomLevelToScale(10), 25, 30, 40, 960, 540, 1920, 1080);
            var lat = new double[3]; var lon = new double[3];
            int n = MapProjectorCheck.Probes(in s, lat, lon);
            if (n != 3) return "probes " + n;
            double cx, cy;
            MapProjector.GeoToView(in s, lat[0], lon[0], out cx, out cy);
            if (Math.Abs(cx - 960) > 1e-6 || Math.Abs(cy - 540) > 1e-6) return "probe 0 is not the centre " + cx + "," + cy;
            for (int i = 1; i < 3; i++)
            {
                double x, yy;
                if (!MapProjector.GeoToView(in s, lat[i], lon[i], out x, out yy)) return "probe hidden";
                double d = Math.Sqrt((x - 960) * (x - 960) + (yy - 540) * (yy - 540));
                if (Math.Abs(d - 0.4 * 540) > 1e-6) return "probe " + i + " at " + d + " px";
            }
            // a view bigger than the disc: off-disc probes are dropped, not returned as garbage
            MapViewSnap full = MapViewSnap.Ortho(0, 0, 1080.0 / (2 * MapProjector.R) * 0.3, 0, 0, 0, 960, 540, 1920, 1080);
            int nf = MapProjectorCheck.Probes(in full, lat, lon);
            if (nf < 1 || nf > 3) return "probes on a small disc " + nf;

            // LogLine
            string line = c.LogLine(in s, 1.2345);
            string want = "map projector mismatch kind=reflect dpx=1.235 zoom=" + MapProjectorCheck.ZoomOf(s.Scale).ToString("0.##", CultureInfo.InvariantCulture) + " rot=25";
            if (line != want) return "LogLine '" + line + "' vs '" + want + "'";
            var fresh = new MapProjectorCheck();
            if (!fresh.LogLine(in s, 1).StartsWith("map projector mismatch kind=ortho ", StringComparison.Ordinal)) return "LogLine ortho";
            MapViewSnap aff = MapViewSnap.Affine(new Affine2 { A = 1, E = 1 }, 100, 100);
            if (!fresh.LogLine(in aff, 1).Contains("kind=affine")) return "LogLine affine";
            if (Math.Abs(MapProjectorCheck.ZoomOf(GameRef.ZoomLevelToScale(12)) - 12) > 1e-6) return "ZoomOf";
            return null;
        }
    }
}
