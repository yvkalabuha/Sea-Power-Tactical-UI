// T-171 / ADR-0068: the category of one weapon (SAM / AShM / ASW: its colour and label bearing; since the supplement 2026-10-22 there are no category
// rings any more, only the checked weapons), "circle or wedges" coverage, the weapon lists, label bearings, legend lines. Pure: no Unity, no game.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>What delivers the weapon: drives the colour (ADR-0068 p. 4a), the label noun and the legend.</summary>
    internal enum RingVia { Missile, Gun, Torpedo, Bomb, Rocket, Asw }

    /// <summary>One weapon for the rule. Class: Secondary is Unknown unless HasSecondary; GunAaNm = the range of an AA shell (own: the shell's MaxNm).</summary>
    internal struct ReachIn { public string Key; public RingAmmoType Type; public RingTarget Primary, Secondary; public double MinNm, MaxNm, GunAaNm; }

    internal struct WeaponOpt { public string Key, Name; public double MaxNm, MinNm; public RingKind Kind; public RingVia Via; }

    internal static class RangeRingReach
    {
        public static RingVia ViaOf(RingAmmoType t)
        {
            switch (t)
            {
                case RingAmmoType.Missile: return RingVia.Missile;
                case RingAmmoType.Projectile: return RingVia.Gun;
                case RingAmmoType.Torpedo: return RingVia.Torpedo;
                case RingAmmoType.Bomb: case RingAmmoType.Cluster: return RingVia.Bomb;
                case RingAmmoType.AerialRocket: case RingAmmoType.Mlrs: return RingVia.Rocket;
                case RingAmmoType.Rbu: case RingAmmoType.Asroc: case RingAmmoType.AirDepthCharge: return RingVia.Asw;
                default: return RingVia.Missile;
            }
        }

        /// <summary>Decoys, chaff, fuel tanks, buoys, MOSS, paratroopers, pods without a type and anti-torpedo weapons do not make a range ring.</summary>
        public static bool Candidate(RingAmmoType t, bool antiTorpedo)
        {
            if (antiTorpedo) return false;
            return t != RingAmmoType.Noisemaker && t != RingAmmoType.Chaff && t != RingAmmoType.Fueltank && t != RingAmmoType.Sonobuoy
                && t != RingAmmoType.Moss && t != RingAmmoType.Paratrooper && t != RingAmmoType.Unknown && t != RingAmmoType.None;
        }

        /// <summary>2026-10-22 (customer: a sonobuoy in the weapon list of a P-3C): the game rewrites the ini Type of the loaded ammunition
        /// (AmmunitionParameters ctor): Sonobuoy / AirDepthCharge / Fueltank / Paratrooper -> _type Bomb, MOSS -> Torpedo, MLRS -> RBU, the real kind
        /// goes to _subType; a cluster warhead sets _subType = Cluster. The kind of an own ammunition = the sub-type when it names one, else the type.</summary>
        public static RingAmmoType EffectiveType(RingAmmoType type, RingAmmoType sub)
        {
            switch (sub)
            {
                case RingAmmoType.Sonobuoy: case RingAmmoType.Fueltank: case RingAmmoType.Paratrooper: case RingAmmoType.Moss: case RingAmmoType.AirDepthCharge:
                case RingAmmoType.Mlrs: case RingAmmoType.Noisemaker: case RingAmmoType.Chaff:
                    return sub;
                case RingAmmoType.Cluster:
                    return type == RingAmmoType.Bomb ? RingAmmoType.Cluster : type == RingAmmoType.Rbu ? RingAmmoType.Mlrs : type;   // a cluster missile stays a missile
                default:
                    return type;
            }
        }

        private static bool Hits(ReachIn w, RingTarget t) { return w.Primary == t || w.Secondary == t; }

        /// <summary>The category of one weapon in the weapon mode: anti-submarine type or any ASW target -> Asw; otherwise the primary target decides (AAW -> Aaw, ASuW -> Asuw);
        /// with no known primary a secondary AAW -> Aaw; else Asuw. A gun that shoots surface targets first and air second (76 mm) is an Asuw weapon: its AA range is not
        /// always in the encyclopedia entry of a mount.</summary>
        public static RingKind CategoryOf(ReachIn w)
        {
            if (RangeRingModel.IsAswType(w.Type) || Hits(w, RingTarget.Asw)) return RingKind.Asw;
            // a gun entry of a class mount carries no AA range of its shell (the game copies the ammunition entry without it): a gun with an AAW primary and an ASuW
            // secondary (76 mm) is then listed by its surface range
            if (w.Type == RingAmmoType.Projectile && w.Primary == RingTarget.Aaw && !(w.GunAaNm > 0) && w.Secondary == RingTarget.Asuw) return RingKind.Asuw;
            if (w.Primary == RingTarget.Aaw) return RingKind.Aaw;
            if (w.Primary == RingTarget.Asuw) return RingKind.Asuw;
            if (w.Secondary == RingTarget.Aaw) return RingKind.Aaw;
            return RingKind.Asuw;
        }

        public static RingVia ViaFor(RingKind k, RingAmmoType t) { return k == RingKind.Asw ? RingVia.Asw : ViaOf(t); }

        /// <summary>The range the weapon reaches in its category (an AA shell: GunAaNm).</summary>
        public static double RadiusOf(ReachIn w, RingKind k)
        {
            if (k == RingKind.Aaw && w.Type == RingAmmoType.Projectile) return w.GunAaNm > 0 ? w.GunAaNm : w.MaxNm;   // an AA-only gun without an AA range: its shell range
            return w.MaxNm;
        }

        public static ReachIn InOf(AmmoFact a)
        {
            return new ReachIn { Key = a.Name, Type = a.Type, Primary = a.Primary, Secondary = a.Secondary, MinNm = a.MinNm, MaxNm = a.MaxNm, GunAaNm = a.MaxNm };
        }

        public static ReachIn InOf(ClassWeapon w)
        {
            return new ReachIn { Key = w.Key, Type = w.Type, Primary = w.Primary, Secondary = w.HasSecondary ? w.Secondary : RingTarget.Unknown, MinNm = w.MinNm, MaxNm = w.MaxNm, GunAaNm = w.GunAaNm };
        }

        // ---- circle or wedges -----------------------------------------------------------------------------------------------------------
        /// <summary>The arcs of the mounts of a weapon -> all-round (true) or the merged spans {centre, width} from the bow. No mounts / any unrestricted mount / union of 359 deg or more -> true.</summary>
        public static bool Coverage(IList<List<ArcDeg>> mountArcs, out List<double[]> spans)
        {
            spans = new List<double[]>();
            if (mountArcs == null || mountArcs.Count == 0) return true;
            var all = new List<double[]>();
            foreach (List<ArcDeg> arcs in mountArcs)
            {
                List<double[]> sp = RangeRingMath.ArcSpans(arcs);
                if (sp.Count == 0) return true;
                all.AddRange(sp);
            }
            List<double[]> u = RangeRingMath.UnionSpans(all);
            double total = 0;
            foreach (double[] x in u) total += x[1];
            if (total >= 359.0 || u.Count == 0) return true;
            spans = u;
            return false;
        }

        /// <summary>Joins class weapons with the mounts of the class ini: a gun / CIWS mount carries the weapon whose Key is the mount's SystemName, any other mount the weapons whose Key is among its ammunition files.</summary>
        public static List<ClassWeaponInfo> JoinClassArcs(IList<ClassWeapon> ws, IList<ClassMount> mounts)
        {
            var res = new List<ClassWeaponInfo>();
            if (ws == null) return res;
            foreach (ClassWeapon w in ws)
            {
                var info = new ClassWeaponInfo { W = w };
                if (mounts != null && !string.IsNullOrEmpty(w.Key))
                {
                    foreach (ClassMount m in mounts)
                    {
                        if (m == null) continue;
                        bool gun = string.Equals(m.Type, "Gun", StringComparison.OrdinalIgnoreCase) || string.Equals(m.Type, "CIWS", StringComparison.OrdinalIgnoreCase);
                        bool hit;
                        if (gun) hit = string.Equals(m.System, w.Key, StringComparison.OrdinalIgnoreCase);
                        else hit = Contains(m.AmmoRefs, w.Key);
                        if (hit) info.MountArcs.Add(new List<ArcDeg>(m.Arcs));
                    }
                }
                res.Add(info);
            }
            return res;
        }

        private static bool Contains(IList<string> l, string key)
        {
            if (l == null) return false;
            foreach (string s in l) if (string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- weapon lists ---------------------------------------------------------------------------------------------------------------
        private static List<WeaponOpt> Sorted(List<WeaponOpt> l, int max)
        {
            var idx = new List<KeyValuePair<int, WeaponOpt>>();
            for (int i = 0; i < l.Count; i++) idx.Add(new KeyValuePair<int, WeaponOpt>(i, l[i]));
            idx.Sort((a, b) => { int c = b.Value.MaxNm.CompareTo(a.Value.MaxNm); return c != 0 ? c : a.Key.CompareTo(b.Key); });
            var res = new List<WeaponOpt>();
            foreach (var kv in idx) { if (res.Count >= max) break; res.Add(kv.Value); }
            return res;
        }

        private static bool Has(List<WeaponOpt> l, string key)
        {
            foreach (WeaponOpt o in l) if (o.Key == key) return true;
            return false;
        }

        /// <summary>The weapons of an own unit that can be chosen: counted, rounds left, range > 0; by range descending; one row per Key.</summary>
        public static List<WeaponOpt> OwnWeapons(OwnArmament a, int max)
        {
            var l = new List<WeaponOpt>();
            if (a == null) return l;
            foreach (AmmoFact am in a.Ammo)
            {
                if (!Candidate(am.Type, am.AntiTorpedo) || am.Left == 0 || string.IsNullOrEmpty(am.Name)) continue;
                ReachIn w = InOf(am);
                RingKind k = CategoryOf(w);
                double r = RadiusOf(w, k);
                if (double.IsNaN(r) || double.IsInfinity(r) || !(r > 0) || Has(l, am.Name)) continue;
                l.Add(new WeaponOpt { Key = am.Name, Name = string.IsNullOrEmpty(am.Display) ? am.Name : am.Display, MaxNm = r, MinNm = Pos(am.MinNm), Kind = k, Via = ViaFor(k, am.Type) });
            }
            return Sorted(l, max);
        }

        /// <summary>The weapons of a class that can be chosen (encyclopedia, variant 0).</summary>
        public static List<WeaponOpt> ClassWeapons(ClassRanges c, int max)
        {
            var l = new List<WeaponOpt>();
            if (c == null) return l;
            foreach (ClassWeaponInfo i in c.Weapons)
            {
                ClassWeapon cw = i.W;
                if (!Candidate(cw.Type, false) || string.IsNullOrEmpty(cw.Key)) continue;
                ReachIn w = InOf(cw);
                RingKind k = CategoryOf(w);
                double r = RadiusOf(w, k);
                if (double.IsNaN(r) || double.IsInfinity(r) || !(r > 0) || Has(l, cw.Key)) continue;
                l.Add(new WeaponOpt { Key = cw.Key, Name = string.IsNullOrEmpty(cw.Name) ? cw.Key : cw.Name, MaxNm = r, MinNm = Pos(cw.MinNm), Kind = k, Via = ViaFor(k, cw.Type) });
            }
            return Sorted(l, max);
        }

        /// <summary>Diagnostics: one line per weapon of a class, "key|type|prim/sec|max|gunaa|category|kept or why dropped" (what ClassWeapons does with it).</summary>
        public static List<string> ClassWeaponWhy(ClassRanges c)
        {
            var res = new List<string>();
            if (c == null) return res;
            var seen = new HashSet<string>();
            foreach (ClassWeaponInfo i in c.Weapons)
            {
                ClassWeapon cw = i.W;
                ReachIn w = InOf(cw);
                RingKind k = CategoryOf(w);
                double r = RadiusOf(w, k);
                string why;
                if (!Candidate(cw.Type, false)) why = "type_excluded";
                else if (string.IsNullOrEmpty(cw.Key)) why = "no_key";
                else if (double.IsNaN(r) || double.IsInfinity(r) || !(r > 0)) why = "range_0";
                else if (!seen.Add(cw.Key)) why = "duplicate";
                else why = "kept";
                res.Add(cw.Key + "|" + cw.Type + "|" + cw.Primary + "/" + (cw.HasSecondary ? cw.Secondary.ToString() : "-") + "|" + cw.MaxNm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + "|" + cw.GunAaNm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "|" + k + "|" + why + "|mounts=" + i.MountArcs.Count);
            }
            return res;
        }

        private static double Pos(double v) { return double.IsNaN(v) || !(v > 0) ? 0 : v; }

        // ---- labels and legend ----------------------------------------------------------------------------------------------------------
        /// <summary>Bearing of the label on the circle: own Asuw 20, Aaw 60, Asw 100, sonar active 140, passive 180; a class ring +180.</summary>
        public static double LabelBearing(RingKind k, bool classSrc)
        {
            double b;
            switch (k)
            {
                case RingKind.Asuw: b = 20; break;
                case RingKind.Aaw: b = 60; break;
                case RingKind.Asw: b = 100; break;
                case RingKind.SonarActive: b = 140; break;
                default: b = 180; break;
            }
            return classSrc ? (b + 180.0) % 360.0 : b;
        }

        /// <summary>The distinct (kind, via) pairs drawn in the scene, minimum-range rings and the rings of checked weapons left out (LegendWeapons
        /// names those), in colour order. anyOwn / anyClass count every ring.</summary>
        public static List<KeyValuePair<RingKind, RingVia>> LegendKeys(RingScene s, out bool anyOwn, out bool anyClass)
        {
            anyOwn = anyClass = false;
            var res = new List<KeyValuePair<RingKind, RingVia>>();
            if (s == null) return res;
            foreach (UnitRings u in s.Units)
            {
                foreach (RingSpec r in u.Rings) if (!r.IsMin) Note(r.Weapon == null ? res : null, r.Kind, r.Via, r.Src, ref anyOwn, ref anyClass);
                foreach (SectorSpec c in u.Sectors) if (!c.IsMin) Note(c.Weapon == null ? res : null, c.Kind, c.Via, c.Src, ref anyOwn, ref anyClass);
            }
            res.Sort((a, b) => { int c = ((int)a.Key).CompareTo((int)b.Key); return c != 0 ? c : ((int)a.Value).CompareTo((int)b.Value); });
            return res;
        }

        /// <summary>ADR-0068 supplement 2026-10-22: the checked weapons drawn in the scene (name, kind, via, shade), once per name and colour, in scene order.</summary>
        public static List<RingLabel> LegendWeapons(RingScene s)
        {
            var res = new List<RingLabel>();
            if (s == null) return res;
            foreach (UnitRings u in s.Units)
            {
                foreach (RingSpec r in u.Rings) if (!r.IsMin && r.Weapon != null) NoteWeapon(res, r.Weapon, r.Kind, r.Via, r.Tint);
                foreach (SectorSpec c in u.Sectors) if (!c.IsMin && c.Weapon != null) NoteWeapon(res, c.Weapon, c.Kind, c.Via, c.Tint);
            }
            return res;
        }

        private static void NoteWeapon(List<RingLabel> res, string name, RingKind k, RingVia v, int tint)
        {
            foreach (var e in res) if (e.Weapon == name && e.Kind == k && e.Via == v && e.Tint == tint) return;
            res.Add(new RingLabel { Weapon = name, Kind = k, Via = v, Tint = tint });
        }

        private static void Note(List<KeyValuePair<RingKind, RingVia>> res, RingKind k, RingVia v, RingSource src, ref bool own, ref bool cls)
        {
            if (k == RingKind.SonarActive || k == RingKind.SonarPassive) v = RingVia.Missile;
            if (src == RingSource.Class) cls = true; else own = true;
            if (res == null) return;
            foreach (var e in res) if (e.Key == k && e.Value == v) return;
            res.Add(new KeyValuePair<RingKind, RingVia>(k, v));
        }

        // ---- self test ------------------------------------------------------------------------------------------------------------------
        private static ReachIn W(string key, RingAmmoType t, RingTarget p, double max, RingTarget s = RingTarget.Unknown, double min = 0, double gunAa = 0)
        {
            return new ReachIn { Key = key, Type = t, Primary = p, Secondary = s, MinNm = min, MaxNm = max, GunAaNm = gunAa };
        }

        private static ArcDeg Arc(double a, double b) { return new ArcDeg { A = a, B = b }; }

        public static string SelfTest()
        {
            const RingAmmoType M = RingAmmoType.Missile, P = RingAmmoType.Projectile, T = RingAmmoType.Torpedo;
            // the radius of one weapon in its category: an AA shell by GunAaNm, an AA-only gun without it by its shell range
            if (RadiusOf(W("gun", P, RingTarget.Aaw, 99, RingTarget.Unknown, 0, 4), RingKind.Aaw) != 4 || RadiusOf(W("gun", P, RingTarget.Aaw, 9), RingKind.Aaw) != 9 || RadiusOf(W("h", M, RingTarget.Asuw, 67), RingKind.Asuw) != 67) return "RadiusOf";
            if (ViaFor(RingKind.Asw, T) != RingVia.Asw || ViaFor(RingKind.Asuw, T) != RingVia.Torpedo) return "ViaFor";
            if (Candidate(RingAmmoType.Missile, true) || !Candidate(RingAmmoType.Missile, false)) return "candidate anti-torpedo";
            if (CategoryOf(W("t", T, RingTarget.Asuw, 1, RingTarget.Asw)) != RingKind.Asw || CategoryOf(W("s", M, RingTarget.Aaw, 1)) != RingKind.Aaw || CategoryOf(W("h", M, RingTarget.Asuw, 1)) != RingKind.Asuw || CategoryOf(W("g", P, RingTarget.Asuw, 8, RingTarget.Aaw)) != RingKind.Asuw
                || CategoryOf(W("a", RingAmmoType.Asroc, RingTarget.Asuw, 1)) != RingKind.Asw) return "CategoryOf";
            if (ViaOf(RingAmmoType.Cluster) != RingVia.Bomb || ViaOf(RingAmmoType.Mlrs) != RingVia.Rocket || ViaOf(RingAmmoType.Rbu) != RingVia.Asw) return "ViaOf";
            // 2026-10-22: the game stores a buoy as _type Bomb + _subType Sonobuoy: the effective type drops it (and fuel tanks, MOSS, paratroopers, pods)
            if (EffectiveType(RingAmmoType.Bomb, RingAmmoType.Sonobuoy) != RingAmmoType.Sonobuoy || EffectiveType(RingAmmoType.Bomb, RingAmmoType.Fueltank) != RingAmmoType.Fueltank
                || EffectiveType(RingAmmoType.Bomb, RingAmmoType.Paratrooper) != RingAmmoType.Paratrooper || EffectiveType(RingAmmoType.Torpedo, RingAmmoType.Moss) != RingAmmoType.Moss
                || EffectiveType(RingAmmoType.Bomb, RingAmmoType.AirDepthCharge) != RingAmmoType.AirDepthCharge || EffectiveType(RingAmmoType.Rbu, RingAmmoType.Mlrs) != RingAmmoType.Mlrs
                || EffectiveType(RingAmmoType.Bomb, RingAmmoType.Cluster) != RingAmmoType.Cluster || EffectiveType(RingAmmoType.Missile, RingAmmoType.Cluster) != RingAmmoType.Missile
                || EffectiveType(RingAmmoType.Rbu, RingAmmoType.Cluster) != RingAmmoType.Mlrs || EffectiveType(M, RingAmmoType.None) != M || EffectiveType(RingAmmoType.Bomb, RingAmmoType.Other) != RingAmmoType.Bomb) return "EffectiveType";
            foreach (var nw in new[] { RingAmmoType.Sonobuoy, RingAmmoType.Noisemaker, RingAmmoType.Chaff, RingAmmoType.Fueltank, RingAmmoType.Moss, RingAmmoType.Paratrooper, RingAmmoType.Unknown, RingAmmoType.None })
                if (Candidate(nw, false)) return "non-weapon is a candidate: " + nw;
            foreach (var wt in new[] { M, P, T, RingAmmoType.Rbu, RingAmmoType.Asroc, RingAmmoType.AerialRocket, RingAmmoType.AirDepthCharge, RingAmmoType.Bomb, RingAmmoType.Mlrs, RingAmmoType.Cluster })
                if (!Candidate(wt, false)) return "weapon is not a candidate: " + wt;

            // coverage: one mount +-130 -> wedges; two mounts of 180 deg -> circle; (0,0) -> circle; none -> circle
            List<double[]> sp;
            if (Coverage(new List<List<ArcDeg>> { new List<ArcDeg> { Arc(-130, 130) } }, out sp) || sp.Count != 1 || Math.Abs(sp[0][1] - 260) > 1e-9 || Math.Abs(sp[0][0]) > 1e-9) return "coverage one mount";
            if (!Coverage(new List<List<ArcDeg>> { new List<ArcDeg> { Arc(-90, 90) }, new List<ArcDeg> { Arc(90, -90) } }, out sp)) return "coverage two halves must close the circle";
            if (!Coverage(new List<List<ArcDeg>> { new List<ArcDeg> { Arc(0, 0) } }, out sp)) return "coverage (0,0)";
            if (!Coverage(new List<List<ArcDeg>> { new List<ArcDeg> { Arc(-30, 30) }, new List<ArcDeg>() }, out sp)) return "coverage unrestricted mount";
            if (!Coverage(null, out sp) || !Coverage(new List<List<ArcDeg>>(), out sp)) return "coverage none";
            if (Coverage(new List<List<ArcDeg>> { new List<ArcDeg> { Arc(-60, 60) }, new List<ArcDeg> { Arc(120, -120) } }, out sp) || sp.Count != 2) return "coverage two wedges " + sp.Count;

            // class arcs join
            var ws = new List<ClassWeapon> { new ClassWeapon { Key = "mk45", Type = P }, new ClassWeapon { Key = "harpoon.ini", Type = M }, new ClassWeapon { Key = "none", Type = M } };
            var mounts = new List<ClassMount>
            {
                new ClassMount { Type = "Gun", System = "MK45", Arcs = { Arc(-150, 150) } },
                new ClassMount { Type = "Missile", System = "HarpoonLauncher", AmmoRefs = { "Harpoon.ini" }, Arcs = { Arc(-135, 135) } },
                new ClassMount { Type = "Missile", System = "Harpoon2", AmmoRefs = { "harpoon.ini" }, Arcs = { Arc(0, 0) } }
            };
            List<ClassWeaponInfo> j = JoinClassArcs(ws, mounts);
            if (j.Count != 3 || j[0].MountArcs.Count != 1 || j[1].MountArcs.Count != 2 || j[2].MountArcs.Count != 0) return "JoinClassArcs " + j[0].MountArcs.Count + "/" + j[1].MountArcs.Count + "/" + j[2].MountArcs.Count;

            // weapon lists
            var arm = new OwnArmament();
            arm.Ammo.Add(RangeRingModel.Am("harpoon", RingTarget.Asuw, 67, 4, RingTarget.Unknown, 3));
            arm.Ammo.Add(new AmmoFact { Name = "sm2", Display = "Standard SM-2", Primary = RingTarget.Aaw, Type = M, MaxNm = 90, Left = 2 });
            arm.Ammo.Add(RangeRingModel.Am("empty", RingTarget.Aaw, 100, 0));
            arm.Ammo.Add(RangeRingModel.Am("chaff", RingTarget.Aaw, 100, 5, RingTarget.Unknown, 0, RingAmmoType.Chaff));
            arm.Ammo.Add(RangeRingModel.Am("harpoon", RingTarget.Asuw, 67, 4));
            arm.Ammo.Add(RangeRingModel.Am("asroc", RingTarget.Asuw, 9, -1, RingTarget.Unknown, 0, RingAmmoType.Asroc));
            // 2026-10-22: a P-3C: the buoys (read as Bomb + Sonobuoy), a fuel tank, MOSS, a noisemaker and a pod never reach the list; the torpedo does
            var p3 = new OwnArmament();
            p3.Ammo.Add(RangeRingModel.Am("usn_ssq-53", RingTarget.Asw, 2.7, 40, RingTarget.Unknown, 0, EffectiveType(RingAmmoType.Bomb, RingAmmoType.Sonobuoy)));
            p3.Ammo.Add(RangeRingModel.Am("usn_d-704", RingTarget.Unknown, 5, 1, RingTarget.Unknown, 0, EffectiveType(RingAmmoType.Bomb, RingAmmoType.Fueltank)));
            p3.Ammo.Add(RangeRingModel.Am("moss", RingTarget.Unknown, 5, 1, RingTarget.Unknown, 0, EffectiveType(T, RingAmmoType.Moss)));
            p3.Ammo.Add(RangeRingModel.Am("nm", RingTarget.Unknown, 5, 4, RingTarget.Unknown, 0, RingAmmoType.Noisemaker));
            p3.Ammo.Add(RangeRingModel.Am("usaf_alq-119", RingTarget.Unknown, 5, 1, RingTarget.Unknown, 0, RingAmmoType.Unknown));
            p3.Ammo.Add(RangeRingModel.Am("usn_mk46", RingTarget.Asw, 6, 8, RingTarget.Unknown, 0, T));
            List<WeaponOpt> p3w = OwnWeapons(p3, 10);
            if (p3w.Count != 1 || p3w[0].Key != "usn_mk46") return "P-3C weapon list: " + string.Join(",", p3w.ConvertAll(x => x.Key).ToArray());
            var buoyClass = new ClassRanges();
            buoyClass.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "ssq", Type = RingAmmoType.Sonobuoy, Primary = RingTarget.Asw, MaxNm = 3 } });
            buoyClass.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "pod", Type = RingAmmoType.Unknown, Primary = RingTarget.Asuw, MaxNm = 3 } });
            buoyClass.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "mk46", Type = T, Primary = RingTarget.Asw, MaxNm = 6 } });
            List<WeaponOpt> bcw = ClassWeapons(buoyClass, 10);
            if (bcw.Count != 1 || bcw[0].Key != "mk46") return "class weapon list keeps a buoy / a pod: " + bcw.Count;
            List<WeaponOpt> ow = OwnWeapons(arm, 10);
            if (ow.Count != 3 || ow[0].Key != "sm2" || ow[0].Name != "Standard SM-2" || ow[1].Key != "harpoon" || ow[2].Key != "asroc" || ow[2].Kind != RingKind.Asw || ow[1].MinNm != 3) return "OwnWeapons " + ow.Count;
            if (OwnWeapons(arm, 2).Count != 2 || OwnWeapons(null, 5).Count != 0) return "OwnWeapons max";
            var cr = new ClassRanges();
            cr.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "g", Name = "5in gun", Type = P, Primary = RingTarget.Aaw, MaxNm = 20, GunAaNm = 8 } });
            cr.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "h", Name = "Harpoon", Type = M, Primary = RingTarget.Asuw, MaxNm = 67 } });
            cr.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "h", Name = "Harpoon", Type = M, Primary = RingTarget.Asuw, MaxNm = 67 } });
            cr.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "z", Name = "zero", Type = M, Primary = RingTarget.Asuw, MaxNm = 0 } });
            List<WeaponOpt> cw = ClassWeapons(cr, 10);
            if (cw.Count != 2 || cw[0].Key != "h" || cw[1].MaxNm != 8 || cw[1].Kind != RingKind.Aaw || cw[1].Via != RingVia.Gun) return "ClassWeapons " + cw.Count;

            // a class gun entry without an AA range (AAW primary, ASuW secondary): listed by its surface range; mixed class with RBU and torpedo tubes
            var mixed = new ClassRanges();
            mixed.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "SET-40", Name = "SET-40", Type = T, Primary = RingTarget.Asw, Secondary = RingTarget.Asuw, HasSecondary = true, MaxNm = 4 } });
            mixed.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "RBU6000", Name = "RBU-6000", Type = RingAmmoType.Rbu, Primary = RingTarget.Asw, MaxNm = 3 } });
            mixed.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "AK726", Name = "AK-726", Type = P, Primary = RingTarget.Aaw, Secondary = RingTarget.Asuw, HasSecondary = true, MaxNm = 8.6, GunAaNm = 0 } });
            List<WeaponOpt> mw = ClassWeapons(mixed, 10);
            if (mw.Count != 3 || mw[0].Key != "AK726" || mw[0].Kind != RingKind.Asuw || mw[0].Via != RingVia.Gun || Math.Abs(mw[0].MaxNm - 8.6) > 1e-9) return "mixed class lists the gun " + mw.Count;
            var gunOnly = new ClassRanges();
            gunOnly.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "AK630", Name = "AK-630", Type = P, Primary = RingTarget.Aaw, MaxNm = 2.7 } });
            List<WeaponOpt> go = ClassWeapons(gunOnly, 10);
            if (go.Count != 1 || go[0].Kind != RingKind.Aaw || go[0].Via != RingVia.Gun || go[0].MaxNm != 2.7) return "AA-only gun without an AA range uses its shell range";
            List<string> why = ClassWeaponWhy(mixed);
            if (why.Count != 3 || !why[2].Contains("|kept|") || ClassWeaponWhy(null).Count != 0) return "ClassWeaponWhy";
            gunOnly.Weapons.Add(new ClassWeaponInfo { W = new ClassWeapon { Key = "z", Type = RingAmmoType.Chaff, MaxNm = 1 } });
            if (!ClassWeaponWhy(gunOnly)[1].Contains("type_excluded")) return "ClassWeaponWhy reasons";

            // label bearings
            if (LabelBearing(RingKind.Asuw, false) != 20 || LabelBearing(RingKind.Aaw, false) != 60 || LabelBearing(RingKind.Asw, false) != 100 || LabelBearing(RingKind.SonarActive, false) != 140 || LabelBearing(RingKind.SonarPassive, false) != 180) return "bearings own";
            if (LabelBearing(RingKind.Asuw, true) != 200 || LabelBearing(RingKind.Aaw, true) != 240 || LabelBearing(RingKind.Asw, true) != 280 || LabelBearing(RingKind.SonarPassive, true) != 0) return "bearings class";

            // legend keys
            var sc = new RingScene();
            var u = new UnitRings();
            u.Rings.Add(new RingSpec { Kind = RingKind.SonarPassive, Src = RingSource.Own, MaxNm = 30 });
            u.Rings.Add(new RingSpec { Kind = RingKind.SonarActive, Src = RingSource.Own, MaxNm = 8 });
            u.Rings.Add(new RingSpec { Kind = RingKind.SonarPassive, Src = RingSource.Class, MaxNm = 12, UpTo = true });
            sc.Units.Add(u);
            bool ao, ac;
            List<KeyValuePair<RingKind, RingVia>> lk = LegendKeys(sc, out ao, out ac);
            if (lk.Count != 2 || lk[0].Key != RingKind.SonarActive || lk[1].Key != RingKind.SonarPassive || !ao || !ac) return "LegendKeys " + lk.Count;
            if (LegendWeapons(sc).Count != 0) return "LegendWeapons without weapons";
            // checked weapons: named in the legend, not as categories; two shades of SAM are two lines, a repeated one is one
            var sw = new RingScene();
            var uw = new UnitRings();
            uw.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 90, Weapon = "SM-2" });
            uw.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 27, Weapon = "ESSM", Tint = 1 });
            uw.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 3, Weapon = "ESSM", Tint = 1, IsMin = true });
            uw.Sectors.Add(new SectorSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Class, RangeNm = 67, WidthDeg = 90, Weapon = "Harpoon" });
            uw.Sectors.Add(new SectorSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Class, RangeNm = 67, WidthDeg = 90, Weapon = "Harpoon" });
            sw.Units.Add(uw);
            List<RingLabel> lw = LegendWeapons(sw);
            if (lw.Count != 3 || lw[0].Weapon != "SM-2" || lw[1].Weapon != "ESSM" || lw[1].Tint != 1 || lw[2].Weapon != "Harpoon") return "LegendWeapons " + lw.Count;
            if (LegendKeys(sw, out ao, out ac).Count != 0 || !ao || !ac) return "LegendKeys skip weapon rings";
            return null;
        }
    }
}
