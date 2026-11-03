// T-112 §3.6 CapOverlay: zone rectangle (dashed for a ship-anchored zone), racetrack lanes by slot, birds, deck-priority mark.
// Two paths like AswOverlay: IMGUI Repaint + GL (labels allowed) and the Noesis layer (DrawGeo into a render texture, no labels). GL only through GlSafe.
using System;
using System.Collections.Generic;
using UnityEngine;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal sealed class CapOverlay
    {
        private readonly Func<CapView> _view;
        private readonly IAswMap _map;
        private readonly Func<GeoPt?> _refPoint;
        private readonly Func<int, GeoPt?> _unitPos;   // uid -> position of a bird (null = unknown); the lane markers still draw without it
        private readonly Action<string> _log;
        private readonly CapParams _lanes = new CapParams();
        private Material _mat, _matPm;
        private static bool _premul;   // drawing into the layer's render texture: premultiplied alpha
        private bool _errLogged;
        private static GUIStyle _labelStyle;

        /// <summary>True while the Noesis layer owns the drawing (then OnGUI stays silent and the layer calls DrawGeo).</summary>
        public Func<bool> NoesisActive;

        /// <summary>0.28.1: the group names on the zones in the Noesis path (the render texture has no text). Null = only the IMGUI path writes them.</summary>
        public IMapExtraLabels ExtraLabels;
        private const string LabelOwner = "cap";
        private string _labelSig = "";

        /// <summary>T-154 A7: the zone being composed in the window (lat0, lon0, lat1, lon1) or null; drawn dashed in the accent colour, no order yet.</summary>
        public Func<double[]> DraftZone;

        private static readonly Color DraftColor = new Color(0.3f, 0.75f, 1f, 0.95f);

        private double[] Draft()
        {
            try { var z = DraftZone == null ? null : DraftZone(); return z != null && z.Length >= 4 ? z : null; }
            catch (Exception) { return null; }
        }

        private static readonly Color[] Palette =
        {
            new Color(0.35f, 0.85f, 1f, 0.95f), new Color(0.6f, 1f, 0.45f, 0.95f), new Color(1f, 0.8f, 0.35f, 0.95f), new Color(0.85f, 0.55f, 1f, 0.95f),
        };
        private static readonly Color PrioColor = new Color(1f, 0.55f, 0.1f, 0.95f);
        private static readonly Color GapColor = new Color(1f, 0.3f, 0.25f, 0.95f);

        private static Color Pm(Color c) { return _premul ? new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a) : c; }

        public CapOverlay(Func<CapView> view, IAswMap map, Func<GeoPt?> refPoint, Func<int, GeoPt?> unitPos, Action<string> log)
        {
            _view = view; _map = map; _refPoint = refPoint ?? (() => null); _unitPos = unitPos; _log = log ?? (m => { });
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

        private static bool Shown(CapGroupView g) { return g.Ordered && g.Rect != null && g.Rect.Length >= 4; }

        /// <summary>T-179 / ADR-0079 section 3: the selected group of the window (the circles of an alert group are drawn for it only).</summary>
        public Func<string> SelectedGroup;

        private bool ShownAlert(CapGroupView g)
        {
            if (g == null || !g.Ordered || g.Kind != "alert" || g.AlertSpec == null || g.Line == null) return false;
            string sel = null;
            try { sel = SelectedGroup == null ? null : SelectedGroup(); } catch (Exception) { }
            return sel != null && sel == g.Line.Group;
        }

        private bool Any(CapView v) { foreach (var g in v.Groups) if (Shown(g) || ShownAlert(g)) return true; return false; }

        public bool HasContent()
        {
            try { var v = _view(); return (v != null && Any(v)) || Draft() != null; }
            catch { return false; }
        }

        /// <summary>Reference point of the current view (for the layer's projection when no radar frame exists).</summary>
        public bool RefPoint(out double lat, out double lon)
        {
            lat = lon = 0;
            try { var p = _refPoint(); if (!p.HasValue) return false; lat = p.Value.Lat; lon = p.Value.Lon; return true; }
            catch { return false; }
        }

        public void OnGUI()
        {
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint || _mat == null) return;
                if (NoesisActive != null && NoesisActive()) return;
                var v = _view();
                var pt = _refPoint();
                if (v == null || !pt.HasValue || (!Any(v) && Draft() == null)) return;
                double[] aff, rect;
                if (!_map.TryProjection(pt.Value.Lat, pt.Value.Lon, out aff, out rect)) return;
                var px = MapPx.FromAffine(aff, Screen.width, Screen.height);   // the IMGUI fallback stays affine (T-309 Q2)
                var occ = _map is AswMapProvider ? ((AswMapProvider)_map).OccludedRects() : ModUiRects.All();
                var pieces = CoverageMath.SubtractRects(rect, occ, 8, 8);
                foreach (var piece in pieces)
                {
                    var vp = new Rect((float)piece[0], Screen.height - (float)(piece[1] + piece[3]), (float)piece[2], (float)piece[3]);
                    GL.PushMatrix();
                    try
                    {
                        GL.Viewport(vp);
                        GL.LoadPixelMatrix((float)piece[0], (float)(piece[0] + piece[2]), (float)(piece[1] + piece[3]), (float)piece[1]);
                        _mat.SetPass(0);
                        DrawAll(v, px);
                    }
                    finally
                    {
                        GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
                        GL.PopMatrix();
                    }
                }
                if (ExtraLabels == null) DrawLabels(v, px, rect, occ);
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("cap overlay failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>Once per frame: publishes the zone labels (group name at the zone's north-west corner) when they changed; none = removed.</summary>
        public void Tick()
        {
            var svc = ExtraLabels;
            if (svc == null) return;
            try
            {
                var zl = CapWindowModel.ZoneLabels(_view());
                var sb = new System.Text.StringBuilder();
                foreach (var z in zl) sb.Append(z.Index).Append('|').Append(z.Lat.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                                        .Append(z.Lon.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(z.Text).Append(';');
                string sig = sb.ToString();
                if (sig == _labelSig) return;
                _labelSig = sig;
                var items = new List<LabelItem>(zl.Count);
                foreach (var z in zl) items.Add(new LabelItem { Key = CapWindowModel.ZoneLabelKeyBase - z.Index, Lat = z.Lat, Lon = z.Lon, Text = z.Text, Argb = Argb(z.Prio ? PrioColor : Palette[z.Index % Palette.Length]) });
                svc.SetExtraLabels(LabelOwner, items);
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("cap labels failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>Module disposed: take the labels off the map.</summary>
        public void ClearLabels()
        {
            try { if (ExtraLabels != null && _labelSig.Length > 0) ExtraLabels.SetExtraLabels(LabelOwner, null); } catch (Exception) { }
            _labelSig = "";
        }

        private static uint Argb(Color c)
        {
            Func<float, uint> b = f => (uint)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);
            return (0xFFu << 24) | (b(c.r) << 16) | (b(c.g) << 8) | b(c.b);
        }

        /// <summary>Noesis path: draws into the layer's render texture. px = geo -> RT pixels (MapPx, T-309). Throws are caught by the layer.</summary>
        public void DrawGeo(MapPx px)
        {
            if (_matPm == null) return;
            var v = _view();
            if (v == null) return;
            _premul = true;
            try
            {
                _matPm.SetPass(0);
                DrawAll(v, px);
            }
            finally { _premul = false; }
        }

        // ---- primitives -------------------------------------------------------------------------------------------------------------------
        /// <summary>Target pixel of a geo point; NaN when it is behind the horizon (the pixel helpers skip NaN points).</summary>
        private static Vector2 P(MapPx px, double lat, double lon)
        {
            double x, y;
            if (!px.Pt(lat, lon, out x, out y)) return new Vector2(float.NaN, float.NaN);
            return new Vector2((float)x, (float)y);
        }

        private static bool Bad(Vector2 a) { return float.IsNaN(a.x) || float.IsNaN(a.y); }

        /// <summary>A geo segment: a great circle cut at the horizon on the globe (continuous dash phase), a plain segment on a flat map.</summary>
        private sealed class SegSink : IGeoSink
        {
            public Color C; public bool Dash; public float W, H; private float _phase;
            public void Line(float x0, float y0, float x1, float y1)
            {
                if (!Dash) { CapOverlay.Line(new Vector2(x0, y0), new Vector2(x1, y1), C); return; }
                float ax = x0, ay = y0, bx = x1, by = y1;
                if (!DrawGuard.ClipSegment(ref ax, ref ay, ref bx, ref by, W, H, 32f)) { _phase += (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)); return; }
                float len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (!(len > 0.001f) || float.IsInfinity(len)) return;
                float dx = (bx - ax) / len, dy = (by - ay) / len, t = 0, on = 8f, per = 14f;
                for (int guard = 0; t < len && guard < 4096; guard++)
                {
                    float ph = _phase % per, step;
                    if (ph < on)
                    {
                        step = Math.Min(on - ph, len - t);
                        CapOverlay.Line(new Vector2(ax + dx * t, ay + dy * t), new Vector2(ax + dx * (t + step), ay + dy * (t + step)), C);
                    }
                    else step = Math.Min(per - ph, len - t);
                    t += step; _phase += step;
                }
            }
            public void Break() { _phase = 0; }
        }

        private static readonly SegSink Sink = new SegSink();

        private static void GeoLine(MapPx px, double la0, double lo0, double la1, double lo1, Color c)
        {
            Sink.C = c; Sink.Dash = false; Sink.Break();
            px.Line(la0, lo0, la1, lo1, Sink);
        }

        private static void GeoDashed(MapPx px, double la0, double lo0, double la1, double lo1, Color c)
        {
            if (px.Kind == MapProjKind.Affine) { Dashed(P(px, la0, lo0), P(px, la1, lo1), c); return; }
            Sink.C = c; Sink.Dash = true; Sink.W = px.OutW; Sink.H = px.OutH; Sink.Break();
            px.Line(la0, lo0, la1, lo1, Sink);
        }

        private static void Line(Vector2 a, Vector2 b, Color c)
        {
            if (Bad(a) || Bad(b)) return;
            GlSafe.Lines(() => { GL.Color(Pm(c)); GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0); });
        }

        private static void Dashed(Vector2 a, Vector2 b, Color c, float on = 8f, float off = 6f)
        {
            if (Bad(a) || Bad(b)) return;
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

        private static void DashedCircle(Vector2 c, float r, Color col)
        {
            if (Bad(c) || float.IsNaN(r)) return;
            const int n = 48;
            for (int i = 0; i < n; i += 2)
            {
                float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n;
                Line(new Vector2(c.x + Mathf.Cos(a0) * r, c.y + Mathf.Sin(a0) * r), new Vector2(c.x + Mathf.Cos(a1) * r, c.y + Mathf.Sin(a1) * r), col);
            }
        }

        private static void Circle(Vector2 c, float r, Color col)
        {
            if (Bad(c) || float.IsNaN(r)) return;
            const int n = 24;
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

        private static void Cross(Vector2 p, float r, Color c)
        {
            Line(new Vector2(p.x - r, p.y), new Vector2(p.x + r, p.y), c);
            Line(new Vector2(p.x, p.y - r), new Vector2(p.x, p.y + r), c);
        }

        private static void Diamond(Vector2 p, float r, Color c)
        {
            Line(new Vector2(p.x, p.y - r), new Vector2(p.x + r, p.y), c); Line(new Vector2(p.x + r, p.y), new Vector2(p.x, p.y + r), c);
            Line(new Vector2(p.x, p.y + r), new Vector2(p.x - r, p.y), c); Line(new Vector2(p.x - r, p.y), new Vector2(p.x, p.y - r), c);
        }

        private static Color Dim(Color c, float k) { return new Color(c.r, c.g, c.b, c.a * k); }

        /// <summary>Rectangle outline (lat0, lon0, lat1, lon1) in pixels, solid or dashed; pad grows it outward by that many pixels.</summary>
        private static void Frame(MapPx aff, double[] z, Color col, bool dashed, float pad)
        {
            if (pad == 0 && aff.Kind != MapProjKind.Affine)   // globe: four great-circle edges, cut at the horizon
            {
                double[] la = { z[0], z[0], z[2], z[2] }, lo = { z[1], z[3], z[3], z[1] };
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) % 4;
                    if (dashed) GeoDashed(aff, la[i], lo[i], la[j], lo[j], col); else GeoLine(aff, la[i], lo[i], la[j], lo[j], col);
                }
                return;
            }
            var a = P(aff, z[0], z[1]); var b = P(aff, z[0], z[3]); var c = P(aff, z[2], z[3]); var d = P(aff, z[2], z[1]);
            var ctr = (a + b + c + d) / 4f;
            Vector2[] q = { a, b, c, d };
            for (int i = 0; i < 4; i++) { var dir = q[i] - ctr; if (dir.magnitude > 0.001f) q[i] = q[i] + dir.normalized * pad; }
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                if (dashed) Dashed(q[i], q[j], col); else Line(q[i], q[j], col);
            }
        }

        /// <summary>T-333: a bird in an intercept with a guided point draws a thin line to it.</summary>
        internal static bool IcptLineOk(CapMemberView m) { return m != null && m.Icpt != null && m.HasIp; }

        internal static Color BirdColor(string where)
        {
            switch (where)
            {
                case "zone": return new Color(0.4f, 1f, 0.5f, 0.95f);
                case "to_zone": return new Color(0.4f, 0.9f, 1f, 0.95f);
                case "leaving": return new Color(1f, 0.75f, 0.3f, 0.95f);
                case "game_zone": case "player": return new Color(0.7f, 0.7f, 0.7f, 0.9f);
                default: return new Color(1f, 1f, 1f, 0.8f);
            }
        }

        // ---- drawing ----------------------------------------------------------------------------------------------------------------------
        private void DrawAll(CapView v, MapPx aff)
        {
            var draft = Draft();
            if (draft != null) Frame(aff, draft, DraftColor, true, 0);
            for (int gi = 0; gi < v.Groups.Count; gi++)
            {
                var g = v.Groups[gi];
                if (ShownAlert(g))
                {
                    // the dashed circle of the reaction radius and the thin one of the leash around the protected ship (no zone, no handles)
                    GeoPt? c0 = null;
                    try { c0 = g.AlertSpec.RefUid != 0 && _unitPos != null ? _unitPos(g.AlertSpec.RefUid) : null; } catch (Exception) { }
                    if (!c0.HasValue) foreach (var m in g.Members) { try { c0 = m.ShipUid != 0 && _unitPos != null ? _unitPos(m.ShipUid) : null; } catch (Exception) { } if (c0.HasValue) break; }
                    if (c0.HasValue)
                    {
                        var ctr = P(aff, c0.Value.Lat, c0.Value.Lon);
                        float pxNm = (float)aff.PxPerNm(c0.Value.Lat, c0.Value.Lon);
                        var ac = Palette[gi % Palette.Length];
                        DashedCircle(ctr, (float)g.AlertSpec.RadiusNm * pxNm, ac);
                        Circle(ctr, (float)g.AlertSpec.PursuitNm * pxNm, Dim(ac, 0.45f));
                    }
                }
                if (!Shown(g)) continue;
                var col = Palette[gi % Palette.Length];
                bool gap = g.Line != null && g.Line.GapS >= 0, prio = g.Line != null && g.Line.DeckPriority;
                bool lost = g.Line != null && g.Line.Warns != null && g.Line.Warns.Contains("anchor_lost");
                bool unit = g.Anchor == "unit" && !lost;   // a ship-anchored zone moves: dashed
                Frame(aff, g.Rect, gap ? GapColor : col, unit, 0);
                if (prio) { Frame(aff, g.Rect, PrioColor, false, 4f); Frame(aff, g.Rect, PrioColor, true, 8f); }

                float pxPerNm = (float)aff.PxPerNm(g.Rect[0], g.Rect[1]);
                var occupied = new HashSet<int>();
                foreach (var m in g.Members) if (m.Slot >= 0 && m.Where == "zone") occupied.Add(m.Slot);
                foreach (var lane in CapWindowModel.LanesOf(g, _lanes))
                {
                    var lc = occupied.Contains(lane.Slot) ? col : Dim(col, 0.4f);
                    if (lane.Orbit || lane.Track == null || lane.Track.Count < 2)
                    {
                        var c = P(aff, lane.Center.Lat, lane.Center.Lon);
                        Circle(c, Mathf.Max(6f, (float)_lanes.OrbitBelowNm * 0.5f * pxPerNm), lc);
                        continue;
                    }
                    for (int i = 0; i < lane.Track.Count; i++)
                    {
                        var a = lane.Track[i]; var b = lane.Track[(i + 1) % lane.Track.Count];
                        GeoLine(aff, a.Lat, a.Lon, b.Lat, b.Lon, lc);
                    }
                }
                string selName = null;
                try { selName = SelectedGroup == null ? null : SelectedGroup(); } catch (Exception) { }
                bool selected = selName != null && g.Line != null && g.Line.Group == selName;   // T-333: the intercept lines of the selected group only
                if (_unitPos != null)
                    foreach (var m in g.Members)
                    {
                        GeoPt? pos = null;
                        try { pos = _unitPos(m.Uid); } catch (Exception) { pos = null; }
                        if (!pos.HasValue) continue;
                        var bc = BirdColor(m.Where);
                        var pp = P(aff, pos.Value.Lat, pos.Value.Lon);
                        Diamond(pp, 5f, bc); Cross(pp, 2f, bc);
                        if (selected && IcptLineOk(m)) GeoLine(aff, pos.Value.Lat, pos.Value.Lon, m.IpLat, m.IpLon, Dim(col, 0.7f));   // T-333 (ADR-0129): bird -> the point it is guided to
                    }
            }
        }

        /// <summary>IMGUI path only: group name at the zone corner and the "deck priority" text; skipped under mod/game windows.</summary>
        private void DrawLabels(CapView v, MapPx aff, double[] rect, List<double[]> occ)
        {
            if (_labelStyle == null) _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };
            for (int gi = 0; gi < v.Groups.Count; gi++)
            {
                var g = v.Groups[gi];
                if (!Shown(g) || g.Line == null) continue;
                var p = P(aff, Math.Max(g.Rect[0], g.Rect[2]), Math.Min(g.Rect[1], g.Rect[3]));
                if (float.IsNaN(p.x) || p.x < rect[0] || p.y < rect[1] || p.x > rect[0] + rect[2] || p.y > rect[1] + rect[3]) continue;
                bool hidden = false;
                foreach (var o in occ) if (p.x >= o[0] && p.x <= o[0] + o[2] && p.y >= o[1] && p.y <= o[1] + o[3]) { hidden = true; break; }
                if (hidden) continue;
                _labelStyle.normal.textColor = g.Line.DeckPriority ? PrioColor : Palette[gi % Palette.Length];
                string text = g.Line.Group + (g.Line.DeckPriority ? " · " + CapWindow.WarnText("deck_priority") : "");
                GUI.Label(new Rect(p.x + 3, p.y - 17, 260, 16), text, _labelStyle);
            }
        }
    }
}
