using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-095 / ADR-0036: pure ASW geometry, buoy range, relief planning. No Unity, no reflection.
    // Local frame: X forward along the axis (miles), Y to the right.

    internal struct NmPt
    {
        public double X, Y;
        public NmPt(double x, double y) { X = x; Y = y; }
    }

    internal struct GeoPt
    {
        public double Lat, Lon;
        public GeoPt(double lat, double lon) { Lat = lat; Lon = lon; }
    }

    internal sealed class BuoyType
    {
        public string Ammo, Sensor;
        public double PassiveKm, ActiveKm, GameEstimateNm, LifeS;
        public bool Active, Vlad;
        public double SonarDepthFt;   // T-176: depth the sensor sits at (ft, positive); 0 = unknown
    }

    internal sealed class REffResult
    {
        public double Nm;
        public string Source;   // game | ini | active | clamped:<src>
    }

    /// <summary>[Asw] v3 settings read live (one instance in AswParams.Pat; lives here so the stands that only need AswMath compile).</summary>
    internal sealed class AswPatternParams
    {
        /// <summary>A shallow copy (all fields are values).</summary>
        public AswPatternParams Clone() { return (AswPatternParams)MemberwiseClone(); }

        public string CourseTactic = "auto", AreaTactic = "auto", BuoyDepth = "auto";
        public double CorridorWidthNm = 20, BarrierWidthNm = 40, DipStandoffNm = 20, CorridorStandoffNm = 20, BarrierStandoffNm = 40,
                      MinWaterBuoyM = 30, MinWaterDipM = 30, BarrierStepNm = 10, ReanchorDeg = 30, ReanchorHoldS = 60, ReseedLeadS = 600;
        // T-229 (ADR-0094): the sliding barrier: rows ahead, the interval, the staggered odd rows, the band reset (the axis turn held for a time)
        public double BarrierGapNm = 5, BarrierResetDeg = 30, BarrierResetHoldS = 60;
        public int CorridorMaxRows = 6, BarrierRows = 2;
        public bool BarrierActive, BarrierStagger = true;
    }

    internal sealed class AswParams
    {
        public double LengthNm = 12, WidthNm = 10, ANewNm = 14, DMinNm = 8, LeadMadNm = 8, LeadDipNm = 6,
                      DipHoldS = 120, MadLegNm = 1.6, AreaSizeNm = 10, GameEstimateFactor = 1.0,
                      IniFactor = 0.5, ActiveFactor = 0.7, RMinNm = 0.3, RMaxNm = 20, LostAfterS = 300,
                      TurnaroundS = 3000;
        public int Standby = 1;
        public string Loadouts = "ASWHunter,ASW,ASWKiller";
        // T-150: route guard and adoption (global settings, not saved per group)
        public double HazardNm = 5, AxisMinKn = 5, AxisKeepDeg = 15, AdoptGapS = 300, LandM = 0;
        // T-142 K: a by-course pattern is re-anchored no more often than every ReplanMinS (T-176: the angle is Pat.ReanchorDeg, ReplanDeg is gone)
        public double ReplanMinS = 15;
        public AswPatternParams Pat = new AswPatternParams();   // T-176 (ADR-0076): the v3 search patterns, read live
    }

    /// <summary>Snapshot of one aircraft for the pure functions.</summary>
    internal sealed class AswMember
    {
        public int Uid, DeckUid;
        public int ShipUid;                                 // T-155: the ship of the machine for the "Ship" column: the deck's ship, or the home ship of a machine in the air (0 = unknown / dead)
        public string Name, Type, Where, Base, Loadout;     // Type = vehicle type key (ini / file name), same on the deck and in the air; Where: air|deck_ready|deck_prep|deck_service|deck_launching|deck_recover|player|sar|game_patrol|landed|lost
        public string TypeLabel;                            // display label of the type (deck DisplayName | air ClassName); null -> show Type
        public string[] Roles;                              // game unit roles / loadout roles (ASW, AEW, MPA, ...), may be null
        public double ReadyInS, FuelFrac, EnduranceS, TransitS, LaunchS, OnStationS;
        public double Lat, Lon;
        public double SpeedKn;                              // T-176: cruise speed for the lane weights (0 = unknown: the class default, AswController.SpeedOf)
        public int Passive, ActiveBuoys, Vlad, Torpedoes;
        public Dictionary<string, int> BuoysByAmmo;         // T-172 B4: sonobuoys by ammunition key as the game lists them; null = unknown
        public bool Dipping, Mad;
        public string DipSrc, DipNote;                      // T-188: where Dipping comes from: type (ini of the type) | guess | live | refused; DipNote = why the type's sonar is not loaded
        public bool Evading;                                // T-100: the machine is in a missile evasion turn (game state or mod air_evasion): no tactics, not dropped from the group
        public bool LoadKnown = true;                       // T-154: false = the load of this machine is not known yet (hangar / handed over with no ammo list): the tactic buttons must not grey it
        public double HangarPrepS = -1;                     // T-192: hangar rows: preparation time of an order, -1 = not a hangar row
        public double NextPrepS = -1;                       // T-213: preparation of this machine's next order from the hangar (cool: row - by AswGame; air - by the controller); -1 = unknown
        public string AiState, Task;
        public string DeckKey;                              // deck rows only: "task:<guid>" (pending task) | "hangar:<type>" | "unit:<uid>" | "cool:<guid>" (landed, cooling down)
        public string Serial;                               // T-150: serial number of the machine (air unit, unit: and cool: rows); null = unknown
        public string FromKey;                              // "unit:" row of a LaunchTask: the "task:<guid>" of the pending task it was spawned from (same LaunchTaskParameters); null = unknown
        public int DeckCount;                               // deck rows: machines in the row (hangar: Numbers), 0 elsewhere
        public int Slot;                                    // T-181 (ADR-0080 §1): k of a hangar slot row (1..MaxSlots), 0 = not a slot (task/unit/cool/air, legacy "hangar:<type>")
        public bool Allowed;                                // T-181 §5: the mod already asked this deck machine to take off (create allowLaunch=true or allow); filled by the controller for PlanRelief
        public string DeckPhase;                            // T-181 §4: game state of a task row: ready_up|await_deck|await_crew|await_spawn|launching; null = unknown
        // T-109 (SAR): filled by the game layer; plain data
        public bool Helicopter, GameSarZone;                // class Helicopter; the unit is held by the game's own SAR patrol zone
        public int Onboard, Capacity, BestCapacity;         // people on board / seats of the current loadout / most seats over the loadouts of the type
    }

    internal sealed class AswTaskSpec
    {
        public string Id, Tactic;            // dip|rows|cross|mad|area|contact
        public int NeedBuoys;
        public double BuoysPerHour;
        public bool Holding;
        public int Want;                     // T-181 §5: holders of the tactic (lanes); 0..1 = the old single-relief rule
    }

    internal sealed class ReliefAction
    {
        public string Kind;                  // launch|prepare|dump_buoys|release|warn
        public int Uid;
        public string TaskId, Loadout, Code;
        public double GapMin;
    }

    /// <summary>T-348 (review T-347): per-cell cache of the world (flat / globe). Pure, no Unity. A cell is decided (value kept), undecided by latitude
    /// (near the equator the worlds coincide: the last decided world is used, permanently) or failed (metric unavailable: the last world until RetryS passed).</summary>
    internal sealed class WorldCellCache
    {
        public const int Cap = 4096;
        public const double RetryS = 5.0;
        private const int Decided = 1, Equator = 2, Failed = 3;
        private struct Cell { public int State; public bool Globe; public double FailedAt; }
        private readonly Dictionary<long, Cell> _cells = new Dictionary<long, Cell>();

        /// <summary>The last decided world, initially false.</summary>
        public bool Last { get; private set; }

        /// <summary>true = answered from the cache (globe is the answer); false = the caller must measure (globe = Last meanwhile).</summary>
        public bool TryGet(long key, double nowS, out bool globe)
        {
            globe = Last;
            Cell c;
            if (!_cells.TryGetValue(key, out c)) return false;
            if (c.State == Decided) { globe = c.Globe; return true; }
            if (c.State == Equator) return true;
            if (nowS >= c.FailedAt + RetryS) { _cells.Remove(key); return false; }
            return true;
        }

        /// <summary>Stores a measurement; true when Last changed (the caller logs and sets AswMath.WorldGlobe).</summary>
        public bool Put(long key, double nowS, bool metricOk, bool decided, bool globe)
        {
            if (_cells.Count >= Cap) _cells.Clear();
            var c = new Cell();
            if (!metricOk) { c.State = Failed; c.FailedAt = nowS; _cells[key] = c; return false; }
            if (!decided) { c.State = Equator; _cells[key] = c; return false; }
            c.State = Decided; c.Globe = globe; _cells[key] = c;
            bool changed = globe != Last;
            Last = globe;
            return changed;
        }
    }

    internal static class AswMath
    {
        public const double KmPerNm = 1.852;

        private static readonly string[] AlwaysLogged =
        {
            "asw cmd", "asw launch", "asw prepare", "asw warn", "asw adopt", "asw handover", "asw member", "asw contact", "asw attack", "asw strike",
            "asw sub detected", "asw mad", "asw drop", "asw skip",
            "asw radar",   // T-245: on / restore / player_off / none of the radar of an ASW aircraft (check list 61)
            "asw view",   // T-335: barrier stale / fresh of the view (N3: was missing from LogOutput)
            "asw slot",   // T-200: ADR-0082 s.5 wants every slot move in the log
        };

        /// <summary>T-200: log lines written without [Asw] Debug (the ones that explain what happened to a machine); the noisy ones (route, standby, frame, assign) stay behind Debug.</summary>
        internal static bool LogAlways(string line)
        {
            if (line == null) return false;
            foreach (string p in AlwaysLogged) if (line.StartsWith(p, StringComparison.Ordinal)) return true;
            // T-238 (N8): the barrier lines carry the group name after "asw pattern "
            if (line.StartsWith("asw pattern ", StringComparison.Ordinal))
                foreach (string p in AlwaysLoggedPattern) if (line.IndexOf(p, 12, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static readonly string[] AlwaysLoggedPattern = { " barrier row ", " barrier reset ", " barrier axis=", " anchor axis=", " reanchor ", " barrier hold ", " frame hold ", " barrier frozen", " barrier freeze ", " barrier stand " };   // T-274: frozen / freeze wait / stand defer lines   // T-245: anchor lines (check list 55)   // T-238: after "asw pattern <group>"
        private const double Deg = Math.PI / 180.0;

        public static double Clamp(double nm, AswParams p)
        {
            return nm < p.RMinNm ? p.RMinNm : (nm > p.RMaxNm ? p.RMaxNm : nm);
        }

        // ---- R_eff and spacing ----------------------------------------------------------------------------------

        public static REffResult REff(BuoyType b, AswParams p)
        {
            double raw; string src;
            if (b.Active && b.ActiveKm > 0) { raw = p.ActiveFactor * b.ActiveKm / KmPerNm; src = "active"; }
            else if (b.GameEstimateNm > 0) { raw = b.GameEstimateNm * p.GameEstimateFactor; src = "game"; }
            else if (b.PassiveKm > 0) { raw = p.IniFactor * b.PassiveKm / KmPerNm; src = "ini"; }
            else if (b.ActiveKm > 0) { raw = p.ActiveFactor * b.ActiveKm / KmPerNm; src = "active"; }
            else { raw = 0; src = "ini"; }
            double c = Clamp(raw, p);
            return new REffResult { Nm = c, Source = Math.Abs(c - raw) > 1e-9 ? "clamped:" + src : src };
        }

        // ADR-0063: neighbouring detection circles of one pattern overlap by OverlapFrac of the diameter: spacing = 2 R_eff (1 - overlap) = 1.6 R_eff.
        // R_eff is the range of the very buoy type being dropped (per dropper), so a long-range buoy gets a sparser pattern.
        public const double OverlapFrac = 0.2;
        /// <summary>ADR-0063: a new buoy closer than MinGapFrac * spacing to a buoy in the water (or one already taken into the package) is not dropped.</summary>
        public const double MinGapFrac = 0.7;

        public static double Spacing(double r) { return 2 * r * (1 - OverlapFrac); }
        /// <summary>Overlap of two neighbouring circles on the line between them (nm).</summary>
        public static double OverlapNm(double r) { return 2 * r - Spacing(r); }
        public static double MinGapNm(double r) { return MinGapFrac * Spacing(r); }
        /// <summary>The contact cross is denser by design (neighbours 0.8 R sqrt2 apart, never closer than 1 nm to the contact): its merge gap is MinGapFrac of that.</summary>
        public static double LocalizeGapNm(double r) { return MinGapFrac * Math.Max(0.8 * r, 1.0) * Math.Sqrt(2); }

        public static double NearestNm(GeoPt p, IList<GeoPt> pts)
        {
            double best = double.MaxValue;
            if (pts != null) foreach (var q in pts) { double d = DistNm(p, q); if (d < best) best = d; }
            return best;
        }

        /// <summary>The smallest distance between two points of the list (MaxValue for fewer than two).</summary>
        public static double MinPairNm(IList<GeoPt> pts)
        {
            double best = double.MaxValue;
            for (int i = 0; i < pts.Count; i++) for (int j = i + 1; j < pts.Count; j++) { double d = DistNm(pts[i], pts[j]); if (d < best) best = d; }
            return best;
        }

        /// <summary>
        /// ADR-0063: the points (in their priority order) that are at least gapNm from every existing buoy and from every point kept before them.
        /// <paramref name="skipped"/> = how many were left out. Pure; the order of the kept points is unchanged.
        /// </summary>
        public static List<GeoPt> Thin(IList<GeoPt> pts, IList<GeoPt> existing, double gapNm, out int skipped)
        {
            skipped = 0;
            var res = new List<GeoPt>();
            foreach (var p in pts)
            {
                if ((existing != null && NearestNm(p, existing) < gapNm - 1e-9) || NearestNm(p, res) < gapNm - 1e-9) { skipped++; continue; }
                res.Add(p);
            }
            return res;
        }
        public static double RowGap(double r) { return 1.8 * r; }
        public static int RowCount(double lengthNm, double d) { return (int)Math.Ceiling(lengthNm / d - 1e-9) + 1; }
        public static double XMax(double vKn, double lifeS) { return 0.8 * vKn * lifeS / 3600.0; }
        public static bool BarrierBuoy(double lifeS) { return lifeS >= 900; }

        private static readonly double[] DipSteps = { 1, 2, 4, 6, 8 };

        public static double DipStep(double rDipNm)
        {
            double s = 1.6 * rDipNm, best = 1;
            foreach (double c in DipSteps) if (c <= s + 1e-9) best = c;
            return best;
        }

        public static double DipScreenKn(double stepNm, double holdS, double transitKn = 150)
        {
            double t = holdS + stepNm / transitKn * 3600.0;
            return t <= 0 ? 0 : stepNm / t * 3600.0;
        }

        public static bool DipLeapfrog(double screenKn, double vKn) { return screenKn <= 2 * vKn; }

        // ---- course tactics ----------------------------------------------------------------------------------------

        /// <summary>2+2 rows. Result is in priority order: inner rows first (by X), then outer rows.</summary>
        public static List<NmPt> Rows(double r, double lengthNm, double vKn, double lifeS, int available, out int dropped)
        {
            double d = Spacing(r), p = RowGap(r);
            int n = RowCount(lengthNm, d);
            double xmax = XMax(vKn, lifeS);
            var inner = new List<NmPt>();
            var outer = new List<NmPt>();
            for (int i = 0; i < n; i++)
            {
                double x = i * d;
                inner.Add(new NmPt(x, -r));
                inner.Add(new NmPt(x, r));
                double xo = x + d / 2;
                outer.Add(new NmPt(xo, -(r + p)));
                outer.Add(new NmPt(xo, r + p));
            }
            var all = new List<NmPt>();
            int total = inner.Count + outer.Count;
            foreach (var pt in inner) if (pt.X <= xmax + 1e-9) all.Add(pt);
            foreach (var pt in outer) if (pt.X <= xmax + 1e-9) all.Add(pt);
            if (available < 0) available = 0;
            if (all.Count > available) all.RemoveRange(available, all.Count - available);
            dropped = total - all.Count;
            return all;
        }

        /// <summary>
        /// ADR-0063: the 2+2 rows package starting at X = x0, with the points that <paramref name="taken"/> says are already covered left out; the
        /// package is filled from the next segments of the same lattice (segment = RowCount * spacing, at most maxSegments) - the barrier goes on
        /// ahead instead of seeding the same water again. Count = what Rows would give (inner rows first when buoys are short).
        /// <paramref name="endX"/> = the far end of what was looked at (the cursor when nothing was free).
        /// </summary>
        public static List<NmPt> RowsAhead(double r, double lengthNm, double vKn, double lifeS, int available, double x0, Func<NmPt, bool> taken, int maxSegments,
                                           out int dropped, out int skipped, out double endX)
        {
            skipped = 0; endX = x0;
            var first = Rows(r, lengthNm, vKn, lifeS, available, out dropped);
            int need = first.Count;
            var res = new List<NmPt>();
            if (need == 0) return res;
            double seg = RowCount(lengthNm, Spacing(r)) * Spacing(r);
            for (int s = 0; s < Math.Max(1, maxSegments) && res.Count < need; s++)
            {
                double off = x0 + s * seg;
                foreach (var p in first)
                {
                    if (res.Count >= need) break;
                    var q = new NmPt(p.X + off, p.Y);
                    endX = Math.Max(endX, q.X);
                    if (taken != null && taken(q)) { skipped++; continue; }
                    res.Add(q);
                }
            }
            return res;
        }

        /// <summary>
        /// T-172 O2: like <see cref="RowsAhead"/>, but the lattice is filled column by column (both inner and both outer points of a column before the next
        /// column), so a package cut short by a small load, a contact or a refusal never leaves one row empty ahead of the others: what is covered is a
        /// contiguous block of full columns. Count = what Rows would give for the load; points of the full-size package are the same.
        /// </summary>
        public static List<NmPt> RowsFill(double r, double lengthNm, double vKn, double lifeS, int available, double x0, Func<NmPt, bool> taken, int maxColumns,
                                          out int dropped, out int skipped, out double endX)
        {
            skipped = 0; endX = x0;
            var first = Rows(r, lengthNm, vKn, lifeS, available, out dropped);
            int need = first.Count;
            var res = new List<NmPt>();
            if (need == 0) return res;
            double d = Spacing(r), p = RowGap(r), xmax = XMax(vKn, lifeS);
            int per = RowCount(lengthNm, d);
            for (int c = 0; c < maxColumns && res.Count < need; c++)
            {
                double rel = (c % per) * d, xi = x0 + c * d;
                var col = new List<NmPt>();
                if (rel <= xmax + 1e-9) { col.Add(new NmPt(xi, -r)); col.Add(new NmPt(xi, r)); }
                if (rel + d / 2 <= xmax + 1e-9 + d / 2 && rel <= xmax + 1e-9) { col.Add(new NmPt(xi + d / 2, -(r + p))); col.Add(new NmPt(xi + d / 2, r + p)); }
                foreach (var q in col)
                {
                    if (res.Count >= need) break;
                    endX = Math.Max(endX, q.X);
                    if (taken != null && taken(q)) { skipped++; continue; }
                    res.Add(q);
                }
            }
            return res;
        }

        public static double CrossWidth(double formationWidthNm, double r) { return Math.Max(10, formationWidthNm + 4 * r); }

        public static List<NmPt> Cross(double aNew, double width, double d)
        {
            int n = (int)Math.Ceiling(width / d - 1e-9) + 1;
            var l = new List<NmPt>();
            for (int i = 0; i < n; i++) l.Add(new NmPt(aNew, (i - (n - 1) / 2.0) * d));
            return l;
        }

        public static double RefreshS(double aNew, double dMin, double vKn, double lifeS)
        {
            double byLife = 0.7 * lifeS;
            if (vKn <= 0.01) return byLife;
            return Math.Min((aNew - dMin) / vKn * 3600.0, byLife);
        }

        public static List<NmPt> DipPoints(double lead, double s, int count)
        {
            var l = new List<NmPt>();
            for (int i = 0; i < count; i++) l.Add(new NmPt(lead + i * s, 0));
            return l;
        }

        /// <summary>T-154 B3: the sink of the one-line "pattern refused" journal (set by the game layer; null in the stands).</summary>
        public static Action<string> GuardLog;
        private static bool _guardLogged;
        private const int PatternMaxSteps = 10000;

        private static bool PatternOk(string what, params double[] v)
        {
            foreach (double x in v) if (double.IsNaN(x) || double.IsInfinity(x)) { GuardNote(what, x); return false; }
            if (v.Length > 0 && !(v[v.Length - 1] > 1e-6)) { GuardNote(what, v[v.Length - 1]); return false; }   // the last value is the step
            return true;
        }

        private static void GuardNote(string what, double x)
        {
            if (_guardLogged) return;
            _guardLogged = true;
            var g = GuardLog;
            if (g != null) g("asw pattern " + what + " refused: bad step/size " + x.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>MAD zig-zag: legs across the axis (Y = +-width/2), advanced by <paramref name="leg"/> each time, starting at X = lead.</summary>
        public static List<NmPt> MadSnake(double lead, double width, double leg, double lengthNm)
        {
            var l = new List<NmPt>();
            int k = 0;
            if (!PatternOk("mad_snake", lead, width, lengthNm, leg)) return l;
            for (double x = lead; x <= lead + lengthNm + 1e-9 && k < PatternMaxSteps; x += leg, k++)
            {
                double a = (k % 2 == 0) ? -width / 2 : width / 2;
                l.Add(new NmPt(x, a));
                l.Add(new NmPt(x, -a));
            }
            return l;
        }

        public static List<NmPt> StandingRing(double radius, double d)
        {
            int n = Math.Max(3, (int)Math.Ceiling(2 * Math.PI * radius / d - 1e-9));
            var l = new List<NmPt>();
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                l.Add(new NmPt(radius * Math.Cos(a), radius * Math.Sin(a)));
            }
            return l;
        }

        // ---- area patterns -----------------------------------------------------------------------------------------

        /// <summary>Staggered grid centred on (0,0), centre first, then by distance. <paramref name="covered"/> = placed / total.</summary>
        public static List<NmPt> AreaGrid(double w, double h, double d, double p, int available, out double covered)
        {
            int rows = Math.Max(1, (int)Math.Ceiling(h / p - 1e-9));
            double mid = (rows - 1) / 2.0;
            var pts = new List<NmPt>();
            for (int j = 0; j < rows; j++)
            {
                double y = (j - mid) * p;
                bool shift = ((int)Math.Round(Math.Abs(j - mid))) % 2 == 1;
                double off = shift ? d / 2 : 0;
                int kmax = (int)Math.Ceiling(w / d) + 1;
                for (int k = -kmax; k <= kmax; k++)
                {
                    double x = k * d + off;
                    if (Math.Abs(x) <= w / 2 + 1e-9) pts.Add(new NmPt(x, y));
                }
            }
            pts.Sort((a, b) => (a.X * a.X + a.Y * a.Y).CompareTo(b.X * b.X + b.Y * b.Y));
            int total = pts.Count;
            if (available < 0) available = 0;
            if (pts.Count > available) pts.RemoveRange(available, pts.Count - available);
            covered = total == 0 ? 1.0 : (double)pts.Count / total;
            return pts;
        }

        public static List<NmPt> CreepingLine(double w, double h, double leg)
        {
            var l = new List<NmPt>();
            int k = 0;
            if (!PatternOk("creeping_line", w, h, leg)) return l;
            for (double y = -h / 2; y <= h / 2 + 1e-9 && k < PatternMaxSteps; y += leg, k++)
            {
                double a = (k % 2 == 0) ? -w / 2 : w / 2;
                l.Add(new NmPt(a, y));
                l.Add(new NmPt(-a, y));
            }
            return l;
        }

        /// <summary>Corners of an expanding square: start point, then <paramref name="legs"/> corners; leg lengths 1,1,2,2,3,3...</summary>
        public static List<NmPt> ExpandingSquare(NmPt c, double leg, int legs)
        {
            var l = new List<NmPt> { c };
            double x = c.X, y = c.Y;
            int[] dx = { 1, 0, -1, 0 }, dy = { 0, 1, 0, -1 };
            for (int i = 0; i < legs; i++)
            {
                double len = (i / 2 + 1) * leg;
                x += dx[i % 4] * len; y += dy[i % 4] * len;
                l.Add(new NmPt(x, y));
            }
            return l;
        }

        /// <summary>"Cross" of 4 buoys at 0.8R around the contact, never closer than 1 mile to it.</summary>
        public static List<NmPt> Localize(NmPt contact, double r, bool hasActive, out NmPt? activeAt)
        {
            double rad = Math.Max(0.8 * r, 1.0);
            var l = new List<NmPt>
            {
                new NmPt(contact.X + rad, contact.Y), new NmPt(contact.X, contact.Y + rad),
                new NmPt(contact.X - rad, contact.Y), new NmPt(contact.X, contact.Y - rad)
            };
            activeAt = hasActive ? new NmPt(contact.X - 1.0, contact.Y - 1.0) : (NmPt?)null;   // ~1.4 mi off the estimate
            return l;
        }

        // ---- axis and geodesy --------------------------------------------------------------------------------------

        public static double AngleDiff(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d > 180) d -= 360;
            if (d < -180) d += 360;
            return Math.Abs(d);
        }

        public static bool NeedReplan(double oldAxis, double newAxis, double oldV, double newV)
        {
            if (AngleDiff(oldAxis, newAxis) > 30) return true;
            if ((newV < 3) != (oldV < 3)) return true;
            double b = Math.Max(oldV, 0.5);
            return Math.Abs(newV - oldV) / b > 0.30;
        }

        /// <summary>T-142 K: a loadout of a deck for the ASW sortie list: its name is one of the configured ones (csv, substring, any case) or it carries buoys / torpedoes.</summary>
        public static bool IsAswLoadout(LoadoutOption lo, string csv)
        {
            if (lo == null || string.IsNullOrEmpty(lo.Name)) return false;
            foreach (string w in (csv ?? "").Split(','))
            {
                string x = w.Trim();
                if (x.Length > 0 && (lo.Name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || (lo.Display ?? "").IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            }
            if (lo.Name.IndexOf("asw", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (lo.Weapons != null)
                foreach (var kv in lo.Weapons)
                {
                    if (kv.Value <= 0 || kv.Key == null) continue;
                    string k = kv.Key.ToLowerInvariant();
                    if (k.Contains("ssq") || k.Contains("buoy") || k.Contains("torp") || k.Contains("mk46") || k.Contains("mk50") || k.Contains("mk54")) return true;
                }
            return false;
        }

        /// <summary>T-142 K: the CSV for ResolveLoadout: the chosen loadout of the type, or the configured list for "auto" / nothing chosen.</summary>
        public static string LoadoutCsvFor(IDictionary<string, string> chosen, string type, string defaultCsv)
        {
            string want;
            if (chosen == null || type == null || !chosen.TryGetValue(type, out want) || string.IsNullOrWhiteSpace(want)
                || string.Equals(want.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) return defaultCsv;
            return want.Trim();
        }

        public static double Norm360(double a)
        {
            a %= 360.0;
            return a < 0 ? a + 360.0 : a;
        }

        /// <summary>Minimum net displacement (nm) for the smoothed track to define the axis.</summary>
        public const double MinTrackNm = 0.2;
        /// <summary>T-264: the span of the target track (s): 180 samples at 1 s (AswGame history); an evasion leg stays in the chord this long.</summary>
        public const double TrackSpanS = 180;
        /// <summary>A waypoint bearing is believed only when it is this close to the heading (degrees).</summary>
        public const double WpHeadingTolDeg = 45;

        /// <summary>
        /// Axis of the target: the course made good by the track (net displacement &gt;= 0.2 nm, track points are {lat, lon}, oldest first);
        /// otherwise the bearing to the next waypoint when it agrees with the heading within 45 degrees; otherwise the heading.
        /// <paramref name="src"/>: track | wp | heading. Never 0 by default (north) as before.
        /// </summary>
        public static double AxisDeg(double? nextWpBrgDeg, double headingDeg, IList<double[]> track, out string src)
        {
            if (track != null && track.Count >= 2)
            {
                var a = track[0]; var b = track[track.Count - 1];
                double n = (b[0] - a[0]) * 60.0, e = (b[1] - a[1]) * 60.0 * LonK(a[0], b[0]);
                if (Math.Sqrt(n * n + e * e) >= MinTrackNm) { src = "track"; return Norm360(Math.Atan2(e, n) / Deg); }
            }
            if (nextWpBrgDeg.HasValue && AngleDiff(nextWpBrgDeg.Value, headingDeg) <= WpHeadingTolDeg) { src = "wp"; return Norm360(nextWpBrgDeg.Value); }
            src = "heading";
            return Norm360(headingDeg);
        }

        // Flat on stable (1 nautical mile = 1/60 degree on both axes, GeoPosition.GeoOffsetByDistanceOnHeading), x LonK on the beta globe (T-348).

        /// <summary>T-348: the world of the running game, process-wide. false = stable/flat or unknown (default); true = beta LCC globe.
        /// Set only by GameApi.WorldGlobeAt (main thread); read anywhere.</summary>
        public static volatile bool WorldGlobe;

        /// <summary>1.0 exactly when !WorldGlobe (so x*LonK keeps the flat result bit-identical), otherwise LonScale(latA, latB, true).
        /// nm east = dLon * 60 * LonK; dLon = nm / (60 * LonK).</summary>
        public static double LonK(double latA, double latB)
        {
            return WorldGlobe ? LonScale(latA, latB, true) : 1.0;
        }

        public static double LonK(double lat) { return LonK(lat, lat); }

        private sealed class WorldRestore : IDisposable
        {
            private readonly bool _prev;
            public WorldRestore(bool globe) { _prev = WorldGlobe; WorldGlobe = globe; }
            public void Dispose() { WorldGlobe = _prev; }
        }

        /// <summary>Test helper: sets WorldGlobe for a block and restores it (stands only).</summary>
        internal static IDisposable WorldScope(bool globe) { return new WorldRestore(globe); }

        /// <summary>Test helper: great-circle distance on the sphere 1 deg = 60 nm (reference for the 1.5 % checks).</summary>
        internal static double GreatCircleNm(GeoPt a, GeoPt b)
        {
            double p1 = a.Lat * Deg, p2 = b.Lat * Deg, dp = p2 - p1, dl = (b.Lon - a.Lon) * Deg;
            double h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(h))) / Deg * 60.0;
        }

        /// <summary>g relative to origin in miles: X = north, Y = east (game rule, no cos(lat)).</summary>
        public static NmPt FlatNm(GeoPt origin, GeoPt g)
        {
            return new NmPt((g.Lat - origin.Lat) * 60.0, (g.Lon - origin.Lon) * 60.0 * LonK(origin.Lat, g.Lat));
        }

        public static GeoPt Offset(GeoPt g, double bearingDeg, double distNm)
        {
            double b = bearingDeg * Deg;
            double lat = g.Lat + distNm * Math.Cos(b) / 60.0;
            return new GeoPt(lat, g.Lon + distNm * Math.Sin(b) / (60.0 * LonK(g.Lat, lat)));
        }

        public static GeoPt ToGeo(GeoPt origin, double axisDeg, NmPt p)
        {
            double a = axisDeg * Deg;
            double north = p.X * Math.Cos(a) - p.Y * Math.Sin(a);
            double east = p.X * Math.Sin(a) + p.Y * Math.Cos(a);
            double lat = origin.Lat + north / 60.0;
            return new GeoPt(lat, origin.Lon + east / (60.0 * LonK(origin.Lat, lat)));
        }

        public static NmPt ToLocal(GeoPt origin, double axisDeg, GeoPt g)
        {
            double a = axisDeg * Deg;
            double north = (g.Lat - origin.Lat) * 60.0;
            double east = (g.Lon - origin.Lon) * 60.0 * LonK(origin.Lat, g.Lat);
            return new NmPt(north * Math.Cos(a) + east * Math.Sin(a), -north * Math.Sin(a) + east * Math.Cos(a));
        }

        public static double DistNm(GeoPt a, GeoPt b)
        {
            double n = (b.Lat - a.Lat) * 60.0;
            double e = (b.Lon - a.Lon) * 60.0 * LonK(a.Lat, b.Lat);
            return Math.Sqrt(n * n + e * e);
        }

        public static double BearingDeg(GeoPt a, GeoPt b)
        {
            double n = (b.Lat - a.Lat) * 60.0;
            double e = (b.Lon - a.Lon) * 60.0 * LonK(a.Lat, b.Lat);
            return Norm360(Math.Atan2(e, n) / Deg);
        }

        // T-347: the metric of the game world. Stable 261021: flat in degrees, 1 deg = 60 nm on both axes (globe=false, the old rule, bit-identical).
        // Beta (LCC globe): 1 deg of longitude = 60*cos(lat) nm (globe=true, cos of the mean latitude, floored at MinCosLat).
        public const double MinCosLat = 0.05;

        public static double LonScale(double latA, double latB, bool globe)
        {
            double c = globe ? Math.Cos((latA + latB) * 0.5 * Deg) : 1.0;
            return globe && !(c >= MinCosLat) ? MinCosLat : c;
        }

        /// <summary>The point g in the frame of origin: X = north, Y = east, nm.</summary>
        public static NmPt WorldNm(GeoPt origin, GeoPt g, bool globe)
        {
            return new NmPt((g.Lat - origin.Lat) * 60.0, (g.Lon - origin.Lon) * 60.0 * LonScale(origin.Lat, g.Lat, globe));
        }

        public static double DistNm(GeoPt a, GeoPt b, bool globe)
        {
            double n = (b.Lat - a.Lat) * 60.0;
            double e = (b.Lon - a.Lon) * 60.0 * LonScale(a.Lat, b.Lat, globe);
            return Math.Sqrt(n * n + e * e);
        }

        /// <summary>Radial speed of a track (velocity vNorthKn/vEastKn) towards a point, kn (&gt; 0 = approaching). d &lt; 1e-6 gives coincideKn.</summary>
        public static double RadialClosingKn(GeoPt track, GeoPt point, double vNorthKn, double vEastKn, bool globe, double coincideKn)
        {
            double pn = (track.Lat - point.Lat) * 60.0, pe = (track.Lon - point.Lon) * 60.0 * LonScale(track.Lat, point.Lat, globe);
            double d = Math.Sqrt(pn * pn + pe * pe);
            if (d < 1e-6) return coincideKn;
            return -((pn * vNorthKn + pe * vEastKn) / d);
        }

        /// <summary>Dead reckoning by north/east miles (the exact inverse of DistNm: the longitude step uses the mean latitude of the leg).</summary>
        public static GeoPt OffsetNm(GeoPt g, double northNm, double eastNm, bool globe)
        {
            double lat = g.Lat + northNm / 60.0;
            return new GeoPt(lat, g.Lon + eastNm / (60.0 * LonScale(g.Lat, lat, globe)));
        }

        /// <summary>Which world a metric sample (nm per degree) belongs to: r = lon/lat, c = cos(lat); globe = |r-c| &lt; |r-1|. decided=false near the equator (the worlds coincide) or for a bad sample.</summary>
        public static bool GlobeFromMetric(double lat, double lonNmPerDeg, double latNmPerDeg, out bool decided)
        {
            decided = false;
            if (double.IsNaN(lat) || double.IsNaN(lonNmPerDeg) || double.IsNaN(latNmPerDeg) || !(lonNmPerDeg > 0) || !(latNmPerDeg > 0)) return false;
            double c = Math.Cos(lat * Deg);
            if (1.0 - c < 0.01) return false;
            double r = lonNmPerDeg / latNmPerDeg;
            decided = true;
            return Math.Abs(r - c) < Math.Abs(r - 1.0);
        }

        // ---- route order ---------------------------------------------------------------------------------------------

        /// <summary>Length (nm, flat) of the path start -> p[0] -> p[1] ...</summary>
        public static double RouteLengthNm(GeoPt start, IList<GeoPt> p)
        {
            double len = 0; var cur = start;
            foreach (var q in p) { len += DistNm(cur, q); cur = q; }
            return len;
        }

        /// <summary>
        /// Order of the points that minimises the transit: the given order, the reversed one (start from the nearest end) and the
        /// nearest-neighbour chain from the aircraft are compared; the shortest wins (ties keep the given order). Pure, no state.
        /// </summary>
        public static List<GeoPt> BestOrder(GeoPt start, IList<GeoPt> pts)
        {
            var given = new List<GeoPt>(pts);
            if (given.Count < 3) { var r1 = new List<GeoPt>(given); if (r1.Count == 2 && DistNm(start, r1[1]) < DistNm(start, r1[0])) r1.Reverse(); return r1; }
            var rev = new List<GeoPt>(given); rev.Reverse();
            var left = new List<GeoPt>(given); var nn = new List<GeoPt>(); var cur = start;
            while (left.Count > 0)
            {
                int bi = 0; double bd = double.MaxValue;
                for (int i = 0; i < left.Count; i++) { double d = DistNm(cur, left[i]); if (d < bd - 1e-9) { bd = d; bi = i; } }
                cur = left[bi]; nn.Add(cur); left.RemoveAt(bi);
            }
            var best = given; double bl = RouteLengthNm(start, given);
            double lr = RouteLengthNm(start, rev); if (lr < bl - 1e-9) { best = rev; bl = lr; }
            double ln = RouteLengthNm(start, nn); if (ln < bl - 1e-9) { best = nn; }
            return best;
        }

        // ---- assignment and relief ---------------------------------------------------------------------------------

        private static bool Excluded(AswMember m) { return WhereReason(m) != null; }
        private static int Buoys(AswMember m) { return m.Passive + m.ActiveBuoys + m.Vlad; }

        /// <summary>Why the aircraft is not the controller's to use because of its state (not its load): null = free.</summary>
        private static string WhereReason(AswMember m)
        {
            switch (m.Where)
            {
                case "player": return "player";
                case "sar": return "ext_sar";          // the workshop mod AutomaticSAR holds the unit (T-109: renamed from "sar")
                case "game_patrol": return "game_patrol";
                case "landed": return "landed";
                case "lost": return "lost";
                default: return m.GameSarZone ? "game_sar_zone" : null;
            }
        }

        /// <summary>T-109: may a unit of this class with these loadout capacities serve in a SAR group? Class Helicopter and some loadout with seats.</summary>
        public static bool SarCapable(string vehicleClass, IEnumerable<int> capacities)
        {
            if (!string.Equals(vehicleClass, "Helicopter", StringComparison.OrdinalIgnoreCase) || capacities == null) return false;
            foreach (int c in capacities) if (c > 0) return true;
            return false;
        }

        /// <summary>
        /// T-109: null = the unit may serve in a SAR group. Otherwise ext_sar | player | game_patrol | landed | lost | game_sar_zone | no_capacity
        /// (in_group:&lt;name&gt; is added by the controller, which knows the groups).
        /// </summary>
        public static string SarExclusionReason(AswMember m)
        {
            string w = WhereReason(m);
            if (w != null) return w;
            return SarCapable(m.Helicopter ? "Helicopter" : "", new[] { m.BestCapacity > 0 ? m.BestCapacity : m.Capacity }) ? null : "no_capacity";
        }

        private static bool HasRole(string[] roles, string role)
        {
            if (roles == null) return false;
            foreach (var r in roles) if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Airborne early warning aircraft (E-2, E-3, ...): by roles, else by the type name.</summary>
        public static bool IsAew(string type, string[] roles)
        {
            if (HasRole(roles, "AEW") && !HasRole(roles, "ASW") && !HasRole(roles, "MPA")) return true;
            if (roles != null && roles.Length > 0) return false;
            if (string.IsNullOrEmpty(type)) return false;
            string t = type.ToUpperInvariant();
            return t.StartsWith("E-2", StringComparison.Ordinal) || t.StartsWith("E-3", StringComparison.Ordinal) || t.Contains("AEW")
                || t.Contains("HAWKEYE") || t.Contains("SENTRY") || t.StartsWith("EC-121", StringComparison.Ordinal);
        }

        /// <summary>An ASW load: something to drop, to dip, to detect with or to shoot. AEW never counts.</summary>
        /// <summary>T-343: an ASW-capable TYPE = one of its loadouts is ASW (IsAswLoadout, csv = [Asw] Loadouts) or its dipping sonar is loaded; roles alone never.</summary>
        public static bool TypeAswCapable(IEnumerable<LoadoutOption> typeLoadouts, string aswCsv, bool? typeDipping)
        {
            if (typeDipping == true) return true;
            if (typeLoadouts != null) foreach (var lo in typeLoadouts) if (IsAswLoadout(lo, aswCsv)) return true;
            return false;
        }

        public static bool CapableLoad(string[] roles, int buoys, bool dipping, bool mad, int torpedoes)
        {
            if (HasRole(roles, "AEW") && !HasRole(roles, "ASW") && !HasRole(roles, "MPA")) return false;
            return buoys > 0 || dipping || mad || torpedoes > 0;
        }

        /// <summary>
        /// null = the aircraft may serve in an ASW group. Otherwise the reason it is listed under "excluded":
        /// no_asw_load | aew | player | ext_sar | game_patrol | game_sar_zone | landed | lost.
        /// </summary>
        public static string ExclusionReason(AswMember m)
        {
            string w = WhereReason(m);
            if (w != null) return w;
            if (CapableLoad(m.Roles, Buoys(m), m.Dipping, m.Mad, m.Torpedoes)) return IsAew(m.Type, m.Roles) ? "aew" : null;
            return IsAew(m.Type, m.Roles) ? "aew" : "no_asw_load";
        }

        /// <summary>The aircraft flies or stands on a deck, ready to be given tasks (not launching / recovering).</summary>
        public static bool Usable(AswMember m)
        {
            return m.Where == "air" || m.Where == "deck_ready" || m.Where == "deck_prep" || m.Where == "deck_service";
        }

        // ---- deck aircraft from Flight Operations ------------------------------------------------------------------

        /// <summary>One line of the game's Flight Operations window (FlightDeck.FlightDeckTasks) or one hangar vehicle type.</summary>
        internal sealed class DeckEntry
        {
            public string Key;                  // stable: task Guid / "hangar:<type>"
            public int ShipUid, Uid;            // Uid: synthetic (negative) for deck rows, the real unit uid for a launching aircraft
            public string Type, Squadron, Label, Loadout, ShipName;
            public string State;                // hangar | prep | ready | launching | recovering | cooling
            public string Serial;               // T-150: machine serial (unit: and cool: rows)
            public string FromKey;              // launching rows: the pending task key the LaunchTask was spawned from
            public double ReadyInS;
            public int Count = 1;
            public int Slot;                    // T-181: k of a hangar slot row, 0 = not a slot
            public string DeckPhase;            // T-181: game state of a task row (null = unknown)
            public string[] Roles;
            public Dictionary<string, int> Ammo;   // loadout ammunition (name -> count), may be null
            public bool Helicopter = true, AswCapableType;
            public bool? LoadoutAsw;            // T-344: the row's loadout is an ASW one (IsAswLoadout, csv = [Asw] Loadouts); null = no loadout known
            public bool? TypeDipping;           // T-188: the type's loaded sensors have a dipping sonar (null = unreadable -> the old guess by the load)
            public string DipNote;
            public string UnitName;             // T-192: callsign of a unit: row (launching / recovering); Label stays the type label
            public double HangarPrepS = -1;     // T-192: hangar row: preparation time with the deck timing mode, -1 = not a hangar row
            public double NextPrepS = -1;       // T-213: cool: row: preparation of the next order of this machine from the hangar, -1 = unknown
        }

        /// <summary>Sonobuoys and torpedoes by ammunition name (the deck has no unit to ask): ssq-62 active, ssq-77 vlad, other buoys passive, mk46/torpedo.</summary>
        public static void AmmoLoad(IDictionary<string, int> ammo, out int passive, out int active, out int vlad, out int torpedoes)
        {
            passive = active = vlad = torpedoes = 0;
            if (ammo == null) return;
            foreach (var kv in ammo)
            {
                string n = (kv.Key ?? "").ToLowerInvariant();
                if (n.Contains("ssq-62") || n.Contains("ssq62")) active += kv.Value;
                else if (n.Contains("ssq-77") || n.Contains("ssq77")) vlad += kv.Value;
                else if (n.Contains("ssq") || n.Contains("sonobuoy") || n.Contains("buoy") || n.Contains("rgb-")) passive += kv.Value;
                else if (n.Contains("mk46") || n.Contains("mk-46") || n.Contains("mk 46") || n.Contains("mk50") || n.Contains("mk-50") || n.Contains("torp") || n.Contains("a244") || n.Contains("apr-")) torpedoes += kv.Value;
            }
        }

        /// <summary>
        /// The game's EnduranceInSec is the time LEFT (Aircraft: range / speed, Helicopter: range / consumption); AswMember.EnduranceS is the endurance of
        /// a full tank (the planner multiplies by FuelFrac). Unknown (null / not positive) -> 0.
        /// </summary>
        internal static double FullEnduranceS(double? remainingS, double fuelFrac)
        {
            if (!remainingS.HasValue || double.IsNaN(remainingS.Value) || remainingS.Value <= 0) return 0;
            return fuelFrac > 0.02 ? remainingS.Value / Math.Min(1, fuelFrac) : remainingS.Value;
        }

        /// <summary>T-192: the type label of an aircraft in the air: the deck label of its type, else ClassName unless it is just the callsign (null = show Type).</summary>
        internal static string TypeLabelOf(string deckLabel, string className, string unitName)
        {
            if (!string.IsNullOrEmpty(deckLabel)) return deckLabel;
            return !string.IsNullOrEmpty(className) && className != unitName ? className : null;
        }

        /// <summary>T-192: a hangar row ("hangar:&lt;type&gt;#k" or legacy "hangar:&lt;type&gt;").</summary>
        internal static bool IsHangarRow(string deckKey) { return deckKey != null && deckKey.StartsWith("hangar:", StringComparison.Ordinal); }

        /// <summary>T-344: null when the loadout is unknown (null / ""); else whether it is an ASW one (IsAswLoadout, csv = [Asw] Loadouts).</summary>
        public static bool? LoadoutAswOf(string loadout, Dictionary<string, int> ammo, string csv)
        {
            if (string.IsNullOrEmpty(loadout)) return null;
            return IsAswLoadout(new LoadoutOption { Name = loadout, Display = loadout, Weapons = ammo }, csv);
        }

        /// <summary>Deck row -> member. The load comes from the loadout; nominal helicopter gear only when the loadout is ASW (or unknown) but names nothing we know.</summary>
        internal static AswMember DeckToMember(DeckEntry d)
        {
            int pa, ac, vl, tp;
            AmmoLoad(d.Ammo, out pa, out ac, out vl, out tp);
            bool? loAsw = d.LoadoutAsw ?? LoadoutAswOf(d.Loadout, d.Ammo, null);   // T-344: a known non-ASW loadout (Strike) gets no nominal ASW gear
            bool nominal = pa + ac + vl + tp == 0 && d.AswCapableType && loAsw != false;
            string where = d.State == "ready" ? "deck_ready" : d.State == "prep" ? "deck_prep" : d.State == "launching" ? "deck_launching" : d.State == "recovering" ? "deck_recover" : "deck_service";   // "cooling" and "hangar" are both service states
            bool heli = d.Helicopter;
            return new AswMember
            {
                Helicopter = heli,
                Uid = d.Uid, DeckUid = d.ShipUid, ShipUid = d.ShipUid, Name = !string.IsNullOrEmpty(d.UnitName) ? d.UnitName : string.IsNullOrEmpty(d.Label) ? d.Type : d.Label,   // T-192: callsign of a unit: row; T-155: the ship has its own column; T-181: no "x N", every machine is its own row
                Type = d.Type, TypeLabel = d.Label, HangarPrepS = d.HangarPrepS, NextPrepS = d.NextPrepS, Where = where, ReadyInS = IsHangarRow(d.Key) && d.HangarPrepS >= 0 ? d.HangarPrepS : d.State == "ready" ? 0 : Math.Max(0, d.ReadyInS),   // T-226 (Q-T192-2): a hangar row is ready in HangarPrepS, not at once
                FuelFrac = 1, EnduranceS = 9000, TransitS = 600, LaunchS = 120,
                Passive = nominal ? 12 : pa, ActiveBuoys = nominal ? 8 : ac, Vlad = nominal ? 4 : vl, Torpedoes = nominal ? 2 : tp,
                Dipping = d.TypeDipping.HasValue ? heli && d.TypeDipping.Value : heli && (nominal || pa + ac + vl + tp > 0) && d.AswCapableType, Mad = heli && nominal,
                DipSrc = d.TypeDipping.HasValue ? "type" : "guess", DipNote = d.DipNote,
                LoadKnown = nominal || (d.Ammo != null && d.Ammo.Count > 0) || loAsw == false,
                Base = d.ShipName, Loadout = d.Loadout, Roles = d.Roles, DeckKey = d.Key, Serial = d.Serial, FromKey = d.FromKey, DeckCount = d.Count, Slot = d.Slot, DeckPhase = d.DeckPhase
            };
        }

        /// <summary>
        /// Which live aircraft a launched deck row turned into (T-142 P0). 0) the "unit:" row the game spawned from this very task (FromKey = the task key:
        /// the LaunchTask shares the LaunchTaskParameters of its PendingLaunchTask), whatever the known set says; 1) a "unit:" deck row of the same deck that is
        /// launching and unclaimed; 2) otherwise the air unit of the same type closest to the deck (the first one when the deck position is unknown). 0 = none yet.
        /// </summary>
        internal static int PickAdoptee(AswMember launched, IList<AswMember> live, ISet<int> known, Func<int, bool> taken, double deckLat, double deckLon, string fromKey = null)
        {
            if (fromKey != null)
                foreach (var c in live)
                    if (c.FromKey == fromKey && !taken(c.Uid)) return c.Uid;
            foreach (var c in live)
            {
                // T-150 P5: a foreign launch of another type from the same deck (E-2C, F-14) is not ours
                if (c.DeckUid == launched.DeckUid && c.Where == "deck_launching" && c.DeckKey != null && c.DeckKey.StartsWith("unit:", StringComparison.Ordinal)
                    && (launched.Type == null || c.Type == launched.Type)
                    && !known.Contains(c.Uid) && !taken(c.Uid)) return c.Uid;
            }
            bool pos = !double.IsNaN(deckLat) && !double.IsNaN(deckLon);
            int best = 0; double bd = double.MaxValue;
            foreach (var c in live)
            {
                if (c.Where != "air" || c.Type != launched.Type || known.Contains(c.Uid) || taken(c.Uid)) continue;
                if (!pos) return c.Uid;
                double d = (c.Lat - deckLat) * (c.Lat - deckLat) + (c.Lon - deckLon) * (c.Lon - deckLon);
                if (d < bd) { bd = d; best = c.Uid; }
            }
            return best;
        }

        private static void AdoptSelfTest()
        {
            var launched = new AswMember { Uid = -5, DeckUid = 9, Type = "usn_sh-3h", Where = "deck_ready" };
            var uni = new AswMember { Uid = 77, DeckUid = 9, Type = "usn_sh-3h", Where = "deck_launching", DeckKey = "unit:77" };
            var near = new AswMember { Uid = 10, Type = "usn_sh-3h", Where = "air", Lat = 1, Lon = 1 };
            var far = new AswMember { Uid = 11, Type = "usn_sh-3h", Where = "air", Lat = 5, Lon = 5 };
            var other = new AswMember { Uid = 12, Type = "usn_sh-60", Where = "air", Lat = 1, Lon = 1 };
            var none = new HashSet<int>();
            True("adopt unit first", PickAdoptee(launched, new[] { far, near, uni }, none, u => false, 0, 0) == 77);
            True("adopt unit known", PickAdoptee(launched, new[] { uni }, new HashSet<int> { 77 }, u => false, 0, 0) == 0);
            True("adopt unit taken", PickAdoptee(launched, new[] { uni }, none, u => u == 77, 0, 0) == 0);
            True("adopt unit other deck", PickAdoptee(new AswMember { DeckUid = 8, Type = "x" }, new[] { uni }, none, u => false, 0, 0) == 0);
            True("adopt nearest", PickAdoptee(launched, new[] { far, near, other }, none, u => false, 0, 0) == 10);
            True("adopt nearest 2", PickAdoptee(launched, new[] { near, far }, none, u => false, 5, 5) == 11);
            True("adopt skip known", PickAdoptee(launched, new[] { near, far }, new HashSet<int> { 10 }, u => false, 0, 0) == 11);
            True("adopt skip taken", PickAdoptee(launched, new[] { near, far }, none, u => u == 10 || u == 11, 0, 0) == 0);
            True("adopt no pos", PickAdoptee(launched, new[] { far, near }, none, u => false, double.NaN, double.NaN) == 11);
            True("adopt type", PickAdoptee(launched, new[] { other }, none, u => false, 0, 0) == 0);
            var foreignUnit = new AswMember { Uid = 78, DeckUid = 9, Type = "usn_f-14", Where = "deck_launching", DeckKey = "unit:78" };
            True("adopt unit other type", PickAdoptee(launched, new[] { foreignUnit }, none, u => false, 0, 0) == 0);
            True("adopt unit other type, own after", PickAdoptee(launched, new[] { foreignUnit, uni }, none, u => false, 0, 0) == 77);
            var linked = new AswMember { Uid = 79, DeckUid = 9, Type = "usn_sh-3h", Where = "deck_launching", DeckKey = "unit:79", FromKey = "task:k1" };
            True("full endurance", Math.Abs(FullEnduranceS(3600, 0.5) - 7200) < 1e-6 && FullEnduranceS(null, 0.5) == 0 && FullEnduranceS(0, 0.5) == 0 && FullEnduranceS(600, 0) == 600);
            True("adopt linked first", PickAdoptee(launched, new[] { uni, linked }, none, u => false, 0, 0, "task:k1") == 79);
            True("adopt linked though known", PickAdoptee(launched, new[] { linked }, new HashSet<int> { 79 }, u => false, 0, 0, "task:k1") == 79);
            True("adopt linked taken", PickAdoptee(launched, new[] { linked }, new HashSet<int> { 79 }, u => u == 79, 0, 0, "task:k1") == 0);
            True("adopt other link", PickAdoptee(launched, new[] { linked }, new HashSet<int> { 79 }, u => false, 0, 0, "task:k2") == 0);
            int h1 = HangarUid(7, "usn_sh-3h"), h2 = HangarUid(7, "usn_sh-3h"), h3 = HangarUid(7, "usn_sh-2f"), h4 = HangarUid(8, "usn_sh-3h");
            True("hangar uid stable", h1 == h2 && h1 < 0 && h1 != h3 && h1 != h4);
            True("hangar uid not a task uid", (-h1 & 0x40000000) == 0 && (-h3 & 0x40000000) == 0);
            var cool = DeckToMember(new DeckEntry { Key = "cool:abc", Uid = -(0x40000000 | 5), ShipUid = 7, Type = "usn_sh-3h", State = "cooling", ReadyInS = 300, Serial = "A12", ShipName = "Ike" });
            True("deck cooling", cool.Where == "deck_service" && cool.ReadyInS == 300 && cool.Serial == "A12" && cool.DeckKey == "cool:abc" && LaunchRoute(cool) == null);
        }

        /// <summary>
        /// T-150 P7: the uid of a hangar row, stable for the ship and the type (FNV-1a), in the range -(0x20000000 .. 0x3FFFFFFF);
        /// task rows live above 0x40000000, so the two never meet.
        /// </summary>
        internal static int HangarUid(int shipUid, string type)
        {
            uint h = 2166136261;
            foreach (char ch in shipUid.ToString(CultureInfo.InvariantCulture) + "|" + (type ?? ""))
            {
                h ^= ch;
                h *= 16777619;
            }
            return -(0x20000000 | (int)(h & 0x1FFFFFFF));
        }

        // ---- T-181 (ADR-0080): hangar slots, task uids, rebinding ---------------------------------------------------

        // T-186 (ADR-0082): the slot math moved to AirOpsMath (shared by the ASW and CAP windows); these are thin forwards.
        internal const int MaxSlots = AirOpsMath.MaxSlots;

        internal static int HangarSlotUid(int shipUid, string type, int k) { return AirOpsMath.HangarSlotUid(shipUid, type, k); }

        internal static int TaskUid(string key) { return AirOpsMath.TaskUid(key); }

        internal static string SlotKey(string type, int k) { return AirOpsMath.SlotKey(type, k); }

        internal static bool ParseSlotKey(string key, out string type, out int k) { return AirOpsMath.ParseSlotKey(key, out type, out k); }

        internal static List<AirOpsMath.SlotMove> RebindSlots(int numbers, IList<AirOpsMath.SlotClaim> claims) { return AirOpsMath.RebindSlots(numbers, claims); }

        /// <summary>ADR-0080 §5 / Q1 (customer 2026-10-22): false = every machine flies, a change only after a landing; true = [Asw] Standby machines stay on deck.</summary>
        internal const bool ReserveOnDeck = false;

        internal static int HolderCap(int tactics, int usable, int standby) { return HolderCap(tactics, usable, standby, ReserveOnDeck); }

        internal static int HolderCap(int tactics, int usable, int standby, bool reserveOnDeck)
        {
            return reserveOnDeck ? Math.Max(tactics, usable - Math.Max(0, standby)) : usable;
        }

        private static void SlotSelfTest()
        {
            int a = HangarSlotUid(7, "usn_sh-3h", 1), b = HangarSlotUid(7, "usn_sh-3h", 1);
            True("slot uid stable", a == b && a < 0);
            True("slot uid differs", a != HangarSlotUid(7, "usn_sh-3h", 2) && a != HangarSlotUid(7, "usn_sh-2f", 1) && a != HangarSlotUid(8, "usn_sh-3h", 1));
            True("slot uid not a task", (-a & 0x40000000) == 0 && a != HangarUid(7, "usn_sh-3h"));
            True("task uid", TaskUid("task:abc") == TaskUid("task:abc") && (-TaskUid("task:abc") & 0x40000000) != 0 && TaskUid("task:a") != TaskUid("cool:a"));
            True("slot uid literals", HangarSlotUid(7, "usn_sh-3h", 1) == -577077302 && HangarSlotUid(7, "usn_sh-3h", 2) == -560299683 && HangarSlotUid(100, "F-14A", 1) == -836476755 && HangarSlotUid(100, "F-14A", 3) == -870031993);
            True("task uid literals", TaskUid("task:abc") == -1994244102 && TaskUid("cool:abc") == -1142562998 && TaskUid("k1") == -1480425665);
            string ty; int kk;
            True("slot key", SlotKey("usn_sh-3h", 3) == "hangar:usn_sh-3h#3" && ParseSlotKey("hangar:usn_sh-3h#3", out ty, out kk) && ty == "usn_sh-3h" && kk == 3);
            True("slot key legacy", !ParseSlotKey("hangar:usn_sh-3h", out ty, out kk) && !ParseSlotKey("task:abc", out ty, out kk));
            var dm = DeckToMember(new DeckEntry { Key = "hangar:usn_sh-3h#2", Uid = a, ShipUid = 7, Type = "usn_sh-3h", Label = "SH-3H", State = "hangar", Count = 5, Slot = 2 });
            True("slot member", dm.Name == "SH-3H" && dm.Slot == 2 && dm.DeckCount == 5);
            True("holder cap", HolderCap(1, 4, 1, true) == 3 && HolderCap(2, 2, 1, true) == 2 && HolderCap(3, 2, 1, true) == 3 && HolderCap(1, 1, 1, true) == 1 && HolderCap(1, 4, 0, true) == 4);
            True("holder cap all fly", HolderCap(1, 4, 1, false) == 4 && HolderCap(1, 4, 1) == (ReserveOnDeck ? 3 : 4));
        }

        /// <summary>How a deck aircraft is launched: "allow" = permit the already prepared task, "create" = start a new task from the hangar, null = not a deck launch.</summary>
        public static string LaunchRoute(AswMember m)
        {
            if (m == null || m.DeckKey == null) return null;
            if ((m.Where == "deck_ready" || m.Where == "deck_prep") && m.DeckKey.StartsWith("task:", StringComparison.Ordinal)) return "allow";
            if (m.DeckKey.StartsWith("hangar:", StringComparison.Ordinal)) return "create";
            return null;
        }

        /// <summary>uid -> tactic (dip|corridor|barrier|grid|mad; old rows|cross) or "reserve". player/sar/game_patrol aircraft are left out.</summary>
        public static Dictionary<int, string> Assign(IList<AswMember> m, ICollection<string> tactics)
        {
            var res = new Dictionary<int, string>();
            foreach (var a in m) if (!Excluded(a)) res[a.Uid] = "reserve";
            foreach (string t in new[] { "dip", "corridor", "grid", "rows", "barrier", "cross", "mad" })   // T-176: corridor | barrier by course, grid in an area (rows | cross = old ids)
            {
                if (tactics == null || !Contains(tactics, t)) continue;
                AswMember best = null; int bestRank = 99;
                bool barrier = t != "dip" && t != "mad";
                foreach (var a in m)
                {
                    if (Excluded(a) || res[a.Uid] != "reserve") continue;
                    bool ok = t == "dip" ? a.Dipping : t == "mad" ? a.Mad : SearchBuoys(a) > 0;   // T-172 R10: active buoys do not search a barrier
                    if (!ok) continue;
                    // T-172 B9: airborne (and not heading home) first, then a ready deck aircraft, then the rest (as before: more search buoys for a barrier, else the first)
                    int rank = a.Where == "air" && !IsLeaving(a) ? 0 : a.Where == "deck_ready" && ReadyS(a) <= 0 ? 1 : 2;   // T-196: a hangar row is not ready at once
                    if (best == null || rank < bestRank) { best = a; bestRank = rank; continue; }
                    if (rank > bestRank) continue;
                    if (barrier && SearchBuoys(a) > SearchBuoys(best)) best = a;
                }
                if (best != null) res[best.Uid] = t;
            }
            return res;
        }

        private static bool Contains(ICollection<string> c, string s)
        {
            foreach (var x in c) if (x == s) return true;
            return false;
        }

        /// <summary>T-196: seconds until the machine can take off: a hangar row -> HangarPrepS (an order prepares, whatever CanLaunch says);
        /// deck_ready -> 0; other deck rows -> max(0, ReadyInS); air -> 0.</summary>
        internal static double ReadyS(AswMember m)
        {
            if (m == null || m.Where == "air") return 0;
            if (IsHangarRow(m.DeckKey) && m.HangarPrepS >= 0) return m.HangarPrepS;
            if (m.Where == "deck_ready") return 0;
            if (m.DeckKey != null && m.DeckKey.StartsWith("cool:", StringComparison.Ordinal)) return Math.Max(0, m.ReadyInS) + Math.Max(0, m.NextPrepS);   // T-213: cool-down, then the next order prepares in the hangar
            return Math.Max(0, m.ReadyInS);
        }

        /// <summary>T-226 (Q-T192-2): among the deck_ready / deck_prep rows accepted by <paramref name="ok"/> the one that can take off soonest (smallest ReadyS);
        /// on a tie a task, not a hangar row; then the smaller Uid. null = none.</summary>
        internal static AswMember SoonestDeckLaunch(IEnumerable<AswMember> rows, Func<AswMember, bool> ok)
        {
            AswMember best = null; double bt = 0; bool bh = false;
            if (rows == null) return null;
            foreach (var m in rows)
            {
                if (m == null || (m.Where != "deck_ready" && m.Where != "deck_prep") || (ok != null && !ok(m))) continue;
                double t = ReadyS(m); bool h = IsHangarRow(m.DeckKey);
                if (best == null || t < bt - 1e-9 || (Math.Abs(t - bt) <= 1e-9 && ((bh && !h) || (bh == h && m.Uid < best.Uid)))) { best = m; bt = t; bh = h; }
            }
            return best;
        }

        /// <summary>T-213: seconds the next hangar order of the machine prepares: a hangar row -> HangarPrepS; otherwise NextPrepS; unknown -> 0.</summary>
        internal static double NextPrepS(AswMember m)
        {
            if (m == null) return 0;
            if (IsHangarRow(m.DeckKey) && m.HangarPrepS >= 0) return m.HangarPrepS;
            return Math.Max(0, m.NextPrepS);
        }

        /// <summary>T-213: ground time of one machine between two sorties: p.TurnaroundS (landing + cool-down, no preparation) + the longest NextPrepS among the rotation.</summary>
        internal static double CycleTurnS(IEnumerable<AswMember> rotation, AswParams p)
        {
            double prep = 0;
            if (rotation != null) foreach (var a in rotation) prep = Math.Max(prep, NextPrepS(a));
            return p.TurnaroundS + prep;
        }

        internal static bool IsLeaving(AswMember m)
        {
            return m.AiState == "BingoFuel" || m.AiState == "Winchester" || m.AiState == "ReturnToBase";
        }

        /// <summary>Seconds the aircraft can still work on the task: min(fuel*0.9, buoys/rate, weapons).</summary>
        public static double RemainS(AswMember m, AswTaskSpec t)
        {
            if (IsLeaving(m)) return 0;
            double fuel = Math.Max(0, (m.FuelFrac * m.EnduranceS - m.TransitS) * 0.9);
            double r = fuel;
            if (t != null && t.BuoysPerHour > 0 && t.NeedBuoys > 0) r = Math.Min(r, SearchBuoys(m) / t.BuoysPerHour * 3600.0);   // T-172 R10
            // T-172: a holder of a live contact without torpedoes is not taken off (it keeps the contact for the shooter); Holding is read by T-173
            return r;
        }

        private static bool Candidate(AswMember m, string taskId)
        {
            return string.IsNullOrEmpty(m.Task) || m.Task == "reserve" || m.Task == taskId;
        }

        public static List<ReliefAction> PlanRelief(IList<AswMember> m, IList<AswTaskSpec> t, double now, AswParams p)
        {
            var res = new List<ReliefAction>();
            string first = (p.Loadouts ?? "").Split(',')[0].Trim();
            foreach (var task in t)
            {
                var air = new List<AswMember>();
                var deck = new List<AswMember>();
                int inRotation = 0;
                foreach (var a in m)
                {
                    if (Excluded(a) || !Candidate(a, task.Id)) continue;
                    if (a.Where == "air") { if (a.Task == task.Id) { air.Add(a); inRotation++; } }
                    else if (a.Where == "deck_ready" || a.Where == "deck_prep" || a.Where == "deck_service") { deck.Add(a); inRotation++; }
                }
                // sort deck: ready first, then by ReadyInS
                // T-196: a ready pending task goes before a hangar row (which still has to prepare)
                deck.Sort((x, y) => (x.Where == "deck_ready" ? 0 : 1).CompareTo(y.Where == "deck_ready" ? 0 : 1) != 0
                    ? (x.Where == "deck_ready" ? 0 : 1).CompareTo(y.Where == "deck_ready" ? 0 : 1)
                    : ReadyS(x).CompareTo(ReadyS(y)));
                AswMember ready = null;
                foreach (var a in deck) if (a.Where == "deck_ready") { ready = a; break; }

                // best remaining time among aircraft currently serving the task
                double bestRemain = 0;
                AswMember leaving = null;
                foreach (var a in air)
                {
                    double r = RemainS(a, task);
                    if (r > bestRemain) bestRemain = r;
                }
                foreach (var a in air) if (RemainS(a, task) <= 0) { leaving = a; res.Add(new ReliefAction { Kind = "release", Uid = a.Uid, TaskId = task.Id }); }

                bool launched = false;
                var launchedUids = new HashSet<int>();
                if (task.Want >= 2)
                {
                    // T-181 §5: lanes. covered = airborne on the task + holders already allowed + launching on the task
                    int allowedN = 0, launching = 0;
                    foreach (var a in deck) if (a.Allowed) allowedN++;   // a reserve that is already allowed is a replacement under way
                    foreach (var a in m) if (a.Where == "deck_launching" && a.Task == task.Id) launching++;
                    int covered = air.Count + allowedN + launching;
                    int shortN = task.Want - covered;
                    foreach (var a in deck)
                    {
                        if (shortN <= 0) break;
                        if (a.Task != task.Id || a.Allowed) continue;
                        res.Add(new ReliefAction { Kind = "launch", Uid = a.Uid, TaskId = task.Id });
                        launchedUids.Add(a.Uid); launched = true; shortN--;
                    }
                    // T-196: the j-th low aircraft (the least remaining first, after the surplus) is replaced by the j-th ready deck row; a hangar row adds its preparation to the lead
                    var byRemain = new List<AswMember>(air);
                    byRemain.Sort((x, y) => RemainS(x, task).CompareTo(RemainS(y, task)));
                    int idx = Math.Max(0, covered - task.Want);
                    foreach (var a in deck)
                    {
                        if (idx >= byRemain.Count) break;
                        if (a.Where != "deck_ready" || a.Allowed || launchedUids.Contains(a.Uid)) continue;
                        if (RemainS(byRemain[idx], task) >= ReadyS(a) + a.TransitS + a.LaunchS + 60) break;
                        res.Add(new ReliefAction { Kind = "launch", Uid = a.Uid, TaskId = task.Id });
                        launchedUids.Add(a.Uid); launched = true; idx++;
                    }
                }
                else if (ready != null)
                {
                    double lead = ReadyS(ready) + ready.TransitS + ready.LaunchS;   // T-196
                    if (air.Count == 0 || bestRemain < lead + 60)
                    {
                        // a fresh aircraft that is not yet counted: nothing airborne works the task long enough
                        res.Add(new ReliefAction { Kind = "launch", Uid = ready.Uid, TaskId = task.Id });
                        launched = true; launchedUids.Add(ready.Uid);
                    }
                }

                // keep Standby aircraft ready on the deck
                int readyLeft = 0;
                foreach (var a in deck) if (a.Where == "deck_ready" && !launchedUids.Contains(a.Uid)) readyLeft++;
                if (p.Standby > 0 && readyLeft < p.Standby)
                {
                    foreach (var a in deck)
                    {
                        if (a.Where == "deck_ready" || a.Allowed || launchedUids.Contains(a.Uid)) continue;
                        res.Add(new ReliefAction { Kind = "prepare", Uid = a.Uid, TaskId = task.Id, Loadout = first });
                        break;
                    }
                }

                // continuity
                if (inRotation < 3)
                {
                    double sortie = 0;
                    var cands = new List<AswMember>();
                    foreach (var a in m) if (!Excluded(a) && Candidate(a, task.Id)) { cands.Add(a); sortie = Math.Max(sortie, Math.Max(0, a.EnduranceS * 0.9 - a.TransitS)); }
                    double turnS = CycleTurnS(cands, p);   // T-213: landing + cool-down + preparation of the next order
                    double gapS = Math.Max(0, sortie + turnS - Math.Max(1, inRotation) * sortie);
                    // also the wait for the next aircraft while the current one runs out
                    if (air.Count > 0 && ready == null)
                    {
                        double nextIn = double.MaxValue;
                        foreach (var a in deck) nextIn = Math.Min(nextIn, ReadyS(a) + a.TransitS + a.LaunchS);
                        if (nextIn < double.MaxValue) gapS = Math.Max(gapS, nextIn - bestRemain);
                        else gapS = Math.Max(gapS, turnS);
                    }
                    res.Add(new ReliefAction { Kind = "warn", TaskId = task.Id, Code = "continuity_gap", GapMin = Math.Round(gapS / 60.0, 1) });
                    if (inRotation == 1 && air.Count == 1 && !launched && SearchBuoys(air[0]) > 0 && bestRemain < 600)
                        res.Add(new ReliefAction { Kind = "dump_buoys", Uid = air[0].Uid, TaskId = task.Id });
                }
                else if (air.Count == 0 && ready == null && deck.Count == 0)
                    res.Add(new ReliefAction { Kind = "warn", TaskId = task.Id, Code = "no_aircraft" });
                if (leaving != null && task.NeedBuoys > 0 && SearchBuoys(leaving) > 0 && inRotation >= 3 && bestRemain <= 0 && launched == false && ready == null)
                    res.Add(new ReliefAction { Kind = "dump_buoys", Uid = leaving.Uid, TaskId = task.Id });
            }
            return res;
        }

        /// <summary>Best buoy for a barrier: highest R_eff among the matching kind that lives long enough for a barrier.</summary>
        public static string PickBuoy(IList<BuoyType> onBoard, bool active, AswParams p)
        {
            string best = null; double bestR = -1;
            foreach (var b in onBoard)
            {
                if (b.Active != active || !BarrierBuoy(b.LifeS)) continue;
                double r = REff(b, p).Nm;
                if (r > bestR) { bestR = r; best = b.Ammo; }
            }
            return best;
        }

        // ---- T-172: helpers of the review fixes -----------------------------------------------------------------------

        /// <summary>The game takes at most this many points per DropSonobuoys / DipSonar order (Asw.cs AswMaxPoints).</summary>
        public const int MaxDropPoints = 40;

        /// <summary>T-172 R2: merge gap between buoys of two different types: MinGapFrac * (1 - overlap) * (ra + rb); equal ranges give MinGapNm(r).</summary>
        public static double PairGapNm(double ra, double rb) { return MinGapFrac * (1 - OverlapFrac) * (ra + rb); }

        /// <summary>Buoys that can search a barrier: passive and VLAD ones (active buoys only localise a contact).</summary>
        public static int SearchBuoys(AswMember m) { return m.Passive + m.Vlad; }

        /// <summary>T-172 B2: the altitude of a MAD leg: the wanted one, but at most 0.8 of the MAD maximum deployment altitude (ft, 0 = unknown), not below 50 ft.</summary>
        public static int MadAltFt(int wantFt, double maxDeployFt)
        {
            if (maxDeployFt <= 0) return wantFt;
            int cap = Math.Max(50, (int)Math.Floor(0.8 * maxDeployFt));
            return Math.Min(wantFt, cap);
        }

        /// <summary>
        /// T-172: the weapon row an ASW attack takes: category "asw" with ammunition left, a torpedo or an ASROC before a depth charge or an RBU;
        /// equal kinds keep the row order. rows = WeaponRows [file, display, type, target, category, count, ...]; -1 = none.
        /// </summary>
        public static int AswWeaponIndex(IList<string[]> rows)
        {
            int best = -1, bestRank = 99;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                int n;
                if (r.Length < 6 || r[4] != "asw" || !int.TryParse(r[5], out n) || n <= 0) continue;
                int rank = r[2] == "Torpedo" || r[2] == "ASROC" ? 0 : 1;
                if (rank < bestRank) { bestRank = rank; best = i; }
            }
            return best;
        }

        /// <summary>T-172 B1: centre and extent of a drawn rectangle (8 numbers lat,lon x 4 corners, any corner order): north-south and east-west miles.</summary>
        public static void RectSpans(double[] rect, out GeoPt centre, out double northNm, out double eastNm)
        {
            centre = new GeoPt((rect[0] + rect[2] + rect[4] + rect[6]) / 4, (rect[1] + rect[3] + rect[5] + rect[7]) / 4);
            double nMin = double.MaxValue, nMax = double.MinValue, eMin = double.MaxValue, eMax = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var l = ToLocal(centre, 0, new GeoPt(rect[2 * i], rect[2 * i + 1]));
                nMin = Math.Min(nMin, l.X); nMax = Math.Max(nMax, l.X); eMin = Math.Min(eMin, l.Y); eMax = Math.Max(eMax, l.Y);
            }
            northNm = nMax - nMin; eastNm = eMax - eMin;
        }

        internal struct BuoyAt { public GeoPt P; public double R; }

        /// <summary>
        /// T-172 R2: like <see cref="Thin"/>, but the gap to an existing buoy depends on both ranges (PairGapNm(rNew, e.R)); between the new points
        /// it is MinGapNm(rNew). The order of the kept points is unchanged.
        /// </summary>
        internal static List<GeoPt> ThinPair(IList<GeoPt> pts, double rNew, IList<BuoyAt> existing, out int skipped, double capGapNm = -1)
        {
            skipped = 0;
            var res = new List<GeoPt>();
            double own = capGapNm > 0 ? Math.Min(MinGapNm(rNew), capGapNm) : MinGapNm(rNew);   // T-176: a designed pattern (corridor rows) has neighbours closer than the merge gap
            foreach (var p in pts)
            {
                bool close = NearestNm(p, res) < own - 1e-9;
                if (!close && existing != null)
                    foreach (var e in existing) if (DistNm(p, e.P) < (capGapNm > 0 ? Math.Min(PairGapNm(rNew, e.R), capGapNm) : PairGapNm(rNew, e.R)) - 1e-9) { close = true; break; }
                if (close) { skipped++; continue; }
                res.Add(p);
            }
            return res;
        }

        /// <summary>
        /// T-172 O2: where the next rows package continues: X of the outer point of the last FULL column (all four points of the 2+2 lattice from x0 present)
        /// counted from the first column; NaN = not even the first column is complete (the cursor must not move).
        /// </summary>
        internal static double RowsCursorX(Func<NmPt, bool> present, double x0, double r, int maxCols)
        {
            double d = Spacing(r), p = RowGap(r), last = double.NaN;
            for (int i = 0; i < maxCols; i++)
            {
                double xi = x0 + i * d, xo = xi + d / 2;
                if (!(present(new NmPt(xi, -r)) && present(new NmPt(xi, r)) && present(new NmPt(xo, -(r + p))) && present(new NmPt(xo, r + p)))) break;
                last = xo;
            }
            return last;
        }

        public static string ParseDepth(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "above": return "AboveLayer";
                case "below": return "BelowLayer";
                case "alternate": return "Intermittent";
                default: return null;
            }
        }

        // ---- self test ---------------------------------------------------------------------------------------------

        private static string _err;
        private static void Eq(string what, double got, double want, double tol)
        {
            if (_err == null && !(Math.Abs(got - want) <= tol))
                _err = what + ": got " + got.ToString("R", CultureInfo.InvariantCulture) + " want " + want.ToString("R", CultureInfo.InvariantCulture);
        }
        private static void True(string what, bool v) { if (_err == null && !v) _err = what; }

        private static BuoyType Ssq53() { return new BuoyType { Ammo = "usn_ssq-53", Sensor = "SSQ-53_Sonar", PassiveKm = 10, LifeS = 28800 }; }
        private static BuoyType Ssq62() { return new BuoyType { Ammo = "usn_ssq-62", Sensor = "SSQ-62_Sonar", ActiveKm = 8, LifeS = 14400, Active = true }; }
        private static BuoyType Ssq77() { return new BuoyType { Ammo = "usn_ssq-77", Sensor = "SSQ-77_Sonar", PassiveKm = 20, LifeS = 28800, Vlad = true }; }
        private static BuoyType Rgb3() { return new BuoyType { Ammo = "wp_rgb-3", Sensor = "RGB-3_Sonar", PassiveKm = 10, ActiveKm = 8, LifeS = 300 }; }

        private static AswMember Mem(int uid, string where, double ready = 0)
        {
            return new AswMember { Uid = uid, Name = "H" + uid, Type = "SH-3H", Where = where, ReadyInS = ready, FuelFrac = where == "air" ? 0.9 : 1.0,
                                   EnduranceS = 9000, TransitS = 600, LaunchS = 120, Passive = 12, ActiveBuoys = 8, Vlad = 4, Torpedoes = 2, Dipping = true, Mad = true, Task = null };
        }

        /// <summary>
        /// 0.27.10 (live): a noisemaker / decoy track (the game's Overview names it "Noisemaker") is no submarine contact. Any of the names the
        /// game gives the track (class ini, display class, object type, object class) that reads as a countermeasure -> that name; else null.
        /// </summary>
        public static string DecoyName(params string[] names)
        {
            if (names == null) return null;
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                string l = n.ToLowerInvariant();
                if (l.Contains("noisemaker") || l.Contains("noise maker") || l.Contains("decoy") || l.Contains("countermeasure") || l.Contains("nixie")) return n;
            }
            return null;
        }

        private static void DecoySelfTest()
        {
            True("decoy noisemaker type", DecoyName(null, "Noisemaker") == "Noisemaker");
            True("decoy class ini", DecoyName("usn_noisemaker_mk2", null) != null);
            True("decoy display", DecoyName(null, null, "Decoy") == "Decoy");
            True("decoy kilo", DecoyName("su_kilo", "Kilo", "Submarine") == null);
            True("decoy none", DecoyName() == null && DecoyName(null, "") == null);
        }

        private static void HelperSelfTest()
        {
            Eq("pairgap equal", PairGapNm(2.7, 2.7), MinGapNm(2.7), 1e-9);
            Eq("pairgap mixed", PairGapNm(2.7, 5.4), 0.56 * 8.1, 1e-9);
            Eq("mad alt open", MadAltFt(100, 1000), 100, 0); Eq("mad alt capped", MadAltFt(100, 100), 80, 0); Eq("mad alt unknown", MadAltFt(100, 0), 100, 0);
            Eq("mad alt floor", MadAltFt(100, 20), 50, 0);
            var bomb = new[] { "dc", "DC", "AirDepthCharge", "Submarine", "asw", "4" };
            var torp = new[] { "mk46", "Mk46", "Torpedo", "Submarine", "asw", "2" };
            var torp0 = new[] { "mk46", "Mk46", "Torpedo", "Submarine", "asw", "0" };
            Eq("weapon torpedo first", AswWeaponIndex(new List<string[]> { bomb, torp }), 1, 0);
            Eq("weapon only bomb", AswWeaponIndex(new List<string[]> { bomb }), 0, 0);
            Eq("weapon empty torpedo", AswWeaponIndex(new List<string[]> { torp0, bomb }), 1, 0);
            Eq("weapon none", AswWeaponIndex(new List<string[]>()), -1, 0);
            var o = new GeoPt(35, 20);
            var tl = ToGeo(o, 0, new NmPt(3, -10)); var tr = ToGeo(o, 0, new NmPt(3, 10)); var br = ToGeo(o, 0, new NmPt(-3, 10)); var bl = ToGeo(o, 0, new NmPt(-3, -10));
            GeoPt c; double n, e;
            RectSpans(new[] { tl.Lat, tl.Lon, tr.Lat, tr.Lon, br.Lat, br.Lon, bl.Lat, bl.Lon }, out c, out n, out e);
            Eq("rect north", n, 6, 1e-6); Eq("rect east", e, 20, 1e-6); Eq("rect centre", c.Lat, 35, 1e-9);
            RectSpans(new[] { bl.Lat, bl.Lon, br.Lat, br.Lon, tr.Lat, tr.Lon, tl.Lat, tl.Lon }, out c, out n, out e);
            Eq("rect north 2", n, 6, 1e-6); Eq("rect east 2", e, 20, 1e-6);
            RectSpans(new[] { tl.Lat, tl.Lon, bl.Lat, bl.Lon, br.Lat, br.Lon, tr.Lat, tr.Lon }, out c, out n, out e);
            Eq("rect north 3", n, 6, 1e-6); Eq("rect east 3", e, 20, 1e-6);
            // ThinPair: the gap to a long-range buoy is larger
            var near = ToGeo(o, 0, new NmPt(4, 0));
            int sk;
            var kept = ThinPair(new[] { near }, 2.7, new[] { new BuoyAt { P = o, R = 2.7 } }, out sk);
            True("thinpair same type keeps", kept.Count == 1 && sk == 0);
            kept = ThinPair(new[] { near }, 2.7, new[] { new BuoyAt { P = o, R = 5.4 } }, out sk);
            True("thinpair long range drops", kept.Count == 0 && sk == 1);
            var two = ThinPair(new[] { o, ToGeo(o, 0, new NmPt(1, 0)) }, 2.7, null, out sk);
            True("thinpair own gap", two.Count == 1 && sk == 1);
            // RowsCursorX: 2+2 lattice from x0 = 0.5
            double r = 2.7, d = Spacing(r), p = RowGap(r), x0 = 0.5;
            var lat = new List<NmPt>();
            for (int i = 0; i < 4; i++) { lat.Add(new NmPt(x0 + i * d, -r)); lat.Add(new NmPt(x0 + i * d, r)); lat.Add(new NmPt(x0 + i * d + d / 2, -(r + p))); lat.Add(new NmPt(x0 + i * d + d / 2, r + p)); }
            Func<List<NmPt>, Func<NmPt, bool>> has = l => q => { foreach (var z in l) if (Math.Abs(z.X - q.X) < 0.01 && Math.Abs(z.Y - q.Y) < 0.01) return true; return false; };
            Eq("cursor full", RowsCursorX(has(lat), x0, r, 8), x0 + 3 * d + d / 2, 1e-9);
            var ten = new List<NmPt>(); foreach (var q in lat) ten.Add(q);
            ten.RemoveAt(15); ten.RemoveAt(14); ten.RemoveAt(11); ten.RemoveAt(10); ten.RemoveAt(7); ten.RemoveAt(6);   // 10 of 16: inner everywhere, outer only of the first column
            Eq("cursor ten", RowsCursorX(has(ten), x0, r, 8), x0 + d / 2, 1e-9);
            var inner = new List<NmPt>(); foreach (var q in lat) if (Math.Abs(q.Y) < r + 0.01) inner.Add(q);
            True("cursor none", double.IsNaN(RowsCursorX(has(inner), x0, r, 8)));
        }

        /// <summary>T-348: the world-flag section alone (stand aswtest world).</summary>
        public static string WorldTest() { _err = null; WorldSelfTest(); return _err; }

        public static string SelfTest()
        {
            _err = null;
            DecoySelfTest();
            var p = new AswParams();
            RSelfTest(p); RowsSelfTest(p); AreaSelfTest(p); AssignSelfTest(p); ReliefSelfTest(p); GeoSelfTest(); ExclusionSelfTest(); RouteSelfTest(); LaunchRouteSelfTest(); AdoptSelfTest(); SlotSelfTest(); PatternGuardSelfTest(); SpacingSelfTest(); HelperSelfTest(); WorldSelfTest();
            return _err;
        }

        private static void RSelfTest(AswParams p)
        {
            var r = REff(new BuoyType { PassiveKm = 10, GameEstimateNm = 3.0 }, p);
            Eq("reff game", r.Nm, 3.0, 1e-9); True("reff game src", r.Source == "game");
            r = REff(new BuoyType { PassiveKm = 10, GameEstimateNm = 0 }, p);
            Eq("reff ini", r.Nm, 2.70, 0.005); True("reff ini src", r.Source == "ini");
            r = REff(Ssq62(), p);
            Eq("reff active", r.Nm, 3.02, 0.005); True("reff active src", r.Source == "active");
            r = REff(new BuoyType { GameEstimateNm = 50 }, p);
            Eq("reff clamp hi", r.Nm, 20, 1e-9); True("reff clamp hi src", r.Source == "clamped:game");
            r = REff(new BuoyType { GameEstimateNm = 0.1 }, p);
            Eq("reff clamp lo", r.Nm, 0.3, 1e-9);
            Eq("xmax", XMax(15, 300), 1.0, 1e-9);
            True("barrier 300", !BarrierBuoy(300)); True("barrier 3600", BarrierBuoy(3600));
            Eq("dipstep 5.4", DipStep(5.4), 8, 0); Eq("dipstep 1.1", DipStep(1.1), 1, 0); Eq("dipstep 2.7", DipStep(2.7), 4, 0);
            Eq("dipstep tiny", DipStep(0.2), 1, 0);
            Eq("dipscreen", DipScreenKn(4, 120), 66.7, 0.1);
            True("leapfrog", DipLeapfrog(25, 15)); True("no leapfrog", !DipLeapfrog(40, 15));
            Eq("refresh ssq53", RefreshS(14, 8, 15, 28800), 1440, 0.5);
            Eq("refresh ssq47", RefreshS(14, 8, 15, 1800), 1260, 0.5);
            Eq("refresh rgb1", RefreshS(14, 8, 15, 2700), 1440, 0.5);
            Eq("crosswidth", CrossWidth(4, 2.7), 14.8, 1e-9); Eq("crosswidth min", CrossWidth(1, 1), 10, 1e-9);
            Eq("pick", PickBuoy(new List<BuoyType> { Ssq53(), Ssq62(), Ssq77() }, false, p) == "usn_ssq-77" ? 1 : 0, 1, 0);
            True("pick active", PickBuoy(new List<BuoyType> { Ssq53(), Ssq62(), Ssq77() }, true, p) == "usn_ssq-62");
            True("pick rgb3", PickBuoy(new List<BuoyType> { Rgb3() }, false, p) == null);
            True("depth", ParseDepth("below") == "BelowLayer" && ParseDepth("above") == "AboveLayer" && ParseDepth("alternate") == "Intermittent" && ParseDepth("x") == null);
        }

        private static void RowsSelfTest(AswParams p)
        {
            double r = 2.7, d = Spacing(r);
            Eq("d", d, 4.32, 1e-9); Eq("gap", RowGap(r), 4.86, 1e-9); Eq("rowcount", RowCount(12, d), 4, 0);
            int dropped;
            var rows = Rows(r, 12, 15, 28800, 100, out dropped);
            Eq("rows count", rows.Count, 16, 0); Eq("rows dropped", dropped, 0, 0);
            int innerOk = 0, outerOk = 0, shifted = 0;
            foreach (var pt in rows)
            {
                if (Math.Abs(Math.Abs(pt.Y) - 2.7) < 1e-9) innerOk++;
                if (Math.Abs(Math.Abs(pt.Y) - 7.56) < 1e-9) { outerOk++; if (Math.Abs((pt.X % d) - d / 2) < 1e-9) shifted++; }
            }
            Eq("inner", innerOk, 8, 0); Eq("outer", outerOk, 8, 0); Eq("outer shifted", shifted, 8, 0);
            var few = Rows(r, 12, 15, 28800, 10, out dropped);
            Eq("rows avail", few.Count, 10, 0); Eq("rows avail dropped", dropped, 6, 0);
            int innerFirst = 0; for (int i = 0; i < 8; i++) if (Math.Abs(Math.Abs(few[i].Y) - 2.7) < 1e-9) innerFirst++;
            Eq("inner first", innerFirst, 8, 0);
            var life = Rows(1.1, 12, 15, 2700, 100, out dropped);
            foreach (var pt in life) True("rows xmax", pt.X <= XMax(15, 2700) + 1e-9);
            True("rows xmax dropped", dropped > 0);
            var cross = Cross(14, 10, d);
            Eq("cross n", cross.Count, 4, 0);
            Eq("cross sym", cross[0].Y + cross[3].Y, 0, 1e-9); Eq("cross x", cross[2].X, 14, 0);
            var mad = MadSnake(8, 10, 1.6, 6.4);
            Eq("mad first x", mad[0].X, 8, 1e-9); Eq("mad leg", mad[2].X - mad[0].X, 1.6, 1e-9);
            var ring = StandingRing(8, 4.32);
            Eq("ring n", ring.Count, 12, 0); Eq("ring r", Math.Sqrt(ring[3].X * ring[3].X + ring[3].Y * ring[3].Y), 8, 1e-9);
            // T-142 K: the angle limit and the interval
            var lasw = new LoadoutOption { Name = "ASWKiller", Display = "ASW Killer" }; var ltr = new LoadoutOption { Name = "Transport", Display = "Transport" };
            var lw = new LoadoutOption { Name = "Mix", Weapons = new Dictionary<string, int> { { "usn_ssq-53", 8 } } };
            var lw0 = new LoadoutOption { Name = "Mix2", Weapons = new Dictionary<string, int> { { "usn_ssq-53", 0 }, { "rocket", 4 } } };
            True("aswlo name", IsAswLoadout(lasw, "ASWHunter,ASW")); True("aswlo csv", IsAswLoadout(new LoadoutOption { Name = "Sub", Display = "Sub" }, "Sub"));
            True("aswlo no", !IsAswLoadout(ltr, "ASWHunter,ASW")); True("aswlo weapons", IsAswLoadout(lw, "ASW")); True("aswlo zero", !IsAswLoadout(lw0, "")); True("aswlo null", !IsAswLoadout(null, "ASW"));
            var ch = new Dictionary<string, string> { { "SH-3H", "ASWKiller" }, { "SH-60B", "auto" } };
            True("csv chosen", LoadoutCsvFor(ch, "SH-3H", "A,B") == "ASWKiller"); True("csv auto", LoadoutCsvFor(ch, "SH-60B", "A,B") == "A,B");
            True("csv none", LoadoutCsvFor(ch, "X", "A,B") == "A,B"); True("csv null", LoadoutCsvFor(null, "X", "A,B") == "A,B");
            True("replan 29", !NeedReplan(0, 29, 15, 15)); True("replan 31", NeedReplan(0, 31, 15, 15));
            True("replan wrap", NeedReplan(350, 25, 15, 15)); True("replan v+31", NeedReplan(0, 0, 10, 13.1));
            True("replan v small", NeedReplan(0, 0, 15, 2.9)); True("no replan v", !NeedReplan(0, 0, 15, 16));
            string src;
            // waypoint agrees with the heading -> used; far from the heading -> the heading wins (ship evading / formation station point)
            Eq("axis wp", AxisDeg(90, 100, null, out src), 90, 1e-9); True("axis wp src", src == "wp");
            Eq("axis wp far", AxisDeg(90, 180, null, out src), 180, 1e-9); True("axis wp far src", src == "heading");
            // a zero-length track falls back to the heading, never to north
            var tr0 = new List<double[]> { new[] { 30.0, 10.0 }, new[] { 30.0, 10.0 } };
            Eq("axis zero track", AxisDeg(null, 145, tr0, out src), 145, 1e-9); True("axis zero src", src == "heading");
            Eq("axis no track", AxisDeg(null, 145, null, out src), 145, 1e-9);
            Eq("axis tiny track", AxisDeg(null, 145, new List<double[]> { new[] { 30.0, 10.0 }, new[] { 30.0 + 0.1 / 60, 10.0 } }, out src), 145, 1e-9);
            // track of 0.5 nm on 145 degrees (flat: 1 nm = 1/60 deg on both axes)
            double dn = 0.5 * Math.Cos(145 * Deg) / 60.0, de = 0.5 * Math.Sin(145 * Deg) / 60.0;
            var tr145 = new List<double[]> { new[] { 30.0, 10.0 }, new[] { 30.0 + dn, 10.0 + de } };
            Eq("axis track 145", AxisDeg(90, 10, tr145, out src), 145, 1e-6); True("axis track src", src == "track");
            var tr = new List<double[]> { new[] { 30.0, 10.0 }, new[] { 30.0 + 0.05, 10.0 } };
            Eq("axis track north", AxisDeg(null, 77, tr, out src), 0, 1e-6);
            var tr2 = new List<double[]> { new[] { 30.0, 10.0 }, new[] { 30.0, 10.05 } };
            Eq("axis track east", AxisDeg(null, 77, tr2, out src), 90, 1e-6);
        }

        private static void AreaSelfTest(AswParams p)
        {
            double cov;
            var g = AreaGrid(10, 10, 4.32, 4.86, 100, out cov);
            True("grid centre first", g.Count > 0 && Math.Abs(g[0].X) < 1e-9 && Math.Abs(g[0].Y) < 1e-9);
            Eq("grid cov", cov, 1.0, 1e-9);
            bool stag = false; foreach (var a in g) foreach (var b in g) if (Math.Abs(a.Y - b.Y) > 1 && Math.Abs(Math.Abs(a.X - b.X) - 2.16) < 0.01) stag = true;
            True("grid staggered", stag);
            var g2 = AreaGrid(10, 10, 4.32, 4.86, 3, out cov);
            Eq("grid few", g2.Count, 3, 0); True("grid cov<1", cov < 1.0 && cov > 0);
            var cl = CreepingLine(10, 6, 1.6);
            True("creep", cl.Count >= 6 && Math.Abs(cl[0].X + cl[1].X) < 1e-9);
            var es = ExpandingSquare(new NmPt(0, 0), 1.0, 6);
            Eq("es n", es.Count, 7, 0);
            double[] want = { 1, 1, 2, 2, 3, 3 };
            for (int i = 0; i < 6; i++)
                Eq("es leg" + i, Math.Sqrt(Math.Pow(es[i + 1].X - es[i].X, 2) + Math.Pow(es[i + 1].Y - es[i].Y, 2)), want[i], 1e-9);
            NmPt? act;
            var loc = Localize(new NmPt(5, 5), 2.7, true, out act);
            Eq("loc n", loc.Count, 4, 0);
            foreach (var q in loc)
            {
                double dd = Math.Sqrt(Math.Pow(q.X - 5, 2) + Math.Pow(q.Y - 5, 2));
                Eq("loc r", dd, 2.16, 1e-9);
            }
            True("loc active", act.HasValue && Math.Sqrt(Math.Pow(act.Value.X - 5, 2) + Math.Pow(act.Value.Y - 5, 2)) >= 1.0);
            var loc2 = Localize(new NmPt(0, 0), 1.0, false, out act);
            foreach (var q in loc2) True("loc min 1", Math.Sqrt(q.X * q.X + q.Y * q.Y) >= 1.0 - 1e-9);
            True("loc no active", !act.HasValue);
        }

        private static void LaunchRouteSelfTest()
        {
            var e = new DeckEntry { Key = "task:g1", ShipUid = 9, Uid = -5, Type = "SH-3H", State = "ready" };
            var m = DeckToMember(e);
            True("deck key carried", m.DeckKey == "task:g1");
            True("route ready task", LaunchRoute(m) == "allow");
            e.State = "prep"; e.ReadyInS = 30;
            True("route prep task", LaunchRoute(DeckToMember(e)) == "allow");
            var h = DeckToMember(new DeckEntry { Key = "hangar:SH-3H", ShipUid = 9, Uid = -6, Type = "SH-3H", State = "hangar" });
            True("route hangar", h.Where == "deck_service" && LaunchRoute(h) == "create");
            True("route air", LaunchRoute(new AswMember { Where = "air", Uid = 1 }) == null);
            True("route no key", LaunchRoute(new AswMember { Where = "deck_ready" }) == null);
        }

        private static void AssignSelfTest(AswParams p)
        {
            var dip = new AswMember { Uid = 1, Where = "air", Dipping = true };
            var buoy = new AswMember { Uid = 2, Where = "air", Passive = 12 };
            var mad = new AswMember { Uid = 3, Where = "air", Mad = true };
            var none = new AswMember { Uid = 4, Where = "air" };
            var pl = new AswMember { Uid = 5, Where = "player", Dipping = true };
            var sar = new AswMember { Uid = 6, Where = "sar", Passive = 12 };
            var a = Assign(new[] { dip, buoy, mad, none, pl, sar }, new[] { "dip", "rows", "mad" });
            True("assign dip", a[1] == "dip"); True("assign rows", a[2] == "rows"); True("assign mad", a[3] == "mad");
            True("assign reserve", a[4] == "reserve"); True("assign skip", !a.ContainsKey(5) && !a.ContainsKey(6));
            var b = Assign(new[] { buoy }, new[] { "cross" });
            True("assign cross", b[2] == "cross");
            var c = Assign(new[] { buoy, new AswMember { Uid = 7, Where = "air", Passive = 5 } }, new[] { "rows", "cross" });
            True("assign two buoy", c[2] == "rows" && c[7] == "cross");
            var m = new AswMember { FuelFrac = 0.5, EnduranceS = 7200, TransitS = 600, Passive = 12 };
            var t = new AswTaskSpec { Id = "x", NeedBuoys = 4, BuoysPerHour = 24 };
            Eq("remain buoys", RemainS(m, t), 1800, 1e-6);
            t.BuoysPerHour = 1;
            Eq("remain fuel", RemainS(m, t), (0.5 * 7200 - 600) * 0.9, 1e-6);
            m.AiState = "BingoFuel";
            Eq("remain bingo", RemainS(m, t), 0, 0);
            // T-172 R10: active buoys do not count for a barrier
            var act = new AswMember { Uid = 8, Where = "air", Passive = 4, ActiveBuoys = 8 }; var pas = new AswMember { Uid = 9, Where = "air", Passive = 12 };
            var r10 = Assign(new[] { act, pas }, new[] { "rows" });
            True("assign rows by passive", r10[9] == "rows" && r10[8] == "reserve");
            True("assign active only", Assign(new[] { new AswMember { Uid = 1, Where = "air", ActiveBuoys = 8 } }, new[] { "rows" })[1] == "reserve");
            Eq("remain rows by passive", RemainS(new AswMember { FuelFrac = 1, EnduranceS = 1e6, Passive = 4, ActiveBuoys = 20 }, new AswTaskSpec { NeedBuoys = 4, BuoysPerHour = 4 }), 3600, 1e-6);
            // T-172 B9: an airborne aircraft before a ready one on deck
            var dk = new AswMember { Uid = 11, Where = "deck_ready", Passive = 40 }; var ar = new AswMember { Uid = 12, Where = "air", Passive = 4 };
            True("assign air first", Assign(new[] { dk, ar }, new[] { "rows" })[12] == "rows");
            m.AiState = null; t.Holding = true; m.Torpedoes = 0;
            True("remain hold no weapon", RemainS(m, t) > 0);
        }

        private static int Count(List<ReliefAction> l, string kind) { int n = 0; foreach (var a in l) if (a.Kind == kind) n++; return n; }
        private static ReliefAction Find(List<ReliefAction> l, string kind) { foreach (var a in l) if (a.Kind == kind) return a; return null; }

        private static void ReliefSelfTest(AswParams p)
        {
            var task = new AswTaskSpec { Id = "rows", Tactic = "rows", NeedBuoys = 4, BuoysPerHour = 20 };
            var tasks = new List<AswTaskSpec> { task };
            // 3 aircraft: one low on fuel in the air, one ready, one in service
            var air = Mem(1, "air"); air.Task = "rows"; air.FuelFrac = 0.1;
            var rdy = Mem(2, "deck_ready"); rdy.Task = "rows";
            var svc = Mem(3, "deck_service", 1800); svc.Task = "rows";
            var plan = PlanRelief(new[] { air, rdy, svc }, tasks, 0, p);
            True("relief launch", Count(plan, "launch") == 1 && Find(plan, "launch").Uid == 2);
            True("relief no warn", Count(plan, "warn") == 0);
            // the airborne one is healthy: no launch
            air.FuelFrac = 0.9;
            plan = PlanRelief(new[] { air, rdy, svc }, tasks, 0, p);
            True("relief idle", Count(plan, "launch") == 0 && Count(plan, "warn") == 0);
            // standby: nobody ready -> prepare with first loadout
            rdy.Where = "deck_service"; rdy.ReadyInS = 600;
            plan = PlanRelief(new[] { air, rdy, svc }, tasks, 0, p);
            var prep = Find(plan, "prepare");
            True("relief prepare", prep != null && prep.Loadout == "ASWHunter" && prep.Uid == 2);
            // 2 aircraft: continuity gap
            var air2 = Mem(1, "air"); air2.Task = "rows"; air2.EnduranceS = 4000; air2.FuelFrac = 0.5;
            var dk = Mem(2, "deck_service", 1500); dk.Task = "rows"; dk.EnduranceS = 4000;
            plan = PlanRelief(new[] { air2, dk }, tasks, 0, p);
            var w = Find(plan, "warn");
            True("relief 2 warn", w != null && w.Code == "continuity_gap" && w.GapMin > 0);
            // 1 aircraft about to leave: warn + dump
            var one = Mem(1, "air"); one.Task = "rows"; one.FuelFrac = 0.07; one.EnduranceS = 9000;
            plan = PlanRelief(new[] { one }, tasks, 0, p);
            True("relief 1 warn", Count(plan, "warn") == 1 && Count(plan, "dump_buoys") == 1);
            // player / sar are never relief candidates
            var pl = Mem(9, "player"); var sr = Mem(8, "sar");
            plan = PlanRelief(new[] { pl, sr }, tasks, 0, p);
            True("relief ignore", Count(plan, "launch") == 0 && Count(plan, "prepare") == 0);
            ReliefWantSelfTest(p);
        }

        private static AswMember Holder(int uid, string where, string task, double ready = 0, bool allowed = false)
        {
            var h = Mem(uid, where, ready); h.Task = task; h.Allowed = allowed; return h;
        }

        /// <summary>T-181 A4: the lanes (Want &gt;= 2).</summary>
        private static void ReliefWantSelfTest(AswParams p)
        {
            var task = new AswTaskSpec { Id = "corridor", Tactic = "corridor", Want = 3 };
            var tasks = new List<AswTaskSpec> { task };
            var plan = PlanRelief(new[] { Holder(1, "deck_service", "corridor", 600), Holder(2, "deck_ready", "corridor"), Holder(3, "deck_service", "corridor", 100) }, tasks, 0, p);
            True("W3 start", Count(plan, "launch") == 3 && plan[0].Kind == "launch" && plan[0].Uid == 2 && plan[1].Uid == 3 && plan[2].Uid == 1);
            var a1 = Holder(1, "air", "corridor"); var a2 = Holder(2, "air", "corridor");
            plan = PlanRelief(new[] { a1, a2, Holder(3, "deck_prep", "corridor", 60, true) }, tasks, 0, p);
            True("W3 covered", Count(plan, "launch") == 0);
            plan = PlanRelief(new[] { a1, a2, Holder(3, "deck_launching", "corridor") }, tasks, 0, p);
            True("W3 launching", Count(plan, "launch") == 0);
            var a3 = Holder(3, "air", "corridor");
            var rdy = Holder(4, "deck_ready", "reserve");
            plan = PlanRelief(new[] { a1, a2, a3, rdy }, tasks, 0, p);
            True("W3 no low no launch", Count(plan, "launch") == 0);
            a2.FuelFrac = 0.1;
            plan = PlanRelief(new[] { a1, a2, a3, rdy }, tasks, 0, p);
            True("W3 relief lane", Count(plan, "launch") == 1 && Find(plan, "launch").Uid == 4);
            a3.FuelFrac = 0.1;
            plan = PlanRelief(new[] { a1, a2, a3, rdy, Holder(5, "deck_ready", "reserve") }, tasks, 0, p);
            True("W3 two low", Count(plan, "launch") == 2);
            plan = PlanRelief(new[] { a1, a2, a3, rdy, Holder(5, "deck_ready", "reserve") , Holder(6, "deck_prep", "reserve", 0, true) }, tasks, 0, p);
            True("W3 low already covered", Count(plan, "launch") == 1);
            a2.FuelFrac = 0.9; a3.FuelFrac = 0.9;
            plan = PlanRelief(new[] { a1, a2, a3, Holder(7, "deck_service", "reserve", 600) }, tasks, 0, p);
            True("W3 standby prepare", Count(plan, "launch") == 0 && Count(plan, "prepare") == 1 && Find(plan, "prepare").Uid == 7);
            var one = new AswTaskSpec { Id = "corridor", Tactic = "corridor", Want = 1 };
            var zero = new AswTaskSpec { Id = "corridor", Tactic = "corridor", Want = 0 };
            var set = new[] { Holder(1, "air", "corridor"), Holder(2, "deck_ready", "corridor"), Holder(3, "deck_service", "corridor", 900) };
            set[0].FuelFrac = 0.1;
            var p1 = PlanRelief(set, new List<AswTaskSpec> { one }, 0, p); var p0 = PlanRelief(set, new List<AswTaskSpec> { zero }, 0, p);
            bool same = p1.Count == p0.Count;
            for (int i = 0; same && i < p1.Count; i++) same = p1[i].Kind == p0[i].Kind && p1[i].Uid == p0[i].Uid;
            True("W1 same as W0", same && Count(p1, "launch") == 1);
        }

        private static void RouteSelfTest()
        {
            var o = new GeoPt(35, 20);
            // rows laid by X then Y zigzag across the formation; starting at the far end the reversed order is shorter
            var pts = new List<GeoPt>();
            for (int i = 0; i < 6; i++) { pts.Add(ToGeo(o, 90, new NmPt(3 + i * 4, -3))); pts.Add(ToGeo(o, 90, new NmPt(3 + i * 4, 3))); }
            var far = ToGeo(o, 90, new NmPt(40, 0));
            var best = BestOrder(far, pts);
            True("route count", best.Count == pts.Count);
            True("route not longer than naive", RouteLengthNm(far, best) <= RouteLengthNm(far, pts) + 1e-9);
            True("route shorter from far end", RouteLengthNm(far, best) < RouteLengthNm(far, pts) - 5);
            // a shuffled grid: nearest-neighbour beats the naive order a lot
            var shuffled = new List<GeoPt>();
            int[] idx = { 0, 7, 2, 9, 4, 11, 1, 8, 3, 10, 5, 6 };
            foreach (int k in idx) shuffled.Add(pts[k]);
            var near = ToGeo(o, 90, new NmPt(0, 0));
            True("shuffled shorter", RouteLengthNm(near, BestOrder(near, shuffled)) < 0.6 * RouteLengthNm(near, shuffled));
            // already optimal stays as is
            var line = new List<GeoPt> { ToGeo(o, 90, new NmPt(1, 0)), ToGeo(o, 90, new NmPt(2, 0)), ToGeo(o, 90, new NmPt(3, 0)) };
            var same = BestOrder(o, line);
            True("optimal kept", same[0].Lon == line[0].Lon && same[2].Lon == line[2].Lon);
        }

        private static void ExclusionSelfTest()
        {
            var e2c = new AswMember { Uid = 1, Type = "E-2C", Where = "deck_ready" };
            True("E-2C aew", ExclusionReason(e2c) == "aew");
            var e2r = new AswMember { Uid = 1, Type = "Hawkeye", Where = "air", Roles = new[] { "AEW" } };
            True("AEW role", ExclusionReason(e2r) == "aew");
            True("AEW load never capable", !CapableLoad(new[] { "AEW" }, 4, true, true, 2));
            var bare = new AswMember { Uid = 2, Type = "SH-60", Where = "air", Roles = new[] { "ASW" } };
            True("no load", ExclusionReason(bare) == "no_asw_load");
            True("buoys capable", ExclusionReason(new AswMember { Where = "air", Type = "SH-3H", Passive = 12 }) == null);
            True("dip capable", ExclusionReason(new AswMember { Where = "deck_prep", Type = "SH-2F", Dipping = true }) == null);
            True("torpedoes capable", CapableLoad(null, 0, false, false, 2) && !CapableLoad(null, 0, false, false, 0));
            True("player", ExclusionReason(new AswMember { Where = "player", Passive = 12 }) == "player");
            True("ext_sar", ExclusionReason(new AswMember { Where = "sar", Passive = 12 }) == "ext_sar");
            True("game_sar_zone", ExclusionReason(new AswMember { Where = "air", Passive = 12, GameSarZone = true }) == "game_sar_zone");
            // T-109: SAR groups
            True("sar capable helo", SarCapable("Helicopter", new[] { 0, 8 }) && !SarCapable("Helicopter", new[] { 0, 0 }) && !SarCapable("Aircraft", new[] { 8 }) && !SarCapable("Helicopter", null));
            var sh2 = new AswMember { Where = "air", Helicopter = true, Capacity = 2, BestCapacity = 8 };
            True("sar ok", SarExclusionReason(sh2) == null);
            True("sar no seats", SarExclusionReason(new AswMember { Where = "air", Helicopter = true }) == "no_capacity");
            True("sar not helo", SarExclusionReason(new AswMember { Where = "air", Helicopter = false, BestCapacity = 8 }) == "no_capacity");
            True("sar ext", SarExclusionReason(new AswMember { Where = "sar", Helicopter = true, BestCapacity = 8 }) == "ext_sar");
            True("sar zone", SarExclusionReason(new AswMember { Where = "air", Helicopter = true, BestCapacity = 8, GameSarZone = true }) == "game_sar_zone");
            True("sar player", SarExclusionReason(new AswMember { Where = "player", Helicopter = true, BestCapacity = 8 }) == "player");
            True("deck heli flag", DeckToMember(new DeckEntry { Uid = -9, ShipUid = 7, Type = "SH-3H", State = "ready", Helicopter = true }).Helicopter);
            True("patrol", ExclusionReason(new AswMember { Where = "game_patrol", Passive = 12 }) == "game_patrol");
            True("landed", ExclusionReason(new AswMember { Where = "landed", Passive = 12 }) == "landed");
            True("lost", ExclusionReason(new AswMember { Where = "lost", Passive = 12 }) == "lost");
            True("usable", Usable(new AswMember { Where = "deck_prep" }) && !Usable(new AswMember { Where = "deck_launching" }));
            int pa, ac, vl, tp;
            AmmoLoad(new Dictionary<string, int> { { "usn_ssq-53", 20 }, { "usn_ssq-62", 6 }, { "usn_ssq-77", 4 }, { "usn_mk46", 2 }, { "usn_flare", 9 } }, out pa, out ac, out vl, out tp);
            True("ammo load", pa == 20 && ac == 6 && vl == 4 && tp == 2);
            // deck rows: a prepared and a ready SH-2F both become usable ASW members
            var ready = DeckToMember(new DeckEntry { Uid = -5, ShipUid = 7, Type = "SH-2F", Label = "SH-2F", State = "ready", AswCapableType = true, Ammo = new Dictionary<string, int> { { "usn_ssq-53", 12 }, { "usn_mk46", 2 } } });
            var prep = DeckToMember(new DeckEntry { Uid = -6, ShipUid = 7, Type = "SH-2F", Label = "SH-2F", State = "prep", ReadyInS = 240, AswCapableType = true, Count = 2 });
            True("deck ready", ready.Where == "deck_ready" && ready.Passive == 12 && ready.Torpedoes == 2 && ready.Dipping && ExclusionReason(ready) == null);
            True("deck prep nominal", prep.Where == "deck_prep" && prep.ReadyInS == 240 && prep.Name == "SH-2F" && prep.ShipUid == 7 && prep.Base == null && !prep.Name.Contains("(") && ExclusionReason(prep) == null);
            var awk = DeckToMember(new DeckEntry { Uid = -7, ShipUid = 7, Type = "E-2C", State = "hangar", Helicopter = false, AswCapableType = false });
            True("deck aew excluded", ExclusionReason(awk) == "aew" && awk.Where == "deck_service" && !awk.Dipping);
        }

        // ADR-0063: spacing from the overlap, the merge gap, thinning, the rows going on ahead of covered water
        private static void SpacingSelfTest()
        {
            Eq("overlap spacing", Spacing(2.7), 2 * 2.7 * (1 - OverlapFrac), 1e-12); Eq("overlap nm", OverlapNm(2.7), 1.08, 1e-9);
            Eq("gap", MinGapNm(2.7), 3.024, 1e-9); True("contact gap smaller", LocalizeGapNm(2.7) < MinGapNm(2.7));
            var o = new GeoPt(35, 20);
            int sk;
            var t = Thin(new List<GeoPt> { Offset(o, 90, 1), Offset(o, 90, 5), Offset(o, 90, 6) }, new List<GeoPt> { o }, 3.0, out sk);
            True("thin", t.Count == 1 && sk == 2 && DistNm(t[0], Offset(o, 90, 5)) < 1e-9);
            int dr; double end;
            var a = RowsAhead(2.7, 12, 15, 28800, 100, 3, q => q.X < 15, 3, out dr, out sk, out end);
            True("rows ahead", a.Count == 16 && sk > 0);
            foreach (var q in a) True("rows ahead past covered", q.X >= 15 - 1e-9);
        }

        private static void PatternGuardSelfTest()
        {
            int logged = 0;
            var old = GuardLog; GuardLog = s => logged++;
            _guardLogged = false;
            True("snake leg 0", MadSnake(1, 4, 0, 10).Count == 0);
            True("snake leg NaN", MadSnake(1, 4, double.NaN, 10).Count == 0);
            True("snake length inf", MadSnake(1, 4, 1, double.PositiveInfinity).Count == 0);
            True("creep leg 0", CreepingLine(10, 10, 0).Count == 0);
            True("creep leg inf", CreepingLine(10, 10, double.PositiveInfinity).Count == 0);
            True("creep tiny leg capped", CreepingLine(10, 10, 1e-5).Count <= 2 * PatternMaxSteps);
            True("pattern guard logs once", logged == 1);
            True("snake normal", MadSnake(1, 4, 2, 10).Count > 0 && CreepingLine(10, 10, 2).Count > 0);
            GuardLog = old; _guardLogged = false;
        }

        // ---- T-348: the world flag, LonK and the cell cache ----
        private static GeoPt OldOffset(GeoPt g, double brg, double d) { double b = brg * Deg; return new GeoPt(g.Lat + d * Math.Cos(b) / 60.0, g.Lon + d * Math.Sin(b) / 60.0); }
        private static GeoPt OldToGeo(GeoPt o, double ax, NmPt p) { double a = ax * Deg; double n = p.X * Math.Cos(a) - p.Y * Math.Sin(a), e = p.X * Math.Sin(a) + p.Y * Math.Cos(a); return new GeoPt(o.Lat + n / 60.0, o.Lon + e / 60.0); }
        private static NmPt OldToLocal(GeoPt o, double ax, GeoPt g) { double a = ax * Deg; double n = (g.Lat - o.Lat) * 60.0, e = (g.Lon - o.Lon) * 60.0; return new NmPt(n * Math.Cos(a) + e * Math.Sin(a), -n * Math.Sin(a) + e * Math.Cos(a)); }
        private static double OldDist(GeoPt a, GeoPt b) { double n = (b.Lat - a.Lat) * 60.0, e = (b.Lon - a.Lon) * 60.0; return Math.Sqrt(n * n + e * e); }
        private static double OldBrg(GeoPt a, GeoPt b) { double n = (b.Lat - a.Lat) * 60.0, e = (b.Lon - a.Lon) * 60.0; return Norm360(Math.Atan2(e, n) / Deg); }

        private static void WorldSelfTest()
        {
            bool prev = WorldGlobe;
            try
            {
                WorldGlobe = false;
                foreach (double la in new[] { 0.0, 60.0, 89.9 }) True("flat LonK " + la, LonK(la) == 1.0 && LonK(la, la + 3) == 1.0);
                using (WorldScope(true))
                {
                    Eq("globe LonK 60", LonK(60), 0.5, 1e-12);
                    Eq("globe LonK 89.9", LonK(89.9), MinCosLat, 1e-12);
                    Eq("globe offset 60 east", Offset(new GeoPt(60, 10), 90, 10).Lon - 10, 10.0 / 30.0, 1e-12);
                }
                True("scope restored", !WorldGlobe);
                Eq("flat offset 60 east", Offset(new GeoPt(60, 10), 90, 10).Lon - 10, 10.0 / 60.0, 1e-12);
                WorldBitwise();
                foreach (double lat in new[] { 0.0, 45.0, 60.0, 70.0 })
                {
                    var P = new GeoPt(lat, 10.0);
                    using (WorldScope(true))
                    {
                        foreach (double brg in new[] { 0.0, 45.0, 90.0, 135.0 })
                            foreach (double d in new[] { 10.0, 50.0, 80.0 })
                            {
                                var Q = Offset(P, brg, d);
                                Eq("gc " + lat + "/" + brg + "/" + d, GreatCircleNm(P, Q) / d, 1.0, 0.015);
                                Eq("dist back " + lat + "/" + brg, DistNm(P, Q), d, 1e-9);
                            }
                        WorldRoundtrip(P);
                    }
                    foreach (double brg in new[] { 0.0, 45.0, 90.0, 135.0 }) Eq("flat dist back " + lat, DistNm(P, Offset(P, brg, 50)), 50, 1e-9);
                    WorldRoundtrip(P);
                }
                WorldCacheSelfTest();
            }
            finally { WorldGlobe = prev; }
        }

        private static void WorldRoundtrip(GeoPt o)
        {
            foreach (double ax in new[] { 0.0, 37.0, 170.0, 300.0 })
            {
                var pt = new NmPt(30, -12);
                var back = ToLocal(o, ax, ToGeo(o, ax, pt));
                Eq("world roundtrip x", back.X, pt.X, 1e-9); Eq("world roundtrip y", back.Y, pt.Y, 1e-9);
            }
        }

        private static void WorldBitwise()
        {
            WorldGlobe = false;
            ulong s = 12345;
            Func<double, double, double> rnd = (lo, hi) => { s = s * 6364136223846793005UL + 1442695040888963407UL; return lo + (hi - lo) * ((s >> 11) / (double)(1UL << 53)); };
            for (int i = 0; i < 200; i++)
            {
                var o = new GeoPt(rnd(-75, 75), rnd(-170, 170));
                double brg = Math.Floor(rnd(0, 360)), d = rnd(0, 120);
                var g = OldOffset(o, brg, d);
                var np = new NmPt(rnd(-100, 100), rnd(-100, 100));
                var off = Offset(o, brg, d);
                True("bit Offset", off.Lat == g.Lat && off.Lon == g.Lon);
                var tg = ToGeo(o, brg, np); var otg = OldToGeo(o, brg, np);
                True("bit ToGeo", tg.Lat == otg.Lat && tg.Lon == otg.Lon);
                var tl = ToLocal(o, brg, g); var otl = OldToLocal(o, brg, g);
                True("bit ToLocal", tl.X == otl.X && tl.Y == otl.Y);
                var fl = FlatNm(o, g);
                True("bit FlatNm", fl.X == (g.Lat - o.Lat) * 60.0 && fl.Y == (g.Lon - o.Lon) * 60.0);
                True("bit DistNm", DistNm(o, g) == OldDist(o, g));
                True("bit BearingDeg", BearingDeg(o, g) == OldBrg(o, g));
                var tr = new List<double[]> { new[] { o.Lat, o.Lon }, new[] { g.Lat, g.Lon } };
                string src; double ax = AxisDeg(null, 77, tr, out src);
                double n = (g.Lat - o.Lat) * 60.0, e = (g.Lon - o.Lon) * 60.0;
                double want = Math.Sqrt(n * n + e * e) >= MinTrackNm ? Norm360(Math.Atan2(e, n) / Deg) : 77.0;
                True("bit AxisDeg", ax == want);
            }
        }

        private static void WorldCacheSelfTest()
        {
            var c = new WorldCellCache();
            bool g;
            True("cache empty", !c.TryGet(1, 0, out g) && !g && !c.Last);
            True("cache put globe changes Last", c.Put(1, 0, true, true, true) && c.Last);
            True("cache decided hit", c.TryGet(1, 1, out g) && g);
            True("cache same world no change", !c.Put(2, 1, true, true, true));
            True("cache flat cell changes Last", c.Put(3, 1, true, true, false) && !c.Last);
            True("cache decided keeps own value", c.TryGet(1, 2, out g) && g);
            True("cache undecided no Last change", !c.Put(4, 2, true, false, true) && !c.Last);
            True("cache undecided permanent", c.TryGet(4, 1000, out g) && !g);
            True("cache failed no Last change", !c.Put(5, 10, false, false, false) && !c.Last);
            True("cache failed waits", c.TryGet(5, 10 + WorldCellCache.RetryS - 0.1, out g) && !g);
            True("cache failed retries", !c.TryGet(5, 10 + WorldCellCache.RetryS, out g));
            for (int i = 0; i <= WorldCellCache.Cap + 3; i++) c.Put(1000 + i, 20, true, true, false);
            True("cache cleared on overflow", !c.TryGet(1, 21, out g));
        }

        private static void GeoSelfTest()
        {
            var o = new GeoPt(35.0, 20.0);
            foreach (double ax in new[] { 0.0, 45.0, 170.0, 300.0 })
            {
                var pt = new NmPt(30, -12);
                var g = ToGeo(o, ax, pt);
                var back = ToLocal(o, ax, g);
                Eq("roundtrip x", back.X, pt.X, 0.01); Eq("roundtrip y", back.Y, pt.Y, 0.01);
            }
            var north = ToGeo(o, 0, new NmPt(60, 0));
            Eq("north 60nm", north.Lat, 36.0, 1e-9);
            var right = ToGeo(o, 0, new NmPt(0, 10));
            True("right is east", right.Lon > o.Lon && Math.Abs(right.Lat - o.Lat) < 1e-9);
            Eq("dist", DistNm(o, north), 60, 0.01); Eq("brg", BearingDeg(o, right), 90, 0.01);
            var off = Offset(o, 90, 10);
            Eq("offset", DistNm(o, off), 10, 0.01);
            // game rule: 1/60 degree per mile on both axes, no cos(lat)
            Eq("flat east lon", off.Lon - o.Lon, 10.0 / 60.0, 1e-12);
            var fl = FlatNm(o, new GeoPt(36.0, 20.5));
            Eq("flat n", fl.X, 60, 1e-9); Eq("flat e", fl.Y, 30, 1e-9);
            Eq("flat dist", DistNm(o, new GeoPt(36.0, 20.5)), Math.Sqrt(60 * 60 + 30 * 30), 1e-9);
        }
    }
}
