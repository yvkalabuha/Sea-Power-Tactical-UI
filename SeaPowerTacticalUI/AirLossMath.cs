using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075 item 6: the cause of a lost own aircraft. Pure: the facts come from the game layer (AirLossGame) and the tracker (hits).

    internal sealed class AirLossFacts
    {
        public double LastHitAgoS = -1, MissileGoneAgoS = -1, AltM = -1, FuelFrac = -1;   // -1 = unknown / never
        public string HitWeapon, HitByClass;
        public bool Landed, Destroyed;
        public bool Alive;   // the object still exists and is not destroyed (a bird in the landing circuit leaves the "in flight" list without being lost)
    }

    internal static class AirLossMath
    {
        public const double HitWindowS = 30, MissileWindowS = 10, CrashAltM = 15;

        /// <summary>shot_down | crash | fuel | unknown; null when the aircraft landed (no loss). Order: a hit within 30 s or a missile that was aimed at it gone within 10 s,
        /// then the altitude at 15 m or lower without hits, then no fuel, then unknown.</summary>
        public static string Cause(AirLossFacts f, out string weapon, out string byClass)
        {
            weapon = null; byClass = null;
            if (f == null || f.Landed) return null;
            if (f.LastHitAgoS >= 0 && f.LastHitAgoS <= HitWindowS) { weapon = f.HitWeapon; byClass = f.HitByClass; return "shot_down"; }
            if (f.MissileGoneAgoS >= 0 && f.MissileGoneAgoS <= MissileWindowS) return "shot_down";
            if (f.AltM >= 0 && f.AltM <= CrashAltM) return "crash";
            if (f.FuelFrac >= 0 && f.FuelFrac <= 1e-6) return "fuel";
            return "unknown";
        }

        public static string SelfTest()
        {
            string w, by;
            Func<AirLossFacts, string> c = f => Cause(f, out w, out by);
            if (Cause(new AirLossFacts { Landed = true, LastHitAgoS = 1 }, out w, out by) != null) return "landing is no loss";
            if (Cause(new AirLossFacts { LastHitAgoS = 5, HitWeapon = "AA-2", HitByClass = "MiG-21bis", AltM = 6000 }, out w, out by) != "shot_down" || w != "AA-2" || by != "MiG-21bis") return "hit -> shot_down with weapon and class";
            if (Cause(new AirLossFacts { LastHitAgoS = 30 }, out w, out by) != "shot_down") return "hit at 30 s";
            if (Cause(new AirLossFacts { LastHitAgoS = 31, AltM = 5000 }, out w, out by) != "unknown") return "hit at 31 s is too old";
            if (Cause(new AirLossFacts { MissileGoneAgoS = 4, AltM = 3000 }, out w, out by) != "shot_down" || w != null || by != null) return "missile gone -> shot_down without a weapon";
            if (Cause(new AirLossFacts { MissileGoneAgoS = 11, AltM = 3000 }, out w, out by) != "unknown") return "missile gone 11 s ago";
            if (Cause(new AirLossFacts { AltM = 15 }, out w, out by) != "crash") return "15 m -> crash";
            if (Cause(new AirLossFacts { AltM = 16, FuelFrac = 0.5 }, out w, out by) != "unknown") return "16 m is not a crash";
            if (Cause(new AirLossFacts { AltM = 0 }, out w, out by) != "crash") return "0 m -> crash";
            if (Cause(new AirLossFacts { AltM = -1 }, out w, out by) != "unknown") return "unknown altitude is not a crash";
            if (Cause(new AirLossFacts { AltM = 2000, FuelFrac = 0 }, out w, out by) != "fuel") return "no fuel";
            if (Cause(new AirLossFacts { AltM = 2000, FuelFrac = 0.3 }, out w, out by) != "unknown") return "unknown";
            if (Cause(new AirLossFacts { LastHitAgoS = 2, AltM = 5, FuelFrac = 0 }, out w, out by) != "shot_down") return "hit beats crash and fuel";
            if (Cause(new AirLossFacts { AltM = 5, FuelFrac = 0 }, out w, out by) != "crash") return "crash beats fuel";
            return null;
        }
    }
}
