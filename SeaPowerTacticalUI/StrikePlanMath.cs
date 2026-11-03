using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    internal sealed class StrikePlanInput
    {
        public IList<StrikeAircraft> Air;
        public IList<StrikeShip> Ships;
        public StrikeTargetSet Target;
        public GeoPt From;
        public double Now;
        public StrikeParams P = new StrikeParams();
        public Func<StrikeAircraft, StrikeMissile, StrikeShip, double, double> TofS;   // (aircraft, missile, target, launch range nm) -> time of flight, s; null or <= 0 = the rough formula
        public Func<GeoPt, bool> IsLand;   // phase B: null = everything is sea
        public string ClearingNote;        // out: none | shown | hidden (the log of the plan)
        public double Difficulty = 1;      // phase E1: 1 - Globals._missileInterceptChanceReduction (1 = not readable)
        public string Goal = "breach";   // T-342: breach (v1) | sink; the default keeps v1 for every old caller
        public double DamageBonus = 0.25; public bool BonusKnown;
        public double KillThreshold = 0.8;
        public bool NoClearPause;          // T-342 C: the queued replans of the clearing itself do not build a new clear_pause
        public double TMin = -1;           // phase D: the plan is not earlier than this time (the player asked to shift T for a late jammer); -1 = none
    }

    // T-341 / ADR-0132 sections 3-4 (design 5.3-5.5): the priority of the targets, the assignment of the missiles, and the variants of the strike. Pure.
    internal static class StrikePlanMath
    {
        private const double DefaultCruiseKn = 400, GateBackNm = 15;

        /// <summary>The stand sets it to test the clearing structure under the phase A rule (gain 1); the game never does.</summary>
        internal static double? ClearGainTest;
        private static double ClearGainNow { get { return ClearGainTest ?? StrikeCalib.ClearGain; } }

        /// <summary>null = a target; else civilian | neutral | friendly | not_hostile | not_identified | the given reason.</summary>
        public static string Exclusion(StrikeShip s)
        {
            if (s == null) return "not_hostile";
            if (s.Excluded) return s.Why ?? "not_hostile";
            if (s.Civilian || string.Equals(s.Role, "Merchant", StringComparison.OrdinalIgnoreCase) || string.Equals(s.Role, "SmallCivilian", StringComparison.OrdinalIgnoreCase)) return "civilian";
            string rej = AttackHostility.StrikeOk(s.Side ?? (s.Hostile ? "hostile" : "unknown"), null, null, "hostile");
            if (rej != null) return rej;
            if (!s.Identified) return "not_identified";
            return null;
        }

        internal static bool LowPriorityRole(string role)
        {
            return string.Equals(role, "Transport", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Landing", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "RAS", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The valid targets by the priority: AA strength (K) down; among ships within tonBandFrac of the leader of the band, tonnage down; transports / landing / supply last. Excluded ships are not in the list.</summary>
        public static List<StrikeShip> Prioritize(IList<StrikeShip> ships, double tonBandFrac)
        {
            var res = new List<StrikeShip>();
            if (ships == null) return res;
            var ok = ships.Where(s => s != null && Exclusion(s) == null).ToList();
            foreach (int tier in new[] { 0, 1 })
            {
                var part = ok.Where(s => (LowPriorityRole(s.Role) ? 1 : 0) == tier).OrderByDescending(s => s.Ad.Ksub).ToList();
                int i = 0;
                while (i < part.Count)
                {
                    double top = part[i].Ad.Ksub;
                    int j = i;
                    while (j < part.Count && part[j].Ad.Ksub >= top * (1 - tonBandFrac) - 1e-12) j++;
                    res.AddRange(part.Skip(i).Take(j - i).OrderByDescending(s => s.DisplacementT));
                    i = j;
                }
            }
            return res;
        }

        /// <summary>The best (lowest) score first: ranking key of the variant (see Score).</summary>
        public static int Compare(StrikeVariant a, StrikeVariant b) { return a.Score.CompareTo(b.Score); }

        private sealed class Ctx
        {
            public StrikePlanInput In;
            public StrikeParams P;
            public List<StrikeAircraft> Air = new List<StrikeAircraft>();
            public List<StrikeShip> Defenders = new List<StrikeShip>();   // valid targets with AA, at the lead position
            public Dictionary<int, StrikeShip> Lead = new Dictionary<int, StrikeShip>();   // valid ships at the lead position
            public double Dt0;
            public StrikeMissile Primary;
            public List<string> Warn = new List<string>();
            public bool Sink; public double Th, Bonus;   // T-342: the goal, the threshold of the goal, the damage bonus
            public List<StrikeShip> Hostiles = new List<StrikeShip>();   // every known hostile ship that is not civilian (the descent point looks at all of them)
        }

        private static double Tof(Ctx c, StrikeAircraft a, StrikeMissile m, StrikeShip s, double rNm)
        {
            double t = c.In.TofS == null ? 0 : c.In.TofS(a, m, s, rNm);
            return t > 0 ? t : StrikeTotMath.RoughTofS(rNm, m.SpeedKn > 0 ? m.SpeedKn : 540);
        }

        /// <summary>The launch range: as near as the zone of the target allows (outside the SAM + standoff), inside the window of the missile.</summary>
        private static double LaunchRange(Ctx c, StrikeMissile m, StrikeShip s)
        {
            double r = Math.Min(0.9 * m.MaxNm, Math.Max(s.SamMaxNm + c.P.StandoffNm, m.MinNm * 1.5 + 5));
            if (r <= m.MinNm) r = m.MinNm + (m.MaxNm - m.MinNm) * 0.5;
            return r;
        }

        private static GeoPt Pos(StrikeShip s) { return new GeoPt(s.Lat, s.Lon); }

        private static bool Supersonic(StrikeMissile m) { return m.SpeedKn >= StrikeCalib.SupersonicKn; }

        private static double KOf(StrikeShip s, bool sup) { return sup ? s.Ad.Ksup : s.Ad.Ksub; }
        private static double SOf(StrikeShip s, bool sup) { return sup ? s.Ad.Ssup : s.Ad.Ssub; }

        /// <summary>The zones on the track to the target: the neighbours in the order of passing (chord share, eta, shared factor, clearing), the target last.</summary>
        private static List<StrikeZone> Zones(Ctx c, StrikeShip target, StrikeMissile m, double axis, ISet<int> shared, IDictionary<int, double> cleared)
        {
            bool sup = Supersonic(m);
            var tp = Pos(target);
            double r = LaunchRange(c, m, target);
            var launch = StrikeGeoMath.LaunchPt(tp, axis, r);
            var near = new List<(double d, StrikeZone z)>();
            foreach (var n in c.Defenders)
            {
                if (n.TrackId == target.TrackId) continue;
                double k = KOf(n, sup), s = SOf(n, sup);
                if (k <= 0 || s <= 0) continue;
                double rad = n.SamMaxNm;
                if (StrikeAirMath.IsSeaSkim(m)) rad = Math.Min(rad, StrikeGeoMath.HorizonNm(n.RadarFt > 0 ? n.RadarFt : 100, m.CruiseAltFt));
                if (rad <= 0) continue;
                double chord = StrikeGeoMath.ChordNm(launch, tp, Pos(n), rad);
                double w = Math.Min(1.0, chord / (2 * rad));
                if (w <= 0) continue;
                double cc = AswMath.DistNm(Pos(n), tp) <= StrikeCalib.EtaNm ? StrikeCalib.Eta : 1.0;
                if (shared != null && shared.Contains(n.TrackId)) cc *= StrikeCalib.SharedFactor;
                double fz = StrikeAdMath.MissileFactor(n.Sams, m, sup);
                var z = StrikeZone.Near(k * fz, s, w, cc); z.F = fz;
                z.TrackId = n.TrackId; z.ClassIni = n.ClassIni; z.Scale = StrikeAdMath.Scale(n.ClassIni, c.In.Difficulty);
                double pj;
                if (cleared != null && cleared.TryGetValue(n.TrackId, out pj))
                {
                    bool skip;
                    z = StrikeAdMath.Cleared(z, pj, c.Th, ClearGainNow, out skip);
                }
                near.Add((AswMath.DistNm(launch, Pos(n)), z));
            }
            var zones = near.OrderBy(x => x.d).Select(x => x.z).ToList();
            double ft = StrikeAdMath.MissileFactor(target.Sams, m, sup);
            var tz = StrikeZone.Target(KOf(target, sup) * ft, SOf(target, sup)); tz.F = ft;
            tz.TrackId = target.TrackId; tz.ClassIni = target.ClassIni; tz.Scale = StrikeAdMath.Scale(target.ClassIni, c.In.Difficulty);
            zones.Add(tz);
            return zones;
        }

        private sealed class Pool
        {
            private readonly Dictionary<string, int> _rem = new Dictionary<string, int>();
            private static string Key(StrikeAircraft a, StrikeMissile m) { return a.Uid.ToString(CultureInfo.InvariantCulture) + "|" + m.Ammo; }
            public Pool(IEnumerable<StrikeAircraft> air)
            {
                foreach (var a in air) foreach (var m in a.Missiles) if (StrikeAirMath.IsStrikeMissile(m) && m.Operable && m.Count > 0) _rem[Key(a, m)] = (_rem.ContainsKey(Key(a, m)) ? _rem[Key(a, m)] : 0) + m.Count;
            }
            public int Rem(StrikeAircraft a, StrikeMissile m) { int n; return _rem.TryGetValue(Key(a, m), out n) ? n : 0; }
            public void Take(StrikeAircraft a, StrikeMissile m, int n) { _rem[Key(a, m)] = Rem(a, m) - n; }
            public int Total() { return _rem.Values.Sum(); }
            public Pool Clone() { var p = new Pool(new StrikeAircraft[0]); foreach (var kv in _rem) p._rem[kv.Key] = kv.Value; return p; }
            public void CopyFrom(Pool o) { _rem.Clear(); foreach (var kv in o._rem) _rem[kv.Key] = kv.Value; }
        }

        private sealed class Piece { public StrikeAircraft A; public StrikeMissile M; public int N; public StrikeAssignment As; }

        /// <summary>Greedy: the aircraft already used for this target first (fewer carriers), then the one with the most missiles. Returns what was assigned (salvo up to 8 per aircraft and ammo).</summary>
        private static List<Piece> Alloc(Ctx c, Pool pool, StrikeShip ship, int need, double axis, StrikeVariant v, bool early)
        {
            var got = new List<Piece>();
            var used = new HashSet<int>();
            var tp = Pos(ship);
            int left = need;
            while (left > 0)
            {
                StrikeAircraft ba = null; StrikeMissile bm = null; int brem = 0; bool bused = false;
                foreach (var a in c.Air)
                    foreach (var m in a.Missiles)
                    {
                        if (!StrikeAirMath.IsStrikeMissile(m) || !m.Operable) continue;
                        int rem = pool.Rem(a, m);
                        if (rem <= 0) continue;
                        var ex = got.FirstOrDefault(p => p.A == a && p.M.Ammo == m.Ammo);
                        if (ex != null && ex.N >= 8) continue;
                        bool u = used.Contains(a.Uid);
                        if (ba == null || (u && !bused) || (u == bused && rem > brem)) { ba = a; bm = m; brem = rem; bused = u; }
                    }
                if (ba == null) break;
                var piece = got.FirstOrDefault(p => p.A == ba && p.M.Ammo == bm.Ammo);
                int room = 8 - (piece == null ? 0 : piece.N);
                int take = Math.Min(Math.Min(brem, left), room);
                if (piece == null)
                {
                    double r = LaunchRange(c, bm, ship);
                    var launch = StrikeGeoMath.LaunchPt(tp, axis, r);
                    if (c.In.IsLand != null)
                    {
                        bool moved, still;
                        launch = StrikeWaitMath.LaunchOffLand(launch, tp, bm.MinNm, c.In.IsLand, c.P.LandStepNm, out moved, out still);
                        if (moved) r = AswMath.DistNm(launch, tp);
                        if (still) v.Warnings.Add("launch_land:" + ba.Uid.ToString(CultureInfo.InvariantCulture));
                    }
                    double kn = ba.CruiseKn > 0 ? ba.CruiseKn : DefaultCruiseKn;
                    string gwhy;
                    var gate = StrikeGeoMath.GatePt(launch, axis, c.In.IsLand, c.P, out gwhy);
                    if (gwhy == "no_gate") v.Warnings.Add("gate:" + ba.Uid.ToString(CultureInfo.InvariantCulture));
                    var apos = new GeoPt(ba.Lat, ba.Lon);
                    bool atStart;
                    var descent = StrikeGeoMath.DescentPt(apos, gate, launch, c.Hostiles, ba.CruiseAltFt > 0 ? ba.CruiseAltFt : 25000, c.P, out atStart);
                    double legNm = AswMath.DistNm(gate, launch);
                    var asg = new StrikeAssignment
                    {
                        Uid = ba.Uid, TargetId = ship.TrackId, Ammo = bm.Ammo, LaunchPt = launch, LaunchNm = r,
                        LaunchKn = kn, GatePt = gate, GateWhy = gwhy, DescentPt = descent, DescentAtStart = atStart, CruiseAltFt = ba.CruiseAltFt,
                        LaunchAltFt = bm.MinLaunchAltFt > 0 ? bm.MinLaunchAltFt : ba.CruiseAltFt,
                        ReadyS = (AswMath.DistNm(apos, gate) + legNm) / kn * 3600.0,
                        TofS = Tof(c, ba, bm, ship, r),
                    };
                    piece = new Piece { A = ba, M = bm, As = asg };
                    got.Add(piece);
                }
                piece.N += take; piece.As.Salvo = piece.N;
                pool.Take(ba, bm, take); used.Add(ba.Uid); left -= take;
            }
            return got;
        }

        private static StrikeMissile Rep(Ctx c, List<Piece> got)
        {
            if (got.Count == 0) return c.Primary;
            return got.OrderByDescending(p => p.N).First().M;
        }

        private static string F2(double d) { return d.ToString("0.00", CultureInfo.InvariantCulture); }

        private sealed class Spec { public StrikeShip Ship; public bool Early; }

        /// <summary>The kill model of the pieces allocated to one ship: one missile type per entry, sigma of the leak of one missile of the whole salvo of n.</summary>
        private static StrikeKillNeed EvalPieces(Ctx c, StrikeShip ship, List<Piece> got, double axis, ISet<int> shared, IDictionary<int, double> cleared, out double sigma)
        {
            int n = got.Sum(p => p.N);
            var shots = new List<StrikeKillShot>();
            sigma = 0;
            foreach (var g in got.GroupBy(p => p.M.Ammo))
            {
                var m = g.First().M;
                double sg = StrikeAdMath.PathSigma(n, Zones(c, ship, m, axis, shared, cleared));
                if (sigma == 0) sigma = sg;
                shots.Add(new StrikeKillShot { Loss = StrikeKillMath.HitLoss(m, c.Bonus), Sigma = sg, Count = g.Sum(p => p.N) });
            }
            return StrikeKillMath.Eval(ship, shots, c.Bonus, c.Th);
        }

        /// <summary>Fills the probability fields of one target plan from the pieces allocated to it (breach: v1; sink: the kill model). Returns the warnings of the target.</summary>
        private static void FillTarget(Ctx c, StrikeVariant v, StrikeShip ship, StrikeTargetPlan tp, List<Piece> got, double axis, ISet<int> shared, IDictionary<int, double> cleared, StrikeKillNeed need)
        {
            int n = got.Sum(p => p.N);
            var rep = Rep(c, got);
            var zonesRep = Zones(c, ship, rep, axis, shared, cleared);
            tp.N = n; tp.Ammo = rep != null ? rep.Ammo : null; tp.Sup = rep != null && Supersonic(rep); tp.Zones = zonesRep;
            double pBreach = n > 0 ? StrikeAdMath.PathLeak(n, zonesRep) : 0;
            tp.PBreach = pBreach;
            if (!c.Sink) { tp.P = pBreach; tp.Goal = "breach"; return; }
            tp.Goal = "sink";
            double sigma0;
            var ev = n > 0 ? EvalPieces(c, ship, got, axis, shared, cleared, out sigma0) : new StrikeKillNeed { Short = true, Budget = ship.KillBudget };
            double sigma = n > 0 ? StrikeAdMath.PathSigma(n, zonesRep) : 0;
            bool known = ship.KillBudget >= 0;
            tp.Budget = ship.KillBudget; tp.BudgetLeft = known ? StrikeKillMath.BudgetLeft(ship.KillBudget, ship.PriorLoss, StrikeCalib.PriorHitWeight) : -1;
            tp.Sigma = sigma;
            tp.PBreach = n > 0 && ev.PBreach > 0 ? ev.PBreach : pBreach;
            if (need != null) { tp.H = need.H; tp.H1 = need.H1; tp.MSink = need.M; tp.LossPerHit = need.Loss; }
            if (!known) { tp.PSunk = -1; tp.P = 0; tp.Short = true; tp.ShortBy = 0; return; }
            tp.PSunk = n > 0 ? ev.P : 0; tp.P = tp.PSunk;
            if (need != null && need.H1 > 0 && n > 0) { tp.H = ev.H > 0 ? ev.H : tp.H; tp.H1 = ev.H1 > 0 ? ev.H1 : tp.H1; if (tp.LossPerHit <= 0) tp.LossPerHit = ev.Loss; }
            tp.Short = tp.PSunk < c.Th;
            if (tp.Short) tp.ShortBy = need != null && need.M > 0 ? Math.Max(1, need.M - n) : Math.Max(1, StrikeCalib.KillMaxM - n);
        }

        /// <summary>One variant: the ordered targets (cleared neighbours first, they arrive earlier), the ids hit in the same volley, the clearing neighbours.
        /// Goal sink (T-342): the number of missiles for P(sunk) >= KillThreshold; kind damage_only = every missile of the airborne aircraft on the main target (red).</summary>
        private static StrikeVariant Make(Ctx c, string kind, double axis, IList<StrikeShip> order, ISet<int> shared, IList<StrikeShip> clearing)
        {
            var pool = new Pool(c.Air);
            bool all = kind == "damage_only";
            var v = new StrikeVariant { Kind = kind, AxisDeg = axis, Goal = c.Sink ? "sink" : "breach", Red = all };
            var cleared = new Dictionary<int, double>();
            var allPieces = new List<Piece>();
            var early = new HashSet<int>();
            var plans = new Dictionary<int, StrikeTargetPlan>();
            var specs = new List<Spec>();
            var pieceOf = new Dictionary<int, List<Piece>>();
            var sharedOf = new Dictionary<int, ISet<int>>();
            var clearedOf = new Dictionary<int, IDictionary<int, double>>();
            if (clearing != null) foreach (var n in clearing) specs.Add(new Spec { Ship = n, Early = true });
            foreach (var s in order) specs.Add(new Spec { Ship = s });
            foreach (var sp in specs)
            {
                var ship = sp.Ship;
                var shr = sp.Early ? null : shared;
                var clr = sp.Early ? null : cleared;
                var zones = Zones(c, ship, c.Primary, axis, shr, clr);
                int need; StrikeKillNeed kn = null;
                if (!c.Sink) need = StrikeAdMath.PathNeed(zones, c.P.PkThreshold);
                else
                {
                    kn = StrikeKillMath.Need(ship, c.Primary, m => StrikeAdMath.PathSigma(m, zones), c.Bonus, c.Th);
                    need = all || kn.M < 0 ? pool.Total() : kn.M;
                }
                var snap = pool.Clone(); int w0 = v.Warnings.Count;
                var got = Alloc(c, pool, ship, need, axis, v, sp.Early);
                if (c.Sink && !all && kn.M > 0)
                {
                    // a mixed allocation may be weaker than the primary type: one more missile at a time while the threshold is missed and missiles remain
                    for (int extra = 0; extra < 8 && pool.Total() > 0 && got.Sum(p => p.N) >= need; extra++)
                    {
                        double sg0;
                        if (EvalPieces(c, ship, got, axis, shr, clr, out sg0).P >= c.Th) break;
                        pool.CopyFrom(snap); v.Warnings.RemoveRange(w0, v.Warnings.Count - w0);
                        need++;
                        got = Alloc(c, pool, ship, need, axis, v, sp.Early);
                    }
                }
                var tp = new StrikeTargetPlan { TrackId = ship.TrackId, ClassName = ship.ClassName, ClassIni = ship.ClassIni };
                FillTarget(c, v, ship, tp, got, axis, shr, clr, kn);
                if (sp.Early) { cleared[ship.TrackId] = c.Sink && tp.PSunk >= 0 ? tp.PSunk : tp.PBreach; early.Add(ship.TrackId); }
                allPieces.AddRange(got);
                pieceOf[ship.TrackId] = got; sharedOf[ship.TrackId] = shr; clearedOf[ship.TrackId] = clr == null ? null : new Dictionary<int, double>(clr);
                if (!c.Sink) { if (need > tp.N) v.Warnings.Add("short n=" + (need - tp.N).ToString(CultureInfo.InvariantCulture) + " target=" + ship.TrackId.ToString(CultureInfo.InvariantCulture)); }
                else if (ship.KillBudget < 0) v.Warnings.Add("kill_unknown target=" + ship.TrackId.ToString(CultureInfo.InvariantCulture));
                else if (tp.Short) v.Warnings.Add("kill_short n=" + tp.ShortBy.ToString(CultureInfo.InvariantCulture) + " target=" + ship.TrackId.ToString(CultureInfo.InvariantCulture));
                plans[ship.TrackId] = tp;
                v.PerTarget.Add(tp);
            }
            // times; a late second row of one aircraft is shifted (within MixShiftMaxS and the fuel) or taken out, then the times are computed once more
            double now = c.In.Now, margin = c.P.ArriveMarginS;
            double tNormal = 0, tEarly = 0;
            var arrivals = new Dictionary<int, List<double>>();
            var late = new List<string>();
            for (int pass = 0; pass < 4; pass++)
            {
                v.Assignments.Clear(); arrivals.Clear();
                var normal = allPieces.Where(p => !early.Contains(p.As.TargetId)).Select(p => p.As.ReadyS + p.As.TofS).ToList();
                var earlyList = allPieces.Where(p => early.Contains(p.As.TargetId)).Select(p => p.As.ReadyS + p.As.TofS).ToList();
                tNormal = StrikeTotMath.ArriveT(normal, now, margin, 0);
                if (c.In.TMin > 0) tNormal = Math.Max(tNormal, c.In.TMin);
                if (earlyList.Count > 0) tNormal = Math.Max(tNormal, StrikeTotMath.ArriveT(earlyList, now, margin, 0) + StrikeCalib.ClearDeltaS);
                tEarly = tNormal - StrikeCalib.ClearDeltaS;
                int wTimes = v.Warnings.Count;
                foreach (var p in allPieces)
                {
                    var a = p.As;
                    a.TArrive = early.Contains(a.TargetId) ? tEarly : tNormal;
                    a.TLaunch = StrikeTotMath.TLaunch(a.TArrive, a.TofS);
                    a.TIssue = StrikeTotMath.TIssue(a.TLaunch, StrikeTotMath.TurnLeadS(0, c.P));
                    double kn = a.LaunchKn > 0 ? a.LaunchKn : DefaultCruiseKn;
                    a.TGo = StrikeTotMath.TGo(a.TLaunch, AswMath.DistNm(a.GatePt, a.LaunchPt), kn);
                    double inNm, inS; bool under;
                    StrikeGeoMath.SamRisk(a.LaunchPt, axis, a.LaunchAltFt, kn, c.Defenders, out inNm, out inS, out under);
                    a.InSamNm = inNm; a.InSamS = inS; a.UnderHorizon = under;
                    if (inNm > 0.05) v.Warnings.Add("sam_risk uid=" + a.Uid.ToString(CultureInfo.InvariantCulture) + " nm=" + F2(inNm) + (under ? " under_horizon" : ""));
                    if (p.M.NeedsIllum) v.Warnings.Add("needs_illum uid=" + a.Uid.ToString(CultureInfo.InvariantCulture));
                    v.Assignments.Add(a);
                    // fuel
                    if (p.A.EnduranceS > 0)
                    {
                        double remain, needS;
                        double runNm = AswMath.DistNm(new GeoPt(p.A.Lat, p.A.Lon), a.LaunchPt);
                        if (!AswStrikeMath.StrikeFuelOk(p.A.FuelFrac, p.A.EnduranceS, 0, runNm, kn, out remain, out needS)) v.Warnings.Add("fuel uid=" + a.Uid.ToString(CultureInfo.InvariantCulture));
                        v.FuelMinFrac = Math.Min(v.FuelMinFrac, Math.Max(0, (remain - needS) / p.A.EnduranceS));
                    }
                }
                // the assignments of one aircraft: the farther launch first (Order 0); a second one must be reachable: the leg between the two launch points plus the turn
                var drop = new List<Piece>();
                foreach (var grp in v.Assignments.GroupBy(x => x.Uid).ToList())
                {
                    var ord = grp.OrderByDescending(x => x.LaunchNm).ThenBy(x => x.Ammo, StringComparer.Ordinal).ToList();
                    var air = c.Air.First(x => x.Uid == grp.Key);
                    for (int i = 0; i < ord.Count; i++)
                    {
                        ord[i].Order = i;
                        if (i == 0) continue;
                        double kn = ord[i].LaunchKn > 0 ? ord[i].LaunchKn : DefaultCruiseKn;
                        double needS = AswMath.DistNm(ord[i - 1].LaunchPt, ord[i].LaunchPt) / kn * 3600.0 + StrikeTotMath.TurnLeadS(90, c.P);
                        double deficit = needS - (ord[i].TLaunch - ord[i - 1].TLaunch);
                        if (deficit <= 0) continue;
                        bool fuelOk = true;
                        if (air.EnduranceS > 0)
                        {
                            double remain, needF;
                            double runNm = AswMath.DistNm(new GeoPt(air.Lat, air.Lon), ord[i - 1].LaunchPt) + AswMath.DistNm(ord[i - 1].LaunchPt, ord[i].LaunchPt) + kn * Math.Max(0, deficit) / 3600.0;
                            fuelOk = AswStrikeMath.StrikeFuelOk(air.FuelFrac, air.EnduranceS, 0, runNm, kn, out remain, out needF);
                        }
                        if (deficit <= c.P.MixShiftMaxS + 1e-9 && fuelOk)
                        {
                            var a = ord[i];
                            a.TArrive += deficit; a.TLaunch += deficit; a.TIssue += deficit; a.TGo += deficit;
                            v.Warnings.Add("mixed_shift uid=" + a.Uid.ToString(CultureInfo.InvariantCulture) + " s=" + deficit.ToString("0", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            drop.Add(allPieces.First(p => p.As == ord[i]));
                            late.Add("mix_late uid=" + ord[i].Uid.ToString(CultureInfo.InvariantCulture) + " ammo=" + ord[i].Ammo);
                        }
                    }
                }
                if (drop.Count == 0) break;
                // take the late rows out, recompute the plan of their targets and the times once more
                foreach (var d in drop)
                {
                    allPieces.Remove(d);
                    var list = pieceOf[d.As.TargetId]; list.Remove(d);
                }
                v.Warnings.RemoveRange(wTimes, v.Warnings.Count - wTimes);
                foreach (var kv in pieceOf)
                {
                    var ship = specs.First(s => s.Ship.TrackId == kv.Key).Ship; var tp = plans[kv.Key];
                    if (!drop.Any(d => d.As.TargetId == kv.Key)) continue;
                    var zonesN = Zones(c, ship, c.Primary, axis, sharedOf[kv.Key], clearedOf[kv.Key]);
                    StrikeKillNeed kn2 = c.Sink ? StrikeKillMath.Need(ship, c.Primary, m => StrikeAdMath.PathSigma(m, zonesN), c.Bonus, c.Th) : null;
                    FillTarget(c, v, ship, tp, kv.Value, axis, sharedOf[kv.Key], clearedOf[kv.Key], kn2);
                    if (c.Sink && ship.KillBudget >= 0 && tp.Short) { v.Warnings.RemoveAll(w => w.StartsWith("kill_short", StringComparison.Ordinal) && w.EndsWith("target=" + kv.Key.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)); v.Warnings.Add("kill_short n=" + tp.ShortBy.ToString(CultureInfo.InvariantCulture) + " target=" + kv.Key.ToString(CultureInfo.InvariantCulture)); }
                }
            }
            v.Warnings.AddRange(late);
            foreach (var a in v.Assignments)
            {
                if (!arrivals.ContainsKey(a.TargetId)) arrivals[a.TargetId] = new List<double>();
                for (int k = 0; k < Math.Max(1, a.Salvo); k++) arrivals[a.TargetId].Add(a.TArrive + k);
            }
            foreach (var kv in plans)
            {
                List<double> arr;
                arrivals.TryGetValue(kv.Key, out arr);
                kv.Value.TArrive = early.Contains(kv.Key) ? tEarly : tNormal;
                kv.Value.SpreadS = StrikeTotMath.SpreadS(arr);
                if (kv.Value.SpreadS > c.P.TotToleranceS) v.Warnings.Add("spread s=" + F2(kv.Value.SpreadS) + " target=" + kv.Key.ToString(CultureInfo.InvariantCulture));
            }
            // cleared by
            if (early.Count > 0) foreach (var kv in plans) if (!early.Contains(kv.Key)) kv.Value.ClearedBy = early.First();
            v.T = tNormal;
            v.MissilesUsed = v.PerTarget.Sum(t => t.N);
            v.TargetsHit = v.PerTarget.Count(t => t.P >= c.Th);
            var mainPlan = order.Count > 0 && plans.ContainsKey(order[0].TrackId) ? plans[order[0].TrackId] : null;
            bool mainOk = mainPlan != null && mainPlan.P >= c.Th && !all;
            v.Score = (mainOk ? 0 : 1e6) + v.MissilesUsed * 1000.0 - v.TargetsHit * 10.0 + (v.T - now) / 1000.0;
            return v;
        }

        private static string Why(Ctx c, StrikeVariant v)
        {
            var main = v.PerTarget.FirstOrDefault();
            return main == null ? "" : "n=" + main.N.ToString(CultureInfo.InvariantCulture) + " p=" + F2(main.P);
        }

        /// <summary>The common prologue of Variants and Probe (priority, the lead positions, the axis); null = nothing to plan.</summary>
        private static Ctx Prepare(StrikePlanInput inp, out StrikeShip mainShip, out double axis, out List<StrikeShip> valid)
        {
            mainShip = null; axis = 0; valid = null;
            if (inp == null || inp.Air == null || inp.Ships == null || inp.Target == null) return null;
            var c = new Ctx { In = inp, P = inp.P ?? new StrikeParams() };
            c.Sink = string.Equals(inp.Goal, "sink", StringComparison.OrdinalIgnoreCase);
            c.Th = c.Sink ? inp.KillThreshold : c.P.PkThreshold;
            c.Bonus = inp.DamageBonus;
            foreach (var a in inp.Air)
            {
                if (a == null || StrikeAirMath.AircraftWhy(a) != null) continue;
                if (!a.Airborne) { c.Warn.Add("not_airborne uid=" + a.Uid.ToString(CultureInfo.InvariantCulture)); continue; }
                c.Air.Add(a);
            }
            if (c.Air.Count == 0) return null;
            foreach (var hs in inp.Ships) if (hs != null && hs.Hostile && !hs.Civilian) c.Hostiles.Add(hs);
            // primary missile: the most numerous type of the group
            var byAmmo = new Dictionary<string, int>();
            var proto = new Dictionary<string, StrikeMissile>();
            foreach (var a in c.Air) foreach (var m in a.Missiles) if (StrikeAirMath.IsStrikeMissile(m) && m.Operable && m.Count > 0) { byAmmo[m.Ammo] = (byAmmo.ContainsKey(m.Ammo) ? byAmmo[m.Ammo] : 0) + m.Count; proto[m.Ammo] = m; }
            if (byAmmo.Count == 0) return null;
            c.Primary = proto[byAmmo.OrderByDescending(kv => kv.Value).ThenByDescending(kv => proto[kv.Key].MaxNm).First().Key];
            valid = Prioritize(inp.Ships, StrikeCalib.TonBandFrac);
            var vById = valid.ToDictionary(s => s.TrackId);
            if (!vById.ContainsKey(inp.Target.MainId)) return null;
            var main0 = vById[inp.Target.MainId];
            // lead positions at the time the missiles are expected to arrive
            double rr = LaunchRange(c, c.Primary, main0);
            c.Dt0 = StrikeTotMath.RoughTofS(rr, c.Primary.SpeedKn > 0 ? c.Primary.SpeedKn : 540) + 120;
            foreach (var s in valid)
            {
                var l = new StrikeShip
                {
                    TrackId = s.TrackId, Lat = StrikeGeoMath.Lead(s, c.Dt0).Lat, Lon = StrikeGeoMath.Lead(s, c.Dt0).Lon, CourseDeg = s.CourseDeg, SpeedKn = s.SpeedKn,
                    DisplacementT = s.DisplacementT, SamMaxNm = s.SamMaxNm, SamCeilFt = s.SamCeilFt, RadarFt = s.RadarFt, Ad = s.Ad, Sams = s.Sams, ClassName = s.ClassName, ClassIni = s.ClassIni,
                    Role = s.Role, Identified = s.Identified, Hostile = s.Hostile, Carrier = s.Carrier,
                    KillBudget = s.KillBudget, HasCiws = s.HasCiws, PriorLoss = s.PriorLoss, PriorHits = s.PriorHits,
                };
                c.Lead[s.TrackId] = l;
                if (s.Ad.Ksub > 0 && s.SamMaxNm > 0) c.Defenders.Add(l);
            }
            mainShip = c.Lead[main0.TrackId];
            axis = StrikeGeoMath.AxisDeg(inp.From, new GeoPt(mainShip.Lat, mainShip.Lon));
            return c;
        }

        /// <summary>T-342 B: ONE variant of the wanted kind without the others (the deck planner asks it for every probe). min_missiles: the variant of Variants (in sink the red damage_only when the threshold is not reached); max_targets: the whole target set in one volley. null = nothing to plan (no airborne striker, no valid main target).</summary>
        public static StrikeVariant Probe(StrikePlanInput inp, string kind)
        {
            StrikeShip mainShip; double axis; List<StrikeShip> valid;
            var c = Prepare(inp, out mainShip, out axis, out valid);
            if (c == null) return null;
            StrikeVariant v;
            if (kind == "clear_pause")
            {
                if (!c.Sink) return null;
                var onTrack = OnTrack(c, mainShip, axis);
                if (onTrack.Count == 0) return null;
                v = ClearPauseCore(c, axis, mainShip, onTrack.OrderByDescending(x => x.Ad.Ksub).First());
                if (v != null) v.Warnings.AddRange(c.Warn);
                return v;
            }
            if (kind == "max_targets")
            {
                var set = new List<StrikeShip>();
                foreach (var s in valid) if (s.TrackId == inp.Target.MainId || inp.Target.Ships.Contains(s.TrackId)) set.Add(c.Lead[s.TrackId]);
                if (set.Count < 2) return Make(c, "min_missiles", axis, new[] { mainShip }, null, null);
                v = Make(c, "max_targets", axis, set, new HashSet<int>(set.Select(s => s.TrackId)), null);
            }
            else
            {
                v = Make(c, "min_missiles", axis, new[] { mainShip }, null, null);
                if (c.Sink && (v.PerTarget.Count == 0 || v.PerTarget[0].P < c.Th)) v = Make(c, "damage_only", axis, new[] { mainShip }, null, null);
            }
            v.Warnings.AddRange(c.Warn);
            return v;
        }

        /// <summary>The neighbours with AA whose zone the track to the main target crosses (at the lead position).</summary>
        private static List<StrikeShip> OnTrack(Ctx c, StrikeShip mainShip, double axis)
        {
            var onTrack = new List<StrikeShip>();
            bool sup = Supersonic(c.Primary);
            double r = LaunchRange(c, c.Primary, mainShip);
            var tp = Pos(mainShip);
            var launch = StrikeGeoMath.LaunchPt(tp, axis, r);
            foreach (var n in c.Defenders)
            {
                if (n.TrackId == mainShip.TrackId) continue;
                double rad = StrikeAirMath.IsSeaSkim(c.Primary) ? Math.Min(n.SamMaxNm, StrikeGeoMath.HorizonNm(n.RadarFt > 0 ? n.RadarFt : 100, c.Primary.CruiseAltFt)) : n.SamMaxNm;
                if (KOf(n, sup) > 0 && rad > 0 && StrikeGeoMath.ChordNm(launch, tp, Pos(n), rad) > 0) onTrack.Add(n);
            }
            return onTrack;
        }

        /// <summary>A copy of the context with another set of aircraft and (dropNeighbour != 0) without that ship among the defenders: the world of one stage of clear_pause.</summary>
        private static Ctx SubCtx(Ctx c, IEnumerable<StrikeAircraft> air, int dropNeighbour)
        {
            var r = new Ctx { In = c.In, P = c.P, Dt0 = c.Dt0, Primary = c.Primary, Warn = c.Warn, Sink = c.Sink, Th = c.Th, Bonus = c.Bonus, Hostiles = c.Hostiles, Lead = c.Lead };
            r.Air.AddRange(air);
            foreach (var d in c.Defenders) if (d.TrackId != dropNeighbour) r.Defenders.Add(d);
            return r;
        }

        private static int UsableMissiles(StrikeAircraft a)
        {
            int n = 0;
            foreach (var m in a.Missiles) if (StrikeAirMath.IsStrikeMissile(m) && m.Operable && m.Count > 0) n += m.Count;
            return n;
        }

        /// <summary>clear_pause for one neighbour: stage 1 = whole aircraft (nearest to the neighbour first) until P(sunk neighbour) reaches the threshold; stage 2 = the main target from the other aircraft with the zones WITHOUT the neighbour, and the same missiles counted with the neighbour (PIfAlive). null = stage 1 is red or no aircraft is left for stage 2.</summary>
        private static StrikeVariant ClearPauseCore(Ctx c, double axis, StrikeShip mainShip, StrikeShip nb)
        {
            if (!c.Sink || nb == null || mainShip == null || nb.TrackId == mainShip.TrackId || nb.KillBudget < 0) return null;
            var zones1 = Zones(c, nb, c.Primary, axis, null, null);
            var need = StrikeKillMath.Need(nb, c.Primary, m => StrikeAdMath.PathSigma(m, zones1), c.Bonus, c.Th);
            if (need.M <= 0) return null;
            var nbPos = Pos(nb);
            var s1 = new List<StrikeAircraft>();
            int have = 0;
            foreach (var a in c.Air.OrderBy(x => AswMath.DistNm(new GeoPt(x.Lat, x.Lon), nbPos)).ThenBy(x => x.Uid))
            {
                int cnt = UsableMissiles(a);
                if (cnt <= 0) continue;
                s1.Add(a); have += cnt;
                if (have >= need.M) break;
            }
            if (have < need.M) return null;
            var v1 = Make(SubCtx(c, s1, 0), "clear_pause", axis, new[] { nb }, null, null);
            if (v1.PerTarget.Count == 0 || v1.PerTarget[0].P < c.Th) return null;
            var rest = c.Air.Where(a => !s1.Contains(a) && UsableMissiles(a) > 0).ToList();
            if (rest.Count == 0) return null;
            var c2 = SubCtx(c, rest, nb.TrackId);
            var v2 = Make(c2, "clear_pause", axis, new[] { mainShip }, null, null);
            if (v2.PerTarget.Count == 0 || v2.PerTarget[0].P < c.Th) v2 = Make(c2, "damage_only", axis, new[] { mainShip }, null, null);
            bool red = v2.Red || v2.PerTarget.Count == 0 || v2.PerTarget[0].P < c.Th;
            var alive = Make(SubCtx(c, rest, 0), "damage_only", axis, new[] { mainShip }, null, null);   // every missile of stage 2, the neighbour still shooting
            var p1 = v1.PerTarget[0]; var p2 = v2.PerTarget[0];
            p1.Stage = 1; p2.Stage = 2;
            p2.PIfAlive = alive.PerTarget.Count == 0 ? 0 : (alive.PerTarget[0].PSunk >= 0 ? alive.PerTarget[0].PSunk : alive.PerTarget[0].P);
            var v = new StrikeVariant { Kind = "clear_pause", AxisDeg = axis, Goal = "sink", Red = red, ClearNeighbour = nb.TrackId };
            foreach (var a in v1.Assignments) { a.Stage = 1; v.Assignments.Add(a); }
            foreach (var a in v2.Assignments) { a.Stage = 2; v.Assignments.Add(a); }
            v.PerTarget.Add(p1); v.PerTarget.Add(p2);
            v.Warnings.AddRange(v1.Warnings); v.Warnings.AddRange(v2.Warnings);
            v.Warnings.Add("clear_pause:" + nb.TrackId.ToString(CultureInfo.InvariantCulture));
            v.T = v1.T;
            v.TStage2 = Math.Max(v2.T, v1.T + c.P.ResultDelayS + c.P.TurnLeadS);
            v.FuelMinFrac = Math.Min(v1.FuelMinFrac, v2.FuelMinFrac);
            v.MissilesUsed = p1.N + p2.N;
            v.TargetsHit = v.PerTarget.Count(t => t.P >= c.Th);
            v.Score = (red ? 1e6 : 0) + v.MissilesUsed * 1000.0 - v.TargetsHit * 10.0 + (v.T - c.In.Now) / 1000.0;
            v.Why.Add("neighbour " + nb.TrackId.ToString(CultureInfo.InvariantCulture) + " n=" + p1.N.ToString(CultureInfo.InvariantCulture) + " p=" + F2(p1.P));
            return v;
        }

        /// <summary>T-342 C: clear_pause for the given neighbour (null = no valid neighbour on the track / no aircraft left for stage 2 / stage 1 red). Sink goal only.</summary>
        public static StrikeVariant ClearPause(StrikePlanInput inp, int neighbourId)
        {
            StrikeShip mainShip; double axis; List<StrikeShip> valid;
            var c = Prepare(inp, out mainShip, out axis, out valid);
            if (c == null || !c.Sink) return null;
            StrikeShip nb;
            if (!c.Lead.TryGetValue(neighbourId, out nb)) return null;
            var v = ClearPauseCore(c, axis, mainShip, nb);
            if (v != null) v.Warnings.AddRange(c.Warn);
            return v;
        }

        /// <summary>2-3 variants (candidates min_missiles | max_targets | clear_pause | flank), best first. Empty when there is nothing to plan (no airborne striker with missiles, no valid main target).</summary>
        public static List<StrikeVariant> Variants(StrikePlanInput inp)
        {
            var result = new List<StrikeVariant>();
            StrikeShip mainShip; double axis; List<StrikeShip> valid;
            var c = Prepare(inp, out mainShip, out axis, out valid);
            if (c == null) return result;
            var cand = new List<StrikeVariant>();
            // min_missiles
            var minV = Make(c, "min_missiles", axis, new[] { mainShip }, null, null);
            // sink: when the main target does not reach the threshold, the first variant is the red damage_only (every missile of the airborne aircraft on the main target)
            StrikeVariant redV = null;
            if (c.Sink && (minV.PerTarget.Count == 0 || minV.PerTarget[0].P < c.Th))
            {
                redV = Make(c, "damage_only", axis, new[] { mainShip }, null, null);
                cand.Add(redV);
            }
            else cand.Add(minV);
            // max_targets
            if (string.Equals(inp.Target.Mode, "spread", StringComparison.OrdinalIgnoreCase))
            {
                var set = new List<StrikeShip>();
                foreach (var s in valid) if (s.TrackId == inp.Target.MainId || inp.Target.Ships.Contains(s.TrackId)) set.Add(c.Lead[s.TrackId]);
                for (int k = set.Count; k >= 2; k--)
                {
                    var order = set.Take(k).ToList();
                    var ids = new HashSet<int>(order.Select(s => s.TrackId));
                    var mv = Make(c, "max_targets", axis, order, ids, null);
                    if (mv.PerTarget.All(t => t.P >= c.Th)) { cand.Add(mv); break; }
                }
            }
            // clear_pause (T-342 C, ADR-0133 section 5): the strongest neighbour with AA on the track to the main target is sunk first (stage 1), the main target follows after the observed sinking (stage 2)
            inp.ClearingNote = "none";
            if (c.Sink && !inp.NoClearPause)
            {
                var onTrack = OnTrack(c, mainShip, axis);
                inp.ClearingNote = "off";
                if (onTrack.Count > 0)
                {
                    var nb = onTrack.OrderByDescending(x => x.Ad.Ksub).First();
                    var cp = ClearPauseCore(c, axis, mainShip, nb);
                    inp.ClearingNote = "hidden";
                    if (cp != null)
                    {
                        // shown: the direct strike is red and this one is green by the main target; or it is not dearer than the direct strike by more than ClearShowFrac; or the neighbour is a target of its own
                        bool listed = onTrack.Any(n => inp.Target.Ships.Contains(n.TrackId));
                        bool cheap = cp.MissilesUsed <= minV.MissilesUsed * StrikeCalib.ClearShowFrac + 1e-9;
                        bool show = (redV != null && !cp.Red) || listed || (cheap && (!cp.Red || redV != null));
                        if (show) { cp.Why.Add("clear_pause_wait_sunk"); cand.Add(cp); inp.ClearingNote = "shown"; }
                    }
                }
            }
            // flank: another approach where the strong ships are not first on the track
            {
                StrikeVariant best = null;
                foreach (double d in new double[] { 45, -45, 90, -90, 135, -135, 180 })
                {
                    var fv = Make(c, "flank", AswMath.Norm360(axis + d), new[] { mainShip }, null, null);
                    if (fv.Warnings.Any(w => w.StartsWith("fuel", StringComparison.Ordinal) || w.StartsWith("short", StringComparison.Ordinal) || w.StartsWith("kill_short", StringComparison.Ordinal) || w.StartsWith("kill_unknown", StringComparison.Ordinal))) continue;
                    if (fv.PerTarget.Count == 0 || fv.PerTarget[0].P < c.Th) continue;
                    if (best == null || fv.MissilesUsed < best.MissilesUsed) best = fv;
                }
                if (best != null && minV.PerTarget.Count > 0 && best.MissilesUsed <= minV.MissilesUsed * (1 - StrikeCalib.FlankSaveFrac) + 1e-9) cand.Add(best);
            }
            // sink: the other variants stay only when they reach the threshold on the main target
            if (c.Sink && redV != null) cand.RemoveAll(v => v != redV && !v.PerTarget.Any(x => x.TrackId == mainShip.TrackId && x.P >= c.Th));
            foreach (var v in cand)
            {
                v.Why.Add("kind:" + v.Kind);
                v.Why.Add("main " + Why(c, v));
                v.Why.Add("carriers=" + v.Assignments.Select(a => a.Uid).Distinct().Count().ToString(CultureInfo.InvariantCulture));
                v.Warnings.AddRange(c.Warn);
            }
            cand.Sort(Compare);
            foreach (var v in cand) { if (result.Count >= c.P.MaxVariants) break; result.Add(v); }
            return result;
        }

        // ---- self test ----
        private static StrikeAd Ad(string ini)
        {
            switch (ini)
            {
                case "tico": return new StrikeAd { Ksub = 18.3163738, Ssub = 50.2173462, Ksup = 13.6496897, Ssup = 26.5461197, Source = "cache" };
                case "kidd": return new StrikeAd { Ksub = 8.86082554, Ssub = 27.714283, Ksup = 5.15813732, Ssup = 12.2857141, Source = "cache" };
                case "perry": return new StrikeAd { Ksub = 3.11741447, Ssub = 9.54761791, Ksup = 1.88876057, Ssup = 4.40476179, Source = "cache" };
                default: return new StrikeAd { Source = "none" };
            }
        }

        private static StrikeShip Ship(int id, string ad, double lat, double lon, double tons, double sam, string role = "AAW", bool ident = true, string side = "hostile")
        {
            return new StrikeShip { TrackId = id, Lat = lat, Lon = lon, DisplacementT = tons, SamMaxNm = sam, RadarFt = 100, Ad = Ad(ad), Identified = ident, Hostile = side == "hostile", Side = side, Role = role, ClassName = ad };
        }

        public static string SelfTest(string part = "all")
        {
            string e;
            if ((part == "all" || part == "prio") && (e = SelfTestPrio()) != null) return e;
            if ((part == "all" || part == "plan") && (e = SelfTestPlan()) != null) return e;
            if ((part == "all" || part == "plan-sink") && (e = SelfTestSink()) != null) return e;
            if ((part == "all" || part == "mix") && (e = SelfTestMix()) != null) return e;
            if ((part == "all" || part == "mix-e1b") && (e = SelfTestMixE1b()) != null) return "mix-e1b: " + e;
            if ((part == "all" || part == "probe") && (e = SelfTestProbe()) != null) return e;
            if ((part == "all" || part == "plan-clear") && (e = SelfTestClear()) != null) return "plan-clear: " + e;
            if ((part == "all" || part == "plan-survivors") && (e = SelfTestSurvivors()) != null) return "plan-survivors: " + e;
            if (part == "deck-budget" && (e = SelfTestDeckBudget()) != null) return e;
            return null;
        }

        /// <summary>T-346 D2: the survivors of a target set: the ships of the set, the main one and the extra ids that are still in the list of ships and not dead, by the priority.</summary>
        internal static List<StrikeShip> Survivors(StrikeTargetSet t, IEnumerable<int> alsoIds, IList<StrikeShip> ships, Func<int, bool> dead, double tonBandFrac)
        {
            var ids = new List<int>();
            if (t != null) { if (t.MainId != 0) ids.Add(t.MainId); foreach (int id in t.Ships) if (!ids.Contains(id)) ids.Add(id); }
            if (alsoIds != null) foreach (int id in alsoIds) if (!ids.Contains(id)) ids.Add(id);
            var alive = (ships ?? new List<StrikeShip>()).Where(s => s != null && ids.Contains(s.TrackId) && (dead == null || !dead(s.TrackId))).ToList();
            return Prioritize(alive, tonBandFrac);
        }

        /// <summary>T-346 D2: the target set with the main one taken from the survivors (the old main is sunk or gone); null = no live target. Mode: spread with more than one, else single.</summary>
        internal static StrikeTargetSet PromoteMain(StrikeTargetSet t, IList<StrikeShip> ships, Func<int, bool> dead, double tonBandFrac)
        {
            var left = Survivors(t, null, ships, dead, tonBandFrac);
            if (left.Count == 0) return null;
            var res = new StrikeTargetSet { Mode = left.Count > 1 ? "spread" : "single", MainId = left[0].TrackId };
            foreach (var s in left) res.Ships.Add(s.TrackId);
            return res;
        }

        private static string SelfTestSurvivors()
        {
            var a = Ship(100, "kidd", 41, 10, 8300, 90); var b = Ship(101, "perry", 41, 10.2, 4000, 20);
            var t = new StrikeTargetSet { Mode = "spread", MainId = 100 }; t.Ships.Add(100); t.Ships.Add(101);
            // 100 is sunk and gone from the list of ships, 101 is alive and never fired at
            var l = Survivors(t, new[] { 100 }, new List<StrikeShip> { b }, id => false, 0.15);
            if (l.Count != 1 || l[0].TrackId != 101) return "survivor " + string.Join(",", l.Select(s => s.TrackId));
            // a dead flag drops a ship that is still in the list
            l = Survivors(t, null, new List<StrikeShip> { a, b }, id => id == 100, 0.15);
            if (l.Count != 1 || l[0].TrackId != 101) return "dead flag " + string.Join(",", l.Select(s => s.TrackId));
            // an excluded / neutral ship is not a survivor
            var n = Ship(102, "kidd", 41, 10.3, 8000, 90, "AAW", true, "neutral"); t.Ships.Add(102);
            l = Survivors(t, null, new List<StrikeShip> { a, b, n }, null, 0.15);
            if (l.Count != 2 || l.Any(s => s.TrackId == 102)) return "neutral " + string.Join(",", l.Select(s => s.TrackId));
            // an extra id (a result of the wave) is a candidate too
            var c = Ship(103, "perry", 41, 10.4, 4000, 20);
            l = Survivors(t, new[] { 103 }, new List<StrikeShip> { a, b, c }, null, 0.15);
            if (l.Count != 3) return "extra id " + l.Count;
            var p = PromoteMain(t, new List<StrikeShip> { b }, id => false, 0.15);
            if (p == null || p.MainId != 101 || p.Mode != "single" || p.Ships.Count != 1) return "promote one " + (p == null ? "null" : p.MainId + p.Mode);
            p = PromoteMain(t, new List<StrikeShip> { a, b }, id => false, 0.15);
            if (p == null || p.Mode != "spread" || p.Ships.Count != 2) return "promote two";
            if (PromoteMain(t, new List<StrikeShip>(), id => false, 0.15) != null) return "all gone = null";
            if (PromoteMain(t, new List<StrikeShip> { a, b }, id => true, 0.15) != null) return "all dead = null";
            return null;
        }

        /// <summary>P7: a Ticonderoga, three decks with three strike loadouts each, the real Probe as Eval: one decision in less than 20 ms (the best of 3).</summary>
        private static string SelfTestDeckBudget()
        {
            var air = new List<StrikeAircraft>(); for (int i = 1; i <= 4; i++) air.Add(HarpoonPlane(i, 39.5 - 0.05 * i, 4));
            var tico = Killable(Ship(120, "tico", 41, 10, 9600, 90), 9600, "minor");
            var decks = new List<StrikeDeckInfo>();
            for (int d = 0; d < 3; d++)
            {
                var kh = StrikeDeckPlanner.KittyHawk(700 + d, 38, 10 - d * 0.4);
                kh.Rows[0].Loadouts.Add(new StrikeDeckLoadout { Name = "AntiShip3", Display = "AntiShip3", Available = true, ReadyS = 280, Missiles = { new StrikeMissile { Ammo = "usn_agm-84c", Name = "usn_agm-84c", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", MinNm = 3.5, MaxNm = 65, Count = 3, SpeedKn = 540, CruiseAltFt = 50, MinLaunchAltFt = 200, Operable = true, WarheadPower = 45 } } });
                StrikeAirMath.Classify(new[] { new StrikeAircraft { Missiles = kh.Rows[0].Loadouts[3].Missiles } });
                decks.Add(kh);
            }
            var inp0 = new StrikeDeckInput { Decks = decks, Target = tico, AxisFrom = new GeoPt(40, 10), AxisDeg = 0, PlanAmmo = "usn_agm-84c", P = new StrikeParams() };
            List<StrikeDeckOffer> rej;
            double best = double.MaxValue; StrikeDeckDecision dec = null;
            for (int run = 0; run < 3; run++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var offers = StrikeDeckPlanner.Offers(inp0, out rej);
                var need = new StrikeDeckNeed
                {
                    Kind = "min_missiles", Offers = offers, Live = new List<StrikeDeckOrder>(), ShortHint = 6, EvalMax = StrikeCalib.DeckEvalMax,
                    Eval = extra =>
                    {
                        var all = new List<StrikeAircraft>(air); all.AddRange(extra);
                        var pi = SinkInput(all, new List<StrikeShip> { tico }, 120);
                        var v = Probe(pi, "min_missiles");
                        if (v == null) return new StrikeDeckEval();
                        return new StrikeDeckEval { Ok = !v.Red && v.PerTarget[0].P >= pi.KillThreshold, P = v.PerTarget[0].P, N = v.MissilesUsed };
                    },
                };
                dec = StrikeDeckPlanner.Decide(need);
                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            if (dec == null || dec.Order.Count == 0 || dec.Why != "ordered" || !dec.With.Ok) return "decision " + (dec == null ? "null" : dec.Why + " " + dec.Order.Count);
            Console.WriteLine("strike deck budget ms=" + best.ToString("0.0", CultureInfo.InvariantCulture) + " evals=" + dec.Evals + " order=" + dec.Order.Count);
            if (best > 20 * 3) return "budget " + best;
            return null;
        }

        private static string SelfTestProbe()
        {
            var air = new List<StrikeAircraft>(); for (int i = 1; i <= 4; i++) air.Add(HarpoonPlane(i, 39.5 - 0.05 * i, 4));
            var kidd = Killable(Ship(100, "kidd", 41, 10, 9630, 90), 9630, "minor");
            var tico = Killable(Ship(120, "tico", 41, 10.2, 9600, 90), 9600, "minor");
            foreach (string goal in new[] { "sink", "breach" })
                foreach (var tgt in new[] { Tuple.Create(kidd, 100), Tuple.Create(tico, 120) })
                {
                    var inp = SinkInput(air, new List<StrikeShip> { tgt.Item1 }, tgt.Item2, goal);
                    var vs = Variants(inp);
                    var pv = Probe(SinkInput(air, new List<StrikeShip> { tgt.Item1 }, tgt.Item2, goal), "min_missiles");
                    if (vs.Count == 0 || pv == null) return goal + " " + tgt.Item2 + ": nothing";
                    var want = vs[0].Kind == "damage_only" || vs[0].Kind == "min_missiles" ? vs[0] : vs.First(v => v.Kind == "min_missiles");
                    if (pv.Kind != want.Kind || pv.MissilesUsed != want.MissilesUsed || Math.Abs(pv.PerTarget[0].P - want.PerTarget[0].P) > 1e-12 || Math.Abs(pv.PerTarget[0].PSunk - want.PerTarget[0].PSunk) > 1e-12) return goal + " " + tgt.Item2 + ": probe " + pv.Kind + "/" + pv.MissilesUsed + " vs " + want.Kind + "/" + want.MissilesUsed;
                }
            // max_targets: two targets in one volley
            var k2 = Killable(Ship(101, "kidd", 41, 10.9, 9630, 90), 9630, "minor");
            var big = new List<StrikeAircraft>(); for (int i = 1; i <= 8; i++) big.Add(HarpoonPlane(i, 39.5 - 0.02 * i, 8));
            var two = SinkInput(big, new List<StrikeShip> { kidd, k2 }, 100, "sink"); two.Target.Mode = "spread"; two.Target.Ships.Add(100); two.Target.Ships.Add(101);
            var mv = Probe(two, "max_targets");
            if (mv == null || mv.Kind != "max_targets" || mv.PerTarget.Count != 2) return "max_targets " + (mv == null ? "null" : mv.Kind + mv.PerTarget.Count);
            // a virtual aircraft takes part
            var virt = new List<StrikeAircraft>(air); var va = HarpoonPlane(-1, 39.4, 4); va.DeckKey = "virtual:7"; virt.Add(va);
            var withV = Probe(SinkInput(virt, new List<StrikeShip> { kidd }, 100), "min_missiles");
            var without = Probe(SinkInput(air, new List<StrikeShip> { kidd }, 100), "min_missiles");
            if (withV == null || without == null || withV.PerTarget[0].PSunk < without.PerTarget[0].PSunk - 1e-9) return "virtual aircraft";
            // an empty sky: null
            if (Probe(SinkInput(new List<StrikeAircraft>(), new List<StrikeShip> { kidd }, 100), "min_missiles") != null) return "empty air";
            return null;
        }

        private static string SelfTestPrio()
        {
            var tico = Ship(1, "tico", 41, 10, 9600, 90); var kidd = Ship(2, "kidd", 41, 10.1, 8300, 90); var perry = Ship(3, "perry", 41, 10.2, 4000, 20);
            var l = Prioritize(new List<StrikeShip> { perry, kidd, tico }, 0.15);
            if (l.Count != 3 || l[0].TrackId != 1 || l[1].TrackId != 2 || l[2].TrackId != 3) return "prio by AA " + string.Join(",", l.Select(s => s.TrackId));
            var kidd2 = Ship(4, "kidd", 41, 10.3, 9000, 90); kidd2.Ad.Ksub = 8.5;   // within 15 %: the tonnage decides
            l = Prioritize(new List<StrikeShip> { kidd, kidd2 }, 0.15);
            if (l[0].TrackId != 4) return "tonnage band " + l[0].TrackId;
            kidd2.Ad.Ksub = 6.0;   // outside the band: the AA decides
            l = Prioritize(new List<StrikeShip> { kidd2, kidd }, 0.15);
            if (l[0].TrackId != 2) return "AA outside the band";
            var transport = Ship(5, "none", 41, 10.4, 20000, 0, "Transport");
            var zero = Ship(6, "none", 41, 10.5, 3000, 0, "AAW");
            l = Prioritize(new List<StrikeShip> { transport, perry, zero }, 0.15);
            if (l[0].TrackId != 3 || l[1].TrackId != 6 || l[2].TrackId != 5) return "transport last " + string.Join(",", l.Select(s => s.TrackId));
            var transportAa = Ship(7, "kidd", 41, 10.4, 20000, 90, "Landing");
            l = Prioritize(new List<StrikeShip> { transportAa, perry }, 0.15);
            if (l[0].TrackId != 3) return "landing with AA is still last";
            // exclusions
            var civ = Ship(8, "none", 41, 10, 30000, 0, "Merchant"); civ.Civilian = true;
            var neutral = Ship(9, "kidd", 41, 10, 8000, 90, "AAW", true, "neutral");
            var friend = Ship(10, "kidd", 41, 10, 8000, 90, "AAW", true, "friendly");
            var unk = Ship(11, "kidd", 41, 10, 8000, 90, "AAW", false, "unknown");
            var hostNotId = Ship(12, "kidd", 41, 10, 8000, 90, "AAW", false, "hostile");
            if (Exclusion(civ) != "civilian" || Exclusion(neutral) != "neutral" || Exclusion(friend) != "friendly" || Exclusion(unk) != "not_hostile" || Exclusion(hostNotId) != "not_identified") return "exclusions " + Exclusion(civ) + "," + Exclusion(neutral) + "," + Exclusion(friend) + "," + Exclusion(unk) + "," + Exclusion(hostNotId);
            if (Exclusion(tico) != null) return "valid target";
            if (Prioritize(new List<StrikeShip> { civ, neutral, friend, unk, hostNotId }, 0.15).Count != 0) return "excluded ships are not listed";
            var cv = Ship(13, "kidd", 41, 10, 8000, 90); cv.Excluded = true; cv.Why = "own_reason";
            if (Exclusion(cv) != "own_reason") return "excluded flag";
            return null;
        }

        private static StrikeAircraft Plane(int uid, string type, double lat, double lon, string ammo, int count, double minNm = 3.5, double maxNm = 65, double kn = 540, double cruiseAlt = 50, string illum = null)
        {
            var a = new StrikeAircraft { Uid = uid, Type = type, ObjType = "Aircraft", Airborne = true, Lat = lat, Lon = lon, CruiseKn = 450, CruiseAltFt = 25000, FuelFrac = 1, EnduranceS = 18000, AltFt = 25000 };
            a.Missiles.Add(new StrikeMissile { Ammo = ammo, Name = ammo, Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = illum ?? "none", Count = count, MinNm = minNm, MaxNm = maxNm, SpeedKn = kn, CruiseAltFt = cruiseAlt, MinLaunchAltFt = 200, FrontalM2 = 0.08, MassKg = 520, Operable = true });
            StrikeAirMath.Classify(new[] { a });
            return a;
        }

        private static StrikePlanInput Input(IList<StrikeAircraft> air, IList<StrikeShip> ships, int main, string mode = "single", params int[] set)
        {
            var t = new StrikeTargetSet { Mode = mode, MainId = main };
            t.Ships.AddRange(set);
            return new StrikePlanInput { Air = air, Ships = ships, Target = t, From = new GeoPt(40, 10), Now = 1000, TofS = (a, m, s, r) => StrikeTotMath.RoughTofS(r, m.SpeedKn) };
        }

        private static void Dump(string tag, IList<StrikeVariant> vs)
        {
            if (Environment.GetEnvironmentVariable("STRIKE_DUMP") == null) return;
            foreach (var v in vs)
                Console.WriteLine("dump " + tag + " " + v.Kind + " score=" + v.Score.ToString("0.0", CultureInfo.InvariantCulture) + " n=" + v.MissilesUsed + " hit=" + v.TargetsHit + " T=" + (v.T - 1000).ToString("0", CultureInfo.InvariantCulture)
                    + " targets=[" + string.Join(";", v.PerTarget.Select(x => x.TrackId + ":n" + x.N + ":p" + F2(x.P) + ":t" + (x.TArrive - 1000).ToString("0", CultureInfo.InvariantCulture))) + "] warn=[" + string.Join(";", v.Warnings) + "]");
        }

        private static string SelfTestPlan()
        {
            // (a) 4 x A-6E, 4 Harpoon each, against a Kidd: min_missiles needs 8 = 2 carriers
            var air = new List<StrikeAircraft>();
            for (int i = 1; i <= 4; i++) air.Add(Plane(i, "usn_a-6e", 39.5 - 0.05 * i, 10, "usn_agm-84c", 4));
            var kidd = Ship(100, "kidd", 41, 10, 8300, 90);
            var vs = Variants(Input(air, new List<StrikeShip> { kidd }, 100));
            if (vs.Count == 0 || vs[0].Kind != "min_missiles") return "(a) no min_missiles first";
            Dump("a", vs);
            var v = vs[0];
            if (v.MissilesUsed != 8 || v.PerTarget[0].N != 8 || v.PerTarget[0].P < 0.9) return "(a) n=" + v.MissilesUsed + " p=" + v.PerTarget[0].P;
            if (v.Assignments.Select(a => a.Uid).Distinct().Count() != 2) return "(a) carriers " + v.Assignments.Select(a => a.Uid).Distinct().Count();
            if (v.Why.Count == 0 || vs.Any(x => x.Why.Count == 0)) return "(i) why";
            if (v.Estimate != StrikeCalib.Estimate) return "estimate flag";
            foreach (var a in v.Assignments) if (a.Salvo < 1 || a.Salvo > 8 || a.TArrive != v.T) return "(a) assignment times";
            if (vs.Count > 3) return "(i) at most 3";

            // (b) Kidd + Perry, 16 missiles, spread: max_targets hits both, the shared factor is applied
            air.Clear();
            for (int i = 1; i <= 4; i++) air.Add(Plane(i, "usn_a-6e", 39.5 - 0.05 * i, 10, "usn_agm-84c", 4));
            var perry = Ship(101, "perry", 41, 10.8, 4000, 20);   // 48 nm to the east: not on the track
            var both = new List<StrikeShip> { kidd, perry };
            vs = Variants(Input(air, both, 100, "spread", 100, 101));
            Dump("b", vs);
            var mt = vs.FirstOrDefault(x => x.Kind == "max_targets");
            if (mt == null) return "(b) no max_targets";
            if (mt.TargetsHit != 2 || mt.PerTarget.Count != 2 || mt.PerTarget.Any(t => t.P < 0.9)) return "(b) both targets p";
            if (mt.MissilesUsed <= vs.First(x => x.Kind == "min_missiles").MissilesUsed) return "(b) max_targets uses more missiles";
            if (mt.PerTarget[0].TArrive != mt.PerTarget[1].TArrive) return "(b) one T for the volley";
            // a shared neighbour costs more missiles than a lone one: Kidd with a Perry that is hit in the same volley and sits near the track
            var perryNear = Ship(102, "perry", 40.95, 10.05, 4000, 20);
            var vsN = Variants(Input(air, new List<StrikeShip> { kidd, perryNear }, 100, "spread", 100, 102));
            var mtn = vsN.FirstOrDefault(x => x.Kind == "max_targets");
            var minN = vsN.First(x => x.Kind == "min_missiles");
            var minLone = Variants(Input(air, new List<StrikeShip> { kidd }, 100)).First();
            if (minN.MissilesUsed < minLone.MissilesUsed) return "(b) a neighbour never lowers the need";

            // (c) T-342 C: the card "clearing" of v1 is gone in every line (ADR-0133 section 5): a strong ship before a weak main target never makes a variant of that kind (clear_pause is tested in plan-clear)
            air.Clear();
            for (int i = 1; i <= 6; i++) air.Add(Plane(i, "usn_a-6e", 39.5 - 0.05 * i, 10, "usn_agm-84c", 4));
            var main = Ship(110, "perry", 41, 10, 4000, 20);
            var guard = Ship(111, "tico", 40.85, 10, 9600, 90);   // 9 nm south of the main target: on the track
            foreach (var cin in new[] { Input(air, new List<StrikeShip> { main, guard }, 110), Input(air, new List<StrikeShip> { main, guard }, 110, "spread", 110, 111) })
            {
                vs = Variants(cin);
                Dump("c", vs);
                if (vs.Any(x => x.Kind == "clearing" || x.Kind == "clear_pause")) return "(c) breach: neither clearing nor clear_pause " + string.Join(",", vs.Select(x => x.Kind));
                if (cin.ClearingNote != "none") return "(c) the note of a breach plan " + cin.ClearingNote;
            }

            // (c2) the launch point over land moves toward the target; land up to the minimum range = a warning
            {
                var one = new List<StrikeAircraft> { Plane(1, "usn_a-6e", 39.5, 10, "usn_agm-84c", 8) };
                var kd = Ship(300, "kidd", 41, 10, 8300, 90);
                var inL = Input(one, new List<StrikeShip> { kd }, 300);
                var v0 = Variants(inL)[0];
                var lp0 = v0.Assignments[0].LaunchPt;
                inL = Input(one, new List<StrikeShip> { kd }, 300);
                inL.IsLand = pt => AswMath.DistNm(pt, lp0) < 3;
                var v1 = Variants(inL)[0];
                var a1 = v1.Assignments[0];
                if (AswMath.DistNm(a1.LaunchPt, lp0) < 2.9 || a1.LaunchNm >= v0.Assignments[0].LaunchNm || a1.LaunchNm < 3.5 || v1.Warnings.Any(w => w.StartsWith("launch_land", StringComparison.Ordinal))) return "(c2) launch point shifted off the land " + a1.LaunchNm;
                inL = Input(one, new List<StrikeShip> { kd }, 300);
                inL.IsLand = pt => true;
                var v2 = Variants(inL)[0];
                if (!v2.Warnings.Contains("launch_land:1")) return "(c2) launch_land warning " + string.Join(";", v2.Warnings);
            }

            // (d) flank: only with a saving of >= 20 % and fuel
            foreach (var x in vs) if (x.Kind == "flank" && x.MissilesUsed > vs.First(y => y.Kind == "min_missiles").MissilesUsed * 0.8 + 1e-9) return "(d) flank without a saving";
            // fuel: an aircraft that cannot fly the extra way is not a flank candidate
            var lowFuel = new List<StrikeAircraft>();
            foreach (var a in air) { a.FuelFrac = 0.05; lowFuel.Add(a); }
            vs = Variants(Input(lowFuel, new List<StrikeShip> { main, guard }, 110));
            if (vs.Any(x => x.Kind == "flank")) return "(d) flank without fuel";
            foreach (var a in air) a.FuelFrac = 1;

            // (e) not enough missiles: the main target as a whole + a warning
            var few = new List<StrikeAircraft> { Plane(1, "usn_a-6e", 39.5, 10, "usn_agm-84c", 4) };
            var tico = Ship(120, "tico", 41, 10, 9600, 90);
            vs = Variants(Input(few, new List<StrikeShip> { tico }, 120));
            if (vs.Count == 0 || vs[0].MissilesUsed != 4 || !vs[0].Warnings.Any(w => w.StartsWith("short n=", StringComparison.Ordinal))) return "(e) short " + (vs.Count > 0 ? string.Join(";", vs[0].Warnings) : "none");
            if (vs[0].PerTarget[0].P >= 0.9) return "(e) p below the threshold";
            // spread without missiles for all: the main target stays whole
            vs = Variants(Input(few, new List<StrikeShip> { tico, kidd }, 120, "spread", 120, 100));
            if (vs.Any(x => x.Kind == "max_targets")) return "(e) max_targets needs the missiles for every target";

            // (f) mixed load: Harpoon D (118 nm) and AM-39 (35 nm) on one target: one T, the far missile is launched first
            var mixed = new List<StrikeAircraft>
            {
                Plane(1, "usn_a-6e", 39.5, 10, "usn_agm-84d", 4, 3.5, 118),
                Plane(2, "fr_f-1c", 39.5, 10.1, "fr_am-39", 4, 6, 35, 620, 33),
            };
            vs = Variants(Input(mixed, new List<StrikeShip> { kidd }, 100));
            Dump("f", vs);
            var mx = vs[0];
            if (mx.Assignments.Select(a => a.Ammo).Distinct().Count() < 2) return "(f) both types are used " + mx.MissilesUsed;
            if (mx.Assignments.Select(a => a.TArrive).Distinct().Count() != 1) return "(f) one T per target";
            var far = mx.Assignments.First(a => a.Ammo == "usn_agm-84d"); var nearA = mx.Assignments.First(a => a.Ammo == "fr_am-39");
            if (!(far.LaunchNm > nearA.LaunchNm) || !(far.TofS > nearA.TofS) || !(far.TLaunch < nearA.TLaunch)) return "(f) the far missile first: " + far.TLaunch + " vs " + nearA.TLaunch;
            // (g) AS-2: a warning
            var as2 = new List<StrikeAircraft> { Plane(1, "wp_tu-16k", 39.0, 10, "wp_as-2", 6, 35, 150, 1100, 3000, "radio") };
            vs = Variants(Input(as2, new List<StrikeShip> { kidd }, 100));
            if (vs.Count == 0 || !vs[0].Warnings.Any(w => w.StartsWith("needs_illum", StringComparison.Ordinal))) return "(g) needs_illum";
            // (P7) phase C: the gate behind the launch point over the sea, the descent point, the times of one assignment
            var pp = new StrikeParams();
            foreach (var a in mx.Assignments)
            {
                if (a.GateWhy != "ok" || Math.Abs(AswMath.DistNm(a.GatePt, a.LaunchPt) - 15) > 0.3) return "(P7) gate " + a.GateWhy;
                if (!(a.TGo < a.TLaunch) || a.CruiseAltFt != 25000 || a.LaunchAltFt != 200) return "(P7) go / altitudes";
                if (Math.Abs(a.TIssue - (a.TLaunch - StrikeTotMath.TurnLeadS(0, pp))) > 1e-9) return "(P7) the issue time follows the turn lead";
                if (!a.DescentAtStart) return "(P7) the aircraft is inside the horizon of the ship: the descent is at the start";
            }
            var farAir = new List<StrikeAircraft> { Plane(1, "usn_a-6e", 35.0, 10, "usn_agm-84d", 8, 3.5, 118) };
            vs = Variants(Input(farAir, new List<StrikeShip> { kidd }, 100));
            if (vs.Count == 0) return "(P7) far aircraft: no plan";
            var fa = vs[0].Assignments[0];
            if (fa.DescentAtStart || Math.Abs(AswMath.DistNm(fa.DescentPt, new GeoPt(kidd.Lat, kidd.Lon)) - (StrikeGeoMath.HorizonNm(100, 25000) + 5)) > 2.0) return "(P7) the descent point " + AswMath.DistNm(fa.DescentPt, new GeoPt(kidd.Lat, kidd.Lon));
            // gate over land: no gate = a warning of the card
            var landInp = Input(farAir, new List<StrikeShip> { kidd }, 100);
            landInp.IsLand = pt => true;
            vs = Variants(landInp);
            if (vs.Count == 0 || !vs[0].Warnings.Any(w => w.StartsWith("gate:", StringComparison.Ordinal))) return "(P7) gate over land: " + (vs.Count > 0 ? string.Join(";", vs[0].Warnings) : "none");
            // one aircraft, two missiles: the far one first; a second launch that cannot be reached in time is flagged
            var two = Plane(1, "usn_a-6e", 39.5, 10, "usn_agm-84d", 2, 3.5, 118);
            two.Missiles.Add(new StrikeMissile { Ammo = "fr_am-39", Name = "fr_am-39", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = 2, MinNm = 6, MaxNm = 35, SpeedKn = 620, CruiseAltFt = 33, MinLaunchAltFt = 200, FrontalM2 = 0.08, MassKg = 520, Operable = true });
            StrikeAirMath.Classify(new[] { two });
            vs = Variants(Input(new List<StrikeAircraft> { two }, new List<StrikeShip> { kidd }, 100));
            if (vs.Count == 0 || vs[0].Assignments.Count != 2) return "(P7) two missiles of one aircraft: " + (vs.Count > 0 ? vs[0].Assignments.Count.ToString() : "none");
            var o0 = vs[0].Assignments.First(a => a.Order == 0); var o1 = vs[0].Assignments.First(a => a.Order == 1);
            if (o0.Ammo != "usn_agm-84d" || o1.Ammo != "fr_am-39" || !(o0.TLaunch < o1.TLaunch)) return "(P7) order of one aircraft: " + o0.Ammo + " " + o1.Ammo;
            if (!vs[0].Warnings.Any(w => w.StartsWith("mixed_shift uid=1 s=", StringComparison.Ordinal)) || Math.Abs((o1.TArrive - o0.TArrive) - 66) > 5) return "(P7) mixed_shift: " + string.Join(";", vs[0].Warnings);
            foreach (var a in mx.Assignments) if (a.Order != 0) return "(P7) different aircraft are all Order 0";
            // (h) compare
            var lo = new StrikeVariant { Score = 8000 }; var hi = new StrikeVariant { Score = 9000 };
            if (Compare(lo, hi) >= 0 || Compare(hi, lo) <= 0 || Compare(lo, lo) != 0) return "(h) compare";
            var shortV = new StrikeVariant { MissilesUsed = 4 };
            // a variant that misses the threshold is worse than one that reaches it, however few missiles it spends
            vs = Variants(Input(few, new List<StrikeShip> { tico }, 120));
            // (j) nothing to plan
            if (Variants(Input(new List<StrikeAircraft>(), new List<StrikeShip> { kidd }, 100)).Count != 0) return "(j) no aircraft";
            if (Variants(Input(air, new List<StrikeShip> { kidd }, 999)).Count != 0) return "(j) no main target";
            var civ = Ship(130, "kidd", 41, 10, 8000, 90); civ.Civilian = true;
            if (Variants(Input(air, new List<StrikeShip> { civ }, 130)).Count != 0) return "(j) civilian main target";
            var deck = Plane(9, "usn_a-6e", 39, 10, "usn_agm-84c", 4); deck.Airborne = false;
            vs = Variants(Input(new List<StrikeAircraft> { deck, Plane(10, "usn_a-6e", 39, 10, "usn_agm-84c", 4), Plane(11, "usn_a-6e", 39, 10, "usn_agm-84c", 4) }, new List<StrikeShip> { kidd }, 100));
            if (vs.Count == 0 || vs[0].Assignments.Any(a => a.Uid == 9) || !vs[0].Warnings.Contains("not_airborne uid=9")) return "(j) deck aircraft";
            // (k) budget: 8 aircraft x 12 targets
            var big = new List<StrikeAircraft>(); for (int i = 1; i <= 8; i++) big.Add(Plane(i, "usn_a-6e", 39.5 - 0.02 * i, 10 + 0.02 * i, "usn_agm-84c", 4));
            var many = new List<StrikeShip>(); var ids = new List<int>();
            for (int i = 0; i < 12; i++) { many.Add(Ship(200 + i, i % 3 == 0 ? "tico" : i % 3 == 1 ? "kidd" : "perry", 41 + 0.03 * (i / 4), 10 + 0.04 * (i % 4), 5000 + 100 * i, 40)); ids.Add(200 + i); }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            vs = Variants(Input(big, many, 200, "spread", ids.ToArray()));
            sw.Stop();
            Console.WriteLine("strike plan budget ms=" + sw.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " variants=" + vs.Count);
            if (vs.Count == 0) return "(k) no variants";
            return null;
        }

        // ---- T-342 phase C: clear_pause (stage 1 sinks the neighbour, stage 2 hits the main target without its zone)
        private static List<StrikeAircraft> Squadron(int n, int each = 4)
        {
            var air = new List<StrikeAircraft>(); for (int i = 1; i <= n; i++) air.Add(HarpoonPlane(i, 39.5 - 0.03 * i, each));
            return air;
        }

        private static StrikeShip PerryKillable(int id, double lat, double lon)
        {
            var p = Killable(Ship(id, "perry", lat, lon, 4000, 40), 4000, "minor");
            return p;
        }

        private static string SelfTestClear()
        {
            var kidd = Killable(Ship(100, "kidd", 41, 10, 9630, 90), 9630, "minor");
            var perry = PerryKillable(101, 40.75, 10);   // 15 nm before the Kidd on the track
            var air = Squadron(6);
            var inp = SinkInput(air, new List<StrikeShip> { kidd, perry }, 100);
            var cp = ClearPause(inp, 101);
            if (cp == null) return "no clear_pause for a Perry in front of a Kidd (6 x 4 Harpoon)";
            Dump("clear", new List<StrikeVariant> { cp });
            if (cp.Kind != "clear_pause" || cp.ClearNeighbour != 101 || cp.Goal != "sink") return "kind " + cp.Kind + " nb " + cp.ClearNeighbour;
            var p1 = cp.PerTarget.FirstOrDefault(t => t.Stage == 1); var p2 = cp.PerTarget.FirstOrDefault(t => t.Stage == 2);
            if (p1 == null || p2 == null || p1.TrackId != 101 || p2.TrackId != 100) return "stages of the targets";
            if (p1.PSunk < 0.8 - 1e-9) return "stage 1 P(sunk Perry) " + p1.PSunk;
            if (cp.Red || p2.PSunk < 0.8 - 1e-9) return "stage 2 is green " + p2.PSunk;
            // the aircraft are whole: no aircraft in both stages; stage 1 is the nearest to the Perry
            var u1 = cp.Assignments.Where(a => a.Stage == 1).Select(a => a.Uid).Distinct().ToList();
            var u2 = cp.Assignments.Where(a => a.Stage == 2).Select(a => a.Uid).Distinct().ToList();
            if (u1.Count == 0 || u2.Count == 0 || u1.Intersect(u2).Any()) return "aircraft in both stages " + string.Join(",", u1) + " / " + string.Join(",", u2);
            if (cp.Assignments.Any(a => a.Stage == 1 && a.TargetId != 101) || cp.Assignments.Any(a => a.Stage == 2 && a.TargetId != 100)) return "targets of the assignments";
            // stage 2 has no zone of the Perry; the fallback with the neighbour is lower
            if (p2.Zones.Any(z => z.TrackId == 101)) return "the Perry is in the zones of stage 2";
            if (!(p2.PIfAlive >= 0 && p2.PIfAlive < p2.PSunk)) return "PIfAlive " + p2.PIfAlive + " vs " + p2.PSunk;
            if (!(cp.TStage2 > cp.T)) return "TStage2 " + cp.TStage2 + " T " + cp.T;
            if (cp.MissilesUsed != p1.N + p2.N) return "missiles used";
            if (!cp.Warnings.Contains("clear_pause:101")) return "warning";
            // a Probe of the same kind is the variant of Variants
            var vs = Variants(SinkInput(Squadron(6), new List<StrikeShip> { kidd, perry }, 100));
            Dump("clear-v", vs);
            var vc = vs.FirstOrDefault(x => x.Kind == "clear_pause");
            var pr = Probe(SinkInput(Squadron(6), new List<StrikeShip> { kidd, perry }, 100), "clear_pause");
            if (pr == null || pr.MissilesUsed != cp.MissilesUsed || Math.Abs(pr.PerTarget[1].P - p2.P) > 1e-12) return "Probe clear_pause";
            // not enough aircraft for stage 2: null
            if (ClearPause(SinkInput(Squadron(2), new List<StrikeShip> { kidd, perry }, 100), 101) != null) return "no aircraft left for stage 2";
            // stage 1 red: the Perry cannot be sunk with what is there
            if (ClearPause(SinkInput(Squadron(1, 2), new List<StrikeShip> { kidd, perry }, 100), 101) != null) return "stage 1 red";
            // breach: never
            if (ClearPause(SinkInput(Squadron(6), new List<StrikeShip> { kidd, perry }, 100, "breach"), 101) != null) return "breach has no clear_pause";
            var inB = SinkInput(Squadron(6), new List<StrikeShip> { kidd, perry }, 100, "breach");
            if (Variants(inB).Any(x => x.Kind == "clear_pause" || x.Kind == "clearing")) return "breach variants";
            // NoClearPause
            var inN = SinkInput(Squadron(6), new List<StrikeShip> { kidd, perry }, 100); inN.NoClearPause = true;
            if (Variants(inN).Any(x => x.Kind == "clear_pause") || inN.ClearingNote != "none") return "NoClearPause";
            // the rule of the card: dearer than the direct strike by more than ClearShowFrac and the neighbour is not a target of its own -> hidden
            var inH = SinkInput(Squadron(6), new List<StrikeShip> { kidd, perry }, 100);
            vs = Variants(inH);
            var direct = vs.FirstOrDefault(x => x.Kind == "min_missiles");
            if (direct == null || cp.MissilesUsed <= direct.MissilesUsed * StrikeCalib.ClearShowFrac) return "the scenario must make clear_pause dearer than the direct strike " + cp.MissilesUsed + " vs " + (direct == null ? -1 : direct.MissilesUsed);
            if (vs.Any(x => x.Kind == "clear_pause") || inH.ClearingNote != "hidden") return "a dear clear_pause is hidden " + inH.ClearingNote;
            // the neighbour is a target of its own: the card stays, and it is a variant of sink with a green stage 2
            var inS = SinkInput(Squadron(6), new List<StrikeShip> { kidd, perry }, 100, "sink"); inS.Target.Mode = "spread"; inS.Target.Ships.AddRange(new[] { 100, 101 });
            vs = Variants(inS);
            var shown = vs.FirstOrDefault(x => x.Kind == "clear_pause");
            if (shown == null || inS.ClearingNote != "shown" || shown.Red) return "a listed neighbour keeps the card " + inS.ClearingNote + " " + string.Join(",", vs.Select(x => x.Kind));
            if (shown.PerTarget.Count != 2 || shown.Assignments.Any(a => a.Stage == 0)) return "the stages of the shown card";
            if (vs.Any(x => x.Kind == "clearing")) return "no v1 clearing card in any line";
            // the budget: Variants with clear_pause over a Kidd, a Perry and a Ticonderoga (warm cache) in less than 20 ms (the best of 3)
            var tico = Killable(Ship(102, "tico", 40.6, 10, 9600, 90), 9600, "minor");
            double best = double.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                var w = System.Diagnostics.Stopwatch.StartNew();
                Variants(SinkInput(Squadron(8), new List<StrikeShip> { kidd, perry, tico }, 100));
                w.Stop(); best = Math.Min(best, w.Elapsed.TotalMilliseconds);
            }
            if (best > 20) return "budget " + best.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
            if (Environment.GetEnvironmentVariable("STRIKE_DUMP") != null) Console.WriteLine("strike clear plan ms=" + best.ToString("0.0", CultureInfo.InvariantCulture));
            // a lone neighbour that is not on the track: no clear_pause
            var far = PerryKillable(102, 41, 11.5);
            var inF = SinkInput(Squadron(6), new List<StrikeShip> { kidd, far }, 100);
            if (Variants(inF).Any(x => x.Kind == "clear_pause") || inF.ClearingNote != "off") return "a neighbour off the track " + inF.ClearingNote;
            return null;
        }

        // ---- T-342 phase A: the goal sink (the kill model) and the late second row of a mixed load
        private static StrikeAircraft HarpoonPlane(int uid, double lat, int count)
        {
            var a = Plane(uid, "usn_a-6e", lat, 10, "usn_agm-84d", count, 3.5, 118);
            foreach (var m in a.Missiles) { m.WarheadPower = 45; m.Warhead = "normal"; }
            return a;
        }

        private static StrikeShip Killable(StrikeShip s, double disp, string armor)
        {
            s.KillBudget = VoiceBotBridge.Ui.HitEstimateMath.Budget(new VoiceBotBridge.Ui.HeClass { Kind = VoiceBotBridge.Ui.HeTargetKind.Ship, Displacement = disp, Armor = armor });
            return s;
        }

        private static StrikePlanInput SinkInput(IList<StrikeAircraft> air, IList<StrikeShip> ships, int main, string goal = "sink")
        {
            var i = Input(air, ships, main);
            i.Goal = goal; i.DamageBonus = 0.25; i.BonusKnown = true; i.KillThreshold = 0.8;
            return i;
        }

        private static string SelfTestSink()
        {
            // (a) a Kidd and 16 Harpoon in the air: min_missiles with MSink ~12 (sigma depends on M), P(sunk) >= 0.8, green
            var air = new List<StrikeAircraft>(); for (int i = 1; i <= 4; i++) air.Add(HarpoonPlane(i, 39.5 - 0.05 * i, 4));
            var kidd = Killable(Ship(100, "kidd", 41, 10, 9630, 90), 9630, "minor");
            var vs = Variants(SinkInput(air, new List<StrikeShip> { kidd }, 100));
            Dump("sink-a", vs);
            if (vs.Count == 0 || vs[0].Kind != "min_missiles" || vs[0].Red || vs[0].Goal != "sink") return "(a) first variant " + (vs.Count > 0 ? vs[0].Kind : "none");
            var tp = vs[0].PerTarget[0];
            if (tp.PSunk < 0.8 || tp.MSink < 8 || tp.MSink > 16 || tp.N != tp.MSink || tp.Goal != "sink" || tp.H != 3 || tp.H1 != 4) return "(a) plan msink=" + tp.MSink + " n=" + tp.N + " p=" + tp.PSunk + " h=" + tp.H + "/" + tp.H1;
            if (tp.P != tp.PSunk || tp.PBreach < tp.PSunk || tp.Sigma <= 0 || tp.Budget < 300 || tp.LossPerHit < 100 || tp.Short) return "(a) fields " + tp.PBreach + " " + tp.Sigma + " " + tp.Budget + " " + tp.LossPerHit;
            if (vs[0].TargetsHit != 1 || vs[0].Warnings.Any(w => w.StartsWith("kill_", StringComparison.Ordinal))) return "(a) hit / warnings " + string.Join(";", vs[0].Warnings);
            // the same input as breach is the plan of v1 (8 missiles)
            var vb = Variants(SinkInput(air, new List<StrikeShip> { kidd }, 100, "breach"));
            if (vb.Count == 0 || vb[0].MissilesUsed != 8 || vb[0].Goal != "breach" || vb[0].Red || vb[0].PerTarget[0].Goal != "breach" || vb[0].PerTarget[0].PSunk != -1 || vb[0].PerTarget[0].MSink != -1) return "(a) breach is v1 " + (vb.Count > 0 ? vb[0].MissilesUsed.ToString() : "none");
            // a hit before: the plan needs fewer missiles
            var kiddHit = Killable(Ship(100, "kidd", 41, 10, 9630, 90), 9630, "minor"); kiddHit.PriorLoss = 2 * 123.75; kiddHit.PriorHits = 2;
            var vh = Variants(SinkInput(air, new List<StrikeShip> { kiddHit }, 100));
            if (vh.Count == 0 || vh[0].PerTarget[0].MSink >= tp.MSink || vh[0].PerTarget[0].BudgetLeft >= tp.BudgetLeft) return "(a) the prior hits lower the need";
            // (b) a Ticonderoga and 16 Harpoon: the first variant is the red damage_only; no green min_missiles
            var tico = Killable(Ship(120, "tico", 41, 10, 9600, 90), 9600, "minor");
            vs = Variants(SinkInput(air, new List<StrikeShip> { tico }, 120));
            Dump("sink-b", vs);
            if (vs.Count == 0 || vs[0].Kind != "damage_only" || !vs[0].Red) return "(b) first variant " + (vs.Count > 0 ? vs[0].Kind : "none");
            if (vs.Any(x => x.Kind == "min_missiles")) return "(b) no green min_missiles";
            var tb = vs[0].PerTarget[0];
            if (tb.N != 16 || vs[0].MissilesUsed != 16 || tb.PSunk >= 0.8 || tb.PBreach < 0.9 || !tb.Short) return "(b) n=" + tb.N + " psunk=" + tb.PSunk + " pbreach=" + tb.PBreach;
            var sw = vs[0].Warnings.FirstOrDefault(w => w.StartsWith("kill_short n=", StringComparison.Ordinal));
            int shortN = sw == null ? -1 : int.Parse(sw.Substring("kill_short n=".Length).Split(' ')[0], CultureInfo.InvariantCulture);
            if (shortN < 2 || shortN > 8 || tb.ShortBy != shortN || tb.MSink < 17 || tb.MSink != 16 + shortN) return "(b) kill_short " + sw + " msink=" + tb.MSink;
            if (vs[0].Score < 1e6) return "(b) the red variant is ranked below a green one";
            // (c) an unread class: kill_unknown, the variant is red, no number of missiles
            var unk = Ship(130, "kidd", 41, 10, 9630, 90);   // KillBudget = -1
            vs = Variants(SinkInput(air, new List<StrikeShip> { unk }, 130));
            if (vs.Count == 0 || vs[0].Kind != "damage_only" || !vs[0].Red || !vs[0].Warnings.Contains("kill_unknown target=130") || vs[0].PerTarget[0].PSunk != -1) return "(c) kill_unknown " + (vs.Count > 0 ? vs[0].Kind + ";" + string.Join(";", vs[0].Warnings) : "none");
            // (d) many aircraft: spread keeps max_targets only when both reach the threshold; the budget of the plan holds (3 types, M <= 64)
            var big = new List<StrikeAircraft>(); for (int i = 1; i <= 8; i++) big.Add(HarpoonPlane(i, 39.5 - 0.02 * i, 8));
            var k2 = Killable(Ship(101, "kidd", 41, 10.9, 9630, 90), 9630, "minor");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            vs = Variants(SinkInput(big, new List<StrikeShip> { kidd, k2, tico }, 100));
            watch.Stop();
            if (vs.Count == 0 || vs[0].Red) return "(d) 64 missiles are enough for a Kidd";
            if (watch.Elapsed.TotalMilliseconds > 300) return "(d) budget " + watch.Elapsed.TotalMilliseconds;
            return null;
        }

        private static StrikeAircraft Tu16k(double kn, double fuel = 1)
        {
            var a = Plane(1, "wp_tu-16k", 39.0, 10, "wp_as-6", 2, 20, 200, 1000, 3000, "none");
            a.CruiseKn = kn; a.FuelFrac = fuel;
            var m2 = new StrikeMissile { Ammo = "wp_as-2", Name = "wp_as-2", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = 2, MinNm = 10, MaxNm = 70, SpeedKn = 1100, CruiseAltFt = 3000, MinLaunchAltFt = 200, FrontalM2 = 0.08, MassKg = 520, Operable = true };
            a.Missiles.Add(m2);
            StrikeAirMath.Classify(new[] { a });
            return a;
        }

        /// <summary>T-349: the real Tu-16K of the e1b stand: 2 x AS-6 (long range) + 2 x AS-2 (about 100 nm closer), 226 kn, the second row cannot make it.</summary>
        private static string SelfTestMixE1b()
        {
            var kidd = Ship(101, "kidd", 43, 10, 8300, 170);   // SAM 170 nm: the AS-6 launches at 0.9 x 180 = 162 nm, the AS-2 at 0.9 x 70 = 63 nm (gap 99 nm)
            Func<double, double, double, StrikeVariant> mk = (kn, maxS, as2Kn) =>
            {
                var a = Plane(1, "wp_tu-16k", 39.0, 10, "wp_as-6", 2, 20, 180, 1000, 3000, "none");
                a.CruiseKn = kn; a.FuelFrac = 1;
                a.Missiles.Add(new StrikeMissile { Ammo = "wp_as-2", Name = "wp_as-2", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MidCourse = "none", Count = 2, MinNm = 10, MaxNm = 70, SpeedKn = as2Kn, CruiseAltFt = 3000, MinLaunchAltFt = 200, FrontalM2 = 0.08, MassKg = 520, Operable = true });
                StrikeAirMath.Classify(new[] { a });
                var inp = Input(new List<StrikeAircraft> { a }, new List<StrikeShip> { kidd }, 101);
                inp.P.MixShiftMaxS = maxS;
                return Variants(inp)[0];
            };
            foreach (double kn in new[] { 226.0, 430.0 })
            {
                var v = mk(kn, 120, 1100);
                if (!v.Warnings.Any(w => w.StartsWith("mix_late uid=1 ammo=wp_as-2", StringComparison.Ordinal))) return "no mix_late at " + kn + " kn: " + string.Join(";", v.Warnings) + " rows " + v.Assignments.Count;
                if (v.Assignments.Any(a => a.Ammo == "wp_as-2") || v.Warnings.Any(w => w.StartsWith("mixed_shift", StringComparison.Ordinal))) return "AS-2 row or mixed_shift left at " + kn + " kn";
                if (v.PerTarget[0].N != v.Assignments.Sum(a => a.Salvo)) return "target not recounted at " + kn + " kn";
            }
            var ctl = mk(2000, 300, 1100);
            if (ctl.Assignments.Count != 2 || ctl.Warnings.Any(w => w.StartsWith("mix_late", StringComparison.Ordinal))) return "control: both rows expected " + ctl.Assignments.Count + " " + string.Join(";", ctl.Warnings);
            double gap = ctl.Assignments.First(a => a.Order == 0).LaunchNm - ctl.Assignments.First(a => a.Order == 1).LaunchNm;
            if (gap < 95 || gap > 105) return "launch gap " + gap.ToString("0.0", CultureInfo.InvariantCulture) + " nm";
            return null;
        }

        private static string SelfTestMix()
        {
            // both rows are used: a Kidd target with 4 missiles in the air (the farther AS-6 first, then the AS-2)
            var big = Ship(101, "kidd", 41, 10, 8300, 90);
            Func<double, double, double, StrikeVariant> mk = (kn, maxS, fuel) =>
            {
                var inp = Input(new List<StrikeAircraft> { Tu16k(kn, fuel) }, new List<StrikeShip> { big }, 101);
                inp.P.MixShiftMaxS = maxS;
                return Variants(inp)[0];
            };
            var vAll = mk(400, 100000, 1);
            var shiftW = vAll.Warnings.FirstOrDefault(w => w.StartsWith("mixed_shift uid=1 s=", StringComparison.Ordinal));
            if (vAll.Assignments.Count != 2) return "mix: two rows expected " + vAll.Assignments.Count + " " + string.Join(";", vAll.Warnings);
            if (shiftW == null) return "mix: no deficit in the geometry of the test: " + string.Join(";", vAll.Warnings);
            double d = double.Parse(shiftW.Substring("mixed_shift uid=1 s=".Length), CultureInfo.InvariantCulture);
            if (d < 2) return "mix: the deficit is too small to test " + d;
            // a row that is in time is not touched
            var vFast = mk(2000, 0, 1);   // a fast aircraft covers the leg
            if (vFast.Assignments.Count != 2 || vFast.Warnings.Any(w => w.StartsWith("mixed_shift", StringComparison.Ordinal) || w.StartsWith("mix_late", StringComparison.Ordinal))) return "mix: a row in time is untouched: " + string.Join(";", vFast.Warnings);
            // the shift is within the limit: T of the late row moves by the deficit, the row stays
            var vShift = mk(400, d + 5, 1);
            var o1 = vShift.Assignments.First(a => a.Order == 1); var o0 = vShift.Assignments.First(a => a.Order == 0);
            if (vShift.Assignments.Count != 2 || !vShift.Warnings.Any(w => w.StartsWith("mixed_shift uid=1 s=", StringComparison.Ordinal)) || Math.Abs((o1.TArrive - o0.TArrive) - d) > 1.5 || vShift.Warnings.Any(w => w.StartsWith("mix_late", StringComparison.Ordinal))) return "mix: shift " + string.Join(";", vShift.Warnings);
            if (o0.LaunchKn != 400 || o1.LaunchKn != 400) return "mix: LaunchKn " + o0.LaunchKn;
            // the deficit is over the limit: the row is excluded in advance with a warning and the target is recounted
            var vLate = mk(400, d - 1, 1);
            if (vLate.Assignments.Count != 1 || !vLate.Warnings.Any(w => w.StartsWith("mix_late uid=1 ammo=wp_as-2", StringComparison.Ordinal)) || vLate.Warnings.Any(w => w.StartsWith("mixed_shift", StringComparison.Ordinal))) return "mix: late " + vLate.Assignments.Count + " " + string.Join(";", vLate.Warnings);
            if (vLate.PerTarget[0].N != vLate.Assignments.Sum(a => a.Salvo) || vLate.MissilesUsed >= vAll.MissilesUsed) return "mix: the target is recounted " + vLate.PerTarget[0].N + " vs " + vAll.MissilesUsed;
            // MixShiftMaxS = 0: always an exclusion
            var vZero = mk(400, 0, 1);
            if (vZero.Assignments.Count != 1 || !vZero.Warnings.Any(w => w.StartsWith("mix_late", StringComparison.Ordinal))) return "mix: zero limit " + string.Join(";", vZero.Warnings);
            // the fuel does not allow the longer way: an exclusion even inside the limit
            var vFuel = mk(400, d + 5, 0.03);
            if (vFuel.Assignments.Count != 1 || !vFuel.Warnings.Any(w => w.StartsWith("mix_late", StringComparison.Ordinal))) return "mix: fuel " + vFuel.Assignments.Count + " " + string.Join(";", vFuel.Warnings);
            return null;
        }
    }
}
