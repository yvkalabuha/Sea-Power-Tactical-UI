using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-176 / ADR-0076: the search patterns v3 of the ASW controller: the order spec, the anchor of a moving axis, the lattice of an area,
    // the corridor / barrier / dip chain by course, the point filter. All of the state lives in one field, Grp.Plan.
    internal sealed partial class AswController
    {
        /// <summary>A lane of a member (T-176 C9): the tactic, the side of the corridor and the weight the share was made from.</summary>
        private sealed class LaneRec { public string Tactic; public int Side; public double W; public int Index, K; }

        /// <summary>One row of the sliding barrier (T-229, ADR-0094), fixed on the ground: Seq is the number from the start of the band (even / odd = the stagger), XNm its distance from the band origin,
        /// Open the points not given to an aircraft yet, Taken the aircraft that took a part of it.</summary>
        private sealed class BarrierRec
        {
            public GeoPt Center; public double AxisDeg, HalfNm, LaidS, XNm; public int Buoys, Seq, Skipped; public bool Passed;   // Skipped (T-334): points closed for lack of slack, not saved
            public List<GeoPt> Open = new List<GeoPt>(); public HashSet<int> Taken = new HashSet<int>();
        }

        /// <summary>The band of the barrier: origin and axis fixed on the ground (not the corridor anchor), the rows, the start of a steady axis deviation.</summary>
        private sealed class BarrierBand
        {
            public bool Set; public GeoPt O; public double AxisDeg, T0, DevSinceS; public int NextSeq;
            public int Id;   // T-322: the id of the band for the session (StartBand gives it), not saved; a band from a save has 0
            public List<BarrierRec> Rows = new List<BarrierRec>();
        }

        /// <summary>Everything the v3 patterns keep for one group.</summary>
        private sealed class AswPlanState
        {
            public AswCourseSpec Course = new AswCourseSpec();                      // the by-course order (a copy of the command)
            public string AreaPattern = "auto";                                     // auto | grid | dip | mad
            public AswAnchor Anchor;                                                // the lattice anchor of a moving axis
            public List<LatNode> Nodes;                                             // the area lattice
            public Dictionary<int, List<int>> Sectors = new Dictionary<int, List<int>>();   // uid -> lattice nodes of the sector
            public HashSet<int> NodesDone = new HashSet<int>();
            public Dictionary<int, LaneRec> Lanes = new Dictionary<int, LaneRec>();
            public double CursorL, CursorR;                                         // the corridor cursors of the left and right sides (x, nm)
            public BarrierBand Band = new BarrierBand();                            // T-229: the sliding barrier
            public List<double> DipTaken = new List<double>();                      // x of the dip points of the chain (nm)
            public List<GeoPt> Deferred = new List<GeoPt>();                        // hazard / air points to repeat (the filter)
            public double LastRebalanceS = -1e9;
            public string LastR = "";                                               // the radius the lattice was built for
            public string Warn;                                                     // last plan warning code
            // area (C5)
            public GeoPt AreaC; public double AreaNorth, AreaEast, AreaStep, AreaStepMin;        // the rectangle (centre, spans) and the lattice step
            public List<LatNode> DipNodes; public HashSet<int> DipVisited = new HashSet<int>();   // the dipping lattice of the area
            public string SectorSig = "", MadSig = "";
            public Dictionary<int, List<GeoPt>> MadPts = new Dictionary<int, List<GeoPt>>(); public Dictionary<int, int> MadIdx = new Dictionary<int, int>();
            public double ReseedCheckS = -1e9; public int RowsTotal, RowsLaid;
            public int ViewSig = -1;
            public HashSet<int> NodeLaid = new HashSet<int>();                       // area nodes with a buoy of the group (a node that was only skipped is never re-seeded)
            public List<GeoPt> OpenAll = new List<GeoPt>(), OpenDip = new List<GeoPt>(), OpenMad = new List<GeoPt>();   // the rest of the area plan for the view
        }

        // ---- the order spec (T-176 C1) -----------------------------------------------------------------------------

        /// <summary>A new order: the v3 state of the old one goes (the anchor, lattice, cursors, laid lines); the order spec stays.</summary>
        private void ResetPlanState(Grp g)
        {
            var old = g.Plan;
            g.Plan = new AswPlanState { Course = old.Course, AreaPattern = old.AreaPattern };
            g.HoldAxis = null; g.WasSteered = false; g.SteerEndS = -1; g.LastSteerS = -1; g.Frozen = false; g.StandSinceS = -1; g.FreezeWaitLogged = null;   // T-251, T-264, T-266, T-274
            _planLines.Remove(g);
        }

        /// <summary>Cruise speed of a member: the game value when it is known, else 100 kn for a helicopter and 250 kn for an aeroplane.</summary>
        private static double SpeedOf(AswMember m) { return m.SpeedKn > 0 ? m.SpeedKn : m.Helicopter ? AircraftKn : 250; }

        /// <summary>The depth mode for the aircraft: the group's, "auto" = below the layer for the corridor / barrier, alternating in an area, a cross or shallow water (no layer under 500 ft).</summary>
        private string DepthModeOf(Grp g, AswMember m)
        {
            if (g.Depth == "auto")
            {
                double waterFt = 0;
                var p = (m.Lat != 0 || m.Lon != 0) ? new GeoPt(m.Lat, m.Lon) : AswPatternMath.Lead(g.Plan.Anchor, _now);
                try { double h = _game.HeightM(p); if (h < 0) waterFt = -h / 0.3048; } catch { }
                return AswPatternMath.DepthAuto(g.Tactics, g.Order, waterFt);
            }
            return AswMath.ParseDepth(g.Depth) != null ? g.Depth : "alternate";
        }

        private static readonly string[] AutoCourse = { "dip", "corridor" };
        private static readonly string[] AutoArea = { "dip", "grid", "mad" };

        private bool AnyBuoys(Grp g)
        {
            foreach (int u in g.Members) { AswMember m; if (_live.TryGetValue(u, out m) && BuoyCount(m) > 0) return true; }
            return false;
        }

        /// <summary>
        /// The by-course spec of a command: the v3 fields as given, the old ones (rows | cross | mad, lead_nm, width_nm) through AswPatternMath.MigrateCourse,
        /// every missing value from the [Asw] defaults. madRemoved: a "mad" of an old order that no member can lay (no buoys).
        /// </summary>
        private AswCourseSpec CourseSpecOf(AswCommandsParse.CourseCmd cmd, Grp g, out bool madRemoved)
        {
            var pat = _defaults.Pat;
            bool old = cmd.LeadNm.HasValue || cmd.WidthNm.HasValue || cmd.Tactics.Contains("rows") || cmd.Tactics.Contains("cross") || cmd.Tactics.Contains("mad");
            var spec = AswPatternMath.MigrateCourse(cmd.Tactics, cmd.LeadNm ?? 0, cmd.LeadNm ?? 0, cmd.WidthNm ?? 0, AnyBuoys(g), out madRemoved);
            // values the command did not give: the [Asw] defaults (an old command keeps what the migration table says)
            if (!(old && (cmd.Tactics.Contains("rows") || cmd.Tactics.Contains("mad")))) spec.CorridorXNm = AswPatternMath.Snap(pat.CorridorWidthNm, AswPatternMath.WidthList);
            if (!(old && cmd.Tactics.Contains("cross") && cmd.WidthNm.HasValue)) spec.BarrierXNm = AswPatternMath.Snap(pat.BarrierWidthNm, AswPatternMath.WidthList);
            if (!(old && cmd.LeadNm.HasValue))
            {
                spec.CorridorLNm = AswPatternMath.Snap(pat.CorridorStandoffNm, AswPatternMath.StandoffList);
                spec.DipLNm = AswPatternMath.Snap(pat.DipStandoffNm, AswPatternMath.StandoffList);
            }
            if (!(old && cmd.Tactics.Contains("cross"))) spec.BarrierLNm = AswPatternMath.Snap(pat.BarrierStandoffNm, AswPatternMath.StandoffList);
            if (cmd.CorridorX.HasValue) spec.CorridorXNm = cmd.CorridorX.Value;
            if (cmd.BarrierX.HasValue) spec.BarrierXNm = cmd.BarrierX.Value;
            if (cmd.StandDip.HasValue) spec.DipLNm = cmd.StandDip.Value;
            if (cmd.StandCorridor.HasValue) spec.CorridorLNm = cmd.StandCorridor.Value;
            if (cmd.StandBarrier.HasValue) spec.BarrierLNm = cmd.StandBarrier.Value;
            spec.BarrierRows = Math.Max(1, Math.Min(5, cmd.BarrierRows ?? pat.BarrierRows));                                            // T-229
            spec.BarrierGapNm = AswPatternMath.Snap(cmd.BarrierGap ?? pat.BarrierGapNm, AswPatternMath.BarrierGapList);
            spec.Depth = cmd.Depth ?? "auto";
            if (spec.Tactics.Count == 0 && !madRemoved) spec.Tactics.AddRange(AswPatternMath.ComboTactics(pat.CourseTactic));   // the empty list = the default of [Asw]
            return spec;
        }

        /// <summary>The tactics the group works with: the spec's, or (auto) dipping sonar + corridor.</summary>
        private static List<string> CourseTactics(AswCourseSpec spec)
        {
            return spec.Tactics.Count > 0 ? new List<string>(spec.Tactics) : new List<string>(AutoCourse);
        }

        /// <summary>An old tactic id read as of the current order mode (rows | cross -> corridor | barrier by course, grid in an area; mad by course -> corridor).</summary>
        private static string NormTactic(string order, string t)
        {
            if (order == "area") return t == "rows" || t == "cross" || t == "corridor" || t == "barrier" ? "grid" : t;
            return t == "rows" || t == "mad" ? "corridor" : t == "cross" ? "barrier" : t;
        }

        private static List<string> NormTactics(string order, IEnumerable<string> ts)
        {
            var res = new List<string>();
            foreach (string t in ts) { string n = NormTactic(order, t); if (!res.Contains(n)) res.Add(n); }
            return res;
        }

        /// <summary>T-176 C2: the v3 order of a restored group; an old file (no "course" / "area_pattern") goes through the migration table of ADR-0076 s.9.</summary>
        private void RestorePattern(Grp g, AswGroupDto dto)
        {
            if (g.Order == "course")
            {
                if (dto.Course != null) g.Plan.Course = dto.Course;
                else
                {
                    bool removed;
                    // the old width_nm of a group was the MAD snake: the barrier width comes from the table default (40)
                    g.Plan.Course = AswPatternMath.MigrateCourse(dto.Tactics, g.P.LeadMadNm, g.P.LeadDipNm, 0, AnyBuoys(g), out removed);
                    g.Plan.Course.Depth = "auto";
                    if (removed) Warn(g, "tactic_removed", "mad", 0);
                    _game.Log("asw restore " + g.Name + " migrated tactics=" + string.Join(",", dto.Tactics.ToArray()) + " -> " + AswPatternMath.ComboOf(g.Plan.Course.Tactics));
                }
                // T-334 / ADR-0130: corridor and barrier exclude each other; a save with both keeps the barrier (the corridor holders are given the freed tactic by the next Reassign)
                bool exclusive;
                var kept = AswPatternMath.ExclusiveTactics(g.Plan.Course.Tactics, out exclusive);
                if (exclusive)
                {
                    string was = string.Join(",", g.Plan.Course.Tactics.ToArray());
                    g.Plan.Course.Tactics = kept;
                    _game.Log("asw restore " + g.Name + " tactics " + was + " -> " + AswPatternMath.ComboOf(kept) + " (exclusive)");
                    foreach (int u in new List<int>(g.Assign.Keys)) if (g.Assign[u] == "corridor") g.Assign.Remove(u);
                }
                g.Tactics = CourseTactics(g.Plan.Course);
                if (dto.Barrier != null && g.Tactics.Contains("barrier")) g.Plan.Band = BandFrom(dto.Barrier);   // T-229: the rows stay where they were
                g.SeedAxis = dto.FrameAxisDeg ?? (dto.Barrier != null && g.Tactics.Contains("barrier") ? (double?)dto.Barrier.AxisDeg : null);   // T-238: the first anchor after the load takes it
                g.HoldAxis = g.Tactics.Contains("barrier") && g.SeedAxis.HasValue ? g.SeedAxis : null; g.HoldSinceS = 0;   // T-251: the axis of the save holds the barrier until the route confirms it
            }
            else if (g.Order == "area")
            {
                g.Plan.AreaPattern = dto.AreaPattern ?? AswPatternMath.MigrateArea(dto.Tactics.Count == 1 ? dto.Tactics[0] : "auto");
                g.Tactics = NormTactics("area", dto.Tactics);
            }
        }

        /// <summary>Test / window access to the order spec of a group (null = no such group).</summary>
        internal AswCourseSpec SpecOf(string group) { var g = Find(group); return g == null ? null : g.Plan.Course; }
        internal int BarrierLines(string group) { var g = Find(group); return g == null ? 0 : RowsAhead(g).Count; }
        /// <summary>T-229 test / dev: "seq:x:open/total:passed;..." of the band rows.</summary>
        internal string BarrierRowsInfo(string group)
        {
            var g = Find(group); if (g == null) return "";
            var sb = new StringBuilder();
            foreach (var b in g.Plan.Band.Rows) { if (sb.Length > 0) sb.Append(';'); sb.Append(I(b.Seq)).Append(':').Append(N(Math.Round(b.XNm, 1))).Append(':').Append(I(b.Open.Count)).Append('/').Append(I(b.Buoys)).Append(':').Append(b.Passed ? "1" : "0"); }
            return sb.ToString();
        }
        internal string AreaPatternOf(string group) { var g = Find(group); return g == null ? null : g.Plan.AreaPattern; }

        // ---- the anchor of the moving axis (T-176 C3, ADR-0076 s.5) ------------------------------------------------

        /// <summary>The widest corridor / barrier of the order (the drift limit of the anchor is max(R, X/8)); 0 = a dipping-only order.</summary>
        private double OrderXNm(Grp g)
        {
            double x = 0;
            if (g.Tactics.Contains("corridor")) x = Math.Max(x, g.Plan.Course.CorridorXNm);
            if (g.Tactics.Contains("barrier")) x = Math.Max(x, g.Plan.Course.BarrierXNm);
            return x;
        }

        /// <summary>
        /// The search frame follows the target's axis (T-142 K, T-150): StableAxis holds it inside AxisKeepDeg degrees. The pattern is built on an anchor fixed on the ground
        /// (AswPatternMath.Reanchor: turn / drift / speed / stand); a re-anchor needs ReplanMinS since the last one. g.Axis / g.PlanAxis / g.PlanV mirror the anchor.
        /// </summary>
        private void UpdateFrame(Grp g, AswTarget t)
        {
            var pat = _defaults.Pat;
            string src;
            double rawAxis = AswMath.AxisDeg(t.NextWpBrg, t.CourseDeg, t.Track, out src);
            // T-245 / T-251 / T-260 / T-264: the steering state and the post-evasion hold are computed before the axis (the barrier without a route needs PostSteer)
            double v = t.SpeedKn;
            if (t.ModSteered) { g.WasSteered = true; g.LastSteerS = _now; }
            else if (g.WasSteered) { g.WasSteered = false; g.SteerEndS = _now; }
            // T-260 / ADR-0104: the hold ends when the heading is back on the player's route (<= 180 s); axis and speed are held apart
            var ps = g.SteerEndS >= 0
                ? AswPatternMath.PostSteer(g.SteerEndS, _now, AswPatternMath.PostSteerMaxS, pat.ReanchorHoldS, t.CourseDeg, t.RouteBrg, g.PlanAxis, v, g.PlanV, pat.ReanchorDeg)
                : default(AswPatternMath.AswPostSteer);
            if (g.SteerEndS >= 0 && ps.End != null)
            {
                _game.Log("asw pattern " + g.Name + " frame hold end reason=" + ps.End);
                g.SteerEndS = -1;
            }
            // T-266 / ADR-0106: the barrier of a ship that cannot move keeps anchor, frame axis and band axis (no reanchor, no reset; the save gets the axis from before)
            bool frz = AswRouteGuard.FreezeImmobile(g.Tactics.Contains("barrier") && g.Plan.Band.Set, g.FrameSet && g.Plan.Anchor.Set, t.ImmobileWhy);
            // T-274 / ADR-0110: why a slow barrier is not frozen (once per change of the reason)
            string fw = AswRouteGuard.FreezeWait(g.Tactics.Contains("barrier"), g.Plan.Band.Set, g.FrameSet && g.Plan.Anchor.Set, t.ImmobileWhy, t.WayPending, t.SpeedKn);
            if (fw != g.FreezeWaitLogged)
            {
                g.FreezeWaitLogged = fw;
                if (fw != null) _game.Log("asw pattern " + g.Name + " barrier freeze wait reason=" + fw + " kn=" + N(Math.Round(t.SpeedKn, 1)) + " tg=" + (t.Telegraph.HasValue ? I(t.Telegraph.Value) : "na") + " imm=" + (t.ImmobileWhy ?? "-"));
            }
            if (frz && !g.Frozen)
            {
                g.Frozen = true;
                _game.Log("asw pattern " + g.Name + " barrier frozen reason=immobile why=" + t.ImmobileWhy + " axis=" + N(Math.Round(g.BandAxisSet ? g.BandAxis : g.Plan.Band.AxisDeg)));
            }
            if (frz) { g.ReplanInS = -1; g.V = t.SpeedKn; return; }
            if (g.Frozen)
            {
                g.Frozen = false; g.Plan.Band.DevSinceS = 0; g.LastReplanS = _now;   // the old deviation timer must not reset the band at once
                _game.Log("asw pattern " + g.Name + " barrier frozen end axis=" + N(Math.Round(g.BandAxisSet ? g.BandAxis : g.Plan.Band.AxisDeg)));
            }
            // T-260 / ADR-0104: a barrier with a band takes the frame axis from the player's route, not from the track (it still holds the evasion leg)
            if (AswRouteGuard.FrameByRoute(g.Tactics.Contains("barrier") && g.Plan.Band.Set, g.FrameSet && g.Plan.Anchor.Set, t.ModSteered, t.RouteBrg)) { rawAxis = AswMath.Norm360(t.RouteBrg.Value); src = "route"; }
            else if (AswRouteGuard.FrameByHeading(g.Tactics.Contains("barrier") && g.Plan.Band.Set, g.FrameSet && g.Plan.Anchor.Set, t.ModSteered, t.RouteBrg, g.LastSteerS, _now, AswMath.TrackSpanS)) { rawAxis = ps.HoldAxis ? g.PlanAxis : AswMath.Norm360(t.CourseDeg); src = ps.HoldAxis ? "post_hold" : "post_heading"; }
            double axis = AswRouteGuard.StableAxis(g.RawAxis, g.FrameSet, rawAxis, src, t.CourseDeg, v, _defaults.AxisMinKn, _defaults.AxisKeepDeg, out src);
            if (src != g.LastAxisSrc || AswMath.AngleDiff(axis, g.LastAxisLogged) > 5 || _now - g.LastAxisLogS >= 60)
            {
                g.LastAxisSrc = src; g.LastAxisLogged = axis; g.LastAxisLogS = _now;
                _game.Log("asw frame " + g.Name + " axis=" + N(Math.Round(axis)) + " src=" + src + " heading=" + N(Math.Round(t.CourseDeg)) + " v=" + N(Math.Round(v, 1)));
            }
            g.RawAxis = axis;
            g.ReplanInS = -1;
            var pos = Pos(t);
            if (!g.FrameSet || !g.Plan.Anchor.Set)
            {
                // T-238 (N6): after a load the course reads 0 and there is no track: the anchor takes the axis of the save, not the heading
                // T-251 / ADR-0103: also with a barrier or on an evasion (the route then is the evasion point, not the player's)
                if (g.SeedAxis.HasValue && (src.StartsWith("heading", StringComparison.Ordinal) || g.Tactics.Contains("barrier") || t.ModSteered)) { axis = g.SeedAxis.Value; src = "save"; g.RawAxis = axis; }
                else if (t.ModSteered)
                {
                    // T-251: a new order on an evasion takes the player's route, not the evasion course
                    if (t.RouteBrg.HasValue) { axis = AswMath.Norm360(t.RouteBrg.Value); src = "route"; g.RawAxis = axis; }
                    else if (t.SavedCourseDeg.HasValue) { axis = AswMath.Norm360(t.SavedCourseDeg.Value); src = "saved_course"; g.RawAxis = axis; }
                }
                g.SeedAxis = null;
                g.WasSteered = t.ModSteered; g.SteerEndS = -1; g.LastSteerS = t.ModSteered ? _now : -1; g.Frozen = false; g.StandSinceS = -1; g.FreezeWaitLogged = null;   // T-266, T-274
                AswPatternMath.Reanchor(ref g.Plan.Anchor, axis, v, pos, _now, pat.ReanchorDeg, pat.ReanchorHoldS, 0);
                g.PlanAxis = axis; g.Axis = axis; g.PlanV = v; g.FrameSet = true; g.CursorSet = false; g.LastReplanS = _now;
                _game.Log("asw pattern " + g.Name + " " + AswPatternMath.ComboOf(g.Tactics) + " anchor axis=" + N(Math.Round(axis)) + " o=" + pos.Lat.ToString("0.0000", CultureInfo.InvariantCulture) + "," + pos.Lon.ToString("0.0000", CultureInfo.InvariantCulture)
                    + " v=" + N(Math.Round(v, 1)) + " R=" + N(Math.Round(g.RNm, 2)) + " src=" + src);
                g.V = v;
                return;
            }
            // T-245 (N2): while the mod steers the ship (evasion) the frame stays as it was - the barrier band is held the same way (ADR-0098); turn / speed would only cancel the packets
            // T-251 / ADR-0103: the frame also waits for the ship to come back after the evasion (up to ReanchorHoldS) and while the axis of the save holds the band
            if (g.Tactics.Contains("barrier") && g.Plan.Band.Set)
            {
                if (t.ModSteered || g.HoldAxis.HasValue) { axis = g.PlanAxis; v = g.PlanV; }
                else
                {
                    if (ps.HoldAxis) axis = g.PlanAxis;
                    if (ps.HoldSpeed) v = g.PlanV;
                }
            }
            double r = g.RNm > 0 ? g.RNm : 2.7;
            double drift = Math.Max(r, OrderXNm(g) / 8);
            AswAnchor before = g.Plan.Anchor, a = before;
            string why = AswPatternMath.Reanchor(ref a, axis, v, pos, _now, pat.ReanchorDeg, pat.ReanchorHoldS, drift);
            if (why == null) g.Plan.Anchor = a;
            else if (g.LastReplanS >= 0 && _now - g.LastReplanS < _defaults.ReplanMinS)
            {
                g.Plan.Anchor = before;   // seen, but the interval has not passed yet: the condition stays and is checked again
                g.ReplanInS = Math.Max(0, g.LastReplanS + _defaults.ReplanMinS - _now);
            }
            else
            {
                double turned = AswMath.AngleDiff(g.PlanAxis, axis);
                double lat = Math.Abs(AswMath.ToLocal(before.O, before.AxisDeg, pos).Y);
                g.Plan.Anchor = a;
                bool keep = AswPatternMath.KeepPacket(turned);
                _game.Log("asw pattern " + g.Name + " reanchor reason=" + why + " d_axis=" + N(Math.Round(turned, 1)) + " lat_nm=" + N(Math.Round(lat, 2)) + " packet=" + (keep ? "kept" : "cancelled"));
                g.PlanAxis = axis; g.Axis = axis; g.PlanV = v; g.CursorSet = false; g.NextCrossAt = 0; g.NextRingAt = 0; g.LastReplanS = _now;
                // what is still queued along the old axis goes when the axis really turned (a package in flight survives a turn below 45 degrees, T-172)
                foreach (int u in g.Members)
                {
                    AswMember am; string tac;
                    if (!_live.TryGetValue(u, out am) || am.Where != "air" || !g.Assign.TryGetValue(u, out tac) || (tac != "corridor" && tac != "rows")) continue;   // T-229: the barrier band does not follow the anchor (it has its own reset)
                    Job inFlight;
                    if (keep && g.Jobs.TryGetValue(u, out inFlight) && inFlight.IsDrop && _now < inFlight.DoneAt) continue;
                    CancelRoute(g, u); g.Jobs.Remove(u);
                }
            }
            g.V = v;
        }

        // ---- the point filter (T-176 C4, ADR-0076 s.6) -------------------------------------------------------------

        /// <summary>What the last Guard call dropped / deferred (for the package log line).</summary>
        private struct SkipCount { public int Land, Shallow, Hazard, Air, Deferred; public string Circles; /* T-180: names of the circles that fired */ }
        private SkipCount _skip;

        /// <summary>T-180: " circles=t12:Surface:5,t40:air:50" of the last Guard call, or "".</summary>
        private string SkipCirclesNote() { return string.IsNullOrEmpty(_skip.Circles) ? "" : " circles=" + _skip.Circles; }

        private static string HazardLabel(AswHazard h, double r) { return "t" + I(h.TrackId) + ":" + (h.Kind ?? "-") + ":" + N(Math.Round(r, 1)); }

        private IList<AswCircle> DangerCircles(GeoPt? from = null)
        {
            var res = new List<AswCircle>();
            try
            {
                var hz = _game.Hazards();
                if (hz != null) foreach (var h in hz) { double hr = h.RangeNm > 0 ? h.RangeNm : _defaults.HazardNm; res.Add(new AswCircle { C = h.P, RNm = hr, Why = AswSkip.Hazard, Label = HazardLabel(h, hr) }); }
                if (AirGuard != null)
                {
                    var ap = AirGuard.P;
                    var tr = AirGuard.Tracks();
                    if (tr != null)
                        foreach (var t in tr)
                        {
                            if (!t.Hostile || t.Destroyed) continue;
                            double r = ap.RetreatNmHelo;
                            // T-180: a track that does not approach (and is not close) only blocks its own radius + margin
                            if (from.HasValue && !AirGuardMath.HeloHolds(t, AswMath.DistNm(from.Value, new GeoPt(t.Lat, t.Lon), t.Globe), AirGuardMath.TrackClosingKn(t, from.Value.Lat, from.Value.Lon), ap)) r = t.RNm + ap.PathMarginNm;
                            res.Add(new AswCircle { C = new GeoPt(t.Lat, t.Lon), RNm = r, Why = AswSkip.Air, Label = "t" + I(t.Id) + ":air:" + N(Math.Round(r, 1)) });
                        }
                }
            }
            catch (Exception e) { _game.Log("asw circles error: " + e.Message); }
            return res;
        }

        /// <summary>
        /// One pass before every package (after the merge gap). land and shallow points (buoy: sensor depth + 3 m, dipping sonar: body + cable) go for good; hazard (known air defence)
        /// and air (stage 2 of the air threat, T-175) points are deferred when <paramref name="defer"/> (kind: buoy | dip | fly - a route of an aircraft without drops: land only) and tried again with the next package; a leg that crosses a circle defers its far point.
        /// </summary>
        private List<GeoPt> Guard(Grp g, IList<GeoPt> pts, string kind = "buoy", double sonarFt = 0, AswMember by = null, bool defer = false)
        {
            GuardCalls++;
            var pat = _defaults.Pat;
            bool dip = kind == "dip";
            double minW = kind == "fly" ? 0 : dip ? AswRouteGuard.MinWaterDipM(pat.MinWaterDipM) : AswRouteGuard.MinWaterBuoyM(pat.MinWaterBuoyM, sonarFt);
            double lm = _defaults.LandM;
            var hz = _game.Hazards();
            Func<GeoPt, bool> air = null;
            if (AirGuard != null) air = p => { var th = AirGuard.AtPoint(p, true); return th != null && th.Stage >= AirStage.Retreat; };
            bool real = g.Name != "preview";
            var labels = new List<string>();
            var cand = new List<GeoPt>();
            if (defer && real && g.Plan.Deferred.Count > 0)
            {
                foreach (var d in g.Plan.Deferred) { bool dup = false; foreach (var p in pts) if (AswMath.DistNm(p, d) < 0.05) dup = true; if (!dup) cand.Add(d); }
                g.Plan.Deferred.Clear();
            }
            cand.AddRange(pts);
            var sk = new SkipCount();
            var res = new List<GeoPt>();
            foreach (var p in cand)
            {
                double h = _game.HeightM(p) - lm;
                int hi;
                switch (AswRouteGuard.Classify(p, dip, h, minW, hz, _defaults.HazardNm, air, out hi))
                {
                    case AswSkip.Land: sk.Land++; break;
                    case AswSkip.Shallow: sk.Shallow++; break;
                    case AswSkip.Hazard:
                        sk.Hazard++;
                        if (hi >= 0 && hz != null) { var hh = hz[hi]; string lb = HazardLabel(hh, hh.RangeNm > 0 ? hh.RangeNm : _defaults.HazardNm); if (!labels.Contains(lb)) labels.Add(lb); }
                        if (defer && real) g.Plan.Deferred.Add(p); break;
                    case AswSkip.Air: sk.Air++; if (defer && real) g.Plan.Deferred.Add(p); break;
                    default: res.Add(p); break;
                }
            }
            if (by != null && (by.Lat != 0 || by.Lon != 0) && res.Count > 0)
            {
                var from = new GeoPt(by.Lat, by.Lon);
                var circles = DangerCircles(from);
                for (int guard = 0; guard < 64 && circles.Count > 0; guard++)
                {
                    int i = AswRouteGuard.FirstBlockedLeg(from, res, circles);
                    if (i < 0) break;
                    var blocked = res[i];
                    var why = AswSkip.Hazard;
                    foreach (var c in circles) if (AswRouteGuard.SegDistNm(i == 0 ? from : res[i - 1], blocked, c.C) < c.RNm - 1e-9) { why = c.Why; if (c.Label != null && !labels.Contains(c.Label)) labels.Add(c.Label); break; }
                    if (why == AswSkip.Air) sk.Air++; else sk.Hazard++;
                    if (defer && real) g.Plan.Deferred.Add(blocked);
                    res.RemoveAt(i);
                }
            }
            sk.Deferred = g.Plan.Deferred.Count;
            sk.Circles = AswRouteGuard.CirclesNote(labels);
            if (g.Plan.Deferred.Count > 200) g.Plan.Deferred.RemoveRange(0, g.Plan.Deferred.Count - 200);
            _skip = sk;
            int gone = sk.Land + sk.Shallow + sk.Hazard + sk.Air;
            if (gone > 0 && real)
            {
                Warn(g, "points_skipped", "land=" + I(sk.Land) + " shallow=" + I(sk.Shallow) + " hazard=" + I(sk.Hazard) + " air=" + I(sk.Air), 300);
                string sig = SkipSig(g.Name, sk.Circles);
                double realNow = RealClock();
                KeyValuePair<string, double> last;
                bool known = _skipLog.TryGetValue(g.Name, out last);
                if (SkipLogDue(known ? last.Key : null, known ? last.Value : 0, sig, realNow, SkipLogS))
                {
                    _skipLog[g.Name] = new KeyValuePair<string, double>(sig, realNow);
                    _game.Log("asw skip " + g.Name + " land=" + I(sk.Land) + " shallow=" + I(sk.Shallow) + " hazard=" + I(sk.Hazard) + " air=" + I(sk.Air) + " deferred=" + I(sk.Deferred) + " kept=" + I(res.Count) + SkipCirclesNote());
                }
            }
            return res;
        }

        /// <summary>Test access to the point filter (kind buoy | dip | fly); <paramref name="uid"/> != 0 = the legs from that aircraft are checked too. Deferred points are kept in the group.</summary>
        internal List<GeoPt> GuardPoints(string group, IList<GeoPt> pts, string kind, double sonarFt, int uid, out int land, out int shallow, out int hazard, out int air, out int deferred)
        {
            var g = Find(group);
            AswMember m = null;
            if (uid != 0) { RefreshLive(); _live.TryGetValue(uid, out m); }
            var res = Guard(g, pts, kind, sonarFt, m, true);
            land = _skip.Land; shallow = _skip.Shallow; hazard = _skip.Hazard; air = _skip.Air; deferred = g.Plan.Deferred.Count;
            return res;
        }

        // ---- T-191: the "asw skip" line is written on a change of the picture or every SkipLogS ----
        internal const double SkipLogS = 60.0;
        // T-215: the pause runs on real time (the game clock at x4 made 60 s = 15 s)
        internal Func<double> RealClock = DefaultRealClock;
        internal static double DefaultRealClock() { return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency; }
        private readonly Dictionary<string, KeyValuePair<string, double>> _skipLog = new Dictionary<string, KeyValuePair<string, double>>();
        /// <summary>Test counter: how many times the point filter ran.</summary>
        internal int GuardCalls;

        /// <summary>T-289: the signature of a skip line: group + the set of circles only. The counts (deferred points are re-counted on every call) stay out of it.</summary>
        internal static string SkipSig(string group, string circles)
        {
            return group + "|" + (circles ?? "");
        }

        /// <summary>Log when the signature of the group changed or everyS passed since its last line.</summary>
        internal static bool SkipLogDue(string lastSig, double lastAt, string sig, double now, double everyS)
        {
            return lastSig == null || lastSig != sig || now - lastAt >= everyS;
        }
    }
}
