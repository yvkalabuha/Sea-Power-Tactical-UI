// T-288 / ADR-0115: the sub launch area (core, far sector, buffer, axis) as an IRtOverlay consumer of the coverage layer (Noesis render texture) with a caption at P0.
// Drawn under the game's interface like the range rings. Budget: at most MaxLive + grey sectors, each <= 3 polygons of <= 64 points. GL only through GlSafe.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge
{
    internal sealed class SubDatumOverlay : IRtOverlay, IRtTexts
    {
        /// <summary>The buffer grows with time: redraw every 10 s while a sector is shown.</summary>
        public const double RedrawS = 10.0;

        private readonly Func<SubDatumBook> _book;
        private readonly Func<double> _nowS;
        private readonly Func<bool> _visible;
        private readonly Action<string> _log;
        private Material _matPm;
        private double _due = double.NegativeInfinity;
        private bool _errLogged;

        private static readonly Color Live = new Color(1f, 0.62f, 0.2f, 1f);
        private static readonly Color Grey = new Color(0.72f, 0.72f, 0.72f, 1f);

        public SubDatumOverlay(Func<SubDatumBook> book, Func<double> nowS, Func<bool> visible, Action<string> log)
        {
            _book = book; _nowS = nowS; _visible = visible; _log = log ?? (m => { });
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null)
            {
                _matPm = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _matPm.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);   // premultiplied alpha: the layer's texture is premultiplied
                _matPm.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _matPm.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _matPm.SetInt("_ZWrite", 0);
            }
        }

        // ---- IRtOverlay -------------------------------------------------------------------------------------------------------------------
        public string Id { get { return "sub_datum"; } }

        public bool HasContent()
        {
            try { var b = _book(); return _visible() && b != null && b.All.Count > 0; }
            catch (Exception) { return false; }
        }

        public bool RefPoint(out double lat, out double lon)
        {
            lat = lon = 0;
            try
            {
                var b = _book();
                if (b == null || b.All.Count == 0) return false;
                var d = b.All[b.All.Count - 1];
                lat = d.Lat0; lon = d.Lon0;
                return true;
            }
            catch (Exception) { return false; }
        }

        public double DueAt { get { return HasContent() ? _due : double.PositiveInfinity; } }

        /// <summary>A sector appeared / changed / closed (or the layer was switched on): the layer draws it with the next frame.</summary>
        public void RequestDraw() { _due = double.NegativeInfinity; }

        // ---- IRtTexts ---------------------------------------------------------------------------------------------------------------------
        public IList<RtTextItem> TextItems()
        {
            var res = new List<RtTextItem>();
            try
            {
                var b = _book();
                if (b == null || !_visible()) return res;
                double now = _nowS();
                foreach (SubDatum d in b.All)
                {
                    var m = new SubDatumCardModel { Ammo = d.Ammo, SigmaDeg = d.SigmaDeg, AgeS = Math.Max(0, (d.State == "live" ? now : Math.Min(now, d.ClosedAtS)) - d.T0S) };
                    res.Add(new RtTextItem { Lat = d.Lat0, Lon = d.Lon0, Text = SubDatumCard.MapLabel(m), Argb = d.State == "live" ? 0xFFFFA033u : 0xFFB8B8B8u });
                }
            }
            catch (Exception) { }
            return res;
        }

        // ---- drawing ----------------------------------------------------------------------------------------------------------------------
        private static Color Pm(Color c, float a) { return new Color(c.r * a, c.g * a, c.b * a, a); }

        private static Vector2 P(double[] a, double lat, double lon)
        {
            return new Vector2((float)(a[0] * lat + a[1] * lon + a[2]), (float)(a[3] * lat + a[4] * lon + a[5]));
        }

        private static List<Vector2> Screen_(SubDatum d, IList<DxY> poly, double[] aff)
        {
            var l = new List<Vector2>(poly.Count);
            foreach (DxY p in poly)
            {
                double la, lo;
                SubDatumMath.ToGeo(d.Lat0, d.Lon0, p, d.Globe, out la, out lo);
                l.Add(P(aff, la, lo));
            }
            return l;
        }

        private static void Seg(Vector2 a, Vector2 b, Color c)
        {
            GL.Color(c); GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0);
        }

        private static void DashedSeg(Vector2 a, Vector2 b, Color c, int rw, int rh)
        {
            float ax = a.x, ay = a.y, bx = b.x, by = b.y;
            if (!DrawGuard.ClipSegment(ref ax, ref ay, ref bx, ref by, rw, rh, 32f)) return;
            a = new Vector2(ax, ay); b = new Vector2(bx, by);
            float len = (b - a).magnitude;
            const float on = 8f, off = 6f;
            int n = DrawGuard.DashCount(len, on, off, 128);
            if (n == 0) return;
            var dir = (b - a) / len;
            for (int i = 0; i < n; i++)
            {
                float t = i * (on + off);
                Seg(a + dir * t, a + dir * Mathf.Min(len, t + on), c);
            }
        }

        private static void Outline(List<Vector2> pts, Color c, bool dashed, int rw, int rh)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
                if (dashed) DashedSeg(a, b, c, rw, rh); else Seg(a, b, c);
            }
        }

        /// <summary>Draws into the active render texture. affRt = {a0..a5} geo -> RT pixels. Throws are caught by the layer.</summary>
        public void DrawGeo(double[] affRt, int rw, int rh)
        {
            _due = Time.unscaledTime + RedrawS;
            var book = _book();
            if (book == null || !_visible() || _matPm == null) return;
            try
            {
                double now = _nowS();
                var items = new List<SubDatum>(book.All);
                var fills = new List<KeyValuePair<List<Vector2>, Color>>();
                var lines = new List<System.Action>();
                foreach (SubDatum d in items)
                {
                    bool live = d.State == "live";
                    Color col = live ? Live : Grey;
                    var core = Screen_(d, d.Core, affRt);
                    var far = Screen_(d, d.Far, affRt);
                    var buf = Screen_(d, book.Inflated(d, true, now), affRt);
                    fills.Add(new KeyValuePair<List<Vector2>, Color>(core, Pm(col, live ? 0.20f : 0.10f)));
                    var p0 = P(affRt, d.Lat0, d.Lon0);
                    DxY axisEnd = new DxY(Math.Sin(d.ThetaDeg * Math.PI / 180) * d.RmaxNm, Math.Cos(d.ThetaDeg * Math.PI / 180) * d.RmaxNm);
                    double ela, elo; SubDatumMath.ToGeo(d.Lat0, d.Lon0, axisEnd, d.Globe, out ela, out elo);
                    var pe = P(affRt, ela, elo);
                    Color cLine = Pm(col, live ? 0.95f : 0.5f), cFar = Pm(col, live ? 0.55f : 0.3f);
                    lines.Add(() =>
                    {
                        Outline(core, cLine, false, rw, rh);
                        Outline(far, cFar, false, rw, rh);
                        Outline(buf, cLine, true, rw, rh);
                        DashedSeg(p0, pe, cFar, rw, rh);
                    });
                }
                _matPm.SetPass(0);
                GlSafe.Tris(() =>
                {
                    foreach (var f in fills)
                    {
                        var pts = f.Key;
                        if (pts.Count < 3) continue;
                        GL.Color(f.Value);
                        for (int i = 1; i + 1 < pts.Count; i++)
                        {
                            GL.Vertex3(pts[0].x, pts[0].y, 0); GL.Vertex3(pts[i].x, pts[i].y, 0); GL.Vertex3(pts[i + 1].x, pts[i + 1].y, 0);
                        }
                    }
                });
                _matPm.SetPass(0);
                GlSafe.Lines(() => { foreach (var a in lines) a(); });
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("[SubDatum] overlay draw failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        public void Dispose()
        {
            try { if (_matPm != null) UnityEngine.Object.Destroy(_matPm); } catch (Exception) { }
            _matPm = null;
        }
    }
}
