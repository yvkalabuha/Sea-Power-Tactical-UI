#if SAR_WIP
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class Program
    {

        /// <summary>T-348: the order rectangle 10x10 nm is 10 true nm on the beta globe at 0/45/60/70 deg (lives here: SarRouteMath stays free of SarCommands).</summary>
        private static string OrderRectLat()
        {
            try
            {
                foreach (double lat in new[] { 0.0, 45.0, 60.0, 70.0 })
                    using (AswMath.WorldScope(true))
                    {
                        var cmd = new Dictionary<string, object>();
                        cmd["group"] = "G";
                        var ar = new Dictionary<string, object>();
                        ar["bearing"] = 90.0; ar["dist_nm"] = 5.0; ar["size_nm"] = 10.0;
                        var org = new Dictionary<string, object>(); org["lat"] = lat; org["lon"] = 10.0;
                        ar["origin"] = org; cmd["area"] = ar;
                        string err;
                        SarOrderCmd o = SarCommands.ParseOrder(cmd, out err);
                        if (o == null) return "lat " + lat + ": ParseOrder " + err;
                        double[] r = o.Area.Rect;
                        double mid = (r[0] + r[2]) / 2;
                        double w = AswMath.GreatCircleNm(new GeoPt(mid, r[1]), new GeoPt(mid, r[3]));
                        double h = AswMath.GreatCircleNm(new GeoPt(r[0], r[1]), new GeoPt(r[2], r[1]));
                        if (Math.Abs(w - 10.0) > 0.15 || Math.Abs(h - 10.0) > 0.15) return "lat " + lat + ": rect " + w.ToString("0.000") + "x" + h.ToString("0.000");
                    }
                return null;
            }
            catch (Exception ex) { return "lat rect: " + ex; }
        }

        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            if (a == "save-sample") { Console.WriteLine(SarSave.Sample()); return 0; }
            int bad = 0;
            bool all = a == "all";
            if (all || a == "airops") bad += Report("airops", AirOpsMath.SelfTest());
            if (all || a == "math") bad += Report("math", SarMath.SelfTest());
            if (all || a == "scan") bad += Report("scan", SarRaftScan.SelfTest());
            if (all || a == "cache") bad += Report("cache", SarRaftCache.SelfTest());
            if (all || a == "perf") bad += Report("perf", SarControllerTests.Perf());
            if (all || a == "route") bad += Report("route", SarRouteMath.SelfTest());
            if (all || a == "lat") bad += Report("lat", SarRouteMath.SelfTestLat() ?? OrderRectLat());
            if (all || a == "save") bad += Report("save", SarSave.SelfTest());
            if (all || a == "commands") bad += Report("commands", SarCommands.SelfTest());
            if (all || a == "window") bad += Report("window", SarWindow.SelfTest());
            if (all || a == "ships") bad += Report("ships", SarShipPanel.SelfTest());
            if (all || a == "controller") bad += Report("controller", SarControllerTests.Run());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("sar " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
#endif
