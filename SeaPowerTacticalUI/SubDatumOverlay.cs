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

        // ---- the exact projector (ADR-0119) ----------------------------------------------------------------------------------------------
        private struct GeoPt { public double Lat, Lon; }

        private static List<GeoPt> GeoOf(SubDatum d, IList<DxY> poly)
        {
            var l = new List<GeoPt>(poly.Count);
            foreach (DxY p in poly)
            {
                GeoPt g;
                SubDatumMath.ToGeo(d.Lat0, d.Lon0, p, d.Globe, out g.Lat, out g.Lon);
                l.Add(g);
            }
            return l;
        }

        /// <summary>The corners of a fill in RT pixels, hidden ones pressed to the limb; false = no corner is visible (nothing to fill).</summary>
        private static bool FillPts(MapPx px, List<GeoPt> geo, List<Vector2> pts)
        {
            bool any = false;
            foreach (GeoPt g in geo)
            {
                double x, y;
                if (px.PtClamped(g.Lat, g.Lon, out x, out y)) any = true;
                pts.Add(new Vector2((float)x, (float)y));
            }
            return any;
        }

        /// <summary>Segments of the outlines (solid pieces, dashes already cut: the phase runs through the pieces of one outline).</summary>
        private sealed class SegBuf : IGeoSink
        {
            public readonly List<float> Xy = new List<float>(1024);
            public readonly List<Color> Col = new List<Color>(256);
            public Color Color; public bool Dashed; public double Phase; public int Rw, Rh;
            private const float On = 8f, Off = 6f;
            public void Break() { }
            private void Add(float ax, float ay, float bx, float by) { Xy.Add(ax); Xy.Add(ay); Xy.Add(bx); Xy.Add(by); Col.Add(Color); }
            public void Line(float ax, float ay, float bx, float by)
            {
                double dx = (double)bx - ax, dy = (double)by - ay, full = Math.Sqrt(dx * dx + dy * dy);
                if (double.IsNaN(full) || double.IsInfinity(full)) return;
                float cax = ax, cay = ay, cbx = bx, cby = by;
                bool ok = DrawGuard.ClipSegment(ref cax, ref cay, ref cbx, ref cby, Rw, Rh, 32f);
                double start = Phase;
                Phase += full;
                if (!ok) return;
                if (!Dashed) { Add(cax, cay, cbx, cby); return; }
                double ex = (double)cbx - cax, ey = (double)cby - cay, len = Math.Sqrt(ex * ex + ey * ey);
                if (len < 1e-6) return;
                double lead = Math.Sqrt(((double)cax - ax) * ((double)cax - ax) + ((double)cay - ay) * ((double)cay - ay));
                double step = On + Off, ph = (start + lead) % step;
                int n = DrawGuard.DashCount((float)(len + ph), On, Off, 128);
                double ux = ex / len, uy = ey / len;
                for (int i = 0; i <= n; i++)
                {
                    double ds = i * step - ph, de = ds + On;
                    if (de <= 0) continue;
                    if (ds >= len) break;
                    if (ds < 0) ds = 0;
                    if (de > len) de = len;
                    if (de - ds < 0.25) continue;
                    Add((float)(cax + ux * ds), (float)(cay + uy * ds), (float)(cax + ux * de), (float)(cay + uy * de));
                }
            }
        }

        private static void OutlineGeo(MapPx px, SegBuf buf, List<GeoPt> geo, Color c, bool dashed)
        {
            buf.Color = c; buf.Dashed = dashed; buf.Phase = 0;
            for (int i = 0; i < geo.Count; i++)
            {
                GeoPt a = geo[i], b = geo[(i + 1) % geo.Count];
                px.Line(a.Lat, a.Lon, b.Lat, b.Lon, buf);
            }
        }

        /// <summary>Draws into the active render texture through the exact projector: sides are great circles cut at the horizon, fills pressed to the limb. Throws are caught.</summary>
        public void DrawGeo(MapPx px, int rw, int rh)
        {
            _due = Time.unscaledTime + RedrawS;
            var book = _book();
            if (book == null || !_visible() || _matPm == null || px == null) return;
            try
            {
                double now = _nowS();
                var items = new List<SubDatum>(book.All);
                var fills = new List<KeyValuePair<List<Vector2>, Color>>();
                var buf = new SegBuf { Rw = rw, Rh = rh };
                foreach (SubDatum d in items)
                {
                    bool live = d.State == "live";
                    Color col = live ? Live : Grey;
                    List<GeoPt> core = GeoOf(d, d.Core), far = GeoOf(d, d.Far), infl = GeoOf(d, book.Inflated(d, true, now));
                    var cp = new List<Vector2>(core.Count);
                    if (FillPts(px, core, cp)) fills.Add(new KeyValuePair<List<Vector2>, Color>(cp, Pm(col, live ? 0.20f : 0.10f)));
                    Color cLine = Pm(col, live ? 0.95f : 0.5f), cFar = Pm(col, live ? 0.55f : 0.3f);
                    OutlineGeo(px, buf, core, cLine, false);
                    OutlineGeo(px, buf, far, cFar, false);
                    OutlineGeo(px, buf, infl, cLine, true);
                    DxY axisEnd = new DxY(Math.Sin(d.ThetaDeg * Math.PI / 180) * d.RmaxNm, Math.Cos(d.ThetaDeg * Math.PI / 180) * d.RmaxNm);
                    double ela, elo; SubDatumMath.ToGeo(d.Lat0, d.Lon0, axisEnd, d.Globe, out ela, out elo);
                    buf.Color = cFar; buf.Dashed = true; buf.Phase = 0;
                    px.Line(d.Lat0, d.Lon0, ela, elo, buf);
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
                GlSafe.Lines(() =>
                {
                    for (int i = 0; i < buf.Col.Count; i++)
                    {
                        GL.Color(buf.Col[i]);
                        int k = i * 4;
                        GL.Vertex3(buf.Xy[k], buf.Xy[k + 1], 0); GL.Vertex3(buf.Xy[k + 2], buf.Xy[k + 3], 0);
                    }
                });
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
