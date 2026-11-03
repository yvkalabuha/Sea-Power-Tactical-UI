// T-309 / ADR-0119: geo -> target pixels for every overlay of the mod. Pure (no Unity, no game). Kind = Affine keeps the old numbers byte for byte.
using System;

namespace VoiceBotBridge
{
    /// <summary>Receiver of pixel geometry (target pixels). Break() = the polyline is cut here (horizon / budget).</summary>
    internal interface IGeoSink
    {
        void Line(float x0, float y0, float x1, float y1);
        void Break();
    }

    /// <summary>Fallback "reflect": geo -> local map units through the game (TryLocationToView). false = behind the horizon / error.</summary>
    internal delegate bool GeoToLocalFn(double latDeg, double lonDeg, out double x, out double y);

    /// <summary>Everything that decides where a geo point lands. Equal keys = the same picture.</summary>
    internal struct MapPxKey
    {
        public MapProjKind Kind;
        public MapViewSnap Snap;
        public double Out0, Out1, Out2, Out3, Out4, Out5;   // Ortho/Reflect: local -> target; Affine: geo -> target (the effective fit)
        public int OutW, OutH;
    }

    internal sealed class MapPx
    {
        private MapProjKind _kind;
        private MapViewSnap _s;
        private double[] _aff;                 // Affine: geo -> target {a0..a5}
        private GeoToLocalFn _fn;
        private double _o0, _o1, _o2, _o3, _o4, _o5, _pxPerM;
        private int _outW, _outH;
        private int _verts, _cut;
        // polyline walker
        private bool _wHave, _wPrevVis; private V3 _wPrevF; private double _wPx, _wPy;
        // grid tables
        private double[] _sLat = new double[0], _cLat = new double[0], _sDl = new double[0], _cDl = new double[0];
        private int _gRows, _gCols; private double _gLatMax, _gDLat, _gLonMin, _gDLon;

        public int VertexBudget { get; set; }
        public int Verts { get { return _verts; } }
        public int Cut { get { return _cut; } }
        public bool OverBudget { get { return _verts >= Limit; } }
        public void ResetCounters() { _verts = 0; _cut = 0; _secLimit = int.MaxValue; _secV0 = 0; _secC0 = 0; }

        // T-311: budget sections. A section limits the vertices of one layer to a share of the frame budget; the rest stays for the others.
        private int _secLimit = int.MaxValue, _secV0, _secC0;
        /// <summary>Effective limit = min(VertexBudget, section limit).</summary>
        public int Limit { get { return Math.Min(VertexBudget, _secLimit); } }
        public int Remaining { get { return Math.Max(0, Limit - _verts); } }
        public int SectionVerts { get { return _verts - _secV0; } }
        public int SectionCut { get { return _cut - _secC0; } }
        /// <summary>share &lt;= 0: no section limit. No nesting: a new BeginSection replaces the old one.</summary>
        public void BeginSection(int share)
        {
            _secV0 = _verts; _secC0 = _cut;
            _secLimit = share <= 0 ? int.MaxValue : (int)Math.Min((long)int.MaxValue, (long)_verts + share);
        }
        /// <summary>Limit back to VertexBudget; SectionVerts/SectionCut keep their last values until the next BeginSection/ResetCounters.</summary>
        public void EndSection() { _secLimit = int.MaxValue; }
        public int Share(double fraction) { double v = Math.Floor(VertexBudget * fraction); return v < 0 ? 0 : (int)Math.Min(v, int.MaxValue); }
        /// <summary>Budget accounting for callers that emit through their own sink.</summary>
        public void AddVerts(int n) { _verts += n; }

        public MapProjKind Kind { get { return _kind; } }
        public MapViewSnap Snap { get { return _s; } }
        public int OutW { get { return _outW; } }
        public int OutH { get { return _outH; } }
        public double[] AffineGeo { get { return _aff; } }

        private MapPx() { }

