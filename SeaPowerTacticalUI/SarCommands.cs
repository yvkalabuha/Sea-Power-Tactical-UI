using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039: parsing of sar_order / sar_ship. Err codes are plain (as in AswCommands); the ack adds "!".
    internal sealed class SarOrderCmd { public string Group, OnComplete = "rtb"; public SarArea Area; }
    internal sealed class SarShipCmd { public int Uid; public string Op; public double RadiusNm; }

    internal static class SarCommands
    {
        // on_complete values of helicopter groups: rtb = go home, loiter = an empty helicopter hovers over its own ship (T-147 stream H, SarHoverOver); with survivors aboard it still flies home.
        private static readonly string[] OnCompleteValues = { "rtb", "loiter" };

        private static double? Num(Dictionary<string, object> c, string k)
        {
            object v;
            if (c != null && c.TryGetValue(k, out v) && v is double) return (double)v;
            return null;
        }

        private static string Str(Dictionary<string, object> c, string k)
        {
            object v;
            return c != null && c.TryGetValue(k, out v) ? v as string : null;
        }

        private static bool GeoOk(double lat, double lon) { return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180; }

        private static double[] RectOf(double lat0, double lon0, double lat1, double lon1)
        {
            return new[] { Math.Min(lat0, lat1), Math.Min(lon0, lon1), Math.Max(lat0, lat1), Math.Max(lon0, lon1) };
        }

        /// <summary>
        /// c: {group, area?, on_complete?, origin?:{lat,lon}}. area: "all" (default) | rect [lat0,lon0,lat1,lon1] (or 4 corners = 8 numbers)
        /// | {bearing, dist_nm, size_nm?, origin?:{lat,lon}} | {lat, lon, radius_nm}. The origin of a bearing area is the target ship; the caller
        /// passes it as origin (area.origin or c.origin); without it err = "no_origin".
        /// </summary>
        public static SarOrderCmd ParseOrder(Dictionary<string, object> c, out string err)
        {
            err = null;
            var r = new SarOrderCmd { Group = Str(c, "group"), Area = new SarArea() };
            if (string.IsNullOrEmpty(r.Group)) { err = "missing_group"; return null; }
            object o;
            if (c.TryGetValue("on_complete", out o))
            {
                string oc = o as string;
                if (oc == null || Array.IndexOf(OnCompleteValues, oc) < 0) { err = "bad_on_complete"; return null; }
                r.OnComplete = oc;
            }
            if (!c.TryGetValue("area", out o) || o == null) return r;
            string s = o as string;
            if (s != null)
            {
                if (s == "all") return r;
                err = "bad_area"; return null;
            }
            var l = o as List<object>;
            if (l != null)
            {
                var v = new List<double>();
                foreach (object x in l) { if (!(x is double)) { err = "bad_area"; return null; } v.Add((double)x); }
                if (v.Count == 4) r.Area.Rect = RectOf(v[0], v[1], v[2], v[3]);
                else if (v.Count == 8)
                {
                    double la0 = double.MaxValue, lo0 = double.MaxValue, la1 = double.MinValue, lo1 = double.MinValue;
                    for (int i = 0; i < 8; i += 2)
                    {
                        la0 = Math.Min(la0, v[i]); la1 = Math.Max(la1, v[i]);
                        lo0 = Math.Min(lo0, v[i + 1]); lo1 = Math.Max(lo1, v[i + 1]);
                    }
                    r.Area.Rect = new[] { la0, lo0, la1, lo1 };
                }
                else { err = "bad_area"; return null; }
                if (!GeoOk(r.Area.Rect[0], r.Area.Rect[1]) || !GeoOk(r.Area.Rect[2], r.Area.Rect[3])) { err = "bad_area"; return null; }
                r.Area.Kind = "rect";
                return r;
            }
            var a = o as Dictionary<string, object>;
            if (a == null) { err = "bad_area"; return null; }
            double? rad = Num(a, "radius_nm"), lat = Num(a, "lat"), lon = Num(a, "lon");
            if (rad.HasValue || lat.HasValue || lon.HasValue)
            {
                if (!rad.HasValue || !lat.HasValue || !lon.HasValue || rad.Value <= 0 || rad.Value > 500 || !GeoOk(lat.Value, lon.Value)) { err = "bad_area"; return null; }
                r.Area.Kind = "circle"; r.Area.Center = new GeoPt(lat.Value, lon.Value); r.Area.RadiusNm = rad.Value;
                return r;
            }
            double? b = Num(a, "bearing"), d = Num(a, "dist_nm"), sz = Num(a, "size_nm");
            if (!b.HasValue || !d.HasValue || d.Value <= 0 || d.Value > 200 || (sz.HasValue && (sz.Value <= 0 || sz.Value > 100))) { err = "bad_area"; return null; }
            var org = a.ContainsKey("origin") ? a["origin"] : (c.ContainsKey("origin") ? c["origin"] : null);
            var od = org as Dictionary<string, object>;
            double? olat = Num(od, "lat"), olon = Num(od, "lon");
            if (!olat.HasValue || !olon.HasValue || !GeoOk(olat.Value, olon.Value)) { err = "no_origin"; return null; }
            double size = sz ?? 10;
            var ctr = AswMath.Offset(new GeoPt(olat.Value, olon.Value), AswMath.Norm360(b.Value), d.Value);
            double half = size / 2.0 / 60.0;
            double halfLon = size / 2.0 / (60.0 * AswMath.LonK(ctr.Lat));   // flat on stable, true nm on the beta globe (T-348)
            r.Area.Kind = "rect";
            r.Area.Rect = new[] { ctr.Lat - half, ctr.Lon - halfLon, ctr.Lat + half, ctr.Lon + halfLon };
            return r;
        }

        /// <summary>c: {uid, op: start|stop, radius_nm?: 5|10|20|0 (0 = all known; default for start 0)}.</summary>
        public static SarShipCmd ParseShip(Dictionary<string, object> c, out string err)
        {
            err = null;
            double? u = Num(c, "uid");
            if (!u.HasValue || u.Value != Math.Floor(u.Value) || u.Value == 0) { err = "missing_uid"; return null; }
            string op = Str(c, "op");
            if (op != "start" && op != "stop") { err = "bad_op"; return null; }
            var r = new SarShipCmd { Uid = (int)u.Value, Op = op };
            double? rad = Num(c, "radius_nm");
            if (op == "start" && rad.HasValue)
            {
                double x = rad.Value;
                if (x != 0 && x != 5 && x != 10 && x != 20) { err = "bad_radius"; return null; }
                r.RadiusNm = x;
            }
            return r;
        }

        private static Dictionary<string, object> M(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static List<object> Lst(params double[] v) { var l = new List<object>(); foreach (double x in v) l.Add(x); return l; }

        public static string SelfTest()
        {
            string err;
            var o = ParseOrder(M("group", "SAR-1"), out err);
            if (o == null || err != null || o.Area.Kind != "all" || o.OnComplete != "rtb") return "order default";
            o = ParseOrder(M("group", "G", "area", "all", "on_complete", "loiter"), out err);
            if (o == null || o.OnComplete != "loiter") return "order loiter";
            o = ParseOrder(M("group", "G", "area", Lst(35.2, 20.3, 35.0, 20.0)), out err);
            if (o == null || o.Area.Kind != "rect" || o.Area.Rect[0] != 35.0 || o.Area.Rect[3] != 20.3) return "order rect4";
            o = ParseOrder(M("group", "G", "area", Lst(35, 20, 35, 20.2, 35.1, 20.2, 35.1, 20)), out err);
            if (o == null || o.Area.Kind != "rect" || o.Area.Rect[2] != 35.1 || o.Area.Rect[1] != 20) return "order rect8";
            o = ParseOrder(M("group", "G", "area", M("lat", 35.0, "lon", 20.0, "radius_nm", 15.0)), out err);
            if (o == null || o.Area.Kind != "circle" || o.Area.RadiusNm != 15 || o.Area.Center.Lon != 20.0) return "order circle";
            o = ParseOrder(M("group", "G", "area", M("bearing", 90.0, "dist_nm", 60.0, "size_nm", 20.0, "origin", M("lat", 35.0, "lon", 20.0))), out err);
            if (o == null || o.Area.Kind != "rect") return "order bearing";
            double midLon = (o.Area.Rect[1] + o.Area.Rect[3]) / 2, midLat = (o.Area.Rect[0] + o.Area.Rect[2]) / 2;
            if (Math.Abs(midLon - 21.0) > 1e-6 || Math.Abs(midLat - 35.0) > 1e-6 || Math.Abs((o.Area.Rect[2] - o.Area.Rect[0]) * 60 - 20) > 1e-6) return "order bearing geometry";
            o = ParseOrder(M("group", "G", "area", M("bearing", 0.0, "dist_nm", 5.0), "origin", M("lat", 10.0, "lon", 10.0)), out err);
            if (o == null || Math.Abs((o.Area.Rect[2] - o.Area.Rect[0]) * 60 - 10) > 1e-6) return "order bearing default size/top origin";
            if (ParseOrder(M("area", "all"), out err) != null || err != "missing_group") return "missing group";
            if (ParseOrder(M("group", "G", "on_complete", "fly"), out err) != null || err != "bad_on_complete") return "bad on_complete";
            if (ParseOrder(M("group", "G", "area", "moon"), out err) != null || err != "bad_area") return "bad area str";
            if (ParseOrder(M("group", "G", "area", Lst(1, 2, 3)), out err) != null || err != "bad_area") return "bad rect";
            if (ParseOrder(M("group", "G", "area", M("lat", 1.0, "lon", 2.0)), out err) != null || err != "bad_area") return "bad circle";
            if (ParseOrder(M("group", "G", "area", M("bearing", 0.0, "dist_nm", 5.0)), out err) != null || err != "no_origin") return "no origin";
            var s = ParseShip(M("uid", 17.0, "op", "start", "radius_nm", 10.0), out err);
            if (s == null || s.Uid != 17 || s.Op != "start" || s.RadiusNm != 10) return "ship start 10";
            foreach (double r in new[] { 5.0, 20.0, 0.0 })
            {
                s = ParseShip(M("uid", 1.0, "op", "start", "radius_nm", r), out err);
                if (s == null || s.RadiusNm != r) return "ship radius " + r;
            }
            s = ParseShip(M("uid", 1.0, "op", "start"), out err);
            if (s == null || s.RadiusNm != 0) return "ship default radius";
            s = ParseShip(M("uid", 5.0, "op", "stop"), out err);
            if (s == null || s.Op != "stop") return "ship stop";
            if (ParseShip(M("op", "start"), out err) != null || err != "missing_uid") return "ship no uid";
            if (ParseShip(M("uid", 1.0, "op", "go"), out err) != null || err != "bad_op") return "ship bad op";
            if (ParseShip(M("uid", 1.0, "op", "start", "radius_nm", 7.0), out err) != null || err != "bad_radius") return "ship bad radius";
            return null;
        }
    }
}
