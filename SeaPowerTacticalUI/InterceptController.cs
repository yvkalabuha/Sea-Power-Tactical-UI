using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    // T-138 / ADR-0054 / docs/architecture/seapower-intercept-course.md: the intercept state machine. Pure C# (no Unity, no game types):
    // the game is reached through IInterceptGame only, so the stand `intercepttest` drives it with a fake game.
    // The only orders that ever leave this file are Route (goto + add_waypoint) and SetTelegraph. Local plane: the game is flat
    // (1 nm = 1/60 degree north, east x AswMath.LonK on the beta globe, T-348), x = east, y = north.

    /// <summary>Own unit snapshot. StepsKn = the speed of every telegraph step, index = telegraph + 1; null = unknown.</summary>
    internal sealed class IcOwn
    {
        public int Uid;
        public bool IsSub, Submerged, Alive;
        public double Lat, Lon, SpeedKn;
        public int Telegraph;
        public double[] StepsKn;
        public List<GeoPt> Waypoints = new List<GeoPt>();
    }

    /// <summary>The player's track of the target (never the object itself). AgeS NaN = unknown.</summary>
    internal sealed class IcTrack
    {
        public int Id;
        public string Kind, Relation;   // Kind: Surface | Submarine | ...; Relation: hostile | neutral | unknown | friendly
        public bool Live, BearingOnly, HasVel, Gone;
        public double Lat, Lon, VxKn, VyKn, AgeS = double.NaN;   // VxKn east, VyKn north
    }

    internal sealed class IcParams
    {
        public bool Enabled = true, ShowLine = true;
        public string Mode = "track", Speed = "auto";
        public double RefreshS = 30, MinShiftNm = 0.5, ArriveNm = 1.0, StaleS = 60, LostS = 180, SpeedUncertPct = 20, MaxRangeNm = 150, MaxEtaH = 6;
        public int ProbeBudget = 500;
        public double MinDepthM = 8, SubTransitDepthM = 30, ShoreMarginNm = 0.4;
        public int MaxCells = 40000;
        // T-169 / ADR-0067: enemy weapon zones on the route
        public string Avoid = "warn", OnEntry = "warn";   // Avoid: warn | detour | off; OnEntry: warn | hold | abort
        public double ZoneLeadS = InterceptThreatMath.DefaultLeadS, ZoneMarginNm = InterceptThreatMath.DefaultMarginNm;
    }

    internal interface IInterceptGame
    {
        double NowS { get; }                                     // mission time; NaN outside a mission
        IcOwn Own(int uid);                                      // null = not own / gone
        IcTrack Track(int trackId);                              // the player's track only; null = no track
        double HeightM(GeoPt g);
        /// <summary>first: RememberRoute + goto(pts[0]) + add_waypoint(rest); else the same without RememberRoute; NoteRoutePlaced(pts[0]). null = ok | error code.</summary>
        string Route(int uid, IList<GeoPt> pts, bool first);
        void NoteFirstWaypoint(int uid, GeoPt first);            // keeps "Return control" valid while points are passed
        string SetTelegraph(int uid, int telegraph);             // RememberFields(telegraph) the first time
        void Log(string line);
        void Event(string name, string json);
        /// <summary>T-169: identified hostile tracks of the player with the class range against our domain (submerged: ASW, else ASuW). Never null.</summary>
        IList<IcThreat> Threats(bool submerged);
    }

    internal sealed class IcMenu { public string Label; public bool Enabled, IsCancel; public string Reason; }

    internal sealed class IcView
    {
        public int Uid, TargetId;
        public string State, Style;   // Style: active | grey | red
        public List<GeoPt> Line;
        public GeoPt Ip;
        public string Label;
    }

    internal sealed partial class InterceptController
    {
        public const double LegClearMaxNm = 20;      // longer legs are probed by the budgeted RouteJob (it answers "direct" when the line is clear)
        private const double PassNm = 0.2, WpTolNm = 0.2;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class Pending
        {
            public bool Replan;
            public int Tg, Iter;
            public double StepKn;
            public GeoPt From, Ip, IpStraight;
            public IcInput Input;
            public IcSolution Sol;
            public RouteParams Rp;
            public List<IcThreat> Avoid;   // T-169: zones of the detour (null = land only)
            public bool DetourFailed;
        }

        private sealed class Run
        {
            public int Uid, TargetId;
            public string State = "routing", Reason, Style = "grey", Kind;
            public bool Placed, RouteSent, Frozen, NearShore, Suspended, AwaitResume;   // AwaitResume: the plan after an evasion is still being routed; the unit's route is not ours yet
            public int PlacedTg, LastWpCount, Step;
            public double PlanAtS, EtaS = double.NaN, EtaLoS = double.NaN, EtaHiS = double.NaN, K = 1, StartedS, ExpireS, CourseDeg, StepKn;
            public GeoPt Ip, IpStraight, Start, LastOwn, LastFirst;
            public bool HasFirst;
            public List<GeoPt> Pts = new List<GeoPt>();
            public Pending Pend;
            // T-169: enemy zones
            public List<IcZoneHit> Zones = new List<IcZoneHit>();
            public string ZoneSig = "";
            public double ZoneCheckS = double.NegativeInfinity;
            public HashSet<int> Inside = new HashSet<int>();
            public bool Held, ForceReplan, DetourFailed;
            public int HoldPrevTg, Avoided;
            public IcZoneHit HoldHit;
        }

        private readonly IInterceptGame _g;
        private readonly UnitSteering _steering;
        private readonly InterceptRoute _route;
        private readonly IcParams _p;
        private readonly List<Run> _runs = new List<Run>();
        private readonly List<Run> _ended = new List<Run>();   // unreachable / land_target: the red line stays a few seconds
        private double _lastTickS = double.NaN;

        public InterceptController(IInterceptGame g, UnitSteering steering, InterceptRoute route, IcParams p)
        {
            if (g == null) throw new ArgumentNullException("g");
            _g = g; _steering = steering ?? new UnitSteering(); _route = route; _p = p ?? new IcParams();
        }

        public int ActiveCount { get { return _runs.Count; } }

        // ------------------------------------------------------------------ helpers

        private static string Num(double d) { return Json.Num(Math.Round(d, 3)); }

        private static string Tx(string key, string fallback, params object[] args)
        {
            try
            {
                if (UiText.Has(UiText.DefaultSection, key)) return UiText.Fmt(key, args);
            }
            catch (Exception) { }
            try { return string.Format(Inv, fallback, args ?? new object[0]); } catch (FormatException) { return fallback; }
        }

        private static GeoPt GeoOf(GeoPt origin, double xEastNm, double yNorthNm)
        {
            double lat = origin.Lat + yNorthNm / 60.0;
            return new GeoPt(lat, origin.Lon + xEastNm / (60.0 * AswMath.LonK(origin.Lat, lat)));
        }

        private static double Dist(GeoPt a, GeoPt b) { return AswMath.DistNm(a, b); }

        private static GeoPt Pos(IcOwn o) { return new GeoPt(o.Lat, o.Lon); }

        private RouteParams RP(IcOwn o)
        {
            return new RouteParams
            {
                MinDepthM = o.IsSub && o.Submerged ? _p.SubTransitDepthM : _p.MinDepthM,
                ShoreMarginNm = _p.ShoreMarginNm,
                MaxCells = _p.MaxCells
            };
        }

        private IcInput MakeInput(IcOwn o, IcTrack t, double ownKn)
        {
            var i = new IcInput
            {
                DxNm = GeoLon.DLon(o.Lon, t.Lon) * 60.0 * AswMath.LonK(o.Lat, t.Lat), DyNm = (t.Lat - o.Lat) * 60.0, OwnKn = ownKn,
                MaxRangeNm = _p.MaxRangeNm, MaxEtaH = _p.MaxEtaH
            };
            if (t.HasVel) { i.VxKn = t.VxKn; i.VyKn = t.VyKn; }
            return i;
        }

        private static double StepKnOf(IcOwn o, int tg)
        {
            if (o.StepsKn == null || o.StepsKn.Length == 0) return 0;
            int k = Math.Max(0, Math.Min(o.StepsKn.Length - 1, tg + 1));
            return o.StepsKn[k];
        }

        private bool IsLand(GeoPt g, RouteParams rp) { return !SarRouteMath.Passable(_g.HeightM(g), rp.MinDepthM); }

        private Run Find(int uid)
        {
            foreach (var r in _runs) if (r.Uid == uid) return r;
            return null;
        }

        private static string Quality(IcTrack t, IcParams p) { return InterceptMath.Quality(t.AgeS, t.BearingOnly, p.StaleS, p.LostS); }

        /// <summary>Picks the telegraph step and solves at its speed. tg is the telegraph to order.</summary>
        private IcSolution Solve(IcOwn o, IcTrack t, out int tg, out double stepKn, out IcInput input)
        {
            IcSolution sol;
            tg = InterceptMath.PickStep(o.StepsKn, o.Telegraph, _p.Speed, kn => InterceptMath.Solve(MakeInput(o, t, kn)), out sol);
            stepKn = StepKnOf(o, tg);
            input = MakeInput(o, t, stepKn);
            if (sol != null && (sol.Kind == "ok" || sol.Kind == "cpa")) InterceptMath.Band(input, _p.SpeedUncertPct, sol);
            return sol ?? new IcSolution { Kind = "bad_input" };
        }

        private static string SolveError(IcSolution s)
        {
            switch (s.Kind)
            {
                case "opening": return "opening";
                case "too_far": return "too_far";
                default: return "no_track";
            }
        }

        private static string EtaText(IcSolution s)
        {
            string eta = "~" + InterceptMath.FmtEta(s.EtaS);
            if (InterceptMath.BandIsWide(s)) eta += " (" + InterceptMath.FmtEta(s.EtaLoS) + "-" + InterceptMath.FmtEta(s.EtaHiS) + ")";
            return eta;
        }

        // ------------------------------------------------------------------ menu

        /// <summary>Menu item of the contact menu: ok | cpa | disabled with a reason | "Cancel intercept". No A*; the land hint is a rough probe.</summary>
        public IcMenu Menu(int ownUid, int trackId)
        {
            if (!_p.Enabled) return null;
            IcOwn own = _g.Own(ownUid);
            if (own == null || !own.Alive) return null;
            Run act = Find(ownUid);
            if (act != null && act.TargetId == trackId)
            {
                double left = Math.Max(0, act.EtaS - (_g.NowS - act.PlanAtS));
                string lbl = Tx("icpt.menu_cancel", "Cancel intercept · {0}", double.IsNaN(left) ? "" : "~" + InterceptMath.FmtEta(left));
                _g.Log("[Intercept] menu_open uid=" + ownUid + " target=" + trackId + " kind=cancel");
                return new IcMenu { Label = lbl, Enabled = true, IsCancel = true };
            }
            IcTrack t = _g.Track(trackId);
            string reason = Why(own, t, ownUid);
            IcSolution sol = null;
            double menuKn = 0;
            if (reason == null)
            {
                int tg; IcInput inp;
                sol = Solve(own, t, out tg, out menuKn, out inp);
                if (sol.Kind != "ok" && sol.Kind != "cpa") reason = SolveError(sol);
            }
            if (reason != null)
            {
                _g.Log("[Intercept] menu_open uid=" + ownUid + " target=" + trackId + " kind=disabled reason=" + reason);
                string rt = Tx("icpt.reason." + (reason.StartsWith("busy", StringComparison.Ordinal) ? "busy" : reason), reason);
                return new IcMenu { Label = Tx("icpt.menu_disabled", "Intercept course · {0}", rt), Enabled = false, Reason = reason };
            }
            string hint = "";
            try
            {
                GeoPt ip = GeoOf(Pos(own), sol.IpX, sol.IpY);
                if (_route != null && _route.LineHitsLand(Pos(own), ip, RP(own))) hint = " " + Tx("icpt.land_hint", "(around land)");
            }
            catch (Exception) { }
            string zone = "";
            try
            {
                if (_p.Avoid != "off")
                {
                    GeoPt ip = GeoOf(Pos(own), sol.IpX, sol.IpY);
                    List<IcZoneHit> hits = InterceptThreatMath.Check(Pos(own), new[] { ip }, menuKn, Threats(own), trackId, _p.MaxEtaH * 3600.0);
                    if (hits.Count > 0)
                    {
                        zone = " " + ZonesText(hits);
                        foreach (IcZoneHit h in hits) _g.Log(ZoneLine(ownUid, trackId, "menu", h));
                    }
                }
            }
            catch (Exception) { }
            string label;
            if (sol.Kind == "ok")
                label = Tx("icpt.menu_ok", "Intercept course · {0}, {1}°, {2} kn", EtaText(sol), Math.Round(sol.CourseDeg).ToString("0", Inv), Math.Round(StepKnOf(own, own.Telegraph)).ToString("0", Inv)) + hint;
            else
                label = Tx("icpt.menu_cpa", "Close in · CPA {0} nm", sol.CpaNm.ToString("0.0", Inv)) + hint;
            label += zone;
            _g.Log("[Intercept] menu_open uid=" + ownUid + " target=" + trackId + " kind=" + sol.Kind + " eta=" + InterceptMath.FmtEta(sol.EtaS));
            return new IcMenu { Label = label, Enabled = true };
        }

        /// <summary>null = the intercept may start; otherwise the reason code.</summary>
        private string Why(IcOwn own, IcTrack t, int ownUid)
        {
            if (t == null || t.Gone) return "no_track";
            string a = InterceptMath.Allow(t.Kind, t.Relation, t.Live, t.BearingOnly);
            if (a != null) return a;
            string q = Quality(t, _p);
            if (q == "stale") return "stale";
            if (q == "lost") return "lost";
            if (own.StepsKn == null || own.StepsKn.Length == 0) return "no_steps";
            string other = _steering.HolderOtherThan(ownUid, UnitSteering.Intercept);
            if (other != null) return "busy:" + other;
            return null;
        }

        // ------------------------------------------------------------------ start

        /// <summary>null = ok (ackJson holds the plan, state active or routing) | error code (doc section 8).</summary>
        public string Start(int ownUid, int trackId, out string ackJson)
        {
            ackJson = null;
            if (!_p.Enabled) return "unsupported";
            IcOwn own = _g.Own(ownUid);
            if (own == null || !own.Alive) { _g.Log("[Intercept] start refused uid=" + ownUid + " target=" + trackId + " reason=not_surface"); return "not_surface"; }
            IcTrack t = _g.Track(trackId);
            string why = Why(own, t, ownUid);
            if (why != null) { _g.Log("[Intercept] start refused uid=" + ownUid + " target=" + trackId + " reason=" + why); return why; }
            Run old = Find(ownUid);
            if (old != null) End(old, "cancelled", "replace");
            string claim = _steering.Claim(ownUid, UnitSteering.Intercept);
            if (claim != null) return claim;
            _g.Log("[Intercept] start uid=" + ownUid + " target=" + trackId + " mode=" + _p.Mode + " speed=" + _p.Speed);
            var r = new Run { Uid = ownUid, TargetId = trackId, StartedS = _g.NowS, PlanAtS = _g.NowS, LastOwn = Pos(own), PlacedTg = own.Telegraph };
            string err = Plan(r, own, t, false);
            if (err != null)
            {
                _steering.Release(ownUid, UnitSteering.Intercept);
                _route.Cancel(ownUid);
                _g.Log("[Intercept] result uid=" + ownUid + " state=refused reason=" + err);
                return err;
            }
            _runs.Add(r);
            Emit(r, null);
            ackJson = AckJson(r);
            return null;
        }

        private string AckJson(Run r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"state\":").Append(Json.Str(r.State)).Append(",\"plan\":{\"kind\":").Append(Json.Str(r.Kind ?? ""));
            sb.Append(",\"course\":").Append(Num(r.CourseDeg));
            sb.Append(",\"ip\":{\"lat\":").Append(Json.Num(r.Ip.Lat)).Append(",\"lon\":").Append(Json.Num(r.Ip.Lon)).Append('}');
            sb.Append(",\"route_pts\":").Append(r.Pts.Count);
            sb.Append(",\"eta_s\":").Append(double.IsNaN(r.EtaS) ? "null" : Num(r.EtaS));
            sb.Append(",\"eta_lo_s\":").Append(double.IsNaN(r.EtaLoS) ? "null" : Num(r.EtaLoS));
            sb.Append(",\"eta_hi_s\":").Append(double.IsNaN(r.EtaHiS) ? "null" : Num(r.EtaHiS));
            sb.Append(",\"step\":").Append(r.Step);
            sb.Append(",\"warnings\":[");
            bool first = true;
            Action<string> w = s => { if (!first) sb.Append(','); first = false; sb.Append(Json.Str(s)); };
            if (r.Kind == "cpa") w("cpa");
            if (r.K > 1.02) w("detour");
            if (!double.IsNaN(r.EtaLoS) && r.EtaS > 0 && (r.EtaHiS - r.EtaLoS) > InterceptMath.BandWidePct / 100.0 * r.EtaS) w("band_wide");
            if (r.Zones.Count > 0) w("enemy_zone");
            if (r.DetourFailed) w("detour_failed");
            sb.Append(']');
            AppendZones(sb, r.Zones);
            sb.Append("}}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ planning

        /// <summary>Solves and either orders at once (a clear straight leg) or queues a RouteJob. null = ok (r may be "routing"); error code otherwise.
        /// replan = the run already has an order; the caller keeps the old route on an error.</summary>
        private string Plan(Run r, IcOwn own, IcTrack t, bool replan)
        {
            int tg; double kn; IcInput inp;
            IcSolution sol = Solve(own, t, out tg, out kn, out inp);
            if (sol.Kind != "ok" && sol.Kind != "cpa") return SolveError(sol);
            GeoPt from = Pos(own);
            GeoPt ip = GeoOf(from, sol.IpX, sol.IpY);
            RouteParams rp = RP(own);
            if (IsLand(ip, rp)) return "land_target";
            _g.Log("[Intercept] solve uid=" + r.Uid + " kind=" + sol.Kind + " eta=" + InterceptMath.FmtEta(sol.EtaS) + " step=" + tg + " band=" + (double.IsNaN(sol.EtaLoS) ? "" : InterceptMath.FmtEta(sol.EtaLoS) + "-" + InterceptMath.FmtEta(sol.EtaHiS)));
            List<IcThreat> avoid = null;
            if (_p.Avoid == "detour")
            {
                var skipped = new List<IcThreat>();
                avoid = InterceptThreatMath.Avoidable(from, ip, Threats(own), r.TargetId, _p.ZoneMarginNm, skipped);
                if (skipped.Count > 0) _g.Log("[Intercept] detour skips uid=" + r.Uid + " zones=" + Ids(skipped) + " (the unit or the intercept point is inside: warning only)");
                if (avoid.Count == 0) avoid = null;
            }
            if (Dist(from, ip) <= LegClearMaxNm && _route.LegClear(from, ip, rp) && (avoid == null || !InterceptThreatMath.LegCrosses(from, ip, avoid, _p.ZoneMarginNm)))
            {
                r.IpStraight = ip;
                r.DetourFailed = false; r.Avoided = 0;
                return Accept(r, own, tg, kn, sol, new List<GeoPt> { ip }, 1.0, replan, false);
            }
            r.Pend = new Pending { Replan = replan, Tg = tg, StepKn = kn, From = from, Ip = ip, IpStraight = ip, Input = inp, Sol = sol, Rp = rp, Avoid = avoid };
            if (avoid != null) _g.Log("[Intercept] detour uid=" + r.Uid + " zones=" + Ids(avoid) + " margin_nm=" + Num(_p.ZoneMarginNm));
            _route.Request(r.Uid, from, ip, rp, BlockOf(avoid));
            r.CourseDeg = sol.CourseDeg; r.Kind = sol.Kind; r.Ip = ip; r.Step = tg;
            if (!replan) { r.State = "routing"; r.Style = "grey"; }
            return null;
        }

        /// <summary>Gives the route to the game (goto + add_waypoint) and the telegraph when the step changes.</summary>
        private string Accept(Run r, IcOwn own, int tg, double stepKn, IcSolution sol, List<GeoPt> pts, double k, bool replan, bool nearShore)
        {
            string err = _g.Route(r.Uid, pts, !r.RouteSent);
            if (err != null) { _g.Log("[Intercept] order failed uid=" + r.Uid + " reason=" + err); return err; }
            r.RouteSent = true;
            int placed = own.Telegraph;
            if (tg != own.Telegraph && _p.Speed != "current")
            {
                string terr = _g.SetTelegraph(r.Uid, tg);
                if (terr == null) placed = tg; else _g.Log("[Intercept] telegraph failed uid=" + r.Uid + " reason=" + terr);
            }
            r.Pend = null;
            r.Placed = true; r.PlacedTg = placed; r.Step = placed; r.StepKn = stepKn;
            r.Pts = new List<GeoPt>(pts); r.Ip = pts[pts.Count - 1]; r.Start = Pos(own);
            r.LastWpCount = pts.Count; r.LastFirst = pts[0]; r.HasFirst = true;
            r.PlanAtS = _g.NowS; r.K = k; r.Kind = sol.Kind; r.CourseDeg = sol.CourseDeg;
            double straightEta = sol.EtaS;
            r.EtaS = k > 1.02 ? (stepKn > 0 ? AswMath.RouteLengthNm(Pos(own), pts) / stepKn * 3600.0 : straightEta) : straightEta;
            r.EtaLoS = sol.EtaLoS; r.EtaHiS = sol.EtaHiS;
            r.NearShore = nearShore;
            r.Frozen = false; r.Suspended = false; r.AwaitResume = false;
            string prev = r.State;
            r.State = "active"; r.Style = "active";
            _g.Log("[Intercept] order uid=" + r.Uid + " goto+" + (pts.Count - 1) + " add_waypoint tg=" + placed + " eta=" + InterceptMath.FmtEta(r.EtaS));
            r.Held = false; r.HoldHit = null; r.ForceReplan = false;
            try
            {
                List<IcZoneHit> hits = InterceptThreatMath.Check(Pos(own), pts, stepKn > 0 ? stepKn : own.SpeedKn, Threats(own), r.TargetId, _p.MaxEtaH * 3600.0);
                NoteZones(r, hits, "plan");
                r.ZoneCheckS = _g.NowS;
            }
            catch (Exception e) { _g.Log("[Intercept] zone check failed uid=" + r.Uid + ": " + e.GetType().Name); }
            if (replan && prev != "active") Emit(r, null);
            return null;
        }

        // ------------------------------------------------------------------ route results

        private void Collect(Run r)
        {
            if (r.Pend == null) return;
            RouteResult res = _route.Poll(r.Uid);
            if (res == null) return;
            Pending pd = r.Pend;
            _g.Log("[Intercept] route uid=" + r.Uid + " kind=" + res.Kind + " pts=" + (res.Points == null ? 0 : res.Points.Count) + " probes=" + res.Probes + " ms=" + Math.Round(res.Ms) + " iter=" + pd.Iter);
            if ((res.Kind == "none" || res.Points == null || res.Points.Count == 0) && pd.Avoid != null)
            {
                // T-169: no way around the zones in the A* frame -> the route around land only, the warning stays
                _g.Log("[Intercept] detour none uid=" + r.Uid + " why=" + (res.Why ?? "") + " (route around land only; the zone warning stays)");
                pd.Avoid = null; pd.DetourFailed = true;
                _route.Request(r.Uid, pd.From, pd.Ip, pd.Rp);
                return;
            }
            if (res.Kind == "none" || res.Points == null || res.Points.Count == 0)
            {
                string code = res.Why == "land" ? "land_target" : "unreachable";
                r.Pend = null;
                if (pd.Replan && r.AwaitResume) { End(r, "cancelled", "resume_failed"); return; }
                if (pd.Replan)
                {
                    _g.Log("[Intercept] replan failed uid=" + r.Uid + " reason=" + code + " (the old route stays)");
                    r.PlanAtS = _g.NowS;
                    return;
                }
                End(r, "unreachable", code);
                return;
            }
            var pts = new List<GeoPt>(res.Points);
            if (Dist(pts[pts.Count - 1], pd.Ip) > 0.05) pts.Add(pd.Ip);
            if (pts.Count > 12) pts.RemoveRange(12, pts.Count - 12);
            double len = AswMath.RouteLengthNm(pd.From, pts), straight = Dist(pd.From, pd.Ip);
            double k = straight > 0.01 ? Math.Max(1.0, len / straight) : 1.0;
            if (pd.Iter < InterceptMath.MaxRefine && k > 1.02)
            {
                IcSolution s2 = InterceptMath.RefineForRoute(pd.Input, k);
                if (s2.Kind == "ok" || s2.Kind == "cpa")
                {
                    GeoPt ip1 = GeoOf(pd.From, s2.IpX, s2.IpY);
                    if (Dist(ip1, pd.Ip) > _p.MinShiftNm && !IsLand(ip1, pd.Rp))
                    {
                        pd.Ip = ip1; pd.Iter++;
                        _route.Request(r.Uid, pd.From, ip1, pd.Rp, BlockOf(pd.Avoid));
                        return;
                    }
                }
            }
            IcOwn own = _g.Own(r.Uid);
            if (own == null || !own.Alive) { End(r, "cancelled", "unit_gone"); return; }
            IcSolution sol = pd.Sol;
            r.DetourFailed = pd.DetourFailed; r.Avoided = pd.Avoid == null ? 0 : pd.Avoid.Count;
            string err = Accept(r, own, pd.Tg, pd.StepKn, sol, pts, k, pd.Replan, res.NearShore);
            if (err != null)
            {
                if (pd.Replan && r.AwaitResume) { End(r, "cancelled", "resume_failed"); return; }
                if (pd.Replan) { r.PlanAtS = _g.NowS; return; }
                End(r, "unreachable", err);
                return;
            }
            if (!pd.Replan) Emit(r, null);
        }

        // ------------------------------------------------------------------ tick

        public void FramePump()
        {
            if (_route == null) return;
            _route.ProbeBudget = _p.ProbeBudget;
            _route.FramePump();
            foreach (var r in _runs.ToArray()) if (r.Pend != null) Collect(r);
        }

        /// <summary>Once per second of mission time.</summary>
        public void Tick()
        {
            double now = _g.NowS;
            if (double.IsNaN(now)) return;
            if (!double.IsNaN(_lastTickS) && now >= _lastTickS && now - _lastTickS < 1.0) return;
            _lastTickS = now;
            _ended.RemoveAll(e => now >= e.ExpireS);
            foreach (var r in _runs.ToArray()) TickRun(r, now);
        }

        private void TickRun(Run r, double now)
        {
            IcOwn own = _g.Own(r.Uid);
            if (own == null || !own.Alive) { End(r, "cancelled", "unit_gone"); return; }
            r.LastOwn = Pos(own);

            // who else steers the unit
            string other = _steering.HolderOtherThan(r.Uid, UnitSteering.Intercept);
            if (other == UnitSteering.SmartAttack || other == UnitSteering.Sar) { End(r, "superseded", other); return; }
            bool resumed = false;
            if (other == UnitSteering.Evasion || other == UnitSteering.AirEvasion)
            {
                if (!r.Suspended)
                {
                    r.Suspended = true; r.State = "suspended"; r.Style = "grey";
                    r.Pend = null; _route.Cancel(r.Uid);
                    _g.Log("[Intercept] suspend uid=" + r.Uid + " owner=evasion");
                    Emit(r, "evasion");
                }
                return;
            }
            if (r.Suspended)
            {
                r.Suspended = false; resumed = true; r.AwaitResume = true;
                _g.Log("[Intercept] resume uid=" + r.Uid);
            }

            IcTrack t = _g.Track(r.TargetId);
            if (t == null || t.Gone) { End(r, "target_gone", null); return; }

            // the player changed the route or the step
            if (r.Placed && !resumed && !r.AwaitResume)
            {
                string pc = PlayerChanged(r, own);
                if (pc == "empty")
                {
                    if (Dist(Pos(own), r.Ip) <= _p.ArriveNm && _p.Mode == "track") { r.PlanAtS = double.NegativeInfinity; }
                    else if (!InterceptMath.Arrived(Dist(Pos(own), new GeoPt(t.Lat, t.Lon)), Dist(Pos(own), r.Ip), 0, _p.ArriveNm)) { End(r, "player_changed", "route_cleared"); return; }
                }
                else if (pc != null) { End(r, "player_changed", pc); return; }
            }

            string q = Quality(t, _p);
            if (q == "lost") { End(r, "track_lost", null); return; }
            bool frozen = q != "fresh";
            if (frozen && !r.Frozen) { r.Frozen = true; r.Style = "grey"; _g.Log("[Intercept] stale uid=" + r.Uid + " quality=" + q); }
            else if (!frozen && r.Frozen) { r.Frozen = false; if (r.Placed && r.State == "active") r.Style = "active"; }
            if (r.Placed && !resumed && !r.AwaitResume && ZoneTick(r, own, now)) return;   // T-169: held or ended at an enemy zone
            if (frozen) return;

            if (r.Pend != null) { Collect(r); if (r.Pend != null || !_runs.Contains(r)) return; }
            if (!r.Placed) return;

            // arrived
            double dist = Dist(Pos(own), new GeoPt(t.Lat, t.Lon));
            double etaLeft = Math.Max(0, r.EtaS - (now - r.PlanAtS));
            if (InterceptMath.Arrived(dist, Dist(Pos(own), r.Ip), etaLeft, _p.ArriveNm)) { End(r, "arrived", null); return; }

            // a waypoint was passed: keep "Return control" valid (it compares the first remaining point)
            if (own.Waypoints.Count > 0 && own.Waypoints.Count < r.LastWpCount)
            {
                _g.NoteFirstWaypoint(r.Uid, own.Waypoints[0]);
                r.LastWpCount = own.Waypoints.Count; r.LastFirst = own.Waypoints[0];
            }

            if (_p.Mode != "track" && !resumed) return;

            // replan?
            int tg; double kn; IcInput inp;
            IcSolution sol = Solve(own, t, out tg, out kn, out inp);
            bool kindOk = sol.Kind == "ok" || sol.Kind == "cpa";
            string reason;
            if (resumed) reason = "resume";
            else if (r.ForceReplan) { reason = "zone_clear"; r.ForceReplan = false; }
            else
            {
                var rep = new IcReplan
                {
                    NowS = now, PlanAtS = r.PlanAtS, RefreshS = _p.RefreshS, MinShiftNm = _p.MinShiftNm, DistNm = dist,
                    OffRouteNm = OffRoute(Pos(own), r), NearShore = r.NearShore,
                    KindChanged = kindOk && sol.Kind != r.Kind,
                    IpShiftNm = kindOk ? Dist(GeoOf(Pos(own), sol.IpX, sol.IpY), r.IpStraight) : 0
                };
                if (!InterceptMath.NeedReplan(rep, out reason)) return;
            }
            _g.Log("[Intercept] replan uid=" + r.Uid + " reason=" + reason);
            string err = Plan(r, own, t, true);
            if (err != null)
            {
                _g.Log("[Intercept] replan failed uid=" + r.Uid + " reason=" + err + " (the old route stays)");
                r.PlanAtS = now;
                if (resumed) End(r, "cancelled", "resume_failed");
                return;
            }
            if (resumed && r.State != "active") { r.State = "routing"; r.Style = "grey"; Emit(r, "resume"); }
            else if (resumed) Emit(r, "resume");
        }

        // ------------------------------------------------------------------ enemy zones (T-169 / ADR-0067)

        private IList<IcThreat> Threats(IcOwn own)
        {
            if (_p.Avoid == "off") return new List<IcThreat>();
            try { return _g.Threats(own.IsSub && own.Submerged) ?? new List<IcThreat>(); }
            catch (Exception) { return new List<IcThreat>(); }
        }

        private static Func<GeoPt, bool> BlockOf(List<IcThreat> avoid, double marginNm)
        {
            if (avoid == null || avoid.Count == 0) return null;
            var zones = new List<IcThreat>(avoid);
            return g => InterceptThreatMath.Blocked(g, zones, marginNm);
        }

        private Func<GeoPt, bool> BlockOf(List<IcThreat> avoid) { return BlockOf(avoid, _p.ZoneMarginNm); }

        private static string Ids(IList<IcThreat> ts)
        {
            var sb = new StringBuilder();
            foreach (IcThreat t in ts) { if (sb.Length > 0) sb.Append(','); sb.Append(t.TrackId); }
            return sb.ToString();
        }

        private static string MinText(double s)
        {
            long m = (long)Math.Ceiling(Math.Max(0, s) / 60.0);
            return m < 60 ? Tx("icpt.zone_min", "{0} min", m.ToString(Inv)) : InterceptMath.FmtEta(s);
        }

        private static string WeaponText(IcThreat t)
        {
            if (!string.IsNullOrEmpty(t.Weapon)) return t.Weapon;
            switch (t.WeaponKind)
            {
                case "missile": return Tx("icpt.wpn.missile", "missiles");
                case "gun": return Tx("icpt.wpn.gun", "guns");
                case "torpedo": return Tx("icpt.wpn.torpedo", "torpedoes");
                case "asw": return Tx("icpt.wpn.asw", "ASW weapons");
                default: return Tx("icpt.wpn.other", "weapons");
            }
        }

        private static string NameText(IcZoneHit h)
        {
            string n = string.IsNullOrEmpty(h.Threat.Name) ? "#" + h.Threat.TrackId : h.Threat.Name;
            return h.IsTarget ? n + " " + Tx("icpt.zone_target", "(target)") : n;
        }

        /// <summary>"⚠ Osa zone (P-15 Termit, 25 nm) in 6 min" | "⚠ inside the Osa zone (...)".</summary>
        internal static string ZoneText(IcZoneHit h)
        {
            string range = h.Threat.RangeNm.ToString("0", Inv);
            if (h.InsideNow) return Tx("icpt.zone_inside", "⚠ inside the {0} zone ({1}, {2} nm)", NameText(h), WeaponText(h.Threat), range);
            return Tx("icpt.zone_hint", "⚠ {0} zone ({1}, {2} nm) in {3}", NameText(h), WeaponText(h.Threat), range, MinText(h.EntryS));
        }

        private static string ZonesText(IList<IcZoneHit> hits)
        {
            if (hits == null || hits.Count == 0) return "";
            string s = ZoneText(hits[0]);
            if (hits.Count > 1) s += " " + Tx("icpt.zone_more", "+{0}", (hits.Count - 1).ToString(Inv));
            return s;
        }

        private static string ZoneLine(int uid, int target, string phase, IcZoneHit h)
        {
            return "[Intercept] zone uid=" + uid + " target=" + target + " phase=" + phase + " threat=" + h.Threat.TrackId + " name=" + (h.Threat.Name ?? "")
                + " weapon=" + (h.Threat.Weapon ?? h.Threat.WeaponKind ?? "") + " range_nm=" + Num(h.Threat.RangeNm)
                + " entry_s=" + (h.InsideNow ? "0" : Num(Math.Round(h.EntryS))) + " inside=" + Json.Bool(h.InsideNow) + " is_target=" + Json.Bool(h.IsTarget);
        }

        private static void AppendZones(StringBuilder sb, IList<IcZoneHit> hits)
        {
            sb.Append(",\"zones\":[");
            for (int i = 0; i < hits.Count; i++)
            {
                IcZoneHit h = hits[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"track_id\":").Append(h.Threat.TrackId).Append(",\"name\":").Append(Json.Str(h.Threat.Name ?? ""));
                sb.Append(",\"weapon\":").Append(Json.Str(h.Threat.Weapon ?? h.Threat.WeaponKind ?? "")).Append(",\"range_nm\":").Append(Num(h.Threat.RangeNm));
                sb.Append(",\"entry_s\":").Append(Num(h.InsideNow ? 0 : Math.Round(h.EntryS))).Append(",\"inside\":").Append(Json.Bool(h.InsideNow));
                sb.Append(",\"target\":").Append(Json.Bool(h.IsTarget)).Append('}');
            }
            sb.Append(']');
        }

        /// <summary>Keeps the hits of the run; a change of the set goes to the log (one line per zone) and to the event intercept_zone.</summary>
        private void NoteZones(Run r, List<IcZoneHit> hits, string phase)
        {
            r.Zones = hits ?? new List<IcZoneHit>();
            r.Inside.Clear();
            foreach (IcZoneHit h in r.Zones) if (h.InsideNow) r.Inside.Add(h.Threat.TrackId);
            string sig = InterceptThreatMath.Signature(r.Zones);
            if (sig == r.ZoneSig) return;
            bool had = r.ZoneSig.Length > 0;
            r.ZoneSig = sig;
            if (r.Zones.Count == 0) { if (had) _g.Log("[Intercept] zone clear uid=" + r.Uid + " phase=" + phase); }
            else foreach (IcZoneHit h in r.Zones) _g.Log(ZoneLine(r.Uid, r.TargetId, phase, h));
            var sb = new StringBuilder();
            sb.Append("{\"uid\":").Append(r.Uid).Append(",\"target_id\":").Append(r.TargetId).Append(",\"phase\":").Append(Json.Str(phase));
            AppendZones(sb, r.Zones);
            sb.Append('}');
            _g.Event("intercept_zone", sb.ToString());
        }

        /// <summary>Every CheckEveryS: the zones on the rest of the route; OnZoneEntry hold/abort stops the unit before a zone (not the target's).
        /// true = the run is held or ended (the caller stops this tick).</summary>
        private bool ZoneTick(Run r, IcOwn own, double now)
        {
            if (now - r.ZoneCheckS < InterceptThreatMath.CheckEveryS && now >= r.ZoneCheckS) return r.Held;
            r.ZoneCheckS = now;
            IList<GeoPt> route = own.Waypoints.Count > 0 ? (IList<GeoPt>)own.Waypoints : r.Pts;
            double kn = r.StepKn > 0 ? r.StepKn : own.SpeedKn;
            var before = new HashSet<int>(r.Inside);
            List<IcZoneHit> hits;
            try { hits = InterceptThreatMath.Check(Pos(own), route, kn, Threats(own), r.TargetId, _p.MaxEtaH * 3600.0); }
            catch (Exception e) { _g.Log("[Intercept] zone check failed uid=" + r.Uid + ": " + e.GetType().Name); return r.Held; }
            NoteZones(r, hits, "track");
            if (r.Held)
            {
                IcZoneHit still = InterceptThreatMath.Trigger(hits, _p.ZoneLeadS * InterceptThreatMath.ResumeFactor, null);
                if (still != null && _p.OnEntry == "hold") { r.HoldHit = still; return true; }
                ZoneResume(r, own);
                return false;
            }
            if (_p.OnEntry == "warn") return false;
            IcZoneHit trig = InterceptThreatMath.Trigger(hits, _p.ZoneLeadS, before);
            if (trig == null) return false;
            r.HoldPrevTg = own.Telegraph;
            string terr = _g.SetTelegraph(r.Uid, 0);
            if (terr == null) { r.PlacedTg = 0; own.Telegraph = 0; }
            else _g.Log("[Intercept] telegraph failed uid=" + r.Uid + " reason=" + terr);
            _g.Log("[Intercept] zone stop uid=" + r.Uid + " mode=" + _p.OnEntry + " threat=" + trig.Threat.TrackId + " name=" + (trig.Threat.Name ?? "")
                + " entry_s=" + (trig.InsideNow ? "0" : Num(Math.Round(trig.EntryS))) + " tg_before=" + r.HoldPrevTg);
            if (_p.OnEntry == "abort") { End(r, "aborted", "enemy_zone"); return true; }
            r.Held = true; r.HoldHit = trig; r.State = "held"; r.Style = "grey";
            r.Pend = null; if (_route != null) _route.Cancel(r.Uid);
            Emit(r, "enemy_zone");
            return true;
        }

        private void ZoneResume(Run r, IcOwn own)
        {
            r.Held = false; r.HoldHit = null; r.State = "active"; r.Style = r.Frozen ? "grey" : "active";
            int back = r.HoldPrevTg > 0 ? r.HoldPrevTg : r.Step;
            if (back > 0)
            {
                string terr = _g.SetTelegraph(r.Uid, back);
                if (terr == null) { r.PlacedTg = back; own.Telegraph = back; }
                else _g.Log("[Intercept] telegraph failed uid=" + r.Uid + " reason=" + terr);
            }
            r.ForceReplan = _p.Mode == "track";
            _g.Log("[Intercept] zone resume uid=" + r.Uid + " tg=" + back);
            Emit(r, "zone_clear");
        }

        /// <summary>null = unchanged | "empty" | "route" | "telegraph".</summary>
        private string PlayerChanged(Run r, IcOwn own)
        {
            if (own.Telegraph != r.PlacedTg) return "telegraph";
            if (own.Waypoints.Count == 0) return "empty";
            GeoPt last = own.Waypoints[own.Waypoints.Count - 1];
            if (Dist(last, r.Ip) > WpTolNm) return "route";
            return null;
        }

        /// <summary>Distance of the unit from the planned polyline (plan start, points...).</summary>
        private static double OffRoute(GeoPt unit, Run r)
        {
            if (r.Pts.Count == 0) return 0;
            double best = double.MaxValue;
            GeoPt a = r.Start;
            foreach (GeoPt b in r.Pts)
            {
                best = Math.Min(best, PointSegNm(unit, a, b));
                a = b;
            }
            return best;
        }

        private static double PointSegNm(GeoPt p, GeoPt a, GeoPt b)
        {
            double k = AswMath.LonK(a.Lat, b.Lat);
            double bx = (b.Lat - a.Lat) * 60.0, by = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * k;
            double px = (p.Lat - a.Lat) * 60.0, py = GeoLon.DLon(a.Lon, p.Lon) * 60.0 * k;
            double l2 = bx * bx + by * by;
            double t = l2 < 1e-12 ? 0 : (px * bx + py * by) / l2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double dx = px - bx * t, dy = py - by * t;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ------------------------------------------------------------------ end, cancel, events

        private void End(Run r, string state, string reason)
        {
            _runs.Remove(r);
            _steering.Release(r.Uid, UnitSteering.Intercept);
            if (_route != null) _route.Cancel(r.Uid);
            r.State = state; r.Reason = reason; r.Pend = null;
            _g.Log("[Intercept] result uid=" + r.Uid + " state=" + state + (reason == null ? "" : " reason=" + reason));
            if (state == "unreachable")
            {
                r.Style = "red"; r.ExpireS = _g.NowS + 8;
                if (!double.IsNaN(r.ExpireS)) _ended.Add(r);
            }
            Emit(r, reason);
        }

        private void Emit(Run r, string reason)
        {
            var sb = new StringBuilder();
            sb.Append("{\"uid\":").Append(r.Uid).Append(",\"target_id\":").Append(r.TargetId).Append(",\"state\":").Append(Json.Str(r.State));
            sb.Append(",\"reason\":").Append(reason == null ? "null" : Json.Str(reason)).Append('}');
            _g.Event("intercept_state", sb.ToString());
        }

        /// <summary>T-160: the unit has a run (any state of _runs; _ended does not count). eta = "~m:ss" of the time left, or null.</summary>
        public bool UnitActive(int ownUid, out string eta)
        {
            eta = null;
            Run r = Find(ownUid);
            if (r == null) return false;
            double left = Math.Max(0, r.EtaS - (_g.NowS - r.PlanAtS));
            if (!double.IsNaN(left)) eta = "~" + InterceptMath.FmtEta(left);
            return true;
        }

        /// <summary>Menu "Cancel intercept" / intercept_cancel: the route is not touched. null = ok | not_active.</summary>
        public string Cancel(int ownUid, string why)
        {
            Run r = Find(ownUid);
            if (r == null) return "not_active";
            End(r, "cancelled", why ?? "cancel");
            return null;
        }

        /// <summary>"Return control" participant: forget the unit; restore_control gives the route and the telegraph back.</summary>
        public void ReleaseUnit(int uid)
        {
            Run r = Find(uid);
            if (r == null) return;
            End(r, "cancelled", "return_control");
        }

        public void OnMissionLeft()
        {
            _runs.Clear(); _ended.Clear();
            if (_route != null) _route.Clear();
            _lastTickS = double.NaN;
        }

        // ------------------------------------------------------------------ status, view

        public string StatusJson(int uid)
        {
            var sb = new StringBuilder();
            if (uid > 0)
            {
                Run r = Find(uid);
                if (r == null) return "{\"uid\":" + uid + ",\"state\":\"none\"}";
                StatusOne(sb, r);
                return sb.ToString();
            }
            sb.Append("{\"intercepts\":[");
            for (int i = 0; i < _runs.Count; i++) { if (i > 0) sb.Append(','); StatusOne(sb, _runs[i]); }
            sb.Append("]}");
            return sb.ToString();
        }

        private void StatusOne(StringBuilder sb, Run r)
        {
            sb.Append("{\"uid\":").Append(r.Uid).Append(",\"target_id\":").Append(r.TargetId).Append(",\"state\":").Append(Json.Str(r.State));
            sb.Append(",\"mode\":").Append(Json.Str(_p.Mode)).Append(",\"kind\":").Append(Json.Str(r.Kind ?? ""));
            sb.Append(",\"frozen\":").Append(Json.Bool(r.Frozen)).Append(",\"held\":").Append(Json.Bool(r.Held));
            sb.Append(",\"route_pts\":").Append(r.Pts.Count).Append(",\"step\":").Append(r.Step);
            double left = double.IsNaN(r.EtaS) ? double.NaN : Math.Max(0, r.EtaS - (_g.NowS - r.PlanAtS));
            sb.Append(",\"eta_s\":").Append(double.IsNaN(left) ? "null" : Num(left));
            sb.Append(",\"ip\":{\"lat\":").Append(Json.Num(r.Ip.Lat)).Append(",\"lon\":").Append(Json.Num(r.Ip.Lon)).Append('}');
            AppendZones(sb, r.Zones);
            sb.Append('}');
        }

        public IList<IcView> View()
        {
            var res = new List<IcView>();
            if (!_p.ShowLine) return res;
            foreach (var r in _runs) res.Add(ViewOf(r));
            foreach (var r in _ended) res.Add(ViewOf(r));
            return res;
        }

        private IcView ViewOf(Run r)
        {
            var line = new List<GeoPt> { r.LastOwn };
            if (r.Pts.Count > 0) line.AddRange(r.Pts); else line.Add(r.Ip);
            double dist = Dist(r.LastOwn, r.Ip);
            string label;
            if (r.Held && r.HoldHit != null) label = Tx("icpt.line_held", "Intercept · stopped: {0} zone", NameText(r.HoldHit));
            else if (r.State == "routing") label = Tx("icpt.line_routing", "Intercept · routing...");
            else if (r.Kind == "cpa") label = Tx("icpt.line_cpa", "Can't catch up · {0} nm", dist.ToString("0.0", Inv));
            else
            {
                double left = double.IsNaN(r.EtaS) ? double.NaN : Math.Max(0, r.EtaS - (_g.NowS - r.PlanAtS));
                label = Tx("icpt.line_label", "Intercept · {0} nm · ~{1} · {2}° · {3} kn", dist.ToString("0.0", Inv), InterceptMath.FmtEta(left),
                    Math.Round(r.CourseDeg).ToString("0", Inv), Math.Round(r.StepKn).ToString("0", Inv));
            }
            if (!r.Held && r.Zones.Count > 0) label += " " + ZonesText(r.Zones);
            return new IcView { Uid = r.Uid, TargetId = r.TargetId, State = r.State, Style = r.Style, Line = line, Ip = r.Ip, Label = label };
        }
    }
    /// <summary>T-138 V: the map labels of the intercept lines for IMapExtraLabels (owner "intercept"). Pure.</summary>
    internal static class InterceptLabels
    {
        public const string Owner = "intercept";
        public const int KeyBase = -2000000;   // negative: never clashes with track ids / unit uids; the waypoint labels use -1000000 - uid
        public const uint ArgbActive = 0xFFFFE08Au, ArgbGrey = 0x99C0C0C0u, ArgbRed = 0xFFFF6A5Au;

        public static uint ArgbOf(string style) { return style == "red" ? ArgbRed : style == "grey" ? ArgbGrey : ArgbActive; }

        /// <summary>One label per line, at the intercept point. Lines without a label text or with a bad point give none.</summary>
        public static List<LabelItem> Build(IList<IcView> views)
        {
            var res = new List<LabelItem>();
            if (views == null) return res;
            foreach (var v in views)
            {
                if (v == null || string.IsNullOrEmpty(v.Label) || double.IsNaN(v.Ip.Lat) || double.IsNaN(v.Ip.Lon)) continue;
                res.Add(new LabelItem { Key = KeyBase - v.Uid, Lat = v.Ip.Lat, Lon = v.Ip.Lon, Text = v.Label, Dim = v.Style == "grey", Argb = ArgbOf(v.Style) });
            }
            return res;
        }

        /// <summary>Cheap change signature (labels are re-published only when it changes).</summary>
        public static string Signature(IList<LabelItem> items)
        {
            if (items == null || items.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var it in items)
                sb.Append(it.Key).Append('|').Append(Math.Round(it.Lat, 4).ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                  .Append(Math.Round(it.Lon, 4).ToString("0.####", CultureInfo.InvariantCulture)).Append('|').Append(it.Text).Append('|').Append(it.Argb).Append(';');
            return sb.ToString();
        }
    }
}