        public static MapPx FromAffine(double[] affRt, int outW, int outH)
        {
            var p = new MapPx { _kind = MapProjKind.Affine, _outW = outW, _outH = outH, VertexBudget = 60000 };
            p._aff = new double[6];
            if (affRt != null) for (int i = 0; i < 6 && i < affRt.Length; i++) p._aff[i] = affRt[i];
            p._s = new MapViewSnap { Kind = MapProjKind.Affine, MapW = outW, MapH = outH };
            return p;
        }

        private void SetOut(double[] o)
        {
            if (o == null || o.Length < 6) o = new double[] { 1, 0, 0, 0, 1, 0 };
            _o0 = o[0]; _o1 = o[1]; _o2 = o[2]; _o3 = o[3]; _o4 = o[4]; _o5 = o[5];
            _pxPerM = _s.Scale * Math.Sqrt(Math.Abs(_o0 * _o4 - _o1 * _o3));
        }

        /// <summary>localToOut = {ax, bx, cx, ay, by, cy}. A snapshot of Kind = Affine is composed with it into an Affine MapPx.</summary>
        public static MapPx Create(in MapViewSnap snap, double[] localToOut, int outW, int outH)
        {
            var p = new MapPx { _s = snap, _outW = outW, _outH = outH };
            p.SetOut(localToOut);
            if (snap.Kind == MapProjKind.Affine)
            {
                Affine2 g = snap.GeoToLocal;
                p._kind = MapProjKind.Affine;
                p._aff = new[]
                {
                    p._o0 * g.A + p._o1 * g.D, p._o0 * g.B + p._o1 * g.E, p._o0 * g.C + p._o1 * g.F + p._o2,
                    p._o3 * g.A + p._o4 * g.D, p._o3 * g.B + p._o4 * g.E, p._o3 * g.C + p._o4 * g.F + p._o5,
                };
                p.VertexBudget = 60000;
            }
            else { p._kind = MapProjKind.Ortho; p.VertexBudget = 60000; }
            return p;
        }

        public static MapPx Reflect(in MapViewSnap snap, GeoToLocalFn fn, double[] localToOut, int outW, int outH)
        {
            MapPx p = Create(in snap, localToOut, outW, outH);
            if (p._kind == MapProjKind.Ortho && fn != null) { p._kind = MapProjKind.Reflect; p._fn = fn; p.VertexBudget = 4000; }
            return p;
        }

        // ---- points --------------------------------------------------------------------------------------------------------------------
        private void LocalToOut(double lx, double ly, out double x, out double y)
        {
            x = _o0 * lx + _o1 * ly + _o2;
            y = _o3 * lx + _o4 * ly + _o5;
        }

        private void FrameToOut(V3 f, out double x, out double y)
        {
            double lx, ly;
            MapProjector.FrameToView(in _s, f, out lx, out ly);
            LocalToOut(lx, ly, out x, out y);
        }

        public bool Pt(double lat, double lon, out double x, out double y)
        {
            if (_kind == MapProjKind.Affine)
            {
                x = _aff[0] * lat + _aff[1] * lon + _aff[2];
                y = _aff[3] * lat + _aff[4] * lon + _aff[5];
                return true;
            }
            V3 f = MapProjector.GeoToFrame(in _s, lat, lon);
            if (!(f.Z >= 0)) { x = 0; y = 0; return false; }
            if (_kind == MapProjKind.Reflect)
            {
                double lx, ly;
                if (!_fn(lat, lon, out lx, out ly)) { x = 0; y = 0; return false; }
                LocalToOut(lx, ly, out x, out y);
                return true;
            }
            FrameToOut(f, out x, out y);
            return true;
        }

        /// <summary>For fills: a hidden point is pressed to the limb; false = it was hidden.</summary>
        public bool PtClamped(double lat, double lon, out double x, out double y)
        {
            if (_kind == MapProjKind.Affine) return Pt(lat, lon, out x, out y);
            if (Pt(lat, lon, out x, out y)) return true;
            V3 f = MapProjector.GeoToFrame(in _s, lat, lon);
            if (double.IsNaN(f.Z)) { x = 0; y = 0; return false; }
            if (f.Z >= 0) return false;   // reflect: the game refused a visible point; nothing sensible to draw
            FrameToOut(MapProjector.ClampToLimb(f), out x, out y);
            return false;
        }

