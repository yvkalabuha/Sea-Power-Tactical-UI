using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-356 (ADR-0135): longitudes across the antimeridian. Every helper returns its plain input bit-for-bit when no wrap is needed.
    /// No dependencies: every stand compiles it (Directory.Build.targets); VoiceBotBridge does not (Common/ inlines the wrap).</summary>
    internal static class GeoLon
    {
        /// <summary>toLon - fromLon in (-180, 180]; the plain subtraction when it is already inside.</summary>
        public static double DLon(double fromLon, double toLon)
        {
            double d = toLon - fromLon;
            if (d > 180.0 || d <= -180.0)
            {
                if (d > 540.0 || d < -540.0) { d = Math.IEEERemainder(d, 360.0); if (d <= -180.0) d += 360.0; }
                else if (d > 180.0) d -= 360.0;
                else d += 360.0;
            }
            return d;
        }

        /// <summary>lon moved by whole turns to within 180 deg of refLon; lon itself (same bits) when it is already there.</summary>
        public static double UnwrapLon(double refLon, double lon)
        {
            double d = lon - refLon;
            if (d <= 180.0 && d > -180.0) return lon;
            return refLon + DLon(refLon, lon);
        }

        /// <summary>Absolute longitude into (-180, 180]; the same bits inside.</summary>
        public static double WrapLon(double lon)
        {
            if (lon <= 180.0 && lon > -180.0) return lon;
            if (lon > 540.0 || lon < -540.0) { lon = Math.IEEERemainder(lon, 360.0); if (lon <= -180.0) lon += 360.0; return lon; }
            return lon > 180.0 ? lon - 360.0 : lon + 360.0;
        }

        /// <summary>(a + b) / 2 when |b - a| is at most 180 (the old midpoint, same bits), else the midpoint the short way (unwrapped, not wrapped).</summary>
        public static double MidLon(double lonA, double lonB)
        {
            double d = lonB - lonA;
            if (d <= 180.0 && d > -180.0) return (lonA + lonB) / 2;
            return (lonA + UnwrapLon(lonA, lonB)) / 2;
        }

        /// <summary>Mean along the short way around the first value: sum of UnwrapLon(first, x) in order / count (same bits as the plain loop in range). 0 for no values.</summary>
        public static double MeanLon(IEnumerable<double> lons)
        {
            double sum = 0, first = 0; int n = 0;
            foreach (double x in lons)
            {
                if (n == 0) first = x;
                sum += UnwrapLon(first, x);
                n++;
            }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            if (Math.Abs(DLon(179.9, -179.9) - 0.2) > 1e-9) return "DLon(179.9,-179.9) = " + DLon(179.9, -179.9);
            if (Math.Abs(DLon(-179.9, 179.9) + 0.2) > 1e-9) return "DLon(-179.9,179.9) = " + DLon(-179.9, 179.9);
            if (DLon(0, 180) != 180.0) return "DLon(0,180)";
            if (DLon(0, -180) != 180.0) return "DLon(0,-180)";
            if (Math.Abs(DLon(10, 10 + 720 + 5) - 5) > 1e-9) return "DLon over two turns";
            if (Math.Abs(DLon(10, 10 - 720 - 5) + 5) > 1e-9) return "DLon over two turns (negative)";
            var rnd = new Random(356);
            for (int i = 0; i < 10000; i++)
            {
                double a = rnd.NextDouble() * 360 - 180, b = a + (rnd.NextDouble() * 358 - 179);
                double plain = b - a;
                if (BitConverter.DoubleToInt64Bits(DLon(a, b)) != BitConverter.DoubleToInt64Bits(plain)) return "DLon bits " + a + "/" + b;
                if (BitConverter.DoubleToInt64Bits(UnwrapLon(a, b)) != BitConverter.DoubleToInt64Bits(b)) return "UnwrapLon bits " + a + "/" + b;
                if (BitConverter.DoubleToInt64Bits(MidLon(a, b)) != BitConverter.DoubleToInt64Bits((a + b) / 2)) return "MidLon bits " + a + "/" + b;
                double w = rnd.NextDouble() * 359.9 - 179.9;
                if (BitConverter.DoubleToInt64Bits(WrapLon(w)) != BitConverter.DoubleToInt64Bits(w)) return "WrapLon bits " + w;
            }
            if (Math.Abs(UnwrapLon(179.9, -179.9) - 180.1) > 1e-9) return "UnwrapLon(179.9,-179.9)";
            if (Math.Abs(UnwrapLon(-179.9, 179.9) + 180.1) > 1e-9) return "UnwrapLon(-179.9,179.9)";
            if (Math.Abs(WrapLon(180.4) + 179.6) > 1e-9) return "WrapLon(180.4)";
            if (Math.Abs(WrapLon(-180.4) - 179.6) > 1e-9) return "WrapLon(-180.4)";
            if (WrapLon(180.0) != 180.0 || Math.Abs(WrapLon(-180.0) - 180.0) > 1e-12) return "WrapLon at the edge";
            if (Math.Abs(MidLon(179.8, -179.8) - 180.0) > 1e-9) return "MidLon across: " + MidLon(179.8, -179.8);
            var data = new List<double>();
            for (int i = 0; i < 50; i++) data.Add(rnd.NextDouble() * 20 - 10);
            double s = 0; foreach (double x in data) s += x;
            if (BitConverter.DoubleToInt64Bits(MeanLon(data)) != BitConverter.DoubleToInt64Bits(s / data.Count)) return "MeanLon bits";
            if (Math.Abs(MeanLon(new[] { 179.8, -179.8, 179.9, -179.9 }) - 180.0) > 1e-9) return "MeanLon across: " + MeanLon(new[] { 179.8, -179.8, 179.9, -179.9 });
            return null;
        }
    }
}
