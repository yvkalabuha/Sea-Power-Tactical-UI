using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-095: GameApi part of the asw_* commands (needs the game classes; not in the aswtest stand).
    internal sealed partial class GameApi
    {
        internal AswController Asw;   // set by the plugin (integration I3)

        private static string AswAck(string id, string r)
        {
            if (AswController.IsErr(r))
            {
                string body = r.Substring(1);
                int bar = body.IndexOf('|');
                return Err(id, bar < 0 ? body : body.Substring(0, bar), bar < 0 ? null : body.Substring(bar + 1));
            }
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":true,\"data\":" + r + "}";
        }

        /// <summary>asw_group|asw_search_course|asw_search_area|asw_stop|asw_depth|asw_attack|asw_attack_cancel|asw_order|asw_strike_skip|asw_strike_resume|asw_restore|asw_set_tactics (+ sar_order|sar_ship, T-109). Main thread. Every command and its answer go to the ASW log.</summary>
        internal string AswCmd(string id, string name, Dictionary<string, object> cmd)
        {
            var c = Asw;
            if (c == null) return Err(id, "unsupported", "asw controller is off");
            c.EnsureLive();   // T-126: an idle controller skips the game scan; a command works on fresh rows
            string r;
            switch (name)
            {
                case "asw_group": r = c.Group(cmd); break;
                case "asw_search_course": r = c.SearchCourse(cmd); break;
                case "asw_search_area": r = c.SearchArea(cmd); break;
                case "asw_stop": r = c.Stop(cmd); break;
                case "asw_depth": r = c.Depth(cmd); break;
                case "asw_attack": r = c.Attack(cmd); break;
                case "asw_attack_cancel": r = c.AttackCancel(cmd); break;
                case "asw_order": r = c.Order(cmd); break;
                case "asw_strike_skip": r = c.StrikeSkip(cmd, true); break;
                case "asw_strike_resume": r = c.StrikeSkip(cmd, false); break;
                case "asw_restore": r = c.Restore(cmd); break;
                case "asw_set_tactics": r = c.SetTactics(cmd); break;
                case "asw_loadout": r = c.Loadout(cmd); break;
                case "asw_preview": r = c.PreviewCmd(cmd); break;
                case "asw_preview_clear": r = c.PreviewClearCmd(); break;
                case "sar_order": r = c.SarOrder(cmd); break;
                case "sar_ship": r = c.SarShip(cmd); break;
                default: return null;
            }
            try
            {
                bool sar = name.StartsWith("sar_", StringComparison.Ordinal);
                string line = (sar ? "sar cmd " : "asw cmd ") + name + " " + AswArgs(cmd) + " -> " + (AswController.IsErr(r) ? r : "ok");
                var sink = AswLogSink;
                if (sink != null) sink(sar ? "[Sar] " + line : "[Asw] " + line); else UnityDebugLog(sar ? "[sar] " + line : "[asw] " + line);
            }
            catch { }
            return AswAck(id, r);
        }

        // separate method: the JIT must not need UnityEngine to compile AswCmd (the link smoke runs without the game's assemblies)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void UnityDebugLog(string m) { UnityEngine.Debug.Log(m); }

        private static string AswArgs(Dictionary<string, object> cmd)
        {
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var kv in cmd)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(kv.Key).Append('=');
                var l = kv.Value as List<object>;
                if (l != null) { sb.Append('['); for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(l[i] is List<object> ? "[..]" : Convert.ToString(l[i], System.Globalization.CultureInfo.InvariantCulture)); } sb.Append(']'); }
                else sb.Append(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Append('}').ToString();
        }

        /// <summary>Fragment "asw" for link_state; null when the controller is off.</summary>
        internal string AswStateJson() { if (Asw == null) return null; Asw.FreshView(); return Asw.StateJson(); }
    }
}
