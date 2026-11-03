// T-138 V: the intercept line (route from the own unit to the intercept point, a circle at the point) as an IRtOverlay consumer of the
// coverage layer (Noesis render texture, no labels) and an IMGUI/GL fallback. Labels go through IMapExtraLabels, owner "intercept";
// without that service the IMGUI path writes the text itself. GL only through GlSafe.
using System;
using System.Collections.Generic;
using UnityEngine;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal sealed class InterceptOverlay : IRtOverlay
    {
        private readonly Func<IList<IcView>> _view;
        private readonly IAswMap _map;
        private readonly Action<string> _log;
        private Material _mat, _matPm;
        private static bool _premul;   // drawing into the layer's render texture: premultiplied alpha
        private bool _errLogged;
        private double _drawnAt = double.NegativeInfinity;
        private string _labelSig = "";
        private bool _labelsOn;
        private static GUIStyle _labelStyle;

        /// <summary>Redraw period of the render texture: the units move.</summary>
        public const double RedrawS = 1.0;

        /// <summary>True while the Noesis layer owns the drawing (then OnGUI stays silent and the layer calls DrawGeo).</summary>
        public Func<bool> NoesisActive;

        /// <summary>Label service (ContactInfoController); null = the IMGUI path writes the text itself and the Noesis path has no label.</summary>
        public IMapExtraLabels ExtraLabels;

        private static readonly Color Active = new Color(1f, 0.88f, 0.54f, 0.95f);
        private static readonly Color Grey = new Color(0.75f, 0.75f, 0.75f, 0.6f);
        private static readonly Color Red = new Color(1f, 0.42f, 0.35f, 0.95f);

        private static Color Pm(Color c) { return _premul ? new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a) : c; }

        public InterceptOverlay(Func<IList<IcView>> view, IAswMap map, Action<string> log)
        {
            _view = view; _map = map; _log = log ?? (m => { });
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null)
            {
                _mat = Make(sh, UnityEngine.Rendering.BlendMode.SrcAlpha);
                _matPm = Make(sh, UnityEngine.Rendering.BlendMode.One);
            }
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

        // ---- IRtOverlay -------------------------------------------------------------------------------------------------------------------
        public string Id { get { return "intercept"; } }

        public bool HasContent()
        {
            try { var v = _view(); return AnyShown(v); }
            catch { return false; }
        }

        private static bool Shown(IcView v) { return v != null && v.Line != null && v.Line.Count >= 2; }

        private static bool AnyShown(IList<IcView> l)
        {
            if (l == null) return false;
            for (int i = 0; i < l.Count; i++) if (Shown(l[i])) return true;
            return false;
        }

        public bool RefPoint(out double lat, out double lon)
        {
            lat = lon = 0;
            try
            {
                var l = _view();
                if (l == null) return false;
                for (int i = 0; i < l.Count; i++)
                    if (Shown(l[i])) { lat = l[i].Line[0].Lat; lon = l[i].Line[0].Lon; return true; }
                return false;
            }
            catch { return false; }
        }

        public double DueAt { get { return _drawnAt + RedrawS; } }

        /// <summary>Noesis path: draws into the layer's render texture. affRt = {A,B,C,D,E,F} geo -> RT pixels. Throws are caught by the layer.</summary>
        public void DrawGeo(double[] affRt, int rw, int rh)
        {
            _drawnAt = Time.unscaledTime;
            if (_matPm == null) return;
            var l = _view();
            if (l == null) return;
            _premul = true;
            try
            {
                _matPm.SetPass(0);
                DrawAll(l, affRt);
            }
            finally { _premul = false; }
        }

        // ---- labels (IMapExtraLabels) -----------------------------------------------------------------------------------------------------
        /// <summary>Once per frame from the module: publishes the labels when they changed; an empty list removes them.</summary>
        public void Tick()
        {
            var svc = ExtraLabels;
            if (svc == null) return;
            try
            {
                var items = InterceptLabels.Build(_view());
                string sig = InterceptLabels.Signature(items);
                if (sig == _labelSig) return;
                _labelSig = sig;
                _labelsOn = items.Count > 0;
                svc.SetExtraLabels(InterceptLabels.Owner, items);
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("intercept labels failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>Mission left / module disposed: take the labels off the map.</summary>
        public void ClearLabels()
        {
            try { if (ExtraLabels != null && (_labelsOn || _labelSig.Length > 0)) ExtraLabels.SetExtraLabels(InterceptLabels.Owner, null); }
            catch (Exception) { }
            _labelSig = ""; _labelsOn = false;
        }

        // ---- IMGUI / GL path --------------------------------------------------------------------------------------------------------------
        public void OnGUI()
        {
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint || _mat == null) return;
                if (NoesisActive != null && NoesisActive()) return;
                var l = _view();
                double lat, lon;
                if (l == null || !RefPoint(out lat, out lon)) return;
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
                        DrawAll(l, aff);
                    }
                    finally
                    {
                        GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
                        GL.PopMatrix();
                    }
                }
                if (ExtraLabels == null) DrawLabels(l, aff, rect, occ);
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("intercept overlay failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // ---- primitives -------------------------------------------------------------------------------------------------------------------
        private static Vector2 P(double[] a, double lat, double lon)
        {
            return new Vector2((float)(a[0] * lat + a[1] * lon + a[2]), (float)(a[3] * lat + a[4] * lon + a[5]));
        }

        private static void Line(Vector2 a, Vector2 b, Color c)
        {
            GlSafe.Lines(() => { GL.Color(Pm(c)); GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0); });
        }

        private static void Dashed(Vector2 a, Vector2 b, Color c, float on = 8f, float off = 6f)
        {
            float ax = a.x, ay = a.y, bx = b.x, by = b.y;
            if (!DrawGuard.ClipSegment(ref ax, ref ay, ref bx, ref by, Screen.width, Screen.height, 32f)) return;
            a = new Vector2(ax, ay); b = new Vector2(bx, by);
            float len = (b - a).magnitude;
            int n = DrawGuard.DashCount(len, on, off, 512);
            if (n == 0) return;
            var dir = (b - a) / len;
            for (int i = 0; i < n; i++)
            {
                float t = i * (on + off);
                Line(a + dir * t, a + dir * Mathf.Min(len, t + on), c);
            }
        }

        private static void Circle(Vector2 c, float r, Color col)
        {
            const int n = 20;
            GlSafe.Lines(() =>
            {
                GL.Color(Pm(col));
                for (int i = 0; i < n; i++)
                {
                    float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n;
                    GL.Vertex3(c.x + r * Mathf.Cos(a0), c.y + r * Mathf.Sin(a0), 0); GL.Vertex3(c.x + r * Mathf.Cos(a1), c.y + r * Mathf.Sin(a1), 0);
                }
            });
        }

        internal static Color ColorOf(string style) { return style == "red" ? Red : style == "grey" ? Grey : Active; }

        private static void DrawAll(IList<IcView> l, double[] aff)
        {
            for (int i = 0; i < l.Count; i++)
            {
                var v = l[i];
                if (!Shown(v)) continue;
                var col = ColorOf(v.Style);
                bool dashed = v.Style == "red" || v.Style == "grey";   // solid = an active intercept
                for (int k = 0; k + 1 < v.Line.Count; k++)
                {
                    var a = P(aff, v.Line[k].Lat, v.Line[k].Lon); var b = P(aff, v.Line[k + 1].Lat, v.Line[k + 1].Lon);
                    if (dashed) Dashed(a, b, col); else Line(a, b, col);
                }
                var ip = P(aff, v.Ip.Lat, v.Ip.Lon);
                Circle(ip, 7f, col); Circle(ip, 2.5f, col);
            }
        }

        /// <summary>IMGUI path without the label service: the line text next to the intercept point; skipped under mod/game windows.</summary>
        private static void DrawLabels(IList<IcView> l, double[] aff, double[] rect, List<double[]> occ)
        {
            if (_labelStyle == null) _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };
            for (int i = 0; i < l.Count; i++)
            {
                var v = l[i];
                if (!Shown(v) || string.IsNullOrEmpty(v.Label)) continue;
                var p = P(aff, v.Ip.Lat, v.Ip.Lon);
                if (p.x < rect[0] || p.y < rect[1] || p.x > rect[0] + rect[2] || p.y > rect[1] + rect[3]) continue;
                bool hidden = false;
                foreach (var o in occ) if (p.x >= o[0] && p.x <= o[0] + o[2] && p.y >= o[1] && p.y <= o[1] + o[3]) { hidden = true; break; }
                if (hidden) continue;
                _labelStyle.normal.textColor = ColorOf(v.Style);
                GUI.Label(new Rect(p.x + 10, p.y - 8, 340, 16), v.Label, _labelStyle);
            }
        }
    }
}
