#if SAR_WIP
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-344: a deck helicopter of an ASW type with a non-ASW loadout (Ka-27 / Strike) gets no nominal ASW gear; real ASW loadouts keep it.
    // `aswtest t344deck <name>`.
    internal static class AswT344Deck
    {
        private static readonly string[] Names = { "strike-no-gear", "asw-nominal", "launching-name", "unknown-loadout", "csv-name" };

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "strike-no-gear": e = StrikeNoGear(); break;
                    case "asw-nominal": e = AswNominal(); break;
                    case "launching-name": e = LaunchingName(); break;
                    case "unknown-loadout": e = UnknownLoadout(); break;
                    default: e = CsvName(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static AswMath.DeckEntry Ka27(string state, string loadout, Dictionary<string, int> ammo, string csv, bool setField = true)
        {
            var d = new AswMath.DeckEntry
            {
                ShipUid = 61, ShipName = "Kuznetsov", Type = "wp_ka-27", Label = "Ka-27", Loadout = loadout, Ammo = ammo, State = state, Uid = -5, Key = "task:k",
                Helicopter = true, AswCapableType = true
            };
            if (setField) d.LoadoutAsw = AswMath.LoadoutAswOf(loadout, ammo, csv);
            return d;
        }

        private static bool Nominal(AswMember m) { return m.Passive == 12 && m.ActiveBuoys == 8 && m.Vlad == 4 && m.Torpedoes == 2 && m.Mad; }
        private static bool Bare(AswMember m) { return m.Passive == 0 && m.ActiveBuoys == 0 && m.Vlad == 0 && m.Torpedoes == 0 && !m.Mad; }

        private static string StrikeNoGear()
        {
            var m = AswMath.DeckToMember(Ka27("ready", "Strike", new Dictionary<string, int> { { "wp_fab-250", 4 } }, "ASWHunter,ASW"));
            if (!Bare(m)) return "gear on Strike: " + m.Passive + "/" + m.ActiveBuoys + "/" + m.Vlad + "/" + m.Torpedoes + " mad=" + m.Mad;
            if (!m.LoadKnown) return "load not known";
            m.BestCapacity = 10; m.Capacity = 10;
            string r = AswMath.ExclusionReason(m);
            if (r != "no_asw_load") return "exclusion " + r;
            if (AswStrikeMath.StrikeExclusion(m, false) == null) return "ASW strike admitted";
            return null;
        }

        private static string AswNominal()
        {
            var m = AswMath.DeckToMember(Ka27("ready", "ASW", new Dictionary<string, int> { { "wp_unknown_x", 4 } }, "ASWHunter,ASW"));
            if (!Nominal(m)) return "nominal gear lost: " + m.Passive + "/" + m.ActiveBuoys + "/" + m.Vlad + "/" + m.Torpedoes + " mad=" + m.Mad;
            m.BestCapacity = 10; m.Capacity = 10;
            string r = AswMath.ExclusionReason(m);
            if (r != null) return "exclusion " + r;
            return null;
        }

        private static string LaunchingName()
        {
            var s = AswMath.DeckToMember(Ka27("launching", "Strike", null, "ASWHunter,ASW"));
            if (!Bare(s) || !s.LoadKnown) return "Strike in the air got gear or unknown load";
            var a = AswMath.DeckToMember(Ka27("launching", "ASWHunter", null, "ASWHunter,ASW"));
            if (!Nominal(a)) return "ASWHunter in the air lost the nominal gear";
            return null;
        }

        private static string UnknownLoadout()
        {
            if (AswMath.LoadoutAswOf(null, null, "ASW") != null || AswMath.LoadoutAswOf("", null, "ASW") != null) return "unknown loadout must be null";
            if (!Nominal(AswMath.DeckToMember(Ka27("cooling", null, null, null)))) return "no nominal gear with the field set";
            if (!Nominal(AswMath.DeckToMember(Ka27("cooling", null, null, null, false)))) return "no nominal gear without the field";
            return null;
        }

        private static string CsvName()
        {
            if (AswMath.LoadoutAswOf("Sub", null, "Sub") != true) return "csv name not ASW";
            if (AswMath.LoadoutAswOf("Sub", null, "") != false) return "no csv: Sub must not be ASW";
            if (!Nominal(AswMath.DeckToMember(Ka27("ready", "Sub", null, "Sub")))) return "csv loadout lost the gear";
            if (!Bare(AswMath.DeckToMember(Ka27("ready", "Sub", null, "")))) return "non-csv loadout got gear";
            return null;
        }
    }
}
#endif
