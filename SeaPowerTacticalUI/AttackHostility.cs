using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// 0.27.10: the one hostility rule of the UI mod's attack paths (ASW proposal, ASW attack execution, dev fire guard).
    /// The attack follows what the player sees: the relationship reads "hostile", OR the main map paints the track red
    /// (MapContactVisuals / ViewModel posture "Enemy") while the relationship still reads "unknown". Neutral and friendly
    /// tracks are never attackable, even when painted red; an unknown track that the map does not paint red is refused too.
    /// Pure (BCL only): compiled into the UI mod, the dev DLL and the offline tests.
    /// </summary>
    internal static class AttackHostility
    {
        public static bool IsEnemyPosture(string p)
        {
            return p != null && (p.Equals("Enemy", StringComparison.OrdinalIgnoreCase) || p.Equals("Hostile", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The main map shows the track as hostile (red).</summary>
        public static bool MapHostile(string mapPosture, string vmPosture)
        {
            return IsEnemyPosture(mapPosture) || IsEnemyPosture(vmPosture);
        }

        /// <summary>The rule itself: side "hostile", or side "unknown"/missing and the map paints the track hostile.</summary>
        public static bool AttackOk(string side, bool mapHostile)
        {
            string s = side == null ? "unknown" : side.ToLowerInvariant();
            if (s == "hostile") return true;
            if (s == "neutral" || s == "friendly" || s == "none") return false;
            return mapHostile;
        }

        public static bool AttackOk(string side, string mapPosture, string vmPosture)
        {
            return AttackOk(side, MapHostile(mapPosture, vmPosture));
        }

        /// <summary>
        /// Which execution path an attack on this track takes: "relation" = the shared Attack (relationship already hostile),
        /// "map" = the UI mod's own engage path (unknown relationship, map red), "not_hostile" = refused.
        /// </summary>
        public static string Route(string side, bool mapHostile)
        {
            if (!AttackOk(side, mapHostile)) return "not_hostile";
            return side != null && side.Equals("hostile", StringComparison.OrdinalIgnoreCase) ? "relation" : "map";
        }

        private static bool IsPosture(string p, string a, string b)
        {
            return p != null && (p.Equals(a, StringComparison.OrdinalIgnoreCase) || (b != null && p.Equals(b, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// T-173 (ADR-0074 item 3): the rule of the strike group. null = a target; else neutral | friendly | not_hostile.
        /// Hostile side or a track the map paints red: ok. An unidentified track (side unknown): ok with policy "hostile_unknown" unless the player's posture
        /// on the map says Neutral / Friendly. Neutral, friendly and "none" are never targets, even when painted red. AttackOk stays as it is.
        /// </summary>
        public static string StrikeOk(string side, string mapPosture, string vmPosture, string policy)
        {
            string s = side == null ? "unknown" : side.ToLowerInvariant();
            if (s == "neutral") return "neutral";
            if (s == "friendly") return "friendly";
            if (s == "none") return "not_hostile";
            if (s == "hostile") return null;
            if (MapHostile(mapPosture, vmPosture)) return null;
            if (IsPosture(mapPosture, "Neutral", null) || IsPosture(vmPosture, "Neutral", null)) return "neutral";
            if (IsPosture(mapPosture, "Friendly", "Friend") || IsPosture(vmPosture, "Friendly", "Friend") || IsPosture(mapPosture, "Allied", "Ally") || IsPosture(vmPosture, "Allied", "Ally")) return "friendly";
            return policy == "hostile_unknown" ? null : "not_hostile";
        }

        private static string StrikeSelfTest()
        {
            if (StrikeOk("hostile", null, null, "hostile") != null) return "strike hostile";
            if (StrikeOk("unknown", "Unknown", null, "hostile_unknown") != null) return "strike unknown ok";
            if (StrikeOk("unknown", "Unknown", null, "hostile") != "not_hostile") return "strike unknown, hostile only";
            if (StrikeOk("unknown", null, null, "hostile_unknown") != null) return "strike unknown no posture";
            if (StrikeOk("unknown", "Neutral", null, "hostile_unknown") != "neutral" || StrikeOk("unknown", null, "Neutral", "hostile_unknown") != "neutral") return "strike neutral posture";
            if (StrikeOk("unknown", "Friendly", null, "hostile_unknown") != "friendly" || StrikeOk("unknown", null, "Friendly", "hostile_unknown") != "friendly") return "strike friendly posture";
            if (StrikeOk("unknown", "Enemy", null, "hostile") != null) return "strike unknown red";
            if (StrikeOk("neutral", "Enemy", "Enemy", "hostile_unknown") != "neutral") return "strike neutral painted red";
            if (StrikeOk("friendly", "Enemy", null, "hostile_unknown") != "friendly") return "strike friendly painted red";
            if (StrikeOk("none", "Enemy", null, "hostile_unknown") != "not_hostile") return "strike none";
            if (StrikeOk(null, "Unknown", null, "hostile_unknown") != null) return "strike null side";
            if (AttackOk("unknown", "Unknown", null)) return "AttackOk unchanged";
            return null;
        }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            string se = StrikeSelfTest();
            if (se != null) return se;
            if (!AttackOk("hostile", null, null)) return "hostile side";
            if (!AttackOk("unknown", "Enemy", null)) return "unknown + map red";
            if (!AttackOk("unknown", null, "Enemy")) return "unknown + vm red";
            if (!AttackOk(null, "Hostile", null)) return "null side + red";
            if (AttackOk("unknown", "Unknown", null)) return "unidentified grey";
            if (AttackOk("unknown", null, null)) return "unidentified no posture";
            if (AttackOk("neutral", "Enemy", "Enemy")) return "neutral painted red";
            if (AttackOk("friendly", "Enemy", null)) return "friendly painted red";
            if (AttackOk("none", true)) return "no track";
            if (!AttackOk("Hostile", false)) return "case";
            if (Route("hostile", false) != "relation") return "route hostile";
            if (Route("unknown", true) != "map") return "route map";
            if (Route("unknown", false) != "not_hostile" || Route("neutral", true) != "not_hostile") return "route refused";
            return null;
        }
    }
}
