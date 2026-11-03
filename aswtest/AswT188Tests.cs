using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-188: the dipping sonar of a TYPE from its ini (what the game loads), the deck row built from it, and the controller scenarios (AswT188Ctl).</summary>
    internal static class AswT188Tests
    {
        private static readonly string[] Names = { "ini_ka27", "ini_ka27_strike", "ini_ka27_empty", "ini_ka27_default", "ini_mi14", "ini_loadout_sections", "ini_sh3h", "ini_sh2f", "ini_removed", "ini_parse", "deck_typed", "assign_typed", "refused", "refused_buoys", "refused_type", "other_error", "area_refused", "ui_state" };

        public static string Run(string only)
        {
            string e;
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                e = Case(n);
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string Case(string n)
        {
            switch (n)
            {
                case "ini_ka27": return IniKa27();
                case "ini_ka27_strike": return IniKa27Strike();
                case "ini_ka27_empty": return IniKa27Empty();
                case "ini_ka27_default": return IniKa27Default();
                case "ini_mi14": return IniMi14();
                case "ini_loadout_sections": return IniLoadoutSections();
                case "ini_sh3h": return IniSh3h();
                case "ini_sh2f": return IniSh2f();
                case "ini_removed": return IniRemoved();
                case "ini_parse": return IniParse();
                case "deck_typed": return DeckTyped();
                case "assign_typed": return AssignTyped();
                default: return AswT188Ctl.Run(n);
            }
        }

        // ---- a fake ini: section -> key -> value ----
        private static AswDipTasks.DipSonarInfo Read(Dictionary<string, Dictionary<string, string>> ini, string loadout)
        {
            return Read(ini, loadout, null);
        }

        private static AswDipTasks.DipSonarInfo Read(Dictionary<string, Dictionary<string, string>> ini, string loadout, string nation)
        {
            return AswDipTasks.DipSonarOfIni(
                s => ini.ContainsKey(s),
                (s, k) => { Dictionary<string, string> sec; string v; return ini.TryGetValue(s, out sec) && sec.TryGetValue(k, out v) ? v : null; },
                loadout,
                name => name == "AQS-13E" || name == "VGS-3",
                nation);
        }

        private static Dictionary<string, string> S(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        private static Dictionary<string, Dictionary<string, string>> Ka27()
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                { "SensorSystems", S("NumberOfSensorSystems", "2") },
                { "SensorSystem1", S("Type", "Radar", "SystemName", "Initsiativa") },
                { "SensorSystem2", S("Type", "Sonar", "SystemName", "RGB-NM") },
                { "SensorSystem3", S("Type", "Sonar", "SystemName", "VGS-3") },
                { "WeaponSystems", S("NumberOfWeaponSystems", "3") },
                { "WeaponSystem1", S("Type", "Hardpoint", "SystemName", "Hardpoint") },
                { "WeaponSystem1ASW", S("EnableSensorSystems", "SensorSystem3") },
                { "WeaponSystem1Strike", S() },
                { "WeaponSystem1Empty", S("EnableSensorSystems", "SensorSystem3") },
                { "WeaponSystem3", S("Type", "Chaff") },
            };
        }

        private static Dictionary<string, Dictionary<string, string>> Sh3h()
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                { "SensorSystems", S("NumberOfSensorSystems", "4") },
                { "SensorSystem1", S("Type", "Radar", "SystemName", "APS-116") },
                { "SensorSystem2", S("Type", "Sonar", "SystemName", "AQS-13E") },
                { "SensorSystem3", S("Type", "MAD", "SystemName", "ASQ-81") },
                { "SensorSystem4", S("Type", "Visual", "SystemName", "Eyes") },
            };
        }

        private static string IniKa27()
        {
            var i = Read(Ka27(), "ASW");
            if (!i.Loaded || i.System != "VGS-3" || i.Via != "loadout:WeaponSystem1ASW" || i.Dropped != null) return "loaded=" + i.Loaded + " sys=" + i.System + " via=" + i.Via + " dropped=" + i.Dropped;
            if (AswDipTasks.DipNote(i) != null) return "note " + AswDipTasks.DipNote(i);
            return null;
        }

        private static string IniKa27Strike()
        {
            var i = Read(Ka27(), "Strike");
            if (i.Loaded) return "loaded";
            if (i.Declared != 2) return "declared " + i.Declared;
            return AswDipTasks.DipNote(i) == "beyond_count:SensorSystem3=VGS-3 n=2" ? null : "note " + AswDipTasks.DipNote(i);
        }

        private static string IniKa27Empty()
        {
            var i = Read(Ka27(), "Empty");
            return i.Loaded ? "loaded" : null;
        }

        private static string IniKa27Default()
        {
            var ini = Ka27();
            ini["WeaponSystem1Default"] = S("EnableSensorSystems", "SensorSystem3");
            var i = Read(ini, null);
            return i.Loaded && i.Via == "loadout:WeaponSystem1Default" ? null : "loaded=" + i.Loaded + " via=" + i.Via;
        }

        private static string IniMi14()
        {
            var ini = Ka27();
            ini["SensorSystems"] = S("NumberOfSensorSystems", "3");
            ini["SensorSystem3"] = S("Type", "Radar", "SystemName", "Y");
            ini["SensorSystem4"] = S("Type", "Sonar", "SystemName", "VGS-3");
            ini["SensorSystem5"] = S("Type", "Radar", "SystemName", "X");
            ini["WeaponSystem1ASW"] = S("EnableSensorSystems", "SensorSystem5,SensorSystem4");
            var i = Read(ini, "ASW");
            return i.Loaded && i.Via == "loadout:WeaponSystem1ASW" && i.System == "VGS-3" ? null : "loaded=" + i.Loaded + " via=" + i.Via;
        }

        private static string Pairs(List<KeyValuePair<string, string>> l)
        {
            var parts = new List<string>();
            foreach (var kv in l) parts.Add(kv.Key + ">" + kv.Value);
            return string.Join(";", parts.ToArray());
        }

        private static string IniLoadoutSections()
        {
            var ini = Ka27();
            Func<string, bool> has = s => ini.ContainsKey(s);
            Func<string, string, string> rd = (s, k) => { Dictionary<string, string> sec; string v; return ini.TryGetValue(s, out sec) && sec.TryGetValue(k, out v) ? v : null; };
            string r;
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "ASW", null));
            if (r != "WeaponSystem1ASW>SensorSystem3") return "ASW: " + r;
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "Strike", null));
            if (r != "") return "Strike: " + r;
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "Empty", null));
            if (r != "") return "Empty: " + r;
            ini["WeaponSystem1Default"] = S("EnableSensorSystems", "SensorSystem3");
            if (Pairs(AswDipTasks.LoadoutSensorSections(has, rd, null, null)) != "WeaponSystem1Default>SensorSystem3") return "null as Default";
            // a loadout section with SystemName replaces the base; the game then reads <text3><loadout><loadout>
            ini["WeaponSystem1ASW"] = S("SystemName", "X", "EnableSensorSystems", "SensorSystem3");
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "ASW", null));
            if (r != "") return "SystemName: " + r;
            // base not a hardpoint + loadout section without SystemName: skipped
            ini["WeaponSystem1ASW"] = S("EnableSensorSystems", "SensorSystem3");
            ini["WeaponSystem1"] = S("Type", "Gun");
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "ASW", null));
            if (r != "") return "not hardpoint: " + r;
            ini["WeaponSystem1"] = S("Type", "Hardpoint");
            ini["WeaponSystem1ASW_RU"] = S("EnableSensorSystems", "SensorSystem1");
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "ASW", "RU"));
            if (r != "WeaponSystem1ASW_RU>SensorSystem1") return "nation: " + r;
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "ASW", null));
            if (r != "WeaponSystem1ASW>SensorSystem3") return "nation null: " + r;
            ini["WeaponSystem1ASW"] = S("EnableSensorSystems", "SensorSystem4,SensorSystem5");
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "ASW", null));
            if (r != "WeaponSystem1ASW>SensorSystem4;WeaponSystem1ASW>SensorSystem5") return "two: " + r;
            ini.Remove("WeaponSystems");   // the game returns before LoadWeaponSystems without the section
            r = Pairs(AswDipTasks.LoadoutSensorSections(has, rd, "ASW", null));
            if (r != "") return "no WeaponSystems: " + r;
            return null;
        }

        private static string IniSh3h()
        {
            var i = Read(Sh3h(), "ASW");
            if (!i.Loaded || i.System != "AQS-13E" || i.Dropped != null || i.Via != "count") return "loaded=" + i.Loaded + " sys=" + i.System + " dropped=" + i.Dropped;
            return null;
        }

        private static string IniSh2f()
        {
            var ini = new Dictionary<string, Dictionary<string, string>>
            {
                { "SensorSystems", S("NumberOfSensorSystems", "2") },
                { "SensorSystem1", S("Type", "Radar", "SystemName", "APS-116") },
                { "SensorSystem2", S("Type", "MAD", "SystemName", "ASQ-81") },
            };
            var i = Read(ini, "ASW");
            return i.Loaded || i.Dropped != null ? "loaded=" + i.Loaded + " dropped=" + i.Dropped : null;
        }

        private static string IniRemoved()
        {
            var ini = Sh3h();
            ini["WeaponSystems"] = S("ASW_SensorsToRemove", "2");
            var i = Read(ini, "ASW");
            if (i.Loaded) return "loaded though removed";
            if (i.Dropped == null || !i.Dropped.StartsWith("removed_by_loadout:", StringComparison.Ordinal)) return "dropped " + i.Dropped;
            var other = Read(ini, "Other");
            return other.Loaded ? null : "another loadout lost the sonar";
        }

        private static string IniParse()
        {
            if (AswDipTasks.IniInt("2 //x", -1) != 2) return "int comment";
            if (AswDipTasks.IniInt(null, 7) != 7 || AswDipTasks.IniInt("", 7) != 7) return "int default";
            var l = AswDipTasks.IniIntList(" 6 , 7 //x");
            if (l.Count != 2 || l[0] != 6 || l[1] != 7) return "list";
            var ini = Sh3h();
            ini["SensorSystems"] = S();   // no count key: 1
            var i = Read(ini, "ASW");
            if (i.Loaded || i.Declared != 1) return "default count: loaded=" + i.Loaded + " n=" + i.Declared;
            ini.Remove("SensorSystems");
            if (Read(ini, "ASW").Loaded) return "no [SensorSystems]";
            var low = Sh3h();
            low["SensorSystem2"] = S("Type", "sonar", "SystemName", "AQS-13E");   // the game compares the type with "Sonar" exactly
            if (Read(low, "ASW").Loaded) return "lower-case type";
            var com = Sh3h();
            com["SensorSystem2"] = S("Type", " Sonar //x", "SystemName", "AQS-13E // dip");
            if (!Read(com, "ASW").Loaded) return "comments in values";
            return null;
        }

        private static AswMath.DeckEntry Entry(bool? typeDip)
        {
            return new AswMath.DeckEntry { Uid = -5, ShipUid = 7, Type = "usn_sh-2f", Label = "SH-2F", State = "ready", AswCapableType = true, TypeDipping = typeDip, DipNote = "n", Ammo = new Dictionary<string, int> { { "usn_ssq-53", 12 }, { "usn_mk46", 2 } } };
        }

        private static string DeckTyped()
        {
            var no = AswMath.DeckToMember(Entry(false));
            if (no.Dipping || no.DipSrc != "type" || no.DipNote != "n") return "false: " + no.Dipping + " " + no.DipSrc + " " + no.DipNote;
            if (no.Passive != 12) return "the load is lost";
            var yes = AswMath.DeckToMember(Entry(true));
            if (!yes.Dipping || yes.DipSrc != "type") return "true";
            var guess = AswMath.DeckToMember(Entry(null));
            if (!guess.Dipping || guess.DipSrc != "guess") return "null: " + guess.Dipping + " " + guess.DipSrc;
            return null;
        }

        private static string AssignTyped()
        {
            var a = AswMath.DeckToMember(Entry(false));
            var b = AswMath.DeckToMember(new AswMath.DeckEntry { Uid = -6, ShipUid = 7, Type = "usn_sh-3h", Label = "SH-3H", State = "ready", AswCapableType = true, TypeDipping = true, Ammo = new Dictionary<string, int> { { "usn_ssq-53", 12 } } });
            var res = AswMath.Assign(new List<AswMember> { a }, new[] { "dip" });
            if (res[a.Uid] == "dip") return "the helicopter without sonar got dip";
            res = AswMath.Assign(new List<AswMember> { a, b }, new[] { "dip" });
            if (res[b.Uid] != "dip" || res[a.Uid] == "dip") return "sh-3h: " + res[b.Uid] + " / " + res[a.Uid];
            return null;
        }
    }
}
