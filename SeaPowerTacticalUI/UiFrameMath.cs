// T-148 U2: what the window frame computes without Unity (width clamp, keeping on screen, body height, the hint line) + shared prefs.
using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    internal static class UiFrameMath
    {
        /// <summary>Window width limits are given at scale 1 and multiplied by s.</summary>
        public static float ClampWidth(float w, float minW, float maxW, double s)
        {
            float lo = (float)(minW * s), hi = (float)(maxW * s);
            if (hi < lo) hi = lo;
            return w < lo ? lo : w > hi ? hi : w;
        }

        public static void ClampOnScreen(ref float x, ref float y, float w, float h, float screenW, float screenH)
        {
            float maxX = screenW - w, maxY = screenH - h;
            x = maxX <= 0 ? 0 : Math.Max(0, Math.Min(x, maxX));
            y = maxY <= 0 ? 0 : Math.Max(0, Math.Min(y, maxY));
        }

        /// <summary>Body height: the content height, but the whole window stays within 85 % of the screen (chrome = title + hint line).</summary>
        public static float BodyHeight(float contentH, float screenH, out bool scroll, float chrome = 0f)
        {
            float max = Math.Max(40f, 0.85f * screenH - chrome);
            scroll = contentH > max;
            return scroll ? max : contentH;
        }

        /// <summary>T-174 W2 (ADR-0073 section 1.6): the body is not higher than the room below the window top (min(0.85 screen, screen - y) - chrome), scrolling when
        /// the content is taller, so a window growing at the bottom keeps its y. When that room is under minBodyH the old rule applies (the window moves up).</summary>
        public static float BodyHeightAt(float contentH, float y, float screenH, out bool scroll, float chrome, float minBodyH)
        {
            float room = Math.Min(0.85f * screenH, screenH - Math.Max(0f, y)) - chrome;
            if (contentH <= room) { scroll = false; return contentH; }
            if (room >= minBodyH && room >= 40f) { scroll = true; return room; }
            return BodyHeight(contentH, screenH, out scroll, chrome);
        }

        /// <summary>The smallest body worth keeping at a window top: min(content, 30 % of the screen).</summary>
        public static float MinBodyHeight(float contentH, float screenH) { return Math.Min(contentH, 0.30f * screenH); }

        /// <summary>The hint line: a fresh command answer (note, noteUntil) wins, then the tooltip under the mouse, then the warnings.
        /// (The documented order is tooltip first; but the mouse stays on the button that was just clicked, which would hide its answer.)</summary>
        public static string HintLine(string tooltip, string note, double noteUntil, double now, IList<string> warnings, out UiTone tone, UiTone noteTone = UiTone.Normal)
        {
            if (!string.IsNullOrEmpty(note) && now < noteUntil) { tone = noteTone; return note; }
            if (!string.IsNullOrEmpty(tooltip)) { tone = UiTone.Dim; return tooltip; }
            if (warnings != null && warnings.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (string w in warnings) { if (string.IsNullOrEmpty(w)) continue; if (sb.Length > 0) sb.Append(" · "); sb.Append(w); }
                if (sb.Length > 0) { tone = UiTone.Warn; return sb.ToString(); }
            }
            tone = UiTone.Dim;
            return null;
        }

        /// <summary>T-154: a tooltip of a control counts only while the cursor is inside the window (the IMGUI tooltip stays stale otherwise: "hold LMB... Esc cancels" hung in the hint line).</summary>
        /// <summary>Live finding 0.27.2: the window widens by itself when its tables ask for more (want = window width they need, 0 = no wish), up to maxW.
        /// It only grows (no jitter when a long row comes and goes) and respects a user resize: after the user set the width it grows again only when the
        /// content asks for more than it did at that moment (wantAtResize).</summary>
        public static bool AutoGrow(float w, float want, float wantAtResize, float minW, float maxW, double s, out float grown)
        {
            grown = w;
            // 0.28.1: a wish under half a pixel above the width also grows (live "ui overflow asw members.4 need=188 have=187": the old
            // 0.5 px dead band left the content-fit column cut by a fraction of a pixel for good)
            if (want <= w + 0.01f || want <= wantAtResize + 0.5f) return false;
            float g = ClampWidth((float)Math.Ceiling(want), minW, maxW, s);
            if (g <= w + 0.01f) return false;
            grown = g;
            return true;
        }

        /// <summary>T-233: shifts a window {x, y, w, h} (screen px, y down) off the obstacles. Order: in place ("none"); down below the lowest crossed one + gap
        /// while it fits the screen ("below"); left of the leftmost crossed one ("left"); right of the rightmost ("right"); else in place ("blocked").
        /// Returns true if x/y changed. Self-contained: compiled in stands without the coverage code.</summary>
        public static bool AvoidRect(ref float x, ref float y, float w, float h, IList<double[]> obstacles, float screenW, float screenH, float gap, out string why)
        {
            why = "none";
            var obs = new List<double[]>();
            if (obstacles != null)
                foreach (var o in obstacles)
                    if (o != null && o.Length >= 4 && !double.IsNaN(o[0] + o[1] + o[2] + o[3]) && !double.IsInfinity(o[0] + o[1] + o[2] + o[3])) obs.Add(o);
            if (obs.Count == 0 || Crossed(x, y, w, h, obs) == null) return false;
            float cx = x, cy = y;
            for (int g = 0; g < 16; g++)
            {
                var hit = Crossed(cx, cy, w, h, obs);
                if (hit == null) { if (cy + h <= screenH) { x = cx; y = cy; why = "below"; return true; } break; }
                double bottom = double.NegativeInfinity;
                foreach (var o in hit) bottom = Math.Max(bottom, o[1] + o[3]);
                cy = (float)(bottom + gap);
                if (cy + h > screenH) break;
            }
            cx = x; cy = y;
            for (int g = 0; g < 16; g++)
            {
                var hit = Crossed(cx, cy, w, h, obs);
                if (hit == null) { x = cx; why = "left"; return true; }
                double left = double.PositiveInfinity;
                foreach (var o in hit) left = Math.Min(left, o[0]);
                cx = (float)(left - gap - w);
                if (cx < 0) break;
            }
            cx = x;
            for (int g = 0; g < 16; g++)
            {
                var hit = Crossed(cx, cy, w, h, obs);
                if (hit == null) { x = cx; why = "right"; return true; }
                double right = double.NegativeInfinity;
                foreach (var o in hit) right = Math.Max(right, o[0] + o[2]);
                cx = (float)(right + gap);
                if (cx + w > screenW) break;
            }
            why = "blocked";
            return false;
        }

        private static List<double[]> Crossed(float x, float y, float w, float h, List<double[]> obs)
        {
            List<double[]> r = null;
            foreach (var o in obs)
                if (x < o[0] + o[2] && x + w > o[0] && y < o[1] + o[3] && y + h > o[1]) (r ?? (r = new List<double[]>())).Add(o);
            return r;
        }

        public static string HintTooltip(string tooltip, bool mouseInside)
        {
            return mouseInside && !string.IsNullOrEmpty(tooltip) ? tooltip : null;
        }

        public static string SelfTest()
        {
            string fail = null;
            Action<string, bool> eq = (n, ok) => { if (!ok && fail == null) fail = n; };
            eq("width min", UiFrameMath.ClampWidth(100, 500, 760, 1.0) == 500);
            eq("width max", UiFrameMath.ClampWidth(2000, 500, 760, 1.0) == 760);
            eq("width keep", UiFrameMath.ClampWidth(600, 500, 760, 1.0) == 600);
            eq("width scaled", Math.Abs(UiFrameMath.ClampWidth(100, 500, 760, 1.33) - 665f) < 0.01 && Math.Abs(UiFrameMath.ClampWidth(5000, 500, 760, 1.33) - 1010.8f) < 0.01);
            float gw;
            eq("grow to want", UiFrameMath.AutoGrow(500, 640.2f, 0, 420, 760, 1.0, out gw) && gw == 641f);
            eq("grow capped", UiFrameMath.AutoGrow(500, 2000, 0, 420, 760, 1.0, out gw) && gw == 760f);
            eq("no grow when it fits", !UiFrameMath.AutoGrow(700, 650, 0, 420, 760, 1.0, out gw) && gw == 700f);
            eq("grow by a fraction of a pixel", UiFrameMath.AutoGrow(500, 500.3f, 0, 420, 760, 1.0, out gw) && gw == 501f);
            eq("no grow at the exact width", !UiFrameMath.AutoGrow(501, 501f, 0, 420, 760, 1.0, out gw));
            eq("no grow at max", !UiFrameMath.AutoGrow(760, 900, 0, 420, 760, 1.0, out gw));
            eq("no grow no wish", !UiFrameMath.AutoGrow(500, 0, 0, 420, 760, 1.0, out gw));
            eq("user narrowed: same wish keeps it", !UiFrameMath.AutoGrow(450, 640, 640, 420, 760, 1.0, out gw) && gw == 450f);
            eq("user narrowed: a bigger wish grows", UiFrameMath.AutoGrow(450, 700, 640, 420, 760, 1.0, out gw) && gw == 700f);
            eq("grow scaled", UiFrameMath.AutoGrow(600, 2000, 0, 420, 760, 1.33, out gw) && Math.Abs(gw - 1010.8f) < 0.01);
            float x = -50, y = 2000;
            UiFrameMath.ClampOnScreen(ref x, ref y, 600, 400, 1920, 1080);
            eq("on screen", x == 0 && y == 680);
            x = 1800; y = 10;
            UiFrameMath.ClampOnScreen(ref x, ref y, 600, 400, 1920, 1080);
            eq("on screen right", x == 1320 && y == 10);
            x = 5; y = 5;
            UiFrameMath.ClampOnScreen(ref x, ref y, 3000, 2000, 1920, 1080);
            eq("bigger than screen", x == 0 && y == 0);
            bool sc;
            float bh = UiFrameMath.BodyHeight(400, 1080, out sc);
            eq("body fits", bh == 400 && !sc);
            bh = UiFrameMath.BodyHeight(2000, 1080, out sc);
            eq("body 85 %", sc && Math.Abs(bh - 918f) < 0.01);
            bh = UiFrameMath.BodyHeight(2000, 1080, out sc, 60);
            eq("body chrome", sc && Math.Abs(bh - 858f) < 0.01);
            // T-174 W2: the window at the bottom keeps its y and scrolls
            float mb = UiFrameMath.MinBodyHeight(800, 1080);
            eq("min body", Math.Abs(mb - 324f) < 0.01f && UiFrameMath.MinBodyHeight(200, 1080) == 200f);
            bh = UiFrameMath.BodyHeightAt(800, 100, 1080, out sc, 60, mb);
            eq("at: fits below", bh == 800 && !sc);
            bh = UiFrameMath.BodyHeightAt(800, 600, 1080, out sc, 60, mb);
            eq("at: window low on the screen keeps y and scrolls", sc && Math.Abs(bh - 420f) < 0.01f);
            bh = UiFrameMath.BodyHeightAt(800, 700, 1080, out sc, 60, mb);
            eq("at: under minBodyH the old rule (window moves up)", !sc && bh == 800f);
            bh = UiFrameMath.BodyHeightAt(2000, 950, 1080, out sc, 60, UiFrameMath.MinBodyHeight(2000, 1080));
            eq("at: no room, old rule scrolls at 85 %", sc && Math.Abs(bh - 858f) < 0.01f);
            bh = UiFrameMath.BodyHeightAt(2000, 0, 1080, out sc, 60, UiFrameMath.MinBodyHeight(2000, 1080));
            eq("at: top window is the 85 % rule", sc && Math.Abs(bh - 858f) < 0.01f);
            // T-190: a sticky footer is part of the chrome, so it stays on the screen
            bh = UiFrameMath.BodyHeightAt(2000, 40, 1080, out sc, 24 + 24 + 52, UiFrameMath.MinBodyHeight(2000, 1080));
            eq("at: footer chrome keeps the footer on the screen", sc && 40 + 24 + bh + 52 + 24 <= 1080 + 0.01f);
            UiTone t;
            var warn = new List<string> { "w1", "w2" };
            eq("note first", UiFrameMath.HintLine("tip", "note", 10, 5, warn, out t, UiTone.Error) == "note" && t == UiTone.Error);
            eq("tip after note expired", UiFrameMath.HintLine("tip", "note", 10, 11, warn, out t) == "tip" && t == UiTone.Dim);
            eq("warnings last", UiFrameMath.HintLine(null, null, 0, 0, warn, out t) == "w1 · w2" && t == UiTone.Warn);
            eq("tooltip inside", UiFrameMath.HintTooltip("tip", true) == "tip");
            eq("tooltip outside", UiFrameMath.HintTooltip("tip", false) == null && UiFrameMath.HintTooltip("", true) == null);
            eq("empty", UiFrameMath.HintLine("", "", 0, 0, null, out t) == null && t == UiTone.Dim);
            // T-233: AvoidRect
            {
                string aw; float ax = 1554, ay = 83;
                var ov = new List<double[]> { new double[] { 1344, 86, 445, 443 } };
                eq("avoid below", UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, ov, 1920, 1080, 8, out aw) && aw == "below" && ax == 1554 && Math.Abs(ay - (86 + 443 + 8)) < 0.01);
                ax = 1554; ay = 83;
                ov = new List<double[]> { new double[] { 1344, 86, 445, 990 } };
                eq("avoid left", UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, ov, 1920, 1080, 8, out aw) && aw == "left" && ay == 83 && Math.Abs(ax - (1344 - 8 - 360)) < 0.01);
                ax = 1554; ay = 83;
                ov = new List<double[]> { new double[] { 1344, 86, 445, 990 }, new double[] { 0, 0, 1330, 1080 } };
                eq("avoid blocked", !UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, ov, 1920, 1080, 8, out aw) && aw == "blocked" && ax == 1554 && ay == 83);
                ax = 100; ay = 83;
                ov = new List<double[]> { new double[] { 0, 0, 90, 1080 }, new double[] { 100, 0, 600, 1080 } };
                eq("avoid right", UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, ov, 1920, 1080, 8, out aw) && aw == "right" && Math.Abs(ax - 708) < 0.01 && ay == 83);
                ax = 100; ay = 100;
                ov = new List<double[]> { new double[] { 1344, 86, 445, 443 } };
                eq("avoid none", !UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, ov, 1920, 1080, 8, out aw) && aw == "none" && ax == 100 && ay == 100);
                ax = 1554; ay = 83;
                ov = new List<double[]> { new double[] { 1344, 86, 445, 200 }, new double[] { 1344, 286, 445, 200 } };
                eq("avoid two stacked", UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, ov, 1920, 1080, 8, out aw) && aw == "below" && Math.Abs(ay - 494) < 0.01);
                ax = 1554; ay = 83;
                ov = new List<double[]> { new double[] { double.NaN, 0, 10, 10 }, null };
                eq("avoid ignores NaN", !UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, ov, 1920, 1080, 8, out aw) && aw == "none");
            }
            string gm = GameMenuMath.SelfTest();
            eq("game menu: " + gm, gm == null);
            string gp = GamePopupMath.SelfTest();
            eq("game popup: " + gp, gp == null);
            return fail;
        }
    }

    /// <summary>Settings shared by every mod window; TextFeature.Bind fills them, stands keep the defaults.</summary>
    internal static class UiWindowPrefs
    {
        public static double ScaleOverride;                              // [Ui] Scale, 0 = auto
        public static UiDensity Density = UiDensity.Compact;             // [Ui] Density (T-155 K4): compact | normal; the window frame rebuilds its theme when it changes
        /// <summary>T-240: set each frame by MapMenuFeature; never null (Open = false when nothing is open).</summary>
        public static GamePopupState Popup = new GamePopupState();
        /// <summary>T-240: the window (screen rect) lies under an open game popup and must not draw nor take the mouse.</summary>
        public static bool PopupCovers(double x, double y, double w, double h) { return GamePopupMath.Covers(Popup, x, y, w, h, 8.0); }
        public static bool LoadingHidden;                                // the game shows a loading screen: mod windows are not drawn (set each frame by SettingsFeature)
        public static bool MenuHidden;                                   // a game menu is open (pause / options / confirm / briefing / AAR): mod windows are not drawn (set each frame by SettingsFeature)
        /// <summary>The window frame neither draws nor holds the mouse block.</summary>
        public static bool Hidden { get { return LoadingHidden || MenuHidden; } }
        public static Func<double> GameUiScale = () => 1.0;
        public static UiWindowStateStore State;                          // null = nothing is remembered

        public static double ScaleFor(int screenH) { return RadialMath.Scale(GameUiScale(), screenH, ScaleOverride); }
    }

    /// <summary>
    /// The game's own menus over a mission (live finding 0.27.x): the Esc pause menu with its Options screen and confirmations, the briefing,
    /// and the After Action Report (MissionManager.ExitToMainMenu shows MissionDebriefView). The mod windows hide, the mouse block is lifted and
    /// the map render texture / overlay pumps pause while one is open. Pure: GameApi.PumpGameMenu reads the game, this decides.
    /// </summary>
    internal static class GameMenuMath
    {
        public const string None = "none", Menu = "menu", Debrief = "debrief", Message = "message";

        /// <summary>menuOpen = InputHandler.menuOpen; debrief = the game's menu page is the After Action Report (it wins: the AAR keeps menuOpen too);
        /// message = a mission message popup is up (0.28.1, live: the mod windows covered "MISSION ACCOMPLISHED / Victory"): InputHandler.IsInMessagePauseMode
        /// (RichMissionMessages / MissionManager set it with the pausing popup) or a SeapowerUI.Modal in the game's window host.</summary>
        public static string State(bool menuOpen, bool debrief, bool message = false) { return debrief ? Debrief : (menuOpen ? Menu : (message ? Message : None)); }

        public static bool Hides(string state) { return state == Menu || state == Debrief || state == Message; }

        /// <summary>One log line on a change of the state, null when it did not change.</summary>
        public static string LogLine(string prev, string cur)
        {
            if (prev == cur) return null;
            if (!Hides(cur)) return "game menu closed: mod windows, mouse block and map layers back";
            return "game menu " + cur + ": mod windows hidden, mouse block lifted, map layers paused";
        }

        public static string SelfTest()
        {
            if (State(false, false) != None || State(true, false) != Menu || State(true, true) != Debrief || State(false, true) != Debrief) return "state";
            if (State(false, false, true) != Message || State(true, false, true) != Menu || State(false, true, true) != Debrief) return "state message";
            if (Hides(None) || !Hides(Menu) || !Hides(Debrief) || !Hides(Message) || Hides(null)) return "hides";
            string ml = LogLine(None, Message);
            if (ml == null || !ml.Contains("message") || !ml.Contains("hidden")) return "message line: " + ml;
            if (LogLine(None, None) != null || LogLine(Menu, Menu) != null) return "no change must be silent";
            string l = LogLine(None, Debrief);
            if (l == null || !l.Contains("debrief") || !l.Contains("hidden")) return "debrief line: " + l;
            l = LogLine(Menu, None);
            if (l == null || !l.Contains("closed")) return "closed line: " + l;
            return null;
        }
    }

    /// <summary>T-240: an open game popup (map hamburger / context menu). Rect = screen px, y down; null = open but the rectangle is unknown (covers all).</summary>
    internal sealed class GamePopupState
    {
        public bool Open;
        public double[] Rect;      // x, y, w, h or null
        public string Source;      // "map_menu" | "context_menu" | "map_menu+context_menu"
    }

    internal static class GamePopupMath
    {
        private static bool Valid(double[] r)
        {
            if (r == null || r.Length < 4) return false;
            double sum = r[0] + r[1] + r[2] + r[3];
            return !double.IsNaN(sum) && !double.IsInfinity(sum) && r[2] > 0 && r[3] > 0;
        }

        /// <summary>Union of the rectangles (NaN / non-positive sizes skipped); null if none is valid.</summary>
        public static double[] Union(IList<double[]> rects)
        {
            if (rects == null) return null;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            bool any = false;
            foreach (var r in rects)
            {
                if (!Valid(r)) continue;
                any = true;
                x0 = Math.Min(x0, r[0]); y0 = Math.Min(y0, r[1]);
                x1 = Math.Max(x1, r[0] + r[2]); y1 = Math.Max(y1, r[1] + r[3]);
            }
            return any ? new[] { x0, y0, x1 - x0, y1 - y0 } : null;
        }

        /// <summary>A window rectangle must yield: open && (rect == null || the two intersect after inflating the popup by pad px).</summary>
        public static bool Covers(GamePopupState s, double x, double y, double w, double h, double pad)
        {
            if (s == null || !s.Open) return false;
            var r = s.Rect;
            if (!Valid(r)) return true;
            return x < r[0] + r[2] + pad && x + w > r[0] - pad && y < r[1] + r[3] + pad && y + h > r[1] - pad;
        }

        /// <summary>T-247: seconds the menu popup stays "open" after the game's flag dropped (the live flag flickers 0.3-2 s while the menu is on screen).</summary>
        public const double HoldS = 1.5;

        /// <summary>Hysteresis on closing: true while raw is true or raw went false less than hold s ago. lastOpenAt is updated while raw.</summary>
        public static bool Hold(bool raw, double now, ref double lastOpenAt, double hold)
        {
            if (raw) { lastOpenAt = now; return true; }
            return now - lastOpenAt < hold;
        }

        /// <summary>One log line on a change of Open / Source (rect changes are silent), null when nothing changed.</summary>
        public static string LogLine(GamePopupState prev, GamePopupState cur)
        {
            bool po = prev != null && prev.Open, co = cur != null && cur.Open;
            string ps = po ? prev.Source : null, cs = co ? cur.Source : null;
            if (po == co && ps == cs) return null;
            if (!co) return "game popup closed: mod windows back";
            return "game popup " + cs + ": mod windows under it yield";
        }

        /// <summary>T-262: why OpenMenuRaw returned false; null when it is open. Order: menu_null -> mode -> not_loaded -> flag_off.</summary>
        public static string DropReason(bool menuAlive, bool modeMenu, bool loaded, bool flag)
        {
            if (!menuAlive) return "menu_null";
            if (!modeMenu) return "mode";
            if (!loaded) return "not_loaded";
            if (!flag) return "flag_off";
            return null;
        }

        /// <summary>T-262: the "closed" line with the drop reason and its length; reason null -> the T-240 line unchanged.</summary>
        public static string ClosedLine(string reason, double gapS)
        {
            const string basic = "game popup closed: mod windows back";
            if (reason == null) return basic;
            return basic + " (reason=" + reason + " gap_s=" + gapS.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        public static string SelfTest()
        {
            if (DropReason(false, true, true, true) != "menu_null") return "drop: menu_null";
            if (DropReason(true, true, true, false) != "flag_off") return "drop: flag_off";
            if (DropReason(true, true, true, true) != null) return "drop: open is null";
            if (ClosedLine("menu_null", 1.64) != "game popup closed: mod windows back (reason=menu_null gap_s=1.6)") return "closed line: reason";
            if (ClosedLine(null, 3) != "game popup closed: mod windows back") return "closed line: plain";
            var u = Union(new List<double[]> { new double[] { 10, 10, 20, 20 }, new double[] { 50, 5, 10, 10 }, new double[] { double.NaN, 0, 5, 5 }, new double[] { 0, 0, 0, 5 }, null });
            if (u == null || u[0] != 10 || u[1] != 5 || u[2] != 50 || u[3] != 25) return "union";
            if (Union(new List<double[]> { new double[] { 0, 0, 0, 0 } }) != null || Union(new List<double[]>()) != null || Union(null) != null) return "union empty";
            var closed = new GamePopupState();
            if (Covers(closed, 0, 0, 10, 10, 4)) return "closed";
            var unknown = new GamePopupState { Open = true, Source = "map_menu" };
            if (!Covers(unknown, 0, 0, 10, 10, 4)) return "unknown rect covers all";
            var o = new GamePopupState { Open = true, Source = "map_menu", Rect = new double[] { 100, 100, 50, 50 } };
            if (!Covers(o, 120, 120, 10, 10, 4)) return "inside";
            if (!Covers(o, 152, 100, 10, 10, 4)) return "pad neighbour";
            if (Covers(o, 160, 100, 10, 10, 4)) return "far";
            if (Covers(o, 100, 160, 10, 10, 4)) return "far below";
            var cur = new GamePopupState { Open = true, Source = "map_menu", Rect = new double[] { 1, 1, 5, 5 } };
            string l = LogLine(closed, cur);
            if (l == null || !l.Contains("map_menu") || !l.Contains("yield")) return "open line: " + l;
            if (LogLine(o, cur) != null) return "rect change must be silent";
            l = LogLine(cur, new GamePopupState { Open = true, Source = "map_menu+context_menu" });
            if (l == null || !l.Contains("context_menu")) return "source line: " + l;
            l = LogLine(cur, closed);
            if (l == null || !l.Contains("closed")) return "closed line: " + l;
            if (LogLine(closed, new GamePopupState()) != null) return "closed silent";
            // T-247: a short drop of the game flag does not close the popup, a long one does
            double lo = double.NegativeInfinity;
            if (Hold(false, 10, ref lo, HoldS)) return "hold: never opened";
            if (!Hold(true, 10, ref lo, HoldS) || lo != 10) return "hold: raw open";
            if (!Hold(false, 10.8, ref lo, HoldS)) return "hold: flicker 0.8 s must stay open";
            if (!Hold(false, 11.4, ref lo, HoldS)) return "hold: 1.4 s must stay open";
            if (Hold(false, 11.6, ref lo, HoldS)) return "hold: 1.6 s must close";
            if (!Hold(true, 12, ref lo, HoldS) || !Hold(false, 12.2, ref lo, HoldS)) return "hold: reopen";
            // T-240: the Messages stack (360x200 at the right edge) avoids both the Overview and the radar legend
            float ax = 1554, ay = 83; string why;
            var obs = new List<double[]> { new double[] { 1554, 83, 360, 443 }, new double[] { 1642, 438, 270, 152 } };
            if (!UiFrameMath.AvoidRect(ref ax, ref ay, 360, 200, obs, 1920, 1080, 8, out why)) return "legend avoid moved: " + why;
            foreach (var r in obs)
                if (ax < r[0] + r[2] && ax + 360 > r[0] && ay < r[1] + r[3] && ay + 200 > r[1]) return "legend avoid intersects " + ax + "," + ay + " " + why;
            return null;
        }
    }
}
