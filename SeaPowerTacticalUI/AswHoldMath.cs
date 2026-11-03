using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-331: the waiting point of the ASW attack (the second number and a held shooter). The point is anchored on the bearing at which it was first set:
    /// an aircraft that circles round the point changes its own bearing from the datum all the time, and a new point every minute is a new
    /// "We're on position" message of the game each time the aircraft reaches the previous one. No game, no Unity.
    /// </summary>
    internal static class AswHoldMath
    {
        /// <summary>The aircraft circles around its point: it is not a reason to move the point.</summary>
        public const double LoiterNm = 2.0;

        /// <summary>Bearing of the anchor: the kept one while the datum moved less than regotoNm since it was taken; else the bearing of the aircraft from the new datum.</summary>
        public static double AnchorBrg(double? keptBrg, GeoPt keptDatum, GeoPt datum, GeoPt aircraft, double regotoNm)
        {
            if (keptBrg.HasValue && AswMath.DistNm(keptDatum, datum) < regotoNm) return keptBrg.Value;
            return AswMath.BearingDeg(datum, aircraft);
        }

        /// <summary>Order the waiting point again: the first time; the datum moved by regotoNm or more; or regotoS passed and the aircraft is farther than LoiterNm from the point.</summary>
        public static bool NeedRegoto(bool first, double sinceS, double datumMovedNm, double distToPointNm, double regotoNm, double regotoS)
        {
            if (first) return true;
            if (datumMovedNm >= regotoNm) return true;
            return sinceS >= regotoS && distToPointNm > LoiterNm;
        }
    }
}