        public bool TryInverse(double x, double y, out double lat, out double lon)
        {
            lat = 0; lon = 0;
            if (_kind == MapProjKind.Affine)
            {
                double det = _aff[0] * _aff[4] - _aff[1] * _aff[3];
                if (!(Math.Abs(det) > 1e-300)) return false;
                double rx = x - _aff[2], ry = y - _aff[5];
                lat = (_aff[4] * rx - _aff[1] * ry) / det;
                lon = (-_aff[3] * rx + _aff[0] * ry) / det;
                return !double.IsNaN(lat) && !double.IsNaN(lon);
            }
            double d = _o0 * _o4 - _o1 * _o3;
            if (!(Math.Abs(d) > 1e-300)) return false;
            double ox = x - _o2, oy = y - _o5;
            double lx = (_o4 * ox - _o1 * oy) / d, ly = (-_o3 * ox + _o0 * oy) / d;
            return MapProjector.ViewToGeo(in _s, lx, ly, out lat, out lon);
        }

        /// <summary>Target pixels per nautical mile at a point. Affine: the pixel step of 1' of latitude (as before).</summary>
        public double PxPerNm(double lat, double lon)
        {
            if (_kind == MapProjKind.Affine)
            {
                float x0 = (float)(_aff[0] * lat + _aff[1] * lon + _aff[2]), y0 = (float)(_aff[3] * lat + _aff[4] * lon + _aff[5]);
                float x1 = (float)(_aff[0] * (lat + 1.0 / 60.0) + _aff[1] * lon + _aff[2]), y1 = (float)(_aff[3] * (lat + 1.0 / 60.0) + _aff[4] * lon + _aff[5]);
                double dx = (double)x1 - x0, dy = (double)y1 - y0;
                return Math.Sqrt(dx * dx + dy * dy);
            }
            V3 f = MapProjector.GeoToFrame(in _s, lat, lon);
            return _pxPerM * Math.Sqrt(Math.Max(f.Z, 0.001)) * 1852.0;
        }

        // ---- lines and arcs --------------------------------------------------------------------------------------------------------------
        private void WalkBegin() { _wHave = false; _wPrevVis = false; }

        private void EmitLine(IGeoSink sink, double x0, double y0, double x1, double y1)
        {
            sink.Line((float)x0, (float)y0, (float)x1, (float)y1);
        }

        /// <summary>Feeds the next point (unit vector) of a polyline: lines for visible pieces, cut at the horizon, budgeted.</summary>
        private void Walk(V3 u, IGeoSink sink)
        {
            V3 f = MapProjector.UnitToFrame(in _s, u);
            bool vis = f.Z >= 0;
            if (++_verts > Limit) { _cut++; if (_wHave && _wPrevVis) { sink.Break(); } _wHave = false; _wPrevVis = false; return; }
            double px = 0, py = 0;
            if (vis) PlaceUnit(u, f, out px, out py);
            if (!_wHave) { _wHave = true; _wPrevVis = vis; _wPrevF = f; _wPx = px; _wPy = py; return; }
            if (_wPrevVis && vis)
            {
                EmitLine(sink, _wPx, _wPy, px, py);
            }
            else if (_wPrevVis)
            {
                V3 c;
                if (MapProjector.HorizonCrossing(_wPrevF, f, out c))
                {
                    double cx, cy; FrameToOut(c, out cx, out cy);
                    EmitLine(sink, _wPx, _wPy, cx, cy);
                }
                sink.Break(); _cut++;
            }
            else if (vis)
            {
                V3 c;
                if (MapProjector.HorizonCrossing(_wPrevF, f, out c))
                {
                    double cx, cy; FrameToOut(c, out cx, out cy);
                    EmitLine(sink, cx, cy, px, py);
                }
                _cut++;
            }
            _wPrevVis = vis; _wPrevF = f; _wPx = px; _wPy = py;
        }

