using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge.Ui
{
    // T-168 / ADR-0064: the pure damage model (no Unity, no game). Formulas of docs/research/seapower-damage-model.md par. 2-4 with the corrections of
    // ADR-0064 item 22. A copy of the reference table lives in SelfTest; tests/test_sea_power_hit_estimate_math.py recomputes it with estimate.py.

    internal enum HeTargetKind { None, Ship, Sub, Land, Large }
    internal enum HeAmmoKind { Missile, Torpedo, Bomb, Rocket, DepthCharge }   // no gun shells: they cannot be tied to a track (ADR-0064 item 33a)
    internal enum HeWarhead { Normal, Heat, Cluster, Frag }
    internal enum HeStatus { None, Light, Medium, Heavy, Dead }

    /// <summary>The data of a target CLASS (ini of the class), never of the live object.</summary>
    internal sealed class HeClass
    {
        public string IniName;
        public HeTargetKind Kind;
        public double Displacement;
        public bool Civilian;
        public string Armor = "minor";           // no ArmorType key in the ini = Minor (ADR-0064 item 22)
        public double IntegrityOverride = -1;
        public int DcTeams = -1;
        public bool RaftOrMine;
        public double DamagePoints = -1;
        public bool Mobile;
        public string LandSubType;
        public List<HeSystem> Systems = new List<HeSystem>();
        // T-170 / ADR-0070: the geometry of a large object (filled by the loader only for Kind == Large)
        public bool ZonesRead;         // the loader read Name / Collider / [coll_*] (capability hit_estimate_zones)
        public readonly Dictionary<string, int> GeoRefCache = new Dictionary<string, int>();   // key ammo.File|bonus:F3; -1 = no reference
    }

    /// <summary>Min / mean / max hits that destroy one zone (ADR-0065 item 4.2); Over50: K > 50.</summary>
    internal sealed class HeZoneHits { public int J, K, M; public bool Over50; }

    internal sealed class HeSystem
    {
        public string Section;
        public double Integrity;
        public string Armor = "none";
        public bool Radar;
        // T-170: filled by the loader for large classes only; defaults = unknown
        public string Name;                               // "Name=" of the section; null = none
        public int Colliders;                             // non-empty names in "Collider=" (0 = unreachable)
        public List<HeBox> Boxes = new List<HeBox>();     // resolved [coll_*] Box / Sphere of this system (ADR-0070 item 2); empty = no geometry
        public double SizeAM = -1, SizeBM = -1;           // two largest axes of the largest [coll_*] Scale, metres; -1 = no size
    }

    /// <summary>The parameters of one own ammunition (a shared AmmunitionParameters of the game).</summary>
    internal sealed class HeAmmo
    {
        public string File, Name;
        public HeAmmoKind Kind;
        public HeWarhead Warhead;
        public double Power, DudPower, DamageMult = 1, RadiusM;
        public string Penetration = "minor";
        public bool Surface, Subsurface, Land, LandInstall, LandMobile;
        public bool NoShip, NoSub;                       // _canNotAttackTypes of the game: Vessel / Submarine
        // Surface / Subsurface / Land*: domains of the target; Land = LandInstall || LandMobile
    }

    internal sealed class HeLine
    {
        public string Weapon;
        public int N;
        public double X;             // B / L
        public bool Range, Over20;   // Range: the boundary (|x - round(x)| <= 0.1), the text shows (m-m+1)
        public double Spare;         // N - x: how far below the next integer the pool is; larger = the estimate is steadier
    }

    internal static class HitEstimateMath
    {
        private static readonly string[] Armors = { "none", "minor", "moderate", "heavy" };
        private static readonly string[] Pens = { "minor", "moderate", "heavy", "always" };
        // PenetrationVsArmorMatrix: rows = armor of the system, columns = penetration of the warhead -> (min, max)
        private static readonly double[][] PenLo = { new[] { 1.0, 1, 1, 1 }, new[] { .5, .85, .9, .95 }, new[] { .1, .5, .85, .9 }, new[] { 0.0, .1, .5, .85 } };
        private static readonly double[][] PenHi = { new[] { 1.0, 1, 1, 1 }, new[] { .75, 1, 1, 1 }, new[] { .25, .75, 1, 1 }, new[] { .01, .25, .75, 1 } };

        private static double ArmorMult(string armor)
        {
            switch ((armor ?? "").Trim().ToLowerInvariant())
            {
                case "minor": return 1.2;
                case "moderate": return 1.25;
                case "heavy": return 1.3;
                default: return 1.0;
            }
        }

        /// <summary>maxI of a ship / submarine class (Compartments.Init): 2.5 * cbrt(displacement), x0.5 for civilian / submarine unless the class has damage-control teams,
        /// then the armour factor; IntegrityOverride > 0 replaces everything; rafts and mines 0.1.</summary>
        public static double MaxIntegrity(HeClass c)
        {
            if (c == null) return 0;
            if (c.RaftOrMine) return 0.1;
            if (c.IntegrityOverride > 0) return c.IntegrityOverride;
            double m = 2.5 * Math.Pow(Math.Max(0, c.Displacement), 1.0 / 3.0);
            if (!(c.DcTeams > 0) && (c.Civilian || c.Kind == HeTargetKind.Sub)) m *= 0.5;
            return m * ArmorMult(c.Armor);
        }

        public static double Budget(HeClass c) { return 5 * MaxIntegrity(c); }

        private static bool SubIs(string landSub, string name) { return string.Equals((landSub ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase); }

        /// <summary>D = Power * type factor * (1 + bonus) * 1.1 * damage multiplier of the ammunition (Blastzone.GetWeaponDamage).
        /// Type factor (0.8.7 Blastzone.GetWeaponDamage): Frag x0.5; HEAT vs ship / sub /3, vs mobile land x2.5, vs static land /3 only for
        /// AirBase / Installation / Port (x1 for SAM, AAA, Radar, MissileSite, Bridge, OilRig); Cluster vs mobile land or SAM / AAA x2, else /4.
        /// landSub = LandUnitSubType of the class ini (case-insensitive: the ini spells "Airbase").</summary>
        public static double Damage(HeAmmo a, HeTargetKind k, double bonus, bool dud = false, bool mobile = false, string landSub = null)
        {
            if (a == null) return 0;
            double n = dud ? a.DudPower : a.Power;
            bool land = k == HeTargetKind.Land || k == HeTargetKind.Large;
            switch (a.Warhead)
            {
                case HeWarhead.Frag: n *= 0.5; break;
                case HeWarhead.Heat:
                    if (!land) n /= 3;
                    else if (mobile) n *= 2.5;
                    else if (SubIs(landSub, "AirBase") || SubIs(landSub, "Installation") || SubIs(landSub, "Port")) n /= 3;
                    break;
                case HeWarhead.Cluster: n = land && (mobile || SubIs(landSub, "SAM") || SubIs(landSub, "AAA")) ? n * 2 : n / 4; break;
            }
            n += n * bonus;
            return n * 1.1 * (a.DamageMult > 0 ? a.DamageMult : 1);
        }

        // ---------------------------------------------------------------- large objects (T-170, ADR-0065 / ADR-0070)

        /// <summary>Metres per Unity unit of an ini Scale ([check] live, ADR-0065 item 7); used for the size text only.</summary>
        public const double UnityM = 67.2;

        /// <summary>D against a large object (Blastzone.GetWeaponDamage, decomp 0.8.7): HEAT /3 only for Airbase / Installation / Port, x1 for Bridge / OilRig;
        /// Cluster /4 for every static land unit; Frag x0.5; then (1 + bonus) * 1.1 * DamageMult. One rule with <see cref="Damage"/>.</summary>
        public static double DamageLarge(HeAmmo a, string landSub, double bonus)
        {
            return Damage(a, HeTargetKind.Large, bonus, false, false, landSub);
        }

        /// <summary>j = ceil(0.99 I / 1.2 D), k = ceil(0.99 I / 0.85 D), m = ceil(0.99 I / 0.5 D); null when D &lt;= 0 or I &lt; 1.</summary>
        public static HeZoneHits ZoneHits(double d, double integrity)
        {
            if (!(d > 0) || !(integrity >= 1)) return null;
            double t = 0.99 * integrity;
            var z = new HeZoneHits { J = (int)Math.Ceiling(t / (1.2 * d)), K = (int)Math.Ceiling(t / (0.85 * d)), M = (int)Math.Ceiling(t / (0.5 * d)) };
            z.Over50 = z.K > 50;
            return z;
        }

        /// <summary>The two largest of |sx|, |sy|, |sz| times <see cref="UnityM"/>, the larger first.</summary>
        public static void ColliderSizeM(double sx, double sy, double sz, out double a, out double b)
        {
            var v = new[] { Math.Abs(sx), Math.Abs(sy), Math.Abs(sz) };
            Array.Sort(v);
            a = v[2] * UnityM;
            b = v[1] * UnityM;
        }

        public static bool Reachable(HeSystem s) { return s != null && s.Integrity >= 1 && s.Colliders > 0; }

        /// <summary>DamagePoints of the class ini, else the sum of the Integrity of the systems with Integrity >= 1.</summary>
        public static double ClassDp(HeClass c)
        {
            if (c == null) return 0;
            if (c.DamagePoints >= 0) return c.DamagePoints;
            double sum = 0;
            foreach (var s in c.Systems) if (s.Integrity >= 1) sum += s.Integrity;
            return sum;
        }

        /// <summary>Reachable systems with Integrity &lt;= 1.5 DP (all reachable when none), by Integrity descending, then in section order.</summary>
        public static List<HeSystem> ZoneCandidates(HeClass c)
        {
            var all = new List<HeSystem>();
            if (c == null) return all;
            double lim = 1.5 * ClassDp(c);
            foreach (var s in c.Systems) if (Reachable(s)) all.Add(s);
            var res = new List<HeSystem>();
            foreach (var s in all) if (s.Integrity <= lim) res.Add(s);
            if (res.Count == 0) res = all;
            SortByIntegrity(res);
            return res;
        }

        private static void SortByIntegrity(List<HeSystem> list)
        {
            for (int i = 1; i < list.Count; i++)   // stable insertion sort: equal Integrity keeps the section order
            {
                var x = list[i];
                int j = i - 1;
                while (j >= 0 && list[j].Integrity < x.Integrity) { list[j + 1] = list[j]; j--; }
                list[j + 1] = x;
            }
        }

        /// <summary>Prefix of zones (already sorted) while the running sum &lt;= 0.99 DP, the crossing zone included; clamped to [2, 4]
        /// (fewer when fewer zones exist). Display only: this is NOT the reference line.</summary>
        public static List<HeSystem> TopZones(IList<HeSystem> zones, double dp)
        {
            var res = new List<HeSystem>();
            if (zones == null) return res;
            double sum = 0;
            for (int i = 0; i < zones.Count && res.Count < 4; i++)
            {
                res.Add(zones[i]);
                sum += zones[i].Integrity;
                if (sum > 0.99 * dp && res.Count >= 2) break;
            }
            return res;
        }

        /// <summary>What one hit takes from the limit of a ship / submarine: k * D. A cluster warhead takes nothing (CheckForInstakill leaves early).</summary>
        public static double Loss(HeAmmo a, HeTargetKind k, double bonus, bool dud = false)
        {
            if (a == null || (k != HeTargetKind.Ship && k != HeTargetKind.Sub)) return 0;
            if (a.Warhead == HeWarhead.Cluster) return 0;
            bool sub = k == HeTargetKind.Sub;
            double kk = a.Kind == HeAmmoKind.Torpedo ? (sub ? 1.5 : 3.5) : (sub ? 1 : 2);
            return Damage(a, k, bonus, dud) * kk;
        }

        /// <summary>N = floor(B / L) + 1; null when the weapon does no damage to the target.</summary>
        public static HeLine Hits(double budget, double loss, string weapon)
        {
            if (!(loss > 0) || !(budget > 0)) return null;
            double x = budget / loss;
            int m = (int)Math.Round(x, MidpointRounding.AwayFromZero);
            var l = new HeLine { Weapon = weapon, X = x };
            l.Over20 = x >= 20;
            l.N = l.Over20 ? 21 : (int)Math.Floor(x) + 1;
            l.Range = !l.Over20 && m >= 1 && Math.Abs(x - m) <= 0.1;
            l.Spare = l.N - x;
            return l;
        }

        /// <summary>Blast severity at distance d = frac * R (Blastzone splash branch): 1 at the centre, 0 from R on, never below 0.1 inside.</summary>
        public static double Severity(double frac)
        {
            if (frac >= 1.0) return 0;
            if (frac < 0) frac = 0;
            double n17 = (1 - Math.Sqrt(0.1)) / (2 * Math.Sqrt(0.1 * Math.PI));
            double n18 = Math.Sqrt(1 / (4 * Math.PI));
            double t = n17 * frac + n18;
            return Math.Max(1 / (Math.PI * 4 * t * t), 0.1);
        }

        /// <summary>The damage roll is U(0.5 D, 1.2 D); "for sure" = it holds in 99 % of the rolls, i.e. the 1 % quantile 0.5 + 0.01 * 0.7 (the reference of estimate.py land).</summary>
        private const double SureRoll = 0.507;

        /// <summary>The largest distance (m, step 1% of R) at which the weakest armour roll and the 1 % quantile of the damage roll still destroy the system; -1 = not for sure at any distance.</summary>
        public static double SureRadiusM(HeAmmo a, HeSystem s, double bonus, bool mobile, string landSub)
        {
            if (a == null || s == null || s.Integrity < 1 || !(a.RadiusM > 0)) return -1;
            int ai = Array.IndexOf(Armors, (s.Armor ?? "none").Trim().ToLowerInvariant());
            int pi = Array.IndexOf(Pens, (a.Penetration ?? "minor").Trim().ToLowerInvariant());
            if (ai < 0) ai = 0;
            if (pi < 0) pi = 0;
            double lo = PenLo[ai][pi], hi = PenHi[ai][pi];
            if (hi < 1.0 && hi + bonus < 1.0) return -1;   // the penetration roll can fail
            double d = Damage(a, HeTargetKind.Land, bonus, false, mobile, landSub);
            double best = -1;
            for (int step = 0; step <= 99; step++)
            {
                double f = step / 100.0;
                double min = Math.Max(0.5, lo * Severity(f) * SureRoll * d);
                if (min >= 0.99 * s.Integrity) best = f * a.RadiusM; else break;
            }
            return best;
        }

        /// <summary>p = sum(L) / B: below 0.34 light, below 0.67 medium, below 1 heavy, from 1 dead.</summary>
        public static HeStatus Status(double p)
        {
            if (double.IsNaN(p) || p <= 0) return HeStatus.None;
            if (p < 0.34) return HeStatus.Light;
            if (p < 0.67) return HeStatus.Medium;
            if (p < 1.0) return HeStatus.Heavy;
            return HeStatus.Dead;
        }

        // ---------------------------------------------------------------- self test

        // The reference table (also read by tests/test_sea_power_hit_estimate_math.py): weapon|target|bonus|N|x   (x = B / L)
        internal static readonly string[] Ref =
        {
            "usn_agm-84d|wp_skr_petya2|0.25|2|1.05",
            "usn_mk48|usn_cg_ticonderoga|0.25|1|0.97",
            "usn_mk46_ship|wp_ss_kilo|0.25|2|1.63",
            "wp_ss-n-2|civ_ms_amra|0.25|1|0.93",
            "usn_mk-84|usn_cvn_nimitz|0.25|5|4.25",
            "usn_mk-82|wp_ptg_osa1|0.25|1|0.96",
        };

        private static HeAmmo A(string file, HeAmmoKind k, double power)
        {
            return new HeAmmo { File = file, Name = file, Kind = k, Power = power, DudPower = power * (k == HeAmmoKind.Missile ? 0.5 : 0.2), Surface = true, Subsurface = k == HeAmmoKind.Torpedo };
        }

        private static HeClass C(string n, HeTargetKind k, double disp, string armor, bool civ = false)
        {
            return new HeClass { IniName = n, Kind = k, Displacement = disp, Armor = armor, Civilian = civ };
        }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            var inv = CultureInfo.InvariantCulture;

            var ammo = new Dictionary<string, HeAmmo>
            {
                { "usn_agm-84d", A("usn_agm-84d", HeAmmoKind.Missile, 45) },
                { "usn_mk48", A("usn_mk48", HeAmmoKind.Torpedo, 68) },
                { "usn_mk46_ship", A("usn_mk46_ship", HeAmmoKind.Torpedo, 27) },
                { "wp_ss-n-2", A("wp_ss-n-2", HeAmmoKind.Missile, 66) },
                { "usn_mk-84", A("usn_mk-84", HeAmmoKind.Bomb, 62) },
                { "usn_mk-82", A("usn_mk-82", HeAmmoKind.Bomb, 28) },
            };
            var cls = new Dictionary<string, HeClass>
            {
                { "wp_skr_petya2", C("wp_skr_petya2", HeTargetKind.Ship, 1110, "none") },
                { "usn_cg_ticonderoga", C("usn_cg_ticonderoga", HeTargetKind.Ship, 9600, "minor") },
                { "wp_ss_kilo", C("wp_ss_kilo", HeTargetKind.Sub, 3040, "none") },
                { "civ_ms_amra", C("civ_ms_amra", HeTargetKind.Ship, 20000, "none", true) },
                { "usn_cvn_nimitz", C("usn_cvn_nimitz", HeTargetKind.Ship, 100000, "moderate") },
                { "wp_ptg_osa1", C("wp_ptg_osa1", HeTargetKind.Ship, 209, "none") },
            };
            foreach (var row in Ref)
            {
                var p = row.Split('|');
                double bonus = double.Parse(p[2], inv), x = double.Parse(p[4], inv);
                var c = cls[p[1]];
                var h = Hits(Budget(c), Loss(ammo[p[0]], c.Kind, bonus), p[0]);
                chk(h != null && h.N == int.Parse(p[3], inv) && Math.Abs(h.X - x) < 0.006, "ref " + row + " got " + (h == null ? "null" : h.N + "/" + h.X.ToString("0.000", inv)));
            }
            var hpet = Hits(Budget(cls["wp_skr_petya2"]), Loss(ammo["usn_agm-84d"], HeTargetKind.Ship, 0.25), "x");
            chk(hpet.Range && hpet.N == 2, "petya boundary 1-2");
            chk(!Hits(Budget(cls["wp_skr_petya2"]), Loss(ammo["usn_mk-82"], HeTargetKind.Ship, 0.25), "x").Range, "no boundary");
            // bonus 0 / 0.25 / 0.5 -> Harpoon and Ticonderoga 4 / 3 / 3
            var tic = cls["usn_cg_ticonderoga"];
            int[] want = { 4, 3, 3 };
            double[] bon = { 0, 0.25, 0.5 };
            for (int i = 0; i < 3; i++)
            {
                var h = Hits(Budget(tic), Loss(ammo["usn_agm-84d"], HeTargetKind.Ship, bon[i]), "x");
                chk(h.N == want[i], "bonus " + bon[i] + " -> " + h.N);
            }
            // integrity override replaces everything; rafts and mines
            var ov = C("ov", HeTargetKind.Ship, 100000, "heavy"); ov.IntegrityOverride = 12;
            chk(Math.Abs(MaxIntegrity(ov) - 12) < 1e-9 && Math.Abs(Budget(ov) - 60) < 1e-9, "override");
            var raft = C("r", HeTargetKind.Ship, 100000, "heavy"); raft.IntegrityOverride = 12; raft.RaftOrMine = true;
            chk(Math.Abs(MaxIntegrity(raft) - 0.1) < 1e-9, "raft/mine 0.1");
            // item 22: no ArmorType = minor (the class default), damage-control teams cancel the x0.5
            chk(new HeClass().Armor == "minor", "armor default minor");
            var civ = C("civ", HeTargetKind.Ship, 8000, "none", true);
            double c0 = MaxIntegrity(civ);
            civ.DcTeams = 2;
            chk(Math.Abs(MaxIntegrity(civ) - 2 * c0) < 1e-9, "dc teams: civilian without x0.5");
            var sub = C("sub", HeTargetKind.Sub, 8000, "none"); double s0 = MaxIntegrity(sub); sub.DcTeams = 1;
            chk(Math.Abs(MaxIntegrity(sub) - 2 * s0) < 1e-9, "dc teams: sub without x0.5");
            // item 22: a cluster warhead takes nothing from a ship or a submarine
            var cl = A("cl", HeAmmoKind.Bomb, 40); cl.Warhead = HeWarhead.Cluster;
            chk(Loss(cl, HeTargetKind.Ship, 0.25) == 0 && Loss(cl, HeTargetKind.Sub, 0.25) == 0 && Hits(100, Loss(cl, HeTargetKind.Ship, 0), "cl") == null, "cluster vs ship");
            chk(Damage(cl, HeTargetKind.Land, 0, false, true) > Damage(cl, HeTargetKind.Land, 0, false, false), "cluster land mobile x2 vs /4");
            // static land subtypes (0.8.7 Blastzone.GetWeaponDamage): cluster x2 vs SAM / AAA, /4 vs other static; HEAT /3 only vs AirBase / Installation / Port
            chk(Math.Abs(Damage(cl, HeTargetKind.Land, 0, false, false, "SAM") - 80 * 1.1) < 1e-9 && Math.Abs(Damage(cl, HeTargetKind.Land, 0, false, false, "aaa") - 80 * 1.1) < 1e-9, "cluster SAM/AAA x2");
            chk(Math.Abs(Damage(cl, HeTargetKind.Land, 0, false, false, "Radar") - 10 * 1.1) < 1e-9 && Math.Abs(Damage(cl, HeTargetKind.Land, 0, false, false, "Installation") - 10 * 1.1) < 1e-9, "cluster static /4");
            var h0 = A("h0", HeAmmoKind.Missile, 30); h0.Warhead = HeWarhead.Heat; h0.RadiusM = 20;
            chk(Math.Abs(Damage(h0, HeTargetKind.Land, 0, false, false, "SAM") - 30 * 1.1) < 1e-9 && Math.Abs(Damage(h0, HeTargetKind.Land, 0, false, false, "Radar") - 30 * 1.1) < 1e-9
                && Math.Abs(Damage(h0, HeTargetKind.Land, 0, false, false, "MissileSite") - 30 * 1.1) < 1e-9, "heat SAM/Radar/MissileSite x1");
            chk(Math.Abs(Damage(h0, HeTargetKind.Land, 0, false, false, "Installation") - 10 * 1.1) < 1e-9 && Math.Abs(Damage(h0, HeTargetKind.Large, 0, false, false, "Airbase") - 10 * 1.1) < 1e-9
                && Math.Abs(Damage(h0, HeTargetKind.Large, 0, false, false, "Port") - 10 * 1.1) < 1e-9, "heat Installation/Airbase/Port /3");
            chk(Math.Abs(Damage(h0, HeTargetKind.Land, 0, false, true, "MobileUnit") - 75 * 1.1) < 1e-9, "heat mobile x2.5");
            chk(SureRadiusM(h0, new HeSystem { Integrity = 5, Armor = "none" }, 0, false, "SAM") > SureRadiusM(h0, new HeSystem { Integrity = 5, Armor = "none" }, 0, false, "Installation"), "sure radius uses the subtype");
            // dud power, heat, fragmentation
            var h1 = A("h1", HeAmmoKind.Missile, 30); h1.Warhead = HeWarhead.Heat;
            chk(Math.Abs(Damage(h1, HeTargetKind.Ship, 0) - 30 / 3.0 * 1.1) < 1e-9, "heat /3");
            var fr = A("fr", HeAmmoKind.Missile, 30); fr.Warhead = HeWarhead.Frag;
            chk(Math.Abs(Damage(fr, HeTargetKind.Ship, 0) - 15 * 1.1) < 1e-9, "frag x0.5");
            chk(Damage(ammo["usn_agm-84d"], HeTargetKind.Ship, 0.25, true) < Damage(ammo["usn_agm-84d"], HeTargetKind.Ship, 0.25), "dud weaker");
            // more than 20 hits
            var big = Hits(1000, 10, "w");
            chk(big.Over20 && !big.Range, ">20");
            chk(Hits(100, 0, "w") == null && Hits(0, 10, "w") == null, "no damage / no pool");
            // Spare: the steadier estimate is larger
            chk(Hits(100, 60, "a").Spare < Hits(100, 90, "b").Spare + 1e-9 && Math.Abs(Hits(100, 60, "a").Spare - (2 - 100.0 / 60)) < 1e-9, "spare");

            // severity
            chk(Math.Abs(Severity(0) - 1) < 1e-9 && Math.Abs(Severity(0.5) - 0.23) < 0.01 && Severity(1) == 0 && Severity(2) == 0 && Severity(0.999) >= 0.1, "severity");
            // sure radius vs a coastal artillery (one system, I = 5, armour none): Mk82 about 9 m, Mk84 about 54 m
            var gun = new HeSystem { Section = "ControlSystem", Integrity = 5, Armor = "none" };
            var m82 = ammo["usn_mk-82"]; m82.Warhead = HeWarhead.Normal; m82.RadiusM = 20; m82.Penetration = "moderate";
            var m84 = ammo["usn_mk-84"]; m84.RadiusM = 60; m84.Penetration = "heavy";
            chk(Math.Abs(SureRadiusM(m82, gun, 0.25, false, "Installation") - 9.0) <= 0.5, "sure Mk82 " + SureRadiusM(m82, gun, 0.25, false, "Installation"));
            chk(Math.Abs(SureRadiusM(m84, gun, 0.25, false, "Installation") - 54.0) <= 0.5, "sure Mk84 " + SureRadiusM(m84, gun, 0.25, false, "Installation"));
            var armoured = new HeSystem { Section = "w", Integrity = 5, Armor = "heavy" };
            chk(SureRadiusM(m82, armoured, 0, false, null) < 0, "armour: not for sure");
            chk(SureRadiusM(m82, new HeSystem { Integrity = 0.5 }, 0.25, false, null) < 0, "integrity < 1 never damaged");

            // ---- T-170: large objects
            var zh = ZoneHits(38.5, 100);
            chk(zh != null && zh.J == 3 && zh.K == 4 && zh.M == 6, "zone hits Mk82 3/4/6");
            zh = ZoneHits(85.25, 100); chk(zh.J == 1 && zh.K == 2 && zh.M == 3, "zone hits Mk84 1/2/3");
            zh = ZoneHits(107.3, 100); chk(zh.J == 1 && zh.K == 2 && zh.M == 2, "zone hits FAB-1500 1/2/2");
            zh = ZoneHits(85.25, 450); chk(zh.J == 5 && zh.K == 7 && zh.M == 11, "zone hits runway 5/7/11");
            chk(ZoneHits(38.5, 3000).Over50, "zone hits over 50");
            zh = ZoneHits(269.5, 100); chk(zh.J == 1 && zh.K == 1 && zh.M == 1 && !zh.Over50, "zone hits J == M");
            chk(ZoneHits(0, 100) == null && ZoneHits(38.5, 0.5) == null, "zone hits null");
            var lh = A("lh", HeAmmoKind.Missile, 30); lh.Warhead = HeWarhead.Heat;
            chk(Math.Abs(DamageLarge(lh, "Airbase", 0) - 30 / 3.0 * 1.1) < 1e-9 && Math.Abs(DamageLarge(lh, "Bridge", 0) - 30 * 1.1) < 1e-9
                && Math.Abs(DamageLarge(lh, "OilRig", 0) - 30 * 1.1) < 1e-9, "DamageLarge heat");
            var lc = A("lc", HeAmmoKind.Bomb, 40); lc.Warhead = HeWarhead.Cluster;
            chk(Math.Abs(DamageLarge(lc, "Bridge", 0) - 40 / 4.0 * 1.1) < 1e-9 && Math.Abs(DamageLarge(lc, "Port", 0) - 40 / 4.0 * 1.1) < 1e-9, "DamageLarge cluster");
            var lf = A("lf", HeAmmoKind.Missile, 30); lf.Warhead = HeWarhead.Frag;
            chk(Math.Abs(DamageLarge(lf, "Installation", 0.25) - 15 * 1.25 * 1.1) < 1e-9, "DamageLarge frag + bonus");
            chk(Math.Abs(DamageLarge(m84, "Airbase", 0.25) - 85.25) < 1e-9 && Math.Abs(DamageLarge(m82, "Port", 0.25) - 38.5) < 1e-9, "DamageLarge Mk84 / Mk82");
            double sa, sb; ColliderSizeM(31.5, 0.01, 0.8, out sa, out sb);
            chk(Math.Abs(sa - 2117) <= 1 && Math.Abs(sb - 54) <= 1, "collider size " + sa + "x" + sb);
            // zones: a "selection" system above 1.5 DP is no candidate; unreachable / tiny ones are no zones
            var lcl = new HeClass { Kind = HeTargetKind.Large, DamagePoints = 300 };
            lcl.Systems.Add(new HeSystem { Section = "ControlSystem", Integrity = 1000, Colliders = 1 });
            lcl.Systems.Add(new HeSystem { Section = "HitZone1", Integrity = 60, Colliders = 1 });
            lcl.Systems.Add(new HeSystem { Section = "HitZone2", Integrity = 100, Colliders = 1 });
            lcl.Systems.Add(new HeSystem { Section = "HitZone3", Integrity = 60, Colliders = 1 });
            lcl.Systems.Add(new HeSystem { Section = "HitZone4", Integrity = 200, Colliders = 0 });
            lcl.Systems.Add(new HeSystem { Section = "HitZone5", Integrity = 0.5, Colliders = 1 });
            var cand = ZoneCandidates(lcl);
            chk(cand.Count == 3 && cand[0].Section == "HitZone2" && cand[1].Section == "HitZone1" && cand[2].Section == "HitZone3", "zone candidates order");
            chk(!Reachable(lcl.Systems[4]) && !Reachable(lcl.Systems[5]) && Reachable(lcl.Systems[1]), "reachable");
            lcl.DamagePoints = -1;
            chk(Math.Abs(ClassDp(lcl) - 1420) < 1e-9, "ClassDp sums when DP is -1");
            var one = new List<HeSystem> { new HeSystem { Integrity = 600 }, new HeSystem { Integrity = 10 }, new HeSystem { Integrity = 5 } };
            chk(TopZones(one, 600).Count == 2, "TopZones at least 2");
            var six = new List<HeSystem>();
            foreach (var iv in new double[] { 200, 150, 150, 100, 100, 100 }) six.Add(new HeSystem { Integrity = iv });
            chk(TopZones(six, 600).Count == 4, "TopZones at most 4");
            chk(TopZones(new List<HeSystem> { new HeSystem { Integrity = 450 }, new HeSystem { Integrity = 300 }, new HeSystem { Integrity = 300 } }, 600).Count == 2, "TopZones Keflavik 2");
            chk(TopZones(new List<HeSystem> { new HeSystem { Integrity = 50 } }, 50).Count == 1, "TopZones one zone");

            // status
            chk(Status(0) == HeStatus.None && Status(0.3399) == HeStatus.Light && Status(0.34) == HeStatus.Medium && Status(0.6699) == HeStatus.Medium
                && Status(0.67) == HeStatus.Heavy && Status(0.9999) == HeStatus.Heavy && Status(1.0) == HeStatus.Dead && Status(double.NaN) == HeStatus.None, "status bounds");
            return err;
        }
    }
}
