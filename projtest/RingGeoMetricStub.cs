// T-337: stand-only copy of the T-321 pieces that MapPx.ArcMetric / ArcPointsMetric use (RingGeoMetric, RangeRingKin.OffsetMetric, the flat geodesic
// RangeRingMath.Offset). The real ones are in SeaPowerTacticalUI/RangeRingKin.cs + RangeRingMath.cs, which drag the whole ring model this stand does not
// compile. Keep the shape and the math in sync.
using System;

namespace VoiceBotBridge.Ui
{
    internal struct RingGeoMetric { public double LatNmPerDeg, LonNmPerDeg; public bool Valid; }

    internal static class RangeRingKin
    {
        public static void OffsetMetric(double lat0, double lon0, double bearingDeg, double nm, RingGeoMetric m, out double lat, out double lon)
        {
            double b = bearingDeg * Math.PI / 180.0;
            if (!m.Valid)
            {
                double cl = Math.Cos(lat0 * Math.PI / 180.0);
                if (!(cl >= 0.05)) cl = 0.05;
                lat = lat0 + nm / 60.0 * Math.Cos(b);
                lon = lon0 + nm / 60.0 * Math.Sin(b) / cl;
                return;
            }
            lat = lat0 + nm * Math.Cos(b) / m.LatNmPerDeg;
            lon = lon0 + nm * Math.Sin(b) / m.LonNmPerDeg;
        }
    }
}
