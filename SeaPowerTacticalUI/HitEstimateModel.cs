using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge.Ui
{
    // T-168 / ADR-0064 (supplements 4 and 5): the block under an expanded Overview row. Pure: it asks IHitFacts for facts and builds lines of text.

    /// <summary>One entry of the own armament: an ammunition on a carrier. Count = rounds (ships, aircraft in the air) or rounds of the loadout times airframes (hangar).</summary>
    internal sealed class HeCandidate
    {
        public HeAmmo Ammo;
        public string Unit;   // "Perry", "Eisenhower: A-6E"
        public int Count;
    }

    /// <summary>A candidate for the current target: ammunition + carrier, with the estimate (null for land).</summary>
    internal sealed class HeOption
    {
        public HeAmmo Ammo;
        public string Unit;
        public int Count;
        public string Key;         // the weapon name ("n:Mk-46 Mod 5"; the file when unnamed): one entry per weapon type
        public HeLine Line;
        public double SureM = -1;  // land: the distance of a sure kill of the key system
        public int Ref = -1;       // large object: the reference line of this weapon (ADR-0070), -1 = none
    }

    /// <summary>The player's weapon choice for the session (ADR-0064 item 31): the key of the entry, global for all targets.</summary>
    internal sealed class HeSelection
    {
        public string Key;
        public void Clear() { Key = null; }
    }

    /// <summary>The adapter to the game (GameHitFacts). In the leak test every read of a foreign unit throws.</summary>
    internal interface IHitFacts
    {
        bool InMission { get; }
        bool IsForeign(int trackId);                    // the unit behind the track is neither the player's nor an ally's
        bool IsIdentified(int trackId);
        string ClassIniName(int trackId);               // only after IsIdentified == true
        HeClass Class(string iniName);                  // null = no data; cached
        IList<HeCandidate> Candidates();                // ALL own carriers of the mission: ships, hangars and decks, aircraft in the air
        HeAmmo AmmoByFile(string file);                 // parameters of an own ammunition by file (cache filled by Candidates and by the hit hook)
        double DamageBonus(out bool known);
        string ClassWhy(string iniName);                // diagnostics: why the class has (no) data
    }

    internal sealed class HeSettings { public bool Enabled = true, Counter = true, Land = true, Installations = true; }
    internal sealed class HeCell { public int Hits; public HeStatus Status; public double P = double.NaN; }
    internal sealed class HeBlockLine { public string Text; public uint Argb; }   // 0 = the default colour

    internal sealed class HeDetail
    {
        public string Pick;                              // "weapon · where" of the current choice; null = no choice shown
        public int Choices, PickIndex;                   // Choices > 1: the control cycles
        public List<HeBlockLine> Estimate = new List<HeBlockLine>(), Hits = new List<HeBlockLine>();
        public HeBlockLine Total;
        public int Lines;                                // rows of the block, the pick control included
        // diagnostics (dev section hit_estimate)
        public string ClassName, ClassWhy, Reason;       // Reason: ok | no_class | no_weapon | none | large | land_off | immune
        public int Candidates, Options;
    }

    internal static class HitEstimateModel
    {
        public const int MaxHitLines = 20;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string T(string key, params object[] args) { return UiText.Fmt(key, args); }

        /// <summary>An identified foreign track: only then the row has a "+". The class is not asked for anything else.</summary>
        public static bool Expandable(IHitFacts f, int trackId)
        {
            if (f == null || !f.InMission) return false;
            return f.IsForeign(trackId) && f.IsIdentified(trackId);
        }

        public static uint Argb(HeStatus s)
        {
            switch (s)
            {
                case HeStatus.Light: return 0xFFFFD000;
                case HeStatus.Medium: return 0xFFFF8000;
                case HeStatus.Heavy: return 0xFFFF2020;
                case HeStatus.Dead: return 0xFF901010;
                default: return 0;
            }
        }

        public static string StatusText(HeStatus s)
        {
            switch (s)
            {
                case HeStatus.Light: return T("he.status_light");
                case HeStatus.Medium: return T("he.status_medium");
                case HeStatus.Heavy: return T("he.status_heavy");
                case HeStatus.Dead: return T("he.status_dead");
                default: return "";
            }
        }

        private static HeClass ClassOf(IHitFacts f, int trackId)
        {
            string n = f.ClassIniName(trackId);
            return string.IsNullOrEmpty(n) ? null : f.Class(n);
        }

        /// <summary>True when the hit takes part in p = sum(L) / B: a missile, torpedo, bomb or depth charge whose warhead takes the limit of a ship.</summary>
        private static bool Counts(HeHit h)
        {
            return h.Kind != HeAmmoKind.Rocket && h.Warhead != HeWarhead.Cluster;
        }

        private static double HitLoss(IHitFacts f, HeHit h, HeClass c, double bonus)
        {
            if (!Counts(h)) return double.NaN;
            var a = f.AmmoByFile(h.AmmoFile);
            if (a == null) return double.NaN;
            double l = HitEstimateMath.Loss(a, c.Kind, bonus, h.Dud);
            return l > 0 ? l : double.NaN;
        }

        /// <summary>The cells of the columns hits / dmg. null = "—" (not expandable, no hits).</summary>
        public static HeCell Cell(IHitFacts f, int trackId, HitLedger led)
        {
            if (led == null || !Expandable(f, trackId)) return null;
            int n = led.Count(trackId);
            if (n == 0) return null;
            var cell = new HeCell { Hits = n, Status = HeStatus.None };
            var c = ClassOf(f, trackId);
            if (c == null || (c.Kind != HeTargetKind.Ship && c.Kind != HeTargetKind.Sub)) return cell;
            double b = HitEstimateMath.Budget(c);
            if (!(b > 0)) return cell;
            bool known;
            double bonus = f.DamageBonus(out known);
            double sum = 0; bool any = false;
            foreach (var h in led.All(trackId))
            {
                double l = HitLoss(f, h, c, bonus);
                if (double.IsNaN(l)) continue;
                sum += l; any = true;
            }
            if (!any) return cell;
            cell.P = sum / b;
            cell.Status = HitEstimateMath.Status(cell.P);
            return cell;
        }

        private static bool Domain(HeAmmo a, HeClass c)
        {
            switch (c.Kind)
            {
                case HeTargetKind.Ship:
                    if (a.NoShip) return false;   // as DoesAmmoMatchTarget: primary OR secondary ASuW, and the ammunition may forbid the object type
                    return a.Surface && a.Kind != HeAmmoKind.Rocket && a.Kind != HeAmmoKind.DepthCharge && a.Warhead != HeWarhead.Cluster;
                case HeTargetKind.Sub:
                    if (a.NoSub) return false;
                    return a.Subsurface && a.Kind != HeAmmoKind.Rocket && a.Warhead != HeWarhead.Cluster;
                case HeTargetKind.Land:
                    return (c.Mobile ? a.LandMobile : a.LandInstall) && (a.Kind == HeAmmoKind.Missile || a.Kind == HeAmmoKind.Bomb);
                case HeTargetKind.Large:
                    return a.LandInstall && (a.Kind == HeAmmoKind.Missile || a.Kind == HeAmmoKind.Bomb);
                default:
                    return false;
            }
        }

        /// <summary>One entry per weapon type as the player sees it: ammunition files with the same name (ship and air variants of Mk-46 Mod 5) merge (live 0.28.9).</summary>
        private static string KeyOf(HeCandidate x) { return string.IsNullOrEmpty(x.Ammo.Name) ? x.Ammo.File : "n:" + x.Ammo.Name; }

        /// <summary>The candidates that can hurt the target, merged by (ammunition, carrier) and ordered: fewest hits, then the steadier estimate, then the name.
        /// Land targets: the surest kill first, then the name.</summary>
        public static List<HeOption> Options(HeClass c, IList<HeCandidate> cands, double bonus)
        {
            var res = new List<HeOption>();
            if (c == null || cands == null || c.Kind == HeTargetKind.None || (c.Kind == HeTargetKind.Large && !c.ZonesRead)) return res;
            var byKey = new Dictionary<string, HeOption>();
            foreach (var x in cands)
            {
                if (x == null || x.Ammo == null || x.Count <= 0 || string.IsNullOrEmpty(x.Unit) || !Domain(x.Ammo, c)) continue;
                string key = KeyOf(x);
                HeOption o;
                if (byKey.TryGetValue(key, out o)) { o.Count += x.Count; continue; }
                o = new HeOption { Ammo = x.Ammo, Unit = x.Unit, Count = x.Count, Key = key };
                byKey[key] = o;
                res.Add(o);
            }
            if (c.Kind == HeTargetKind.Land)
            {
                var ks = KeySystem(c);
                foreach (var o in res) o.SureM = ks == null ? -1 : HitEstimateMath.SureRadiusM(o.Ammo, ks, bonus, c.Mobile, c.LandSubType);
                return res.OrderByDescending(o => o.SureM).ThenBy(o => o.Ammo.Name, StringComparer.Ordinal).ThenBy(o => o.Key, StringComparer.Ordinal).ToList();
            }
            if (c.Kind == HeTargetKind.Large)
            {
                // the reference line of each weapon (ADR-0070, cached per class): the fewest bombs first, a weapon without a reference last
                foreach (var o in res) o.Ref = HitEstimateInst.RefFor(c, o.Ammo, bonus) ?? -1;
                return res.OrderBy(o => o.Ref < 0 ? 1 : 0).ThenBy(o => o.Ref).ThenBy(o => o.Ammo.Name, StringComparer.Ordinal).ThenBy(o => o.Key, StringComparer.Ordinal).ToList();
            }
            double budget = HitEstimateMath.Budget(c);
            var kept = new List<HeOption>();
            foreach (var o in res)
            {
                o.Line = HitEstimateMath.Hits(budget, HitEstimateMath.Loss(o.Ammo, c.Kind, bonus), o.Ammo.Name);
                if (o.Line != null) kept.Add(o);
            }
            return kept.OrderBy(o => o.Line.N).ThenByDescending(o => o.Line.Spare).ThenBy(o => o.Ammo.Name, StringComparer.Ordinal).ThenBy(o => o.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>The index of the entry to show: the player's choice, else the same ammunition on another carrier, else the default (0). -1 = no entries.</summary>
        public static int PickIndex(IList<HeOption> opts, HeSelection sel)
        {
            if (opts == null || opts.Count == 0) return -1;
            if (sel == null || string.IsNullOrEmpty(sel.Key)) return 0;
            for (int i = 0; i < opts.Count; i++) if (opts[i].Key == sel.Key) return i;
            return 0;
        }

        /// <summary>Moves the choice by dir (+1 / -1) around the ring and remembers it. Returns the new index (-1 = nothing to choose).</summary>
        public static int Cycle(IList<HeOption> opts, HeSelection sel, int dir)
        {
            int i = PickIndex(opts, sel);
            if (i < 0) return -1;
            int n = opts.Count;
            i = ((i + (dir >= 0 ? 1 : -1)) % n + n) % n;
            if (sel != null) sel.Key = opts[i].Key;
            return i;
        }

        /// <summary>The selector shows the weapon type only: the display name of the ammunition (ADR-0064 item 35).</summary>
        public static string PickText(HeOption o) { return o.Ammo.Name; }

        public static string EstimateText(HeLine l)
        {
            if (l.Over20) return T("he.over20");
            string range = l.Range ? " " + T("he.range", (int)Math.Round(l.X, MidpointRounding.AwayFromZero), (int)Math.Round(l.X, MidpointRounding.AwayFromZero) + 1) : "";
            return T("he.est_line", l.N, range);
        }

        // ---------------------------------------------------------------- land

        /// <summary>The system a land target is killed by (the hardest one that has to go): the hardest radar of a SAM, a system with I >= 0.99 DP, else the biggest one.</summary>
        public static HeSystem KeySystem(HeClass c)
        {
            if (c == null) return null;
            var sys = c.Systems.Where(s => s.Integrity >= 1).ToList();
            if (sys.Count == 0) return null;
            if (c.LandSubType == "SAM")
            {
                var radars = sys.Where(s => s.Radar).ToList();
                if (radars.Count > 0) return radars.OrderByDescending(s => s.Integrity).First();
            }
            double dp = c.DamagePoints >= 0 ? c.DamagePoints : sys.Sum(s => s.Integrity);
            var one = sys.Where(s => s.Integrity >= 0.99 * dp).OrderBy(s => s.Integrity).FirstOrDefault();
            return one ?? sys.OrderByDescending(s => s.Integrity).First();
        }

        /// <summary>What to destroy on a land target: "radars" (n = the radars), "one" (n = 1), "dp" (n = DamagePoints rounded); null = nothing to say.</summary>
        public static string LandKill(HeClass c, out int keySystems)
        {
            keySystems = 0;
            if (c == null) return null;
            var sys = c.Systems.Where(s => s.Integrity >= 1).ToList();
            if (sys.Count == 0) return null;
            double dp = c.DamagePoints >= 0 ? c.DamagePoints : sys.Sum(s => s.Integrity);
            if (c.LandSubType == "SAM")
            {
                int radars = sys.Count(s => s.Radar);
                if (radars > 0) { keySystems = radars; return "radars"; }
            }
            if (sys.Any(s => s.Integrity >= 0.99 * dp)) { keySystems = 1; return "one"; }
            keySystems = (int)Math.Round(dp, MidpointRounding.AwayFromZero);
            return "dp";
        }

        private static string LandKillText(HeClass c)
        {
            int n;
            string k = LandKill(c, out n);
            if (k == null) return null;
            string what = k == "radars" ? T("he.kill_radars", n) : k == "one" ? T("he.kill_one") : T("he.kill_dp", n);
            return T("he.land_kill", what);
        }

        // ---------------------------------------------------------------- the block

        /// <summary>The block of an expanded row; null when the row is not expandable. Reads the class only here, and only for an identified foreign track.</summary>
        public static HeDetail Detail(IHitFacts f, int trackId, HitLedger led, HeSettings s, HeSelection sel)
        {
            if (!Expandable(f, trackId)) return null;
            s = s ?? new HeSettings();
            var d = new HeDetail();
            string cname = f.ClassIniName(trackId);
            var c = string.IsNullOrEmpty(cname) ? null : f.Class(cname);
            d.ClassName = cname; d.ClassWhy = f.ClassWhy(cname);
            bool known;
            double bonus = f.DamageBonus(out known);
            if (c != null) FillEstimate(f, c, bonus, s, sel, d);
            else { d.Reason = "no_class"; d.Estimate.Add(new HeBlockLine { Text = T("he.no_class") }); }
            if (s.Counter && led != null) FillHits(f, trackId, led, c, bonus, d);
            d.Lines = (d.Pick != null ? 1 : 0) + d.Estimate.Count + d.Hits.Count + (d.Total != null ? 1 : 0);
            return d;
        }

        /// <summary>The entries of the choice for a track (the control cycles through exactly these). Empty when there is nothing to choose.</summary>
        public static List<HeOption> OptionsFor(IHitFacts f, int trackId, HeSettings s)
        {
            if (!Expandable(f, trackId)) return new List<HeOption>();
            var c = ClassOf(f, trackId);
            s = s ?? new HeSettings();
            if (c == null || (c.Kind == HeTargetKind.Land && !s.Land) || (c.Kind == HeTargetKind.Large && !HitEstimateInst.Handles(c, s))) return new List<HeOption>();
            bool known;
            return Options(c, f.Candidates(), f.DamageBonus(out known));
        }

        private static void FillEstimate(IHitFacts f, HeClass c, double bonus, HeSettings s, HeSelection sel, HeDetail d)
        {
            d.Reason = "ok";
            if (c.Kind == HeTargetKind.None) { d.Reason = "none"; d.Estimate.Add(new HeBlockLine { Text = T("he.no_est") }); return; }
            if (c.Kind == HeTargetKind.Large && !HitEstimateInst.Handles(c, s)) { d.Reason = "large"; d.Estimate.Add(new HeBlockLine { Text = T("he.large") }); return; }
            if (c.Kind == HeTargetKind.Land && !s.Land) { d.Reason = "land_off"; d.Estimate.Add(new HeBlockLine { Text = T("he.no_est") }); return; }
            var cands = f.Candidates();
            d.Candidates = cands == null ? 0 : cands.Count;
            var opts = Options(c, cands, bonus);
            d.Options = opts.Count;
            if (c.Kind == HeTargetKind.Large) { HitEstimateInst.Fill(c, opts, sel, bonus, d); return; }
            if (opts.Count == 0) d.Reason = "no_weapon";
            if (c.Kind == HeTargetKind.Land)
            {
                string kill = LandKillText(c);
                if (kill != null) d.Estimate.Add(new HeBlockLine { Text = kill });
                if (opts.Count == 0) { d.Estimate.Add(new HeBlockLine { Text = T("he.no_weapon") }); return; }
            }
            else if (opts.Count == 0)
            {
                d.Estimate.Add(new HeBlockLine { Text = T("he.no_weapon") });
                return;
            }
            int i = PickIndex(opts, sel);
            var o = opts[i];
            d.Choices = opts.Count; d.PickIndex = i; d.Pick = PickText(o);
            if (c.Kind == HeTargetKind.Land)
            {
                int r = (int)Math.Round(o.Ammo.RadiusM, MidpointRounding.AwayFromZero);
                d.Estimate.Add(new HeBlockLine { Text = o.SureM >= 0 ? T("he.land_radius", r, (int)Math.Floor(o.SureM)) : T("he.land_unsure", r) });
                return;
            }
            d.Estimate.Add(new HeBlockLine { Text = EstimateText(o.Line) });
        }

        private static string KindText(HeAmmoKind k)
        {
            switch (k)
            {
                case HeAmmoKind.Torpedo: return T("he.kind_torpedo");
                case HeAmmoKind.Bomb: return T("he.kind_bomb");
                case HeAmmoKind.Rocket: return T("he.kind_rocket");
                case HeAmmoKind.DepthCharge: return T("he.kind_depth");
                default: return T("he.kind_missile");
            }
        }

        private static void FillHits(IHitFacts f, int trackId, HitLedger led, HeClass c, double bonus, HeDetail d)
        {
            int n = led.Count(trackId);
            if (n == 0) return;
            bool ship = c != null && (c.Kind == HeTargetKind.Ship || c.Kind == HeTargetKind.Sub);
            double b = ship ? HitEstimateMath.Budget(c) : 0;
            foreach (var h in led.Recent(trackId, MaxHitLines))
            {
                string pct = null;
                if (ship && b > 0)
                {
                    double l = HitLoss(f, h, c, bonus);
                    if (!double.IsNaN(l)) pct = ((int)Math.Round(100 * l / b, MidpointRounding.AwayFromZero)).ToString(Inv);
                }
                string dud = h.Dud ? T("he.dud") : "";
                string text = pct == null
                    ? T("he.hit_line.nopct", h.Clock, h.Weapon, h.Shooter, KindText(h.Kind), dud)
                    : T("he.hit_line", h.Clock, h.Weapon, h.Shooter, KindText(h.Kind), dud, pct);
                d.Hits.Add(new HeBlockLine { Text = text });
            }
            if (n > MaxHitLines) d.Hits.Add(new HeBlockLine { Text = T("he.more", n - MaxHitLines) });
            var cell = Cell(f, trackId, led);
            var st = cell == null ? HeStatus.None : cell.Status;
            d.Total = st == HeStatus.None
                ? new HeBlockLine { Text = T("he.total.none", n) }
                : new HeBlockLine { Text = T("he.total", n, StatusText(st)), Argb = Argb(st) };
        }

        // ---------------------------------------------------------------- self test

        /// <summary>A fake game: reading the class of an unidentified, own or allied track is a test failure (the leak test of ADR-0064 item 2).</summary>
        internal sealed class LeakHitFacts : IHitFacts
        {
            public readonly Dictionary<int, bool> Foreign = new Dictionary<int, bool>(), Identified = new Dictionary<int, bool>();
            public readonly Dictionary<int, string> Names = new Dictionary<int, string>();
            public readonly Dictionary<string, HeClass> Classes = new Dictionary<string, HeClass>();
            public readonly Dictionary<string, HeAmmo> Ammo = new Dictionary<string, HeAmmo>();
            public readonly List<HeCandidate> Cands = new List<HeCandidate>();
            public double Bonus = 0.25;
            public int ClassReads, Violations, CandidateReads;
            public bool InMission { get { return true; } }
            public bool IsForeign(int id) { bool v; return Foreign.TryGetValue(id, out v) && v; }
            public bool IsIdentified(int id) { bool v; return Identified.TryGetValue(id, out v) && v; }
            public string ClassIniName(int id)
            {
                ClassReads++;
                if (!IsForeign(id) || !IsIdentified(id)) { Violations++; throw new InvalidOperationException("class of a track that is not an identified foreign one: " + id); }
                string n; return Names.TryGetValue(id, out n) ? n : null;
            }
            public HeClass Class(string iniName) { ClassReads++; HeClass c; return Classes.TryGetValue(iniName, out c) ? c : null; }
            public IList<HeCandidate> Candidates() { CandidateReads++; return Cands; }
            public HeAmmo AmmoByFile(string file) { HeAmmo a; return Ammo.TryGetValue(file, out a) ? a : null; }
            public double DamageBonus(out bool known) { known = true; return Bonus; }
            public string ClassWhy(string iniName) { return "stub"; }
        }

        private static string dn0Reason(IHitFacts f) { return Detail(f, 1, new HitLedger(), new HeSettings(), new HeSelection()).Reason; }

        private static HeAmmo Am(string file, string name, HeAmmoKind k, double power, bool surface, bool sub)
        {
            return new HeAmmo { File = file, Name = name, Kind = k, Power = power, DudPower = power * (k == HeAmmoKind.Missile ? 0.5 : 0.2), Surface = surface, Subsurface = sub, RadiusM = 20 };
        }

        private static HeCandidate Ca(HeAmmo a, string unit, int n) { return new HeCandidate { Ammo = a, Unit = unit, Count = n }; }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            bool text = UiText.Has(UiText.DefaultSection, "he.est_line") && UiText.Has(UiText.DefaultSection, "he.total");

            var f = new LeakHitFacts();
            var petya = new HeClass { IniName = "wp_skr_petya2", Kind = HeTargetKind.Ship, Displacement = 1110, Armor = "none" };
            var kilo = new HeClass { IniName = "wp_ss_kilo", Kind = HeTargetKind.Sub, Displacement = 3040, Armor = "none" };
            var raft = new HeClass { IniName = "raft", Kind = HeTargetKind.None };
            var airbase = new HeClass { IniName = "base", Kind = HeTargetKind.Large };
            var gun = new HeClass { IniName = "gun", Kind = HeTargetKind.Land, LandSubType = "Installation", DamagePoints = 5, Armor = "none" };
            gun.Systems.Add(new HeSystem { Section = "ControlSystem", Integrity = 5, Armor = "none" });
            foreach (var c in new[] { petya, kilo, raft, airbase, gun }) f.Classes[c.IniName] = c;
            f.Foreign[1] = true; f.Identified[1] = true; f.Names[1] = "wp_skr_petya2";
            f.Foreign[2] = true; f.Identified[2] = false; f.Names[2] = "wp_skr_petya2";
            f.Foreign[3] = false; f.Identified[3] = true; f.Names[3] = "wp_skr_petya2";
            f.Foreign[4] = true; f.Identified[4] = true; f.Names[4] = "wp_ss_kilo";
            f.Foreign[5] = true; f.Identified[5] = true; f.Names[5] = "raft";
            f.Foreign[6] = true; f.Identified[6] = true; f.Names[6] = "base";
            f.Foreign[7] = true; f.Identified[7] = true; f.Names[7] = "gun";
            f.Foreign[8] = true; f.Identified[8] = true; f.Names[8] = "unknown_class";
            var harpoon = Am("usn_agm-84d", "Harpoon", HeAmmoKind.Missile, 45, true, false);
            var mk48 = Am("usn_mk48", "Mk 48", HeAmmoKind.Torpedo, 68, true, true);
            var mk46 = Am("usn_mk46", "Mk 46", HeAmmoKind.Torpedo, 27, false, true);
            var mod5 = Am("usn_mk46_mod5", "Mk 46 Mod 5", HeAmmoKind.Torpedo, 27, true, true);   // ASW torpedo with ASuW as the secondary type
            var noShipW = Am("noship", "NoShip", HeAmmoKind.Missile, 40, true, false); noShipW.NoShip = true;
            var neither = Am("neither", "Neither", HeAmmoKind.Missile, 40, false, false);   // no ASuW / ASW type at all
            var sam = Am("sam", "Standard SAM", HeAmmoKind.Missile, 30, false, false);
            var mk82 = Am("usn_mk-82", "Mk 82", HeAmmoKind.Bomb, 28, true, false); mk82.Penetration = "moderate"; mk82.LandInstall = true; mk82.LandMobile = true; mk82.Land = true;
            var clus = Am("cbu", "CBU", HeAmmoKind.Bomb, 40, true, false); clus.Warhead = HeWarhead.Cluster;
            var rkt = Am("hydra", "Hydra", HeAmmoKind.Rocket, 5, true, false);
            var rbu = Am("rbu", "RBU", HeAmmoKind.DepthCharge, 20, false, true);
            foreach (var a in new[] { harpoon, mk48, mk46, mod5, noShipW, neither, sam, mk82, clus, rkt, rbu }) f.Ammo[a.File] = a;

            // leak: an unidentified, an own and an allied track have no "+", no block and not a single read of the class
            chk(!Expandable(f, 2) && !Expandable(f, 3) && !Expandable(f, 99), "expandable only identified foreign");
            chk(Detail(f, 2, new HitLedger(), new HeSettings(), new HeSelection()) == null && Detail(f, 3, new HitLedger(), new HeSettings(), new HeSelection()) == null, "no detail for them");
            chk(Cell(f, 2, new HitLedger()) == null && Cell(f, 3, new HitLedger()) == null, "no cell for them");
            chk(f.ClassReads == 0 && f.Violations == 0 && f.CandidateReads == 0, "no reads of the class / the armament: " + f.ClassReads);
            var ledEmpty = new HitLedger();
            chk(Cell(f, 1, ledEmpty) == null, "no hits = no cell");

            // candidates: all carriers, the domain filter, merging, the default order
            f.Cands.Add(Ca(harpoon, "Perry", 4)); f.Cands.Add(Ca(harpoon, "Perry", 4));   // merged: Perry x8
            f.Cands.Add(Ca(harpoon, "Yorktown", 8));
            f.Cands.Add(Ca(mk48, "Los Angeles", 6));                                        // ship-capable torpedo
            f.Cands.Add(Ca(mk46, "Eisenhower: SH-60", 4));                                  // submarine only
            f.Cands.Add(Ca(sam, "Perry", 40)); f.Cands.Add(Ca(clus, "Eisenhower: A-6E", 12)); f.Cands.Add(Ca(rkt, "Eisenhower: A-6E", 30));
            f.Cands.Add(Ca(mod5, "Perry", 6)); f.Cands.Add(Ca(noShipW, "Perry", 2)); f.Cands.Add(Ca(neither, "Perry", 2)); f.Cands.Add(Ca(rbu, "Perry", 10)); f.Cands.Add(Ca(mk82, "Eisenhower: A-6E", 12));
            var ship = Options(petya, f.Cands, 0.25);
            chk(ship.Count == 4, "ship options, one per weapon type: " + ship.Count);
            chk(ship.Select(o => o.Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "n:Harpoon", "n:Mk 46 Mod 5", "n:Mk 48", "n:Mk 82" }), "ship set: " + string.Join(",", ship.Select(o => o.Key)));
            chk(ship[0].Ammo.File == "usn_mk48" && ship[1].Ammo.File == "usn_mk46_mod5" && ship[2].Ammo.File == "usn_agm-84d" && ship[3].Ammo.File == "usn_mk-82", "order: fewest hits, then the steadier estimate, then the name: " + string.Join(",", ship.Select(o => o.Key)));
            chk(ship.Count(o => o.Ammo.File == "usn_agm-84d" && o.Count == 16) == 1, "merged by weapon across carriers");
            // live 0.28.9: the ship and air files of Mk-46 Mod 5 carry the same name -> one entry
            var cm = new List<HeCandidate>(f.Cands) { Ca(Am("usn_mk46_mod5_air", "Mk 46 Mod 5", HeAmmoKind.Torpedo, 27, true, true), "Eisenhower: SH-3H", 4) };
            var merged = Options(petya, cm, 0.25);
            chk(merged.Count == 4 && merged.Count(o => o.Ammo.Name == "Mk 46 Mod 5" && o.Count == 10) == 1, "same name, other file: one entry, rounds summed");
            chk(ship.Any(o => o.Ammo.File == "usn_mk46_mod5") && ship.All(o => o.Ammo.File != "noship" && o.Ammo.File != "neither"), "Mk 46 Mod 5 (secondary ASuW) is offered against a ship; a weapon with neither type and one that cannot attack vessels are not");
            chk(ship.All(o => o.Ammo.File != "sam" && o.Ammo.File != "cbu" && o.Ammo.File != "hydra" && o.Ammo.File != "rbu" && o.Ammo.File != "usn_mk46"), "filtered: SAM, cluster, rocket, depth charge, sub-only torpedo");
            var sub = Options(kilo, f.Cands, 0.25);
            chk(sub.Any(o => o.Ammo.File == "usn_mk46") && sub.Any(o => o.Ammo.File == "usn_mk46_mod5") && sub.Any(o => o.Ammo.File == "rbu") && sub.All(o => o.Ammo.File != "usn_agm-84d"), "sub domain");
            chk(Options(raft, f.Cands, 0.25).Count == 0 && Options(airbase, f.Cands, 0.25).Count == 0, "no options for raft; a large class without ZonesRead -> none");

            // the choice: carried over to another target, falls back when the ammunition is gone
            var sel = new HeSelection();
            chk(PickIndex(ship, sel) == 0, "default pick");
            int ci = Cycle(ship, sel, +1);
            chk(ci == 1 && sel.Key == ship[1].Key, "cycle forward");
            chk(Cycle(ship, sel, -1) == 0 && Cycle(ship, sel, -1) == 3, "cycle wraps");
            sel.Key = "n:Harpoon";
            var other = Options(new HeClass { IniName = "t", Kind = HeTargetKind.Ship, Displacement = 9600, Armor = "minor" }, f.Cands, 0.25);
            chk(other[PickIndex(other, sel)].Key == "n:Harpoon", "choice carried to another target");
            sel.Key = "n:Mk 48";
            var noTorp = f.Cands.Where(x => x.Ammo.File != "usn_mk48").ToList();
            chk(Options(petya, noTorp, 0.25)[PickIndex(Options(petya, noTorp, 0.25), sel)].Ammo.File == Options(petya, noTorp, 0.25)[0].Ammo.File, "fallback to the default when the ammunition is gone");
            chk(Cycle(new List<HeOption>(), sel, 1) == -1, "cycle of nothing");

            if (text)
            {
                var led = new HitLedger();
                var d0 = Detail(f, 1, led, new HeSettings(), new HeSelection());
                chk(d0 != null && d0.Pick != null && d0.Choices == 4 && d0.Estimate.Count == 1 && d0.Hits.Count == 0 && d0.Total == null && d0.Lines == 2, "ship block without hits: lines " + (d0 == null ? -1 : d0.Lines));
                chk(d0 != null && d0.Pick.Contains("Mk 48") && d0.Pick == "Mk 48" && d0.Estimate[0].Text.Contains("1"), "ship block text: the weapon type only, no caveat");
                var dh = Detail(f, 1, led, new HeSettings(), new HeSelection { Key = "n:Harpoon" });
                chk(dh.Pick == "Harpoon" && dh.Estimate[0].Text.Contains("2") && dh.Estimate[0].Text.Contains("1"), "Harpoon on the Petya: 2 with the boundary (1-2): " + dh.Estimate[0].Text);
                // hits: a Harpoon, a dud, a cluster, a rocket; newest first; totals
                led.Add(new HeHit { TrackId = 1, Clock = "10:00:01", AmmoFile = "usn_agm-84d", Weapon = "Harpoon", Shooter = "Perry", Kind = HeAmmoKind.Missile });
                var cell = Cell(f, 1, led);
                chk(cell != null && cell.Hits == 1 && cell.Status == HeStatus.Heavy && cell.P > 0.9 && cell.P < 1.0, "one Harpoon = heavy, p " + (cell == null ? -1 : cell.P));
                led.Add(new HeHit { TrackId = 1, Clock = "10:00:09", AmmoFile = "usn_agm-84d", Weapon = "Harpoon", Shooter = "Yorktown", Kind = HeAmmoKind.Missile, Dud = true });
                led.Add(new HeHit { TrackId = 1, Clock = "10:00:20", AmmoFile = "cbu", Weapon = "CBU", Shooter = "A-6E", Kind = HeAmmoKind.Bomb, Warhead = HeWarhead.Cluster });
                led.Add(new HeHit { TrackId = 1, Clock = "10:00:30", AmmoFile = "hydra", Weapon = "Hydra", Shooter = "A-6E", Kind = HeAmmoKind.Rocket });
                var c4 = Cell(f, 1, led);
                chk(c4.Hits == 4 && c4.Status == HeStatus.Dead && c4.P > 1.0, "cluster and rocket count as hits but not in p");
                var d4 = Detail(f, 1, led, new HeSettings(), new HeSelection());
                chk(d4.Hits.Count == 4 && d4.Hits[0].Text.Contains("10:00:30") && d4.Hits[0].Text.Contains("—") && d4.Hits[3].Text.Contains("%"), "hit lines: newest first, rocket without a share");
                chk(d4.Hits[1].Text.Contains("—") && d4.Hits[2].Text.Contains(T("he.dud").Trim(',', ' ')), "cluster share is a dash, dud marked");
                chk(d4.Total != null && d4.Total.Argb == Argb(HeStatus.Dead) && d4.Total.Text.Contains("4") && d4.Lines == 1 + 1 + 4 + 1, "total line, coloured");
                var dnc = Detail(f, 1, led, new HeSettings { Counter = false }, new HeSelection());
                chk(dnc.Hits.Count == 0 && dnc.Total == null, "Counter=false: no list");
                // more than 20
                var big = new HitLedger();
                for (int i = 0; i < 25; i++) big.Add(new HeHit { TrackId = 1, Clock = "t" + i, AmmoFile = "usn_agm-84d", Weapon = "Harpoon", Shooter = "P", Kind = HeAmmoKind.Missile });
                var db = Detail(f, 1, big, new HeSettings(), new HeSelection());
                chk(db.Hits.Count == 21 && db.Hits[0].Text.Contains("t24") && db.Hits[20].Text.Contains("5"), "more than 20: the newest 20 and 'more n'");
                // no class: hits are there, no status
                f.Foreign[8] = true;
                var led8 = new HitLedger(); led8.Add(new HeHit { TrackId = 8, Clock = "t", AmmoFile = "usn_agm-84d", Weapon = "Harpoon", Shooter = "P", Kind = HeAmmoKind.Missile });
                var c8 = Cell(f, 8, led8);
                chk(c8 != null && c8.Hits == 1 && c8.Status == HeStatus.None && double.IsNaN(c8.P), "unknown class: hits without a status");
                var d8 = Detail(f, 8, led8, new HeSettings(), new HeSelection());
                chk(d8.Pick == null && d8.Estimate.Count == 1 && d8.Estimate[0].Text == T("he.no_class") && d8.Total != null && d8.Total.Argb == 0, "unknown class: a reason, no estimate");
                // sub, raft, large, land
                var dk = Detail(f, 4, new HitLedger(), new HeSettings(), new HeSelection());
                chk(dk.Pick != null && dk.Choices >= 2, "sub block");
                chk(Detail(f, 5, new HitLedger(), new HeSettings(), new HeSelection()).Estimate[0].Text == T("he.no_est"), "raft: no estimate");
                var dl = Detail(f, 6, new HitLedger(), new HeSettings(), new HeSelection());
                chk(dl.Pick == null && dl.Estimate.Count == 1 && dl.Estimate[0].Text == T("he.large"), "large object: no estimate");
                var dg = Detail(f, 7, new HitLedger(), new HeSettings(), new HeSelection());
                chk(dg.Pick != null && dg.Estimate.Count == 2 && dg.Estimate[0].Text.Contains(T("he.kill_one")) && dg.Estimate[1].Text.Contains("20"), "land block: " + (dg.Estimate.Count > 1 ? dg.Estimate[1].Text : ""));
                chk(Detail(f, 7, new HitLedger(), new HeSettings { Land = false }, new HeSelection()).Estimate.Count == 1, "Land=false");
                // a reason instead of an empty block
                var dnc2 = Detail(f, 8, new HitLedger(), new HeSettings(), new HeSelection());
                chk(dnc2.Lines == 1 && dnc2.Estimate[0].Text == T("he.no_class") && dnc2.Reason == "no_class" && dnc2.ClassName == "unknown_class", "no class data: one reason line");
                chk(dn0Reason(f) == "ok" && dk.Reason == "ok" && d0.Candidates == 13 && d0.Options == 4, "diagnostic fields");
                var noWeapons = new LeakHitFacts(); noWeapons.Foreign[1] = true; noWeapons.Identified[1] = true; noWeapons.Names[1] = "p"; noWeapons.Classes["p"] = petya;
                var dn = Detail(noWeapons, 1, new HitLedger(), new HeSettings(), new HeSelection());
                chk(dn.Pick == null && dn.Estimate.Count == 1 && dn.Estimate[0].Text == T("he.no_weapon"), "no weapons");
                chk(OptionsFor(f, 1, new HeSettings()).Count == 4 && OptionsFor(f, 2, new HeSettings()).Count == 0, "OptionsFor");
            }
            chk(Argb(HeStatus.Light) == 0xFFFFD000 && Argb(HeStatus.Medium) == 0xFFFF8000 && Argb(HeStatus.Heavy) == 0xFFFF2020 && Argb(HeStatus.Dead) == 0xFF901010 && Argb(HeStatus.None) == 0, "colours");
            // ---- T-170: large objects (ADR-0065 / ADR-0070)
            if (text)
            {
                List<HeGeoWeapon> gws;
                HeClass inst = null;
                foreach (var row in HitEstimateGeo.GeoRef) if (row.StartsWith("is_airbase_keflavik|", StringComparison.Ordinal)) inst = HitEstimateGeo.ParseRow(row, out gws);
                chk(inst != null, "GeoRef row of Keflavik");
                if (inst != null)
                {
                    inst.IniName = "kef"; inst.Kind = HeTargetKind.Large;
                    var mk84 = Am("usn_mk-84", "Mk 84", HeAmmoKind.Bomb, 62, true, false); mk84.LandInstall = true; mk84.Penetration = "heavy"; mk84.RadiusM = 60;
                    var mk82b = Am("usn_mk-82", "Mk 82", HeAmmoKind.Bomb, 28, true, false); mk82b.LandInstall = true; mk82b.RadiusM = 20;
                    var cbu2 = Am("cbu2", "CBU-2", HeAmmoKind.Bomb, 40, true, false); cbu2.Warhead = HeWarhead.Cluster; cbu2.LandInstall = true; cbu2.RadiusM = 60;
                    var immune = new HeClass { IniName = "port", Kind = HeTargetKind.Large, LandSubType = "Port", DamagePoints = 300, ZonesRead = true };
                    immune.Systems.Add(new HeSystem { Section = "HitZone1", Integrity = 60, Colliders = 0 });
                    var g = new LeakHitFacts();
                    foreach (var c in new[] { petya, inst, immune }) g.Classes[c.IniName] = c;
                    foreach (var a in new[] { harpoon, mk84, mk82b, cbu2, sam }) g.Ammo[a.File] = a;
                    g.Foreign[1] = true; g.Identified[1] = true; g.Names[1] = "wp_skr_petya2";
                    g.Foreign[9] = true; g.Identified[9] = true; g.Names[9] = "kef";
                    g.Foreign[10] = true; g.Identified[10] = false; g.Names[10] = "kef";
                    g.Foreign[11] = true; g.Identified[11] = true; g.Names[11] = "port";
                    g.Cands.Add(Ca(mk84, "A-6E", 4)); g.Cands.Add(Ca(mk82b, "A-6E", 12)); g.Cands.Add(Ca(cbu2, "A-6E", 6)); g.Cands.Add(Ca(harpoon, "Perry", 8)); g.Cands.Add(Ca(sam, "Perry", 40));
                    var lo = Options(inst, g.Cands, 0.25);
                    chk(lo.Count == 3 && lo.All(o => o.Ammo.File != "usn_agm-84d" && o.Ammo.File != "sam"), "large options: bombs and cluster pass, Harpoon (no LandInstall) and SAM do not: " + lo.Count);
                    chk(lo.TakeWhile(o => o.Ref >= 0).Count() + lo.Count(o => o.Ref < 0) == lo.Count && lo.Zip(lo.Skip(1), (x, y) => y.Ref < 0 || (x.Ref >= 0 && x.Ref <= y.Ref)).All(v => v), "order by the reference, none last");
                    chk(lo[0].Ref == lo.Where(o => o.Ref >= 0).Min(o => o.Ref), "the smallest reference first");
                    var dk = Detail(g, 9, new HitLedger(), new HeSettings(), new HeSelection());
                    chk(dk != null && dk.Pick != null && dk.Choices == 3 && dk.Reason == "ok" && dk.Estimate[0].Text == T("he.inst_head", 600, 8) && dk.Options == 3 && dk.Candidates == 5, "large block: " + (dk == null ? "null" : dk.Reason + "/" + dk.Estimate[0].Text));
                    chk(dk.Lines == 1 + dk.Estimate.Count, "block height counts the pick control");
                    chk(Detail(g, 9, new HitLedger(), new HeSettings { Installations = false }, new HeSelection()).Estimate[0].Text == T("he.large") && Detail(g, 9, new HitLedger(), new HeSettings { Installations = false }, new HeSelection()).Reason == "large", "Installations=false: the stub");
                    chk(OptionsFor(g, 9, new HeSettings { Installations = false }).Count == 0 && OptionsFor(g, 9, new HeSettings()).Count == 3, "OptionsFor large");
                    var dno = Detail(g, 11, new HitLedger(), new HeSettings(), new HeSelection());
                    chk(dno.Reason == "immune" && dno.Pick == null && dno.Estimate.Count == 1, "immune object");
                    // the choice goes from a ship to the object and back by the weapon name
                    var selc = new HeSelection { Key = "n:Mk 82" };
                    var onShip = OptionsFor(g, 1, new HeSettings());
                    chk(onShip.Count > 0 && onShip[PickIndex(onShip, selc)].Key == "n:Mk 82", "choice on the ship");
                    var onObj = Detail(g, 9, new HitLedger(), new HeSettings(), selc);
                    chk(onObj.Pick == "Mk 82", "choice carried to the object: " + onObj.Pick);
                    // the leak test: an unidentified track reads nothing
                    int reads = g.ClassReads;
                    chk(Detail(g, 10, new HitLedger(), new HeSettings(), new HeSelection()) == null && !Expandable(g, 10) && g.ClassReads == reads && g.Violations == 0, "unidentified large: not a single read");
                    // hits on an object: lines without a share, a total without a status, no status in the cell
                    var ledo = new HitLedger();
                    ledo.Add(new HeHit { TrackId = 9, Clock = "10:00:01", AmmoFile = "usn_mk-84", Weapon = "Mk 84", Shooter = "A-6E", Kind = HeAmmoKind.Bomb });
                    ledo.Add(new HeHit { TrackId = 9, Clock = "10:00:09", AmmoFile = "usn_mk-84", Weapon = "Mk 84", Shooter = "A-6E", Kind = HeAmmoKind.Bomb });
                    var dh = Detail(g, 9, ledo, new HeSettings(), new HeSelection());
                    chk(dh.Hits.Count == 2 && dh.Hits[0].Text.EndsWith("—") && dh.Total != null && dh.Total.Text == T("he.total.none", 2) && dh.Total.Argb == 0, "object hits: no share, no status");
                    var cello = Cell(g, 9, ledo);
                    chk(cello != null && cello.Hits == 2 && cello.Status == HeStatus.None && double.IsNaN(cello.P), "object cell: hits without a status");
                    chk(dh.Reason == "ok", "reason ok");
                }
            }
            // land helpers
            int n;
            var sa3 = new HeClass { IniName = "sa3", Kind = HeTargetKind.Land, LandSubType = "SAM", DamagePoints = 40 };
            sa3.Systems.Add(new HeSystem { Section = "ControlSystem", Integrity = 400 });
            sa3.Systems.Add(new HeSystem { Section = "Radar1", Integrity = 6, Radar = true }); sa3.Systems.Add(new HeSystem { Section = "Radar2", Integrity = 6, Radar = true });
            sa3.Systems.Add(new HeSystem { Section = "Optics", Integrity = 0 }); sa3.Systems.Add(new HeSystem { Section = "W1", Integrity = 10 });
            chk(LandKill(sa3, out n) == "radars" && n == 2, "SA-3: all radars (2)");
            chk(LandKill(gun, out n) == "one" && n == 1, "one system");
            var many = new HeClass { Kind = HeTargetKind.Land, LandSubType = "MobileUnit", DamagePoints = 20 };
            many.Systems.Add(new HeSystem { Integrity = 5 }); many.Systems.Add(new HeSystem { Integrity = 8 });
            chk(LandKill(many, out n) == "dp" && n == 20, "systems up to DP");
            chk(LandKill(new HeClass { Kind = HeTargetKind.Land }, out n) == null, "no systems");
            return err;
        }
    }
}
