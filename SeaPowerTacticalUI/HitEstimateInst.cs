using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge.Ui
{
    // T-170 / ADR-0065 + ADR-0070: the block under an expanded Overview row for a LARGE ground object (airbase, port, bridge, oil rig, factory).
    // Pure: zone lines and the reference line from the static data of the class (HeClass), the weapon from the model's options. No state of the object.

    internal static class HitEstimateInst
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string T(string key, params object[] args) { return UiText.Fmt(key, args); }

        /// <summary>The block applies: a large class whose zones were read (capability hit_estimate_zones) and the setting [HitEstimate] Installations is on.</summary>
        public static bool Handles(HeClass c, HeSettings s)
        {
            return c != null && c.Kind == HeTargetKind.Large && c.ZonesRead && s != null && s.Installations;
        }

        /// <summary>No system of the class can be hit at all (a port without colliders): nothing to hit, nothing to estimate.</summary>
        public static bool Immune(HeClass c)
        {
            if (c == null) return true;
            foreach (var s in c.Systems) if (HitEstimateMath.Reachable(s)) return false;
            return true;
        }

        /// <summary>The geometric reference of the weapon (ADR-0070), cached per class; null = none.</summary>
        public static int? RefFor(HeClass c, HeAmmo a, double bonus) { return HitEstimateGeo.RefCached(c, a, bonus); }

        /// <summary>The name of a zone: the ini Name; HitZoneN -> "zone N"; else the section name (Q2 of the plan: Latin names stay).</summary>
        public static string ZoneName(HeSystem z)
        {
            if (!string.IsNullOrEmpty(z.Name)) return z.Name;
            string sec = z.Section ?? "";
            if (sec.StartsWith("HitZone", StringComparison.OrdinalIgnoreCase))
            {
                int n;
                if (int.TryParse(sec.Substring(7), NumberStyles.Integer, Inv, out n)) return T("he.inst_zone_n", n);
            }
            return sec;
        }

        public static string SizeText(HeSystem z)
        {
            if (z.SizeAM < 0 || z.SizeBM < 0) return "";
            return " · " + T("he.inst_size", (int)Math.Round(z.SizeAM, MidpointRounding.AwayFromZero), (int)Math.Round(z.SizeBM, MidpointRounding.AwayFromZero));
        }

        private static bool NoArmor(HeSystem z) { return string.Equals((z.Armor ?? "none").Trim(), "none", StringComparison.OrdinalIgnoreCase); }

        /// <summary>One zone: "name I · size: ≈k hits (j–m)"; an armoured zone has no estimate (no armour model, ADR-0065 item 4.6).</summary>
        public static string ZoneLine(HeSystem z, double d)
        {
            string name = ZoneName(z);
            int integ = (int)Math.Round(z.Integrity, MidpointRounding.AwayFromZero);
            if (!NoArmor(z)) return T("he.inst_zone_armor", name, integ);
            var h = HitEstimateMath.ZoneHits(d, z.Integrity);
            if (h == null) return T("he.inst_zone_armor", name, integ);
            if (h.Over50) return T("he.inst_zone_over50", name, integ, SizeText(z));
            string range = h.J == h.M ? "" : " " + T("he.range", h.J, h.M);
            return T("he.inst_zone", name, integ, SizeText(z), h.K, range);
        }

        private static void Add(HeDetail d, string text) { d.Estimate.Add(new HeBlockLine { Text = text }); }

        private static bool Is(HeClass c, string sub) { return string.Equals((c.LandSubType ?? "").Trim(), sub, StringComparison.OrdinalIgnoreCase); }

        /// <summary>Fills d.Estimate (and Pick / Choices / PickIndex / Reason) for one large class. opts = the options of the model (Options(...), best reference first).</summary>
        public static void Fill(HeClass c, IList<HeOption> opts, HeSelection sel, double bonus, HeDetail d)
        {
            if (Immune(c))
            {
                d.Reason = "immune";
                Add(d, T("he.inst_immune"));
                return;
            }
            d.Reason = "ok";
            HeOption o = null;
            if (opts != null && opts.Count > 0)
            {
                int i = HitEstimateModel.PickIndex(opts, sel);
                o = opts[i];
                d.Choices = opts.Count; d.PickIndex = i; d.Pick = HitEstimateModel.PickText(o);
            }
            int zones = 0;
            foreach (var s in c.Systems) if (HitEstimateMath.Reachable(s)) zones++;
            double dp = HitEstimateMath.ClassDp(c);
            Add(d, T("he.inst_head", (int)Math.Round(dp, MidpointRounding.AwayFromZero), zones));
            if (o == null)
            {
                d.Reason = "no_weapon";
                Add(d, T("he.no_weapon"));
            }
            else
            {
                double dmg = HitEstimateMath.DamageLarge(o.Ammo, c.LandSubType, bonus);
                foreach (var z in HitEstimateMath.TopZones(HitEstimateMath.ZoneCandidates(c), dp)) Add(d, ZoneLine(z, dmg));
                int? r = o.Ref >= 0 ? o.Ref : (int?)null;
                if (r != null)
                {
                    Add(d, T("he.inst_ref", r.Value));
                    Add(d, T("he.inst_ref_note"));
                }
            }
            if (Is(c, "Airbase"))
            {
                bool fd = false;
                foreach (var s in c.Systems) if (string.Equals(s.Section, "FlightDeck", StringComparison.OrdinalIgnoreCase) && HitEstimateMath.Reachable(s)) fd = true;
                Add(d, T(fd ? "he.inst_airbase_fd" : "he.inst_airbase"));
            }
            else if (Is(c, "Bridge") || Is(c, "OilRig")) Add(d, T("he.inst_elevated"));
        }

        // ---------------------------------------------------------------- self test

        private static HeAmmo Bomb(string file, string name, double power, double radius)
        {
            return new HeAmmo { File = file, Name = name, Kind = HeAmmoKind.Bomb, Power = power, DudPower = power * 0.2, RadiusM = radius, LandInstall = true, LandMobile = true, Land = true, Surface = true, Penetration = "heavy" };
        }

        private static HeClass Row(string fileTag, out List<HeGeoWeapon> ws)
        {
            foreach (var row in HitEstimateGeo.GeoRef) if (row.StartsWith(fileTag + "|", StringComparison.Ordinal)) return HitEstimateGeo.ParseRow(row, out ws);
            ws = null;
            return null;
        }

        private static string All(HeDetail d)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var l in d.Estimate) sb.Append(l.Text).Append('\n');
            return sb.ToString();
        }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (ok, m) => { if (!ok && err == null) err = m; };
            if (!UiText.Has(UiText.DefaultSection, "he.inst_head")) return "texts not loaded";   // texts are not loaded in this stand

            var settings = new HeSettings();
            var mk84 = Bomb("usn_mk-84", "Mk 84", 62, 60);
            var mk82 = Bomb("usn_mk-82", "Mk 82", 28, 20);
            // the Keflavik row of the fixture, with the ini Name of the runway
            List<HeGeoWeapon> ws;
            var kef = Row("is_airbase_keflavik", out ws);
            chk(kef != null, "fixture row of Keflavik");
            if (kef == null) return err;
            kef.Kind = HeTargetKind.Large;
            foreach (var s in kef.Systems) if (s.Section == "HitZone1") s.Name = "Runway";
            int? n84 = null;
            foreach (var w in ws) if (w.File == "usn_mk-84") n84 = w.N;
            chk(Handles(kef, settings) && !Handles(kef, new HeSettings { Installations = false }) && !Handles(new HeClass { Kind = HeTargetKind.Large }, settings), "Handles");
            chk(!Immune(kef), "Keflavik is not immune");

            var cand = new List<HeCandidate> { new HeCandidate { Ammo = mk84, Unit = "A-6E", Count = 4 } };
            var opts = HitEstimateModel.Options(kef, cand, 0.25);
            chk(opts.Count == 1 && opts[0].Ref == n84, "option carries the reference " + (opts.Count > 0 ? opts[0].Ref : -9) + " want " + n84);
            int runs = HitEstimateGeo.Runs;
            var again = HitEstimateModel.Options(kef, cand, 0.25);
            chk(again[0].Ref == n84 && HitEstimateGeo.Runs == runs, "RefFor takes the cache");
            chk(RefFor(kef, mk84, 0.25) == n84, "RefFor");

            var d = new HeDetail();
            Fill(kef, opts, new HeSelection(), 0.25, d);
            string all = All(d);
            chk(d.Reason == "ok" && d.Pick == "Mk 84" && d.Choices == 1, "pick / reason: " + d.Reason);
            chk(d.Estimate[0].Text == T("he.inst_head", 600, 8), "head: " + d.Estimate[0].Text);
            chk(d.Estimate.Count == 1 + 2 + 2 + 1, "lines (head, 2 zones, ref + note, airbase): " + d.Estimate.Count + "\n" + all);
            chk(d.Estimate[1].Text.Contains("Runway") && d.Estimate[1].Text.Contains("450") && d.Estimate[1].Text.Contains("2688") && d.Estimate[1].Text.Contains("≈7") && d.Estimate[1].Text.Contains("5") && d.Estimate[1].Text.Contains("11"), "runway line: " + d.Estimate[1].Text);
            chk(d.Estimate[2].Text.Contains(T("he.inst_zone_n", 3)) && d.Estimate[2].Text.Contains("300"), "second zone: " + d.Estimate[2].Text);
            chk(d.Estimate[3].Text == T("he.inst_ref", n84.Value) && d.Estimate[4].Text == T("he.inst_ref_note"), "reference lines: " + d.Estimate[3].Text);
            chk(d.Estimate[5].Text == T("he.inst_airbase") && !all.Contains(T("he.inst_elevated")), "airbase text");
            chk(!T("he.inst_ref_note").ToLowerInvariant().Contains("нижняя") && !T("he.inst_ref_note").ToLowerInvariant().Contains("lower bound"), "no 'lower bound' in the note");
            foreach (var l in d.Estimate)
            {
                string low = l.Text.ToLowerInvariant();
                chk(!low.Contains("до уничтожения") && !low.Contains("нужно") && !low.Contains("to destroy") && !low.Contains("needed"), "no count to destruction: " + l.Text);
            }

            // no weapon: head + reason + the airbase text
            var dn = new HeDetail();
            Fill(kef, new List<HeOption>(), new HeSelection(), 0.25, dn);
            chk(dn.Pick == null && dn.Reason == "no_weapon" && dn.Estimate.Count == 3 && dn.Estimate[1].Text == T("he.no_weapon") && dn.Estimate[2].Text == T("he.inst_airbase"), "no weapon: " + All(dn));

            // an airbase with a FlightDeck
            List<HeGeoWeapon> ws2;
            var nato = Row("is_airbase_keflavik", out ws2);   // same data; add a reachable FlightDeck by hand below
            nato.Kind = HeTargetKind.Large;
            var fdSys = new HeSystem { Section = "FlightDeck", Integrity = 400, Colliders = 1 };
            fdSys.Boxes.Add(nato.Systems[1].Boxes[0]);
            nato.Systems.Add(fdSys);
            var dfd = new HeDetail();
            Fill(nato, opts, new HeSelection(), 0.25, dfd);
            chk(dfd.Estimate[dfd.Estimate.Count - 1].Text == T("he.inst_airbase_fd"), "airbase with a flight deck: " + dfd.Estimate[dfd.Estimate.Count - 1].Text);

            // a bridge: the zone is named by its section, "direct hits only"
            List<HeGeoWeapon> wsb;
            var bridge = Row("civ_tgt_bridge_6_part", out wsb);
            bridge.Kind = HeTargetKind.Large;
            var db = new HeDetail();
            Fill(bridge, HitEstimateModel.Options(bridge, cand, 0.25), new HeSelection(), 0.25, db);
            chk(db.Estimate[1].Text.Contains("ControlSystem") && db.Estimate[db.Estimate.Count - 1].Text == T("he.inst_elevated") && !All(db).Contains(T("he.inst_airbase")), "bridge: " + All(db));

            // a port without colliders: immune
            var port = new HeClass { IniName = "port_belfast", Kind = HeTargetKind.Large, LandSubType = "Port", DamagePoints = 300, ZonesRead = true };
            port.Systems.Add(new HeSystem { Section = "HitZone1", Integrity = 60, Colliders = 0 });
            var dp = new HeDetail();
            Fill(port, opts, new HeSelection(), 0.25, dp);
            chk(Immune(port) && dp.Pick == null && dp.Reason == "immune" && dp.Estimate.Count == 1 && dp.Estimate[0].Text == T("he.inst_immune"), "immune port");

            // an armoured zone: no estimate for it and no reference
            var arm = new HeClass { IniName = "a", Kind = HeTargetKind.Large, LandSubType = "Installation", DamagePoints = 100, ZonesRead = true };
            var asys = new HeSystem { Section = "ControlSystem", Integrity = 50, Armor = "minor", Colliders = 1 };
            asys.Boxes.Add(HeBox.Make("b", false, 0, 0, 0, 0, 0.01, 0.01, 0.01));
            arm.Systems.Add(asys);
            var zsys = new HeSystem { Section = "HitZone2", Integrity = 50, Colliders = 1 };
            zsys.Boxes.Add(HeBox.Make("b", false, 5, 0, 0, 0, 0.01, 0.01, 0.01));
            arm.Systems.Add(zsys);
            var darm = new HeDetail();
            Fill(arm, HitEstimateModel.Options(arm, cand, 0.25), new HeSelection(), 0.25, darm);
            chk(darm.Estimate[1].Text == T("he.inst_zone_armor", "ControlSystem", 50) && !All(darm).Contains(T("he.inst_ref_note")), "armoured zone: " + All(darm));
            // a zone without a size: no size text; more than 50 hits
            var big = new HeSystem { Section = "HitZone9", Integrity = 3000, Colliders = 1 };
            chk(!ZoneLine(big, 38.5).Contains("×") && ZoneLine(big, 38.5) == T("he.inst_zone_over50", T("he.inst_zone_n", 9), 3000, ""), "over 50, no size: " + ZoneLine(big, 38.5));
            var small = new HeSystem { Section = "HitZone1", Integrity = 100, Colliders = 1, Name = "Tank", SizeAM = 20.4, SizeBM = 5.6 };
            chk(ZoneLine(small, 269.5) == T("he.inst_zone", "Tank", 100, " · " + T("he.inst_size", 20, 6), 1, ""), "J == M: no brackets: " + ZoneLine(small, 269.5));
            return err;
        }
    }
}
