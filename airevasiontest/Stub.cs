namespace VoiceBotBridge
{
    /// <summary>The stand has no game: TacticsMath only needs this one static helper of GameApi.</summary>
    internal static class GameApi
    {
        public static double DistanceNm(double aLat, double aLon, double bLat, double bLon)
        {
            double n = (bLat - aLat) * 60.0, e = (bLon - aLon) * 60.0 * System.Math.Cos((aLat + bLat) / 2 * System.Math.PI / 180.0);
            return System.Math.Sqrt(n * n + e * e);
        }
    }
}
