namespace VoiceBotBridge
{
    /// <summary>
    /// T-355: the metric of the game for the geometry inside Common/ (restore course 75 nm, course_from_threat, evade_torpedo,
    /// the after-launch turn, acks and state). The UI mod fills the hooks (WorldGeoGame.cs: stable = flat, beta = the great circle).
    /// VoiceBotBridge (frozen) has no hook bodies: the compiler removes the calls, done = false, the great-circle helper as before (bit-identical).
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>Implemented only by the UI mod. done = true: nm is the answer; untouched: the great circle.</summary>
        static partial void MetricDistanceHook(double lat1, double lon1, double lat2, double lon2, ref double nm, ref bool done);
        static partial void MetricBearingHook(double lat1, double lon1, double lat2, double lon2, ref double deg, ref bool done);
        static partial void MetricDestinationHook(double lat1, double lon1, double brgDeg, double distNm, ref double lat2, ref double lon2, ref bool done);

        internal static double MetricDistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            double nm = 0; bool done = false;
            MetricDistanceHook(lat1, lon1, lat2, lon2, ref nm, ref done);
            return done ? nm : DistanceNm(lat1, lon1, lat2, lon2);
        }

        internal static double MetricBearing(double lat1, double lon1, double lat2, double lon2)
        {
            double deg = 0; bool done = false;
            MetricBearingHook(lat1, lon1, lat2, lon2, ref deg, ref done);
            return done ? deg : Bearing(lat1, lon1, lat2, lon2);
        }

        internal static void MetricDestination(double lat1, double lon1, double brgDeg, double distNm, out double lat2, out double lon2)
        {
            double la = 0, lo = 0; bool done = false;
            MetricDestinationHook(lat1, lon1, brgDeg, distNm, ref la, ref lo, ref done);
            if (done) { lat2 = la; lon2 = lo; return; }
            Destination(lat1, lon1, brgDeg, distNm, out lat2, out lon2);
        }
    }
}
