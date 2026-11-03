using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>ui_window {name: asw|cap|strike, on?}: opens or closes the ASW / CAP / Strike window (ADR-0046). Visibility only, no side effects on orders.</summary>
    internal sealed partial class GameApi
    {
        private string UiWindowCmd(string id, Dictionary<string, object> cmd)
        {
            string name = cmd.ContainsKey("name") ? cmd["name"] as string : null;
            if (name != "asw" && name != "cap" && name != "strike") return Err(id, "bad_args", "name must be asw, cap or strike");
            bool? on = null;
            if (cmd.ContainsKey("on") && cmd["on"] != null)
            {
                if (!(cmd["on"] is bool)) return Err(id, "bad_args", "on (true/false) optional");
                on = (bool)cmd["on"];
            }
            if (name == "strike")   // T-341
            {
                var sw = StrikeWindow;
                if (sw == null) return Err(id, "unsupported", "strike window is not available");
                bool prev = sw.Visible, wantS = on ?? !prev;
                if (wantS) sw.Open(); else sw.Visible = false;
                return Ok(id, "\"name\":\"strike\",\"on\":" + Json.Bool(sw.Visible) + ",\"previous\":" + Json.Bool(prev));
            }
            Ui.IAswWindow asw = name == "asw" ? AswWindow : null;
            Ui.ICapWindow cap = name == "cap" ? CapWindow : null;
            if (asw == null && cap == null) return Err(id, "unsupported", name + " window is not available");
            bool previous = asw != null ? asw.Visible : cap.Visible;
            bool want = on ?? !previous;
            if (want) { if (asw != null) asw.Open(); else cap.Open(); }
            else { if (asw != null) asw.Visible = false; else cap.Visible = false; }
            bool now = asw != null ? asw.Visible : cap.Visible;
            return Ok(id, "\"name\":" + Json.Str(name) + ",\"on\":" + Json.Bool(now) + ",\"previous\":" + Json.Bool(previous));
        }

        /// <summary>T-178 I3 ui_undo {window: asw|cap}: presses "Undo" of the card of "Stop all" / "Disband" (what the player's click does; dev channel, ADR-0077 s.8).</summary>
        private string UiUndoCmd(string id, Dictionary<string, object> cmd)
        {
            string name = cmd.ContainsKey("window") ? cmd["window"] as string : null;
            if (name != "asw" && name != "cap") return Err(id, "bad_args", "window must be asw or cap");
            Ui.IAswWindow asw = name == "asw" ? AswWindow : null;
            Ui.ICapWindow cap = name == "cap" ? CapWindow : null;
            if (asw == null && cap == null) return Err(id, "unsupported", name + " window is not available");
            string result = asw != null ? asw.Undo() : cap.Undo();
            if (result == null) return Err(id, "no_undo", "nothing to undo in the " + name + " window (the card is gone or never shown)");
            return Ok(id, "\"window\":" + Json.Str(name) + ",\"result\":" + Json.Str(result));
        }
    }
}
