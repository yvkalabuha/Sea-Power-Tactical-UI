using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-153: cells of the contact_info columns of one Overview row (from IContactInfo only).</summary>
    internal sealed class OvCiCells
    {
        public string Dep = "—", Lyr = "—", Cpa = "—", Tcpa = "—", Ttg = "—";
        public double SortDep = double.NaN, SortLyr = double.NaN, SortCpa = double.NaN, SortTcpa = double.NaN, SortTtg = double.NaN;
    }

    /// <summary>T-153: UI column set of the Overview = OverviewMath.AllColumns (Common, read only) + contact_info columns.</summary>
    internal static class OverviewColumns
    {
        public static readonly string[] Extra = { "dep", "lyr", "cpa", "tcpa", "ttg" };
        /// <summary>T-168: the columns of the hit estimate (hits = own hits on the track, dmg = the estimated damage status).</summary>
        public static readonly string[] HitCols = { "hits", "dmg" };
        public static readonly string[] All = OverviewMath.AllColumns.Concat(Extra).Concat(HitCols).ToArray();
        public const string DefaultSpec = "icon,type,dist,brg,spd,alt,dep,crs,cpa,age,idn,hits,dmg";   // new installs only (ADR-0045); config step 4 adds hits,dmg to the untouched old default (T-168)
        private const string Dash = "—";

        public static bool IsExtra(string col) { return Array.IndexOf(Extra, col) >= 0; }

        public static bool IsHit(string col) { return Array.IndexOf(HitCols, col) >= 0; }

        /// <summary>T-168: text of a hits / dmg cell; null cell = "—".</summary>
        public static string HitCellText(HeCell c, string col)
        {
            if (c == null) return Dash;
            if (col == "hits") return c.Hits.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (col == "dmg") return c.Status == HeStatus.None ? Dash : HitEstimateModel.StatusText(c.Status);
            return "";
        }

        /// <summary>T-168: sort key of a hits / dmg cell (hits by count, dmg by p); NaN = last.</summary>
        public static double HitSortKey(HeCell c, string col)
        {
            if (c == null) return double.NaN;
            if (col == "hits") return c.Hits;
            if (col == "dmg") return c.Status == HeStatus.None ? double.NaN : c.P;
            return double.NaN;
        }

        /// <summary>T-168: colour of a cell (ARGB) or null for the default colour: only dmg has one (the status colour).</summary>
        public static uint? CellArgb(string col, HeCell c)
        {
            if (col != "dmg" || c == null || c.Status == HeStatus.None) return null;
            return HitEstimateModel.Argb(c.Status);
        }

        public static string[] ParseColumns(string spec, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(spec)) { error = "columns: empty"; return null; }
            var cols = spec.Split(',').Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).ToArray();
            foreach (var c in cols) if (Array.IndexOf(All, c) < 0) { error = "columns: unknown '" + c + "'"; return null; }
            if (Array.IndexOf(cols, "icon") < 0 || Array.IndexOf(cols, "type") < 0) { error = "columns: icon and type are required"; return null; }
            if (cols.Distinct().Count() != cols.Length) { error = "columns: duplicate"; return null; }
            return cols;
        }

        public static bool ParseSort(string spec, out string column, out bool asc)
        {
            column = "dist"; asc = true;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            var p = spec.Trim().ToLowerInvariant().Split(':');
            if (p.Length > 2 || Array.IndexOf(All, p[0]) < 0) return false;
            if (p.Length == 2) { if (p[1] != "asc" && p[1] != "desc") return false; asc = p[1] == "asc"; }
            column = p[0];
            return true;
        }

        public static OvCiCells Cells(ContactInfoRow r, string lang)
        {
            var c = new OvCiCells();
            if (r == null) return c;
            // depth
            if (r.LayerSide == "nosol") c.Dep = UiText.Get("overview.ci.nosol");
            else if (double.IsNaN(r.DepthFt)) c.Dep = r.LayerSide == null ? "" : Dash;
            else c.Dep = "~" + OverviewMath.FmtFt(r.DepthFt, lang);
            c.SortDep = r.DepthFt;
            // layer
            switch (r.LayerSide)
            {
                case "above":
                case "below":
                    if (double.IsNaN(r.LayerOffsetFt)) break;
                    double a = Math.Abs(r.LayerOffsetFt);
                    bool above = r.LayerSide == "above";
                    c.Lyr = (above ? "+" : "-") + OverviewMath.FmtFt(a, lang);
                    c.SortLyr = above ? a : -a;
                    break;
                case "none": c.Lyr = UiText.Get("overview.ci.lyr.none"); break;
                case null: c.Lyr = ""; break;
            }
            if (!double.IsNaN(r.CpaNm)) { c.Cpa = OverviewMath.FmtNm(r.CpaNm, lang) + " " + UiText.Get("ci.nm"); c.SortCpa = r.CpaNm; }
            if (!double.IsNaN(r.TcpaS)) { c.Tcpa = ContactInfoMath.FmtClock(r.TcpaS); c.SortTcpa = r.TcpaS; }
            if (!double.IsNaN(r.TtgS)) { c.Ttg = (r.TtgExact ? "" : "~") + ContactInfoMath.FmtClock(r.TtgS); c.SortTtg = r.TtgS; }
            return c;
        }

        public static string CellText(OvCiCells c, string col)
        {
            if (!IsExtra(col)) return "";
            if (c == null) return Dash;
            switch (col)
            {
                case "dep": return c.Dep;
                case "lyr": return c.Lyr;
                case "cpa": return c.Cpa;
                case "tcpa": return c.Tcpa;
                default: return c.Ttg;
            }
        }

        public static double SortKey(OvCiCells c, string col)
        {
            if (c == null) return double.NaN;
            switch (col)
            {
                case "dep": return c.SortDep;
                case "lyr": return c.SortLyr;
                case "cpa": return c.SortCpa;
                case "tcpa": return c.SortTcpa;
                case "ttg": return c.SortTtg;
            }
            return double.NaN;
        }

        public static string HeadKey(string col) { return IsExtra(col) || IsHit(col) ? "overview.col." + col : null; }

        public static double BaseWidth(string col)
        {
            switch (col)
            {
                case "dep": return 64;
                case "lyr": return 60;
                case "cpa": return 58;
                case "tcpa": return 50;
                case "ttg": return 54;
                case "hits": return 44;
                case "dmg": return 110;
            }
            return double.NaN;
        }

        public static void Sort(List<OvRow> rows, string column, bool asc, IDictionary<int, OvCiCells> cells, IDictionary<int, HeCell> hits = null)
        {
            if (!IsExtra(column) && !IsHit(column)) { OverviewMath.Sort(rows, column, asc); return; }
            Func<OvRow, double> key = r =>
            {
                if (IsHit(column))
                {
                    HeCell h;
                    return hits != null && hits.TryGetValue(r.Id, out h) ? HitSortKey(h, column) : double.NaN;
                }
                OvCiCells c;
                return cells != null && cells.TryGetValue(r.Id, out c) ? SortKey(c, column) : double.NaN;
            };
            Comparison<OvRow> cmp = (a, b) =>
            {
                double x = key(a), y = key(b);
                bool nx = double.IsNaN(x), ny = double.IsNaN(y);
                if (nx || ny) return nx && ny ? a.Id.CompareTo(b.Id) : nx ? 1 : -1;   // NaN last in both directions
                int c = x.CompareTo(y);
                if (!asc) c = -c;
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            };
            var sorted = rows.OrderBy(r => r, Comparer<OvRow>.Create(cmp)).ToList();
            rows.Clear(); rows.AddRange(sorted);
        }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            string e, col; bool asc;

            chk(All.Length == 19 && All.Distinct().Count() == 19 && All.Take(12).SequenceEqual(OverviewMath.AllColumns), "All");
            var d = ParseColumns(DefaultSpec, out e);
            chk(d != null && d.Length == 13 && string.Join(",", d) == DefaultSpec && DefaultSpec.EndsWith(",hits,dmg"), "default spec");
            chk(ParseColumns("icon,type,dep,cpa,age", out e) != null, "ok spec");
            var t = ParseColumns(" ICON , Type ,TTG", out e);
            chk(t != null && t.Length == 3 && t[2] == "ttg", "trim lower");
            ParseColumns("icon,type,foo", out e); chk(e == "columns: unknown 'foo'", "err unknown");
            ParseColumns("type,dep", out e); chk(e == "columns: icon and type are required", "err required");
            ParseColumns("icon,type,cpa,cpa", out e); chk(e == "columns: duplicate", "err dup");
            ParseColumns("", out e); chk(e == "columns: empty", "err empty");
            foreach (string s in new[] { "icon,type,dist", "icon,type,foo", "type,dist", "icon,type,dist,dist", "" })
            {
                string e1, e2;
                var a1 = ParseColumns(s, out e1); var a2 = OverviewMath.ParseColumns(s, out e2);
                chk(e1 == e2 && ((a1 == null) == (a2 == null)) && (a1 == null || a1.SequenceEqual(a2)), "same as Common: " + s);
            }
            chk(ParseSort("tcpa:asc", out col, out asc) && col == "tcpa" && asc, "sort tcpa");
            chk(ParseSort("ttg:desc", out col, out asc) && col == "ttg" && !asc, "sort ttg");
            chk(ParseSort("cpa", out col, out asc) && col == "cpa" && asc, "sort cpa");
            chk(!ParseSort("dist:up", out col, out asc) && col == "dist" && asc, "sort bad dir");
            chk(!ParseSort("foo", out col, out asc) && col == "dist", "sort bad col");
            chk(!ParseSort("", out col, out asc) && col == "dist", "sort empty");

            // T-168: hits / dmg
            chk(ParseColumns("icon,type,hits,dmg", out e) != null && IsHit("hits") && IsHit("dmg") && !IsHit("dist") && !IsExtra("hits"), "hit columns");
            chk(ParseSort("dmg:desc", out col, out asc) && col == "dmg" && !asc && ParseSort("hits", out col, out asc) && col == "hits", "sort hits dmg");
            chk(HeadKey("hits") == "overview.col.hits" && HeadKey("dmg") == "overview.col.dmg" && BaseWidth("hits") == 44 && BaseWidth("dmg") == 110, "hit heads and widths");
            var heavy = new HeCell { Hits = 2, Status = HeStatus.Heavy, P = 0.8 };
            var noClass = new HeCell { Hits = 1, Status = HeStatus.None, P = double.NaN };
            chk(CellArgb("dmg", heavy) == 0xFFFF2020 && CellArgb("dmg", noClass) == null && CellArgb("dmg", null) == null && CellArgb("hits", heavy) == null && CellArgb("dist", heavy) == null, "dmg colour only");
            chk(HitCellText(null, "hits") == Dash && HitCellText(null, "dmg") == Dash && HitCellText(heavy, "hits") == "2" && HitCellText(noClass, "dmg") == Dash, "hit cell dashes");
            if (UiText.Has("SeaPowerTacticalUI", "he.status_heavy") && UiText.Has("SeaPowerTacticalUI", "overview.col.hits"))
            {
                chk(HitCellText(heavy, "dmg") == UiText.Get("he.status_heavy"), "dmg text");
                foreach (string x in HitCols) chk(UiText.Has("SeaPowerTacticalUI", HeadKey(x)), "head " + x);
            }
            chk(HitSortKey(heavy, "hits") == 2 && HitSortKey(heavy, "dmg") == 0.8 && double.IsNaN(HitSortKey(null, "dmg")) && double.IsNaN(HitSortKey(noClass, "dmg")) && HitSortKey(noClass, "hits") == 1, "hit sort keys");
            var hrows = new List<OvRow> { new OvRow { Id = 1 }, new OvRow { Id = 2 }, new OvRow { Id = 3 }, new OvRow { Id = 4 } };
            var hcells = new Dictionary<int, HeCell> { { 1, new HeCell { Hits = 1, Status = HeStatus.Light, P = 0.2 } }, { 3, heavy }, { 4, noClass } };
            Sort(hrows, "dmg", true, null, hcells);
            chk(string.Join(",", hrows.Select(r => r.Id)) == "1,3,2,4", "dmg ascending, no status last");
            Sort(hrows, "dmg", false, null, hcells);
            chk(string.Join(",", hrows.Select(r => r.Id)) == "3,1,2,4", "dmg descending, no status last");
            Sort(hrows, "hits", false, null, hcells);
            chk(string.Join(",", hrows.Select(r => r.Id)) == "3,1,4,2", "hits descending, missing last");

            // texts (need the language tables; skipped when absent: selfcheck without lang)
            if (UiText.Has("SeaPowerTacticalUI", "overview.ci.nosol") && UiText.Has("SeaPowerTacticalUI", "ci.nm"))
            {
                var n = Cells(null, "ru");
                chk(n.Dep == Dash && n.Lyr == Dash && n.Cpa == Dash && n.Tcpa == Dash && n.Ttg == Dash && double.IsNaN(n.SortCpa) && double.IsNaN(n.SortDep), "cells null");
                var sub = Cells(new ContactInfoRow { DepthFt = 340, DepthErrFt = 60, LayerSide = "below", LayerOffsetFt = -80 }, "ru");
                chk(sub.Dep == "~340 фт" && sub.Lyr == "-80 фт" && sub.SortLyr == -80 && sub.SortDep == 340, "cells below");
                chk(Cells(new ContactInfoRow { DepthFt = 100, LayerSide = "above", LayerOffsetFt = 120 }, "ru").Lyr == "+120 фт", "cells above");
                chk(Cells(new ContactInfoRow { DepthFt = 100, LayerSide = "none" }, "ru").Lyr == UiText.Get("overview.ci.lyr.none"), "cells none");
                var ns = Cells(new ContactInfoRow { LayerSide = "nosol" }, "ru");
                chk(ns.Dep == UiText.Get("overview.ci.nosol") && ns.Lyr == Dash, "cells nosol");
                var sf = Cells(new ContactInfoRow(), "ru");
                chk(sf.Dep == "" && sf.Lyr == "", "cells surface");
                var cp = Cells(new ContactInfoRow { CpaNm = 1.24, TcpaS = 185 }, "ru");
                chk(cp.Cpa == "1,2 " + UiText.Get("ci.nm") && cp.Tcpa == "3:05" && cp.Ttg == Dash, "cells cpa");
                chk(sf.Cpa == Dash && sf.Tcpa == Dash, "cells cpa nan");
                chk(Cells(new ContactInfoRow { TtgS = 65 }, "ru").Ttg == "~1:05" && Cells(new ContactInfoRow { TtgS = 65, TtgExact = true }, "ru").Ttg == "1:05", "cells ttg");
                chk(CellText(null, "dep") == Dash && CellText(cp, "dist") == "" && CellText(cp, "cpa") == cp.Cpa, "CellText");
                foreach (string x in Extra) chk(HeadKey(x) != null && UiText.Has("SeaPowerTacticalUI", HeadKey(x)), "head " + x);
            }
            chk(HeadKey("dep") == "overview.col.dep" && HeadKey("dist") == null, "HeadKey");
            foreach (string x in Extra) chk(BaseWidth(x) > 0, "width " + x);
            chk(double.IsNaN(BaseWidth("dist")), "width dist");

            // sort
            var cells = new Dictionary<int, OvCiCells>
            {
                { 1, new OvCiCells { SortCpa = 5 } }, { 2, new OvCiCells() }, { 3, new OvCiCells { SortCpa = 1 } }, { 4, new OvCiCells { SortCpa = 5 } },
            };
            var rows = new List<OvRow> { new OvRow { Id = 1 }, new OvRow { Id = 2 }, new OvRow { Id = 3 }, new OvRow { Id = 4 } };
            Sort(rows, "cpa", true, cells);
            chk(string.Join(",", rows.Select(r => r.Id)) == "3,1,4,2", "sort asc");
            Sort(rows, "cpa", false, cells);
            chk(string.Join(",", rows.Select(r => r.Id)) == "1,4,3,2", "sort desc");
            cells.Remove(3);
            Sort(rows, "cpa", true, cells);
            chk(string.Join(",", rows.Select(r => r.Id)) == "1,4,2,3", "sort missing cell");
            var r1 = new List<OvRow> { new OvRow { Id = 2, SortDist = 5 }, new OvRow { Id = 1, SortDist = 9 }, new OvRow { Id = 3, SortDist = 1 } };
            var r2 = new List<OvRow>(r1);
            Sort(r1, "dist", true, cells); OverviewMath.Sort(r2, "dist", true);
            chk(r1.Select(r => r.Id).SequenceEqual(r2.Select(r => r.Id)), "sort common");
            return err;
        }
    }
}
