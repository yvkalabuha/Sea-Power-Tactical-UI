// Fix 2026-10-22 (live 0.30.0, ADR-0077 §4 ZoneHandles: "the corner handles stick"): the per-frame model of dragging a zone handle on the map, pure.
// The cause was the T-154 bug again: the game mouse block went on AFTER the press on a handle, so the map had already started to pan and never got the
// release (the map then followed the cursor). Here the block is wanted from the hover (before the press) through the drag until one frame after the
// release, like UiMapDrawInput. A press that started elsewhere (button already held) never becomes a handle drag. The drag ends on the release anywhere
// (window, map, outside), on focus loss, Esc / RMB (cancel) or when editing is no longer allowed. GeoToPx: one geo -> pixel map for the drawing and the
// hit test, built from the map's own pixel -> geo (local units, the space the zone is drawn in), so the handles are hit where they are seen.
using System;

namespace VoiceBotBridge
{
    internal enum HandlePhase { Idle, Hover, Dragging, Releasing }

    internal sealed class ZoneHandleDrag
    {
        /// <summary>Drawn size of a handle and the hit size around its centre (pixels): a forgiving target that still leaves the map its clicks.</summary>
        public const float DrawPx = 12f, HitPx = 20f;

        public HandlePhase Phase { get; private set; }
        /// <summary>The handle under the cursor (Hover) or being dragged (Dragging); None otherwise.</summary>
        public ZoneHandle Handle { get; private set; }

        /// <summary>The game mouse must be blocked now (hover, drag, the frame after the release).</summary>
        public bool WantBlock { get { return Phase != HandlePhase.Idle; } }
        public bool Dragging { get { return Phase == HandlePhase.Dragging; } }

        public void Reset() { Phase = HandlePhase.Idle; Handle = ZoneHandle.None; }

        /// <summary>Per frame (Update). hit = the handle under the cursor (None over a window or away from the handles; Inside counts as None: the map's);
        /// down = LMB held; downEdge = pressed this frame; esc / rmb = pressed this frame; focused = the game window has the focus; enabled = the zone can be
        /// edited now. Returns "begin", "end" (keep the new zone), "cancel" (restore the old one) or null.</summary>
        public string Tick(ZoneHandle hit, bool down, bool downEdge, bool esc, bool rmb, bool focused, bool enabled)
        {
            if (hit == ZoneHandle.Inside) hit = ZoneHandle.None;
            switch (Phase)
            {
                case HandlePhase.Idle:
                    // a button held from before (a map pan, a selection box) never turns into a handle drag
                    if (!enabled || !focused || hit == ZoneHandle.None || down) return null;
                    Phase = HandlePhase.Hover; Handle = hit;
                    return null;
                case HandlePhase.Hover:
                    if (!enabled || !focused || hit == ZoneHandle.None)
                    {
                        Handle = ZoneHandle.None;
                        Phase = down ? HandlePhase.Releasing : HandlePhase.Idle;
                        return null;
                    }
                    Handle = hit;
                    if (downEdge || down) { Phase = HandlePhase.Dragging; return "begin"; }   // the block was on before the press: the map did not see it
                    return null;
                case HandlePhase.Dragging:
                    if (!enabled || esc || rmb)
                    {
                        Handle = ZoneHandle.None;
                        Phase = down && focused ? HandlePhase.Releasing : HandlePhase.Idle;
                        return "cancel";
                    }
                    if (!focused) { Handle = ZoneHandle.None; Phase = HandlePhase.Idle; return "end"; }   // focus lost: the release may never come
                    if (!down) { Handle = ZoneHandle.None; Phase = HandlePhase.Releasing; return "end"; }
                    return null;
                default:   // Releasing: the block stays until the button is up, then one more frame
                    if (!down || !focused) { Phase = HandlePhase.Idle; Handle = ZoneHandle.None; }
                    return null;
            }
        }

