using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>What the retreat helper needs from the game (T-175 / ADR-0075 item 2.3). Implemented by GameApi (AirRetreatGame.cs) and by a fake in the self-test.</summary>
    internal interface IAirSteerGame
    {
        GeoPt? Pos(int uid);                                     // position of the aircraft, null = unknown
        string Goto(int uid, IList<GeoPt> pts, int altFt);       // altFt 0 = do not change; null = ok
        string Orbit(int uid, GeoPt center, double radiusNm, bool racetrack);
        int Throttle(int uid);                                   // current speed step (1-based), -1 = unknown
        string SetThrottle(int uid, int step);
        IList<CapStep> ThrottleSteps(int uid);                   // ascending by speed
        string SetLowPreset(int uid);                            // helicopter: the lowest preset altitude; "unsupported" without the member
        string Remember(int uid);                                // ControlMemory: speed, altitude
        string RestoreSpeedAlt(int uid);
        bool Evading(int uid);                                   // air_evasion or the game's own evasion
    }

    /// <summary>
    /// Shared helper of the owners (ASW, SAR, strike group) that moves an aircraft to the cover point of the air threat service and holds it there.
    /// First call: Remember + speed (CapMath.DashStep) + height; then the route to the point, on the spot an orbit. A repeat does nothing until the point moves.
    /// </summary>
    internal sealed partial class AirRetreatSteer
    {
        private sealed class St { public GeoPt Pt; public bool Orbiting; public string Mode; }

        private readonly IAirSteerGame _g;
        private readonly Action<string> _log;
        private readonly Dictionary<int, St> _st = new Dictionary<int, St>();
        public CapParams Fuel = new CapParams { FuelFloorPct = 0.15 };   // T-330: the evasion keeps its old floor

        public AirRetreatSteer(IAirSteerGame g, Action<string> log) { _g = g; _log = log ?? (s => { }); }

        public bool Active(int uid) { return _st.ContainsKey(uid); }

        public void Reset() { _st.Clear(); }

        /// <summary>"ok" = something was ordered, "hold" = nothing to do, "evading" = the evasion goes first, otherwise an error code of the game.</summary>
        public string Steer(int uid, AirCover c, bool helo, double reserveNm, double maxRangeNm, double now)
        {
            if (c == null) return "no_cover";
            if (_g.Evading(uid)) return "evading";   // ADR-0075 item 12: after the manoeuvre the retreat goes on
            var pos = _g.Pos(uid);
            St s;
            bool first = !_st.TryGetValue(uid, out s);
            string result = "hold";
            if (first)
            {
                s = new St { Pt = c.Point, Mode = c.Mode };
                _st[uid] = s;
                _g.Remember(uid);
                // speed: the fastest step the fuel allows for the way to the point
                try
                {
                    var steps = _g.ThrottleSteps(uid);
                    int cur = _g.Throttle(uid);
                    if (steps != null && steps.Count > 0 && cur > 0 && pos.HasValue)
                    {
                        string note;
                        double d = AswMath.DistNm(pos.Value, c.Point);
                        int step = CapMath.DashStep(steps, cur, d, reserveNm, maxRangeNm, 1e9, Fuel, out note);
                        if (step != cur) { string e = _g.SetThrottle(uid, step); _log("retreat speed uid=" + uid + " step=" + step + " " + note + " -> " + (e ?? "ok")); }
                    }
                }
                catch (Exception) { }
                if (helo)
                {
                    _log("retreat speed uid=" + uid + " helo=native_max");   // T-180: a helicopter has no speed steps in the game, it flies at its maximum
                    string e = _g.SetLowPreset(uid);
                    if (e != "unsupported") _log("retreat low_alt uid=" + uid + " -> " + (e ?? "ok"));
                }
                result = Route(uid, s, c, helo, pos);
                return result;
            }
            if (AswMath.DistNm(s.Pt, c.Point) > 1.0 || s.Mode != c.Mode)
            {
                s.Pt = c.Point; s.Mode = c.Mode; s.Orbiting = false;
                return Route(uid, s, c, helo, pos);
            }
            if (!s.Orbiting && pos.HasValue && AswMath.DistNm(pos.Value, s.Pt) <= 0.5)
            {
                s.Orbiting = true;
                string e = _g.Orbit(uid, s.Pt, helo ? 1.0 : OrbitNm, !helo);
                return e ?? "ok";
            }
            return result;
        }

        /// <summary>Radius of the cover orbit of an aircraft, nm (a helicopter hovers over the point).</summary>
        internal const double OrbitNm = 3.0, OrbitLaps = 3;

        /// <summary>T-331: the circle round c as waypoints: 4 points clockwise from the north, laps times, + the first again. A one-point "orbit" is a route that is finished
        /// at each pass, and the game says "We're on position" each time.</summary>
        internal static List<GeoPt> OrbitPts(GeoPt c, double rNm, int laps = 1)
        {
            var res = new List<GeoPt>();
            for (int l = 0; l < Math.Max(1, laps); l++)
                for (int i = 0; i < 4; i++) res.Add(AswMath.Offset(c, 90.0 * i, rNm));
            res.Add(res[0]);
            return res;
        }

        // T-331: an aircraft far from the point gets the point and the orbit as ONE route (the game reports "on position" at the end of the last waypoint only);
        // a helicopter flies to the point and hovers there (one point, as before)
        private string Route(int uid, St s, AirCover c, bool helo, GeoPt? pos)
        {
            string e;
            if (pos.HasValue && AswMath.DistNm(pos.Value, s.Pt) <= 0.5) { s.Orbiting = true; e = _g.Orbit(uid, s.Pt, helo ? 1.0 : OrbitNm, !helo); }
            else if (!helo)
            {
                s.Orbiting = true;
                var pts = new List<GeoPt> { s.Pt };
                pts.AddRange(OrbitPts(s.Pt, OrbitNm, (int)OrbitLaps));
                e = _g.Goto(uid, pts, 0);
            }
            else { s.Orbiting = false; e = _g.Goto(uid, new List<GeoPt> { s.Pt }, 0); }
            return e ?? "ok";
        }

        /// <summary>Resume: the speed and the height go back, the state is forgotten.</summary>
        public string Release(int uid)
        {
            if (!_st.Remove(uid)) return null;
            return _g.RestoreSpeedAlt(uid);
        }

        // ---- self-test ----

        private sealed class Fake : IAirSteerGame
        {
            public List<string> Calls = new List<string>();
            public GeoPt Where = new GeoPt(0, 0);
            public bool Evade;
            public bool Helo;
            public int Step = 2;
            public string LowResult;
            public GeoPt? Pos(int uid) { return Where; }
            public int LastPts;
            public string Goto(int uid, IList<GeoPt> pts, int altFt) { Calls.Add("goto"); LastPts = pts.Count; return null; }
            public string Orbit(int uid, GeoPt center, double r, bool race) { Calls.Add("orbit:" + r + ":" + race); return null; }
            public int Throttle(int uid) { return Step; }
            public string SetThrottle(int uid, int step) { Calls.Add("throttle:" + step); return null; }
            public IList<CapStep> ThrottleSteps(int uid)
            {
                if (Helo) return new List<CapStep>();
                return new List<CapStep> { new CapStep { Step = 1, Kn = 300 }, new CapStep { Step = 2, Kn = 400 }, new CapStep { Step = 3, Kn = 500 }, new CapStep { Step = 4, Kn = 600 } };
            }
            public string SetLowPreset(int uid) { Calls.Add("low"); return LowResult; }
            public string Remember(int uid) { Calls.Add("remember"); return null; }
            public string RestoreSpeedAlt(int uid) { Calls.Add("restore"); return null; }
            public bool Evading(int uid) { return Evade; }
        }

        public static string SelfTest()
        {
            var cover = new AirCover { Mode = "cover", Point = new GeoPt(0, 20.0 / 60.0) };
            var g = new Fake();
            var s = new AirRetreatSteer(g, null);
            s.Steer(1, cover, false, 2000, 2000, 0);
            if (string.Join(",", g.Calls) != "remember,throttle:4,goto") return "first steer (plane): " + string.Join(",", g.Calls);
            if (g.LastPts < 6) return "T-331: the plane gets the point and the orbit in one route: " + g.LastPts + " points";
            g.Calls.Clear();
            s.Steer(1, cover, false, 2000, 2000, 1);
            if (g.Calls.Count != 0) return "repeat must not call: " + string.Join(",", g.Calls);
            g.Where = new GeoPt(0, 20.0 / 60.0);
            s.Steer(1, cover, false, 2000, 2000, 2);
            if (g.Calls.Count != 0) return "T-331: the orbit is already in the route, no second order on the spot: " + string.Join(",", g.Calls);
            g.Calls.Clear();
            s.Steer(1, cover, false, 2000, 2000, 3);
            if (g.Calls.Count != 0) return "orbit once: " + string.Join(",", g.Calls);
            var moved = new AirCover { Mode = "cover", Point = new GeoPt(0, 30.0 / 60.0) };
            s.Steer(1, moved, false, 2000, 2000, 4);
            if (string.Join(",", g.Calls) != "goto") return "moved point -> new route only: " + string.Join(",", g.Calls);
            g.Calls.Clear();
            if (s.Release(1) != null || string.Join(",", g.Calls) != "restore" || s.Active(1)) return "release: " + string.Join(",", g.Calls);
            var pts5 = OrbitPts(new GeoPt(0, 0), 3.0);
            if (pts5.Count != 5 || AswMath.DistNm(pts5[0], pts5[4]) > 1e-9 || Math.Abs(AswMath.DistNm(new GeoPt(0, 0), pts5[1]) - 3.0) > 0.01) return "OrbitPts";
            if (OrbitPts(new GeoPt(0, 0), 3.0, 3).Count != 13) return "OrbitPts laps";
            // an aircraft that starts on the spot gets the orbit at once
            var on = new Fake { Where = cover.Point };
            new AirRetreatSteer(on, null).Steer(6, cover, false, 2000, 2000, 0);
            if (!on.Calls.Contains("orbit:3:True")) return "on the spot at the start -> orbit: " + string.Join(",", on.Calls);
            // helicopter: low preset, 1 mile orbit, "unsupported" is not an error
            var h = new Fake { LowResult = "unsupported", Where = new GeoPt(0, 0), Helo = true };
            var hlog = new List<string>();
            var s2 = new AirRetreatSteer(h, hlog.Add);
            string r = s2.Steer(2, cover, true, 2000, 2000, 0);
            if (r != "ok" || string.Join(",", h.Calls) != "remember,low,goto") return "helicopter first steer: " + r + " " + string.Join(",", h.Calls);
            if (!hlog.Contains("retreat speed uid=2 helo=native_max") || hlog.Exists(x => x.Contains("low_alt"))) return "helicopter log: " + string.Join("|", hlog);
            var h2 = new Fake { LowResult = null, Helo = true };
            var hlog2 = new List<string>();
            new AirRetreatSteer(h2, hlog2.Add).Steer(5, cover, true, 2000, 2000, 0);
            if (!hlog2.Contains("retreat low_alt uid=5 -> ok")) return "low_alt ok is logged: " + string.Join("|", hlog2);
            h.Where = cover.Point; h.Calls.Clear();
            s2.Steer(2, cover, true, 2000, 2000, 5);
            if (string.Join(",", h.Calls) != "orbit:1:False") return "helicopter orbit: " + string.Join(",", h.Calls);
            // tight fuel: the cruise step stays
            var t = new Fake();
            new AirRetreatSteer(t, null).Steer(3, cover, false, 250, 2000, 0);
            if (t.Calls.Contains("throttle:4") || t.Calls.Contains("throttle:3")) return "tight fuel keeps the step: " + string.Join(",", t.Calls);
            // evasion goes first: no calls, no state
            var e = new Fake { Evade = true };
            var s3 = new AirRetreatSteer(e, null);
            if (s3.Steer(4, cover, false, 2000, 2000, 0) != "evading" || e.Calls.Count != 0 || s3.Active(4)) return "evading";
            e.Evade = false;
            s3.Steer(4, cover, false, 2000, 2000, 1);
            if (!s3.Active(4) || e.Calls.Count == 0) return "retreat goes on after the manoeuvre";
            // no member for the height: the helicopter still goes
            return null;
        }
    }
}
