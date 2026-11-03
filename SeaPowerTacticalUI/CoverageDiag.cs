using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>T-320: pure diagnostics of the radar coverage layer for ui_state radar (units, rebuild). No Unity, no game types.</summary>
    internal static class CoverageDiag
    {
        /// <summary>ui_state radar.units: own radars grouped by owner, at most maxRadars entries (the rest counted in "more").
        /// Per radar: name, type, on, inoperable, emcon, emitting, airborne, antenna_m, profiles:[{tier, range_nm, active}] of the frame (matched by OwnerUid + Name).</summary>
        public static string UnitsJson(IList<RadarSpec> specs, CoverageFrame frame, int maxRadars)
        {
            if (specs == null) return "null";
            var sb = new StringBuilder();
            sb.Append("{\"total\":").Append(specs.Count).Append(",\"owners\":[");
            int shown = 0, lastUid = 0; bool firstOwner = true, firstRadar = true;
            for (int i = 0; i < specs.Count; i++)
            {
                var r = specs[i];
                if (r == null) continue;
                if (shown >= maxRadars) break;
                if (firstOwner || r.OwnerUid != lastUid)
                {
                    if (!firstOwner) sb.Append("]},");
                    firstOwner = false; firstRadar = true; lastUid = r.OwnerUid;
                    sb.Append("{\"uid\":").Append(r.OwnerUid).Append(",\"name\":").Append(Json.Str(r.OwnerName)).Append(",\"radars\":[");
                }
                if (!firstRadar) sb.Append(',');
                firstRadar = false; shown++;
                sb.Append("{\"name\":").Append(Json.Str(r.Name)).Append(",\"type\":").Append(Json.Str(r.Type));
                sb.Append(",\"on\":").Append(Json.Bool(r.On)).Append(",\"inoperable\":").Append(Json.Bool(r.Inoperable)).Append(",\"emcon\":").Append(Json.Bool(r.Emcon));
                sb.Append(",\"emitting\":").Append(Json.Bool(CoverageMath.Emitting(r))).Append(",\"airborne\":").Append(Json.Bool(r.Airborne));
                sb.Append(",\"antenna_m\":").Append(Json.Num(Math.Round(r.AntennaM, 1))).Append(",\"profiles\":[");
                bool firstP = true;
                if (frame != null && frame.Profiles != null)
                    foreach (var p in frame.Profiles)
                    {
                        if (p == null || p.OwnerUid != r.OwnerUid || p.Radar != r.Name) continue;
                        if (!firstP) sb.Append(',');
                        firstP = false;
                        sb.Append("{\"tier\":").Append(p.Tier).Append(",\"range_nm\":").Append(Json.Num(Math.Round(p.RangeM / CoverageMath.MPerNm, 1))).Append(",\"active\":").Append(Json.Bool(p.Active)).Append('}');
                    }
                sb.Append("]}");
            }
            if (!firstOwner) sb.Append("]}");
            sb.Append("],\"more\":").Append(Math.Max(0, specs.Count - shown)).Append('}');
            return sb.ToString();
        }

        /// <summary>ui_state radar.rebuild: {"built":n,"shifted":n,"reused":n,"why":{...}} of the last frame; null frame -> "null".</summary>
        public static string RebuildJson(CoverageFrame frame)
        {
            if (frame == null) return "null";
            var w = frame.RebuildWhy ?? new int[6];
            var sb = new StringBuilder();
            sb.Append("{\"built\":").Append(frame.ProfilesBuilt).Append(",\"shifted\":").Append(frame.ProfilesShifted).Append(",\"reused\":").Append(frame.Reused);
            sb.Append(",\"why\":{\"new\":").Append(At(w, CoverageReuse.WhyNew)).Append(",\"state\":").Append(At(w, CoverageReuse.WhyState)).Append(",\"range\":").Append(At(w, CoverageReuse.WhyRange))
              .Append(",\"axis\":").Append(At(w, CoverageReuse.WhyAxis)).Append(",\"dist\":").Append(At(w, CoverageReuse.WhyDist)).Append("}}");
            return sb.ToString();
        }

        private static int At(int[] a, int i) { return i >= 0 && i < a.Length ? a[i] : 0; }

        public static string SelfTest()
        {
            try
            {
                Func<int, string, bool, bool, RadarSpec> mk = (uid, name, on, emcon) => new RadarSpec { OwnerUid = uid, OwnerName = "U" + uid, Name = name, Type = "Search", On = on, Emcon = emcon, Airborne = uid == 2, AntennaM = 30 };
                var specs = new List<RadarSpec> { mk(1, "SPS-49", true, false), mk(2, "N001", true, true), mk(3, "SPY", false, false) };
                var frame = new CoverageFrame
                {
                    Profiles = new List<RayProfile> { new RayProfile { OwnerUid = 2, Radar = "N001", Tier = 1, RangeM = 185200, Active = true }, new RayProfile { OwnerUid = 3, Radar = "SPY", Tier = 0, RangeM = 1852, Active = false } },
                    Reused = 3, ProfilesBuilt = 2, ProfilesShifted = 1, RebuildWhy = new int[] { 0, 1, 0, 2, 0, 3 }
                };
                var all = Json.Parse(UnitsJson(specs, frame, 64)) as Dictionary<string, object>;
                if (all == null) return "units not an object";
                var owners = all["owners"] as List<object>;
                if (owners == null || owners.Count != 3) return "owners " + (owners == null ? -1 : owners.Count);
                var air = (((owners[1] as Dictionary<string, object>)["radars"] as List<object>)[0]) as Dictionary<string, object>;
                if (!(bool)air["emitting"] || !(bool)air["emcon"]) return "air emitting/emcon";
                var prof = air["profiles"] as List<object>;
                if (prof.Count != 1 || Math.Abs((double)(prof[0] as Dictionary<string, object>)["range_nm"] - 100.0) > 0.01 || !(bool)(prof[0] as Dictionary<string, object>)["active"]) return "air profile";
                var off = (((owners[2] as Dictionary<string, object>)["radars"] as List<object>)[0]) as Dictionary<string, object>;
                if ((bool)off["emitting"]) return "off radar emitting";
                if ((double)all["more"] != 0 || (double)all["total"] != 3) return "more/total";
                var two = Json.Parse(UnitsJson(specs, frame, 2)) as Dictionary<string, object>;
                int n = 0; foreach (var o in two["owners"] as List<object>) n += ((o as Dictionary<string, object>)["radars"] as List<object>).Count;
                if (n != 2 || (double)two["more"] != 1) return "limit " + n + " more " + two["more"];
                if (UnitsJson(specs, null, 64).IndexOf("\"profiles\":[]", StringComparison.Ordinal) < 0) return "null frame profiles";
                if (UnitsJson(null, frame, 4) != "null") return "null specs";
                if (RebuildJson(null) != "null") return "rebuild null";
                var rb = Json.Parse(RebuildJson(frame)) as Dictionary<string, object>;
                var why = rb["why"] as Dictionary<string, object>;
                if ((double)rb["built"] != 2 || (double)rb["shifted"] != 1 || (double)rb["reused"] != 3) return "rebuild counts";
                if ((double)why["new"] != 3 || (double)why["state"] != 1 || (double)why["axis"] != 2 || (double)why["dist"] != 0) return "rebuild why";
                return null;
            }
            catch (Exception e) { return "exception " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
