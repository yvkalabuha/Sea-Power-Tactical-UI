// T-178 Z1 (ADR-0077 §4): the pure maths of editing a zone on the map by its handles. Hit: which of the 8 handles (or the inside) is under the cursor (screen pixels, y down).
// Drag: the new rectangle in local nautical miles (y UP = north) after a drag of a handle: snapped to 1 mile, at least 5 x 5, at most `maxNm` on a side;
// DragCircle: the SAR circle (centre, radius sticking to the list of radii). A new zone from the drawing keeps working through UiMapDrawInput.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal enum ZoneHandle { None, N, NE, E, SE, S, SW, W, NW, Inside, Center, Radius }

    internal static class ZoneEditMath
    {
        public const double SnapNm = 1, MinNm = 5;

        /// <summary>T-352: nm per (60 * deg) of longitude for zone editing: cos(lat) on the beta globe, exactly 1.0 on the flat stable world.</summary>
        public static double LonK(double lat, bool globe) { return globe ? Math.Cos(lat * Math.PI / 180.0) : 1.0; }

        private static bool Near(float mx, float my, float x, float y, float h) { return Math.Abs(mx - x) <= h / 2f && Math.Abs(my - y) <= h / 2f; }

        /// <summary>rectPx = {x0, y0, x1, y1} on the screen (any corner order). The handles are squares of handlePx on the 4 corners and the 4 side middles (corners win), then the inside.</summary>
        public static ZoneHandle Hit(float[] rectPx, float mx, float my, float handlePx)
        {
            if (rectPx == null || rectPx.Length < 4) return ZoneHandle.None;
            float x0 = Math.Min(rectPx[0], rectPx[2]), x1 = Math.Max(rectPx[0], rectPx[2]), y0 = Math.Min(rectPx[1], rectPx[3]), y1 = Math.Max(rectPx[1], rectPx[3]);
            float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f;
            if (Near(mx, my, x0, y0, handlePx)) return ZoneHandle.NW;
            if (Near(mx, my, x1, y0, handlePx)) return ZoneHandle.NE;
            if (Near(mx, my, x1, y1, handlePx)) return ZoneHandle.SE;
            if (Near(mx, my, x0, y1, handlePx)) return ZoneHandle.SW;
            if (Near(mx, my, cx, y0, handlePx)) return ZoneHandle.N;
            if (Near(mx, my, cx, y1, handlePx)) return ZoneHandle.S;
            if (Near(mx, my, x1, cy, handlePx)) return ZoneHandle.E;
            if (Near(mx, my, x0, cy, handlePx)) return ZoneHandle.W;
            if (mx > x0 && mx < x1 && my > y0 && my < y1) return ZoneHandle.Inside;
            return ZoneHandle.None;
        }

        private static double Snap(double v) { return Math.Round(v / SnapNm) * SnapNm; }

        /// <summary>The moving edge of a side handle: snapped, with the size between MinNm and maxNm measured from the fixed edge.</summary>
        private static double Edge(double moving, double fixedEdge, bool movingIsMax, double maxNm)
        {
            double v = Snap(moving);
            double lo = movingIsMax ? fixedEdge + MinNm : fixedEdge - maxNm, hi = movingIsMax ? fixedEdge + maxNm : fixedEdge - MinNm;
            return Math.Max(lo, Math.Min(hi, v));
        }

        /// <summary>rectNm = {xMin, yMin, xMax, yMax} (y up). The result is a new array; handle None / Center returns the input.</summary>
        public static double[] Drag(double[] rectNm, ZoneHandle h, double dxNm, double dyNm, double maxNm)
        {
            if (rectNm == null || rectNm.Length < 4) return rectNm;
            double x0 = Math.Min(rectNm[0], rectNm[2]), x1 = Math.Max(rectNm[0], rectNm[2]), y0 = Math.Min(rectNm[1], rectNm[3]), y1 = Math.Max(rectNm[1], rectNm[3]);
            if (!(maxNm >= MinNm)) maxNm = MinNm;
            switch (h)
            {
                case ZoneHandle.Inside:
                {
                    double nx = Snap(x0 + dxNm), ny = Snap(y0 + dyNm);
                    return new[] { nx, ny, nx + (x1 - x0), ny + (y1 - y0) };
                }
                case ZoneHandle.N: y1 = Edge(y1 + dyNm, y0, true, maxNm); break;
                case ZoneHandle.S: y0 = Edge(y0 + dyNm, y1, false, maxNm); break;
                case ZoneHandle.E: x1 = Edge(x1 + dxNm, x0, true, maxNm); break;
                case ZoneHandle.W: x0 = Edge(x0 + dxNm, x1, false, maxNm); break;
                case ZoneHandle.NE: x1 = Edge(x1 + dxNm, x0, true, maxNm); y1 = Edge(y1 + dyNm, y0, true, maxNm); break;
                case ZoneHandle.NW: x0 = Edge(x0 + dxNm, x1, false, maxNm); y1 = Edge(y1 + dyNm, y0, true, maxNm); break;
                case ZoneHandle.SE: x1 = Edge(x1 + dxNm, x0, true, maxNm); y0 = Edge(y0 + dyNm, y1, false, maxNm); break;
                case ZoneHandle.SW: x0 = Edge(x0 + dxNm, x1, false, maxNm); y0 = Edge(y0 + dyNm, y1, false, maxNm); break;
                default: return rectNm;
            }
            return new[] { x0, y0, x1, y1 };
        }

        /// <summary>cxyRNm = {cx, cy, r}. Center: the centre moves by (dx, dy) snapped to 1 mile; Radius: dxNm is the change of the radius, the result is the nearest value of `radii`
        /// (without a list: snapped to 1 mile, at least 1).</summary>
        public static double[] DragCircle(double[] cxyRNm, ZoneHandle h, double dxNm, double dyNm, IList<double> radii)
        {
            if (cxyRNm == null || cxyRNm.Length < 3) return cxyRNm;
            if (h == ZoneHandle.Center || h == ZoneHandle.Inside) return new[] { Snap(cxyRNm[0] + dxNm), Snap(cxyRNm[1] + dyNm), cxyRNm[2] };
            if (h != ZoneHandle.Radius) return cxyRNm;
            double want = cxyRNm[2] + dxNm, best = Math.Max(1, Snap(want));
            if (radii != null && radii.Count > 0)
            {
                best = radii[0];
                foreach (double r in radii) if (Math.Abs(r - want) < Math.Abs(best - want)) best = r;
            }
            return new[] { cxyRNm[0], cxyRNm[1], best };
        }

        /// <summary>"40 x 20 miles": the unit declines by the last number (ru).</summary>
        public static string SizeLabel(double wNm, double hNm)
        {
            long w = (long)Math.Round(wNm), h = (long)Math.Round(hNm);
            string unit = UiText.Lang == "ru"
                ? UiText.PluralRu(h, UiText.Get("unit.nm.one"), UiText.Get("unit.nm.few"), UiText.Get("unit.nm.many"))
                : UiText.Get("unit.nm.one");
            return UiText.Fmt("ui.zone.size", w, h, unit);
        }

        private static bool Same(double[] a, params double[] b)
        {
            for (int i = 0; i < b.Length; i++) if (Math.Abs(a[i] - b[i]) > 1e-9) return false;
            return true;
        }

        public static string SelfTest()
        {
            if (LonK(60, true) != Math.Cos(60 * Math.PI / 180.0) || LonK(60, false) != 1.0 || LonK(0, true) != 1.0) return "LonK";
            var rect = new[] { 100f, 200f, 300f, 300f };   // 200 x 100 px
            var cases = new[]
            {
                new object[] { 100f, 200f, ZoneHandle.NW }, new object[] { 300f, 200f, ZoneHandle.NE }, new object[] { 300f, 300f, ZoneHandle.SE }, new object[] { 100f, 300f, ZoneHandle.SW },
                new object[] { 200f, 200f, ZoneHandle.N }, new object[] { 200f, 300f, ZoneHandle.S }, new object[] { 300f, 250f, ZoneHandle.E }, new object[] { 100f, 250f, ZoneHandle.W },
                new object[] { 200f, 250f, ZoneHandle.Inside }, new object[] { 50f, 50f, ZoneHandle.None }, new object[] { 105f, 195f, ZoneHandle.NW }, new object[] { 112f, 200f, ZoneHandle.None },
            };
            foreach (var c in cases)
            {
                var got = Hit(rect, (float)c[0], (float)c[1], 16f);
                if (got != (ZoneHandle)c[2]) return "Hit(" + c[0] + "," + c[1] + ") = " + got + ", expected " + c[2];
            }
            if (Hit(new[] { 300f, 300f, 100f, 200f }, 100f, 200f, 16f) != ZoneHandle.NW) return "Hit with a reversed rectangle";
            if (Hit(new[] { 0f, 0f, 10f, 10f }, 5f, 5f, 16f) != ZoneHandle.NE && Hit(new[] { 0f, 0f, 10f, 10f }, 5f, 5f, 16f) == ZoneHandle.None) { /* a tiny zone: some handle wins, never None */ }
            if (Hit(null, 0, 0, 16f) != ZoneHandle.None) return "Hit null";
            // moving the whole zone, snapped
            var box = new[] { 0.0, 0.0, 40.0, 20.0 };
            var m = Drag(box, ZoneHandle.Inside, 10.4, -3.6, 100);
            if (!Same(m, 10, -4, 50, 16)) return "move the whole zone: " + string.Join(",", m);
            // sides and corners (y up: N = max y)
            if (!Same(Drag(box, ZoneHandle.E, 5.4, 9, 100), 0, 0, 45, 20)) return "E handle";
            if (!Same(Drag(box, ZoneHandle.N, 9, 6.6, 100), 0, 0, 40, 27)) return "N handle (snapped to 1 mile)";
            if (!Same(Drag(box, ZoneHandle.W, -10, 0, 100), -10, 0, 40, 20)) return "W handle";
            if (!Same(Drag(box, ZoneHandle.S, 0, -3, 100), 0, -3, 40, 20)) return "S handle";
            if (!Same(Drag(box, ZoneHandle.NE, 4, 3, 100), 0, 0, 44, 23)) return "NE corner";
            if (!Same(Drag(box, ZoneHandle.SW, -4, -3, 100), -4, -3, 40, 20)) return "SW corner";
            if (!Same(Drag(box, ZoneHandle.NW, -2, 2, 100), -2, 0, 40, 22)) return "NW corner";
            if (!Same(Drag(box, ZoneHandle.SE, 2, -2, 100), 0, -2, 42, 20)) return "SE corner";
            // minimum 5 x 5: the opposite edge stays
            if (!Same(Drag(box, ZoneHandle.E, -100, 0, 100), 0, 0, 5, 20)) return "minimum width 5";
            if (!Same(Drag(box, ZoneHandle.N, 0, -100, 100), 0, 0, 40, 5)) return "minimum height 5";
            if (!Same(Drag(box, ZoneHandle.W, 100, 0, 100), 35, 0, 40, 20)) return "minimum width 5 from the west";
            // maximum
            if (!Same(Drag(box, ZoneHandle.E, 500, 0, 100), 0, 0, 100, 20)) return "maximum width 100";
            if (!Same(Drag(box, ZoneHandle.SW, -500, -500, 60), -20, -40, 40, 20)) return "maximum 60 on both sides";
            // the input is not modified, a null / None handle returns it
            if (!Same(box, 0, 0, 40, 20) || Drag(box, ZoneHandle.None, 5, 5, 100) != box) return "input kept";
            // a circle
            var circ = new[] { 10.0, 10.0, 8.0 };
            var radii = new List<double> { 2, 5, 8, 10, 15, 20, 30 };
            if (!Same(DragCircle(circ, ZoneHandle.Center, 2.4, -1.6, radii), 12, 8, 8)) return "circle centre";
            if (!Same(DragCircle(circ, ZoneHandle.Radius, 5, 0, radii), 10, 10, 15) || !Same(DragCircle(circ, ZoneHandle.Radius, 4, 0, radii), 10, 10, 10)) return "circle radius sticks to the list (13 -> 15, 12 -> 10)";
            if (!Same(DragCircle(circ, ZoneHandle.Radius, -100, 0, radii), 10, 10, 2)) return "circle radius minimum of the list";
            if (!Same(DragCircle(circ, ZoneHandle.Radius, 2.2, 0, null), 10, 10, 10)) return "circle radius without a list: 1 mile";
            if (!Same(DragCircle(circ, ZoneHandle.Radius, -100, 0, null), 10, 10, 1)) return "circle radius at least 1";
            // label
            if (UiText.Lang == "ru")
            {
                if (SizeLabel(40, 20) != "40 × 20 миль") return "label: " + SizeLabel(40, 20);
                if (SizeLabel(40, 21) != "40 × 21 миля" || SizeLabel(5, 22) != "5 × 22 мили") return "label declension: " + SizeLabel(40, 21) + "," + SizeLabel(5, 22);
            }
            return null;
        }
    }
}
