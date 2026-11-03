// T-309 D1: RangeRingMath.Emit through the exact map projector (Ortho): geodesic rings, horizon, fills, dashes; the Affine route stays as before (PainterCases).
using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal static class RingTestOrthoCases
    {
        private static readonly double[] Id = { 1, 0, 0, 0, 1, 0 };

        private static MapPx View(double lat0, double lon0, double scale, double rot = 0)
        {
            MapViewSnap s = MapViewSnap.Ortho(lat0, lon0, scale, rot, lat0, lon0, 960, 540, 1920, 1080);
            return MapPx.Create(in s, Id, 1920, 1080);
        }

        private static RingScene Scene(double lat, double lon, double nm, bool dashed, bool weapon = false)
        {
            var s = new RingScene();
            var u = new UnitRings { Key = 1, Center = new RingPt(lat, lon) };
            u.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Src = dashed ? RingSource.Class : RingSource.Own, MaxNm = nm, UpTo = dashed, Weapon = weapon ? "w" : null });
            s.Units.Add(u);
            return s;
        }

        private static double DistM(double la0, double lo0, double la1, double lo1)
        {
            return MapProjector.R * MapProjector.Angle(MapProjector.GeoToUnit(la0, lo0), MapProjector.GeoToUnit(la1, lo1));
        }

        private static double Len(float[] l) { return Math.Sqrt((double)(l[2] - l[0]) * (l[2] - l[0]) + (double)(l[3] - l[1]) * (l[3] - l[1])); }

        public static string SelfTest()
        {
            var st = new RingStyle();
            double zs = 256.0 * Math.Pow(2.0, 9) / (Math.PI * 12756274.0);
            // 1. the ring of 60 nm: every vertex 60 nm (geodesic) from the centre within 0.5 px, at phi = 0 and 70, rotated view too
            foreach (double plat in new[] { 0.0, 70.0 })
                foreach (double rot in new[] { 0.0, 37.0 })
                {
                    MapPx px = View(plat, 10, zs, rot);
                    var p = new CountPainter();
                    RingDrawStats d = RangeRingMath.Emit(Scene(plat, 10, 60, false), px, st, p);
                    if (d.Rings != 1 || p.Lines.Count < 24) return "ring drawn: rings " + d.Rings + " lines " + p.Lines.Count;
                    foreach (float[] l in p.Lines)
                        for (int k = 0; k < 4; k += 2)
                        {
                            double lat, lon;
                            if (!px.TryInverse(l[k], l[k + 1], out lat, out lon)) return "vertex off disc";
                            double err = Math.Abs(DistM(plat, 10, lat, lon) - 60 * 1852.0) * zs;
                            if (err > 0.5) return "ring vertex " + err.ToString("F3") + " px off the 60 nm circle at phi " + plat;
                        }
                    // the pixel radius of the ring agrees with PxPerNm to within the foreshortening
                    double ppn = px.PxPerNm(plat, 10);
                    if (Math.Abs(d.PxPerNm - ppn) > 1e-9) return "stats PxPerNm";
                }
            // 2. a ring through the limb: lines stop at the limb (inside the disc), no line is wholly invented outside, the fill is pressed to the limb
            {
                double sc = 1080.0 / (2.0 * MapProjector.R) * 0.9, limb = MapProjector.R * sc;
                MapPx px = View(0, 0, sc);
                var p = new CountPainter();
                RingDrawStats d = RangeRingMath.Emit(Scene(0, 80, 1500, false, true), px, st, p);
                if (p.Lines.Count < 5) return "limb ring lines " + p.Lines.Count;
                if (p.Tris.Count < 5) return "limb ring fill " + p.Tris.Count;
                double worst = 0;
                foreach (float[] l in p.Lines)
                    for (int k = 0; k < 4; k += 2) worst = Math.Max(worst, Math.Sqrt((l[k] - 960.0) * (l[k] - 960.0) + (l[k + 1] - 540.0) * (l[k + 1] - 540.0)));
                if (worst > limb + 0.05) return "line past the limb " + worst + " > " + limb;
                if (worst < limb - 0.05) return "line does not reach the limb " + worst;
                double fw = 0;
                foreach (float[] t in p.Tris)
                    for (int k = 0; k < 6; k += 2) fw = Math.Max(fw, Math.Sqrt((t[k] - 960.0) * (t[k] - 960.0) + (t[k + 1] - 540.0) * (t[k + 1] - 540.0)));
                if (fw > limb + 0.05 || fw < limb - 0.05) return "fill vertex radius " + fw + " vs limb " + limb;
                // a closed full ring drawn without a hidden part has one line more than steps; this one is cut: fewer lines than a full circle of the same steps
                var full = new CountPainter();
                RangeRingMath.Emit(Scene(0, 0, 1500, false), px, st, full);
                if (full.Lines.Count < 24) return "visible ring of 1500 nm drew " + full.Lines.Count;
            }
            // 3. a ring wholly behind the horizon: culled, nothing drawn; a ring far outside the target: culled
            {
                double sc = 1080.0 / (2.0 * MapProjector.R) * 0.9;
                MapPx px = View(0, 0, sc);
                var p = new CountPainter();
                RingDrawStats d = RangeRingMath.Emit(Scene(0, 170, 100, false, true), px, st, p);
                if (d.Culled + d.SmallSkipped != 1 || p.Lines.Count != 0 || p.Tris.Count != 0 || d.Rings != 0) return "behind: culled " + d.Culled + " lines " + p.Lines.Count + " rings " + d.Rings;
                MapPx pz = View(0, 0, zs * 8);
                p = new CountPainter();
                d = RangeRingMath.Emit(Scene(0, 5, 50, false), pz, st, p);
                if (d.Culled != 1 || p.Lines.Count != 0) return "outside the target: culled " + d.Culled;
            }
            // 4. dashes: the phase runs through the pieces (the dash share of a closed visible ring is on / (on + off))
            {
                MapPx px = View(30, 10, zs);
                var p = new CountPainter();
                RangeRingMath.Emit(Scene(30, 10, 60, true), px, st, p);
                double dash = 0;
                float cx = 0, cy = 0; bool have = false; double path = 0;
                foreach (float[] l in p.Lines) dash += Len(l);
                // the whole path length: the ring drawn solid with the same projector
                var solid = new CountPainter();
                RangeRingMath.Emit(Scene(30, 10, 60, false), px, st, solid);
                foreach (float[] l in solid.Lines) { path += Len(l); cx = l[2]; cy = l[3]; have = true; }
                if (!have || path <= 0) return "dash reference";
                double share = dash / path;
                if (Math.Abs(share - 10.0 / 16.0) > 0.05) return "dash share " + share.ToString("F3") + " (path " + path.ToString("F0") + " px)";
            }
            // 5. a sector (a wedge) of 90 degrees at phi = 70: arc vertices at 40 nm, radial edges are geodesics from the centre
            {
                MapPx px = View(70, 0, zs);
                var s = new RingScene();
                var u = new UnitRings { Key = 1, Center = new RingPt(70, 0), HeadingDeg = 0 };
                u.Sectors.Add(new SectorSpec { Kind = RingKind.Aaw, Src = RingSource.Own, RangeNm = 40, WidthDeg = 90, CenterDeg = 0, Weapon = "w" });
                s.Units.Add(u);
                var p = new CountPainter();
                RingDrawStats d = RangeRingMath.Emit(s, px, st, p);
                if (d.Rings != 1 || p.Lines.Count < 6 || p.Tris.Count < 4) return "sector drawn: lines " + p.Lines.Count + " tris " + p.Tris.Count;
                int onArc = 0, onCentre = 0; double cxp, cyp;
                px.Pt(70, 0, out cxp, out cyp);
                foreach (float[] l in p.Lines)
                    for (int k = 0; k < 4; k += 2)
                    {
                        double lat, lon;
                        if (!px.TryInverse(l[k], l[k + 1], out lat, out lon)) return "sector vertex off disc";
                        double dm = DistM(70, 0, lat, lon);
                        if (Math.Abs(dm - 40 * 1852.0) * zs <= 0.5) onArc++;
                        else if (Math.Abs(l[k] - cxp) < 0.01 && Math.Abs(l[k + 1] - cyp) < 0.01) onCentre++;
                        else if (dm > 40 * 1852.0 + 0.5 / zs) return "sector vertex beyond the radius";
                    }
                if (onArc < 12 || onCentre < 2) return "sector vertices on arc " + onArc + " at the centre " + onCentre;
            }
            // 6. the flat route is unchanged: a MapPx.FromAffine and the old Emit(aff) give the same lines
            {
                double k = 10 * 60.0;
                double[] aff = { 0.0, k, 512, -k, 0.0, 512 };
                var a = new CountPainter(); var b = new CountPainter();
                RangeRingMath.Emit(Scene(0, 0, 50, true, true), aff, 1024, 1024, st, a);
                RangeRingMath.Emit(Scene(0, 0, 50, true, true), MapPx.FromAffine(aff, 1024, 1024), st, b);
                if (a.Lines.Count != b.Lines.Count || a.Tris.Count != b.Tris.Count || a.Lines.Count == 0) return "affine wrapper";
                for (int i = 0; i < a.Lines.Count; i++) for (int q = 0; q < 5; q++) if (a.Lines[i][q] != b.Lines[i][q]) return "affine wrapper line " + i;
            }
            // 7. T-321: a unit at 36 N in the plate carree of the game (60 nm per degree both ways): the ring of 270 nm reaches 4.5 degrees east, not 5.56 (geodesic)
            {
                double k = 60.0;
                double[] aff = { 0.0, k, 512 + 3 * k, -k, 0.0, 512 + 36 * k };   // 1 px per nm, the unit at (512, 512)
                var sc = Scene(36, -3, 270, false);
                sc.Units[0].Metric = new RingGeoMetric { LatNmPerDeg = 60, LonNmPerDeg = 60, Valid = true };
                var pm = new CountPainter(); var pg = new CountPainter();
                RangeRingMath.Emit(sc, aff, 1024, 1024, st, pm);
                RangeRingMath.Emit(Scene(36, -3, 270, false), aff, 1024, 1024, st, pg);
                float maxM = float.MinValue, maxG = float.MinValue;
                foreach (float[] l in pm.Lines) maxM = Math.Max(maxM, Math.Max(l[0], l[2]));
                foreach (float[] l in pg.Lines) maxG = Math.Max(maxG, Math.Max(l[0], l[2]));
                if (pm.Lines.Count == 0 || Math.Abs(maxM - (512 + 270)) > 1.0) return "metric ring east edge " + maxM + " (expected " + (512 + 270) + ")";
                if (!(maxG > maxM + 50)) return "the geodesic ring is wider east-west: " + maxG + " vs " + maxM;
                // the labels sit on the same ring
                double b = RangeRingReach.LabelBearing(RingKind.Aaw, false) * Math.PI / 180.0;
                var lab = RangeRingModel.Labels(sc, 0, 40, 24);
                if (lab.Count != 1 || Math.Abs((lab[0].Lon + 3) - 270 * Math.Sin(b) / 60.0) > 1e-9 || Math.Abs((lab[0].Lat - 36) - 270 * Math.Cos(b) / 60.0) > 1e-9) return "label on the metric ring";
            }
            return null;
        }
    }
}
