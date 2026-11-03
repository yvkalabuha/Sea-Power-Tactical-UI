using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>What the game and the screen look like when the player holds the left button (T-068). Filled by RadialGame, judged by RadialMath.Allowed.</summary>
    internal sealed class RadialEnv
    {
        public bool InMission, Focused, HudVisible, Block3DPatched, ContextMenuOpen, EscMenuOpen, MessagePause, Typing,
                    OverWorldDisplay, PipMouseOver, OnMapEnabled, MapCapable, OverModWindow;
        public string View;      // 3d | map | ui
        public bool TargetOk;
    }

    internal enum GestureEvent { None, Open, Execute, Cancel }

    /// <summary>Pure geometry and rules of the radial menu. Screen coordinates, y down; sector 0 = N, clockwise.</summary>
    internal static class RadialMath
    {
        private static double AngleDeg(double dx, double dy)
        {
            double a = Math.Atan2(dx, -dy) * 180.0 / Math.PI;   // 0 = up, clockwise
            return a < 0 ? a + 360.0 : a;
        }

        private static double Diff(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d <= -180.0) d += 360.0;
            return d;
        }


        /// <summary>Width of one sector of an n-item ring, degrees (n=1 -> 360).</summary>
        public static double SectorWidth(int n) { return 360.0 / Math.Max(1, n); }

        /// <summary>Centre direction of sector i of n, degrees clockwise from up: item 0 is on top (T-152).</summary>
        public static double SectorCentre(int i, int n) { return i * SectorWidth(n); }

        /// <summary>Sector 0..7 under the vector (8-sector ring; kept for old callers).</summary>
        public static int SectorOf(double dx, double dy, int prev, double hystDeg) { return SectorOf(dx, dy, prev, hystDeg, 8); }

        /// <summary>
        /// Sector 0..n-1 under the vector of an n-sector ring (T-152): sector i is centred at i*360/n. The previous sector is kept while
        /// the angle is within hystDeg (at most a quarter of the sector width) beyond its border. n=1: always 0.
        /// </summary>
        public static int SectorOf(double dx, double dy, int prev, double hystDeg, int n)
        {
            if (n <= 0) return -1;
            if (dx == 0 && dy == 0) return prev;
            if (n == 1) return 0;
            double a = AngleDeg(dx, dy), w = SectorWidth(n);
            double h = Math.Min(hystDeg, w / 4.0);
            if (prev >= 0 && prev < n && Math.Abs(Diff(a, prev * w)) <= w / 2.0 + h) return prev;
            return ((int)Math.Floor((a + w / 2.0) / w)) % n;
        }

        /// <summary>Item of the arc around the parent sector of an 8-sector ring (kept for old callers).</summary>
        public static int ArcItemOf(double dx, double dy, int parentSector, int count, double spanDeg, int prev)
        {
            return ArcItemOfAt(dx, dy, parentSector * 45.0, count, spanDeg, prev);
        }

        /// <summary>Item of the arc centred at parentDeg (spanDeg wide in total, items spread evenly clockwise); -1 = outside.</summary>
        public static int ArcItemOfAt(double dx, double dy, double parentDeg, int count, double spanDeg, int prev)
        {
            if (count <= 0) return -1;
            double d = Diff(AngleDeg(dx, dy), parentDeg);
            double half = spanDeg / 2.0, cell = spanDeg / count;
            const double hyst = 3.0;
            if (prev >= 0 && prev < count)
            {
                double lo = -half + prev * cell - hyst, hi = -half + (prev + 1) * cell + hyst;
                if (d >= lo && d <= hi && d >= -half - hyst && d <= half + hyst) return prev;
            }
            if (d < -half || d > half) return -1;
            return Math.Min(count - 1, (int)Math.Floor((d + half) / cell));
        }

        /// <summary>T-328: one page of an arc with more cells than fit. total items, page (any int, wraps), size = most cells per page (including the "More" cell).
        /// total &lt;= size: all on one page, no "More". Else size-1 items per page and a "More" cell (more = true) that pages on (cycling).</summary>
        public static void ArcPage(int total, int page, int size, out int start, out int count, out bool more)
        {
            if (total <= size || size < 2) { start = 0; count = Math.Max(0, total); more = false; return; }
            int per = size - 1, pages = (total + per - 1) / per;
            int p = ((page % pages) + pages) % pages;
            start = p * per; count = Math.Min(per, total - start); more = true;
        }

        /// <summary>T-328: a caption of at most max chars: longer text is cut and ends with an ellipsis.</summary>
        public static string Ellipsize(string s, int max)
        {
            if (s == null) return "";
            if (max < 2 || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "\u2026";
        }

        /// <summary>Moves the ring centre inside the screen so that the whole outer radius is visible.</summary>
        public static void ClampCentre(double cx, double cy, double outerR, double w, double h, out double ox, out double oy)
        {
            ox = w < 2 * outerR ? w / 2 : Math.Max(outerR, Math.Min(w - outerR, cx));
            oy = h < 2 * outerR ? h / 2 : Math.Max(outerR, Math.Min(h - outerR, cy));
        }

        /// <summary>Pixel scale of the ring: the override, else the game's UI scale at the 1080p reference.</summary>
        public static double Scale(double uiScale, int screenH, double overrideScale)
        {
            return overrideScale > 0 ? overrideScale : uiScale * screenH / 1080.0;
        }

        /// <summary>
        /// Screen pixels (y down) -> camera pixels (y down). The camera image (pixelRect: x,y offset + w,h, e.g. 1024x576) is stretched over the
        /// whole screen (e.g. 2560x1440), the way Pip3DView.RemapMouse maps the mouse. Camera == screen is the identity.
        /// </summary>
        public static void ScreenToCamera(double sx, double sy, int screenW, int screenH, double camX, double camY, double camW, double camH, out double cx, out double cy)
        {
            if (screenW <= 0 || screenH <= 0 || !(camW > 0) || !(camH > 0)) { cx = sx; cy = sy; return; }
            cx = camX + sx * camW / screenW;
            cy = camY + sy * camH / screenH;
        }

        /// <summary>Inverse of ScreenToCamera (what WorldToScreenPoint returns, flipped to y down -> screen pixels).</summary>
        public static void CameraToScreen(double cx, double cy, int screenW, int screenH, double camX, double camY, double camW, double camH, out double sx, out double sy)
        {
            if (screenW <= 0 || screenH <= 0 || !(camW > 0) || !(camH > 0)) { sx = cx; sy = cy; return; }
            sx = (cx - camX) * screenW / camW;
            sy = (cy - camY) * screenH / camH;
        }

        /// <summary>Noesis PointToScreen result -> the pixel point the mod uses (the game already returns pixels: pxScale 1; the old 1.333 division is gone).</summary>
        public static void MapPointFromNoesis(double nx, double ny, double pxScale, out double x, out double y)
        {
            double k = pxScale > 0 ? pxScale : 1.0;
            x = nx / k; y = ny / k;
        }

        /// <summary>One log line per ring closing: "radial closed: reason=cancel view=3d held=420ms repaints=12".</summary>
        public static string CloseReasonLine(string reason, string view, double heldMs, int repaints)
        {
            return "radial closed: reason=" + (string.IsNullOrEmpty(reason) ? "-" : reason) + " view=" + (string.IsNullOrEmpty(view) ? "-" : view)
                + " held=" + Math.Round(heldMs).ToString("0", CultureInfo.InvariantCulture) + "ms repaints=" + repaints.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>null = the ring may open, else the reason it must not.</summary>
        public static string Allowed(RadialEnv e)
        {
            if (!e.InMission) return "no_mission";
            if (!e.Focused) return "not_focused";
            if (e.EscMenuOpen) return "esc_menu";
            if (e.Typing) return "typing";
            if (e.MessagePause) return "message_pause";
            if (e.ContextMenuOpen) return "context_menu";
            if (e.OverModWindow) return "over_mod_window";
            if (e.View != "3d" && e.View != "map") return "over_ui";
            if (e.View == "map" && (!e.OnMapEnabled || !e.MapCapable)) return "map_disabled";
            if (!e.HudVisible && !e.Block3DPatched) return "hud_hidden";
            if (e.OverWorldDisplay) return "world_display";
            if (e.PipMouseOver) return "pip";
            if (!e.TargetOk) return "not_target";
            return null;
        }

        /// <summary>
        /// "free" | "taken" | "unknown". Hold key without modifiers: taken when ANY game binding contains it (case-insensitive), because the ring
        /// opens on Input.GetKeyDown(key) whatever modifiers are held. null/empty key -> "taken"; bindings null or no non-empty binding -> "unknown".
        /// conflict = "&lt;name|?&gt; (A+B)" on taken (T-082).
        /// </summary>
        public static string KeyVerdict(string key, IEnumerable<string[]> bindings, IList<string> names, out string conflict)
        {
            conflict = null;
            if (string.IsNullOrEmpty(key)) return "taken";
            if (bindings == null) return "unknown";
            bool any = false;
            int i = 0;
            foreach (var b in bindings)
            {
                int idx = i++;
                if (b == null || b.Length == 0) continue;
                any = true;
                foreach (var c in b)
                    if (string.Equals(c, key, StringComparison.OrdinalIgnoreCase))
                    {
                        string name = names != null && idx < names.Count ? names[idx] : null;
                        conflict = (string.IsNullOrEmpty(name) ? "?" : name) + " (" + string.Join("+", b) + ")";
                        return "taken";
                    }
            }
            return any ? "free" : "unknown";
        }

        private static void RefusalSelfTest()
        {
            var l = new RadialRefusalLog();
            Eq("refusal new", l.Next("not_target", 0, false) == "radial not opened: not_target");
            Eq("refusal same 1s", l.Next("not_target", 1, false) == null);
            Eq("refusal same 5s", l.Next("not_target", 5, false) == null);
            Eq("refusal 10s x3", l.Next("not_target", 10, false) == "radial not opened: not_target (x3)");
            Eq("refusal after repeat quiet", l.Next("not_target", 11, false) == null);
            Eq("refusal debug again", l.Next("not_target", 12, true) == "radial not opened (again): not_target");
            Eq("refusal new reason", l.Next("empty ring", 12.5, false) == "radial not opened: empty ring");
            l.Clear();
            Eq("refusal after clear", l.Next("empty ring", 13, false) == "radial not opened: empty ring");
        }

        private static void KeySelfTest()
        {
            string c;
            Eq("key: no list", KeyVerdict("Q", null, null, out c) == "unknown" && c == null);
            Eq("key: empty list", KeyVerdict("Q", new List<string[]>(), null, out c) == "unknown");
            Eq("key: only empty bindings", KeyVerdict("Q", new List<string[]> { new string[0], null }, null, out c) == "unknown");
            Eq("key: free", KeyVerdict("Q", new List<string[]> { new[] { "W" }, new[] { "E" } }, null, out c) == "free" && c == null);
            Eq("key: taken with name", KeyVerdict("Q", new List<string[]> { new[] { "Q" } }, new List<string> { "Fire" }, out c) == "taken" && c == "Fire (Q)");
            Eq("key: modifier does not save it", KeyVerdict("Q", new List<string[]> { new[] { "LeftShift", "Q" } }, null, out c) == "taken" && c == "? (LeftShift+Q)");
            Eq("key: case-insensitive", KeyVerdict("q", new List<string[]> { new[] { "Q" } }, null, out c) == "taken");
            Eq("key: empty key", KeyVerdict("", new List<string[]> { new[] { "W" } }, null, out c) == "taken");
            Eq("key: null key", KeyVerdict(null, new List<string[]> { new[] { "W" } }, null, out c) == "taken");
            Eq("key: names shorter than bindings", KeyVerdict("Q", new List<string[]> { new[] { "W" }, new[] { "Q" } }, new List<string> { "A" }, out c) == "taken" && c == "? (Q)");
        }

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        public static string SelfTest()
        {
            _fail = null;
            // angles (dy < 0 = up)
            Eq("N", SectorOf(0, -10, -1, 0) == 0);
            Eq("44 deg -> NE", SectorOf(Math.Sin(44 * Math.PI / 180), -Math.Cos(44 * Math.PI / 180), -1, 0) == 1);
            Eq("46 deg -> NE", SectorOf(Math.Sin(46 * Math.PI / 180), -Math.Cos(46 * Math.PI / 180), -1, 0) == 1);
            Eq("E", SectorOf(10, 0, -1, 0) == 2);
            Eq("S", SectorOf(0, 10, -1, 0) == 4);
            Eq("W", SectorOf(-10, 0, -1, 0) == 6);
            Eq("359 deg -> N", SectorOf(-Math.Sin(Math.PI / 180), -Math.Cos(Math.PI / 180), -1, 0) == 0);
            Eq("23 deg without hysteresis -> NE", SectorOf(Math.Sin(23 * Math.PI / 180), -Math.Cos(23 * Math.PI / 180), 0, 0) == 1);
            Eq("hysteresis keeps N at 25 deg", SectorOf(Math.Sin(25 * Math.PI / 180), -Math.Cos(25 * Math.PI / 180), 0, 4) == 0);
            Eq("hysteresis releases N at 28 deg", SectorOf(Math.Sin(28 * Math.PI / 180), -Math.Cos(28 * Math.PI / 180), 0, 4) == 1);
            Eq("throw beyond the outer radius keeps the sector", SectorOf(5000, 10, -1, 4) == 2);
            // arc: 3 items, 120 deg around NW (315 deg)
            double a0 = 315 - 40, a1 = 315, a2 = 315 + 40;
            Func<double, int> arc = ang => ArcItemOf(Math.Sin(ang * Math.PI / 180), -Math.Cos(ang * Math.PI / 180), 7, 3, 120, -1);
            Eq("arc item 0", arc(a0) == 0);
            Eq("arc item 1", arc(a1) == 1);
            Eq("arc item 2", arc(a2) == 2);
            Eq("arc outside", arc(315 - 70) == -1 && arc(315 + 70) == -1);
            Eq("arc across north wraps", ArcItemOf(Math.Sin(10 * Math.PI / 180), -Math.Cos(10 * Math.PI / 180), 0, 3, 120, -1) == 1);
            Eq("arc hysteresis", ArcItemOf(Math.Sin((315 - 21) * Math.PI / 180), -Math.Cos((315 - 21) * Math.PI / 180), 7, 3, 120, 1) == 1);
            // T-152: n-sector ring
            for (int n = 1; n <= 12; n++)
            {
                double w = 360.0 / n;
                Eq("n=" + n + " width", SectorWidth(n) == w && SectorCentre(0, n) == 0);
                for (int i = 0; i < n; i++)
                {
                    double c = SectorCentre(i, n);
                    Func<double, int> at = ang => SectorOf(Math.Sin(ang * Math.PI / 180), -Math.Cos(ang * Math.PI / 180), -1, 0, n);
                    Eq("n=" + n + " centre of " + i, at(c) == i);
                    if (n >= 2)
                    {
                        Eq("n=" + n + " just inside lower border of " + i, at(c - w / 2 + 0.01) == i);
                        Eq("n=" + n + " just inside upper border of " + i, at(c + w / 2 - 0.01) == i);
                        Eq("n=" + n + " past the upper border of " + i + " is the next", at(c + w / 2 + 0.01) == (i + 1) % n);
                        Eq("n=" + n + " past the lower border of " + i + " is the previous", at(c - w / 2 - 0.01) == (i + n - 1) % n);
                    }
                    else Eq("n=1 every direction is 0", at(c) == 0 && at(90) == 0 && at(180) == 0 && at(270) == 0 && at(359.9) == 0);
                }
                Eq("n=" + n + " N is sector 0 (item 1 on top)", SectorOf(0, -10, -1, 0, n) == 0);
                Eq("n=" + n + " far throw keeps the angle", SectorOf(0, -5000, -1, 4, n) == 0);
                Eq("n=" + n + " no vector keeps prev", SectorOf(0, 0, n - 1, 4, n) == n - 1);
                if (n >= 2)
                {
                    double bo = w / 2 + 2;   // 2 deg past the border of sector 0: the hysteresis (<= w/4) keeps it only when w/4 >= 2
                    Eq("n=" + n + " hysteresis", (SectorOf(Math.Sin(bo * Math.PI / 180), -Math.Cos(bo * Math.PI / 180), 0, 4, n) == 0) == (w / 4 >= 2));
                }
            }
            Eq("n=2 sectors: top and bottom, border at the horizontal", SectorOf(0, -10, -1, 0, 2) == 0 && SectorOf(0, 10, -1, 0, 2) == 1 && SectorOf(10, -1, -1, 0, 2) == 0 && SectorOf(10, 1, -1, 0, 2) == 1 && SectorOf(-10, 1, -1, 0, 2) == 1 && SectorOf(-10, -1, -1, 0, 2) == 0);
            Eq("n=3: 120 deg sectors, item 1 at 0, item 2 at 120", SectorOf(Math.Sin(100 * Math.PI / 180), -Math.Cos(100 * Math.PI / 180), -1, 0, 3) == 1 && SectorOf(Math.Sin(50 * Math.PI / 180), -Math.Cos(50 * Math.PI / 180), -1, 0, 3) == 0);
            Eq("n=8 matches the old 8-sector overload", SectorOf(10, 3, -1, 0, 8) == SectorOf(10, 3, -1, 0));
            Eq("n<=0 has no sector", SectorOf(1, 1, -1, 0, 0) == -1);
            // arc centred on a sector of a 5-ring (sector 2 at 144 deg)
            double cen5 = SectorCentre(2, 5);
            Func<double, int> arc5 = ang => ArcItemOfAt(Math.Sin(ang * Math.PI / 180), -Math.Cos(ang * Math.PI / 180), cen5, 3, 120, -1);
            Eq("arc around a 5-ring sector", arc5(cen5 - 40) == 0 && arc5(cen5) == 1 && arc5(cen5 + 40) == 2 && arc5(cen5 + 70) == -1);
            // clamp
            double ox, oy;
            ClampCentre(10, 10, 100, 1920, 1080, out ox, out oy); Eq("clamp top-left", ox == 100 && oy == 100);
            ClampCentre(1915, 1075, 100, 1920, 1080, out ox, out oy); Eq("clamp bottom-right", ox == 1820 && oy == 980);
            ClampCentre(960, 5, 100, 1280, 720, out ox, out oy); Eq("clamp top", ox == 960 && oy == 100);
            ClampCentre(5, 400, 100, 1280, 720, out ox, out oy); Eq("clamp left", ox == 100 && oy == 400);
            ClampCentre(600, 400, 100, 1280, 720, out ox, out oy); Eq("clamp inside untouched", ox == 600 && oy == 400);
            ClampCentre(50, 50, 400, 600, 600, out ox, out oy); Eq("clamp tiny screen", ox == 300 && oy == 300);
            // scale
            Eq("scale 1080", Scale(1, 1080, 0) == 1.0);
            Eq("scale 1440 x1.25", Math.Abs(Scale(1.25, 1440, 0) - 1.25 * 1440 / 1080.0) < 1e-9);
            Eq("scale override", Scale(1.25, 1440, 0.8) == 0.8);

            // gesture, synthetic time
            GestureSelfTest();
            ProjectionSelfTest();
            PickSelfTest();
            AllowedSelfTest();
            KeySelfTest();
            RefusalSelfTest();
            return _fail;
        }

        private static void ProjectionSelfTest()
        {
            double a, b;
            ScreenToCamera(1280, 720, 2560, 1440, 0, 0, 1024, 576, out a, out b);
            Eq("screen->camera centre", Math.Abs(a - 512) < 1e-9 && Math.Abs(b - 288) < 1e-9);
            CameraToScreen(a, b, 2560, 1440, 0, 0, 1024, 576, out a, out b);
            Eq("camera->screen round trip", Math.Abs(a - 1280) < 1e-9 && Math.Abs(b - 720) < 1e-9);
            ScreenToCamera(1099, 675, 2560, 1440, 0, 0, 1024, 576, out a, out b);
            double c, d;
            CameraToScreen(a, b, 2560, 1440, 0, 0, 1024, 576, out c, out d);
            Eq("round trip off-centre", Math.Abs(c - 1099) < 1e-9 && Math.Abs(d - 675) < 1e-9 && Math.Abs(a - 439.6) < 1e-9);
            ScreenToCamera(1099, 675, 2560, 1440, 0, 0, 2560, 1440, out a, out b);
            Eq("full-screen camera is the identity", a == 1099 && b == 675);
            CameraToScreen(1099, 675, 2560, 1440, 0, 0, 2560, 1440, out a, out b);
            Eq("full-screen camera is the identity (back)", a == 1099 && b == 675);
            ScreenToCamera(10, 20, 0, 0, 0, 0, 0, 0, out a, out b);
            Eq("degenerate sizes leave the point", a == 10 && b == 20);
            ScreenToCamera(100, 50, 200, 100, 30, 10, 100, 50, out a, out b);
            Eq("camera offset", a == 80 && b == 35);
            MapPointFromNoesis(1099, 675, 1.0, out a, out b);
            Eq("noesis px scale 1 keeps the cursor (log: cursor 1099,675)", a == 1099 && b == 675);
            MapPointFromNoesis(2560, 1440, 2.0, out a, out b);
            Eq("noesis px scale 2", a == 1280 && b == 720);
            MapPointFromNoesis(100, 50, 0, out a, out b);
            Eq("noesis px scale 0 -> 1", a == 100 && b == 50);
            Eq("target map with scale 1 (cursor on the unit)", RadialPick.TargetMap(false, true, 1099, 675, 1099, 675, 20));
            Eq("close line", CloseReasonLine("cancel", "3d", 420.4, 12) == "radial closed: reason=cancel view=3d held=420ms repaints=12");
            Eq("close line empty", CloseReasonLine(null, null, 0, 0) == "radial closed: reason=- view=- held=0ms repaints=0");
        }

        private static void PickSelfTest()
        {
            Eq("hit3d behind camera", !RadialPick.Hit3D(100, 100, 100, 100, -1, null, 20));
            Eq("hit3d zero depth", !RadialPick.Hit3D(100, 100, 100, 100, 0, null, 20));
            Eq("hit3d in radius", RadialPick.Hit3D(110, 100, 100, 100, 5, null, 20));
            Eq("hit3d outside radius", !RadialPick.Hit3D(200, 100, 100, 100, 5, null, 20));
            Eq("hit3d inside the model rect", RadialPick.Hit3D(300, 120, 100, 100, 5, new[] { 50.0, 80.0, 400.0, 60.0 }, 20));
            Eq("hit3d outside rect and radius", !RadialPick.Hit3D(300, 200, 100, 100, 5, new[] { 50.0, 80.0, 400.0, 60.0 }, 20));
            Eq("hit3d rect ignored behind camera", !RadialPick.Hit3D(300, 120, 100, 100, -2, new[] { 50.0, 80.0, 400.0, 60.0 }, 20));
            PickTargetSelfTest();
            double cx, cy;
            RadialPick.Centre(500, 500, 520, 510, true, 60, out cx, out cy); Eq("centre on the unit when near", cx == 520 && cy == 510);
            RadialPick.Centre(500, 500, 900, 900, true, 60, out cx, out cy); Eq("centre on the press when the unit is far", cx == 500 && cy == 500);
            RadialPick.Centre(500, 500, 510, 510, false, 60, out cx, out cy); Eq("centre on the press without a unit", cx == 500 && cy == 500);
        }

        private static void PickTargetSelfTest()
        {
            Eq("target3d ray hit wins, even behind the camera", RadialPick.Target3D(true, 900, 900, 100, 100, -1, null, 20));
            Eq("target3d no ray: in radius", RadialPick.Target3D(false, 110, 100, 100, 100, 5, null, 20));
            Eq("target3d no ray: outside", !RadialPick.Target3D(false, 200, 100, 100, 100, 5, null, 20));
            Eq("target3d no ray: behind camera", !RadialPick.Target3D(false, 100, 100, 100, 100, -1, null, 20));
            Eq("targetmap icon wins without a point", RadialPick.TargetMap(true, false, 0, 0, 0, 0, 20));
            Eq("targetmap in radius", RadialPick.TargetMap(false, true, 110, 100, 100, 100, 20));
            Eq("targetmap outside radius", !RadialPick.TargetMap(false, true, 200, 100, 100, 100, 20));
            Eq("targetmap no icon, no point", !RadialPick.TargetMap(false, false, 100, 100, 100, 100, 20));
            Eq("targetmap radius edge inclusive", RadialPick.TargetMap(false, true, 120, 100, 100, 100, 20));
            double[] rc = RadialPick.RectOfCorners(new[] { 10.0, 50, 10, 50, 10, 50, 10, 50 }, new[] { 20.0, 20, 40, 40, 20, 20, 40, 40 }, new[] { 1.0, 1, 1, 1, 2, 2, 2, 2 });
            Eq("rect of 8 corners", rc != null && rc[0] == 10 && rc[1] == 20 && rc[2] == 40 && rc[3] == 20);
            rc = RadialPick.RectOfCorners(new[] { 10.0, 50, 10, 50, 0, 0, 0, 900 }, new[] { 20.0, 20, 40, 40, 0, 0, 0, 900 }, new[] { 1.0, 1, 1, 1, 1, -1, -2, -3 });
            Eq("rect skips corners behind the camera", rc != null && rc[0] == 0 && rc[1] == 0 && rc[2] == 50 && rc[3] == 40);
            Eq("rect none in front", RadialPick.RectOfCorners(new[] { 1.0, 2 }, new[] { 1.0, 2 }, new[] { -1.0, -2 }) == null);
            Eq("rect z == 0 is behind", RadialPick.RectOfCorners(new[] { 1.0 }, new[] { 1.0 }, new[] { 0.0 }) == null);
            Eq("rect lengths differ", RadialPick.RectOfCorners(new[] { 1.0, 2 }, new[] { 1.0 }, new[] { 1.0, 1 }) == null);
            Eq("rect null arrays", RadialPick.RectOfCorners(null, null, null) == null);
            Eq("renderer mesh", RadialPick.ModelRenderer("MeshRenderer") && RadialPick.ModelRenderer("SkinnedMeshRenderer"));
            Eq("renderer not a hull", !RadialPick.ModelRenderer("ParticleSystemRenderer") && !RadialPick.ModelRenderer("TrailRenderer")
                && !RadialPick.ModelRenderer("LineRenderer") && !RadialPick.ModelRenderer("BillboardRenderer") && !RadialPick.ModelRenderer(null) && !RadialPick.ModelRenderer(""));

            var i = new RadialPickInfo
            {
                View = "3d", Mx = 1280, My = 720, UnitOk = true, Ux = 1301.5, Uy = 702, Uz = 85.2, Rect = new[] { 1200.0, 650, 300, 90 }, RadiusPx = 26.7,
                RayHits = 3, RayHitSelected = false, RayFirst = "Yorktown", Camera = "Main Camera", CamW = 2560, CamH = 1440, ScreenW = 2560, ScreenH = 1440, OnSel = false
            };
            Eq("pick info reference line", i.Format() == "radial pick: view=3d cursor=1280.0,720.0 unit=1301.5,702.0 z=85.2 rect=1200.0,650.0 300.0x90.0 r=26.7 ray=3 ray_sel=false ray_first=Yorktown cam=Main Camera 2560x1440 screen=2560x1440 map=- map_vm=- map_pt=- on_sel=false");
            i = new RadialPickInfo { View = "map", UnitOk = false, Rect = null, MapPointOk = true, MapX = 10.25, MapY = 20, MapType = "MapBase", MapHitVm = "RangeCircles", OnSel = true };
            string f = i.Format();
            Eq("pick info missing values", f.Contains("unit=- z=-") && f.Contains("rect=-") && f.Contains("map_pt=10.3,20.0") && f.Contains("map=MapBase map_vm=RangeCircles") && f.Contains("ray_first=-") && f.Contains("cam=- ") && f.EndsWith("on_sel=true"));
            i = new RadialPickInfo { RayFirst = new string('x', 100), MapHitVm = new string('y', 100) };
            f = i.Format();
            Eq("pick info truncates", f.Contains("ray_first=" + new string('x', 40) + " ") && !f.Contains(new string('x', 41)) && !f.Contains(new string('y', 41)) && f.Length <= 400);
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
                i = new RadialPickInfo { Mx = 1.5, UnitOk = true, Ux = 2.5 };
                Eq("pick info ignores the culture", i.Format().Contains("cursor=1.5,0.0 unit=2.5,0.0"));
            }
            catch (CultureNotFoundException) { }
            finally { CultureInfo.CurrentCulture = old; }
        }

        private static RadialEnv GoodEnv()
        {
            return new RadialEnv { InMission = true, Focused = true, HudVisible = true, OnMapEnabled = true, MapCapable = true, View = "3d", TargetOk = true };
        }

        private static void AllowedSelfTest()
        {
            Eq("allowed: all good", Allowed(GoodEnv()) == null);
            var e = GoodEnv(); e.InMission = false; Eq("no mission", Allowed(e) == "no_mission");
            e = GoodEnv(); e.Focused = false; Eq("not focused", Allowed(e) == "not_focused");
            e = GoodEnv(); e.EscMenuOpen = true; Eq("esc menu", Allowed(e) == "esc_menu");
            e = GoodEnv(); e.Typing = true; Eq("typing", Allowed(e) == "typing");
            e = GoodEnv(); e.MessagePause = true; Eq("message pause", Allowed(e) == "message_pause");
            e = GoodEnv(); e.ContextMenuOpen = true; Eq("context menu", Allowed(e) == "context_menu");
            e = GoodEnv(); e.OverModWindow = true; Eq("over a mod window (Overview)", Allowed(e) == "over_mod_window");
            e = GoodEnv(); e.View = "ui"; Eq("over ui", Allowed(e) == "over_ui");
            e = GoodEnv(); e.View = null; Eq("no view", Allowed(e) == "over_ui");
            e = GoodEnv(); e.View = "map"; Eq("map ok", Allowed(e) == null);
            e = GoodEnv(); e.View = "map"; e.OnMapEnabled = false; Eq("map disabled by config", Allowed(e) == "map_disabled");
            e = GoodEnv(); e.View = "map"; e.MapCapable = false; Eq("map without capability", Allowed(e) == "map_disabled");
            e = GoodEnv(); e.HudVisible = false; Eq("hud hidden without the patch", Allowed(e) == "hud_hidden");
            e = GoodEnv(); e.HudVisible = false; e.Block3DPatched = true; Eq("hud hidden with the patch", Allowed(e) == null);
            e = GoodEnv(); e.OverWorldDisplay = true; Eq("world display", Allowed(e) == "world_display");
            e = GoodEnv(); e.PipMouseOver = true; Eq("pip", Allowed(e) == "pip");
            e = GoodEnv(); e.TargetOk = false; Eq("not the target", Allowed(e) == "not_target");
        }

        private static void GestureSelfTest()
        {
            // hold 300 ms, tolerance 6 px, submenu 180 ms, dead 28, outer 110, hysteresis 4 deg, 2 release frames
            Func<RadialGesture> mk = () => new RadialGesture(300, 6, 180, 28, 110, 4, 2);
            int tries = 0;
            Func<double, double, double[]> open = (x, y) => { tries++; return new[] { x, y }; };
            Func<double, double, double[]> refuse = (x, y) => { tries++; return null; };
            Func<int, int> kids = s => s == 7 ? 3 : s == 6 ? -1 : s == 5 ? -1 : 0;   // 7 = group, 6 and 5 = empty/grey

            // short click: nothing
            var g = mk(); tries = 0;
            var ev = g.Step(0, true, 500, 500, open, kids);
            Eq("press alone is not an event", ev == GestureEvent.None && !g.Open && !g.BlockInput);
            ev = g.Step(150, false, 500, 500, open, kids);
            Eq("short click: no open, no block", ev == GestureEvent.None && !g.Open && !g.BlockInput && tries == 0);

            // moved beyond tolerance before the threshold: ignored until release; next press works again
            g = mk(); tries = 0;
            g.Step(0, true, 500, 500, open, kids);
            g.Step(100, true, 507, 500, open, kids);
            ev = g.Step(400, true, 507, 500, open, kids);
            Eq("moved 7 px: ignored", ev == GestureEvent.None && !g.Open && tries == 0);
            g.Step(500, false, 507, 500, open, kids);
            g.Step(600, true, 500, 500, open, kids);
            ev = g.Step(900, true, 500, 500, open, kids);
            Eq("press again works", ev == GestureEvent.Open && g.Open && g.BlockInput && tries == 1);
            Eq("centre from tryOpen", g.CentreX == 500 && g.CentreY == 500);
            ev = g.Step(1000, true, 500, 500, open, kids);
            Eq("tryOpen asked exactly once", tries == 1 && ev == GestureEvent.None);

            // T-152: 3-sector ring, sector 1 centred at 120 deg (lower right), sector 2 at 240 deg
            g = mk(); g.SectorCount = 3; g.OpenNow(0, 500, 500);
            g.Step(10, true, 500 + 60 * Math.Sin(120 * Math.PI / 180), 500 - 60 * Math.Cos(120 * Math.PI / 180), open, s3 => 0);
            Eq("3 sectors: 120 deg is sector 1", g.Sector == 1);
            g.Step(20, true, 500 + 60 * Math.Sin(182 * Math.PI / 180), 500 - 60 * Math.Cos(182 * Math.PI / 180), open, s3 => 0);
            Eq("3 sectors: 182 deg stays in sector 1 (hysteresis past the border at 180)", g.Sector == 1);
            ev = g.Step(30, false, 500 + 60 * Math.Sin(250 * Math.PI / 180), 500 - 60 * Math.Cos(250 * Math.PI / 180), open, s3 => 0);
            Eq("3 sectors: release at 250 deg executes sector 2", ev == GestureEvent.Execute && g.Sector == 2);
            g = mk(); g.SectorCount = 1; g.OpenNow(0, 500, 500);
            ev = g.Step(10, false, 500 - 60, 500, open, s1 => 0);
            Eq("1 sector: any direction executes sector 0", ev == GestureEvent.Execute && g.Sector == 0);

            // tolerance edge: 6 px ok
            g = mk(); tries = 0;
            g.Step(0, true, 500, 500, open, kids);
            ev = g.Step(300, true, 506, 500, open, kids);
            Eq("6 px still opens", ev == GestureEvent.Open);

            // refusal: the event stays with the game until release
            g = mk(); tries = 0;
            g.Step(0, true, 500, 500, refuse, kids);
            ev = g.Step(300, true, 500, 500, refuse, kids);
            Eq("refused: no open, no block", ev == GestureEvent.None && !g.Open && !g.BlockInput && tries == 1);
            g.Step(500, true, 500, 500, refuse, kids);
            Eq("refused: asked once", tries == 1);
            g.Step(600, false, 500, 500, refuse, kids);
            Eq("refused: idle after release", !g.Open && !g.BlockInput);

            // throw to E (leaf): Execute(sector 2)
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 560, 500, open, kids);
            Eq("sector E while dragging", g.Sector == 2 && g.Open);
            ev = g.Step(400, false, 560, 500, open, kids);
            Eq("release on a leaf executes", ev == GestureEvent.Execute && g.Sector == 2 && g.ArcItem == -1 && !g.Open);
            Eq("block lasts 2 frames after release", g.BlockInput);
            g.Step(416, false, 560, 500, open, kids); Eq("frame 1 after release", g.BlockInput);
            g.Step(432, false, 560, 500, open, kids); Eq("frame 2 after release", g.BlockInput);
            g.Step(448, false, 560, 500, open, kids); Eq("block off after the release frames", !g.BlockInput);

            // throw beyond the outer radius: the same sector
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 500, 800, open, kids);
            ev = g.Step(400, false, 500, 800, open, kids);
            Eq("long throw to S executes S", ev == GestureEvent.Execute && g.Sector == 4);

            // release in the dead zone: cancel
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 510, 505, open, kids);
            ev = g.Step(400, false, 510, 505, open, kids);
            Eq("dead zone cancels", ev == GestureEvent.Cancel && g.Sector == -1);

            // empty / grey sector: cancel
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 440, 500, open, kids);   // W = 6: empty
            ev = g.Step(400, false, 440, 500, open, kids);
            Eq("empty sector cancels", ev == GestureEvent.Cancel);

            // group without a choice: cancel
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 470, 470, open, kids);   // NW = 7
            ev = g.Step(400, false, 470, 470, open, kids);
            Eq("group released before the arc opens cancels", ev == GestureEvent.Cancel && !g.ArcOpen);

            // group: 180 ms opens the arc, then an item is chosen
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 470, 470, open, kids);
            g.Step(500, true, 470, 470, open, kids); Eq("arc not yet at 150 ms", !g.ArcOpen);
            g.Step(540, true, 470, 470, open, kids); Eq("arc opens after 180 ms", g.ArcOpen);
            double r = 150, ang = (315 + 40) * Math.PI / 180;   // outside the ring, third item
            ev = g.Step(560, true, 500 + r * Math.Sin(ang), 500 - r * Math.Cos(ang), open, kids);
            Eq("arc item under the cursor", g.ArcOpen && g.ArcItem == 2);
            ev = g.Step(600, false, 500 + r * Math.Sin(ang), 500 - r * Math.Cos(ang), open, kids);
            Eq("release on an arc item executes it", ev == GestureEvent.Execute && g.Sector == 7 && g.ArcItem == 2);

            // arc open, then the cursor moves to another sector: arc folds
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 470, 470, open, kids); g.Step(540, true, 470, 470, open, kids);
            g.Step(560, true, 560, 500, open, kids);
            Eq("leaving the arc folds it", !g.ArcOpen && g.Sector == 2);
            ev = g.Step(600, false, 560, 500, open, kids);
            Eq("then the new sector executes", ev == GestureEvent.Execute && g.Sector == 2);

            // arc open, cursor stays inside the ring: release cancels
            g = mk();
            g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
            g.Step(350, true, 470, 470, open, kids); g.Step(540, true, 470, 470, open, kids);
            ev = g.Step(600, false, 470, 470, open, kids);
            Eq("release on the group sector cancels", ev == GestureEvent.Cancel);

            // hotkey: open at once, execute on release
            g = mk();
            g.OpenNow(0, 700, 400);
            Eq("OpenNow opens", g.Open && g.BlockInput && g.CentreX == 700 && g.CentreY == 400);
            g.Step(10, true, 760, 400, open, kids);
            ev = g.Step(50, false, 760, 400, open, kids);
            Eq("OpenNow: execute on release", ev == GestureEvent.Execute && g.Sector == 2);

            // Reset
            g = mk(); g.OpenNow(0, 1, 1); g.Reset();
            Eq("reset", !g.Open && !g.BlockInput);

            // T-328: arc width by cell count, dwell time on a cell, next page
            {
                int st, ct; bool mo;
                ArcPage(4, 0, 6, out st, out ct, out mo); Eq("page: 4 items on one page", st == 0 && ct == 4 && !mo);
                ArcPage(6, 0, 6, out st, out ct, out mo); Eq("page: 6 items fit", st == 0 && ct == 6 && !mo);
                ArcPage(9, 0, 6, out st, out ct, out mo); Eq("page: 9 items, first page 5 + More", st == 0 && ct == 5 && mo);
                ArcPage(9, 1, 6, out st, out ct, out mo); Eq("page: 9 items, second page 4 + More", st == 5 && ct == 4 && mo);
                ArcPage(9, 2, 6, out st, out ct, out mo); Eq("page: wraps to the first", st == 0 && ct == 5 && mo);
                ArcPage(9, -1, 6, out st, out ct, out mo); Eq("page: negative wraps", st == 5 && ct == 4 && mo);
                ArcPage(0, 0, 6, out st, out ct, out mo); Eq("page: empty", ct == 0 && !mo);
                Eq("ellipsize cuts", Ellipsize("Конвой-Север-12", 10) == "Конвой-Се\u2026" && Ellipsize("ПВО-1", 10) == "ПВО-1" && Ellipsize(null, 10) == "" && Ellipsize("1234567890", 10) == "1234567890");
                g = mk(); g.SpanOf = n => 32.0 * n;   // 5 cells -> 160 deg
                g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
                g.Step(350, true, 470, 470, open, kids); g.Step(540, true, 470, 470, open, kids);
                // kids(7) is 3 in this rig: force 5 cells through ResetArc
                g.ResetArc(5, 540);
                Func<double, int> at = a => { double rr = 150, an = a * Math.PI / 180; g.Step(600, true, 500 + rr * Math.Sin(an), 500 - rr * Math.Cos(an), open, kids); return g.ArcItem; };
                Eq("span: 5 cells of 32 deg, centre 315 -> first cell is 235..267", at(315 - 80 + 5) == 0);
                Eq("span: last cell", at(315 + 80 - 5) == 4);
                Eq("span: outside", at(315 + 90) == -1 || g.Sector != 7);
                g = mk();
                g.Step(0, true, 500, 500, open, kids); g.Step(300, true, 500, 500, open, kids);
                g.Step(350, true, 470, 470, open, kids); g.Step(540, true, 470, 470, open, kids);
                double rr2 = 150, an2 = (315 + 40) * Math.PI / 180, x2 = 500 + rr2 * Math.Sin(an2), y2 = 500 - rr2 * Math.Cos(an2);
                Eq("dwell: none before the arc item", g.ArcHoverMs(600) == 0);
                g.Step(600, true, x2, y2, open, kids);
                g.Step(900, true, x2, y2, open, kids);
                Eq("dwell grows on one cell", g.ArcItem == 2 && Math.Abs(g.ArcHoverMs(900) - 300) < 1e-6);
                double an3 = (315 + 0) * Math.PI / 180;
                g.Step(950, true, 500 + rr2 * Math.Sin(an3), 500 - rr2 * Math.Cos(an3), open, kids);
                Eq("dwell resets on another cell", g.ArcItem == 1 && Math.Abs(g.ArcHoverMs(950)) < 1e-6);
                g.ResetArc(5, 1000);
                Eq("ResetArc: arc open, nothing hovered", g.ArcOpen && g.ArcItem == -1 && g.ArcHoverMs(1100) == 0);
                g.Step(1100, true, 500 + rr2 * Math.Sin(an2), 500 - rr2 * Math.Cos(an2), open, kids);
                Eq("ResetArc: cells counted by the new count", g.ArcOpen && g.ArcItem >= 0);
                var g0 = mk(); g0.ResetArc(5, 0);
                Eq("ResetArc on a closed arc does nothing", !g0.ArcOpen);
            }
        }
    }

    /// <summary>Hit tests of the target unit and the ring centre (pure).</summary>
    internal static class RadialPick
    {
        /// <summary>3D: the cursor is within radiusPx of the unit's screen point, or inside the model's screen rectangle (x,y,w,h). z &lt;= 0: behind the camera.</summary>
        public static bool Hit3D(double mx, double my, double ux, double uy, double uz, double[] rect, double radiusPx)
        {
            if (uz <= 0) return false;
            double dx = mx - ux, dy = my - uy;
            if (dx * dx + dy * dy <= radiusPx * radiusPx) return true;
            return rect != null && mx >= rect[0] && mx <= rect[0] + rect[2] && my >= rect[1] && my <= rect[1] + rect[3];
        }

        /// <summary>3D target: the game's own ray hit the selected unit, else the old radius/rectangle rule (Hit3D).</summary>
        public static bool Target3D(bool rayHitSelected, double mx, double my, double ux, double uy, double uz, double[] rect, double radiusPx)
        {
            return rayHitSelected || Hit3D(mx, my, ux, uy, uz, rect, radiusPx);
        }

        /// <summary>Map target: the selected unit's icon is under the cursor (hit test), else the cursor is within radiusPx of the unit's map point.</summary>
        public static bool TargetMap(bool iconIsSelected, bool pointOk, double mx, double my, double ux, double uy, double radiusPx)
        {
            if (iconIsSelected) return true;
            if (!pointOk) return false;
            double dx = mx - ux, dy = my - uy;
            return dx * dx + dy * dy <= radiusPx * radiusPx;
        }

        /// <summary>Screen rectangle (x,y,w,h; y down) of the projected corners with z &gt; 0; corners behind the camera are skipped; null when none is in front.</summary>
        public static double[] RectOfCorners(double[] xs, double[] ys, double[] zs)
        {
            if (xs == null || ys == null || zs == null || xs.Length != ys.Length || xs.Length != zs.Length) return null;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            bool any = false;
            for (int i = 0; i < xs.Length; i++)
            {
                if (zs[i] <= 0) continue;
                any = true;
                if (xs[i] < x0) x0 = xs[i];
                if (xs[i] > x1) x1 = xs[i];
                if (ys[i] < y0) y0 = ys[i];
                if (ys[i] > y1) y1 = ys[i];
            }
            return any ? new[] { x0, y0, x1 - x0, y1 - y0 } : null;
        }

        /// <summary>Renderers that outline the hull (exact type name). Particles, trails, lines, billboards - no.</summary>
        public static bool ModelRenderer(string typeName)
        {
            return typeName == "MeshRenderer" || typeName == "SkinnedMeshRenderer";
        }

        /// <summary>Ring centre: the unit's screen point when it is within maxDist of the press, else the press point.</summary>
        public static void Centre(double pressX, double pressY, double ux, double uy, bool unitOk, double maxDist, out double cx, out double cy)
        {
            double dx = pressX - ux, dy = pressY - uy;
            if (unitOk && dx * dx + dy * dy <= maxDist * maxDist) { cx = ux; cy = uy; }
            else { cx = pressX; cy = pressY; }
        }
    }

    /// <summary>One diagnostics line per failed pick attempt (config Radial.Debug). Numbers: invariant culture, 1 decimal; missing values "-".</summary>
    internal sealed class RadialPickInfo
    {
        public string View, Camera, RayFirst, MapType, MapHitVm;
        public double Mx, My, Ux, Uy, Uz, MapX, MapY, RadiusPx;
        public int CamW, CamH, ScreenW, ScreenH, RayHits;
        public bool UnitOk, RayHitSelected, OnSel, MapPointOk;
        public double[] Rect;

        private static string N(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string S(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            return s.Length > max ? s.Substring(0, max) : s;
        }
        private static string B(bool b) { return b ? "true" : "false"; }

        public string Format()
        {
            string unit = UnitOk ? N(Ux) + "," + N(Uy) : "-";
            string z = UnitOk ? N(Uz) : "-";
            string rect = Rect != null && Rect.Length >= 4 ? N(Rect[0]) + "," + N(Rect[1]) + " " + N(Rect[2]) + "x" + N(Rect[3]) : "-";
            string mp = MapPointOk ? N(MapX) + "," + N(MapY) : "-";
            string r = "radial pick: view=" + S(View, 40) + " cursor=" + N(Mx) + "," + N(My) + " unit=" + unit + " z=" + z + " rect=" + rect
                + " r=" + N(RadiusPx) + " ray=" + RayHits.ToString(CultureInfo.InvariantCulture) + " ray_sel=" + B(RayHitSelected) + " ray_first=" + S(RayFirst, 40)
                + " cam=" + S(Camera, 40) + " " + CamW.ToString(CultureInfo.InvariantCulture) + "x" + CamH.ToString(CultureInfo.InvariantCulture)
                + " screen=" + ScreenW.ToString(CultureInfo.InvariantCulture) + "x" + ScreenH.ToString(CultureInfo.InvariantCulture)
                + " map=" + S(MapType, 40) + " map_vm=" + S(MapHitVm, 40) + " map_pt=" + mp + " on_sel=" + B(OnSel);
            return r.Length > 400 ? r.Substring(0, 400) : r;
        }
    }

    /// <summary>T-312: which ring refusals reach the log. A new reason always; the same one again only in debug or after RepeatS (with the suppressed count).</summary>
    internal sealed class RadialRefusalLog
    {
        public const double RepeatS = 10.0;
        private string _why; private double _at; private int _count;

        public string Next(string why, double nowS, bool debug)
        {
            if (why != _why) { _why = why; _at = nowS; _count = 0; return "radial not opened: " + why; }
            _count++;
            if (debug) { _at = nowS; _count = 0; return "radial not opened (again): " + why; }
            if (nowS - _at >= RepeatS)
            {
                int n = _count; _at = nowS; _count = 0;
                return "radial not opened: " + why + " (x" + n.ToString(CultureInfo.InvariantCulture) + ")";
            }
            return null;
        }

        public void Clear() { _why = null; _count = 0; }
    }

    /// <summary>
    /// Left-button hold gesture of the ring (no Unity types, time is passed in). Idle -> Pressed -> (Active | Ignored) -> Idle.
    /// A short click or a drag never opens it, so the game keeps those events. While the ring is open, and for a couple of frames
    /// after the release, BlockInput is true.
    /// </summary>
    internal sealed class RadialGesture
    {
        public double HoldMs, TolPx, SubmenuMs, DeadR, OuterR, HystDeg, ArcSpanDeg = 120;
        /// <summary>T-328: arc width in degrees for a number of visible cells (null = ArcSpanDeg).</summary>
        public Func<int, double> SpanOf;
        public int ReleaseFrames;
        /// <summary>Number of ring sectors (T-152), set by the caller when the ring is built.</summary>
        public int SectorCount = 8;
        private enum St { Idle, Pressed, Ignored, Active }
        private St _st = St.Idle;
        private double _t0, _px, _py, _hoverT, _arcHoverT;
        private int _releaseLeft, _sector = -1, _arcItem = -1, _arcSector = -1, _arcCount, _hover = -1;
        private bool _arcOpen;
        private double _cx, _cy;

        public RadialGesture(double holdMs, double tolPx, double submenuMs, double deadR, double outerR, double hystDeg, int releaseFrames)
        {
            HoldMs = holdMs; TolPx = tolPx; SubmenuMs = submenuMs; DeadR = deadR; OuterR = outerR; HystDeg = hystDeg; ReleaseFrames = releaseFrames;
        }

        public bool Open { get { return _st == St.Active; } }
        public bool BlockInput { get { return _st == St.Active || _releaseLeft > 0; } }
        public int Sector { get { return _sector; } }
        public int ArcItem { get { return _arcItem; } }
        public bool ArcOpen { get { return _arcOpen; } }

        /// <summary>T-328: how long the cursor has stayed on the current arc cell (0 = none under the cursor / just changed).</summary>
        public double ArcHoverMs(double tMs) { return _arcOpen && _arcItem >= 0 ? Math.Max(0, tMs - _arcHoverT) : 0; }

        /// <summary>T-328: the arc content changed (next page): new cell count, nothing under the cursor, the dwell timer restarts. The arc stays open.</summary>
        public void ResetArc(int count, double tMs) { if (!_arcOpen) return; _arcCount = count; _arcItem = -1; _arcHoverT = tMs; }
        public double CentreX { get { return _cx; } }
        public double CentreY { get { return _cy; } }

        public void Reset()
        {
            _st = St.Idle; _releaseLeft = 0; _sector = -1; _arcItem = -1; _arcOpen = false; _hover = -1;
        }

        /// <summary>Opens at once (hotkey): the ring lives until the button/key is released.</summary>
        public void OpenNow(double tMs, double cx, double cy)
        {
            Reset();
            _st = St.Active; _cx = cx; _cy = cy; _hoverT = tMs;
        }

        public GestureEvent Step(double tMs, bool held, double mx, double my, Func<double, double, double[]> tryOpen, Func<int, int> childCount)
        {
            if (_releaseLeft > 0 && _st != St.Active) _releaseLeft--;
            switch (_st)
            {
                case St.Idle:
                    if (held) { _st = St.Pressed; _t0 = tMs; _px = mx; _py = my; }
                    return GestureEvent.None;
                case St.Ignored:
                    if (!held) _st = St.Idle;
                    return GestureEvent.None;
                case St.Pressed:
                    {
                        if (!held) { _st = St.Idle; return GestureEvent.None; }
                        double dx = mx - _px, dy = my - _py;
                        if (dx * dx + dy * dy > TolPx * TolPx) { _st = St.Ignored; return GestureEvent.None; }
                        if (tMs - _t0 < HoldMs) return GestureEvent.None;
                        var c = tryOpen(_px, _py);
                        if (c == null) { _st = St.Ignored; return GestureEvent.None; }
                        _st = St.Active; _cx = c[0]; _cy = c[1];
                        _sector = -1; _arcItem = -1; _arcOpen = false; _hover = -1; _hoverT = tMs;
                        return GestureEvent.Open;
                    }
                default:
                    return StepActive(tMs, held, mx, my, childCount);
            }
        }

        private GestureEvent StepActive(double tMs, bool held, double mx, double my, Func<int, int> childCount)
        {
            double dx = mx - _cx, dy = my - _cy, dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < DeadR) { _sector = -1; _hover = -1; _arcOpen = false; _arcItem = -1; }
            else
            {
                int prev = _sector;
                int s = RadialMath.SectorOf(dx, dy, prev, HystDeg, SectorCount);
                bool folded = false;
                if (_arcOpen)
                {
                    double span = SpanOf != null ? SpanOf(_arcCount) : ArcSpanDeg;
                    int it = dist > OuterR ? RadialMath.ArcItemOfAt(dx, dy, RadialMath.SectorCentre(_arcSector, SectorCount), _arcCount, span, _arcItem) : -1;
                    if (it != _arcItem) _arcHoverT = tMs;
                    if (it >= 0) { _arcItem = it; s = _arcSector; }
                    else if (s != _arcSector) { _arcOpen = false; _arcItem = -1; folded = true; }
                    else _arcItem = -1;
                }
                _sector = s;
                if (s != _hover || folded) { _hover = s; _hoverT = tMs; }
                if (!_arcOpen && tMs - _hoverT >= SubmenuMs)
                {
                    int n = childCount(s);
                    if (n > 0) { _arcOpen = true; _arcSector = s; _arcCount = n; _arcItem = -1; _arcHoverT = tMs; }
                }
            }
            if (held) return GestureEvent.None;
            // release
            GestureEvent ev;
            if (_arcOpen && _arcItem >= 0) ev = GestureEvent.Execute;
            else if (_sector >= 0 && !_arcOpen && childCount(_sector) == 0) ev = GestureEvent.Execute;
            else ev = GestureEvent.Cancel;
            _st = St.Idle; _releaseLeft = ReleaseFrames + 1;   // the release frame itself plus ReleaseFrames more
            return ev;
        }
    }
}
