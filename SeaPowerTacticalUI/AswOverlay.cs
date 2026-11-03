using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>IAswMap on top of the coverage overlay's map lookup and projection (ADR-0027). Integration wires it: new AswMapProvider(coverageOverlay).</summary>
    internal sealed class AswMapProvider : IAswMap
    {
        private readonly CoverageOverlay _cov;
        public AswMapProvider(CoverageOverlay cov) { _cov = cov; }

        /// <summary>[Asw] Debug: log "asw proj" (screen vs local projection) at most every 5 s while the map is drawn.</summary>
        public bool Debug;
        public Action<string> Log;
        private double _projLogAt = -1e9;

        public bool TryProjection(double refLat, double refLon, out double[] aff, out double[] rect)
        {
            aff = null; rect = null;
            object map;
            if (_cov == null || !_cov.TryFindMap(out map)) return false;
            Affine2 a;
            if (!_cov.TryProjection(map, refLat, refLon, out a, out rect)) return false;
            aff = new[] { a.A, a.B, a.C, a.D, a.E, a.F };
            if (Debug && Log != null && Time.realtimeSinceStartup - _projLogAt >= 5)
            {
                _projLogAt = Time.realtimeSinceStartup;
                try { string line = _cov.ProjDebugLine(map, refLat, refLon); if (line != null) Log(line); } catch { }
            }
            return true;
        }

        public bool TryPixelToGeo(double px, double py, double refLat, double refLon, out double lat, out double lon)
        {
            lat = lon = 0;
            object map;
            if (_cov == null || !_cov.TryFindMap(out map)) return false;
            Affine2 g; double mw, mh, lx, ly;
            if (!_cov.TryLocalProjection(map, refLat, refLon, out g, out mw, out mh) || !_cov.TryPixelToLocal(map, px, py, out lx, out ly)) return false;
            return AswWindowModel.PixelToGeo(new[] { g.A, g.B, g.C, g.D, g.E, g.F }, lx, ly, out lat, out lon);
        }

        /// <summary>Mod windows and game windows (pixels): the IMGUI overlay is cut around them.</summary>
        public List<double[]> OccludedRects()
        {
            return _cov == null ? new List<double[]>() : _cov.OccludedRects();
        }
    }

    /// <summary>T-095 / D3: barriers, buoys (brightness = remaining life), the search area, contacts with the localization radius. IMGUI Repaint + GL.</summary>
    internal sealed class AswOverlay
    {
        private readonly Func<AswView> _view;
        private readonly IAswMap _map;
        private readonly Action<string> _log;
        private Material _mat, _matPm;
        private static bool _premul;   // drawing into the layer's render texture: premultiplied alpha

        private static Color Pm(Color c) { return _premul ? new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a) : c; }
        private bool _errLogged;

        /// <summary>True while the Noesis layer owns the drawing (then OnGUI stays silent and the layer calls DrawGeo).</summary>
        public Func<bool> NoesisActive;

        /// <summary>T-174 W5: the window's "Route" flag of the area order (null = off): the buoys, lines and the remaining plan of an area order are drawn only with it.</summary>
        public Func<bool> AreaRoute;
        public Func<bool> CourseRoute;   // T-176 W6: the persistent "Route" flag of the course order

        public AswOverlay(Func<AswView> view, IAswMap map, Action<string> log)
        {
            _view = view; _map = map; _log = log ?? (m => { });
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null)
            {
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _mat.SetInt("_ZWrite", 0);
                _matPm = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _matPm.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                _matPm.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _matPm.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _matPm.SetInt("_ZWrite", 0);
            }
        }

        public void OnGUI()
        {
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint || _mat == null) return;
                if (NoesisActive != null && NoesisActive()) return;
                var v = _view();
                if (v == null) return;
                bool any = v.Preview.Count > 0;
                foreach (var g in v.Groups) if (g.Order != "none" || g.Buoys.Count > 0) any = true;
                if (HasSar(v)) any = true;
                if (!any) return;
                double[] aff, rect;
                if (!_map.TryProjection(v.RefLat, v.RefLon, out aff, out rect)) return;
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
                        foreach (var g in v.Groups) DrawGroup(g, aff);
                        DrawPreview(v, aff);
                        DrawSar(v, aff);
                    }
                    finally
                    {
                        GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
                        GL.PopMatrix();
                    }
                }
                DrawRaftLabels(v, aff, rect, occ);
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; _log("asw overlay failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>The group geometry has something to show (the layer redraws about once a second while true).</summary>
        public bool HasContent()
        {
            try
            {
                var v = _view();
                if (v == null) return false;
                if (v.Preview.Count > 0) return true;
                foreach (var g in v.Groups) if (g.Order != "none" || g.Buoys.Count > 0) return true;
                if (HasSar(v)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Reference point of the current view (for the layer's projection when no radar frame exists).</summary>
        public bool RefPoint(out double lat, out double lon)
        {
            lat = lon = 0;
            try { var v = _view(); if (v == null) return false; lat = v.RefLat; lon = v.RefLon; return true; }
            catch { return false; }
        }

        /// <summary>Noesis path: draws into the layer's render texture. The caller has set the target and the pixel matrix (RT pixels, y down);
        /// affRt = {A,B,C,D,E,F} geo -> RT pixels (MapLayerMath.LocalToRt). Throws are caught by the layer (it switches itself off for 5 s).</summary>
        public void DrawGeo(double[] affRt)
        {
            if (_matPm == null) return;
            var v = _view();
            if (v == null) return;
            _premul = true;
            try
            {
                _matPm.SetPass(0);
                foreach (var g in v.Groups) DrawGroup(g, affRt);
                DrawPreview(v, affRt);
                DrawSar(v, affRt);
            }
            finally { _premul = false; }
        }

        private static Vector2 P(double[] a, double lat, double lon)
        {
            return new Vector2((float)(a[0] * lat + a[1] * lon + a[2]), (float)(a[3] * lat + a[4] * lon + a[5]));
        }

        private static void Line(Vector2 a, Vector2 b, Color c)
        {
            GlSafe.Lines(() => { GL.Color(Pm(c)); GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0); });
        }

        private static void Circle(Vector2 c, float r, Color col)
        {
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

        private static Color PreviewColor(string tactic)
        {
            // the colours of the group lines of the same tactic, half transparent
            return tactic == "cross" || tactic == "barrier" ? new Color(1f, 0.85f, 0.2f, 0.55f) : tactic == "dip" ? new Color(0.5f, 1f, 0.5f, 0.65f)
                 : tactic == "mad" ? new Color(0.9f, 0.5f, 1f, 0.65f) : new Color(0.4f, 0.9f, 1f, 0.55f);
        }

        /// <summary>Dashed segment (pixels): on/off pattern restarts per segment.</summary>
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

        /// <summary>Dry-run route of the order being composed (AswView.Preview): dashed, not saved anywhere.</summary>
        private void DrawPreview(AswView v, double[] aff)
        {
            foreach (var l in v.Preview)
            {
                var col = PreviewColor(l.Tactic);
                for (int i = 1; i < l.Points.Count; i++) Dashed(P(aff, l.Points[i - 1].Lat, l.Points[i - 1].Lon), P(aff, l.Points[i].Lat, l.Points[i].Lon), col);
                foreach (var pt in l.Points)
                {
                    var p = P(aff, pt.Lat, pt.Lon);
                    Line(new Vector2(p.x - 3, p.y), new Vector2(p.x + 3, p.y), col);
                    Line(new Vector2(p.x, p.y - 3), new Vector2(p.x, p.y + 3), col);
                }
            }
        }

        // ---- T-109: rafts, rescuer -> target lines, ship routes ---------------------------------------------------------

        private static bool HasSar(AswView v) { return v.RaftsDrawn || (v.ShipsSarJson != null && v.ShipsSarJson.Length > 2); }

        /// <summary>Raft colour by state: threat red, unreachable grey, claimed/covered green, free yellow.</summary>
        internal static Color RaftColor(string state)
        {
            switch (state)
            {
                case "threat": return new Color(1f, 0.25f, 0.2f, 0.95f);
                case "unreachable": return new Color(0.6f, 0.6f, 0.6f, 0.8f);
                case "claimed": case "covered": return new Color(0.4f, 1f, 0.5f, 0.95f);
                default: return new Color(1f, 0.9f, 0.3f, 0.95f);
            }
        }

        private string _shipsSrc;
        private List<ShipRun> _ships = new List<ShipRun>();

        private sealed class ShipRun { public int Uid, Raft; public bool HasPos; public double Lat, Lon; public List<GeoPt> Route = new List<GeoPt>(); }

        private static double D(object o) { return o is double ? (double)o : double.NaN; }

        /// <summary>ShipsSarJson -> runs. lat/lon/route are optional (read when the controller provides them); parsed only when the string changes.</summary>
        private List<ShipRun> Ships(AswView v)
        {
            if (ReferenceEquals(v.ShipsSarJson, _shipsSrc) || v.ShipsSarJson == _shipsSrc) return _ships;
            _shipsSrc = v.ShipsSarJson;
            var res = new List<ShipRun>();
            try
            {
                var arr = Json.Parse(_shipsSrc) as List<object>;
                if (arr != null)
                    foreach (var o in arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        var sr = new ShipRun();
                        object x;
                        if (d.TryGetValue("uid", out x) && !double.IsNaN(D(x))) sr.Uid = (int)D(x);
                        if (d.TryGetValue("raft", out x) && !double.IsNaN(D(x))) sr.Raft = (int)D(x);
                        double la = double.NaN, lo = double.NaN;
                        if (d.TryGetValue("lat", out x)) la = D(x);
                        if (d.TryGetValue("lon", out x)) lo = D(x);
                        if (!double.IsNaN(la) && !double.IsNaN(lo)) { sr.HasPos = true; sr.Lat = la; sr.Lon = lo; }
                        if (d.TryGetValue("route", out x) && x is List<object>)
                            foreach (var pt in (List<object>)x)
                            {
                                var pl = pt as List<object>;
                                if (pl != null && pl.Count >= 2 && !double.IsNaN(D(pl[0])) && !double.IsNaN(D(pl[1]))) sr.Route.Add(new GeoPt(D(pl[0]), D(pl[1])));
                            }
                        res.Add(sr);
                    }
            }
            catch (Exception) { }
            _ships = res;
            return res;
        }

        private static readonly List<AswRaftView> NoRafts = new List<AswRaftView>();
        private static List<AswRaftView> Drawn(AswView v) { return v.SarIdle ? NoRafts : v.Rafts; }   // T-192: idle rafts are in the view / ui_state, not on the map

        private void DrawSar(AswView v, double[] aff)
        {
            foreach (var r in Drawn(v))
            {
                var p = P(aff, r.Lat, r.Lon);
                var col = RaftColor(r.State);
                Circle(p, 4, col);
                Line(new Vector2(p.x - 2, p.y), new Vector2(p.x + 2, p.y), col);
                // people: one tick per person (max 10) above the dot, so the count is readable without text (Noesis path has no labels)
                int n = Math.Min(10, r.People);
                for (int i = 0; i < n; i++)
                    Line(new Vector2(p.x - 2 * n + 4 * i + 1, p.y - 8), new Vector2(p.x - 2 * n + 4 * i + 1, p.y - 12), col);
            }
            var byId = new Dictionary<int, AswRaftView>();
            foreach (var r in Drawn(v)) byId[r.Id] = r;
            // helicopter -> its target raft
            var heloCol = new Color(1f, 0.95f, 0.5f, 0.7f);
            foreach (var row in v.Aircraft)
            {
                AswRaftView tr;
                if (row.HasPos && row.TargetRaft > 0 && byId.TryGetValue(row.TargetRaft, out tr))
                    Dashed(P(aff, row.Lat, row.Lon), P(aff, tr.Lat, tr.Lon), heloCol);
            }
            var ships = Ships(v);
            var lineCol = new Color(0.4f, 0.9f, 1f, 0.8f);
            foreach (var sr in ships)
            {
                GeoPt prev = sr.HasPos ? new GeoPt(sr.Lat, sr.Lon) : default(GeoPt);
                bool havePrev = sr.HasPos;
                foreach (var pt in sr.Route)
                {
                    if (havePrev) Dashed(P(aff, prev.Lat, prev.Lon), P(aff, pt.Lat, pt.Lon), lineCol);
                    prev = pt; havePrev = true;
                }
                AswRaftView target;
                if (sr.HasPos && sr.Route.Count == 0 && sr.Raft > 0 && byId.TryGetValue(sr.Raft, out target))
                    Line(P(aff, sr.Lat, sr.Lon), P(aff, target.Lat, target.Lon), lineCol);
            }
        }

        private static GUIStyle _raftLabel;

        /// <summary>IMGUI path only: the number of people next to the raft dot (screen pixels = IMGUI pixels).</summary>
        private void DrawRaftLabels(AswView v, double[] aff, double[] rect, List<double[]> occ)
        {
            if (!v.RaftsDrawn) return;
            if (_raftLabel == null) { _raftLabel = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold }; }
            foreach (var r in Drawn(v))
            {
                var p = P(aff, r.Lat, r.Lon);
                if (p.x < rect[0] || p.y < rect[1] || p.x > rect[0] + rect[2] || p.y > rect[1] + rect[3]) continue;
                bool hidden = false;
                foreach (var o in occ) if (p.x >= o[0] && p.x <= o[0] + o[2] && p.y >= o[1] && p.y <= o[1] + o[3]) { hidden = true; break; }
                if (hidden) continue;
                _raftLabel.normal.textColor = RaftColor(r.State);
                GUI.Label(new Rect(p.x + 6, p.y - 8, 40, 16), r.People.ToString(), _raftLabel);
            }
        }

        private static readonly Color[] LaneColors =
        {
            new Color(0.4f, 0.9f, 1f, 0.6f), new Color(1f, 0.75f, 0.3f, 0.6f), new Color(0.8f, 0.6f, 1f, 0.6f), new Color(0.6f, 1f, 0.5f, 0.6f),
        };

        private static Color TacticColor(string t, float a)
        {
            return t == "barrier" ? new Color(1f, 0.85f, 0.2f, a) : t == "dip" ? new Color(0.5f, 1f, 0.5f, a) : t == "mad" ? new Color(0.9f, 0.5f, 1f, a) : new Color(0.4f, 0.9f, 1f, a);
        }

        /// <summary>T-176 W7 (ADR-0076 s.8): one line of the plan by its kind. Laid (State "done") = solid, planned = thin or dashed, deferred = a grey circle with a slash, lost ring = red dashed.</summary>
        private void DrawLine(AswLineView l, double[] aff)
        {
            bool done = l.State == "done";
            float fade = Mathf.Clamp01(l.Alpha);   // T-322: a stale barrier fades out, then it is not drawn
            if (fade <= 0f) return;
            if (l.Kind == "strike_aim")   // T-173: the shooter -> the drop point
            {
                for (int i = 1; i < l.Points.Count; i++) Dashed(P(aff, l.Points[i - 1].Lat, l.Points[i - 1].Lon), P(aff, l.Points[i].Lat, l.Points[i].Lon), new Color(1f, 0.55f, 0.2f, 0.8f));
                return;
            }
            if (l.Kind == "deferred")
            {
                if (l.Points.Count == 0) return;
                var p = P(aff, l.Points[0].Lat, l.Points[0].Lon);
                var gc = new Color(0.7f, 0.7f, 0.7f, 0.9f);
                Circle(p, 5, gc);
                Line(new Vector2(p.x - 4, p.y + 4), new Vector2(p.x + 4, p.y - 4), gc);
                return;
            }
            if (l.Kind == "lost_ring" || l.Kind == "standoff" || l.Kind == "route" || (!done && (l.Kind == "barrier_line" || l.Kind == "dip_chain")))
            {
                Color dc = l.Kind == "lost_ring" ? new Color(1f, 0.3f, 0.3f, 0.7f) : l.Kind == "route" ? LaneColors[Math.Max(0, l.Lane) % LaneColors.Length] : TacticColor(l.Tactic, 0.55f);
                for (int i = 1; i < l.Points.Count; i++) Dashed(P(aff, l.Points[i - 1].Lat, l.Points[i - 1].Lon), P(aff, l.Points[i].Lat, l.Points[i].Lon), Faded(dc, fade));
                return;
            }
            Color col = l.Kind == "cross" ? new Color(1f, 0.85f, 0.2f, 0.5f) : l.Kind == "dip" ? new Color(0.5f, 1f, 0.5f, 0.6f) : l.Kind == "mad" ? new Color(0.9f, 0.5f, 1f, 0.6f)
                      : l.Kind == "contact" ? new Color(1f, 0.3f, 0.3f, 0.7f)
                      : l.Kind == "corridor_row" ? TacticColor("corridor", done ? 0.5f : 0.3f) : l.Kind == "barrier_line" ? TacticColor("barrier", 0.7f) : l.Kind == "dip_chain" ? TacticColor("dip", 0.7f)
                      : new Color(0.4f, 0.9f, 1f, 0.35f);
            for (int i = 1; i < l.Points.Count; i++) Line(P(aff, l.Points[i - 1].Lat, l.Points[i - 1].Lon), P(aff, l.Points[i].Lat, l.Points[i].Lon), Faded(col, fade));
        }

        private static Color Faded(Color c, float f) { return new Color(c.r, c.g, c.b, c.a * f); }

        // T-180: the arrow of a retreating machine to its cover ship; the colour is the retreat one, the dark shadow (1 px down-right) keeps it readable on a light map
        private static readonly Color RetreatColor = new Color(1f, 0.55f, 0.1f, 0.95f), RetreatShadow = new Color(0f, 0f, 0f, 0.6f);

        private void DrawRetreatArrows(AswGroupView g, double[] aff)
        {
            if (g.RetreatArrows == null) return;
            foreach (var a in g.RetreatArrows)
            {
                var p0 = P(aff, a.FromLat, a.FromLon); var p1 = P(aff, a.ToLat, a.ToLon);
                var head = AswDrawPlan.ArrowHead(p0.x, p0.y, p1.x, p1.y);
                var sh = new Vector2(1f, 1f);
                if (a.Away) { Dashed(p0 + sh, p1 + sh, RetreatShadow); Dashed(p0, p1, RetreatColor); }
                else { Line(p0 + sh, p1 + sh, RetreatShadow); Line(p0, p1, RetreatColor); }
                if (head == null) continue;
                Line(p1 + sh, new Vector2(head[0], head[1]) + sh, RetreatShadow); Line(p1 + sh, new Vector2(head[2], head[3]) + sh, RetreatShadow);
                Line(p1, new Vector2(head[0], head[1]), RetreatColor); Line(p1, new Vector2(head[2], head[3]), RetreatColor);
            }
        }

        private void DrawGroup(AswGroupView g, double[] aff)
        {
            DrawRetreatArrows(g, aff);   // T-180: always, no "Route" flag
            var set = AswDrawPlan.For(g, g.Order == "course" ? CourseRoute != null && CourseRoute() : AreaRoute != null && AreaRoute());
            // search area
            if (set.Outline && g.Rect != null && g.Rect.Length == 8)
            {
                var col = new Color(0.3f, 0.8f, 1f, 0.9f);
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) % 4;
                    Line(P(aff, g.Rect[2 * i], g.Rect[2 * i + 1]), P(aff, g.Rect[2 * j], g.Rect[2 * j + 1]), col);
                }
            }
            // planned barriers and passes ("contact" / "lost_ring" lines go with the contacts); at most 400 segments (the dashed routes go first)
            foreach (var l in AswDrawPlan.Budget(g.Lines))
            {
                if (!(AswDrawPlan.WithContacts(l) ? set.Contacts : set.Lines)) continue;
                if (l.Kind == "area") continue;   // the grid itself is drawn as buoys
                DrawLine(l, aff);
            }
            // the rest of the area plan: dashed in the colour of its tactic
            if (set.AreaPlan)
                foreach (var l in g.AreaPlan)
                {
                    var pc = PreviewColor(l.Tactic);
                    for (int i = 1; i < l.Points.Count; i++) Dashed(P(aff, l.Points[i - 1].Lat, l.Points[i - 1].Lon), P(aff, l.Points[i].Lat, l.Points[i].Lon), pc);
                }
            // buoys: cross marker, brightness = remaining life
            if (set.Buoys)
            foreach (var b in g.Buoys)
            {
                var p = P(aff, b.Lat, b.Lon);
                float a = 0.25f + 0.75f * Mathf.Clamp01((float)b.LifeFrac);
                var col = new Color(0.4f, 1f, 0.6f, a);
                Line(new Vector2(p.x - 4, p.y), new Vector2(p.x + 4, p.y), col);
                Line(new Vector2(p.x, p.y - 4), new Vector2(p.x, p.y + 4), col);
            }
            // the targets of a strike group: a cross on each
            if (set.Contacts)
                foreach (var t in g.StrikeTargets)
                {
                    var tp = P(aff, t.Lat, t.Lon);
                    var tc = t.Skipped ? new Color(0.7f, 0.7f, 0.7f, 0.8f) : new Color(1f, 0.35f, 0.2f, 0.9f);
                    Line(new Vector2(tp.x - 6, tp.y - 6), new Vector2(tp.x + 6, tp.y + 6), tc);
                    Line(new Vector2(tp.x - 6, tp.y + 6), new Vector2(tp.x + 6, tp.y - 6), tc);
                }
            // contacts and the localization radius
            if (set.Contacts)
            foreach (var c in g.Contacts)
            {
                var p = P(aff, c.Lat, c.Lon);
                var col = new Color(1f, 0.25f, 0.25f, 0.95f);
                Circle(p, 6, col);
                // radius in pixels from the local scale: one mile of latitude
                var p2 = P(aff, c.Lat + 1.0 / 60.0, c.Lon);
                float pxPerNm = Mathf.Abs(p2.y - p.y);
                if (pxPerNm > 0.5f) Circle(p, (float)(0.8 * Math.Max(c.RNm, 1.25)) * pxPerNm, new Color(1f, 0.25f, 0.25f, 0.4f));
            }
        }
    }
}
