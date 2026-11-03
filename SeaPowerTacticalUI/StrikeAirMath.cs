using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132: which aircraft and which missiles the strike window may use (T-340 recon section 1.4). Pure.
    internal static class StrikeAirMath
    {
        /// <summary>Ranges under this are not anti-ship missiles for the strike (ATGM, AGM-65 class).</summary>
        public const double MinAsmRangeNm = 15;

        private static bool Is(string v, params string[] names)
        {
            if (v == null) return false;
            foreach (string n in names) if (string.Equals(v, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>asm | illum | tv | arm | short | unguided | not_asuw. Guidance / midcourse are the names of TacticsMath (arh, ir, tv, arm, none; radio, datalink, wire) or the enum names of the game.</summary>
        public static string MissileClass(StrikeMissile m)
        {
            if (m == null) return "not_asuw";
            if (!Is(m.Type, "Missile") || !Is(m.TargetType, "ASuW")) return "not_asuw";
            if (m.AntiRadiation || Is(m.Guidance, "arm", "AntiRadiation", "AntiRadiationHoming")) return "arm";
            if (m.Guidance == null || Is(m.Guidance, "none", "unguided", "0")) return "unguided";
            if (m.MaxNm < MinAsmRangeNm) return "short";
            if (Is(m.Guidance, "tv", "Television", "Optical", "laser", "LaserGuided")) return "tv";
            if (!Is(m.Guidance, "arh", "ActiveRadarHoming", "ir", "IRHoming", "IR")) return "not_asuw";
            if (Is(m.MidCourse, "radio", "RadioCommand")) return "illum";
            return "asm";
        }

        public static string ClassOf(StrikeMissile m) { return m == null ? "not_asuw" : (m.Klass ?? MissileClass(m)); }

        /// <summary>A missile the strike can plan with: an anti-ship missile (asm) or one that needs the carrier to illuminate (illum).</summary>
        public static bool IsStrikeMissile(StrikeMissile m) { string k = ClassOf(m); return k == "asm" || k == "illum"; }

        /// <summary>Sea skimmer by cruise altitude (the profile is fixed in the data, the player cannot choose it).</summary>
        public static bool IsSeaSkim(StrikeMissile m) { return m != null && m.CruiseAltFt > 0 && m.CruiseAltFt <= 500; }

        public static bool IsEwStation(string sensorType) { return Is(sensorType, "Offensive", "Universal"); }

        /// <summary>null = a striker; helicopter | no_asm | tv_only | no_ammo | mount_inoperable.</summary>
        public static string AircraftWhy(StrikeAircraft a)
        {
            if (a == null) return "no_asm";
            if (Is(a.ObjType, "Helicopter")) return "helicopter";
            bool usable = false, tv = false, ammo = false, operable = false;
            if (a.Missiles != null)
                foreach (var m in a.Missiles)
                {
                    string k = ClassOf(m);
                    if (k == "tv") tv = true;
                    if (k != "asm" && k != "illum") continue;
                    usable = true;
                    if (m.Count > 0) { ammo = true; if (m.Operable) operable = true; }
                }
            if (!usable) return tv ? "tv_only" : "no_asm";
            if (!ammo) return "no_ammo";
            if (!operable) return "mount_inoperable";
            return null;
        }

        /// <summary>striker | ew | excluded. ew = no usable missile but a station of an offensive jammer.</summary>
        public static string RoleOf(StrikeAircraft a)
        {
            string why = AircraftWhy(a);
            if (why == null) return "striker";
            if (why != "helicopter" && a != null && a.Ew != null && StrikeEwMath.EwWhy(a) == null) return "ew";
            return "excluded";
        }

        /// <summary>Fills Role and Why of every aircraft (the game layer calls it after reading).</summary>
        public static void Classify(IList<StrikeAircraft> list)
        {
            if (list == null) return;
            foreach (var a in list)
            {
                if (a == null) continue;
                if (a.Missiles != null) foreach (var m in a.Missiles) { m.Klass = MissileClass(m); m.NeedsIllum = m.Klass == "illum"; m.SeaSkim = IsSeaSkim(m); }
                a.Why = AircraftWhy(a);
                a.Role = RoleOf(a);
                if (a.Role == "ew") a.Why = null;
                else if (a.Role == "excluded" && a.Ew != null && a.Why != "helicopter") a.Why = StrikeEwMath.EwWhy(a) ?? a.Why;   // phase D: ew_no_api | ew_inoperable
            }
        }

        /// <summary>The number of usable missiles of the aircraft (operable mounts only).</summary>
        public static int UsableCount(StrikeAircraft a)
        {
            int n = 0;
            if (a != null && a.Missiles != null) foreach (var m in a.Missiles) if (IsStrikeMissile(m) && m.Operable) n += Math.Max(0, m.Count);
            return n;
        }

        // ---- self test ----
        private static StrikeMissile M(string ammo, string guidance, string mid, double minNm, double maxNm, int count = 2, bool arm = false, bool req = false, string type = "Missile", string target = "ASuW", bool operable = true)
        {
            return new StrikeMissile { Ammo = ammo, Name = ammo, Type = type, TargetType = target, Guidance = guidance, MidCourse = mid, MinNm = minNm, MaxNm = maxNm, Count = count, AntiRadiation = arm, TargetRequired = req, Operable = operable };
        }

        private static StrikeAircraft A(string type, string objType, params StrikeMissile[] ms)
        {
            var a = new StrikeAircraft { Type = type, ObjType = objType, Airborne = true };
            a.Missiles.AddRange(ms);
            return a;
        }

        public static string SelfTest()
        {
            var table = new List<(StrikeMissile m, string klass)>
            {
                (M("usn_agm-84a", "arh", "none", 3.5, 65), "asm"), (M("usn_agm-84c", "arh", "none", 3.5, 65), "asm"), (M("usn_agm-84d", "arh", "none", 3.5, 118), "asm"),
                (M("fr_am-39", "arh", "none", 6, 35), "asm"), (M("is_gabriel_3as", "arh", "none", 3, 35), "asm"), (M("jasdf_asm-1", "arh", "none", 6, 27), "asm"),
                (M("pla_yj-6", "arh", "none", 4.3, 54), "asm"), (M("pla_yj-1", "arh", "none", 3.5, 40), "asm"),
                (M("wp_as-4", "arh", "none", 100, 250, 1, false, true), "asm"), (M("wp_as-6", "arh", "none", 100, 160, 2, false, true), "asm"),
                (M("wp_as-2", "arh", "radio", 35, 150, 1, false, true), "illum"),
                (M("wp_as-13", "tv", "datalink", 2.5, 21.6), "tv"),
                (M("usn_agm-88", "arm", "none", 4, 80, 2, true), "arm"), (M("usn_agm-45", "arm", "none", 2, 25, 2, true), "arm"),
                (M("usn_agm-65", "ir", "none", 1, 8), "short"), (M("usn_bgm-71", "tv", "wire", 0.5, 2), "short"),
                (M("wp_as-4b", "none", "none", 100, 250), "unguided"),
                (M("usn_aim-9", "ir", "none", 1, 10, 2, false, false, "Missile", "AAW"), "not_asuw"),
                (M("mk82", "none", "none", 0, 1, 4, false, false, "Bomb", "ASuW"), "not_asuw"),
            };
            foreach (var t in table)
            {
                string k = MissileClass(t.m);
                if (k != t.klass) return "class " + t.m.Ammo + " = " + k + ", want " + t.klass;
            }
            if (MissileClass(M("x", "ActiveRadarHoming", "RadioCommand", 35, 150)) != "illum") return "enum names";
            if (!IsEwStation("Offensive") || !IsEwStation("Universal") || IsEwStation("Defensive")) return "ew station";
            if (!IsSeaSkim(new StrikeMissile { CruiseAltFt = 50 }) || IsSeaSkim(new StrikeMissile { CruiseAltFt = 3000 })) return "sea skim";

            var harpoon = M("usn_agm-84c", "arh", "none", 3.5, 65, 4);
            string w;
            if ((w = AircraftWhy(A("usn_a-6e", "Aircraft", harpoon))) != null) return "A-6E why " + w;
            if (RoleOf(A("usn_a-6e", "Aircraft", harpoon)) != "striker") return "A-6E role";
            if ((w = AircraftWhy(A("fr_sa-321", "Helicopter", M("fr_am-39", "arh", "none", 6, 35)))) != "helicopter") return "helicopter " + w;
            if ((w = AircraftWhy(A("wp_su-24m", "Aircraft", M("wp_as-13", "tv", "datalink", 2.5, 21.6)))) != "tv_only") return "su-24m " + w;
            if ((w = AircraftWhy(A("usn_ea-6b", "Aircraft", M("usn_agm-88", "arm", "none", 4, 80, 2, true)))) != "no_asm") return "ea-6b " + w;
            if ((w = AircraftWhy(A("usn_a-6e", "Aircraft", M("usn_agm-84c", "arh", "none", 3.5, 65, 0)))) != "no_ammo") return "no ammo " + w;
            if ((w = AircraftWhy(A("usn_a-6e", "Aircraft", M("usn_agm-84c", "arh", "none", 3.5, 65, 4, false, false, "Missile", "ASuW", false)))) != "mount_inoperable") return "inoperable " + w;
            var ea = A("usn_ea-6b", "Aircraft");
            ea.Ew = new StrikeEw { RangeNm = 200, ConeDeg = 30, Bands = new[] { "X" } };
            if (RoleOf(ea) != "ew") return "ew role";
            var heli = A("h", "Helicopter"); heli.Ew = ea.Ew;
            if (RoleOf(heli) != "excluded") return "heli ew excluded";
            var eaOff = A("usn_ea-6b", "Aircraft"); eaOff.Ew = new StrikeEw { RangeNm = 200, ConeDeg = 30, Operable = false };
            var eaNoApi = A("usn_ea-6b", "Aircraft"); eaNoApi.Ew = new StrikeEw { RangeNm = 200, ConeDeg = 30, Capable = false };
            var l2 = new List<StrikeAircraft> { eaOff, eaNoApi, ea, heli }; Classify(l2);
            if (eaOff.Role != "excluded" || eaOff.Why != "ew_inoperable" || eaNoApi.Why != "ew_no_api" || ea.Role != "ew" || ea.Why != null || heli.Why != "helicopter") return "ew why";
            if (RoleOf(A("none", "Aircraft")) != "excluded") return "no missiles excluded";
            var mixed = A("mix", "Aircraft", M("wp_as-13", "tv", "datalink", 2.5, 21.6, 2), harpoon);
            if (AircraftWhy(mixed) != null || UsableCount(mixed) != 4) return "mixed load";
            var list = new List<StrikeAircraft> { mixed, ea };
            Classify(list);
            if (mixed.Role != "striker" || mixed.Missiles[0].Klass != "tv" || mixed.Missiles[1].Klass != "asm" || ea.Role != "ew") return "classify";
            return null;
        }
    }
}
