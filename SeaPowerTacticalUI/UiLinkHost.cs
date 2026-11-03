using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// UI side of the in-process link channel v1 (ADR-0033, plan 3.4). Puts a slot into the AppDomain; the voice mod calls it.
    /// Only BCL types cross the slot. No HarmonyLib / BepInEx / IMGUI references: compiled into the UI selfcheck.
    /// </summary>
    internal static class UiLinkHost
    {
        private static GameApi _api;
        private static Func<string> _caps;
        private static Action _ensureStarted;

        /// <summary>capsJson returns the inner JSON "capabilities":[..],"missing":[..] (see CapsInner). ensureStarted (optional) runs the owner decision before the first answer.</summary>
        internal static void Register(GameApi api, Func<string> capsJson, Action ensureStarted = null)
        {
            _api = api;
            _caps = capsJson;
            _ensureStarted = ensureStarted;
            var slot = new Dictionary<string, object>
            {
                { "link", Ownership.LinkVersion },
                { "version", ModInfo.Version },
                { "owner_tactics", true },
                { "call", (Func<string, string>)Call },
            };
            AppDomain.CurrentDomain.SetData(Ownership.SlotKey, slot);
        }

        internal static void Unregister()
        {
            AppDomain.CurrentDomain.SetData(Ownership.SlotKey, null);
            _api = null; _caps = null; _ensureStarted = null;
        }

        internal static string CapsInner(GameApi api)
        {
            var sb = new StringBuilder("\"capabilities\":[");
            bool first = true;
            foreach (var c in api.Capabilities) { if (!first) sb.Append(','); first = false; sb.Append(Json.Str(c)); }
            sb.Append("],\"missing\":[");
            first = true;
            foreach (var c in api.Checks)
            {
                if (c.Present) continue;
                if (!first) sb.Append(','); first = false; sb.Append(Json.Str(c.Member));
            }
            return sb.Append(']').ToString();
        }

        private static string IdOf(Dictionary<string, object> cmd)
        {
            object o; cmd.TryGetValue("id", out o);
            if (o == null) return "null";
            if (o is string) return Json.Str((string)o);
            if (o is double) return Json.Num((double)o);
            return "null";
        }

        private static string Ok(string id, string data)
        {
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":true,\"data\":" + data + "}";
        }

        private static string Err(string id, string code, string detail)
        {
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":false,\"error\":" + Json.Str(code) + (detail == null ? "" : ",\"detail\":" + Json.Str(detail)) + "}";
        }

        /// <summary>One NDJSON command line in, one ack line out. Main thread only (the voice mod calls it from Update).</summary>
        internal static string Call(string line)
        {
            var api = _api;
            if (api == null) return Err("null", "unsupported", "ui mod is off");
            Dictionary<string, object> cmd = null;
            try
            {
                cmd = Json.Parse(line) as Dictionary<string, object>;
                if (cmd == null) return Err("null", "bad_json", "command must be an object");
                string id = IdOf(cmd);
                string name = cmd.ContainsKey("cmd") ? cmd["cmd"] as string : null;
                try { if (_ensureStarted != null) _ensureStarted(); } catch { }
                if (_api == null) return Err(id, "unsupported", "ui mod is off");
                switch (name)
                {
                    case "link_hello":
                        return Ok(id, "{\"link\":" + Ownership.LinkVersion + ",\"version\":" + Json.Str(ModInfo.Version) + "," + (_caps != null ? _caps() : CapsInner(api)) + "}");
                    case "link_state":
                        return Ok(id, StateData(api));
                    case "link_events":
                        return Ok(id, EventsData(api));
                    default:
                        return api.Execute(cmd);
                }
            }
            catch (Exception e)
            {
                return Err(cmd == null ? "null" : IdOf(cmd), "link_error", e.GetType().Name + ": " + e.Message);
            }
        }

        private static string Frag(GameApi api, string cap, Func<string> f)
        {
            if (!api.Capabilities.Contains(cap)) return "null";
            try { return f(); } catch { return "null"; }
        }

        private static string StateData(GameApi api)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"overlay\":").Append(Frag(api, "radar_overlay", api.OverlayJson));
            sb.Append(",\"radial\":").Append(Frag(api, "radial_menu", api.RadialStateJson));
            sb.Append(",\"overview\":").Append(Frag(api, "overview", api.OverviewStateJson));
            string asw = null;
            if (api.Capabilities.Contains("asw")) { try { asw = api.AswStateJson(); } catch { } }
            sb.Append(",\"asw\":").Append(asw ?? "null"); // T-095, UI mod only: the voice mod ignores it until T-106
            string cap = null;
            if (api.Capabilities.Contains("cap")) { try { cap = api.CapStateJson(); } catch { } }
            sb.Append(",\"cap\":").Append(cap ?? "null"); // T-112, UI mod only: the voice mod ignores it until T-106
            string smart = null;
            if (api.Capabilities.Contains("smart_attack")) { try { smart = api.SmartAttackStateJson(); } catch { } }
            sb.Append(",\"smart_attack\":").Append(smart ?? "null"); // T-102, UI mod only: the voice mod ignores it until T-106
            string tac = "null";
            if (api.Capabilities.Contains("unit_flags") || api.HarmonyStatus != "off") { try { tac = api.TacticsJson(); } catch { } }
            sb.Append(",\"tactics\":").Append(tac);
            sb.Append(",\"flags_by_uid\":").Append(FlagsByUid(api));
            return sb.Append('}').ToString();
        }

        private static string FlagsByUid(GameApi api)
        {
            var sb = new StringBuilder("{");
            if (!api.Capabilities.Contains("unit_flags")) return "{}";
            try
            {
                if (!api.Frame.InMission) return "{}";
                var seen = new HashSet<int>();
                bool first = true;
                Action<object> add = u =>
                {
                    try
                    {
                        if (u == null || api.UnitKindOfObj(u) == null) return;
                        int uid = api.UidOf(u);
                        if (!seen.Add(uid)) return;
                        if (!first) sb.Append(','); first = false;
                        sb.Append('"').Append(uid).Append("\":").Append(api.FlagsJson(u));
                    }
                    catch { }
                };
                var tf = api.GetStatic(CoreSpecs.PlayerTaskforce);
                foreach (var listSpec in CoreSpecs.OwnShipLists)
                {
                    var list = api.Get(tf, listSpec) as IEnumerable;
                    if (list == null) continue;
                    foreach (var x in list) add(x);
                }
                foreach (var u in api.AirUnitsForLink()) add(u);
            }
            catch { }
            return sb.Append('}').ToString();
        }

        private static string EventsData(GameApi api)
        {
            var evs = api.DrainEvents();
            return "{\"events\":[" + (evs == null ? "" : string.Join(",", evs.ToArray())) + "]}";
        }
    }
}
