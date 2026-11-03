using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-235: the [Cap] cover keys are saved with the group parameters; an old file (no cover keys, with lane_sep_max_nm) reads with the defaults.
    internal static class CapT235SaveTests
    {
        public static string Run(string only = null)
        {
            if (only != null && only != "save_keys") return "unknown scenario " + only;
            var d = new CapSaveData { MissionTimeS = 10 };
            d.Groups.Add(new CapGroupDto
            {
                Group = "CAP-1", Seq = 1, Roe = "tight", Rect = new[] { 10.0, 20.0, 10.5, 20.5 }, Anchor = "geo",
                Loadouts = new Dictionary<string, string>(), P = new CapParams { CoverAamFrac = 0.3, CoverFallbackNm = 7 },
                Members = new List<CapMemberDto>(), Keys = new List<string>()
            });
            string json = CapSave.ToJson(d), err;
            if (!json.Contains("\"cover_aam_frac\":0.3") || !json.Contains("\"cover_fallback_nm\":7") || json.Contains("lane_sep_max_nm")) return "keys not written: " + json;
            var back = CapSave.Parse(json, out err);
            if (err != null || back.Groups.Count != 1) return "round trip: " + err;
            var p = back.Groups[0].P;
            if (System.Math.Abs(p.CoverAamFrac - 0.3) > 1e-9 || System.Math.Abs(p.CoverFallbackNm - 7) > 1e-9) return "round trip values";
            // an old file: no cover keys, a lane_sep_max_nm that is ignored
            string old = json.Replace("\"cover_aam_frac\":0.3,\"cover_fallback_nm\":7", "\"lane_sep_max_nm\":4");
            if (old == json) return "old json not built";
            var ob = CapSave.Parse(old, out err);
            if (err != null || ob.Groups.Count != 1) return "old file: " + err;
            var op = ob.Groups[0].P;
            if (op.CoverAamFrac != 0.5 || op.CoverFallbackNm != 10) return "old file defaults";
            return null;
        }
    }
}
