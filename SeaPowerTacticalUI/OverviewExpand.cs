using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-168 / ADR-0064 item 24: the expanded rows of the Overview (track ids, session only) and the pure layout of rows of different height. No Unity.</summary>
    internal sealed class OverviewExpand
    {
        private readonly HashSet<int> _open = new HashSet<int>();

        public int Version { get; private set; }

        public bool IsOpen(int id) { return _open.Contains(id); }

        /// <summary>Flips the row; returns the new state.</summary>
        public bool Toggle(int id)
        {
            Version++;
            if (_open.Remove(id)) return false;
            _open.Add(id);
            return true;
        }

        /// <summary>Closes the rows for which keep(id) is false; returns how many.</summary>
        public int Prune(Func<int, bool> keep)
        {
            var gone = new List<int>();
            foreach (var id in _open) if (!keep(id)) gone.Add(id);
            foreach (var id in gone) _open.Remove(id);
            if (gone.Count > 0) Version++;
            return gone.Count;
        }

        public void Clear()
        {
            if (_open.Count == 0) return;
            _open.Clear();
            Version++;
        }

        public int Count { get { return _open.Count; } }

        /// <summary>Top of every row; row i is rowH high plus extraLines(id) * lineH when expanded. total = the height of the whole list.</summary>
        public static float[] RowTops(IList<int> ids, Func<int, int> extraLines, float rowH, float lineH, out float total)
        {
            var tops = new float[ids.Count];
            float y = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                tops[i] = y;
                int k = extraLines == null ? 0 : Math.Max(0, extraLines(ids[i]));
                y += rowH + k * lineH;
            }
            total = y;
            return tops;
        }

        /// <summary>Index of the row at content y (the whole expanded block belongs to its row); -1 = below the list or above the first row.</summary>
        public static int RowAt(float[] tops, float total, float y)
        {
            if (tops == null || tops.Length == 0 || y < 0 || y >= total) return -1;
            for (int i = tops.Length - 1; i >= 0; i--) if (y >= tops[i]) return i;
            return -1;
        }

        /// <summary>Live finding 35: the weapon selector "◂ name ▸" has fixed places. ◂ at x0, a fixed name field, ▸ right after it, whatever the name.
        /// Returns {leftX, textX, textW, rightX}; every rect is aw (arrows) or textW wide.</summary>
        public static float[] PickLayout(float x0, float aw, float textW)
        {
            return new[] { x0, x0 + aw, textW, x0 + aw + textW };
        }

        /// <summary>Cuts s to fit maxW by measure, with "…" at the end; s itself when it fits. Binary search on the prefix length (measure is called O(log n) times).</summary>
        public static string Ellipsize(string s, float maxW, Func<string, float> measure)
        {
            if (string.IsNullOrEmpty(s) || measure == null || measure(s) <= maxW) return s;
            int lo = 0, hi = s.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (measure(s.Substring(0, mid).TrimEnd() + "\u2026") <= maxW) lo = mid; else hi = mid - 1;
            }
            return s.Substring(0, lo).TrimEnd() + "\u2026";
        }

        /// <summary>T-178 O1: the choices of the hit estimate can only be reached by cycling (Cycle(+1)). One full turn reads every name (the current one first) and leaves the selection where it was.
        /// readPick = the text of the current choice; cycle(1) = to the next one.</summary>
        public static string[] CollectPicks(int choices, int pickIndex, Func<string> readPick, Action<int> cycle)
        {
            var names = new string[Math.Max(0, choices)];
            if (names.Length == 0) return names;
            int start = ((pickIndex % names.Length) + names.Length) % names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                names[(start + i) % names.Length] = readPick() ?? "";
                cycle(1);
            }
            return names;
        }

        /// <summary>How many Cycle(+1) steps lead from the current choice to the picked one (0..n-1; the choices wrap).</summary>
        public static int StepsTo(int current, int target, int n)
        {
            if (n <= 0) return 0;
            return (((target - current) % n) + n) % n;
        }

        /// <summary>Columns of the weapon list: as many as needed so that it is not taller than maxRows rows (at most 3).</summary>
        public static int PickColumns(int count, int maxRows)
        {
            if (count <= 0) return 1;
            return Math.Max(1, Math.Min(3, (count + Math.Max(1, maxRows) - 1) / Math.Max(1, maxRows)));
        }

        // T-183: the window frame

        /// <summary>"x,y,w,h" (fractions of the screen) or null = do not write the rect (not initialised, empty rect or screen).</summary>
        public static string RectSpec(bool init, double x, double y, double w, double h, double screenW, double screenH)
        {
            if (!init || w <= 0 || h <= 0 || screenW <= 0 || screenH <= 0) return null;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return (x / screenW).ToString("0.000", ic) + "," + (y / screenH).ToString("0.000", ic) + ","
                + (w / screenW).ToString("0.000", ic) + "," + (h / screenH).ToString("0.000", ic);
        }

        /// <summary>The size corner {x, y, w, h} in window coordinates: the bottom-right square of side grip.</summary>
        public static float[] GripRect(float w, float h, float grip)
        {
            return new[] { w - grip, h - grip, grip, grip };
        }

        public static bool InRect(float[] r, float x, float y)
        {
            return r != null && x >= r[0] && x < r[0] + r[2] && y >= r[1] && y < r[1] + r[3];
        }

        /// <summary>GUI.BeginScrollView rules (alwaysShow = false): {needH 0|1, needV 0|1, clipW, clipH}.</summary>
        public static float[] ScrollBars(float contentW, float contentH, float viewW, float viewH, float vbarW, float hbarH)
        {
            float clipW = viewW, clipH = viewH;
            bool needH = contentW > viewW;
            if (needH) clipH -= hbarH;
            bool needV = contentH > clipH;
            if (needV) clipW -= vbarW;
            if (!needH && contentW > clipW) { needH = true; clipH -= hbarH; }
            return new[] { needH ? 1f : 0f, needV ? 1f : 0f, clipW, clipH };
        }

        /// <summary>T-205: the space a scroll bar takes in GUI.BeginScrollView = fixed size (16 when the skin has none) plus the margin on the list side.</summary>
        public static float BarThickness(float fixedSize, float margin)
        {
            return (fixedSize > 0 ? fixedSize : 16f) + Math.Max(0f, margin);
        }

        /// <summary>T-191: FitRows height plus the horizontal bar when the content is wider than the window (the bar then takes part of the list).</summary>
        public static double FitWithBar(double fitH, double contentW, double viewW, double hbarH, double scale = 1)
        {
            // T-205: the bar plus a small slack (the bar's frame/border), scaled with the UI; otherwise the last row sits half under the bar
            return contentW > viewW ? fitH + hbarH + 2 * scale : fitH;
        }

        /// <summary>A row may take the mouse: window point inside the visible list and not on the grip.</summary>
        public static bool RowHitAllowed(float winX, float winY, float viewTop, float clipW, float clipH, float[] grip)
        {
            if (InRect(grip, winX, winY)) return false;
            return winX >= 0 && winX < clipW && winY >= viewTop && winY < viewTop + clipH;
        }

        /// <summary>Header cell of column i in group coordinates: {colX - scrollX, 0, colW, h}.</summary>
        public static float[] HeaderRect(double colX, double colW, double scrollX, float h)
        {
            return new[] { (float)(colX - scrollX), 0f, (float)colW, h };
        }

        /// <summary>Column under window x (header row only; the caller checks y): -1 = outside [0, clipW) or no column.</summary>
        public static int HeaderAt(IList<double> colX, IList<double> colW, double scrollX, float clipW, float x)
        {
            if (x < 0 || x >= clipW || colX == null || colW == null) return -1;
            for (int i = 0; i < colX.Count && i < colW.Count; i++)
            {
                double l = colX[i] - scrollX;
                if (x >= l && x < l + colW[i]) return i;
            }
            return -1;
        }

        public static string StripSortArrow(string head)
        {
            if (head == null) return "";
            head = head.Trim();
            if (head.EndsWith("\u25B2") || head.EndsWith("\u25BC")) head = head.Substring(0, head.Length - 1).TrimEnd();
            return head;
        }

        public static string ColTip(string fullName, string head, string action)
        {
            string name = string.IsNullOrEmpty(fullName) ? StripSortArrow(head) : fullName;
            return string.IsNullOrEmpty(action) ? name : name + "\n" + action;
        }

        public static string ResizeTipKey(bool fitRows, double rectH, double fitH)
        {
            return fitRows && rectH >= fitH - 0.5 ? "overview.tip.resize_fit" : "overview.tip.resize";
        }

        /// <summary>One journal line: "Type: message @ first 'at ' frame".</summary>
        public static string ErrLine(string type, string message, string stack)
        {
            string line = type + ": " + message;
            if (!string.IsNullOrEmpty(stack))
            {
                foreach (var raw in stack.Split('\n'))
                {
                    string t = raw.Trim();
                    if (t.StartsWith("at ")) return line + " @ " + t;
                }
            }
            return line;
        }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            var e = new OverviewExpand();
            chk(!e.IsOpen(5) && e.Count == 0, "closed");
            chk(e.Toggle(5) && e.IsOpen(5) && !e.Toggle(5) && !e.IsOpen(5) && e.Version == 2, "toggle");
            e.Toggle(1); e.Toggle(2); e.Toggle(3);
            chk(e.Prune(id => id != 2) == 1 && e.IsOpen(1) && !e.IsOpen(2) && e.IsOpen(3), "prune");
            int v = e.Version;
            chk(e.Prune(id => true) == 0 && e.Version == v, "prune nothing");
            e.Clear();
            chk(e.Count == 0 && !e.IsOpen(1) && e.Version == v + 1, "clear");
            float total;
            var ids = new List<int> { 10, 20, 30, 40 };
            var tops = RowTops(ids, id => id == 20 ? 5 : 0, 18f, 15f, out total);
            chk(tops[0] == 0 && tops[1] == 18 && tops[2] == 2 * 18 + 5 * 15 && tops[3] == 3 * 18 + 5 * 15, "tops: 3rd row after the expanded 2nd");
            chk(total == 4 * 18 + 5 * 15, "total is the sum");
            RowTops(new List<int>(), null, 18f, 15f, out total);
            chk(total == 0, "empty total");
            tops = RowTops(ids, id => id == 20 ? 5 : 0, 18f, 15f, out total);
            chk(RowAt(tops, total, 5) == 0 && RowAt(tops, total, 19) == 1 && RowAt(tops, total, 18 + 18 + 40) == 1 && RowAt(tops, total, 18 + 18 + 5 * 15) == 2
                && RowAt(tops, total, total) == -1 && RowAt(tops, total, -1) == -1, "row at y");

            // live finding 35: the arrows do not move with the name
            var a = PickLayout(30f, 16f, 180f);
            var b = PickLayout(30f, 16f, 180f);
            chk(a[0] == 30f && a[1] == 46f && a[2] == 180f && a[3] == 226f && a[3] == b[3], "pick layout fixed");
            Func<string, float> m = x => x.Length * 7f;   // 7 px per char
            chk(Ellipsize("Mk-48 ADCAP", 180f, m) == "Mk-48 ADCAP", "short name kept");
            string longName = "AGM-84D Harpoon Block 1C extended range";
            string cut = Ellipsize(longName, 180f, m);
            chk(cut.EndsWith("\u2026") && m(cut) <= 180f && m(cut) > 180f - 2 * 7f && longName.StartsWith(cut.Substring(0, cut.Length - 1)), "long name ellipsized: " + cut);
            chk(Ellipsize("ABCDEF", 3f, m) == "\u2026", "nothing fits: just the ellipsis");
            chk(Ellipsize(null, 10f, m) == null && Ellipsize("", 10f, m) == "", "null and empty");
            chk(Ellipsize("AB  CDEFGHIJ", 4 * 7f, m) == "AB\u2026", "no space before the ellipsis");

            // T-178 O1: the list of weapons is read by one turn of the cycle; picking an index is a number of steps forward
            int pos = 2; var all = new[] { "Mk-48", "Mk-46", "Harpoon", "ASROC", "Mk-54" };
            int reads = 0, steps = 0;
            var got = CollectPicks(all.Length, 2, () => { reads++; return all[pos]; }, d => { steps++; pos = (pos + d + all.Length) % all.Length; });
            chk(string.Join("|", got) == "Mk-48|Mk-46|Harpoon|ASROC|Mk-54" && reads == 5 && steps == 5 && pos == 2, "collect picks: the order of the choices, back at the start: " + string.Join("|", got) + " pos " + pos);
            chk(CollectPicks(0, 0, () => "x", d => { }).Length == 0 && CollectPicks(1, 0, () => "a", d => { })[0] == "a", "collect picks: nothing / one");
            chk(StepsTo(2, 4, 5) == 2 && StepsTo(4, 1, 5) == 2 && StepsTo(3, 3, 5) == 0 && StepsTo(0, 4, 5) == 4 && StepsTo(0, 0, 0) == 0, "steps to the picked weapon");
            chk(PickColumns(5, 8) == 1 && PickColumns(9, 8) == 2 && PickColumns(30, 8) == 3 && PickColumns(0, 8) == 1 && PickColumns(4, 0) == 3, "columns of the weapon list");
            // the list itself: UiDropdownModel in the window (the weapon button at the left, the list under it, or above when the window ends)
            var dd = new UiDropdownModel();
            dd.Open("7", new UiRect(30, 40, 212, 16), 5, 1, 212, 16, 200, 400);
            chk(dd.IsOpen("7") && dd.HitItem(40, 60) == 0 && dd.HitItem(40, 56 + 16 * 4 + 3) == 4 && dd.HitItem(40, 30) == -1, "weapon list: items under the button");
            dd.Open("7", new UiRect(30, 150, 212, 16), 5, 1, 212, 16, 200, 400);
            chk(dd.Above && dd.HitItem(40, 150 - 5 * 16 + 2) == 0, "weapon list: above when the window ends");

            // T-183: the window frame
            chk(RectSpec(false, 10, 10, 300, 200, 1920, 1080) == null && RectSpec(true, 0, 0, 0, 0, 1920, 1080) == null && RectSpec(true, 10, 10, 300, 200, 0, 0) == null, "rect spec: skipped");
            chk(RectSpec(true, 1344, 86.4, 537.6, 432, 1920, 1080) == "0.700,0.080,0.280,0.400", "rect spec: text " + RectSpec(true, 1344, 86.4, 537.6, 432, 1920, 1080));
            double px, py, pw, ph;
            chk(OverviewMath.ParseRect("0.700,0.080,0.280,0.400", out px, out py, out pw, out ph), "rect spec parses back");
            var g = GripRect(400, 300, 16);
            chk(g[0] == 384 && g[1] == 284 && g[2] == 16 && g[3] == 16 && InRect(g, 384, 284) && !InRect(g, 400, 290) && !InRect(g, 383, 290), "grip rect");
            Func<float[], string> sbs = r => r[0] + "," + r[1] + "," + r[2] + "," + r[3];
            chk(sbs(ScrollBars(300, 100, 400, 200, 15, 15)) == "0,0,400,200", "bars: none");
            chk(sbs(ScrollBars(600, 100, 400, 200, 15, 15)) == "1,0,400,185", "bars: horizontal");
            chk(sbs(ScrollBars(300, 500, 400, 200, 15, 15)) == "0,1,385,200", "bars: vertical");
            chk(sbs(ScrollBars(390, 500, 400, 200, 15, 15)) == "1,1,385,185", "bars: vertical calls horizontal");
            chk(sbs(ScrollBars(600, 190, 400, 200, 15, 15)) == "1,1,385,185", "bars: horizontal calls vertical");
            chk(FitWithBar(200, 300, 400, 16) == 200 && FitWithBar(200, 500, 400, 16) == 218 && FitWithBar(200, 500, 400, 16, 2) == 220, "fit with bar");
            {
                double fit9 = OverviewMath.FitHeight(9, 1), withBar = FitWithBar(fit9, 500, 400, 16);
                var sb9 = ScrollBars(500, 9 * 18, 400, (float)(withBar - 20 - 18), 15, 16);
                chk(sb9[0] == 1 && sb9[1] == 0 && sb9[3] >= 9 * 18, "fit rows with bar: no vertical bar, " + sbs(sb9));
                // T-205: the visible list height (window - header - column heads - bar) holds every row at scale 1 and 1.5
                foreach (double sc in new[] { 1.0, 1.5 })
                {
                    double wb = FitWithBar(OverviewMath.FitHeight(9, sc), 500, 400, 16, sc);
                    double visible = wb - (OverviewMath.HeaderH + OverviewMath.ColHeadH) * sc - 16;
                    chk(visible >= 9 * OverviewMath.RowH * sc, "fit rows with bar: last row visible at scale " + sc);
                }
            }
            chk(BarThickness(0, 0) == 16 && BarThickness(20, 4) == 24 && BarThickness(16, -3) == 16, "bar thickness includes margin");
            chk(!RowHitAllowed(390, 290, 20, 385, 185, new[] { 384f, 284f, 16f, 16f }) && !RowHitAllowed(390, 100, 20, 385, 185, null)
                && !RowHitAllowed(100, 205, 20, 385, 185, null) && RowHitAllowed(100, 100, 20, 385, 185, null) && !RowHitAllowed(100, 10, 20, 385, 185, null), "row hit allowed");
            var hx = new List<double> { 0, 32, 142 }; var hw = new List<double> { 32, 110, 72 };
            var hr = HeaderRect(142, 72, 50, 18);
            chk(hr[0] == 92 && hr[1] == 0 && hr[2] == 72 && hr[3] == 18, "header rect shifted");
            chk(HeaderAt(hx, hw, 0, 400, 150) == 2 && HeaderAt(hx, hw, 50, 400, 100) == 2 && HeaderAt(hx, hw, 50, 400, 80) == 1
                && HeaderAt(hx, hw, 0, 400, 400) == -1 && HeaderAt(hx, hw, 0, 400, -1) == -1 && HeaderAt(hx, hw, 0, 400, 300) == -1, "header at");
            chk(StripSortArrow("V\u0441\u0431\u043B \u25B2") == "V\u0441\u0431\u043B" && StripSortArrow("D \u25BC") == "D" && StripSortArrow("Type") == "Type", "strip sort arrow");
            chk(ColTip("Full", "V \u25B2", "Click") == "Full\nClick" && ColTip(null, "TTG \u25BC", "x") == "TTG\nx" && ColTip("A", "a", null) == "A", "col tip");
            chk(ResizeTipKey(true, 300, 300) == "overview.tip.resize_fit" && ResizeTipKey(true, 500, 300) == "overview.tip.resize_fit" && ResizeTipKey(true, 200, 300) == "overview.tip.resize" && ResizeTipKey(false, 300, 300) == "overview.tip.resize", "resize tip key");
            chk(ErrLine("NullReferenceException", "x", "  at A.B () [0x0]\n  at C.D ()") == "NullReferenceException: x @ at A.B () [0x0]" && ErrLine("E", "m", null) == "E: m", "err line");
            return err;
        }
    }
}
