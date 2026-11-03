using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge.Ui
{
    // T-170 / ADR-0070: the geometric reference line of a large ground object (no Unity, no game, no randomness).
    // The python twin is tools/bench/seapower-damage-model/estimate.py geo_reference; the order of operations is the same on purpose
    // (the result must match exactly, tests/test_sea_power_hit_estimate_installations.py).

    /// <summary>One [coll_*] Box / Sphere of a large object in the object's own frame, metres. A sphere is treated as a box with its Scale (as estimate.py).</summary>
    internal sealed class HeBox
    {
        /// <summary>Metres per Unity unit as the game code has it: 1 / 0.0148809375 (not the literal 67.2).</summary>
        public const double GeoU2M = 1.0 / 0.0148809375;

        public string Name;
        public double Cx, Cy, Cz;     // Position * GeoU2M
        public double Hx, Hy, Hz;     // Scale * GeoU2M / 2
        public double Yaw;            // Rotation.y, radians (pitch / roll are ignored, as estimate.py)
        public bool Sphere;
        public double Sx = 1, Sy = 1, Sz = 1;   // the raw ini Scale (Unity units): the size text

        public static HeBox Make(string name, bool sphere, double px, double py, double pz, double ryDeg, double sx, double sy, double sz)
        {
            return new HeBox
            {
                Name = name, Sphere = sphere, Sx = sx, Sy = sy, Sz = sz,
                Cx = px * GeoU2M, Cy = py * GeoU2M, Cz = pz * GeoU2M,
                Hx = sx * GeoU2M / 2, Hy = sy * GeoU2M / 2, Hz = sz * GeoU2M / 2,
                Yaw = ryDeg * (Math.PI / 180.0),
            };
        }

        /// <summary>Three comma-separated numbers ("1.5,0,-6.2"); false when the text does not have exactly three.</summary>
        public static bool TryVec3(string text, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (text == null) return false;
            var p = text.Split(',');
            if (p.Length != 3) return false;
            var inv = CultureInfo.InvariantCulture;
            return double.TryParse(p[0].Trim(), NumberStyles.Float, inv, out x) && double.TryParse(p[1].Trim(), NumberStyles.Float, inv, out y)
                && double.TryParse(p[2].Trim(), NumberStyles.Float, inv, out z);
        }

        private void Local(double px, double py, double pz, out double qx, out double qy, out double qz)
        {
            double dx = px - Cx, dy = py - Cy, dz = pz - Cz;
            double c = Math.Cos(Yaw), s = Math.Sin(Yaw);
            qx = c * dx - s * dz;
            qy = dy;
            qz = s * dx + c * dz;
        }

        /// <summary>Distance from the point to the box surface, 0 inside.</summary>
        public double Dist(double px, double py, double pz)
        {
            double qx, qy, qz;
            Local(px, py, pz, out qx, out qy, out qz);
            double a = Math.Max(Math.Abs(qx) - Hx, 0.0), b = Math.Max(Math.Abs(qy) - Hy, 0.0), c = Math.Max(Math.Abs(qz) - Hz, 0.0);
            return Math.Sqrt(a * a + b * b + c * c);
        }

        /// <summary>Distance along the ray point -> box centre to the box surface (Blastzone raycasts towards the collider position, length 1.5 R);
        /// null when the point is inside or the surface is farther than maxLen.</summary>
        public double? RayEntry(double px, double py, double pz, double maxLen)
        {
            double qx, qy, qz;
            Local(px, py, pz, out qx, out qy, out qz);
            if (Math.Abs(qx) <= Hx && Math.Abs(qy) <= Hy && Math.Abs(qz) <= Hz) return null;
            double len = Math.Sqrt(qx * qx + qy * qy + qz * qz);
            if (len == 0) len = 1e-9;
            double[] q = { qx, qy, qz }, h = { Hx, Hy, Hz }, d = new double[3];
            for (int i = 0; i < 3; i++) d[i] = -q[i] / len;
            double t0 = 0.0, t1 = len;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(d[i]) < 1e-12)
                {
                    if (Math.Abs(q[i]) > h[i]) return null;
                }
                else
                {
                    double a = (-h[i] - q[i]) / d[i], b = (h[i] - q[i]) / d[i];
                    if (a > b) { double t = a; a = b; b = t; }
                    t0 = Math.Max(t0, a);
                    t1 = Math.Min(t1, b);
                    if (t0 > t1) return null;
                }
            }
            return t0 <= maxLen ? (double?)t0 : null;
        }
    }

    /// <summary>One weapon of a fixture row: file, bonus, D, R and the expected reference (null = none).</summary>
    internal sealed class HeGeoWeapon { public string File; public double Bonus, D, R; public int? N; }

    internal static class HitEstimateGeo
    {
        public const int GeoCap = 200;

        /// <summary>Box checks of the last <see cref="GeoReference"/> run and the number of runs (selftest hooks).</summary>
        internal static int LastChecks, Runs;

        /// <summary>Blastzone splash severity at distance m along the ray for the radius r: 1 / (4 pi (n17 m + n18)^2), never below 0.1.</summary>
        public static double SevGame(double m, double r)
        {
            double n17 = (1 - Math.Sqrt(0.1)) / (2 * r * Math.Sqrt(0.1 * Math.PI));
            double n18 = Math.Sqrt(1 / (4 * Math.PI));
            double t = n17 * m + n18;
            double t2 = t * t;
            return Math.Max(1 / (Math.PI * 4 * t2), 0.1);
        }

        /// <summary>Sets SizeAM / SizeBM of a system: the two largest axes of its collider with the largest area of those; -1 = no box (ADR-0065 item 10).</summary>
        public static void FillSize(HeSystem s)
        {
            s.SizeAM = s.SizeBM = -1;
            double best = -1;
            foreach (var b in s.Boxes)
            {
                double x, y;
                HitEstimateMath.ColliderSizeM(b.Sx, b.Sy, b.Sz, out x, out y);
                if (x * y > best) { best = x * y; s.SizeAM = x; s.SizeBM = y; }
            }
        }

        private static bool Aimable(HeSystem s) { return s.Integrity >= 1 && s.Boxes.Count > 0; }

        private static bool ArmorNone(HeSystem s) { return string.Equals((s.Armor ?? "none").Trim(), "none", StringComparison.OrdinalIgnoreCase); }

        /// <summary>ADR-0070 item 4: bombs to destroy the object with perfect aim, every hit = max(0.5, sev * 0.85 * d) per (system, collider inside r).
        /// null: nothing left to aim at while the object lives / more than GeoCap bombs / d or r &lt;= 0 / an armoured target.</summary>
        public static int? GeoReference(HeClass c, double d, double r)
        {
            LastChecks = 0;
            Runs++;
            if (c == null || !(d > 0) || !(r > 0)) return null;
            double dp = HitEstimateMath.ClassDp(c);
            double lim = 1.5 * dp;
            var all = new List<HeSystem>();
            foreach (var s in c.Systems) if (Aimable(s)) all.Add(s);
            var tgts = new List<HeSystem>();
            foreach (var s in all) if (s.Integrity <= lim) tgts.Add(s);
            if (tgts.Count == 0) tgts = all;
            if (tgts.Count == 0) return null;
            for (int i = 1; i < tgts.Count; i++)   // stable: Integrity descending, section order on ties
            {
                var x = tgts[i];
                int j = i - 1;
                while (j >= 0 && tgts[j].Integrity < x.Integrity) { tgts[j + 1] = tgts[j]; j--; }
                tgts[j + 1] = x;
            }
            var aimX = new double[tgts.Count];
            var aimZ = new double[tgts.Count];
            for (int i = 0; i < tgts.Count; i++)
            {
                var cs = new List<HeBox>(tgts[i].Boxes);
                for (int a = 1; a < cs.Count; a++)   // stable: area X * Z descending
                {
                    var x = cs[a];
                    int b = a - 1;
                    while (b >= 0 && cs[b].Hx * cs[b].Hz < x.Hx * x.Hz) { cs[b + 1] = cs[b]; b--; }
                    cs[b + 1] = x;
                }
                if (!ArmorNone(tgts[i])) return null;
                var big = cs[0];
                var small = cs[cs.Count - 1];
                var pick = cs.Count > 1 && big.Hx * big.Hz > 4 * small.Hx * small.Hz ? small : big;
                aimX[i] = pick.Cx;
                aimZ[i] = pick.Cz;
            }
            int ns = c.Systems.Count;
            var left = new double[ns];
            var idx = new Dictionary<HeSystem, int>();
            for (int i = 0; i < ns; i++) { left[i] = c.Systems[i].Integrity; idx[c.Systems[i]] = i; }
            double mean = 0.85 * d;
            for (int n = 1; n <= GeoCap; n++)
            {
                int at = -1;
                for (int i = 0; i < tgts.Count; i++)
                {
                    int k = idx[tgts[i]];
                    if (left[k] > 0.01 * tgts[i].Integrity) { at = i; break; }
                }
                if (at < 0) return null;
                double px = aimX[at], pz = aimZ[at];
                for (int k = 0; k < ns; k++)
                {
                    var sys = c.Systems[k];
                    if (sys.Integrity < 1 || left[k] <= 0.01 * sys.Integrity) continue;
                    foreach (var box in sys.Boxes)
                    {
                        LastChecks++;
                        if (box.Dist(px, 0.0, pz) >= r) continue;
                        double? m = box.RayEntry(px, 0.0, pz, 1.5 * r);
                        double sev = m == null ? 1.0 : SevGame(m.Value, r);
                        left[k] -= Math.Max(0.5, sev * mean);
                        if (left[k] <= 0.01 * sys.Integrity) break;
                    }
                }
                double dead = 0;
                for (int k = 0; k < ns; k++)
                    if (left[k] <= 0.01 * c.Systems[k].Integrity) dead += c.Systems[k].Integrity - left[k];
                if (dead > 0.99 * dp) return n;
            }
            return null;
        }

        /// <summary>Lazily computes and stores in c.GeoRefCache (key file|bonus F3); d = DamageLarge, r = RadiusM; null = no reference.</summary>
        public static int? RefCached(HeClass c, HeAmmo a, double bonus)
        {
            if (c == null || a == null) return null;
            string key = a.File + "|" + bonus.ToString("F3", CultureInfo.InvariantCulture);
            int v;
            if (!c.GeoRefCache.TryGetValue(key, out v))
            {
                var g = GeoReference(c, HitEstimateMath.DamageLarge(a, c.LandSubType, bonus), a.RadiusM);
                v = g.HasValue ? g.Value : -1;
                c.GeoRefCache[key] = v;
            }
            return v >= 0 ? (int?)v : null;
        }

        // ---------------------------------------------------------------- fixture

        /// <summary>Rows (also read by tests/test_sea_power_hit_estimate_installations.py), geometry = the raw ini strings (Unity units, degrees):
        /// "class|sub|DP|sys:I:armor:coll1+coll2;...|coll:Box/Sphere:px,py,pz:ry:sx,sy,sz;...|weapon:bonus:D:R:N;..." (N "-" = no reference).
        /// Systems in ini section order; N is the python geo_reference.</summary>
        internal static readonly string[] GeoRef =
        {
            "is_airbase_keflavik|Airbase|600|FlightDeck:0.1:none:;HitZone1:450:none:coll_runway;HitZone3:300:none:coll_hangars_1;HitZone4:300:none:coll_hangars_2;HitZone5:250:none:coll_hangar_big;HitZone6:50:none:coll_control_tower;HitZone7:50:none:coll_area_1;HitZone8:50:none:coll_area_2;HitZone9:50:none:coll_area_3|coll_runway:Box:1.75,0,-6.24:0:40,0.01,1;coll_road:Box:1.75,0,-3.89:0:38,0.05,0.4;coll_hangars_1:Box:-8.189,0.129,-2.753:0:4.8,0.3,0.8;coll_hangars_2:Box:-3.4,0.129,-2.75:0:4.8,0.3,0.8;coll_hangar_big:Box:5.962,0.096,-2.376:0:0.8,0.2,0.7;coll_control_tower:Box:3.384,0.217,-2.982:0:0.25,0.45,0.3;coll_area_1:Box:0.44,0.096,0.313:0:5,0.2,1;coll_area_2:Box:13.116,0.096,1.195:44.49999:2.8,0.2,1.3;coll_area_3:Box:0.44,0.096,0.313:0:5,0.2,1|usn_mk-84:0.25:85.25:60.0:12;usn_mk-82:0.25:38.5:20.0:24",
            "port_eastcoast_norfolk|Port|410|HitZone1:60:none:coll_port_small_fueltanks_1;HitZone2:60:none:coll_port_small_warehouses_1;HitZone3:60:none:coll_mil_pt_1_buildings_2;HitZone4:60:none:coll_mil_pt_2_buildings_3 (1);HitZone5:60:none:coll_mil_pt_2_buildings_7;HitZone6:60:none:coll_mil_pt_2_buildings_8 (1);HitZone7:60:none:coll_port_big__buildings_4|coll_mil_pt_1_buildings_5:Box:9.179428,0.3,-0.7970314:-178:0.9886706,0.4054683,2.760545;coll_mil_pt_1_buildings_4:Box:11.18334,0.197,3.062386:-178:0.7989258,0.1930801,2.536245;coll_mil_pt_1_buildings_3:Box:4.971474,0.181,-1.982899:-178:0.7519301,0.1609001,0.8856377;coll_mil_pt_1_buildings_2:Box:11.70158,0.258,8.24646:-178:2.346316,0.3180728,1.45784;coll_mil_pt_1_buildings_1:Box:11.21232,0.2,6.671574:-178:1.456982,0.2011686,1.454036;coll_mil_pt_1_pier_9:Box:4.758034,-0.397,7.979622:154.4556:1.17665,1,2.299019;coll_mil_pt_1_pier_8:Box:4.955894,-0.397,3.358902:-178:0.6254969,1,2.791829;coll_mil_pt_1_pier_7:Box:5.084221,-0.397,7.033661:-178:0.6254969,1,2.538027;coll_mil_pt_1_pier_6:Box:5.737,-0.397,7.646255:-178:0.6254969,1,1.180477;coll_mil_pt_1_pier_5:Box:7.171051,-0.397,7.27898:-178:0.6254969,1,1.835533;coll_mil_pt_1_pier_4:Box:8.697121,-0.397,7.225689:-178:0.6254969,1,1.835533;coll_mil_pt_1_pier_3:Box:6.906609,-0.397,-0.322422:-178:0.6254969,1,3.512982;coll_mil_pt_1_pier_2:Box:8.498634,-0.397,-0.3780174:-178:0.7698423,1,3.512982;coll_mil_pt_1_pier_1:Box:9.797844,-0.397,-0.4233856:-178:0.6220949,1,3.512982;coll_mil_pt_1_main_4:Box:5.188362,-0.397,0.01575089:-178:1.34507,1,3.903881;coll_mil_pt_1_main_3:Box:7.626595,-0.397,8.747978:-178:5.626436,1,1.139819;coll_mil_pt_1_main_2:Box:7.232956,-0.397,-2.553169:-178:5.626436,1,1.060297;coll_mil_pt_1_main:Box:11.47747,-0.397,2.975067:-178:2.6,1,12.338;coll_mil_pt_1_buildings_5 (1):Box:47.18887,0.3,65.23382:-76.8001:0.9886705,0.4054683,2.760545;coll_mil_pt_1_buildings_4 (1):Box:50.58556,0.197,62.51844:-76.8001:0.7989257,0.1930801,2.536245;coll_mil_pt_1_buildings_3 (1):Box:46.84291,0.181,69.59196:-76.8001:0.7519301,0.1609001,0.8856376;coll_mil_pt_1_buildings_2 (1):Box:55.57024,0.258,61.00315:-76.8001:2.346316,0.3180728,1.45784;coll_mil_pt_1_buildings_1 (1):Box:54.12038,0.2,61.78899:-76.8001:1.456982,0.2011686,1.454036;coll_mil_pt_1_pier_9 (1):Box:56.65715,-0.397,67.86629:-104.3445:1.17665,1,2.299019;coll_mil_pt_1_pier_8 (1):Box:52.08601,-0.397,68.56969:-76.8001:0.6254968,1,2.791829;coll_mil_pt_1_pier_7 (1):Box:55.66586,-0.397,67.73005:-76.8001:0.6254968,1,2.538026;coll_mil_pt_1_pier_6 (1):Box:56.13999,-0.397,66.97072:-76.8001:0.6254968,1,1.180477;coll_mil_pt_1_pier_5 (1):Box:55.50117,-0.397,65.63531:-76.8001:0.6254968,1,1.835533;coll_mil_pt_1_pier_4 (1):Box:55.15248,-0.397,64.14866:-76.8001:0.6254968,1,1.835533;coll_mil_pt_1_pier_3 (1):Box:48.0959,-0.397,67.37117:-76.8001:0.6254968,1,3.512982;coll_mil_pt_1_pier_2 (1):Box:47.73214,-0.397,65.82026:-76.8001:0.7698422,1,3.512982;coll_mil_pt_1_pier_1 (1):Box:47.43528,-0.397,64.5546:-76.8001:0.6220948,1,3.512982;coll_mil_pt_1_main_4 (1):Box:48.76137,-0.397,68.991:-76.8001:1.34507,1,3.903881;coll_mil_pt_1_main_3 (1):Box:56.85371,-0.397,64.90311:-76.8001:5.626436,1,1.139819;coll_mil_pt_1_main_2 (1):Box:45.84425,-0.397,67.48433:-76.8001:5.626436,1,1.060297;coll_mil_pt_1_main (1):Box:50.44277,-0.397,62.24688:-76.8001:2.6,1,12.338;coll_mil_pt_2_main_1:Box:12.76011,-0.403,43.70034:-175.6802:2.8,1,5.967501;coll_mil_pt_2_main_2:Box:12.7033,-0.403,37.50502:-175.6802:1.99809,1,6.415063;coll_mil_pt_2_main_2b:Box:11.25544,-0.403,36.10109:-175.6802:0.8438808,1,3.41101;coll_mil_pt_2_main_3:Box:13.62908,-0.403,49.09722:-175.6802:1.898185,1,5.051863;coll_mil_pt_2_pier_1:Box:10.40887,-0.403,51.45948:-175.6802:4.898801,1,0.8067193;coll_mil_pt_2_pier_2:Box:9.255104,-0.403,46.68683:-175.6802:4.898801,1,0.5041995;coll_mil_pt_2_pier_3:Box:9.347519,-0.403,41.23237:-175.6802:3.761763,1,0.5041995;coll_mil_pt_2_pier_4:Box:9.065811,-0.403,37.503:-175.6802:3.761763,1,0.9075592;coll_mil_pt_2_pier_5:Box:8.953659,-0.403,36.01823:-175.6802:3.761763,1,0.5672244;coll_mil_pt_2_pier_6:Box:8.862518,-0.403,34.81167:-175.6802:3.761763,1,0.4750505;coll_mil_pt_2_pier_7:Box:7.709724,-0.403,40.68618:-175.6802:0.5501578,1,3.195457;coll_mil_pt_2_pier_8:Box:7.49852,-0.403,37.89014:-175.6802:0.5501578,1,1.048509;coll_mil_pt_2_pier_9:Box:8.020416,-0.403,44.81251:-175.6802:0.5501578,1,3.714719;coll_mil_pt_2_pier_10:Box:6.917,-0.403,46.41517:-170.6492:0.3859632,1,3.857734;coll_mil_pt_2_pier_11:Box:7.793861,-0.403,51.31905:-170.6492:0.4805979,1,3.469222;coll_mil_pt_2_buildings_1:Box:12.88058,0.269,39.20148:-175.6802:1.824561,0.3514903,2.93049;coll_mil_pt_2_buildings_2:Box:12.93111,0.197,46.17649:94.3199:0.7989259,0.1930801,2.536245;coll_mil_pt_2_buildings_3:Box:11.51084,0.178,35.20731:94.3199:0.7519302,0.1609001,0.8856378;coll_mil_pt_2_buildings_4:Box:13.44984,0.258,48.15805:-85.6804:2.346316,0.3180728,1.45784;coll_mil_pt_2_buildings_5:Box:13.6259,0.2,50.46233:-175.6802:1.456982,0.2011686,1.454035;coll_mil_pt_2_buildings_6:Box:9.554718,0.145,35.96982:-175.6802:1.290795,0.09241184,0.4884651;coll_mil_pt_2_buildings_7:Box:9.811134,0.145,37.62521:-175.6802:1.290795,0.09241184,0.4884651;coll_mil_pt_2_buildings_8:Box:8.439041,0.145,37.72884:-175.6802:1.290795,0.09241184,0.4884651;coll_mil_pt_2_main_1 (1):Box:11.80524,-0.403,20.80455:-178:2.8,1,5.967501;coll_mil_pt_2_main_2 (1):Box:11.99924,-0.403,14.61201:-178:1.99809,1,6.415063;coll_mil_pt_2_main_2b (1):Box:10.60938,-0.403,13.15062:-178:0.8438808,1,3.41101;coll_mil_pt_2_main_3 (1):Box:12.45506,-0.403,26.23218:-178:1.898185,1,5.051863;coll_mil_pt_2_pier_1 (1):Box:9.141861,-0.403,28.46217:-178:4.898802,1,0.8067194;coll_mil_pt_2_pier_2 (1):Box:8.182213,-0.403,23.64672:-178:4.898802,1,0.5041995;coll_mil_pt_2_pier_3 (1):Box:8.495338,-0.403,18.20048:-178:3.761763,1,0.5041995;coll_mil_pt_2_pier_4 (1):Box:8.364815,-0.403,14.46275:-178:3.761763,1,0.9075592;coll_mil_pt_2_pier_5 (1):Box:8.312843,-0.403,12.97466:-178:3.761763,1,0.5672245;coll_mil_pt_2_pier_6 (1):Box:8.270615,-0.403,11.7654:-178:3.761763,1,0.4750505;coll_mil_pt_2_pier_7 (1):Box:6.880989,-0.403,17.58844:-178:0.5501579,1,3.195457;coll_mil_pt_2_pier_8 (1):Box:6.783134,-0.403,14.78615:-178:0.5501579,1,1.048509;coll_mil_pt_2_pier_9 (1):Box:7.024406,-0.403,21.72396:-178:0.5501579,1,3.714719;coll_mil_pt_2_pier_10 (1):Box:5.857025,-0.403,23.28065:-172.9689:0.3859634,1,3.857736;coll_mil_pt_2_pier_11 (1):Box:6.534676,-0.403,28.216:-172.9689:0.4805981,1,3.469223;coll_mil_pt_2_buildings_1 (1):Box:12.10771,0.269,16.31425:-178:1.824561,0.3514903,2.930491;coll_mil_pt_2_buildings_2 (1):Box:11.87586,0.197,23.28559:92.0002:0.7989261,0.1930801,2.536246;coll_mil_pt_2_buildings_3 (1):Box:10.90076,0.178,12.26791:92.0002:0.7519304,0.1609001,0.8856379;coll_mil_pt_2_buildings_4 (1):Box:12.31396,0.258,25.28652:-88.0002:2.346316,0.3180728,1.45784;coll_mil_pt_2_buildings_5 (1):Box:12.39661,0.2,27.59605:-178:1.456982,0.2011686,1.454036;coll_mil_pt_2_buildings_6 (1):Box:8.915375,0.145,12.95061:-178:1.290795,0.09241184,0.4884651;coll_mil_pt_2_buildings_7 (1):Box:9.104576,0.145,14.61503:-178:1.290795,0.09241184,0.4884651;coll_mil_pt_2_buildings_8 (1):Box:7.729416,0.145,14.66305:-178:1.290795,0.09241184,0.4884651;coll_mil_pt_3_main:Box:70.1516,-0.394,54.50253:-71.6227:3.758907,1,7.4875;coll_mil_pt_3_main_2:Box:75.32276,-0.394,51.77615:-71.6227:1.852436,1,4.052493;coll_mil_pt_3_main_3:Box:78.41174,-0.394,50.46438:-71.6227:1.286864,1,2.688784;coll_mil_pt_3_main_4:Box:72.44127,-0.394,57.77557:-71.6227:3.886456,1,2.352686;coll_mil_pt_3_main_5:Box:69.55466,-0.394,57.54278:-71.6227:1.934119,1,3.029083;coll_mil_pt_3_main_6:Box:65.44011,-0.394,55.13625:-71.6227:1.934119,1,2.196085;coll_mil_pt_3_main_7:Box:66.90146,-0.394,56.96267:-125.8542:1.341621,1,4.901882;coll_mil_pt_3_main_8:Box:76.56322,-0.394,56.28396:-71.6227:1.146195,1,3.495275;coll_mil_pt_3_main_9:Box:75.99265,-0.394,57.9477:-71.6227:2.034496,1,1.572874;coll_mil_pt_3_main_10:Box:77.58461,-0.394,56.93832:-116.6226:3.563213,1,0.5997053;coll_mil_pt_3_main_11:Box:76.69286,-0.394,57.24511:-71.6227:1.144404,1,1.317282;coll_mil_pt_3_pier_1:Box:74.69434,-0.394,57.20199:-71.6227:4.260908,1,0.6227267;coll_mil_pt_3_pier_2:Box:77.51441,-0.394,52.84467:-71.6227:1.627675,1,0.5371017;coll_mil_pt_3_pier_3:Box:78.29472,-0.394,55.19345:-71.6227:1.220756,1,0.5371017;coll_mil_pt_3_buildings_1:Box:75.12254,0.269,51.83318:-71.6227:1.824561,0.3514903,2.93049;coll_mil_pt_3_buildings_2:Box:76.3353,0.197,56.20689:-71.6227:0.7989258,0.1930801,2.536245;coll_mil_pt_3_buildings_3:Box:75.41971,0.178,58.51633:-161.6225:0.7519301,0.1609001,0.8856375;coll_mil_pt_3_buildings_4:Box:71.85361,0.258,53.1647:18.3772:2.346316,0.3180728,1.45784;coll_mil_pt_3_buildings_5:Box:72.7294,0.2,54.58607:-71.6227:1.456982,0.2011686,1.454035;coll_mil_pt_3_buildings_6:Box:71.88915,0.145,58.70505:-71.6227:1.290795,0.09241184,0.4884651;coll_mil_pt_3_buildings_7:Box:68.22221,0.145,58.03602:-36.6227:1.290795,0.09241184,0.488465;coll_mil_pt_3_buildings_8:Box:67.1134,0.145,57.21682:-36.6227:1.290795,0.09241184,0.488465;coll_mil_pt_3_buildings_9:Box:68.06028,0.197,55.22996:-126.6225:0.7989258,0.1930801,2.536244;coll_mil_pt_4_buildings_11:Box:15.25159,0.181,-32.5158:137.9379:1.45315,0.164031,0.5254907;coll_mil_pt_4_buildings_10:Box:12.299,0.181,-26.72021:152.9324:1.45315,0.164031,0.5254908;coll_mil_pt_4_buildings_9:Box:12.6553,0.145,-29.77642:147.843:1.290795,0.09241184,0.488465;coll_mil_pt_4_buildings_8:Box:13.13849,0.145,-31.03538:147.843:1.290795,0.09241184,0.488465;coll_mil_pt_4_buildings_7:Box:14.29493,0.145,-30.30834:147.843:1.290795,0.09241184,0.488465;coll_mil_pt_4_buildings_6:Box:16.55738,0.145,-33.2269:137.9778:1.290795,0.09241184,0.488465;coll_mil_pt_4_buildings_4:Box:17.87101,0.258,-31.53049:-122.1571:2.346317,0.3180728,1.45784;coll_mil_pt_4_buildings_3:Box:17.48542,0.178,-34.7519:37.8514:0.7519304,0.1609001,0.8856378;coll_mil_pt_4_buildings_2:Box:19.47684,0.197,-33.47039:147.843:0.7989258,0.1930801,2.536244;coll_mil_pt_4_buildings_1:Box:16.50175,0.269,-29.62494:147.843:1.756139,0.3514903,0.9890404;coll_mil_pt_4_pier_6:Box:12.65664,-0.396,-27.88092:153.1565:2.242981,1,0.6500332;coll_mil_pt_4_pier_5:Box:12.32249,-0.396,-26.70308:153.1565:1.778168,1,0.7672997;coll_mil_pt_4_pier_4:Box:11.8586,-0.396,-25.61388:153.1565:1.227932,1,0.605508;coll_mil_pt_4_pier_3:Box:13.31344,-0.396,-27.88851:147.843:1.264472,1,0.4300732;coll_mil_pt_4_pier_2:Box:11.44917,-0.396,-24.30502:147.843:0.7330934,1,1.109866;coll_mil_pt_4_pier_1:Box:13.14615,-0.396,-23.23343:147.843:1.149951,1,1.109866;coll_mil_pt_4_main_12:Box:19.59957,-0.396,-34.82486:73.3023:1.242709,1,0.7882491;coll_mil_pt_4_main_11:Box:18.34663,-0.396,-35.1767:73.3023:1.242709,1,0.7882491;coll_mil_pt_4_main_10:Box:18.18784,-0.396,-35.29425:127.6197:0.949747,1,0.7225016;coll_mil_pt_4_main_9:Box:20.02144,-0.396,-34.49821:147.843:1.524077,1,1.733034;coll_mil_pt_4_main_8:Box:16.81589,-0.396,-31.93396:136.6215:0.5806009,1,0.5267581;coll_mil_pt_4_main_7:Box:17.55828,-0.396,-33.60521:127.6197:0.5806009,1,0.5267582;coll_mil_pt_4_main_6:Box:17.80986,-0.396,-34.58219:127.6197:1.670341,1,1.167461;coll_mil_pt_4_main_5:Box:16.72549,-0.396,-33.30076:131.013:2.371633,1,1.167461;coll_mil_pt_4_main_4:Box:15.25566,-0.396,-31.92855:137.8441:2.727582,1,1.582161;coll_mil_pt_4_main_3:Box:13.61766,-0.396,-29.85295:147.843:3.372172,1,2.202738;coll_mil_pt_4_main_2:Box:13.52792,-0.396,-25.98068:147.843:1.174078,1,6.232326;coll_mil_pt_4_main:Box:17.59206,-0.396,-31.06801:147.843:2.041875,1,6.745625;coll_port_big__buildings_6:Box:38.0062,0.186,66.92374:-53.2584:1.290795,0.09241184,0.4884652;coll_port_big__buildings_5:Box:32.78896,0.186,61.82851:-8.2584:1.290795,0.09241184,0.4884652;coll_port_big__buildings_4:Box:26.72303,0.213,61.60591:-98.2584:0.5043809,0.1609001,1.461303;coll_port_big__buildings_3:Box:28.31238,0.213,61.83659:-98.2584:0.5043809,0.1609001,1.461303;coll_port_big__buildings_2:Box:39.50291,0.228,63.36482:-8.2584:1.461022,0.1809039,1.45784;coll_port_big__buildings_1:Box:40.94569,0.213,63.93699:81.7416:0.7519306,0.1609001,0.8856379;coll_port_big_pier_3e:Box:11.09759,-0.107,72.92892:-98.2584:0.2658315,0.5103125,2.498522;coll_port_big_pier_3d:Box:9.738373,-0.107,69.6507:-98.2584:5.316629,0.5103125,0.2512735;coll_port_big_pier_3c:Box:10.8548,-0.107,66.15683:-38.2585:0.2106397,0.5103125,2.47025;coll_port_big_pier_3b:Box:16.62792,-0.107,64.57466:-53.2584:0.5804987,0.5103125,2.896054;coll_port_big_pier_3:Box:13.64509,-0.107,65.25628:-98.2584:0.5804985,0.5103125,4.354244;coll_port_big_pier_2b:Box:23.8767,-0.107,67.578:-98.2584:0.2571066,0.5103125,4.095703;coll_port_big_pier_2:Box:25.17142,-0.107,65.80761:-98.2584:4.113706,0.5103125,0.4080845;coll_port_big_pier_1:Box:33.62966,-0.107,68.42164:-98.2584:4.113706,0.5103125,0.2365707;coll_port_big_main_6:Box:40.28085,-0.107,68.36441:-98.2584:1.976188,0.5103125,5.046842;coll_port_big_main_5:Box:38.0977,-0.107,66.41865:-98.2584:1.238804,0.5103125,9.883657;coll_port_big_main_4:Box:19.4966,-0.107,63.71883:-98.2584:1.179814,0.5103125,3.77757;coll_port_big_main_3:Box:27.5384,-0.107,61.32108:-98.2584:1.63863,0.5103125,3.022055;coll_port_big_main_2:Box:21.943,-0.107,61.59521:-98.2584:3.783273,0.5103125,8.058815;coll_port_big_main:Box:36.13876,-0.107,63.62429:-98.2584:3.70875,0.5103125,14.6701;coll_port_small_main:Box:11.02547,-0.28125,-16.58002:-6.3816:4.025,0.7625,7.992361;coll_port_small_main_2:Box:8.232788,-0.28125,-17.9801:-6.3816:1.782949,0.7625,5.816315;coll_port_small_main_3:Box:9.047295,-0.28125,-10.29193:-6.3816:1.537794,0.7625,5.002031;coll_port_small_main_4:Box:11.52286,-0.28125,-10.01505:-6.3816:1.537794,0.7625,5.002031;coll_port_small_warehouses_1:Box:9.027512,0.20875,-11.18567:-6.3816:0.4805606,0.1913399,1.280207;coll_port_small_warehouses_2:Box:9.248924,0.20875,-12.71553:-6.3816:0.4805606,0.1913399,1.280207;coll_port_small_warehouses_3:Box:12.24358,0.17475,-15.73941:-6.3816:0.4805606,0.1183962,1.280207;coll_port_small_fueltanks_1:Box:8.192001,0.27775,-19.30685:-6.3816:0.5075921,0.3653395,1.722279;coll_port_small_fueltanks_2:Box:8.844933,0.27775,-19.23383:-6.3816:0.5075921,0.3653395,1.722279;coll_port_small_fueltanks_3:Box:9.857887,0.24875,-19.64478:-6.3816:0.5964208,0.3151053,0.6321303;coll_port_small_fueltanks_4:Box:10.4033,0.24875,-17.80074:-51.3816:0.5964208,0.3151053,0.6321301;coll_port_small_main (1):Box:90.89357,-0.274,54.45618:-95:4.025,0.7625,7.99236;coll_port_small_main_2 (1):Box:92.22591,-0.274,51.63056:-95:1.782949,0.7625,5.816315;coll_port_small_main_3 (1):Box:84.55962,-0.274,52.6302:-95:1.537794,0.7625,5.002031;coll_port_small_main_4 (1):Box:84.34251,-0.274,55.11172:-95:1.537794,0.7625,5.002031;coll_port_small_warehouses_1 (1):Box:85.45261,0.216,52.58887:-95:0.4805606,0.1913399,1.280207;coll_port_small_warehouses_2 (1):Box:86.98737,0.216,52.77334:-95:0.4805606,0.1913399,1.280207;coll_port_small_warehouses_3 (1):Box:90.08258,0.182,55.69422:-95:0.4805606,0.1183962,1.280207;coll_port_small_fueltanks_1 (1):Box:93.55129,0.285,51.55779:-95:0.5075921,0.3653395,1.722279;coll_port_small_fueltanks_2 (1):Box:93.49403,0.285,52.2123:-95:0.5075921,0.3653395,1.722279;coll_port_small_fueltanks_3 (1):Box:93.92929,0.256,53.21505:-95:0.5964207,0.3151053,0.6321302;coll_port_small_fueltanks_4 (1):Box:92.09893,0.256,53.80476:-140:0.5964206,0.3151053,0.6321301|usn_mk-84:0.25:85.25:60.0:10;usn_mk-82:0.25:38.5:20.0:44",
            "warehouses_3|Installation|350|ControlSystem:600:none:coll_hangar_1+coll_hangar_2+coll_hangar_3+coll_hangar_4+coll_hangar_5+coll_hangar_6+coll_hangar_7+coll_hangar_8+coll_small_warehouse_1+coll_small_warehouse_2+coll_small_warehouse_3+coll_small_warehouse_4+coll_small_warehouse_5+coll_small_warehouse_6+coll_small_warehouse_7+coll_small_warehouse_8+coll_warehouses_1+coll_small_hangar_1+coll_small_hangar_2+coll_storage_1+coll_storage_2+coll_containers;SensorSystem1:20:none:coll_hangar_1;HitZone1:100:none:coll_hangar_1+coll_hangar_2+coll_hangar_3;HitZone2:100:none:coll_hangar_4+coll_hangar_5;HitZone3:80:none:coll_hangar_6+coll_hangar_7+coll_hangar_8;HitZone4:60:none:coll_small_warehouse_1+coll_small_warehouse_2+coll_small_warehouse_3+coll_small_warehouse_4+coll_small_warehouse_5+coll_small_warehouse_6+coll_small_warehouse_7+coll_small_warehouse_8;HitZone5:60:none:coll_warehouses_1;HitZone6:80:none:coll_small_hangar_1+coll_small_hangar_2;HitZone7:60:none:coll_storage_1+coll_storage_2;HitZone8:40:none:coll_containers|coll_hangar_1:Box:1.209,0.102,0.325:0:1.564,0.206,0.8;coll_hangar_2:Box:1.227,0.102,-1.112:0:1.564,0.206,0.8;coll_hangar_3:Box:1.245,0.102,-2.503:0:1.564,0.206,0.8;coll_hangar_4:Box:4.147,0.102,-0.347:0:1.564,0.206,0.8;coll_hangar_5:Box:4.147,0.102,-1.793:0:1.564,0.206,0.8;coll_hangar_6:Box:0.928,0.102,-4.436:0:0.803,0.206,0.7;coll_hangar_7:Box:2.199,0.102,-4.436:0:0.803,0.206,0.7;coll_hangar_8:Box:3.463,0.102,-4.436:0:0.803,0.206,0.7;coll_small_hangar_1:Box:-4.906,0.062,-2.736:9:0.796,0.121,0.414;coll_small_hangar_2:Box:-3.818,0.062,-2.91:9:0.796,0.121,0.414;coll_warehouses_1:Box:-0.82,0.076,-1.114:-90:2.327,0.135,0.406;coll_storage_1:Box:0.725,0.049,2.022:90:1.291,0.098,0.476;coll_storage_2:Box:1.541,0.049,2.022:90:1.291,0.098,0.476;coll_small_warehouse_1:Box:2.875,0.085,5.58:0:1.468,0.171,0.441;coll_small_warehouse_2:Box:2.875,0.085,4.864:0:1.468,0.171,0.441;coll_small_warehouse_3:Box:2.875,0.085,4.148:0:1.468,0.171,0.441;coll_small_warehouse_4:Box:2.875,0.085,3.442:0:1.468,0.171,0.441;coll_small_warehouse_5:Box:4.638,0.085,5.58:0:1.468,0.171,0.441;coll_small_warehouse_6:Box:4.638,0.085,4.864:0:1.468,0.171,0.441;coll_small_warehouse_7:Box:4.638,0.085,4.148:0:1.468,0.171,0.441;coll_small_warehouse_8:Box:4.638,0.085,3.442:0:1.468,0.171,0.441;coll_fueltank_1:Box:0.692,0.182,5.182:90:0.167,0.36,0.17;coll_fueltank_2:Box:0.692,0.182,4.853:90:0.167,0.36,0.17;coll_fueltank_3:Box:0.692,0.182,4.467:90:0.167,0.36,0.17;coll_fueltank_4:Box:-0.6652,0.1449,-4.2762:90:0.25,0.289,0.25;coll_containers:Box:-2.914,0.102,0.103:47:1.75,0.206,2.1;coll_selection_1:Box:0,-0.05,-1.67:0:12,0.01,9;coll_selection_2:Box:3.07,-0.05,4.58:0:5.4,0.01,3.5|usn_mk-84:0.25:85.25:60.0:4;usn_mk-82:0.25:38.5:20.0:11",
            "tgt_fueltanks_medium|Installation|110|ControlSystem:10:minor:coll_control;SensorSystem1:0:none:;HitZone1:15:none:coll_fueltanks_1b;HitZone2:15:none:coll_fueltanks_1c;HitZone3:15:none:coll_fueltanks_7;HitZone4:15:none:coll_fueltanks_3;HitZone5:15:none:coll_fueltanks_4;HitZone6:15:none:coll_fueltanks_5;HitZone7:15:none:coll_fueltanks_6;HitZone8:15:none:coll_control|coll_fueltanks_1a:Box:-0.668,0.248,1.549:0:0.82,0.5173438,0.328125;coll_fueltanks_1b:Box:-0.668,0.248,1.085:0:0.82,0.5173438,0.328125;coll_fueltanks_1c:Box:-0.668,0.248,0.625:0:0.82,0.5173438,0.328125;coll_fueltanks_4:Box:1.020999,0.248,-0.511:0:0.8100001,0.5173438,0.328125;coll_fueltanks_5:Box:1.020999,0.248,-1.142:0:0.8100001,0.5173438,0.328125;coll_fueltanks_6:Box:1.020999,0.248,-1.685:0:0.8100001,0.5173438,0.328125;coll_fueltanks_7:Box:-0.668,0.248,-0.228:0:0.83,0.5173438,0.328125;coll_control:Box:-0.496,0.0743,-1.501:0:0.559,0.1518457,0.6808594|usn_mk-84:0.25:85.25:60.0:-;usn_mk-82:0.25:38.5:20.0:-",
            "civ_tgt_bridge_6_part|Bridge|200|ControlSystem:200:none:coll_deck+coll_deck_2+coll_deck_end_1+coll_deck_end_2+coll_pillar_1+coll_pillar_2+coll_pillar_3+coll_pillar_4+coll_pillar_5+coll_pillar_6+coll_pillar_7;SensorSystem1:0:none:|coll_deck:Box:0,0.29,3.75:0:0.243,0.017,7.578;coll_deck_2:Box:0,0.29,-3.65:0:0.243,0.017,7.578;coll_deck_end_1:Box:0,0.03,8.826:0:0.243,0.017,2.56;coll_deck_end_2:Box:0,0.03,-8.826:0:0.243,0.017,2.56;coll_pillar_1:Box:0,0.117,7.506:0:0.27,0.35,0.162;coll_pillar_2:Box:0,0.117,5.043:0:0.27,0.35,0.162;coll_pillar_3:Box:0,0.117,2.524:0:0.27,0.35,0.162;coll_pillar_4:Box:0,0.117,0.0046:0:0.27,0.35,0.162;coll_pillar_5:Box:0,0.117,-2.5162:0:0.27,0.35,0.162;coll_pillar_6:Box:0,0.117,-5.0345:0:0.27,0.35,0.162;coll_pillar_7:Box:0,0.117,-7.514:0:0.27,0.35,0.162|usn_mk-84:0.25:85.25:60.0:2;usn_mk-82:0.25:38.5:20.0:3",
        };

        public static HeClass ParseRow(string row, out List<HeGeoWeapon> weapons)
        {
            var inv = CultureInfo.InvariantCulture;
            var p = row.Split('|');
            var c = new HeClass { IniName = p[0], Kind = HeTargetKind.Large, LandSubType = p[1], DamagePoints = double.Parse(p[2], inv), ZonesRead = true };
            var boxes = new Dictionary<string, HeBox>();
            foreach (var t in p[4].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = t.Split(':');
                double px, py, pz, sx, sy, sz, ry;
                HeBox.TryVec3(f[2], out px, out py, out pz);
                HeBox.TryVec3(f[4], out sx, out sy, out sz);
                ry = double.Parse(f[3], inv);
                boxes[f[0]] = HeBox.Make(f[0], string.Equals(f[1], "Sphere", StringComparison.OrdinalIgnoreCase), px, py, pz, ry, sx, sy, sz);
            }
            foreach (var t in p[3].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = t.Split(':');
                var s = new HeSystem { Section = f[0], Integrity = double.Parse(f[1], inv), Armor = f[2] };
                var names = f.Length > 3 ? f[3].Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
                s.Colliders = names.Length;
                foreach (var nm in names) { HeBox b; if (boxes.TryGetValue(nm, out b)) s.Boxes.Add(b); }
                FillSize(s);
                c.Systems.Add(s);
            }
            weapons = new List<HeGeoWeapon>();
            foreach (var t in p[5].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = t.Split(':');
                weapons.Add(new HeGeoWeapon { File = f[0], Bonus = double.Parse(f[1], inv), D = double.Parse(f[2], inv), R = double.Parse(f[3], inv), N = f[4] == "-" ? (int?)null : int.Parse(f[4], inv) });
            }
            return c;
        }

        private static HeClass Synth(double dp, params double[] integrities)
        {
            var c = new HeClass { Kind = HeTargetKind.Large, DamagePoints = dp, ZonesRead = true };
            for (int i = 0; i < integrities.Length; i++) c.Systems.Add(new HeSystem { Section = "HitZone" + (i + 1), Integrity = integrities[i], Colliders = 1 });
            return c;
        }

        private static void Put(HeSystem s, double x, double z, double half = 5)
        {
            s.Boxes.Add(new HeBox { Name = "b", Cx = x, Cz = z, Hx = half, Hy = 1, Hz = half });
        }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (ok, m) => { if (!ok && err == null) err = m; };
            var inv = CultureInfo.InvariantCulture;
            double e = 1e-9;

            // severity: 1 at the centre, the lower clamp 0.1 from 1.5 R on
            chk(Math.Abs(SevGame(0, 40) - 1) < e && Math.Abs(SevGame(60, 40) - 0.1) < e && SevGame(10, 40) < 1 && SevGame(10, 40) > SevGame(20, 40), "SevGame");

            // box: rotation by 90 degrees swaps the axes
            var bx = HeBox.Make("t", false, 0, 0, 0, 0, 2 / HeBox.GeoU2M, 2 / HeBox.GeoU2M, 20 / HeBox.GeoU2M);   // 2 x 2 x 20 m
            chk(Math.Abs(bx.Dist(0, 0, 0)) < e && Math.Abs(bx.Dist(0, 0, 15) - 5) < 1e-6 && Math.Abs(bx.Dist(11, 0, 0) - 10) < 1e-6, "box dist");
            var rot = HeBox.Make("t", false, 0, 0, 0, 90, 2 / HeBox.GeoU2M, 2 / HeBox.GeoU2M, 20 / HeBox.GeoU2M);
            chk(Math.Abs(rot.Dist(15, 0, 0) - 5) < 1e-6 && Math.Abs(rot.Dist(0, 0, 11) - 10) < 1e-6, "box dist rotated 90");
            chk(bx.RayEntry(0, 0, 0, 100) == null, "ray inside");
            var ray = bx.RayEntry(0, 0, 25, 100);
            chk(ray.HasValue && Math.Abs(ray.Value - 15) < 1e-6 && bx.RayEntry(0, 0, 25, 10) == null, "ray entry");
            var rayr = rot.RayEntry(25, 0, 0, 100);
            chk(rayr.HasValue && Math.Abs(rayr.Value - 15) < 1e-6, "ray entry rotated");

            // synthetic: one box, the bomb in the centre (sev 1) -> N = ceil(0.99 I / (0.85 D))
            var one = Synth(100, 100);
            Put(one.Systems[0], 0, 0);
            var g = GeoReference(one, 60, 30);
            chk(g.HasValue && g.Value == (int)Math.Ceiling(0.99 * 100 / (0.85 * 60)), "one box " + g);
            // two systems on the same box die in one series
            var two = Synth(200, 100, 100);
            Put(two.Systems[0], 0, 0); Put(two.Systems[1], 0, 0);
            var g2 = GeoReference(two, 60, 30);
            chk(g2.HasValue && g2.Value == g.Value && g2.Value < 2 * g.Value, "shared box " + g2);
            // a zone outside the radius of every aim point of the others still gets its own aim point; unreachable object -> none
            var far = Synth(200, 100, 100);
            Put(far.Systems[0], 0, 0); Put(far.Systems[1], 1000, 0);
            var g3 = GeoReference(far, 60, 30);
            chk(g3.HasValue && g3.Value == 2 * g.Value, "two apart " + g3);
            var nobox = Synth(100, 100);
            chk(GeoReference(nobox, 60, 30) == null, "no box -> none");
            // an armoured target -> none; d / r <= 0 -> none
            var arm = Synth(100, 100);
            Put(arm.Systems[0], 0, 0); arm.Systems[0].Armor = "minor";
            chk(GeoReference(arm, 60, 30) == null, "armour -> none");
            chk(GeoReference(one, 0, 30) == null && GeoReference(one, 60, 0) == null, "d / r <= 0");
            // the cap
            var huge = Synth(1000, 1000);
            Put(huge.Systems[0], 0, 0);
            chk(GeoReference(huge, 0.01, 30) == null, "cap");
            // a system above DP stays out of the targets but takes the damage of the neighbours
            var sel = Synth(100, 100, 1000);
            Put(sel.Systems[0], 0, 0); Put(sel.Systems[1], 0, 0, 1);
            var g4 = GeoReference(sel, 60, 30);
            chk(g4.HasValue && g4.Value == g.Value, "selection system " + g4);

            // cache: the second call does not compute again
            var ammo = new HeAmmo { File = "x", Name = "x", Kind = HeAmmoKind.Bomb, Power = 60 / 1.25 / 1.1, RadiusM = 30 };
            one.LandSubType = "Installation";
            int runs = Runs;
            var r1 = RefCached(one, ammo, 0.25);
            int after1 = Runs;
            var r2 = RefCached(one, ammo, 0.25);
            chk(r1.HasValue && r1 == r2 && after1 == runs + 1 && Runs == after1, "RefCached computes once");
            RefCached(one, ammo, 0.5);
            chk(Runs == after1 + 1, "RefCached key has the bonus");

            // the fixture rows: N from the row, and the iteration budget
            foreach (var row in GeoRef)
            {
                List<HeGeoWeapon> ws;
                var c = ParseRow(row, out ws);
                int boxes = 0;
                foreach (var s in c.Systems) boxes += s.Boxes.Count;
                foreach (var w in ws)
                {
                    var got = GeoReference(c, w.D, w.R);
                    chk(got == w.N, "row " + c.IniName + " " + w.File + ": " + (got.HasValue ? got.Value.ToString(inv) : "-") + " want " + (w.N.HasValue ? w.N.Value.ToString(inv) : "-"));
                    chk(LastChecks <= GeoCap * boxes, "row " + c.IniName + " checks " + LastChecks);
                }
            }
            return err;
        }
    }
}
