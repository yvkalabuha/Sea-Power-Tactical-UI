using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 / ADR-0076: pure geometry of the ASW search patterns v3 (area lattice, corridor, barrier, lanes, anchor). No Unity, no game.
    // Local frames: course frame X forward / Y right (as AswMath); area frame X north / Y east (as the area rectangle).

    /// <summary>One by-course order (command, save, window draft).</summary>
    internal sealed class AswCourseSpec
    {
        public List<string> Tactics = new List<string>();   // dip | corridor | barrier, order dip > corridor > barrier; empty = auto
        public double CorridorXNm = 20, BarrierXNm = 40, DipLNm = 20, CorridorLNm = 20, BarrierLNm = 40;
        public int BarrierRows = 2;                           // T-229: rows ahead of the ship (1..5)
        public double BarrierGapNm = 5;                       // T-229: the interval of the rows (nm); the first row is one interval from the ship
        public string Depth = "auto";                         // auto | above | below | alternate
    }

    internal sealed class LatNode { public int I, J, Ring; public NmPt P; public double AngDeg; }

    internal sealed class LaneMember { public int Uid; public bool Dip, Buoys, Mad, Air; public double SpeedKn, RemainS, ReadyInS, EtaS; public int BuoyCount; }

    /// <summary>Terrain-fixed anchor of a course pattern. TurnSinceS: time the axis deviation began (<= 0 = none).</summary>
    internal struct AswAnchor { public bool Set, Stand; public GeoPt O; public double AxisDeg, T0, VKn, TurnSinceS; }

    /// <summary>Summary input. LifeS / FlyS (0 = unknown) enable the life_short warning of a barrier; Rows / GapNm / Stagger describe the sliding barrier (T-229, LNm is not used by it).</summary>
    internal sealed class AswSummaryIn
    {
        public string Order, Tactic;     // Order: course | area; Tactic: dip | corridor | barrier | grid | mad
        public double R, RDip, XNm, LNm, VKn, AreaNorthNm, AreaEastNm, LifeS, FlyS, GapNm = 5;
        public int Buoys, Members, MaxRows = 6, Rows = 2;
        public bool Stagger = true;
    }

    internal sealed class AswSummary
    {
        public int RowsSide, RowsMax, Nx, Ny, Nodes, Buoys;
        public int RowsPlan, BuoysEven, BuoysOdd, RowsAffordable;   // T-229: the sliding barrier: rows in the plan, buoys of an even / odd row, rows the buoys cover
        public double StepNm, BuoysPerHour, HoursLeft = -1;
        public string Warn;   // null | buoys_short | corridor_too_wide | life_short | barrier_rows_short
    }

    internal static partial class AswPatternMath
    {
        public static readonly double[] WidthList = { 10, 20, 30, 40, 50 };
        public static readonly double[] BarrierGapList = { 3, 5, 8, 10 };   // T-229: the interval of the barrier rows (nm)
        public static readonly int[] BarrierRowsList = { 1, 2, 3, 4, 5 };
        public static readonly double[] StandoffList = { 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        public static readonly double[] AreaDistList = { 5, 10, 15, 20, 30, 40, 50, 60, 80, 100 };
        public static readonly double[] AreaSizeList = { 5, 10, 15, 20, 30, 40, 50, 60 };
        public static readonly double[] SarRadiusList = { 2, 5, 10, 15, 20, 30 };
        // T-334 / ADR-0130: corridor and barrier exclude each other; the barrier wins where both come from a save / cfg / memory
        public static readonly string[] CourseCombos = { "auto", "dip", "corridor", "barrier", "dip+corridor", "dip+barrier" };
        public static readonly string[] AreaPatterns = { "auto", "grid", "dip", "mad" };
        private static readonly string[] CourseOrder = { "dip", "corridor", "barrier" };

        /// <summary>Nearest item of the list; a tie goes to the larger one. Empty list -> v.</summary>
        public static double Snap(double v, double[] list)
        {
            if (list == null || list.Length == 0 || double.IsNaN(v)) return v;
            double best = list[0], bd = Math.Abs(v - best);
            for (int i = 1; i < list.Length; i++)
            {
                double d = Math.Abs(v - list[i]);
                if (d < bd - 1e-9 || (Math.Abs(d - bd) <= 1e-9 && list[i] > best)) { best = list[i]; bd = d; }
            }
            return best;
        }

        /// <summary>"dip+barrier" -> [dip, barrier] in the order dip > corridor > barrier; auto / unknown -> [].</summary>
        public static List<string> ComboTactics(string combo)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(combo)) return res;
            var set = new HashSet<string>(NormCombo(combo.Trim().ToLowerInvariant()).Split('+'));
            foreach (string t in CourseOrder) if (set.Contains(t)) res.Add(t);
            return res;
        }

        /// <summary>Both corridor and barrier in the list.</summary>
        public static bool TacticsConflict(IList<string> tactics)
        {
            return tactics != null && tactics.Contains("corridor") && tactics.Contains("barrier");
        }

        /// <summary>A copy without "corridor" when "barrier" is there (order kept); dropped = true when it removed one.</summary>
        public static List<string> ExclusiveTactics(IList<string> tactics, out bool dropped)
        {
            dropped = false;
            var res = new List<string>();
            if (tactics == null) return res;
            bool conflict = TacticsConflict(tactics);
            foreach (string t in tactics)
            {
                if (conflict && t == "corridor") { dropped = true; continue; }
                res.Add(t);
            }
            return res;
        }

        /// <summary>An old combo string (corridor+barrier, dip+corridor+barrier) -> barrier / dip+barrier; others as is.</summary>
        public static string NormCombo(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return combo;
            var parts = new List<string>(combo.Trim().ToLowerInvariant().Split('+'));
            if (!(parts.Contains("corridor") && parts.Contains("barrier"))) return combo;
            bool dropped;
            return string.Join("+", ExclusiveTactics(parts, out dropped).ToArray());
        }

        /// <summary>Inverse of ComboTactics: the tactics in canonical order joined by '+', none known -> "auto".</summary>
        public static string ComboOf(IList<string> tactics)
        {
            if (tactics == null) return "auto";
            var res = new List<string>();
            foreach (string t in CourseOrder) if (tactics.Contains(t)) res.Add(t);
            return res.Count == 0 ? "auto" : string.Join("+", res.ToArray());
        }

        private static string SelfTestSnap()
        {
            if (Snap(25, WidthList) != 30) return "snap tie 25 -> " + Snap(25, WidthList);
            if (Snap(12, AreaSizeList) != 10) return "snap 12 -> " + Snap(12, AreaSizeList);
            if (Snap(100, StandoffList) != 100 || Snap(5, StandoffList) != 20) return "snap clamp";
            var c = ComboTactics("dip+barrier");
            if (c.Count != 2 || c[0] != "dip" || c[1] != "barrier") return "combo tactics";
            if (ComboTactics("auto").Count != 0 || ComboTactics(null).Count != 0 || ComboTactics("zzz").Count != 0) return "combo auto";
            if (ComboOf(new List<string> { "barrier", "dip" }) != "dip+barrier") return "combo of";
            if (ComboOf(new List<string>()) != "auto" || ComboOf(new List<string> { "rows" }) != "auto") return "combo of auto";
            foreach (string s in CourseCombos) if (ComboOf(ComboTactics(s)) != s) return "combo round trip " + s;
            if (NormCombo("corridor+barrier") != "barrier" || NormCombo("dip+corridor+barrier") != "dip+barrier" || NormCombo("dip+corridor") != "dip+corridor" || NormCombo("auto") != "auto") return "norm combo";
            if (ComboOf(ComboTactics("dip+corridor+barrier")) != "dip+barrier") return "combo tactics exclusive";
            bool dr; var ex = ExclusiveTactics(new List<string> { "dip", "corridor", "barrier" }, out dr);
            if (!dr || ex.Count != 2 || ex[0] != "dip" || ex[1] != "barrier" || !TacticsConflict(new List<string> { "corridor", "barrier" }) || TacticsConflict(new List<string> { "dip", "barrier" })) return "exclusive";
            return null;
        }

        public static string SelfTest() { return SelfTestSnap() ?? SelfTestArea() ?? SelfTestCourse() ?? SelfTestPlan(); }
    }
}
