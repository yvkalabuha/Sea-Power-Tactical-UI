using System;

namespace VoiceBotBridge
{
    /// <summary>T-133: the two static geo helpers of the real GameApi used by OverviewMath.MakeRow. Copy of Common/GameApi.cs (keep in sync).</summary>
    internal sealed class GameApi
    {
        public readonly System.Collections.Generic.HashSet<string> Capabilities = new System.Collections.Generic.HashSet<string>();
        private const double Rad = Math.PI / 180.0;

        internal static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double f1 = lat1 * Rad, f2 = lat2 * Rad, dl = (lon2 - lon1) * Rad;
            double y = Math.Sin(dl) * Math.Cos(f2);
            double x = Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl);
            return ((Math.Atan2(y, x) / Rad) + 360.0) % 360.0;
        }

        internal static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            double f1 = lat1 * Rad, f2 = lat2 * Rad, df = f2 - f1, dl = (lon2 - lon1) * Rad;
            double a = Math.Sin(df / 2) * Math.Sin(df / 2) + Math.Cos(f1) * Math.Cos(f2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(a))) / Rad * 60.0;
        }
    }
}