        private void PlaceUnit(V3 u, V3 f, out double x, out double y)
        {
            if (_kind == MapProjKind.Reflect)
            {
                double lat, lon, lx, ly;
                MapProjector.UnitToGeo(u, out lat, out lon);
                if (_fn(lat, lon, out lx, out ly)) { LocalToOut(lx, ly, out x, out y); return; }
            }
            FrameToOut(f, out x, out y);
        }

        /// <summary>The point at radiusNm from a point along a bearing: geodesic (Ortho/Reflect), FlatOffset (Affine).</summary>
        public void OffsetGeo(double lat, double lon, double bearingDeg, double nm, out double outLat, out double outLon)
        {
            if (_kind == MapProjKind.Affine) { MapProjector.FlatOffset(lat, lon, bearingDeg, nm, out outLat, out outLon); return; }
            MapProjector.UnitToGeo(MapProjector.OffsetOnSphere(lat, lon, bearingDeg, nm * 1852.0 / MapProjector.R), out outLat, out outLon);
        }

        /// <summary>A great-circle segment between two points, cut at the horizon. Affine: a straight line.</summary>
        public void Line(double lat0, double lon0, double lat1, double lon1, IGeoSink sink)
        {
            if (sink == null) return;
            if (_kind == MapProjKind.Affine)
            {
                sink.Line((float)(_aff[0] * lat0 + _aff[1] * lon0 + _aff[2]), (float)(_aff[3] * lat0 + _aff[4] * lon0 + _aff[5]),
                          (float)(_aff[0] * lat1 + _aff[1] * lon1 + _aff[2]), (float)(_aff[3] * lat1 + _aff[4] * lon1 + _aff[5]));
                _verts += 2;
                return;
            }
            V3 a = MapProjector.GeoToUnit(lat0, lon0), b = MapProjector.GeoToUnit(lat1, lon1);
            double theta = MapProjector.Angle(a, b);
            if (double.IsNaN(theta)) return;
            int n = MapProjector.StepsFor(theta, _pxPerM);
            WalkBegin();
            for (int i = 0; i <= n; i++)
            {
                V3 u = i == 0 ? a : i == n ? b : MapProjector.Slerp(a, b, (double)i / n, theta);
                Walk(u, sink);
            }
        }

        private int ArcSteps(double radiusNm, double sweepDeg, int minSteps)
        {
            int n = MapProjector.StepsForArc(Math.Abs(sweepDeg) * Math.PI / 180.0, radiusNm * 1852.0 / MapProjector.R, _pxPerM);
            if (minSteps > n) n = minSteps;
            return n < 1 ? 1 : n > MapProjector.MaxSteps ? MapProjector.MaxSteps : n;
        }

        /// <summary>A circle arc of radiusNm around a point, from bearing b0 over sweep degrees. Geodesic (Ortho/Reflect); Affine: FlatOffset, n = minSteps.</summary>
        public void Arc(double lat, double lon, double radiusNm, double b0Deg, double sweepDeg, int minSteps, IGeoSink sink)
        {
            if (sink == null) return;
            if (_kind == MapProjKind.Affine)
            {
                int na = minSteps < 1 ? 1 : minSteps;
                float px0 = 0, py0 = 0;
                for (int i = 0; i <= na; i++)
                {
                    double la, lo;
                    MapProjector.FlatOffset(lat, lon, b0Deg + sweepDeg * i / na, radiusNm, out la, out lo);
                    float px = (float)(_aff[0] * la + _aff[1] * lo + _aff[2]), py = (float)(_aff[3] * la + _aff[4] * lo + _aff[5]);
                    if (i > 0) sink.Line(px0, py0, px, py);
                    px0 = px; py0 = py;
                }
                _verts += na + 1;
                return;
            }
            int n = ArcSteps(radiusNm, sweepDeg, minSteps);
            double ang = radiusNm * 1852.0 / MapProjector.R;
            WalkBegin();
            for (int i = 0; i <= n; i++) Walk(MapProjector.OffsetOnSphere(lat, lon, b0Deg + sweepDeg * i / n, ang), sink);
        }

