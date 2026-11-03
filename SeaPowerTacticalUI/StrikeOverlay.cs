using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-341 phase B: the strike layer of the map (IRtOverlay in the radar layer's Extra list, plus the IMGUI path while the Noesis layer is off): the SAM zones of the known hostile
    /// ships (the radio horizon of a low missile dashed), the wait point with its orbit, the attack axis, the launch points of the chosen variant. The primitives come from
    /// StrikeOverlayMath (pure); the picture is rebuilt at most once per second. It never throws, never writes to the game.
    /// </summary>
    internal sealed class StrikeOverlay : IRtOverlay
    {
        public const double RedrawS = 1.0;

        private readonly Func<StrikeView> _view;
        private readonly Func<string> _selected;
        private readonly IAswMap _map;
        private readonly Action<string> _log;
        private Material _mat, _matPm;
        private static bool _premul;
        private bool _errLogged;
        private double _drawnAt = double.NegativeInfinity, _builtAt = double.NegativeInfinity;
        private List<StrikePrim> _prims = new List<StrikePrim>();
        private string _last;
        private static GUIStyle _labelStyle;

        /// <summary>True while the Noesis layer owns the drawing (then OnGUI stays silent and the layer calls DrawGeo).</summary>
        public Func<bool> NoesisActive;

        private static readonly Color Red = new Color(1f, 0.42f, 0.35f, 0.9f), Cyan = new Color(0.4f, 0.85f, 1f, 0.95f), Yellow = new Color(1f, 0.88f, 0.54f, 0.95f), Orange = new Color(1f, 0.62f, 0.25f, 0.95f);

        private static Color Pm(Color c) { return _premul ? new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a) : c; }

        public StrikeOverlay(Func<StrikeView> view, Func<string> selectedGroup, IAswMap map, Action<string> log)
        {
            _view = view; _selected = selectedGroup; _map = map; _log = log ?? (m => { });
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null) { _mat = Make(sh, UnityEngine.Rendering.BlendMode.SrcAlpha); _matPm = Make(sh, UnityEngine.Rendering.BlendMode.One); }
        }

        private static Material Make(Shader sh, UnityEngine.Rendering.BlendMode src)
        {
            var m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m.SetInt("_ZWrite", 0);
            return m;
        }

        /// <summary>Once per frame from the module: the primitives are rebuilt when the second is over.</summary>
        public void Tick()
        {
            try
            {
                double now = Time.unscaledTime;
                if (now - _builtAt < RedrawS && now >= _builtAt) return;
                _builtAt = now;
                var v = _view();
                var list = new List<StrikePrim>();
                if (v != null && v.Groups.Count > 0)
                {
                    string sel = _selected == null ? null : _selected();
                    if (sel != null) _last = sel;
                    StrikeGroupView g = null;
                    foreach (var x in v.Groups) if (x.Name == (sel ?? _last)) g = x;
                    if (g == null) g = v.Groups[v.Groups.Count - 1];
                    list = StrikeOverlayMath.Build(g, v.Ships, g.Chosen);
                }
                _prims = list;
            }
            catch (Exception e)
            {
                _prims = new List<StrikePrim>();
                if (!_errLogged) { _errLogged = true; _log("strike overlay build failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // ---- IRtOverlay
        public string Id { get { return "strike"; } }
        public bool HasContent() { return _prims.Count > 0; }
        public double DueAt { get { return _drawnAt + RedrawS; } }

        public bool RefPoint(out double lat, out double lon)
        {
            lat = lon = 0;
            try
            {
                foreach (var p in _prims) if (p.Kind == "wait" || p.Kind == "sam") { lat = p.A.Lat; lon = p.A.Lon; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>Noesis path through the exact projector (ADR-0119, T-309): the geo points go through MapPx (a point behind the horizon is not drawn). The beta tree differs from the stable line only here.</summary>
        public void DrawGeo(MapPx px, int rw, int rh)
        {
            _drawnAt = Time.unscaledTime;
            if (_matPm == null || px == null) return;
            _premul = true;
            try
            {
                _matPm.SetPass(0);
                DrawAll(_prims, (la, lo) => { double x, y; return px.Pt(la, lo, out x, out y) ? new Vector2((float)x, (float)y) : (Vector2?)null; });
            }
            finally { _premul = false; }
        }

        // ---- IMGUI path
        public void OnGUI()
        {
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint || _mat == null || _prims.Count == 0) return;
                if (NoesisActive != null && NoesisActive()) return;
                double lat, lon;
                if (!RefPoint(out lat, out lon)) return;
                double[] aff, rect;
                if (!_map.TryProjection(lat, lon, out aff, out rect)) return;
                var occ = _map is AswMapProvider ? ((AswMapProvider)_map).OccludedRects() : ModUiRects.All();
                foreach (var piece in CoverageMath.SubtractRects(rect, occ, 8, 8))
                {
                    var vp = new Rect((float)piece[0], Screen.height - (float)(piece[1] + piece[3]), (float)piece[2], (float)piece[3]);
                    GL.PushMatrix();
                    try
                    {
                        GL.Viewport(vp);
                        GL.LoadPixelMatrix((float)piece[0], (float)(piece[0] + piece[2]), (float)(piece[1] + piece[3]), (float)piece[1]);
                        _mat.SetPass(0);
                        DrawAll(_prims, (la, lo) => P(aff, la, lo));
                    }
                    finally { GL.Viewport(new Rect(0, 0, Screen.width, Screen.height)); GL.PopMatrix(); }
                }
                DrawLabels(_prims, aff, rect, occ);
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("strike overlay failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static Vector2? P(double[] a, double lat, double lon)
        {
            return new Vector2((float)(a[0] * lat + a[1] * lon + a[2]), (float)(a[3] * lat + a[4] * lon + a[5]));
        }

        private static Color ColorOf(string c) { return c == "red" ? Red : c == "cyan" ? Cyan : c == "orange" ? Orange : Yellow; }

        private static void Line(Vector2 a, Vector2 b, Color c) { GlSafe.Lines(() => { GL.Color(Pm(c)); GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0); }); }

        private static void Dashed(Vector2 a, Vector2 b, Color c, float on = 8f, float off = 6f)
        {
            float ax = a.x, ay = a.y, bx = b.x, by = b.y;
            if (!DrawGuard.ClipSegment(ref ax, ref ay, ref bx, ref by, Screen.width, Screen.height, 32f)) return;
            a = new Vector2(ax, ay); b = new Vector2(bx, by);
            float len = (b - a).magnitude;
            int n = DrawGuard.DashCount(len, on, off, 512);
            if (n == 0) return;
            var dir = (b - a) / len;
            for (int i = 0; i < n; i++) { float t = i * (on + off); Line(a + dir * t, a + dir * Mathf.Min(len, t + on), c); }
        }

        /// <summary>A circle of radius nm round a geo point: 48 points through the projection (no flat-earth shortcut), solid or dashed by segments. A point the projection cannot place breaks the circle.</summary>
        private static void GeoCircle(Func<double, double, Vector2?> proj, GeoPt c, double rNm, Color col, bool dashed)
        {
            const int n = 48;
            var g0 = AswMath.Offset(c, 0, rNm);
            var prev = proj(g0.Lat, g0.Lon);
            for (int i = 1; i <= n; i++)
            {
                var g = AswMath.Offset(c, i * 360.0 / n, rNm);
                var cur = proj(g.Lat, g.Lon);
                if (prev.HasValue && cur.HasValue && (!dashed || i % 2 == 1)) Line(prev.Value, cur.Value, col);
                prev = cur;
            }
        }

        private static void DrawAll(IList<StrikePrim> l, Func<double, double, Vector2?> proj)
        {
            foreach (var p in l)
            {
                var col = ColorOf(p.Color);
                switch (p.Kind)
                {
                    case "sam": case "horizon": case "orbit": case "ew_unc": case "ewf_rt": GeoCircle(proj, p.A, p.RNm, col, p.Dashed); break;
                    case "ew_station":
                    {
                        var q = proj(p.A.Lat, p.A.Lon);
                        if (!q.HasValue) break;
                        var c = q.Value;
                        Line(c + new Vector2(0, -8), c + new Vector2(8, 0), col); Line(c + new Vector2(8, 0), c + new Vector2(0, 8), col);
                        Line(c + new Vector2(0, 8), c + new Vector2(-8, 0), col); Line(c + new Vector2(-8, 0), c + new Vector2(0, -8), col);
                        break;
                    }
                    case "axis": case "route": case "sam_leg": case "ew_route": case "ew_ray": case "ew_arc": case "ew_hatch": case "ewf_ray":
                    {
                        var a = proj(p.A.Lat, p.A.Lon); var b = proj(p.B.Lat, p.B.Lon);
                        if (!a.HasValue || !b.HasValue) break;
                        if (p.Dashed) Dashed(a.Value, b.Value, col); else Line(a.Value, b.Value, col);
                        break;
                    }
                    case "wait": case "launch": case "gate": case "descent":
                    {
                        var q = proj(p.A.Lat, p.A.Lon);
                        if (!q.HasValue) break;
                        var c = q.Value;
                        Line(c + new Vector2(-6, 0), c + new Vector2(6, 0), col); Line(c + new Vector2(0, -6), c + new Vector2(0, 6), col);
                        Line(c + new Vector2(-4, -4), c + new Vector2(4, 4), col); Line(c + new Vector2(-4, 4), c + new Vector2(4, -4), col);
                        break;
                    }
                }
            }
        }

        private static void DrawLabels(IList<StrikePrim> l, double[] aff, double[] rect, List<double[]> occ)
        {
            if (_labelStyle == null) _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };
            foreach (var p in l)
            {
                if (string.IsNullOrEmpty(p.Label)) continue;
                var q = P(aff, p.A.Lat, p.A.Lon).Value;
                if (q.x < rect[0] || q.y < rect[1] || q.x > rect[0] + rect[2] || q.y > rect[1] + rect[3]) continue;
                bool hidden = false;
                foreach (var o in occ) if (q.x >= o[0] && q.x <= o[0] + o[2] && q.y >= o[1] && q.y <= o[1] + o[3]) { hidden = true; break; }
                if (hidden) continue;
                _labelStyle.normal.textColor = ColorOf(p.Color);
                GUI.Label(new Rect(q.x + 9, q.y - 8, 240, 16), p.Label, _labelStyle);
            }
        }
    }
}
