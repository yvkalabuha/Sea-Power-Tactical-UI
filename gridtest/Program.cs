using System;
using System.IO;
using System.Reflection;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "lcc") return LccParity(args[1]);
            string err = GridHeadingMath.SelfTest();
            Console.WriteLine("grid math selftest " + (err == "ok" ? "ok" : "FAILED: " + err));
            return err == "ok" ? 0 : 1;
        }

        /// <summary>T-308: our formulas against the game's own SeaPower.LCC (Seapower-Scripts.dll of the beta); prints "lcc parity ok|skip|FAILED" and a |conv| table.</summary>
        private static int LccParity(string dir)
        {
            string dll = Path.Combine(dir, "Seapower-Scripts.dll");
            if (!File.Exists(dll)) { Console.WriteLine("lcc parity skip: no dll"); return 0; }
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var p = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            Assembly asm;
            Type lccT, geoT;
            try { asm = Assembly.LoadFrom(dll); lccT = asm.GetType("SeaPower.LCC"); geoT = asm.GetType("SeaPower.GeoPosition"); }
            catch (Exception e) { Console.WriteLine("lcc parity skip: " + e.GetType().Name); return 0; }
            if (lccT == null || geoT == null) { Console.WriteLine("lcc parity skip: no SeaPower.LCC"); return 0; }
            var gc = lccT.GetMethod("GridConvergence");
            var ef = lccT.GetMethod("EarthFixedToUnityDirection");
            var geoCtor = geoT.GetConstructor(new[] { typeof(double), typeof(double), typeof(double) });
            if (gc == null || ef == null || geoCtor == null) { Console.WriteLine("lcc parity skip: members"); return 0; }
            var d3T = ef.GetParameters()[1].ParameterType;
            double[][] centers = { new[] { 60.0, 5.0 }, new[] { 20.0, -40.0 }, new[] { -35.0, 150.0 } };
            double[] deltas = { -5, 0, 5 };
            double worst = 0; int n = 0;
            foreach (var c in centers)
            {
                var lcc = Activator.CreateInstance(lccT, new object[] { c[1], c[0], 83.33333333333333 });
                double maxConv = 0;
                foreach (double dla in deltas)
                    foreach (double dlo in deltas)
                    {
                        double lat = c[0] + dla, lon = c[1] + dlo;
                        var g = geoCtor.Invoke(new object[] { lat, lon, 0.0 });
                        double conv = (double)gc.Invoke(lcc, new[] { g });
                        maxConv = Math.Max(maxConv, Math.Abs(conv));
                        double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
                        for (int h = 0; h < 360; h += 30)
                        {
                            double hr = h * Math.PI / 180;
                            // true direction h at (lat, lon) in earth-fixed axes: north cos(h) + east sin(h)
                            double nx = -Math.Sin(la) * Math.Cos(lo), ny = -Math.Sin(la) * Math.Sin(lo), nz = Math.Cos(la);
                            double ex = -Math.Sin(lo), ey = Math.Cos(lo), ez = 0;
                            var dir3 = Activator.CreateInstance(d3T, new object[] { nx * Math.Cos(hr) + ex * Math.Sin(hr), ny * Math.Cos(hr) + ey * Math.Sin(hr), nz * Math.Cos(hr) + ez * Math.Sin(hr) });
                            var u = ef.Invoke(lcc, new[] { g, dir3 });
                            double ux = Convert.ToDouble(u.GetType().GetField("x").GetValue(u)), uz = Convert.ToDouble(u.GetType().GetField("z").GetValue(u));
                            double yaw = GridHeadingMath.Norm360(Math.Atan2(ux, uz) * 180 / Math.PI);
                            double want = GridHeadingMath.GridFromTrue(h, conv);
                            double e1 = Math.Abs(GridHeadingMath.Norm180(yaw - want));
                            double tx, tz;
                            GridHeadingMath.RotateGridToTrue(ux, uz, conv, out tx, out tz);
                            double e2 = Math.Abs(GridHeadingMath.Norm180(GridHeadingMath.Norm360(Math.Atan2(tx, tz) * 180 / Math.PI) - h));
                            worst = Math.Max(worst, Math.Max(e1, e2)); n++;
                        }
                    }
                Console.WriteLine("center " + c[0] + "," + c[1] + " max|conv| in +-5 deg = " + maxConv.ToString("F2") + " deg");
            }
            bool ok = worst <= 0.01;
            Console.WriteLine("lcc parity " + (ok ? "ok" : "FAILED") + " n=" + n + " worst=" + worst.ToString("F5") + " deg");
            return ok ? 0 : 1;
        }
    }
}
