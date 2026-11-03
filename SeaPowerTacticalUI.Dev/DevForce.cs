using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Dev
{
    /// <summary>
    /// T-361: `force` = QA scenarios built from REAL game orders (a buoy on a submarine, a route shift, a divert, a friend into the line of fire,
    /// a hostile towards a SAR helicopter). Dev DLL part: argument schema per op and the journal line; the work is done by the `qa_force` link command.
    /// BCL only, so devtest compiles it. Cheat scope: dev mod only (ADR-0136).
    /// </summary>
    internal static class DevForce
    {
        public static readonly string[] Ops = { "buoy_on_sub", "route_shift", "divert", "ff_lead", "steer_hostile" };

        private sealed class Spec
        {
            public string Required, Optional;
        }

        private static Spec S(string req, string opt) { return new Spec { Required = req, Optional = opt }; }

        private static Spec SpecOf(string op)
        {
            switch (op)
            {
                case "buoy_on_sub": return S("uid", "target_uid dist_nm ammo");
                case "route_shift": return S("uid", "dist_nm bearing require_ai");
                case "divert": return S("uid", "dist_nm bearing");
                case "ff_lead": return S("uid shooter target_id", "ahead_nm off_deg");
                case "steer_hostile": return S("to_uid", "uid stop_nm away hold_fire");
                default: return null;
            }
        }

        private static string[] Split(string s) { return s.Length == 0 ? new string[0] : s.Split(' '); }

        private static bool Range(string op, string k, double v, out string detail)
        {
            double lo, hi;
            switch (op + "." + k)
            {
                case "buoy_on_sub.dist_nm": lo = 0; hi = 2; break;
                case "route_shift.dist_nm": lo = 0.6; hi = 30; break;
                case "route_shift.bearing":
                case "divert.bearing": lo = 0; hi = 360; break;
                case "divert.dist_nm": lo = 1; hi = 100; break;
                case "ff_lead.ahead_nm": lo = 0.2; hi = 2; break;
                case "ff_lead.off_deg": lo = 0; hi = 90; break;
                case "steer_hostile.stop_nm": lo = 1; hi = 20; break;
                default: detail = null; return true;
            }
            if (v < lo || v > hi)
            {
                detail = k + " " + lo.ToString(CultureInfo.InvariantCulture) + ".." + hi.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            detail = null;
            return true;
        }

        /// <summary>null = ok; "bad_args:&lt;detail&gt;" (unknown op, an argument not allowed for the op, a required one missing, a range).</summary>
        public static string Check(Dictionary<string, object> a)
        {
            object v;
            string op = a != null && a.TryGetValue("op", out v) ? v as string : null;
            if (op == null) return "bad_args:op is required: " + string.Join("|", Ops);
            var spec = SpecOf(op);
            if (spec == null) return "bad_args:op: " + string.Join("|", Ops);
            var req = Split(spec.Required);
            var allowed = new HashSet<string>(req);
            foreach (string o in Split(spec.Optional)) allowed.Add(o);
            allowed.Add("op");
            foreach (string k in a.Keys) if (!allowed.Contains(k)) return "bad_args:" + k + " is not an argument of " + op;
            foreach (string k in req) if (!a.ContainsKey(k)) return "bad_args:" + k + " is required for " + op;
            foreach (var kv in a)
            {
                if (!(kv.Value is double)) continue;
                string detail;
                if (!Range(op, kv.Key, (double)kv.Value, out detail)) return "bad_args:" + detail;
            }
            return null;
        }

        /// <summary>[Dev][FORCE] op=&lt;op&gt; args=&lt;k=v ...&gt; → &lt;result&gt; (tests rely on the format).</summary>
        public static string Line(Dictionary<string, object> a, string result)
        {
            object v;
            string op = a != null && a.TryGetValue("op", out v) && v is string ? (string)v : "-";
            var keys = new List<string>();
            if (a != null) foreach (string k in a.Keys) if (k != "op") keys.Add(k);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string k in keys)
            {
                if (sb.Length > 0) sb.Append(' ');
                object x = a[k];
                string s = x is double ? ((double)x).ToString("0.###", CultureInfo.InvariantCulture)
                         : x is bool ? ((bool)x ? "true" : "false") : Convert.ToString(x, CultureInfo.InvariantCulture);
                sb.Append(k).Append('=').Append(s);
            }
            return "[Dev][FORCE] op=" + op + " args=" + (sb.Length == 0 ? "-" : sb.ToString()) + " → " + result;
        }

        private static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        internal static string SelfTest()
        {
            if (Check(O("op", "nope")) == null) return "unknown op accepted";
            if (Check(O()) == null) return "no op accepted";
            if (Check(O("op", "route_shift")) == null) return "route_shift without uid accepted";
            if (Check(O("op", "route_shift", "uid", 7.0)) != null) return "route_shift minimal";
            if (Check(O("op", "ff_lead", "uid", 1.0, "shooter", 2.0, "target_id", 3.0, "stop_nm", 4.0)) == null) return "ff_lead with stop_nm accepted";
            if (Check(O("op", "ff_lead", "uid", 1.0, "shooter", 2.0)) == null) return "ff_lead without target_id accepted";
            if (Check(O("op", "ff_lead", "uid", 1.0, "shooter", 2.0, "target_id", 3.0, "off_deg", 30.0)) != null) return "ff_lead off 30";
            if (Check(O("op", "ff_lead", "uid", 1.0, "shooter", 2.0, "target_id", 3.0, "off_deg", 91.0)) == null) return "off_deg range";
            if (Check(O("op", "buoy_on_sub", "uid", 1.0, "dist_nm", 2.5)) == null) return "buoy dist range";
            if (Check(O("op", "route_shift", "uid", 1.0, "dist_nm", 0.5)) == null) return "route_shift dist low";
            if (Check(O("op", "divert", "uid", 1.0, "dist_nm", 20.0, "bearing", 361.0)) == null) return "divert bearing range";
            if (Check(O("op", "divert", "uid", 1.0, "dist_nm", 20.0)) != null) return "divert ok";
            if (Check(O("op", "steer_hostile", "to_uid", 5.0, "away", true, "hold_fire", false)) != null) return "steer ok";
            if (Check(O("op", "steer_hostile", "uid", 5.0)) == null) return "steer without to_uid accepted";
            if (Check(O("op", "steer_hostile", "to_uid", 5.0, "stop_nm", 0.5)) == null) return "stop_nm range";
            if (Line(O("op", "divert", "uid", 7.0, "dist_nm", 20.0), "ok") != "[Dev][FORCE] op=divert args=dist_nm=20 uid=7 → ok") return "line: " + Line(O("op", "divert", "uid", 7.0, "dist_nm", 20.0), "ok");
            return null;
        }
    }
}
