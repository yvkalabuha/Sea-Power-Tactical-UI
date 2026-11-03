using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075 item 2.3: the game side of the retreat helper (AirRetreatSteer). Main thread. Orders only: a route, a speed step, a height preset.
    // The route goes through the ASW game layer (the same waypoint path as the tactics, so the aircraft is not taken for "taken by the player"); an orbit is
    // a circle of waypoints for an aircraft (T-331), one waypoint for a helicopter (it hovers).
    internal sealed partial class GameApi
    {
        private AgSteerImpl _agSteer;

        internal IAirSteerGame AgSteerGame() { return _agSteer ?? (_agSteer = new AgSteerImpl(this)); }

        private sealed class AgSteerImpl : IAirSteerGame
        {
            private readonly GameApi _a;
            private readonly Dictionary<int, double[]> _mem = new Dictionary<int, double[]>();   // uid -> {speed step (1-based, -1 = unknown), desired altitude (-1 = unknown)}

            public AgSteerImpl(GameApi a) { _a = a; }

            private object Unit(int uid) { try { return uid < 0 ? null : _a.FindOwnUnit(uid); } catch { return null; } }

            public GeoPt? Pos(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    double lat, lon, h;
                    if (u != null && IsAlive(u) && _a.OwnPos(u, out lat, out lon, out h)) return new GeoPt(lat, lon);
                }
                catch { }
                return null;
            }

            public string Goto(int uid, IList<GeoPt> pts, int altFt)
            {
                var impl = _a._aswImpl;
                if (impl != null) { string err = impl.Goto(uid, pts, altFt); _a.WpqNotePlaced(uid, "retreat"); return err; }   // T-331: the retreat is the placer of this route
                var u = Unit(uid);
                return u == null ? "unit_not_found" : "unsupported";
            }

            /// <summary>T-331: an aircraft (racetrack) flies a real circle round the centre; a helicopter hovers over one point.</summary>
            public string Orbit(int uid, GeoPt center, double radiusNm, bool racetrack)
            {
                if (!racetrack) return Goto(uid, new List<GeoPt> { center }, 0);
                return Goto(uid, AirRetreatSteer.OrbitPts(center, radiusNm > 1 ? radiusNm : AirRetreatSteer.OrbitNm, (int)AirRetreatSteer.OrbitLaps), 0);
            }

            public int Throttle(int uid)
            {
                var u = Unit(uid);
                if (u == null) return -1;
                try { var t = D(_a.Call(u, CapSpecs.ObjGetTelegraph)); return t.HasValue ? (int)t.Value + 1 : -1; } catch { return -1; }
            }

            public string SetThrottle(int uid, int step)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try { _a.Call(u, AirOpsSpecs.ObjSetTelegraph, step); return null; }
                catch (Exception e) { return "throttle_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            /// <summary>Steps ascending by speed, afterburner excluded (the same reading as the CAP layer).</summary>
            public IList<CapStep> ThrottleSteps(int uid)
            {
                var res = new List<CapStep>();
                var u = Unit(uid);
                if (u == null) return res;
                if (_a.IsHelicopter(u)) return res;   // T-180: a helicopter has no speed steps (it already flies at its maximum)
                try
                {
                    var ap = _a.Get(u, CapSpecs.AircraftAp);
                    var mach = _a.Get(ap, CapSpecs.ApSpeedMach) as float[];
                    var names = _a.Get(ap, CapSpecs.ApSpeedNames) as string[];
                    var curve = _a.Get(ap, CapSpecs.ApRangeCurve);
                    if (mach == null) return res;
                    for (int i = 0; i < mach.Length; i++)
                    {
                        string nm = names != null && i < names.Length ? names[i] : null;
                        if ((nm != null && nm.IndexOf("afterburner", StringComparison.OrdinalIgnoreCase) >= 0) || (mach.Length > 4 && i == 4 && nm == null)) continue;
                        double k = 0;
                        try { if (curve != null) k = D(_a.Call(curve, CapSpecs.CurveEvaluate, mach[i])) ?? 0; } catch { }
                        res.Add(new CapStep { Step = i + 1, Kn = mach[i] * 638.4, K = k });
                    }
                    res.Sort((x, y) => x.Kn.CompareTo(y.Kn));
                }
                catch { res.Clear(); }
                return res;
            }

            public string SetLowPreset(int uid)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                return _a.SetPresetHeight(u, _a.LowestPreset(u), true);
            }

            public string Remember(int uid)
            {
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                try
                {
                    _a.TacticsEnsureMission();
                    _a._ctlMem.RememberRoute(u, _a.Waypoints(u), _a.DemandedCourse(u));   // what "Return control" gives back
                    if (!_mem.ContainsKey(uid))
                    {
                        var alt = D(_a.RxGet(u, CapSpecs.ObjDesiredAltitude));
                        _mem[uid] = new[] { (double)Throttle(uid), alt.HasValue ? alt.Value : -1 };
                    }
                    return null;
                }
                catch (Exception e) { return e.GetType().Name; }
            }

            public string RestoreSpeedAlt(int uid)
            {
                double[] m;
                if (!_mem.TryGetValue(uid, out m)) return null;
                _mem.Remove(uid);
                var u = Unit(uid);
                if (u == null) return "unit_not_found";
                string err = null;
                try { if (m[0] > 0) _a.Call(u, AirOpsSpecs.ObjSetTelegraph, (int)m[0]); } catch (Exception e) { err = "speed:" + e.GetType().Name; }
                try { if (m[1] > 0 && _a.Present(AirOpsSpecs.ObjDesiredAltitude)) _a.RxSet(u, AirOpsSpecs.ObjDesiredAltitude, (float)m[1]); } catch (Exception e) { err = (err == null ? "" : err + ",") + "alt:" + e.GetType().Name; }
                return err;
            }

            public bool Evading(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return false;
                    string st = _a.StateName(u, "CurrentStateName");
                    return st == "EvadeMissile" || st == "AdvancedMissileEvasion" || _a.SteeredByAirEvasion(uid);
                }
                catch { return false; }
            }
        }

        /// <summary>T-180: height preset of an aircraft or a helicopter (they are different game types). null = ok; "unsupported" = no member for this type.</summary>
        internal string SetPresetHeight(object unit, int preset, bool forWaypoints)
        {
            var spec = IsHelicopter(unit) ? AirGuardSpecs.HeliSetPresetHeight : AirEvasionSpecs.AcSetPresetHeight;
            if (!Present(spec)) return "unsupported";
            try { Call(unit, spec, preset, forWaypoints); return null; }
            catch (Exception e) { return "preset_failed:" + (e.InnerException ?? e).GetType().Name; }
        }

        /// <summary>T-180: helicopter: index of the minimum of _hp._presetAltitudes (0 if it cannot be read); aircraft: 0.</summary>
        internal int LowestPreset(object unit)
        {
            try
            {
                if (!IsHelicopter(unit)) return 0;
                var arr = Get(Get(unit, AirGuardSpecs.HeliHp), AirGuardSpecs.HpPresetAltitudes) as float[];
                if (arr == null || arr.Length == 0) return 0;
                int best = 0;
                for (int i = 1; i < arr.Length; i++) if (arr[i] < arr[best]) best = i;
                return best;
            }
            catch { return 0; }
        }
    }
}
