using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 10: the link commands of the strike window (the window, the ring and the dev mod use one path, as cap_*): strike_group, strike_target, strike_plan, strike_probe.
    internal sealed partial class GameApi
    {
        /// <summary>strike_group | strike_target | strike_plan | strike_cancel | strike_launch | strike_probe. Main thread. Every command and its answer go to the [Strike] log.</summary>
        internal string StrikeCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (Strike == null) return Err(id, "unsupported", "strike controller is off");
            string r = StrikeRaw(name, cmd);
            if (r == null) return null;
            try { StrikeLog("[Strike] strike cmd " + name + " " + AswArgs(cmd) + " -> " + (StrikeController.IsErr(r) ? r : "ok")); }
            catch (Exception) { }
            return AswAck(id, r);
        }

        /// <summary>The controller answer as is: ack json or "!code" (the window path). null = not a strike command.</summary>
        internal string StrikeRaw(string name, Dictionary<string, object> cmd)
        {
            var c = Strike;
            if (c == null) return "!unsupported";
            switch (name)
            {
                case "strike_group": return c.Group(cmd);
                case "strike_target": return c.Target(cmd);
                case "strike_plan": return c.Plan(cmd);
                case "strike_cancel": return c.Cancel(cmd);
                case "strike_goal": return c.SetGoal(cmd);
                case "strike_auto": return c.SetAuto(cmd);
                case "strike_deck_cancel": return c.DeckCancel(cmd);
                case "strike_deck_reorder": return c.DeckReorder(cmd);
                case "strike_launch": return c.Launch(cmd);
                case "strike_probe": return StrikeProbeOp(cmd);
                default: return null;
            }
        }
    }
}
