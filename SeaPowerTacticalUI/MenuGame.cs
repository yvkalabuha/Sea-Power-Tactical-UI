using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// Menu / ring hooks that need the Standing Orders and smart attack code (T-107 M, ADR-0037 section 3).
    /// Kept apart from Menu.cs so that the offline selfcheck, which does not compile DoctrineGame.cs / TorpedoWire.cs yet, still builds Menu.cs.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>Fills Kind (ship|sub|air|helo), the air-defence-ready flag, the Standing Orders switch and the wire-torpedo flag of the sub.</summary>
        partial void MenuViewExt(object unit, UnitView v)
        {
            try
            {
                string k4 = UnitKind4(unit);
                if (k4 != null) v.Kind = k4;
                v.Flags["air_defense_ready"] = ResolveAirDefenseReady(unit);
                if (!StandingOrdersOn) v.Caps.Remove("standing_orders");   // missing members or [StandingOrders] ForceFallback
                if (v.Kind == "sub" && Capabilities.Contains("smart_attack")) v.WiredTorpedoes = SubHasWireTorpedo(unit);
            }
            catch { }
        }

        /// <summary>air_defense_ready: set_doctrine on the unit level with the opposite of the resolved value (T-098 F2).</summary>
        partial void MenuRunExt(string nodeId, object unit, ref string result)
        {
            if (nodeId == "evade_attack") { result = AirEvadeFromMenu(unit); return; }   // T-100
            if (nodeId == "formation_return") { result = FormationReturnRun(unit, "menu"); return; }   // T-243
            if (nodeId == "air_rtb") { result = AirRtbRun(unit, "menu"); return; }   // T-243
            if (nodeId == "sub_torpedo_alert") { result = SubTorpedoAlertFromMenu(unit); return; }   // T-101
            if (nodeId != "air_defense_ready") return;
            var c = new Dictionary<string, object> { { "uid", (double)UidOf(unit) }, { "level", "unit" }, { "flag", "air_defense_ready" }, { "value", !ResolveAirDefenseReady(unit) } };
            result = SetDoctrineCmd("\"menu\"", c);
            Log("menu air_defense_ready uid=" + UidOf(unit) + " -> " + (result.Contains("\"ok\":true") ? "ok" : result));
        }

        /// <summary>The submarine carries at least one wire-guided torpedo (count above zero). The game's own ammunition data decides, never a list in code.</summary>
        private bool SubHasWireTorpedo(object u)
        {
            var dict = Get(u, CoreSpecs.AmmoCounts) as IEnumerable;
            if (dict == null) return false;
            foreach (var kv in dict)
            {
                try
                {
                    string file = Get(kv, CoreSpecs.PairKey) as string;
                    var cnt = Get(kv, CoreSpecs.PairValue);
                    if (file == null || cnt == null || Convert.ToInt32(cnt) <= 0) continue;
                    if (ReadWireAmmo(Call(u, CoreSpecs.AmmoByName, file)) != null) return true;
                }
                catch { }
            }
            return false;
        }
    }
}
