// T-137 A5: RangeRingMath.Emit against a counting painter: budget, RT clip, sector through 0 deg, small radius, dashes, NaN affine.
using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal sealed class CountPainter : IRingPainter
    {
        public readonly List<float[]> Lines = new List<float[]>();
        public void Line(float ax, float ay, float bx, float by, uint c) { Lines.Add(new[] { ax, ay, bx, by, c }); }
        public readonly List<float[]> Tris = new List<float[]>();
        public readonly List<uint> TriCols = new List<uint>();
        public void Tri(float ax, float ay, float bx, float by, float cx, float cy, uint c) { Tris.Add(new[] { ax, ay, bx, by, cx, cy }); TriCols.Add(c); }
    }

    internal static class RingTestPainterCases
    {
        // lat/lon -> RT px: 1 nm = scale px north (y grows downward, x east), centre of the map at (cx, cy)
        private static double[] Aff(double scalePerNm, double cx, double cy)
        {
            double k = scalePerNm * 60.0;
            return new[] { 0.0, k, cx, -k, 0.0, cy };
        }

        private static RingScene Scene(int rings, double nm, bool dashed, RingKind kind = RingKind.Aaw)
        {
            var s = new RingScene();
            var u = new UnitRings { Key = 1, Center = new RingPt(0, 0) };
            for (int i = 0; i < rings; i++) u.Rings.Add(new RingSpec { Kind = kind, Src = dashed ? RingSource.Class : RingSource.Own, MaxNm = nm, UpTo = dashed });
            s.Units.Add(u);
            return s;
        }

        public static string SelfTest()
        {
            var st = new RingStyle();
            double[] aff = Aff(10, 512, 512);   // 1. 24 solid rings of 84 segments (fully inside the RT) fit; 40 rings overshoot 3000 -> cut

            // 1. 24 solid rings of 84 segments (fully inside the RT) fit; 40 rings overshoot 3000 -> cut
            var p = new CountPainter();
            RingDrawStats d = RangeRingMath.Emit(Scene(24, 50, false), aff, 1024, 1024, st, p);
            if (d.Segments > 3000 || d.Segments < 1900 || p.Lines.Count != d.Segments) return "24 rings: " + d.Segments + " lines " + p.Lines.Count;
            p = new CountPainter();
            d = RangeRingMath.Emit(Scene(40, 50, false), aff, 1024, 1024, st, p);
            if (d.Segments != 3000 || d.SegmentsCut <= 0 || p.Lines.Count != 3000) return "budget: segs " + d.Segments + " cut " + d.SegmentsCut;
            // dashed rings cut too
            p = new CountPainter();
            d = RangeRingMath.Emit(Scene(24, 50, true), aff, 1024, 1024, st, p);
            if (d.Segments > 3000) return "dashed budget " + d.Segments;

            // 2. clip to the RT: huge ring, all ends within [-16, 1040]
            p = new CountPainter();
            RangeRingMath.Emit(Scene(1, 60, false), aff, 1024, 1024, st, p);
            if (p.Lines.Count == 0) return "clip: nothing drawn";
            foreach (float[] l in p.Lines)
                for (int i = 0; i < 4; i++)
                    if (l[i] < -16.01f || l[i] > 1040.01f) return "clip: coordinate " + l[i];
            // fully outside the RT: no lines at all
            p = new CountPainter();
            RangeRingMath.Emit(Scene(1, 30, false), Aff(10, 5000, 5000), 1024, 1024, st, p);
            if (p.Lines.Count != 0) return "outside RT drew " + p.Lines.Count;

            // 3. sector through 0 deg (centre 180, width 280 = 40 .. 320 through the stern) and a bow sector
            var sc = new RingScene();
            var u = new UnitRings { Key = 1, Center = new RingPt(0, 0), HeadingDeg = 0 };
            u.Sectors.Add(new SectorSpec { Kind = RingKind.Aaw, CenterDeg = 0, WidthDeg = 80, RangeNm = 20 });
            sc.Units.Add(u);
            p = new CountPainter();
            RangeRingMath.Emit(sc, aff, 1024, 1024, st, p);
            if (p.Lines.Count < 6) return "sector lines " + p.Lines.Count;
            bool crossesNorth = false;
            foreach (float[] l in p.Lines) if (l[1] < 512 - 190 || l[3] < 512 - 190) crossesNorth = true;   // arc around the bow (y up on screen) reaches ~200 px north
            if (!crossesNorth) return "bow sector does not reach the bow";
            u.Sectors[0] = new SectorSpec { Kind = RingKind.Aaw, CenterDeg = 180, WidthDeg = 280, RangeNm = 20 };
            p = new CountPainter();
            RangeRingMath.Emit(sc, aff, 1024, 1024, st, p);
            float minY = 9999, maxY = -9999;
            foreach (float[] l in p.Lines) { minY = Math.Min(minY, Math.Min(l[1], l[3])); maxY = Math.Max(maxY, Math.Max(l[1], l[3])); }
            if (maxY < 512 + 190) return "stern sector does not reach the stern " + maxY;
            if (minY < 512 - 160) return "stern sector reaches too far north " + minY;   // 40..320 deg: cos(40) * 200 = 153 px
            // heading rotates the sector: heading 180 puts the bow sector to the south
            u.HeadingDeg = 180;
            u.Sectors[0] = new SectorSpec { Kind = RingKind.Aaw, CenterDeg = 0, WidthDeg = 60, RangeNm = 20 };
            p = new CountPainter();
            RangeRingMath.Emit(sc, aff, 1024, 1024, st, p);
            foreach (float[] l in p.Lines) if (l[1] < 512 - 1 || l[3] < 512 - 1) return "rotated sector points north";

            // 4. small radius: skipped and counted, not cut
            p = new CountPainter();
            d = RangeRingMath.Emit(Scene(2, 0.5, false), aff, 1024, 1024, st, p);   // 5 px < 8
            if (d.SmallSkipped != 2 || d.Rings != 0 || p.Lines.Count != 0 || d.SegmentsCut != 0) return "small radius: skipped " + d.SmallSkipped;

            // 5. dashes: bounded per input segment, and a dashed ring really has gaps
            p = new CountPainter();
            d = RangeRingMath.Emit(Scene(1, 100, true), Aff(10, 5000, 5000), 100000, 100000, st, p);
            if (p.Lines.Count == 0 || p.Lines.Count > 96 * 513) return "dash count " + p.Lines.Count;
            var solid = new CountPainter();
            RangeRingMath.Emit(Scene(1, 30, false), aff, 1024, 1024, st, solid);
            var dash = new CountPainter();
            RangeRingMath.Emit(Scene(1, 30, true), aff, 1024, 1024, st, dash);
            if (solid.Lines.Count == 0 || dash.Lines.Count == 0) return "dash/solid empty";
            double solidLen = TotalLen(solid), dashLen = TotalLen(dash);
            if (!(dashLen < solidLen * 0.8) || !(dashLen > solidLen * 0.4)) return "dash ratio " + dashLen / solidLen;

            // 6. NaN / infinite affine, bad RT: nothing
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity })
            {
                double[] a = Aff(10, 512, 512); a[2] = bad;
                p = new CountPainter();
                d = RangeRingMath.Emit(Scene(3, 50, false), a, 1024, 1024, st, p);
                if (p.Lines.Count != 0 || d.Segments != 0) return "bad affine drew lines";
            }
            p = new CountPainter();
            RangeRingMath.Emit(Scene(1, 50, false), aff, 0, 1024, st, p);
            RangeRingMath.Emit(Scene(1, 50, false), null, 1024, 1024, st, p);
            if (p.Lines.Count != 0) return "bad input drew lines";

            // 7. min ring is fainter than the main ring (premultiplied alpha), ally dimmer than own
            var sm = Scene(1, 40, false);
            sm.Units[0].Rings.Add(new RingSpec { Kind = RingKind.Aaw, Src = RingSource.Own, MaxNm = 20, IsMin = true });
            p = new CountPainter();
            RangeRingMath.Emit(sm, aff, 1024, 1024, st, p);
            uint first = (uint)p.Lines[0][4], last = (uint)p.Lines[p.Lines.Count - 1][4];
            if ((last >> 24) >= (first >> 24)) return "min ring alpha " + (last >> 24) + " vs " + (first >> 24);
            var ally = Scene(1, 40, false); var ar = ally.Units[0].Rings[0]; ar.Src = RingSource.Ally; ally.Units[0].Rings[0] = ar;
            p = new CountPainter();
            RangeRingMath.Emit(ally, aff, 1024, 1024, st, p);
            if (((uint)p.Lines[0][4] >> 24) >= (first >> 24)) return "ally alpha";

            // 8. T-171 wedges: the minimum-range wedge is an arc only (no radii); a class wedge is dashed (more segments); the colour follows the via
            Func<SectorSpec, CountPainter> run = spec =>
            {
                var w = new RingScene();
                var wu = new UnitRings { Key = 1, Center = new RingPt(0, 0) };
                wu.Sectors.Add(spec);
                w.Units.Add(wu);
                var pp = new CountPainter();
                RangeRingMath.Emit(w, aff, 1024, 1024, st, pp);
                return pp;
            };
            Func<CountPainter, int> radii = cp =>
            {
                int n = 0;
                foreach (float[] l in cp.Lines)
                    if ((Math.Abs(l[0] - 512) < 0.6 && Math.Abs(l[1] - 512) < 0.6) || (Math.Abs(l[2] - 512) < 0.6 && Math.Abs(l[3] - 512) < 0.6)) n++;
                return n;
            };
            var wedge = new SectorSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Own, CenterDeg = 0, WidthDeg = 90, RangeNm = 20 };
            CountPainter solidW = run(wedge);
            var minWedge = wedge; minWedge.IsMin = true;
            CountPainter minW = run(minWedge);
            var dashW = wedge; dashW.Src = RingSource.Class; dashW.UpTo = true;
            CountPainter dashedW = run(dashW);
            if (solidW.Lines.Count == 0 || radii(solidW) < 2) return "solid wedge has its radii " + radii(solidW);
            if (minW.Lines.Count == 0 || radii(minW) != 0) return "minimum wedge: arc only, radii " + radii(minW);
            if (dashedW.Lines.Count <= solidW.Lines.Count) return "dashed wedge must have more segments " + dashedW.Lines.Count + " vs " + solidW.Lines.Count;
            var torpW = wedge; torpW.Via = RingVia.Torpedo;
            if ((uint)run(torpW).Lines[0][4] == (uint)solidW.Lines[0][4]) return "wedge colour must follow the via";
            string fill = FillCases();
            if (fill != null) return fill;
            return null;
        }

        private static double TriArea(float[] t) { return Math.Abs((t[2] - t[0]) * (t[5] - t[1]) - (t[4] - t[0]) * (t[3] - t[1])) / 2.0; }

        private static double Area(CountPainter p) { double a = 0; foreach (float[] t in p.Tris) a += TriArea(t); return a; }

        /// <summary>2026-10-22: fills of weapon rings / wedges: area, annulus (the dead zone stays clear), nested rings without stacking, no fill for sonar and
        /// minimum circles, the cap, the budget, the pale palette, and the time of Emit with fills (printed).</summary>
        private static string FillCases()
        {
            var st = new RingStyle();
            double[] aff = Aff(10, 512, 512);   // 10 px / nm
            Func<RingScene> one = () =>
            {
                var s = new RingScene();
                var u = new UnitRings { Key = 1, Center = new RingPt(0, 0) };
                u.Rings.Add(new RingSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 40, Weapon = "Harpoon" });
                s.Units.Add(u);
                return s;
            };
            // a filled disc of 40 nm = 400 px: area ~ pi r^2 (polygon of n segments a bit less), alpha = FillAlpha, colour = the pale AShM
            var p = new CountPainter();
            RingDrawStats d = RangeRingMath.Emit(one(), aff, 1024, 1024, st, p);
            double disc = Math.PI * 400 * 400;
            if (p.Tris.Count == 0 || d.Tris != p.Tris.Count || Area(p) < disc * 0.98 || Area(p) > disc * 1.001) return "fill disc area " + Area(p) + " / " + disc;
            if (p.TriCols[0] != RangeRingMath.Pack(RingKind.Asuw, RingVia.Missile, 0.12) || d.FillAlpha != 0.12) return "fill colour / alpha " + p.TriCols[0].ToString("X");
            uint outline = (uint)p.Lines[0][4];
            if ((outline >> 24) != (uint)Math.Round(0.85 * 0.7 * 255)) return "weapon outline alpha " + (outline >> 24);
            // FillAlpha 0: outline only
            p = new CountPainter();
            RangeRingMath.Emit(one(), aff, 1024, 1024, new RingStyle { FillAlpha = 0 }, p);
            if (p.Tris.Count != 0 || p.Lines.Count == 0) return "FillAlpha 0 still fills";
            // minimum range: only the annulus is filled; the IsMin circle and sonar rings are outlines only
            var sm = one();
            var r0 = sm.Units[0].Rings[0]; r0.MinNm = 10; sm.Units[0].Rings[0] = r0;
            sm.Units[0].Rings.Add(new RingSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 10, MinNm = 10, IsMin = true, Weapon = "Harpoon" });
            sm.Units[0].Rings.Add(new RingSpec { Kind = RingKind.SonarPassive, Src = RingSource.Own, MaxNm = 30 });
            p = new CountPainter();
            RangeRingMath.Emit(sm, aff, 1024, 1024, st, p);
            double ann = Math.PI * (400 * 400 - 100 * 100);
            if (Area(p) < ann * 0.98 || Area(p) > ann * 1.001) return "annulus area " + Area(p) + " / " + ann;
            // nested rings of one unit do not stack: SM-2 90 nm + ESSM 27 nm fill exactly the 90 nm disc
            var sn = new RingScene();
            var un = new UnitRings { Key = 1, Center = new RingPt(0, 0) };
            un.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 45, Weapon = "SM-2" });
            un.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 20, Weapon = "ESSM", Tint = 1 });
            un.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 20, Weapon = "RAM" });
            sn.Units.Add(un);
            p = new CountPainter();
            RangeRingMath.Emit(sn, Aff(5, 512, 512), 1024, 1024, st, p);
            double d45 = Math.PI * 225 * 225;
            if (Area(p) < d45 * 0.98 || Area(p) > d45 * 1.001) return "nested rings stack: " + Area(p) + " / " + d45;
            if (!p.TriCols.Contains(RangeRingMath.Pack(RingKind.Aaw, RingVia.Missile, 0.12, 1))) return "inner ring has its own shade";
            // a wedge (class, dashed) fills its annular sector outside the filled rings of the unit
            var sw = one();
            sw.Units[0].Sectors.Add(new SectorSpec { Kind = RingKind.Asuw, Via = RingVia.Gun, Src = RingSource.Class, CenterDeg = 0, WidthDeg = 90, RangeNm = 50, UpTo = true, Weapon = "5in", LabelHere = true });
            p = new CountPainter();
            RangeRingMath.Emit(sw, Aff(5, 512, 512), 1024, 1024, st, p);
            double expect = Math.PI * 200 * 200 + Math.PI * (250 * 250 - 200 * 200) / 4;
            if (Area(p) < expect * 0.98 || Area(p) > expect * 1.001) return "wedge fill area " + Area(p) + " / " + expect;
            // the cap: 8 units far apart (8 fill groups) -> each fill lower, 8 of them stacked stay at the cap
            var s8 = new RingScene();
            for (int i = 0; i < 8; i++)
            {
                var u = new UnitRings { Key = i, Center = new RingPt(0, i * 0.01) };
                u.Rings.Add(new RingSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 20, Weapon = "H" });
                s8.Units.Add(u);
            }
            p = new CountPainter();
            d = RangeRingMath.Emit(s8, aff, 1024, 1024, st, p);
            if (d.FillGroups != 8 || !(d.FillAlpha < 0.12) || 1 - Math.Pow(1 - d.FillAlpha, 8) > 0.35 + 1e-9) return "cap " + d.FillAlpha;
            // units far apart do not lower each other's fill
            var sfar = new RingScene();
            for (int i = 0; i < 3; i++)
            {
                var u = new UnitRings { Key = i, Center = new RingPt(0, i * 2.0) };
                u.Rings.Add(new RingSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = 20, Weapon = "H" });
                sfar.Units.Add(u);
            }
            d = RangeRingMath.Emit(sfar, aff, 1024, 1024, st, new CountPainter());
            if (d.FillGroups != 1 || d.FillAlpha != 0.12) return "far units: groups " + d.FillGroups;
            // the budget
            p = new CountPainter();
            d = RangeRingMath.Emit(s8, aff, 1024, 1024, new RingStyle { MaxTris = 100 }, p);
            if (d.Tris != 100 || d.TrisCut <= 0 || p.Tris.Count != 100) return "tri budget " + d.Tris + "/" + d.TrisCut;
            // off the RT: no triangles
            p = new CountPainter();
            RangeRingMath.Emit(one(), Aff(10, 9000, 9000), 1024, 1024, st, p);
            if (p.Tris.Count != 0) return "off-RT fills " + p.Tris.Count;
            // time: 8 units x (3 rings + a 2-wedge weapon) with fills, 200 draws
            var big = new RingScene();
            for (int i = 0; i < 8; i++)
            {
                var u = new UnitRings { Key = i, Center = new RingPt(0, i * 0.2) };
                foreach (double nm in new[] { 90.0, 40.0, 20.0 }) u.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Via = RingVia.Missile, Src = RingSource.Own, MaxNm = nm, MinNm = 2, Weapon = "W" + nm });
                u.Sectors.Add(new SectorSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Own, CenterDeg = 60, WidthDeg = 100, RangeNm = 67, MinNm = 3, Weapon = "H" });
                u.Sectors.Add(new SectorSpec { Kind = RingKind.Asuw, Via = RingVia.Missile, Src = RingSource.Own, CenterDeg = -60, WidthDeg = 100, RangeNm = 67, MinNm = 3, Weapon = "H" });
                big.Units.Add(u);
            }
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            RingDrawStats bd = default(RingDrawStats);
            for (int i = 0; i < 200; i++) { p = new CountPainter(); bd = RangeRingMath.Emit(big, Aff(4, 512, 512), 1024, 1024, st, p); }
            sw2.Stop();
            double ms = sw2.Elapsed.TotalMilliseconds / 200;
            Console.WriteLine("RingTestPainterCases fill timing: " + ms.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " ms per Emit (8 units, 32 fills, tris " + bd.Tris + ", segs " + bd.Segments + ", fill alpha " + bd.FillAlpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")");
            if (ms > 20) return "fill emit too slow: " + ms + " ms";
            return null;
        }

        private static double TotalLen(CountPainter p)
        {
            double t = 0;
            foreach (float[] l in p.Lines) t += Math.Sqrt((l[2] - l[0]) * (l[2] - l[0]) + (l[3] - l[1]) * (l[3] - l[1]));
            return t;
        }
    }
}