        /// <summary>T-321: the arc of a ring in the metric of the game (nm per degree, RangeRingKin.OffsetMetric) instead of true miles. geoNm = the geodesic radius
        /// that encloses it (the step count). Affine: n = minSteps.</summary>
        public void ArcMetric(double lat, double lon, double radiusNm, double geoNm, double b0Deg, double sweepDeg, int minSteps, Ui.RingGeoMetric m, IGeoSink sink)
        {
            if (sink == null) return;
            if (!m.Valid) { Arc(lat, lon, radiusNm, b0Deg, sweepDeg, minSteps, sink); return; }
            if (_kind == MapProjKind.Affine)
            {
                int na = minSteps < 1 ? 1 : minSteps;
                float px0 = 0, py0 = 0;
                for (int i = 0; i <= na; i++)
                {
                    double la, lo;
                    Ui.RangeRingKin.OffsetMetric(lat, lon, b0Deg + sweepDeg * i / na, radiusNm, m, out la, out lo);
                    float px = (float)(_aff[0] * la + _aff[1] * lo + _aff[2]), py = (float)(_aff[3] * la + _aff[4] * lo + _aff[5]);
                    if (i > 0) sink.Line(px0, py0, px, py);
                    px0 = px; py0 = py;
                }
                _verts += na + 1;
                return;
            }
            int n = ArcSteps(geoNm, sweepDeg, minSteps);
            WalkBegin();
            for (int i = 0; i <= n; i++)
            {
                double la, lo;
                Ui.RangeRingKin.OffsetMetric(lat, lon, b0Deg + sweepDeg * i / n, radiusNm, m, out la, out lo);
                Walk(MapProjector.GeoToUnit(la, lo), sink);
            }
        }

        /// <summary>T-321: ArcPoints in the metric of the game.</summary>
        public int ArcPointsMetric(double lat, double lon, double radiusNm, double geoNm, double b0Deg, double sweepDeg, int minSteps, Ui.RingGeoMetric m,
                                   float[] xs, float[] ys, bool[] vis)
        {
            if (!m.Valid) return ArcPoints(lat, lon, radiusNm, b0Deg, sweepDeg, minSteps, xs, ys, vis);
            int cap = Math.Min(Math.Min(xs.Length, ys.Length), vis == null ? int.MaxValue : vis.Length) - 1;
            if (cap < 1) return 0;
            int n;
            if (_kind == MapProjKind.Affine) n = minSteps < 1 ? 1 : minSteps;
            else n = ArcSteps(geoNm, sweepDeg, minSteps);
            if (n > cap) n = cap;
            for (int i = 0; i <= n; i++)
            {
                double la, lo, x, y;
                bool v;
                Ui.RangeRingKin.OffsetMetric(lat, lon, b0Deg + sweepDeg * i / n, radiusNm, m, out la, out lo);
                if (_kind == MapProjKind.Affine) { x = _aff[0] * la + _aff[1] * lo + _aff[2]; y = _aff[3] * la + _aff[4] * lo + _aff[5]; v = true; }
                else v = PtClampedUnit(MapProjector.GeoToUnit(la, lo), out x, out y);
                xs[i] = (float)x; ys[i] = (float)y; if (vis != null) vis[i] = v;
                _verts++;
            }
            return n + 1;
        }

        /// <summary>Arc points for fills: hidden points pressed to the limb. Returns n + 1 (at most xs.Length, 257).</summary>
        public int ArcPoints(double lat, double lon, double radiusNm, double b0Deg, double sweepDeg, int minSteps,
                             float[] xs, float[] ys, bool[] vis)
        {
            int cap = Math.Min(Math.Min(xs.Length, ys.Length), vis == null ? int.MaxValue : vis.Length) - 1;
            if (cap < 1) return 0;
            int n;
            if (_kind == MapProjKind.Affine) n = minSteps < 1 ? 1 : minSteps;
            else n = ArcSteps(radiusNm, sweepDeg, minSteps);
            if (n > cap) n = cap;
            double ang = radiusNm * 1852.0 / MapProjector.R;
            for (int i = 0; i <= n; i++)
            {
                double b = b0Deg + sweepDeg * i / n, x, y;
                bool v;
                if (_kind == MapProjKind.Affine)
                {
                    double la, lo;
                    MapProjector.FlatOffset(lat, lon, b, radiusNm, out la, out lo);
                    x = _aff[0] * la + _aff[1] * lo + _aff[2]; y = _aff[3] * la + _aff[4] * lo + _aff[5]; v = true;
                }
                else
                {
                    V3 u = MapProjector.OffsetOnSphere(lat, lon, b, ang);
                    v = PtClampedUnit(u, out x, out y);
                }
                xs[i] = (float)x; ys[i] = (float)y; if (vis != null) vis[i] = v;
                _verts++;
            }
            return n + 1;
        }