        /// <summary>The geo -> pixel map {A..F} (x = A*lat + B*lon + C, y = D*lat + E*lon + F) from three pixel -> geo samples: g0 at (x0, y0), gx at
        /// (x0 + step, y0), gy at (x0, y0 + step), each {lat, lon}. null = degenerate.</summary>
        public static double[] GeoToPx(double x0, double y0, double step, double[] g0, double[] gx, double[] gy)
        {
            if (g0 == null || gx == null || gy == null || g0.Length < 2 || gx.Length < 2 || gy.Length < 2 || !(Math.Abs(step) > 0)) return null;
            double a = (gx[0] - g0[0]) / step, b = (gy[0] - g0[0]) / step, c = (gx[1] - g0[1]) / step, d = (gy[1] - g0[1]) / step;
            double det = a * d - b * c;
            if (!(Math.Abs(det) > 1e-18) || double.IsNaN(det) || double.IsInfinity(det)) return null;
            double A = d / det, B = -b / det, D = -c / det, E = a / det;
            return new[] { A, B, x0 - A * g0[0] - B * g0[1], D, E, y0 - D * g0[0] - E * g0[1] };
        }

        public static float[] Px(double[] aff, double lat, double lon)
        {
            return new[] { (float)(aff[0] * lat + aff[1] * lon + aff[2]), (float)(aff[3] * lat + aff[4] * lon + aff[5]) };
        }

        /// <summary>A zone {lat0, lon0, lat1, lon1} as a pixel rectangle {x0, y0, x1, y1} (min / max); null = no map or no zone.</summary>
        public static float[] RectPx(double[] aff, double[] zone)
        {
            if (aff == null || aff.Length < 6 || zone == null || zone.Length < 4) return null;
            var a = Px(aff, zone[0], zone[1]); var b = Px(aff, zone[2], zone[3]);
            return new[] { Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[0], b[0]), Math.Max(a[1], b[1]) };
        }

