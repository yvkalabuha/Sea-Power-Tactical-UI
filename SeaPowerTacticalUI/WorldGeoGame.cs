using System;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        /// <summary>T-354: the metric of the game. Beta (AswMath.WorldGlobe) = the Common great-circle helper itself (bit-identical);
        /// stable = WorldGeo flat. Main thread or any thread (volatile read).</summary>
        internal static double WorldDistanceNm(double lat1, double lon1, double lat2, double lon2)
        { return AswMath.WorldGlobe ? DistanceNm(lat1, lon1, lat2, lon2) : WorldGeo.FlatDistanceNm(lat1, lon1, lat2, lon2); }

        internal static double WorldBearing(double lat1, double lon1, double lat2, double lon2)
        { return AswMath.WorldGlobe ? Bearing(lat1, lon1, lat2, lon2) : WorldGeo.FlatBearing(lat1, lon1, lat2, lon2); }

        internal static void WorldDestination(double lat1, double lon1, double brgDeg, double distNm, out double lat2, out double lon2)
        {
            if (AswMath.WorldGlobe) { Destination(lat1, lon1, brgDeg, distNm, out lat2, out lon2); return; }
            WorldGeo.FlatDestination(lat1, lon1, brgDeg, distNm, out lat2, out lon2);
        }

        /// <summary>T-354 selfcheck-ui: 0/45/60/70 deg, both worlds (AswMath.WorldScope). null = ok.</summary>
        internal static string WorldGeoSelfTest()
        {
            bool before = AswMath.WorldGlobe;
            foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
                foreach (double b in new[] { 0.0, 45.0, 90.0, 225.0 })
                    foreach (double d in new[] { 0.2, 5.0, 75.0 })
                    {
                        Destination(phi, 10, b, d, out double tla, out double tlo);
                        using (AswMath.WorldScope(true))
                        {
                            if (WorldDistanceNm(phi, 10, tla, tlo) != DistanceNm(phi, 10, tla, tlo)) return "globe distance " + phi + "/" + b + "/" + d;
                            if (WorldBearing(phi, 10, tla, tlo) != Bearing(phi, 10, tla, tlo)) return "globe bearing " + phi + "/" + b + "/" + d;
                            WorldDestination(phi, 10, b, d, out double wl, out double wo);
                            if (wl != tla || wo != tlo) return "globe destination " + phi + "/" + b + "/" + d;
                        }
                        using (AswMath.WorldScope(false))
                        {
                            if (WorldDistanceNm(phi, 10, tla, tlo) != WorldGeo.FlatDistanceNm(phi, 10, tla, tlo)) return "flat distance " + phi + "/" + b + "/" + d;
                            if (WorldBearing(phi, 10, tla, tlo) != WorldGeo.FlatBearing(phi, 10, tla, tlo)) return "flat bearing " + phi + "/" + b + "/" + d;
                            WorldDestination(phi, 10, b, d, out double wl, out double wo);
                            WorldGeo.FlatDestination(phi, 10, b, d, out double fl, out double fo);
                            if (wl != fl || wo != fo) return "flat destination " + phi + "/" + b + "/" + d;
                        }
                    }
            foreach (double phi in new[] { 0.0, 70.0 })
            {
                Destination(phi, 10, 90, 75, out _, out double sphLon);
                WorldGeo.FlatDestination(phi, 10, 90, 75, out _, out double flatLon);
                double ratio = (sphLon - 10) / (flatLon - 10);
                double want = 1.0 / Math.Cos(phi * Math.PI / 180.0);
                if (Math.Abs(ratio / want - 1.0) > (phi == 0.0 ? 1e-3 : 0.01)) return "contrast at " + phi + ": " + ratio;
            }
            if (AswMath.WorldGlobe != before) return "world flag not restored";
            return null;
        }
    }
}
