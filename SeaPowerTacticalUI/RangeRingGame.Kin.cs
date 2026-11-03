using System;
using System.Collections;
using System.Diagnostics;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-321 / ADR-0125: the game's own arithmetic for the rings of the PLAYER'S OWN weapons. The launcher check of the game compares
    /// Vector3.Distance (world units, not true miles) with MaxRangeRough (kinematics) / EffectiveMaxLaunchRange (torpedoes) / the nominal range.
    /// Every internal Rr* method with a unit id starts with the RrIsOwnSide guard. Every call into the game runs on the Unity main thread only (the
    /// kinematic simulator reads the launcher transform): the caller (RangeRingFeature.Update) takes at most one such call per frame. A foreign
    /// unit is never touched: the target of a verdict is the player's TRACK (position and velocity of the track).</summary>
    internal sealed class RrKinStats { public int Calls; public double MsSum, MsMax; }

    internal sealed partial class GameApi
    {
        internal readonly RrKinStats RrKinTotals = new RrKinStats();

        private void RrKinTime(long t0)
        {
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            RrKinTotals.Calls++; RrKinTotals.MsSum += ms; if (ms > RrKinTotals.MsMax) RrKinTotals.MsMax = ms;
        }

        private object RrKinFindAp(object u, string ammo)
        {
            var obp = Get(u, RadarSpecs.ObjObp);
            if (obp == null || ammo == null) return null;
            foreach (var spec in new[] { RangeRingSpecs.ParLauncherGroups, RangeRingSpecs.ParGunGroups })
            {
                var dict = Get(obp, spec) as IDictionary;
                if (dict == null) continue;
                foreach (var group in dict.Values)
                {
                    var ap = group == null ? null : Get(group, RangeRingSpecs.GroupAp);
                    if (ap != null && ammo == Get(ap, RangeRingSpecs.AmmoFileName) as string) return ap;
                }
            }
            return null;
        }

        private static UnityEngine.Vector3 RrKinPos(object u) { return ((UnityEngine.Component)u).transform.position; }

        private readonly WorldCellCache _worldCache = new WorldCellCache();
        private readonly Stopwatch _worldClock = Stopwatch.StartNew();
        private bool _worldLoggedSet, _worldLoggedVal;
        private double _worldNextPrimeS = double.NegativeInfinity;
        private const double PrimeS = 2.0;
        private const double PrimeFastS = 0.25;   // T-356: until the world of the mission is decided
        private const double EquatorDeg = 8.0, ProbeOffDeg = 20.0;

        /// <summary>T-347/T-348: true = the game world is the LCC globe at the point (lon metric ~ cos(lat)); false = flat (stable) or unknown. Cached per RangeRingKin.CellKey
        /// (WorldCellCache: undecided cells and a failed metric are remembered too). Every change of the world is written to AswMath.WorldGlobe.</summary>
        internal bool WorldGlobeAt(double lat, double lon)
        {
            bool decided;
            return WorldMeasure(lat, lon, "at", out decided);
        }

        /// <summary>One cached measurement; decided = the cell holds an answer of the metric (then AswMath.WorldKnown is set). src goes to the [World] line.</summary>
        private bool WorldMeasure(double lat, double lon, string src, out bool decided)
        {
            decided = false;
            try
            {
                long key = RangeRingKin.CellKey(lat, lon);
                double now = _worldClock.Elapsed.TotalSeconds;
                bool cached;
                if (_worldCache.TryGet(key, now, out cached))
                {
                    decided = _worldCache.IsDecided(key);
                    if (decided) AswMath.WorldKnown = true;
                    return cached;
                }
                RingGeoMetric m;
                bool globe = false;
                bool ok = RrGameMetric(lat, lon, out m);
                if (ok) globe = AswMath.GlobeFromMetric(lat, m.LonNmPerDeg, m.LatNmPerDeg, out decided);
                if (_worldCache.Put(key, now, ok, decided, globe)) AswMath.WorldGlobe = globe;
                if (decided)
                {
                    AswMath.WorldKnown = true;
                    if (!_worldLoggedSet || _worldLoggedVal != globe)
                    {
                        _worldLoggedSet = true; _worldLoggedVal = globe;
                        try { string line = "[World] globe=" + (globe ? "on" : "off") + " lat=" + lat.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " src=" + src; var s = AswLogSink; if (s != null) s(line); else UnityEngine.Debug.Log(line); } catch { }
                    }
                }
                return decided ? globe : _worldCache.Last;
            }
            catch { return _worldCache.Last; }
        }

        /// <summary>T-356 (ADR-0135): the mission was entered (lifecycle order 0, before sar 20 / asw 30 / cap 40 / strike 42 restore the orders): the cells of the
        /// last mission are forgotten (a new mission may have another projection), the world is probed at once.</summary>
        internal void WorldEnter()
        {
            try
            {
                _worldCache.ResetCells();
                AswMath.WorldKnown = false;
                _worldLoggedSet = false;
                _worldNextPrimeS = double.NegativeInfinity;
                WorldProbe("enter");
            }
            catch { }
        }

        /// <summary>The mission was left: the world of the next one is not known yet (AswMath.WorldGlobe keeps the last value as the best guess).</summary>
        internal void WorldLeave() { AswMath.WorldKnown = false; }

        /// <summary>The first own unit; near the equator (|lat| below 8 deg: both worlds coincide) a second probe 20 deg away on the same meridian
        /// (LCC is conformal: lon/lat metric = cos(phi) at any point). true = the world is decided.</summary>
        private bool WorldProbe(string src)
        {
            if (MissionRef() == null) return false;
            foreach (var u in OwnUnits())
            {
                double lat, lon, h;
                if (u == null || !OwnPos(u, out lat, out lon, out h)) continue;
                bool decided;
                WorldMeasure(lat, lon, src, out decided);
                if (decided) return true;
                if (Math.Abs(lat) < EquatorDeg) WorldMeasure(lat >= 0 ? lat + ProbeOffDeg : lat - ProbeOffDeg, lon, "probe20", out decided);
                return decided;
            }
            return false;
        }

        /// <summary>T-348: primes AswMath.WorldGlobe once per PrimeS of real time in a mission from a point of the mission zone (the first own unit);
        /// T-356: every PrimeFastS until the world of the mission is decided. After that only cache hits. Main thread (TickMission).</summary>
        internal void WorldPrime(double realTimeS)
        {
            try
            {
                if (realTimeS < _worldNextPrimeS - PrimeS) _worldNextPrimeS = double.NegativeInfinity;   // the clock went back
                if (realTimeS < _worldNextPrimeS) return;
                _worldNextPrimeS = realTimeS + (AswMath.WorldKnown ? PrimeS : PrimeFastS);
                WorldProbe("prime");
            }
            catch { }
        }

        /// <summary>World units per degree of the game at a location (not a unit): three GeoPosition.ToUnity samples (centre, +SampleDeg east, +SampleDeg north).
        /// Stable: plate carree, 1 degree = 60 nm both ways. Beta: the LCC grid (the length of the step is taken, so a rotated grid is fine).</summary>
        internal bool RrGameMetric(double lat, double lon, out RingGeoMetric m)
        {
            m = default(RingGeoMetric);
            if (!Present(RangeRingSpecs.GeoNew) || !Present(SmartAttackSpecs.GeoToUnity)) return false;
            try
            {
                double s = RangeRingKin.SampleDeg;
                var c = (UnityEngine.Vector3)Call(New(RangeRingSpecs.GeoNew, lat, lon, 0.0), SmartAttackSpecs.GeoToUnity);
                var e = (UnityEngine.Vector3)Call(New(RangeRingSpecs.GeoNew, lat, lon + s, 0.0), SmartAttackSpecs.GeoToUnity);
                var n = (UnityEngine.Vector3)Call(New(RangeRingSpecs.GeoNew, lat + s, lon, 0.0), SmartAttackSpecs.GeoToUnity);
                const double k = RangeRingMath.NmPerUnity;
                m = RangeRingKin.MetricFromSamples((e.x - c.x) * k, (e.z - c.z) * k, (n.x - c.x) * k, (n.z - c.z) * k, s);
                return m.Valid;
            }
            catch { m = default(RingGeoMetric); return false; }
        }

        /// <summary>How the game counts the range of one ammunition of an own unit; null = not own / no such ammunition. Reads the kind of formula, the typical
        /// target is read by the probe.</summary>
        internal KinAmmoInfo RrKinAmmo(int uid, string ammo, bool allowAlly)
        {
            var u = RrFindUnit(uid);
            if (!RrIsOwnSide(u, allowAlly)) return null;
            try
            {
                var ap = RrKinFindAp(u, ammo);
                if (ap == null) return null;
                var ranges = Get(ap, RangeRingSpecs.AmmoLaunchRanges);
                var info = new KinAmmoInfo
                {
                    Name = ammo,
                    Type = RrOwnAmmoType(ap),
                    NominalNm = RrComponent(ranges, RangeRingSpecs.VecY) * RangeRingMath.NmPerUnity,
                    MinNm = RrComponent(ranges, RangeRingSpecs.VecX) * RangeRingMath.NmPerUnity,
                    EvasiveTypical = RrTarget(Get(ap, RangeRingSpecs.AmmoTargetType)) == RingTarget.Aaw,
                };
                var kin = Present(RangeRingSpecs.ApKinematics) ? Get(ap, RangeRingSpecs.ApKinematics) : null;
                info.Kinematic = kin != null && kin.ToString() != "None" && info.Type != RingAmmoType.Torpedo
                    && Present(RangeRingSpecs.ApMaxRangeRough) && Present(RangeRingSpecs.ApTypicalTargetAlt) && Present(RangeRingSpecs.ApTypicalTargetVelocity);
                var type = StrOrNull(Get(u, OverviewSpecs.ObjType));
                info.AirLaunch = type == "Aircraft" || type == "Helicopter";
                var pos = RrKinPos(u);
                info.AltFt = pos.y * SaFtPerUnity;
                info.SpeedKn = info.AirLaunch ? Math.Abs(D(Get(u, RangeRingSpecs.ObjVelocityKn)) ?? 0) : 0;
                return info;
            }
            catch { return null; }
        }

        /// <summary>One MaxRangeRough call of the game for the typical target of the ammunition placed dNm east of the launcher (the target flies at it, as
        /// in MaxRangeRough(launcher)). maxNm = the game's answer, distNm = the 3D distance launcher-target. In range = maxNm &gt;= distNm.</summary>
        internal bool RrKinProbe(int uid, string ammo, double dNm, bool allowAlly, out double maxNm, out double distNm)
        {
            maxNm = double.NaN; distNm = double.NaN;
            var u = RrFindUnit(uid);
            if (!RrIsOwnSide(u, allowAlly)) return false;
            try
            {
                var ap = RrKinFindAp(u, ammo);
                if (ap == null || !Present(RangeRingSpecs.ApMaxRangeRough)) return false;
                float alt = Convert.ToSingle(Get(ap, RangeRingSpecs.ApTypicalTargetAlt) ?? 0f), vel = Convert.ToSingle(Get(ap, RangeRingSpecs.ApTypicalTargetVelocity) ?? 0f);
                bool evasive = RrTarget(Get(ap, RangeRingSpecs.AmmoTargetType)) == RingTarget.Aaw;
                var from = RrKinPos(u);
                var target = new UnityEngine.Vector3(from.x + (float)(dNm / RangeRingMath.NmPerUnity), alt, from.z);
                var tv = new UnityEngine.Vector3(-vel, 0f, 0f);
                long t0 = Stopwatch.GetTimestamp();
                var r = Call(ap, RangeRingSpecs.ApMaxRangeRough, u, target, tv, evasive);
                RrKinTime(t0);
                if (r == null) return false;
                maxNm = Convert.ToDouble(r) * RangeRingMath.NmPerUnity;
                distNm = UnityEngine.Vector3.Distance(from, target) * RangeRingMath.NmPerUnity;
                return !double.IsNaN(maxNm);
            }
            catch { return false; }
        }

        /// <summary>EffectiveMaxLaunchRange of the ammunition (torpedoes: the range of the chosen speed); NaN = error / not own.</summary>
        internal double RrEffectiveNm(int uid, string ammo, bool allowAlly)
        {
            var u = RrFindUnit(uid);
            if (!RrIsOwnSide(u, allowAlly)) return double.NaN;
            try
            {
                var ap = RrKinFindAp(u, ammo);
                if (ap == null || !Present(RangeRingSpecs.ApEffectiveMax)) return double.NaN;
                long t0 = Stopwatch.GetTimestamp();
                var r = Call(ap, RangeRingSpecs.ApEffectiveMax, u);
                RrKinTime(t0);
                return r == null ? double.NaN : Convert.ToDouble(r) * RangeRingMath.NmPerUnity;
            }
            catch { return double.NaN; }
        }

        /// <summary>The launcher check of the game against the player's TRACK: d = world distance to the track position, max = MaxRangeRough with the track
        /// velocity (the game takes the real target; the track is all the player knows, ADR-0055), min = the minimum launch range. Reads of the track:
        /// position, velocity, air or not; nothing else.</summary>
        internal bool RrKinVsTrack(int uid, string ammo, int trackId, bool allowAlly, out double distNm, out double maxNm, out double minNm)
        {
            distNm = double.NaN; maxNm = double.NaN; minNm = double.NaN;
            var u = RrFindUnit(uid);
            if (!RrIsOwnSide(u, allowAlly)) return false;
            try
            {
                var ap = RrKinFindAp(u, ammo);
                var v = RrVehicle(trackId);
                if (ap == null || v == null || !Present(RangeRingSpecs.ApMaxRangeRough) || !Present(SmartAttackSpecs.GeoToUnity)) return false;
                var geo = RxGet(v, ContactInfoSpecs.VehPosition);
                if (geo == null) return false;
                var pos = (UnityEngine.Vector3)Call(geo, SmartAttackSpecs.GeoToUnity);
                var velObj = Get(v, ContactInfoSpecs.VehVelocity);   // grid-ok: raw world vector goes back to the game (T-321)
                var vel = velObj is UnityEngine.Vector3 ? (UnityEngine.Vector3)velObj : UnityEngine.Vector3.zero;
                bool air = Kind(v) == "Air";
                var from = RrKinPos(u);
                long t0 = Stopwatch.GetTimestamp();
                var r = Call(ap, RangeRingSpecs.ApMaxRangeRough, u, pos, vel, air);
                RrKinTime(t0);
                if (r == null) return false;
                maxNm = Convert.ToDouble(r) * RangeRingMath.NmPerUnity;
                distNm = UnityEngine.Vector3.Distance(from, pos) * RangeRingMath.NmPerUnity;
                minNm = RrComponent(Get(ap, RangeRingSpecs.AmmoLaunchRanges), RangeRingSpecs.VecX) * RangeRingMath.NmPerUnity;
                return !double.IsNaN(maxNm);
            }
            catch { return false; }
        }
    }
}
