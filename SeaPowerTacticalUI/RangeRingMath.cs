// T-137 range rings: pure geometry (no Unity, no game). Offset / segment count / firing-arc spans / schedule / Emit into an IRingPainter.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>Line = an outline segment; Tri = a fill triangle (2026-10-22: weapon rings and wedges are filled). Colours are premultiplied ARGB.</summary>
    internal interface IRingPainter
    {
        void Line(float ax, float ay, float bx, float by, uint argbPremul);
        void Tri(float ax, float ay, float bx, float by, float cx, float cy, uint argbPremul);
    }

    /// <summary>Opacity = the outline of sonar rings; weapon outlines use Opacity * OutlineK (0.85 * 0.7 = 0.6 by default). FillAlpha = the fill of a weapon
    /// ring / wedge ([RangeRings] FillAlpha, 0..0.4); FillCap = the most the fills of the whole scene may add up to where they overlap (the per-fill alpha
    /// is lowered when many fill groups are drawn). MaxTris = the fill triangle budget per draw.</summary>
    internal sealed class RingStyle
    {
        public double Opacity = 0.85, AllyAlphaK = 0.7, OutlineK = 0.7, FillAlpha = 0.12, FillCap = 0.35;
        public int MaxSegments = 3000, MaxTris = 6000;
        public double MinRadiusPx = 8;
    }

    internal struct RingDrawStats { public int Rings, Segments, SegmentsCut, SmallSkipped, Tris, TrisCut, FillGroups; public double PxPerNm, FillAlpha; /* RT pixels per nautical mile of the first drawn unit; the per-fill alpha used */ }

    internal static class RangeRingMath
    {
        public const double NmPerUnity = 0.036285132, NmPerKm = 0.5399568;
        private const double MinCosLat = 0.05;

        /// <summary>Flat offset of a point: bearingDeg clockwise from north, nm miles. cos(lat0) is floored at 0.05 near the poles.</summary>
        public static void Offset(double lat0, double lon0, double bearingDeg, double nm, out double lat, out double lon)
        {
            double b = bearingDeg * Math.PI / 180.0;
            double cl = Math.Cos(lat0 * Math.PI / 180.0);
            if (!(cl >= MinCosLat)) cl = MinCosLat;
            lat = lat0 + nm / 60.0 * Math.Cos(b);
            lon = lon0 + nm / 60.0 * Math.Sin(b) / cl;
        }

        /// <summary>clamp(ceil(rPx / 6), 24, 96); non-finite gives 24.</summary>
        public static int Segments(double rPx)
        {
            if (double.IsNaN(rPx) || double.IsInfinity(rPx)) return 24;
            double n = Math.Ceiling(rPx / 6.0);
            return n < 24 ? 24 : n > 96 ? 96 : (int)n;
        }

        /// <summary>
        /// Firing arcs (x = from, y = to, degrees clockwise from the bow, as _firingArcs) to {centerDeg in (-180,180], widthDeg} spans.
        /// Empty list = "no restriction" (null/empty input, any (0,0) arc, a full circle, or nothing usable). x &gt; y wraps through 0/360.
        /// </summary>
        public static List<double[]> ArcSpans(IList<ArcDeg> arcs)
        {
            var res = new List<double[]>();
            if (arcs == null || arcs.Count == 0) return res;
            foreach (ArcDeg a in arcs)
            {
                if (double.IsNaN(a.A) || double.IsNaN(a.B) || double.IsInfinity(a.A) || double.IsInfinity(a.B)) continue;
                if (a.A == 0 && a.B == 0) return new List<double[]>();
                double y = a.B;
                if (a.A > y) y += 360.0;
                double w = y - a.A;
                if (w >= 360.0) return new List<double[]>();
                if (!(w > 0)) continue;
                res.Add(new[] { NormDeg(a.A + w / 2.0), w });
            }
            return res;
        }

        private static double NormDeg(double d)
        {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            else if (d <= -180.0) d += 360.0;
            return d;
        }

        /// <summary>T-171: the ini value of FiringArcs "a,b|c,d" (degrees from the bow) -> arcs adapted to (-180, 180] like IniHandler. Blank -> [(0,0)] (all-round); a junk part -> [(0,0)] as well.</summary>
        public static List<ArcDeg> ParseArcs(string iniValue)
        {
            var all = new List<ArcDeg> { new ArcDeg { A = 0, B = 0 } };
            if (string.IsNullOrWhiteSpace(iniValue)) return all;
            var res = new List<ArcDeg>();
            foreach (string part in iniValue.Split('|'))
            {
                string[] ab = part.Split(',');
                double a, b;
                if (ab.Length != 2 || !double.TryParse(ab[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a)
                    || !double.TryParse(ab[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b)
                    || double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b)) return all;
                res.Add(new ArcDeg { A = Adapt(a), B = Adapt(b) });
            }
            return res.Count == 0 ? all : res;
        }

        private static double Adapt(double d)
        {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            else if (d < -180.0) d += 360.0;
            return d;
        }

        /// <summary>T-171: merges spans {centre, width} (overlapping or touching, also through 0) -> {centre in (-180,180], width}.</summary>
        public static List<double[]> UnionSpans(IList<double[]> spans)
        {
            var res = new List<double[]>();
            if (spans == null || spans.Count == 0) return res;
            var iv = new List<double[]>();
            foreach (double[] sp in spans)
            {
                if (sp == null || sp.Length < 2 || double.IsNaN(sp[0]) || double.IsNaN(sp[1]) || !(sp[1] > 0)) continue;
                double w = Math.Min(sp[1], 360.0);
                double start = (sp[0] - w / 2.0) % 360.0;
                if (start < 0) start += 360.0;
                double end = start + w;
                if (end > 360.0) { iv.Add(new[] { start, 360.0 }); iv.Add(new[] { 0.0, end - 360.0 }); }
                else iv.Add(new[] { start, end });
            }
            if (iv.Count == 0) return res;
            iv.Sort((x, y) => x[0].CompareTo(y[0]));
            var m = new List<double[]>();
            foreach (double[] x in iv)
            {
                if (m.Count > 0 && x[0] <= m[m.Count - 1][1] + 1e-9) { if (x[1] > m[m.Count - 1][1]) m[m.Count - 1][1] = x[1]; }
                else m.Add(new[] { x[0], x[1] });
            }
            if (m.Count > 1 && m[0][0] <= 1e-9 && m[m.Count - 1][1] >= 360.0 - 1e-9)
            {
                m[0][0] = m[m.Count - 1][0] - 360.0;
                m.RemoveAt(m.Count - 1);
            }
            foreach (double[] x in m) res.Add(new[] { NormDeg((x[0] + x[1]) / 2.0), x[1] - x[0] });
            return res;
        }

        /// <summary>T-171: course (deg clockwise from north, [0, 360)) from a velocity (east, north) in knots; below minKn it is unknown.</summary>
        public static bool CourseFromVel(double vxKn, double vzKn, double minKn, out double courseDeg)
        {
            courseDeg = 0;
            if (double.IsNaN(vxKn) || double.IsNaN(vzKn) || double.IsInfinity(vxKn) || double.IsInfinity(vzKn)) return false;
            double speed = Math.Sqrt(vxKn * vxKn + vzKn * vzKn);
            if (!(speed >= minKn) || !(speed > 0)) return false;
            double c = Math.Atan2(vxKn, vzKn) * 180.0 / Math.PI;
            if (c < 0) c += 360.0;
            if (c >= 360.0) c -= 360.0;
            courseDeg = c;
            return true;
        }

        /// <summary>Next redraw time (unscaled): refreshS at rest, airRefreshS when an own/tracked aircraft is in the scene.</summary>
        public static double NextDue(double now, bool anyAir, double refreshS, double airRefreshS)
        {
            double step = anyAir ? airRefreshS : refreshS;
            if (double.IsNaN(step) || step < 0.05) step = 0.05;
            return now + step;
        }

        // ---- Emit --------------------------------------------------------------------------------------------------------------------------
        private struct Rgb { public double R, G, B; public Rgb(double r, double g, double b) { R = r; G = g; B = b; } }

        /// <summary>ADR-0068 p. 4a: the colour follows the kind and what delivers the weapon, the same for own and class. 2026-10-22 (customer): a pale,
        /// desaturated palette (the rings are filled now); the hues stay apart: SAM sky blue, AA gun periwinkle, AShM apricot, torpedo pink, gun pale
        /// yellow, bombs / rockets tan, ASW pale green, sonar lilac.</summary>
        private static Rgb ColorOf(RingKind k, RingVia v)
        {
            switch (k)
            {
                case RingKind.Aaw: return v == RingVia.Gun ? new Rgb(0.74, 0.80, 0.97) : new Rgb(0.62, 0.86, 1.00);
                case RingKind.Asuw:
                    switch (v)
                    {
                        case RingVia.Torpedo: return new Rgb(1.00, 0.68, 0.70);
                        case RingVia.Gun: return new Rgb(0.97, 0.94, 0.62);
                        case RingVia.Bomb: case RingVia.Rocket: return new Rgb(0.90, 0.74, 0.55);
                        default: return new Rgb(1.00, 0.80, 0.56);
                    }
                case RingKind.Asw: return new Rgb(0.70, 0.94, 0.66);
                default: return new Rgb(0.82, 0.74, 1.00);
            }
        }

        internal static uint Pack(RingKind k, double alpha) { return Pack(k, RingVia.Missile, alpha); }

        internal static uint Pack(RingKind k, RingVia via, double alpha) { return Pack(k, via, alpha, 0); }

        /// <summary>ADR-0068 supplement 2026-10-22: the shade of the n-th checked weapon with the same kind and delivery on one unit: 0 = the base colour,
        /// 1 = lighter (45 % towards white), 2 = darker (x 0.7), then repeating (1, 2, 1, 2 ...).</summary>
        private static Rgb Shade(Rgb c, int tint)
        {
            if (tint <= 0) return c;
            if (tint % 2 == 1) return new Rgb(c.R + (1 - c.R) * 0.45, c.G + (1 - c.G) * 0.45, c.B + (1 - c.B) * 0.45);
            return new Rgb(c.R * 0.7, c.G * 0.7, c.B * 0.7);
        }

        internal static uint Pack(RingKind k, RingVia via, double alpha, int tint)
        {
            Rgb c = Shade(ColorOf(k, via), tint);
            if (double.IsNaN(alpha)) alpha = 0;
            alpha = alpha < 0 ? 0 : alpha > 1 ? 1 : alpha;
            uint a = (uint)Math.Round(alpha * 255), r = (uint)Math.Round(c.R * alpha * 255), g = (uint)Math.Round(c.G * alpha * 255), b = (uint)Math.Round(c.B * alpha * 255);
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        private sealed class Ctx
        {
            public double[] Aff; public int Rw, Rh; public RingStyle St; public IRingPainter P; public RingDrawStats S;
            public double FillA;   // the per-fill alpha of this draw (FillAlpha, lowered by the cap)
            public bool Full => S.Segments >= St.MaxSegments;
            public void PutTri(float ax, float ay, float bx, float by, float cx, float cy, uint col)
            {
                // a triangle wholly beyond one edge of the RT draws nothing: skipped (GL clips the rest)
                if ((ax < 0 && bx < 0 && cx < 0) || (ay < 0 && by < 0 && cy < 0) || (ax > Rw && bx > Rw && cx > Rw) || (ay > Rh && by > Rh && cy > Rh)) return;
                if (S.Tris >= St.MaxTris) { S.TrisCut++; return; }
                S.Tris++;
                P.Tri(ax, ay, bx, by, cx, cy, col);
            }
            public void Pt(double lat, double lon, out float x, out float y)
            {
                x = (float)(Aff[0] * lat + Aff[1] * lon + Aff[2]);
                y = (float)(Aff[3] * lat + Aff[4] * lon + Aff[5]);
            }
            public void Put(float ax, float ay, float bx, float by, uint col)
            {
                if (Full) { S.SegmentsCut++; return; }
                S.Segments++;
                P.Line(ax, ay, bx, by, col);
            }
        }

        /// <summary>Draws a polyline segment; on/off &lt;= 0 means solid. phase is the running dash position along the path (in/out).</summary>
        private static void Seg(Ctx c, float ax, float ay, float bx, float by, uint col, float on, float off, ref double phase)
        {
            double dx = (double)bx - ax, dy = (double)by - ay;
            double full = Math.Sqrt(dx * dx + dy * dy);
            if (double.IsNaN(full) || double.IsInfinity(full)) return;
            float cax = ax, cay = ay, cbx = bx, cby = by;
            bool ok = DrawGuard.ClipSegment(ref cax, ref cay, ref cbx, ref cby, c.Rw, c.Rh, 16f);
            double startPhase = phase;
            phase += full;
            if (!ok) return;
            if (!(on > 0) || !(off > 0)) { c.Put(cax, cay, cbx, cby, col); return; }
            double ex = (double)cbx - cax, ey = (double)cby - cay;
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6) return;
            double lead = Math.Sqrt(((double)cax - ax) * ((double)cax - ax) + ((double)cay - ay) * ((double)cay - ay));
            double step = on + off;
            double ph = (startPhase + lead) % step;
            int n = DrawGuard.DashCount((float)(len + ph), on, off, 512);
            double ux = ex / len, uy = ey / len;
            for (int i = 0; i <= n; i++)
            {
                double ds = i * step - ph, de = ds + on;
                if (de <= 0) continue;
                if (ds >= len) break;
                if (ds < 0) ds = 0;
                if (de > len) de = len;
                if (de - ds < 0.25) continue;
                c.Put((float)(cax + ux * ds), (float)(cay + uy * ds), (float)(cax + ux * de), (float)(cay + uy * de), col);
            }
        }

        private static void Ring(Ctx c, UnitRings u, int idx, double pxPerNm)
        {
            RingSpec r = u.Rings[idx];
            double nm = r.MaxNm;
            if (double.IsNaN(nm) || double.IsInfinity(nm) || nm <= 0) return;
            double rPx = nm * pxPerNm;
            if (!(rPx >= c.St.MinRadiusPx)) { c.S.SmallSkipped++; return; }
            c.S.Rings++;
            int n = Segments(rPx);
            bool weapon = r.Weapon != null;
            double ally = r.Src == RingSource.Ally ? c.St.AllyAlphaK : 1.0;
            double alpha = c.St.Opacity * (weapon ? c.St.OutlineK : 1.0) * ally * (r.IsMin ? 0.4 : 1.0);
            uint col = Pack(r.Kind, r.Via, alpha, r.Tint);
            if (weapon && !r.IsMin && c.FillA > 0)
                FillAnnulus(c, u, Pack(r.Kind, r.Via, c.FillA * ally, r.Tint), InnerNm(u, idx), nm, 0, 360, n);
            float on = 0, off = 0;
            if (!r.IsMin)
            {
                if (r.Src == RingSource.Class || r.UpTo) { on = 10; off = 6; }
                else if (r.Kind == RingKind.SonarPassive) { on = 2; off = 5; }
            }
            float px0 = 0, py0 = 0, px = 0, py = 0;
            double phase = 0;
            for (int i = 0; i <= n; i++)
            {
                double lat, lon;
                RangeRingKin.OffsetMetric(u.Center.Lat, u.Center.Lon, 360.0 * i / n, nm, u.Metric, out lat, out lon);
                c.Pt(lat, lon, out px, out py);
                if (i > 0)
                {
                    if (c.Full) { c.S.SegmentsCut += n - i + 1; return; }
                    Seg(c, px0, py0, px, py, col, on, off, ref phase);
                }
                px0 = px; py0 = py;
            }
        }

        private static void Sector(Ctx c, UnitRings u, SectorSpec s, double pxPerNm)
        {
            double nm = s.RangeNm;
            if (double.IsNaN(nm) || double.IsInfinity(nm) || nm <= 0 || double.IsNaN(s.WidthDeg) || double.IsNaN(s.CenterDeg) || !(s.WidthDeg > 0)) return;
            double rPx = nm * pxPerNm;
            if (!(rPx >= c.St.MinRadiusPx)) { c.S.SmallSkipped++; return; }
            c.S.Rings++;
            double w = Math.Min(s.WidthDeg, 360.0);
            int n = Math.Max(4, (int)Math.Ceiling(Segments(rPx) * w / 360.0));
            double ally = s.Src == RingSource.Ally ? c.St.AllyAlphaK : 1.0;
            double alpha = c.St.Opacity * c.St.OutlineK * (s.IsMin ? 0.4 : 1.0) * ally;
            uint col = Pack(s.Kind, s.Via, alpha, s.Tint);
            float on = 0, off = 0;
            if (!s.IsMin && (s.Src == RingSource.Class || s.UpTo)) { on = 10; off = 6; }
            double b0 = u.HeadingDeg + s.CenterDeg - w / 2.0;
            if (s.Weapon != null && !s.IsMin && c.FillA > 0)
                FillAnnulus(c, u, Pack(s.Kind, s.Via, c.FillA * ally, s.Tint), InnerNm(u, s), nm, b0, w, n);
            double cx, cy;
            { float fx, fy; c.Pt(u.Center.Lat, u.Center.Lon, out fx, out fy); cx = fx; cy = fy; }
            double phase = 0;
            float px0 = (float)cx, py0 = (float)cy, px = 0, py = 0;
            for (int i = 0; i <= n; i++)
            {
                double lat, lon;
                RangeRingKin.OffsetMetric(u.Center.Lat, u.Center.Lon, b0 + w * i / n, nm, u.Metric, out lat, out lon);
                c.Pt(lat, lon, out px, out py);
                if (c.Full) { c.S.SegmentsCut += n - i + 2; return; }
                if (i == 0) { if (!s.IsMin) Seg(c, (float)cx, (float)cy, px, py, col, on, off, ref phase); }
                else Seg(c, px0, py0, px, py, col, s.IsMin ? 0 : on, s.IsMin ? 0 : off, ref phase);
                px0 = px; py0 = py;
            }
            if (!s.IsMin) Seg(c, px0, py0, (float)cx, (float)cy, col, on, off, ref phase);
        }

        // ---- fills (2026-10-22) ----------------------------------------------------------------------------------------------------------
        private static bool Filled(RingSpec r) { return r.Weapon != null && !r.IsMin && r.MaxNm > 0; }

        /// <summary>The inner radius of the fill of a weapon ring: its minimum range (the dead zone stays clear), raised to the largest smaller filled ring of
        /// the same unit (rings of one unit are concentric: each fill covers only its own band, so nested fills never stack). Equal radii: only the first fills.</summary>
        private static double InnerNm(UnitRings u, int idx)
        {
            RingSpec r = u.Rings[idx];
            double inner = r.MinNm > 0 && r.MinNm < r.MaxNm ? r.MinNm : 0;
            for (int j = 0; j < u.Rings.Count; j++)
            {
                RingSpec o = u.Rings[j];
                if (j == idx || !Filled(o)) continue;
                if (o.MaxNm < r.MaxNm || (o.MaxNm == r.MaxNm && j < idx)) inner = Math.Max(inner, o.MaxNm);
            }
            return inner;
        }

        /// <summary>The inner radius of a wedge fill: its minimum range, raised to the largest filled ring of the unit inside it.</summary>
        private static double InnerNm(UnitRings u, SectorSpec s)
        {
            double inner = s.MinNm > 0 && s.MinNm < s.RangeNm ? s.MinNm : 0;
            foreach (RingSpec o in u.Rings) if (Filled(o) && o.MaxNm < s.RangeNm) inner = Math.Max(inner, o.MaxNm);
            return inner;
        }

        /// <summary>A filled annular sector [innerNm, outerNm] from bearing b0 over w degrees in n steps (innerNm 0 = a fan from the centre).</summary>
        private static void FillAnnulus(Ctx c, UnitRings u, uint col, double innerNm, double outerNm, double b0, double w, int n)
        {
            if (!(outerNm > innerNm) || n <= 0) return;
            float cx, cy; c.Pt(u.Center.Lat, u.Center.Lon, out cx, out cy);
            float ox0 = 0, oy0 = 0, ix0 = 0, iy0 = 0;
            for (int i = 0; i <= n; i++)
            {
                double lat, lon, b = b0 + w * i / n;
                float ox, oy, ix = cx, iy = cy;
                RangeRingKin.OffsetMetric(u.Center.Lat, u.Center.Lon, b, outerNm, u.Metric, out lat, out lon); c.Pt(lat, lon, out ox, out oy);
                if (innerNm > 0) { RangeRingKin.OffsetMetric(u.Center.Lat, u.Center.Lon, b, innerNm, u.Metric, out lat, out lon); c.Pt(lat, lon, out ix, out iy); }
                if (i > 0)
                {
                    c.PutTri(ox0, oy0, ox, oy, ix0, iy0, col);
                    if (innerNm > 0) c.PutTri(ix0, iy0, ox, oy, ix, iy, col);
                }
                ox0 = ox; oy0 = oy; ix0 = ix; iy0 = iy;
            }
        }

        /// <summary>Fill groups that may overlap at one point: per unit 1 for its concentric rings (they never stack) + 1 per weapon drawn as wedges; the
        /// groups of the units whose filled areas (circles of their largest fill) intersect are added up; the most at any unit counts.</summary>
        private static int FillGroups(RingScene s)
        {
            var g = new List<int>(); var r = new List<double>(); var at = new List<RingPt>();
            foreach (UnitRings u in s.Units)
            {
                if (u == null) continue;
                int n = 0; double reach = 0;
                foreach (RingSpec x in u.Rings) if (Filled(x)) { reach = Math.Max(reach, x.MaxNm); n = 1; }
                var wedged = new HashSet<string>();
                foreach (SectorSpec sc in u.Sectors)
                    if (sc.Weapon != null && !sc.IsMin && sc.RangeNm > 0) { wedged.Add(sc.Weapon + "#" + sc.Tint); reach = Math.Max(reach, sc.RangeNm); }
                n += wedged.Count;
                if (n == 0) continue;
                g.Add(n); r.Add(reach); at.Add(u.Center);
            }
            int most = 0;
            for (int a = 0; a < g.Count; a++)
            {
                int sum = 0;
                double cl = Math.Max(MinCosLat, Math.Cos(at[a].Lat * Math.PI / 180.0));
                for (int b = 0; b < g.Count; b++)
                {
                    double dn = (at[a].Lat - at[b].Lat) * 60.0, de = (at[a].Lon - at[b].Lon) * 60.0 * cl;
                    if (a == b || Math.Sqrt(dn * dn + de * de) < r[a] + r[b]) sum += g[b];
                }
                most = Math.Max(most, sum);
            }
            return most;
        }

        /// <summary>[RangeRings] FillAlpha clamped to 0..0.4 (NaN -> the default 0.12).</summary>
        public static double ClampFill(double v) { return double.IsNaN(v) ? 0.12 : v < 0 ? 0 : v > 0.4 ? 0.4 : v; }

        /// <summary>The per-fill alpha: FillAlpha (0..0.4), lowered so that n overlapping fill groups add up to at most cap: 1 - (1 - cap)^(1/n).</summary>
        public static double FillAlphaFor(double fillAlpha, double cap, int groups)
        {
            if (double.IsNaN(fillAlpha) || !(fillAlpha > 0) || groups <= 0) return 0;
            double a = Math.Min(fillAlpha, 0.4);
            if (!(cap > 0)) return a;
            double most = 1.0 - Math.Pow(1.0 - Math.Min(cap, 0.95), 1.0 / groups);
            return Math.Min(a, most);
        }

        /// <summary>All geometry of a scene into painter lines (clipped to the RT, budgeted). Non-finite affine or empty RT draws nothing.</summary>
        public static RingDrawStats Emit(RingScene s, double[] aff, int rw, int rh, RingStyle st, IRingPainter p)
        {
            var none = new RingDrawStats();
            if (s == null || p == null || aff == null || aff.Length < 6 || rw <= 0 || rh <= 0) return none;
            for (int i = 0; i < 6; i++) if (double.IsNaN(aff[i]) || double.IsInfinity(aff[i])) return none;
            if (st == null) st = new RingStyle();
            var c = new Ctx { Aff = aff, Rw = rw, Rh = rh, St = st, P = p };
            c.S.FillGroups = FillGroups(s);
            c.FillA = c.S.FillAlpha = FillAlphaFor(st.FillAlpha, st.FillCap, c.S.FillGroups);
            foreach (UnitRings u in s.Units)
            {
                if (u == null || double.IsNaN(u.Center.Lat) || double.IsNaN(u.Center.Lon)) continue;
                float x0, y0, x1, y1;
                c.Pt(u.Center.Lat, u.Center.Lon, out x0, out y0);
                c.Pt(u.Center.Lat + 1.0 / 60.0, u.Center.Lon, out x1, out y1);
                double dx = (double)x1 - x0, dy = (double)y1 - y0;
                double pxPerNm = Math.Sqrt(dx * dx + dy * dy);
                if (double.IsNaN(pxPerNm) || double.IsInfinity(pxPerNm) || pxPerNm <= 0) continue;
                if (!(c.S.PxPerNm > 0)) c.S.PxPerNm = pxPerNm;
                // fills first in the painter's own pass (the overlay draws all triangles before the lines)
                for (int i = 0; i < u.Rings.Count; i++) Ring(c, u, i, pxPerNm);
                foreach (SectorSpec sc in u.Sectors) Sector(c, u, sc, pxPerNm);
            }
            return c.S;
        }

        /// <summary>T-164 / ADR-0066: "auto" | "on" | "off"; anything else -> "auto" (trim, ignore case).</summary>
        public static string NormOwn(string mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            return m == "on" || m == "off" ? m : "auto";
        }

        /// <summary>T-164: on -> true; off -> false; auto -> !btmFound.</summary>
        public static bool OwnActive(string mode, bool btmFound)
        {
            string m = NormOwn(mode);
            return m == "on" || (m == "auto" && !btmFound);
        }

        // ---- self test ---------------------------------------------------------------------------------------------------------------------
        private static bool Near(double a, double b, double eps) { return Math.Abs(a - b) <= eps; }

        public static string SelfTest()
        {
            double lat, lon;
            Offset(0, 10, 0, 60, out lat, out lon);
            if (!Near(lat, 1, 1e-9) || !Near(lon, 10, 1e-9)) return "offset north eq " + lat + "," + lon;
            Offset(0, 10, 90, 60, out lat, out lon);
            if (!Near(lat, 0, 1e-9) || !Near(lon, 11, 1e-9)) return "offset east eq " + lat + "," + lon;
            Offset(60, 0, 90, 60, out lat, out lon);
            if (!Near(lon, 2, 1e-6)) return "offset east 60deg " + lon;
            Offset(90, 0, 90, 60, out lat, out lon);
            if (!Near(lon, 1 / MinCosLat, 1e-6) || double.IsNaN(lon) || double.IsInfinity(lon)) return "offset pole " + lon;
            if (Segments(0) != 24 || Segments(100) != 24 || Segments(144) != 24 || Segments(145) != 25 || Segments(1e6) != 96 || Segments(double.NaN) != 24) return "segments";
            if (Segments(300) != 50) return "segments 300 = " + Segments(300);
            List<double[]> a = ArcSpans(new[] { new ArcDeg { A = -130, B = 130 } });
            if (a.Count != 1 || !Near(a[0][0], 0, 1e-9) || !Near(a[0][1], 260, 1e-9)) return "arc -130,130";
            a = ArcSpans(new[] { new ArcDeg { A = 40, B = -40 } });
            if (a.Count != 1 || !Near(a[0][0], 180, 1e-9) || !Near(a[0][1], 280, 1e-9)) return "arc 40,-40 " + (a.Count > 0 ? a[0][0] + "/" + a[0][1] : "empty");
            a = ArcSpans(new[] { new ArcDeg { A = 20, B = 100 } });
            if (a.Count != 1 || !Near(a[0][0], 60, 1e-9) || !Near(a[0][1], 80, 1e-9)) return "arc 20,100";
            if (ArcSpans(new[] { new ArcDeg { A = 0, B = 0 } }).Count != 0) return "arc 0,0";
            if (ArcSpans(new List<ArcDeg>()).Count != 0 || ArcSpans(null).Count != 0) return "arc empty";
            if (ArcSpans(new[] { new ArcDeg { A = -180, B = 180 } }).Count != 0) return "arc full circle";
            a = ArcSpans(new[] { new ArcDeg { A = -30, B = 30 }, new ArcDeg { A = 150, B = -150 } });
            if (a.Count != 2 || !Near(a[1][0], 180, 1e-9) || !Near(a[1][1], 60, 1e-9)) return "arc two spans";
            if (!Near(NextDue(10, false, 1.0, 0.25), 11, 1e-9) || !Near(NextDue(10, true, 1.0, 0.25), 10.25, 1e-9)) return "nextdue";
            if (!Near(NextDue(0, false, 0, 0), 0.05, 1e-9)) return "nextdue floor";
            if (Pack(RingKind.Aaw, 1.0) != 0xFF9EDBFFu) return "pack " + Pack(RingKind.Aaw, 1.0).ToString("X");
            // T-171: ParseArcs, UnionSpans, CourseFromVel, colours by via
            List<ArcDeg> pa = ParseArcs("");
            if (pa.Count != 1 || pa[0].A != 0 || pa[0].B != 0) return "ParseArcs blank";
            pa = ParseArcs("-130,130");
            if (pa.Count != 1 || pa[0].A != -130 || pa[0].B != 130) return "ParseArcs one";
            pa = ParseArcs("40,-40|150,210");
            if (pa.Count != 2 || pa[0].A != 40 || pa[0].B != -40 || pa[1].A != 150 || !Near(pa[1].B, -150, 1e-9)) return "ParseArcs adapt";
            pa = ParseArcs("-180,180");
            if (pa.Count != 1 || pa[0].A != -180 || pa[0].B != 180) return "ParseArcs full circle kept";
            pa = ParseArcs("12,x|1,2");
            if (pa.Count != 1 || pa[0].A != 0 || pa[0].B != 0) return "ParseArcs junk";
            pa = ParseArcs("1,2,3");
            if (pa.Count != 1 || pa[0].A != 0 || pa[0].B != 0) return "ParseArcs three numbers";
            List<double[]> un = UnionSpans(new List<double[]> { new[] { 0.0, 100.0 }, new[] { 60.0, 100.0 } });
            if (un.Count != 1 || !Near(un[0][0], 30, 1e-9) || !Near(un[0][1], 160, 1e-9)) return "union overlap " + (un.Count > 0 ? un[0][0] + "/" + un[0][1] : "empty");
            un = UnionSpans(new List<double[]> { new[] { 170.0, 60.0 }, new[] { -170.0, 60.0 } });
            if (un.Count != 1 || !Near(un[0][1], 80, 1e-9) || !Near(Math.Abs(un[0][0]), 180, 1e-9)) return "union through 180 " + (un.Count > 0 ? un[0][0] + "/" + un[0][1] : "empty");
            un = UnionSpans(new List<double[]> { new[] { -20.0, 60.0 }, new[] { 10.0, 60.0 } });
            if (un.Count != 1 || !Near(un[0][0], -5, 1e-9) || !Near(un[0][1], 90, 1e-9)) return "union through 0 " + (un.Count > 0 ? un[0][0] + "/" + un[0][1] : "empty");
            un = UnionSpans(new List<double[]> { new[] { 0.0, 40.0 }, new[] { 180.0, 40.0 } });
            if (un.Count != 2) return "union apart";
            if (UnionSpans(null).Count != 0 || UnionSpans(new List<double[]>()).Count != 0) return "union empty";
            double crs;
            if (!CourseFromVel(0, 10, 1, out crs) || !Near(crs, 0, 1e-9)) return "course north";
            if (!CourseFromVel(10, 0, 1, out crs) || !Near(crs, 90, 1e-9)) return "course east";
            if (!CourseFromVel(0, -10, 1, out crs) || !Near(crs, 180, 1e-9)) return "course south";
            if (!CourseFromVel(-10, 0, 1, out crs) || !Near(crs, 270, 1e-9)) return "course west";
            if (CourseFromVel(0.5, 0.5, 1, out crs) || CourseFromVel(double.NaN, 1, 0, out crs) || CourseFromVel(0, 0, 0, out crs)) return "course unknown";
            if (Pack(RingKind.Asuw, RingVia.Missile, 1.0) != 0xFFFFCC8Fu) return "pack asuw missile " + Pack(RingKind.Asuw, RingVia.Missile, 1.0).ToString("X");
            // fill alpha: the setting up to 0.4, lowered by the cap when many fill groups may overlap
            if (ClampFill(-1) != 0 || ClampFill(0.9) != 0.4 || ClampFill(double.NaN) != 0.12 || ClampFill(0.2) != 0.2) return "ClampFill";
            if (FillAlphaFor(0.12, 0.35, 1) != 0.12 || FillAlphaFor(0.12, 0.35, 2) != 0.12 || FillAlphaFor(0.6, 0.95, 1) != 0.4 || FillAlphaFor(0, 0.35, 3) != 0 || FillAlphaFor(0.12, 0.35, 0) != 0) return "FillAlphaFor";
            double a8 = FillAlphaFor(0.12, 0.35, 8);
            if (!(a8 < 0.12) || Math.Abs(1 - Math.Pow(1 - a8, 8) - 0.35) > 1e-9) return "FillAlphaFor cap " + a8;
            if (Pack(RingKind.Asuw, RingVia.Torpedo, 1.0) == Pack(RingKind.Asuw, RingVia.Missile, 1.0) || Pack(RingKind.Aaw, RingVia.Gun, 1.0) == Pack(RingKind.Aaw, RingVia.Missile, 1.0)) return "pack by via";
            if (Pack(RingKind.Asw, RingVia.Asw, 1.0) != Pack(RingKind.Asw, 1.0)) return "pack asw";
            uint t0 = Pack(RingKind.Aaw, RingVia.Missile, 1.0, 0), t1 = Pack(RingKind.Aaw, RingVia.Missile, 1.0, 1), t2 = Pack(RingKind.Aaw, RingVia.Missile, 1.0, 2);
            if (t0 != Pack(RingKind.Aaw, 1.0) || t1 == t0 || t2 == t0 || t2 == t1 || Pack(RingKind.Aaw, RingVia.Missile, 1.0, 3) != t1) return "pack shades";
            if ((Pack(RingKind.Asuw, RingVia.Missile, 0.5, 2) >> 24) != 128) return "pack shade keeps alpha";
            if (!OwnActive("auto", false) || OwnActive("auto", true) || !OwnActive("on", true) || OwnActive("off", false)) return "OwnActive table";
            if (NormOwn(" ON ") != "on" || NormOwn("x") != "auto" || NormOwn(null) != "auto") return "NormOwn";
            return null;
        }
    }
}
