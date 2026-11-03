using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-343 stream C: an ASW-capable TYPE = one of its loadouts is ASW or its dipping sonar is loaded; roles alone never (Ka-25Ts: MPA, AEW loadout only).
    // `aswtest t343elig <name>`.
    internal static class AswT343Elig
    {
        private static readonly string[] Names = { "type-asw", "ka25ts-deck" };
        private const string Csv = "ASWHunter,ASW";

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e = n == "type-asw" ? TypeAsw() : Ka25tsDeck();
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static LoadoutOption L(string name, params string[] ammo)
        {
            var lo = new LoadoutOption { Name = name, Display = name, Weapons = new Dictionary<string, int>() };
            foreach (var a in ammo) lo.Weapons[a] = 3;
            return lo;
        }

        private static string TypeAsw()
        {
            var ka27 = new[] { L("ASW", "rgb-nm"), L("Strike", "fab"), L("Empty") };
            var sh3h = new[] { L("ASW"), L("Empty") };
            var sh2f = new[] { L("ASW"), L("ASWBomb"), L("Empty") };
            var qh50 = new[] { L("ASW") };
            var s3a = new[] { L("ASW_Hunter"), L("Empty") };
            var ka29 = new[] { L("Strike", "fab"), L("CAS", "x"), L("AntiArmor", "y"), L("Empty") };
            var ka25ts = new[] { L("AEW") };
            var ch46 = new[] { L("Transport") };
            var md500 = new[] { L("AntiArmor", "tow"), L("Empty") };
            foreach (var t in new[] { ka27, sh3h, sh2f, qh50, s3a })
                if (!AswMath.TypeAswCapable(t, Csv, null)) return "ASW type rejected: " + t[0].Name;
            foreach (var t in new[] { ka29, ka25ts, ch46, md500 })
                if (AswMath.TypeAswCapable(t, Csv, null)) return "non-ASW type admitted: " + t[0].Name;
            if (!AswMath.TypeAswCapable(ka25ts, Csv, true)) return "dipping sonar must admit";
            if (AswMath.TypeAswCapable(ka25ts, Csv, false)) return "dipping=false must not admit";
            if (AswMath.TypeAswCapable(null, Csv, null)) return "null list admitted";
            // a type with buoys / torpedoes in a loadout but no ASW in the name
            if (!AswMath.TypeAswCapable(new[] { L("Patrol", "usn_ssq-53") }, "", null)) return "buoy loadout must admit";
            return null;
        }

        private static string Ka25tsDeck()
        {
            var d = new AswMath.DeckEntry
            {
                ShipUid = 61, ShipName = "Kuznetsov", Type = "wp_ka-25ts", Label = "Ka-25Ts", Loadout = "AEW", Roles = new[] { "Targeting", "MPA", "SAR" },
                Count = 1, Slot = 1, State = "hangar", ReadyInS = 900, Uid = AswMath.HangarSlotUid(61, "wp_ka-25ts", 1), Key = AswMath.SlotKey("wp_ka-25ts", 1),
                Ammo = new Dictionary<string, int>(), HangarPrepS = 900, Helicopter = true,
                AswCapableType = AswMath.TypeAswCapable(new[] { L("AEW") }, Csv, null)
            };
            var m = AswMath.DeckToMember(d);
            m.BestCapacity = 10; m.Capacity = 10;
            string r;
            if ((r = AswMath.ExclusionReason(m)) != "no_asw_load") return "Ka-25Ts ASW search: " + r;
            if (AswStrikeMath.StrikeExclusion(m, false) == null) return "Ka-25Ts ASW strike admitted";
            if ((r = AswMath.SarExclusionReason(m)) != null) return "Ka-25Ts SAR: " + r;
            return null;
        }
    }
}
