using System;

namespace VoiceBotBridge
{
    /// <summary>T-354: the flat world of stable 261021 (GeoPosition): 1 deg = 60 nm on both axes, no cos(lat), no wrap of dLon
    /// (as CoverageMath flat). The sphere stays in Common/GameApi (beta); WorldGeoGame picks by the world flag.</summary>
    internal static class WorldGeo
    {
        public static double FlatDistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            double n = (lat2 - lat1) * 60.0, e = (lon2 - lon1) * 60.0;   // T-354: flat world
            return Math.Sqrt(n * n + e * e);
        }

        /// <summary>0..360, clockwise from north.</summary>
        public static double FlatBearing(double lat1, double lon1, double lat2, double lon2)
        {
            double n = (lat2 - lat1) * 60.0, e = (lon2 - lon1) * 60.0;   // T-354: flat world
            return (Math.Atan2(e, n) * 180.0 / Math.PI + 360.0) % 360.0;
        }

        public static void FlatDestination(double lat1, double lon1, double brgDeg, double distNm, out double lat2, out double lon2)
        {
            double b = brgDeg * Math.PI / 180.0;
            lat2 = lat1 + distNm * Math.Cos(b) / 60.0;
            double l2 = lon1 + distNm * Math.Sin(b) / 60.0;
            lon2 = ((l2 + 540.0) % 360.0) - 180.0;   // the same normalisation as GameApi.Destination
        }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
            {
                if (FlatDistanceNm(phi, 10, phi, 11) != 60.0) return "east degree != 60 nm at " + phi;
                if (FlatDistanceNm(phi, 10, phi + 1, 10) != 60.0) return "north degree != 60 nm at " + phi;
                if (Math.Abs(FlatBearing(phi, 10, phi, 11) - 90.0) > 1e-12) return "east bearing at " + phi;
                if (Math.Abs(FlatBearing(phi, 10, phi - 1, 10) - 180.0) > 1e-12) return "south bearing at " + phi;
                foreach (double b in new[] { 0.0, 45.0, 90.0, 225.0, 315.0 })
                    foreach (double d in new[] { 0.2, 5.0, 75.0 })
                    {
                        FlatDestination(phi, 10, b, d, out double la, out double lo);
                        if (Math.Abs(FlatDistanceNm(phi, 10, la, lo) - d) > 1e-9) return "roundtrip distance " + phi + "/" + b + "/" + d;
                        double db = Math.Abs(FlatBearing(phi, 10, la, lo) - b);
                        if (Math.Min(db, 360.0 - db) > 1e-9) return "roundtrip bearing " + phi + "/" + b + "/" + d;
                    }
            }
            FlatDestination(60, 179.9, 90, 60, out _, out double wrap);
            if (Math.Abs(wrap - (-179.1)) > 1e-9) return "longitude wrap: " + wrap;
            return null;
        }
    }
}
