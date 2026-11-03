using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-112 stand entry point. Prints "cap <name> selftest ok|FAILED: ..." per section.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            if (a == "save-sample") { Console.WriteLine(CapSave.Sample()); return 0; }
            int bad = 0;
            bool all = a == "all";
            if (all || a == "ownership") bad += Report("ownership", AirOwnership.SelfTest());
            if (all || a == "math") bad += Report("math", CapMath.SelfTest("math"));
            if (all || a == "loadout") bad += Report("loadout", CapMath.SelfTest("loadout"));
            if (all || a == "lanes") bad += Report("lanes", CapMath.SelfTest("lanes"));
            if (all || a == "threat") bad += Report("threat", CapMath.SelfTest("threat"));
            if (all || a == "relief") bad += Report("relief", CapMath.SelfTest("relief"));
            if (all || a == "roe") bad += Report("roe", CapMath.SelfTest("roe"));
            if (all || a == "alertmath") bad += Report("alertmath", CapAlertMath.SelfTest());
            if (all || a == "alertbind") bad += Report("alertbind", CapAlertBind.SelfTest());
            if (all || a == "save") bad += Report("save", CapSave.SelfTest());
            if (all || a == "commands") bad += Report("commands", CapCommands.SelfTest());
            if (all || a == "window") bad += Report("window", CapWindow.SelfTest());
            if (all || a == "windowmodel") bad += Report("windowmodel", CapWindowModel.SelfTest());
            if (all || a == "controller") bad += Report("controller", CapControllerTests.Run());
            if (a == "t175") return Report("t175" + (args.Length > 1 ? " " + args[1] : ""), CapT175Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t179") return Report("t179" + (args.Length > 1 ? " " + args[1] : ""), CapT179Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t186") return Report("t186" + (args.Length > 1 ? " " + args[1] : ""), CapT186Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t187") return Report("t187" + (args.Length > 1 ? " " + args[1] : ""), CapT187Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t197") return Report("t197" + (args.Length > 1 ? " " + args[1] : ""), CapT197Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t198") return Report("t198" + (args.Length > 1 ? " " + args[1] : ""), CapT198Tests.Run(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null));
            if (a == "t204") return Report("t204" + (args.Length > 1 ? " " + args[1] : ""), CapT204Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t211") return Report("t211" + (args.Length > 1 ? " " + args[1] : ""), CapT211Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t218") return Report("t218" + (args.Length > 1 ? " " + args[1] : ""), CapT218Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t226") return Report("t226" + (args.Length > 1 ? " " + args[1] : ""), CapT226Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t235") return Report("t235" + (args.Length > 1 ? " " + args[1] : ""), CapT235Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t235save") return Report("t235save" + (args.Length > 1 ? " " + args[1] : ""), CapT235SaveTests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t235ctl") return Report("t235ctl" + (args.Length > 1 ? " " + args[1] : ""), CapT235CtlTests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t244") return Report("t244" + (args.Length > 1 ? " " + args[1] : ""), CapT244Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t250") return Report("t250" + (args.Length > 1 ? " " + args[1] : ""), CapT250Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t257") return Report("t257" + (args.Length > 1 ? " " + args[1] : ""), CapT257Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "radar") return Report("radar", AirRadarLedger.SelfTest() ?? CapT241Tests.Run());   // T-241 (ADR-0099)
            if (a == "t241") return Report("t241" + (args.Length > 1 ? " " + args[1] : ""), CapT241Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t315") return Report("t315" + (args.Length > 1 ? " " + args[1] : ""), CapT315Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t317") return Report("t317" + (args.Length > 1 ? " " + args[1] : ""), CapT317Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t319") return Report("t319" + (args.Length > 1 ? " " + args[1] : ""), CapT319Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t325") return Report("t325" + (args.Length > 1 ? " " + args[1] : ""), CapT325Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t331") return Report("t331" + (args.Length > 1 ? " " + args[1] : ""), CapT331Tests.Run(args.Length > 1 ? args[1] : null));   // T-331
            if (a == "t362") return Report("t362" + (args.Length > 1 ? " " + args[1] : ""), CapT362Tests.Run(args.Length > 1 ? args[1] : null));   // T-362
            if (a == "lat") return Report("lat", CapLatTests.Run());   // T-348
            if (a == "icpt") return Report("icpt", CapInterceptMath.SelfTest());   // T-333
            if (a == "ff") return Report("ff", CapFfMath.SelfTest());
            if (a == "qaforce") return Report("qaforce", QaForceMath.SelfTest());   // T-361
            if (a == "t333") return Report("t333" + (args.Length > 1 ? " " + args[1] : ""), CapT333Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t333-ff") return Report("t333-ff" + (args.Length > 1 ? " " + args[1] : ""), CapT333FfTests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t330") return Report("t330" + (args.Length > 1 ? " " + args[1] : ""), CapT330Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t328") return Report("t328" + (args.Length > 1 ? " " + args[1] : ""), CapT328Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t323") return Report("t323" + (args.Length > 1 ? " " + args[1] : ""), CapT323Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t326") return Report("t326" + (args.Length > 1 ? " " + args[1] : ""), CapT326Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t219") return Report("t219" + (args.Length > 1 ? " " + args[1] : ""), CapT219Tests.Run(args.Length > 1 ? args[1] : null));
            if (all) bad += Report("t175", CapT175Tests.Run());
            if (all) bad += Report("t179", CapT179Tests.Run());
            if (all) bad += Report("t186", CapT186Tests.Run());
            if (all) bad += Report("t187", CapT187Tests.Run());
            if (all) bad += Report("t197", CapT197Tests.Run());
            if (all) bad += Report("t198", CapT198Tests.Run());
            if (all) bad += Report("t204", CapT204Tests.Run());
            if (all) bad += Report("t211", CapT211Tests.Run());
            if (all) bad += Report("t218", CapT218Tests.Run());
            if (all) bad += Report("t315", CapT315Tests.Run());
            if (all) bad += Report("t317", CapT317Tests.Run());
            if (all) bad += Report("t319", CapT319Tests.Run());
            if (all) bad += Report("t325", CapT325Tests.Run());
            if (all) bad += Report("t323", CapT323Tests.Run());
            if (all) bad += Report("t326", CapT326Tests.Run());
            if (all) bad += Report("t219", CapT219Tests.Run());
            if (all) bad += Report("t226", CapT226Tests.Run());
            if (all) bad += Report("t235", CapT235Tests.Run());
            if (all) bad += Report("t235save", CapT235SaveTests.Run());
            if (all) bad += Report("t235ctl", CapT235CtlTests.Run());
            if (all) bad += Report("t244", CapT244Tests.Run());
            if (all) bad += Report("t250", CapT250Tests.Run());
            if (all) bad += Report("t257", CapT257Tests.Run());
            if (all) bad += Report("radar", AirRadarLedger.SelfTest());
            if (all) bad += Report("t241", CapT241Tests.Run());
            if (all) bad += Report("icpt", CapInterceptMath.SelfTest());
            if (all) bad += Report("ff", CapFfMath.SelfTest());
            if (all) bad += Report("qaforce", QaForceMath.SelfTest());   // T-361
            if (all) bad += Report("lat", CapLatTests.Run());
            if (all) bad += Report("t333", CapT333Tests.Run());
            if (all) bad += Report("t333-ff", CapT333FfTests.Run());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("cap " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }

    // T-348: CAP geometry on latitudes 0/45/60/70 for both worlds (globe: true metres within 1.5 %; flat: bit-identical to the old formulas).
    internal static class CapLatTests
    {
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static void Near(string n, double a, double b, double eps) { if (!(Math.Abs(a - b) <= eps) && _err == null) _err = n + ": " + a + " != " + b; }

        public static string Run()
        {
            _err = null;
            foreach (double lat in new[] { 0.0, 45.0, 60.0, 70.0 })
            {
                using (AswMath.WorldScope(true)) Globe(lat);
                using (AswMath.WorldScope(false)) Flat(lat);
            }
            Antimeridian();
            return _err;
        }

        /// <summary>T-356: a zone 179.5..180.5 deg at 60 N gives the lanes, distances, centre and circle of the zone shifted by -10 deg.</summary>
        private static void Antimeridian()
        {
            foreach (bool globe in new[] { false, true })
                using (AswMath.WorldScope(globe))
                {
                    string t = " antimeridian globe=" + globe;
                    var zw = new CapZone { Rect = new[] { 60.0, 179.5, 61.0, 180.5 } };
                    var zs = new CapZone { Rect = new[] { 60.0, 169.5, 61.0, 170.5 } };
                    var p = new CapParams();
                    var lw = CapMath.Lanes(zw, 2, p, 8); var ls = CapMath.Lanes(zs, 2, p, 8);
                    True("lanes count" + t, lw.Count == 2 && ls.Count == 2);
                    if (lw.Count == 2 && ls.Count == 2)
                        for (int i = 0; i < 2; i++)
                        {
                            Near("lane centre lat" + i + t, lw[i].Center.Lat, ls[i].Center.Lat, 1e-9);
                            Near("lane centre lon" + i + t, GeoLon.DLon(lw[i].Center.Lon, ls[i].Center.Lon + 10.0), 0, 1e-9);
                        }
                    foreach (var q in new[] { new GeoPt(60.5, -179.95), new GeoPt(60.5, 179.7), new GeoPt(62.0, -179.5), new GeoPt(60.5, -178.5) })
                        Near("DistToZoneNm " + q.Lon + t, CapMath.DistToZoneNm(q, zw), CapMath.DistToZoneNm(new GeoPt(q.Lat, GeoLon.WrapLon(q.Lon) - 10.0), zs), 1e-6);
                    True("inside" + t, CapMath.DistToZoneNm(new GeoPt(60.5, -179.95), zw) == 0);
                    var circ = CapMath.Lanes(new CapZone { Rect = new[] { 60.0, 179.9, 60.1, 180.1 } }, 1, p, 8);
                    if (circ.Count == 1 && circ[0].Circle)
                        True("OnCircle" + t, CapMath.OnCircle(new GeoPt(circ[0].Center.Lat, GeoLon.WrapLon(circ[0].Center.Lon)), circ[0], p));
                }
            string err;
            var d = (Dictionary<string, object>)Json.Parse("{\"group\":\"G\",\"rect\":[60,179.5,61,-179.5]}");
            CapOrderCmd o = CapCommands.ParseOrder(d, out err);
            True("parse rect4" + err, o != null && err == null && Math.Abs(o.Zone.Rect[1] - 179.5) < 1e-12 && Math.Abs(o.Zone.Rect[3] - 180.5) < 1e-9);
        }

        private static void Globe(double lat)
        {
            string t = " @" + lat + " globe";
            var origin = new GeoPt(lat, 10.0);
            // PolarRect 40 (east-west) x 20 (north-south) nm: true sizes
            var cmd = new CapOrderCmd { Polar = true, Bearing = 0, DistNm = 0, SizeNm = 40, WNm = 20 };
            double[] r = CapCommands.PolarRect(origin, cmd);
            double wNm = AswMath.GreatCircleNm(new GeoPt(lat, r[1]), new GeoPt(lat, r[3]));
            double hNm = AswMath.GreatCircleNm(new GeoPt(r[0], 10.0), new GeoPt(r[2], 10.0));
            Near("polar w" + t, wNm / 40, 1, 0.005); Near("polar h" + t, hNm / 20, 1, 0.005);
            var z = new CapZone { Rect = r };
            // an east-west strip layout stays inside the zone and evenly spaced in true nm
            var p = new CapParams();
            var lanes = CapMath.Lanes(z, 2, p, 8);
            True("lanes" + t, lanes.Count == 2);
            foreach (var l in lanes)
                foreach (var g in l.Track)
                    True("lane inside" + t, g.Lat >= r[0] - 1e-9 && g.Lat <= r[2] + 1e-9 && g.Lon >= r[1] - 1e-9 && g.Lon <= r[3] + 1e-9);
            // leave distance: 5 true nm east of the zone
            var east = AswMath.Offset(new GeoPt((r[0] + r[2]) / 2, r[3]), 90, 5);
            Near("dist east" + t, CapMath.DistToZoneNm(east, z) / 5, 1, 0.005);
            // circle lane points lie on the radius
            var lane = new CapLane { Center = new GeoPt(lat, 10.0), Circle = true, StartDeg = 0, RadiusNm = 6 };
            var route = CapMath.CircleRouteFrom(lane, 0, 6, 1, p);
            foreach (var g in route) Near("circle radius" + t, AswMath.GreatCircleNm(lane.Center, g) / 6, 1, 0.005);
            True("on circle" + t, CapMath.OnCircle(route[0], lane, p));
            // intercept lead point: an eastbound raid 40 nm east flying west; ip is on the line to the target
            var bird = new GeoPt(lat, 10.0);
            var tgtPos = AswMath.Offset(bird, 90, 40);
            var c = new CapContact { Id = 1, Lat = tgtPos.Lat, Lon = tgtPos.Lon, Hostile = true, CourseDeg = 270, SpeedKn = 300 };
            GeoPt ip; bool lead;
            double eta = CapInterceptMath.EtaS(bird, 600, c, out ip, out lead);
            True("lead" + t, lead);
            Near("eta" + t, eta, 40.0 / 900.0 * 3600.0, 40.0 / 900.0 * 3600.0 * 0.02);
            Near("ip dist" + t, AswMath.GreatCircleNm(bird, ip) / (40.0 * 600 / 900.0), 1, 0.02);
            // alert raid distance to the east
            var rl = CapAlertMath.Raids(new List<CapContact> { c }, AlertTargets.Air, 0, bird);
            True("raid" + t, rl.Count == 1);
            if (rl.Count == 1) Near("raid nm" + t, rl[0].Nm / AswMath.GreatCircleNm(bird, tgtPos), 1, 0.015);
        }

        private static void Flat(double lat)
        {
            string t = " @" + lat + " flat";
            var origin = new GeoPt(lat, 10.0);
            var cmd = new CapOrderCmd { Polar = true, Bearing = 0, DistNm = 0, SizeNm = 40, WNm = 20 };
            double[] r = CapCommands.PolarRect(origin, cmd);
            GeoPt ctr = AswMath.Offset(origin, 0, 0);
            double w = 40 / 120.0, h = 20 / 120.0;
            True("flat polar bits" + t, r[0] == ctr.Lat - h && r[1] == ctr.Lon - w && r[2] == ctr.Lat + h && r[3] == ctr.Lon + w);
            var z = new CapZone { Rect = r };
            var east = new GeoPt((r[0] + r[2]) / 2, r[3] + 5 / 60.0);
            Near("flat dist" + t, CapMath.DistToZoneNm(east, z), 5, 1e-9);
            True("flat dist bits" + t, CapMath.DistToZoneNm(east, z) == Math.Sqrt(0.0 * 0.0 + (Math.Max(Math.Max(r[1] - east.Lon, 0), east.Lon - r[3]) * 60.0) * (Math.Max(Math.Max(r[1] - east.Lon, 0), east.Lon - r[3]) * 60.0)));
        }
    }
}
