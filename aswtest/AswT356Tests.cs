using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-356 (ADR-0135): longitudes across the antimeridian in the AswMath primitives, the world cache reset. `aswtest t356 [prims|cells]`.
    internal static class AswT356Tests
    {
        public static string Run(string only)
        {
            string e;
            if ((only == null || only == "prims") && (e = Prims()) != null) return "prims: " + e;
            if ((only == null || only == "cells") && (e = Cells()) != null) return "cells: " + e;
            if ((only == null || only == "copies") && (e = Copies()) != null) return "copies: " + e;
            return null;
        }

        private static bool Same(double a, double b) { return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b); }

        private static string Prims()
        {
            var rnd = new Random(3561);
            foreach (bool globe in new[] { false, true })
                foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
                {
                    using (AswMath.WorldScope(globe))
                    {
                        string w = " globe=" + globe + " phi=" + phi;
                        // (1) bit-identical to the old formula for |dLon| <= 179
                        for (int i = 0; i < 200; i++)
                        {
                            var a = new GeoPt(phi + rnd.NextDouble() - 0.5, rnd.NextDouble() * 300 - 150);
                            var b = new GeoPt(phi + rnd.NextDouble() - 0.5, a.Lon + rnd.NextDouble() * 358 - 179);
                            double k = AswMath.LonK(a.Lat, b.Lat), ks = AswMath.LonScale(a.Lat, b.Lat, globe);
                            double n = (b.Lat - a.Lat) * 60.0, eo = (b.Lon - a.Lon) * 60.0 * k, es = (b.Lon - a.Lon) * 60.0 * ks;
                            var f = AswMath.FlatNm(a, b);
                            if (!Same(f.X, n) || !Same(f.Y, eo)) return "FlatNm bits" + w;
                            if (!Same(AswMath.DistNm(a, b), Math.Sqrt(n * n + eo * eo))) return "DistNm bits" + w;
                            if (!Same(AswMath.BearingDeg(a, b), AswMath.Norm360(Math.Atan2(eo, n) / (Math.PI / 180.0)))) return "BearingDeg bits" + w;
                            if (!Same(AswMath.DistNm(a, b, globe), Math.Sqrt(n * n + es * es))) return "DistNm(globe) bits" + w;
                            var wn = AswMath.WorldNm(a, b, globe);
                            if (!Same(wn.X, n) || !Same(wn.Y, es)) return "WorldNm bits" + w;
                            var tl = AswMath.ToLocal(a, 33.0, b);
                            double ax = 33.0 * (Math.PI / 180.0), ca = Math.Cos(ax), sa = Math.Sin(ax);
                            if (!globe && (!Same(tl.X, n * ca + eo * sa) || !Same(tl.Y, -n * sa + eo * ca))) return "ToLocal bits" + w;
                            // T-362: on the globe ToLocal is azimuthal equidistant: the radius is the great circle
                            if (globe && Math.Abs(Math.Sqrt(tl.X * tl.X + tl.Y * tl.Y) - AswMath.GreatCircleNm(a, b)) > 1e-6 * Math.Max(1.0, AswMath.GreatCircleNm(a, b))) return "ToLocal radius" + w;
                            double pn = (b.Lat - a.Lat) * 60.0, pe = (b.Lon - a.Lon) * 60.0 * AswMath.LonScale(b.Lat, a.Lat, globe);
                            double d = Math.Sqrt(pn * pn + pe * pe);
                            double rc = AswMath.RadialClosingKn(b, a, 12.0, -7.0, globe, 99.0);
                            if (d >= 1e-6 && !Same(rc, -((pn * 12.0 + pe * -7.0) / d))) return "RadialClosingKn bits" + w;
                            string src;
                            double ad = AswMath.AxisDeg(null, 10.0, new List<double[]> { new[] { a.Lat, a.Lon }, new[] { b.Lat, b.Lon } }, out src);
                            double nn = (b.Lat - a.Lat) * 60.0, ee = (b.Lon - a.Lon) * 60.0 * k;
                            if (Math.Sqrt(nn * nn + ee * ee) >= AswMath.MinTrackNm && !Same(ad, AswMath.Norm360(Math.Atan2(ee, nn) / (Math.PI / 180.0)))) return "AxisDeg bits" + w;
                        }
                        // (2) across 180 = the shifted pair
                        var p = new GeoPt(phi, 179.9); var q = new GeoPt(phi + 0.1, -179.9); var q2 = new GeoPt(phi + 0.1, 180.1);
                        if (Math.Abs(AswMath.DistNm(p, q) - AswMath.DistNm(p, q2)) > 1e-9) return "DistNm across" + w;
                        if (Math.Abs(AswMath.DistNm(p, q, globe) - AswMath.DistNm(p, q2, globe)) > 1e-9) return "DistNm(globe) across" + w;
                        if (Math.Abs(AswMath.BearingDeg(p, q) - AswMath.BearingDeg(p, q2)) > 1e-9) return "BearingDeg across" + w;
                        var f1 = AswMath.FlatNm(p, q); var f2 = AswMath.FlatNm(p, q2);
                        if (Math.Abs(f1.X - f2.X) > 1e-9 || Math.Abs(f1.Y - f2.Y) > 1e-9) return "FlatNm across" + w;
                        var w1 = AswMath.WorldNm(p, q, globe); var w2 = AswMath.WorldNm(p, q2, globe);
                        if (Math.Abs(w1.X - w2.X) > 1e-9 || Math.Abs(w1.Y - w2.Y) > 1e-9) return "WorldNm across" + w;
                        var l1 = AswMath.ToLocal(p, 40.0, q); var l2 = AswMath.ToLocal(p, 40.0, q2);
                        if (Math.Abs(l1.X - l2.X) > 1e-9 || Math.Abs(l1.Y - l2.Y) > 1e-9) return "ToLocal across" + w;
                        if (Math.Abs(AswMath.RadialClosingKn(q, p, 5, 9, globe, 0) - AswMath.RadialClosingKn(q2, p, 5, 9, globe, 0)) > 1e-9) return "RadialClosingKn across" + w;
                        string s1, s2;
                        double a1 = AswMath.AxisDeg(null, 10.0, new List<double[]> { new[] { p.Lat, p.Lon }, new[] { q.Lat, q.Lon } }, out s1);
                        double a2 = AswMath.AxisDeg(null, 10.0, new List<double[]> { new[] { p.Lat, p.Lon }, new[] { q2.Lat, q2.Lon } }, out s2);
                        if (Math.Abs(a1 - a2) > 1e-9 || s1 != s2) return "AxisDeg across" + w;
                    }
                }
            return null;
        }

        private static string Copies()
        {
            foreach (bool globe in new[] { false, true })
                foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
                {
                    var a = SubDatumMath.ToLocal(phi, 179.9, phi + 0.1, -179.9, globe);
                    var b = SubDatumMath.ToLocal(phi, 179.9, phi + 0.1, 180.1, globe);
                    if (Math.Abs(a.X - b.X) > 1e-9 || Math.Abs(a.Y - b.Y) > 1e-9) return "SubDatumMath.ToLocal across 180 " + globe + "/" + phi;
                }
            return null;
        }

        private static string Cells()
        {
            var c = new WorldCellCache();
            c.Put(7, 0.0, true, true, true);
            if (!c.Last) return "Last not set";
            bool g;
            if (!c.TryGet(7, 1.0, out g) || !g) return "decided cell not served";
            c.ResetCells();
            if (!c.Last) return "ResetCells lost Last";
            if (c.TryGet(7, 1.0, out g)) return "cell survived ResetCells";
            if (!g) return "Last not offered after the reset";
            c.Put(7, 2.0, true, true, false);
            if (c.Last) return "re-measure after the reset not stored";
            return null;
        }
    }
}