        public static string SelfTest()
        {
            var d = new ZoneHandleDrag();
            // a pan that starts on the map and passes over a handle is not ours
            if (d.Tick(ZoneHandle.NE, true, false, false, false, true, true) != null || d.Phase != HandlePhase.Idle || d.WantBlock) return "held button over a handle";
            d.Tick(ZoneHandle.None, false, false, false, false, true, true);
            // hover blocks BEFORE the press (the live bug: the block came after it and the map kept panning)
            if (d.Tick(ZoneHandle.NE, false, false, false, false, true, true) != null || d.Phase != HandlePhase.Hover || !d.WantBlock || d.Handle != ZoneHandle.NE) return "hover blocks";
            if (d.Tick(ZoneHandle.NE, true, true, false, false, true, true) != "begin" || !d.Dragging || !d.WantBlock) return "press on the hovered handle";
            // the cursor leaves the handle and goes over the window: still dragging
            if (d.Tick(ZoneHandle.None, true, false, false, false, true, true) != null || !d.Dragging) return "drag goes on off the handle";
            // the release anywhere ends it; the block stays one frame
            if (d.Tick(ZoneHandle.None, false, false, false, false, true, true) != "end" || d.Phase != HandlePhase.Releasing || !d.WantBlock) return "release ends";
            if (d.Tick(ZoneHandle.None, false, false, false, false, true, true) != null || d.Phase != HandlePhase.Idle || d.WantBlock) return "idle a frame after the release";
            // released over a handle: hover again, no new drag without a new press
            d.Tick(ZoneHandle.N, false, false, false, false, true, true);
            if (d.Phase != HandlePhase.Hover || d.Tick(ZoneHandle.N, false, false, false, false, true, true) != null || d.Dragging) return "no drag without a press";
            // leaving the handle without a press: the block goes at once
            if (d.Tick(ZoneHandle.None, false, false, false, false, true, true) != null || d.WantBlock) return "hover off";
            // the inside of the zone is the map's
            if (d.Tick(ZoneHandle.Inside, false, false, false, false, true, true) != null || d.WantBlock) return "inside is the map's";
            // Esc / RMB cancel; the block stays while the button is held
            d.Tick(ZoneHandle.SW, false, false, false, false, true, true); d.Tick(ZoneHandle.SW, true, true, false, false, true, true);
            if (d.Tick(ZoneHandle.SW, true, false, true, false, true, true) != "cancel" || d.Phase != HandlePhase.Releasing || !d.WantBlock) return "esc cancels";
            if (d.Tick(ZoneHandle.SW, true, false, false, false, true, true) != null || d.Phase != HandlePhase.Releasing) return "held after esc: no new drag";
            d.Tick(ZoneHandle.SW, false, false, false, false, true, true);
            if (d.Phase != HandlePhase.Idle) return "released after esc";
            d.Tick(ZoneHandle.W, false, false, false, false, true, true); d.Tick(ZoneHandle.W, true, true, false, false, true, true);
            if (d.Tick(ZoneHandle.W, true, false, false, true, true, true) != "cancel") return "rmb cancels";
            d.Tick(ZoneHandle.None, false, false, false, false, true, true);
            // focus lost during a drag (the release goes to another window): ends, no block
            d.Tick(ZoneHandle.E, false, false, false, false, true, true); d.Tick(ZoneHandle.E, true, true, false, false, true, true);
            if (d.Tick(ZoneHandle.E, true, false, false, false, false, true) != "end" || d.WantBlock) return "focus lost ends the drag";
            if (d.Tick(ZoneHandle.E, true, false, false, false, false, true) != null || d.WantBlock) return "no hover without focus";
            // editing taken away (drawing mode, window closed): cancel
            d.Tick(ZoneHandle.None, false, false, false, false, true, true);
            d.Tick(ZoneHandle.S, false, false, false, false, true, true); d.Tick(ZoneHandle.S, true, true, false, false, true, true);
            if (d.Tick(ZoneHandle.S, true, false, false, false, true, false) != "cancel" || d.Dragging) return "disabled cancels";
            d.Tick(ZoneHandle.None, false, false, false, false, true, false);
            if (d.WantBlock) return "disabled releases";
            if (d.Tick(ZoneHandle.S, false, false, false, false, true, false) != null || d.WantBlock) return "disabled: no hover";
            d.Reset();
            if (d.WantBlock || d.Handle != ZoneHandle.None) return "reset";

            // GeoToPx inverts a pixel -> geo map (here: 1 px = 0.01 deg lat up, 0.02 deg lon right, a small skew)
            Func<double, double, double[]> toGeo = (x, y) => new[] { 10.0 - 0.01 * (y - 300) + 0.0005 * (x - 400), 20.0 + 0.02 * (x - 400) };
            var aff = GeoToPx(500, 400, 50, toGeo(500, 400), toGeo(550, 400), toGeo(500, 450));
            if (aff == null) return "affine";
            var p = Px(aff, toGeo(123, 77)[0], toGeo(123, 77)[1]);
            if (Math.Abs(p[0] - 123) > 1e-3 || Math.Abs(p[1] - 77) > 1e-3) return "affine round trip " + p[0] + "," + p[1];
            if (GeoToPx(0, 0, 50, new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }) != null) return "degenerate";
            // the handles drawn from RectPx are hit by ZoneEditMath.Hit at the same pixels, within HitPx
            var zone = new[] { toGeo(300, 500)[0], toGeo(300, 500)[1], toGeo(600, 200)[0], toGeo(600, 200)[1] };
            var r = RectPx(aff, zone);
            if (r == null || Math.Abs(r[0] - 300) > 0.01 || Math.Abs(r[3] - 500) > 0.01) return "rect px " + (r == null ? "null" : r[0] + "," + r[1] + "," + r[2] + "," + r[3]);
            if (ZoneEditMath.Hit(r, r[2] + 8, r[1] - 8, HitPx) != ZoneHandle.NE) return "corner hit within the slop";
            if (ZoneEditMath.Hit(r, r[2] + 12, r[1], HitPx) == ZoneHandle.NE) return "beyond the slop is the map's";
            if (ZoneEditMath.Hit(r, (r[0] + r[2]) / 2, (r[1] + r[3]) / 2, HitPx) != ZoneHandle.Inside) return "inside";
            return null;
        }
    }
}