        private bool PtClampedUnit(V3 u, out double x, out double y)
        {
            V3 f = MapProjector.UnitToFrame(in _s, u);
            if (f.Z >= 0) { PlaceUnit(u, f, out x, out y); return true; }
            if (double.IsNaN(f.Z)) { x = 0; y = 0; return false; }
            FrameToOut(MapProjector.ClampToLimb(f), out x, out y);
            return false;
        }

        /// <summary>The whole circle is behind the horizon, or outside the target plus margin. Affine: never culled.</summary>
        public bool Culled(double lat, double lon, double radiusNm, double marginPx = 16)
        {
            if (_kind == MapProjKind.Affine) return false;
            V3 f = MapProjector.GeoToFrame(in _s, lat, lon);
            if (double.IsNaN(f.Z)) return true;
            double ang = radiusNm * 1852.0 / MapProjector.R;
            double z = f.Z > 1 ? 1 : f.Z < -1 ? -1 : f.Z;
            if (Math.Acos(z) - ang >= Math.PI / 2) return true;
            double x, y;
            FrameToOut(f, out x, out y);
            double chord = 2.0 * Math.Sin(Math.Min(ang, Math.PI) / 2.0);
            double rPx = MapProjector.R * _pxPerM * chord;
            return x + rPx < -marginPx || x - rPx > _outW + marginPx || y + rPx < -marginPx || y - rPx > _outH + marginPx;
        }

        // ---- raster grid ------------------------------------------------------------------------------------------------------------------
        /// <summary>Tables for a grid of rows x cols cells: corner (row, col) is at lat = latMax - row * dLat, lon = lonMin + col * dLon (row 0..rows, col 0..cols).</summary>
        public void PrepareGrid(double latMax, double dLat, int rows, double lonMin, double dLon, int cols)
        {
            if (rows < 0) rows = 0;
            if (cols < 0) cols = 0;
            _gRows = rows; _gCols = cols; _gLatMax = latMax; _gDLat = dLat; _gLonMin = lonMin; _gDLon = dLon;
            if (_kind == MapProjKind.Affine) return;
            if (_sLat.Length < rows + 1) { _sLat = new double[rows + 1]; _cLat = new double[rows + 1]; }
            if (_sDl.Length < cols + 1) { _sDl = new double[cols + 1]; _cDl = new double[cols + 1]; }
            const double rad = Math.PI / 180.0;
            for (int r = 0; r <= rows; r++) { double a = (latMax - r * dLat) * rad; _sLat[r] = Math.Sin(a); _cLat[r] = Math.Cos(a); }
            for (int c = 0; c <= cols; c++) { double a = (lonMin + c * dLon) * rad - _s.Lon0Rad; _sDl[c] = Math.Sin(a); _cDl[c] = Math.Cos(a); }
        }

        /// <summary>Corner (row, col) from the tables, hidden ones pressed to the limb; false = it was hidden (or out of the grid).</summary>
        public bool GridPt(int row, int col, out double x, out double y)
        {
            x = 0; y = 0;
            if (row < 0 || col < 0 || row > _gRows || col > _gCols) return false;
            _verts++;
            return GridCorner(row, col, out x, out y);
        }

