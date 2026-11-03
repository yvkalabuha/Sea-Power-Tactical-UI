using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>Builds the one-line JSON of `selfcheck manifest` (PROTOCOL / plan section 3.5). Pure: no game, no Harmony.</summary>
    internal static class ManifestJson
    {
        internal sealed class PatchInfo
        {
            public string Id, Target, When;   // When: always | tactics_owner | radial_input
            public PatchInfo(string id, string target, string when) { Id = id; Target = target; When = when; }
        }

        /// <summary>One function of the mod for the `features` list of the UI manifest (ADR-0043): what it needs from the game and what happens without it.</summary>
        internal sealed class FeatureInfo
        {
            public string Id, Module, Fallback;   // Fallback = null: no fallback path (the function is switched off)
            public string[] Needs;
            public FeatureInfo(string id, string module, string[] needs, string fallback) { Id = id; Module = module; Needs = needs; Fallback = fallback; }
        }

        public static string PlanJson(OwnershipPlan p)
        {
            return "{\"enabled\":" + Json.Bool(p.Enabled) + ",\"tactics_owner\":" + Json.Bool(p.TacticsOwner) +
                   ",\"link\":" + Json.Bool(p.Link) + ",\"press_ignore\":" + Json.Bool(p.PressIgnore) +
                   ",\"warning\":" + (p.Warning == null ? "null" : Json.Str(p.Warning)) + "}";
        }

        private static string StrList(IEnumerable<string> l)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var x in l) { if (!first) sb.Append(','); first = false; sb.Append(Json.Str(x)); }
            return sb.Append(']').ToString();
        }

        public static string Build(string flavor, string guid, string harmonyId, string version, bool server, bool autopause, string ignore,
            IEnumerable<string> hotkeys, IEnumerable<string> menus, IEnumerable<PatchInfo> patches, IEnumerable<KeyValuePair<string, OwnershipPlan>> plans)
        {
            return Build(flavor, guid, harmonyId, version, server, autopause, ignore, hotkeys, menus, patches, plans, null);   // no features: the output is as before
        }

        /// <summary>As above, plus the `features` list (T-120, UI manifest only); features == null leaves the output unchanged.</summary>
        public static string Build(string flavor, string guid, string harmonyId, string version, bool server, bool autopause, string ignore,
            IEnumerable<string> hotkeys, IEnumerable<string> menus, IEnumerable<PatchInfo> patches, IEnumerable<KeyValuePair<string, OwnershipPlan>> plans,
            IEnumerable<FeatureInfo> features)
        {
            var sb = new StringBuilder("{\"flavor\":").Append(Json.Str(flavor));
            sb.Append(",\"guid\":").Append(Json.Str(guid)).Append(",\"harmony_id\":").Append(Json.Str(harmonyId));
            sb.Append(",\"version\":").Append(Json.Str(version));
            sb.Append(",\"server\":").Append(Json.Bool(server)).Append(",\"autopause\":").Append(Json.Bool(autopause));
            sb.Append(",\"ignore\":").Append(Json.Str(ignore));
            sb.Append(",\"hotkeys\":").Append(StrList(hotkeys)).Append(",\"menus\":").Append(StrList(menus));
            bool first = true;
            if (features != null)
            {
                sb.Append(",\"features\":[");
                foreach (var f in features)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"id\":").Append(Json.Str(f.Id)).Append(",\"module\":").Append(Json.Str(f.Module)).Append(",\"needs\":").Append(StrList(f.Needs))
                      .Append(",\"fallback\":").Append(f.Fallback == null ? "null" : Json.Str(f.Fallback)).Append('}');
                }
                sb.Append(']');
                first = true;
            }
            sb.Append(",\"patches\":[");
            foreach (var p in patches)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(Json.Str(p.Id)).Append(",\"target\":").Append(Json.Str(p.Target)).Append(",\"when\":").Append(Json.Str(p.When)).Append('}');
            }
            sb.Append("],\"plans\":{");
            first = true;
            foreach (var kv in plans)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(Json.Str(kv.Key)).Append(':').Append(PlanJson(kv.Value));
            }
            return sb.Append("}}").ToString();
        }
    }
}
