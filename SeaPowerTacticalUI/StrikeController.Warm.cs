using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase C (review of phase B, finding 3): the first simulated time of flight of a missile is slow (~50 ms cold). One call per missile type is made in the tick (WarmPerTick calls),
    // before the first plan, so that the plan itself is cheap; Plan finishes what is missing. The cache and the sim/rough scale are in StrikeTofCache.
    internal sealed partial class StrikeController
    {
        private readonly StrikeTofCache _tofs = new StrikeTofCache();
        private readonly HashSet<string> _warmFailed = new HashSet<string>();

        public StrikeTofCache Tofs { get { return _tofs; } }

        // T-342: a new warm-up on the current range (a held row, a target that went away from the planned range): queued by the execution, made in Warm (never in the step; no EstimateShot in the tick)
        private sealed class RewarmReq { public int Uid, TargetId; public string Ammo; public double RangeNm, AltFt, Kn; public string Key; }
        private readonly Queue<RewarmReq> _rewarm = new Queue<RewarmReq>();
        private readonly HashSet<string> _rewarmKeys = new HashSet<string>();

        internal int RewarmQueued { get { return _rewarm.Count; } }

        internal void RequestRewarm(int uid, string ammo, double rangeNm, double altFt, double kn, int targetId)
        {
            if (ammo == null || !_tofs.NeedRewarm(ammo, rangeNm, _p.TofRewarmNm)) return;
            string key = ammo + "|" + ((long)Math.Floor(rangeNm / Math.Max(1, _p.TofRewarmNm))).ToString(CultureInfo.InvariantCulture);
            if (!_rewarmKeys.Add(key)) return;
            _rewarm.Enqueue(new RewarmReq { Uid = uid, TargetId = targetId, Ammo = ammo, RangeNm = rangeNm, AltFt = altFt, Kn = kn, Key = key });
        }

        private void RewarmOne(RewarmReq q)
        {
            // the key stays: a bucket of the range is asked once per mission (a failed sim is not asked again every tick)
            var a = Air(q.Uid); var ship = Ship(q.TargetId);
            var m = a == null ? null : a.Missiles.FirstOrDefault(x => x.Ammo == q.Ammo && StrikeAirMath.IsStrikeMissile(x));
            if (a == null || m == null || ship == null) return;
            var sp = new GeoPt(ship.Lat, ship.Lon);
            var ap = new GeoPt(a.Lat, a.Lon);
            double brg = AswMath.DistNm(sp, ap) < 1e-6 ? 180 : AswMath.BearingDeg(sp, ap);
            var from = AswMath.Offset(sp, brg, q.RangeNm);
            double rough = StrikeTotMath.RoughTofS(q.RangeNm, m.SpeedKn > 0 ? m.SpeedKn : 540), sim = 0;
            bool ok = false;
            try { ok = _g.Tof(q.Uid, m, from.Lat, from.Lon, q.AltFt, q.Kn, ship, sp.Lat, sp.Lon, out sim); } catch (Exception) { ok = false; }
            if (ok && sim > 0) _tofs.Put(q.Ammo, q.RangeNm, ship.SpeedKn, q.AltFt, sim, rough);
            _log("[Strike] strike tof_rewarm uid=" + I(q.Uid) + " ammo=" + q.Ammo + " nm=" + F1(q.RangeNm) + " sim=" + (ok && sim > 0 ? F1(sim) : "none") + " rough=" + F1(rough));
        }

        private List<string> MemberAmmos()
        {
            var res = new List<string>();
            foreach (var g in _groups)
                foreach (int uid in g.Members)
                {
                    var a = Air(uid);
                    if (a == null) continue;
                    foreach (var m in a.Missiles) if (StrikeAirMath.IsStrikeMissile(m) && m.Ammo != null && !res.Contains(m.Ammo)) res.Add(m.Ammo);
                }
            return res;
        }

        private bool _jitDone;

        /// <summary>The first plan of a session was 40-50 ms in the live checks, a repeated one 0.4 ms: the cost is the JIT of the plan code. One synthetic plan in the tick (no game, no state) pays it before the player asks.</summary>
        private void JitWarm()
        {
            long t0 = TimerStart();
            var m = new StrikeMissile { Ammo = "jit", Name = "jit", Type = "Missile", TargetType = "ASuW", Guidance = "ActiveRadarHoming", MidCourse = "none", Count = 4, MinNm = 3.5, MaxNm = 65, SpeedKn = 540, CruiseAltFt = 50, MinLaunchAltFt = 200, FrontalM2 = 0.08, MassKg = 520, Operable = true };
            var a = new StrikeAircraft { Uid = 1, Type = "jit", ObjType = "Aircraft", Airborne = true, Lat = 10.0, Lon = 10.0, CruiseKn = 450, CruiseAltFt = 25000, AltFt = 25000, FuelFrac = 1, EnduranceS = 18000 };
            a.Missiles.Add(m);
            StrikeAirMath.Classify(new[] { a });
            var tgt = new StrikeShip { TrackId = 1, Lat = 11.0, Lon = 10.0, CourseDeg = 90, SpeedKn = 15, DisplacementT = 8000, SamMaxNm = 40, RadarFt = 100, Side = "hostile", Hostile = true, Identified = true, Role = "AAW", ClassName = "jit",
                                   Ad = new StrikeAd { Ksub = 8.8, Ssub = 27.7, Ksup = 5.1, Ssup = 12.2, Source = "cache" } };
            var other = new StrikeShip { TrackId = 2, Lat = 11.0, Lon = 10.2, CourseDeg = 90, SpeedKn = 15, DisplacementT = 4000, SamMaxNm = 20, RadarFt = 100, Side = "hostile", Hostile = true, Identified = true, Role = "AAW", ClassName = "jit",
                                     Ad = new StrikeAd { Ksub = 3.1, Ssub = 9.5, Ksup = 1.9, Ssup = 4.4, Source = "cache" } };
            var set = new StrikeTargetSet { Mode = "spread", MainId = 1 };
            set.Ships.Add(1); set.Ships.Add(2);
            var inp = new StrikePlanInput { Air = new List<StrikeAircraft> { a }, Ships = new List<StrikeShip> { tgt, other }, Target = set, From = new GeoPt(10.0, 10.0), Now = 0, P = _p, IsLand = p => false };
            StrikePlanMath.Variants(inp);
            _log("[Strike] strike warm jit ms=" + F1(TimerMs(t0)));
        }

        private void Warm(double t)
        {
            if (_groups.Count == 0) return;
            if (!_jitDone) { _jitDone = true; try { JitWarm(); } catch (Exception) { } return; }
            var need = _tofs.NeedWarm(MemberAmmos().Where(a => !_warmFailed.Contains(a)));
            int left = Math.Max(1, _p.WarmPerTick);
            bool warmed = false;
            foreach (string ammo in need)
            {
                if (left-- <= 0) break;
                if (WarmOne(ammo)) warmed = true;   // false = nothing to ask with (no target ship known yet): no call was made, the slot stays free for a plan
            }
            while (left > 0 && _rewarm.Count > 0) { left--; RewarmOne(_rewarm.Dequeue()); warmed = true; }
            // T-342 C: the slot of the tick is one cold call OR one queued plan, never both
            if (!warmed && _replanQ.Count > 0) ServeReplan(t);
        }

        /// <summary>One simulated flight at a typical range (0.35 and 0.7 of the maximum) against a standing target, at the launch altitude and speed of the plan. false = nothing to ask with (no target ship known yet: tried again later).</summary>
        private bool WarmOne(string ammo)
        {
            StrikeAircraft a = null; StrikeMissile m = null;
            foreach (var g in _groups)
            {
                foreach (int uid in g.Members)
                {
                    var x = Air(uid);
                    var mm = x == null ? null : x.Missiles.FirstOrDefault(q => q.Ammo == ammo && StrikeAirMath.IsStrikeMissile(q));
                    if (mm != null) { a = x; m = mm; break; }
                }
                if (a != null) break;
            }
            if (a == null) return false;
            var ship = Hostiles().FirstOrDefault(s => s.Ad.Ksub > 0) ?? Hostiles().FirstOrDefault();
            if (ship == null) return false;
            var sp = new GeoPt(ship.Lat, ship.Lon);
            double ms = 0, altFt = m.MinLaunchAltFt > 0 ? m.MinLaunchAltFt : a.CruiseAltFt;   // T-342: the altitude and the speed the launch is planned at (the plan asks the cache the same way)
            int got = 0;
            foreach (double frac in new[] { 0.35, 0.7 })   // two ranges: the scale of the missile is their median
            {
                double r = frac * (m.MaxNm > 0 ? m.MaxNm : 60);
                var from = AswMath.Offset(sp, 180, r);
                double rough = StrikeTotMath.RoughTofS(r, m.SpeedKn > 0 ? m.SpeedKn : 540), sim = 0;
                long t0 = TimerStart();
                bool ok = false;
                try { ok = _g.Tof(a.Uid, m, from.Lat, from.Lon, altFt, a.CruiseKn, ship, sp.Lat, sp.Lon, out sim); } catch (Exception) { ok = false; }
                ms += TimerMs(t0);
                if (!ok || !(sim > 0)) continue;
                got++;
                _tofs.Put(ammo, r, 0, altFt, sim, rough);
                _log("[Strike] strike warm ammo=" + ammo + " r_nm=" + F1(r) + " sim=" + F1(sim) + " rough=" + F1(rough));
                TofCheck(ammo, sim, rough);
            }
            if (got == 0) { _warmFailed.Add(ammo); _log("[Strike] strike warm ammo=" + ammo + " -> no_sim ms=" + F1(ms)); return true; }
            _log("[Strike] strike warm ammo=" + ammo + " ms=" + F1(ms) + " scale=" + F1(_tofs.Scale(ammo) * 1000) + "e-3");
            return true;
        }

        /// <summary>The rough formula against the simulated value: more than 5 % apart is a line in the log, not an error.</summary>
        private void TofCheck(string ammo, double sim, double rough)
        {
            if (rough <= 0) return;
            double d = Math.Abs(sim / rough - 1) * 100;
            if (d > 5) _log("[Strike] strike tof_check ammo=" + ammo + " sim=" + F1(sim) + " rough=" + F1(rough) + " d_pct=" + F1(d));
        }
    }
}