        /// <summary>GridPt without counting (same coordinates and visibility).</summary>
        public bool GridCorner(int row, int col, out double x, out double y)
        {
            x = 0; y = 0;
            if (row < 0 || col < 0 || row > _gRows || col > _gCols) return false;
            double lat = _gLatMax - row * _gDLat, lon = _gLonMin + col * _gDLon;
            if (_kind == MapProjKind.Affine || _kind == MapProjKind.Reflect) return PtClamped(lat, lon, out x, out y);
            double sl = _sLat[row], cl = _cLat[row], sd = _sDl[col], cd = _cDl[col];
            var f = new V3(cl * sd, _s.CosLat0 * sl - _s.SinLat0 * cl * cd, _s.SinLat0 * sl + _s.CosLat0 * cl * cd);
            bool vis = f.Z >= 0;
            if (!vis)
            {
                if (double.IsNaN(f.Z)) return false;
                f = MapProjector.ClampToLimb(f);
            }
            FrameToOut(f, out x, out y);
            return vis;
        }

        // ---- redraw key -------------------------------------------------------------------------------------------------------------------
        public MapPxKey Key
        {
            get
            {
                var k = new MapPxKey { Kind = _kind, Snap = _s, OutW = _outW, OutH = _outH };
                if (_kind == MapProjKind.Affine) { k.Out0 = _aff[0]; k.Out1 = _aff[1]; k.Out2 = _aff[2]; k.Out3 = _aff[3]; k.Out4 = _aff[4]; k.Out5 = _aff[5]; }
                else { k.Out0 = _o0; k.Out1 = _o1; k.Out2 = _o2; k.Out3 = _o3; k.Out4 = _o4; k.Out5 = _o5; }
                return k;
            }
        }

        private static bool SnapEq(in MapViewSnap a, in MapViewSnap b)
        {
            return a.Lat0 == b.Lat0 && a.Lon0 == b.Lon0 && a.Scale == b.Scale && a.RotationDeg == b.RotationDeg
                && a.MapRefX == b.MapRefX && a.MapRefY == b.MapRefY && a.ViewRefX == b.ViewRefX && a.ViewRefY == b.ViewRefY
                && a.MapW == b.MapW && a.MapH == b.MapH;
        }

        /// <summary>Upper estimate of how far a visible point of the view moved (target px) between two keys. +inf for another Kind or target size; 0 for equal keys.
        /// Affine: 0 for equal coefficients, else +inf (the old ProjectionChanged decides there).</summary>
        public static double KeyShiftPx(in MapPxKey a, in MapPxKey b)
        {
            if (a.Kind != b.Kind || a.OutW != b.OutW || a.OutH != b.OutH) return double.PositiveInfinity;
            bool sameOut = a.Out0 == b.Out0 && a.Out1 == b.Out1 && a.Out2 == b.Out2 && a.Out3 == b.Out3 && a.Out4 == b.Out4 && a.Out5 == b.Out5;
            if (a.Kind == MapProjKind.Affine) return sameOut ? 0 : double.PositiveInfinity;
            if (!sameOut) return double.PositiveInfinity;
            if (SnapEq(in a.Snap, in b.Snap)) return 0;
            if (a.Snap.MapW != b.Snap.MapW || a.Snap.MapH != b.Snap.MapH) return double.PositiveInfinity;
            double w = a.Snap.MapW, h = a.Snap.MapH, worst = 0;
            for (int i = 0; i < 9; i++)
            {
                double lx = (i % 3) * 0.5 * w, ly = (i / 3) * 0.5 * h, lat, lon;
                if (!MapProjector.ViewToGeo(in a.Snap, lx, ly, out lat, out lon)) continue;
                double bx, by;
                if (!MapProjector.GeoToView(in b.Snap, lat, lon, out bx, out by)) continue;
                double dx = bx - lx, dy = by - ly;
                double ox = a.Out0 * dx + a.Out1 * dy, oy = a.Out3 * dx + a.Out4 * dy;
                double d = Math.Sqrt(ox * ox + oy * oy);
                if (double.IsNaN(d)) return double.PositiveInfinity;
                if (d > worst) worst = d;
            }
            return worst;
        }
    }
}
