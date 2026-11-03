using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>A map layer the dev channel can switch (on = null: toggle); returns the state after the call.</summary>
    internal interface ILayerSwitch { bool Switch(bool? on); }

    /// <summary>ILayerSwitch over a function (built by the owning feature).</summary>
    internal sealed class LayerSwitch : ILayerSwitch
    {
        private readonly Func<bool?, bool> _f;
        internal LayerSwitch(Func<bool?, bool> f) { _f = f; }
        public bool Switch(bool? on) { return _f(on); }
    }

    /// <summary>Dev channel (T-139 follow-up): the map layers "range_rings" and "contact_info" switched like the layer menu does (same setters).
    /// The feature module installs the delegate in Init and clears it in Dispose; null = the module is off.</summary>
    internal sealed partial class GameApi
    {
        /// <summary>on = null: toggle. Returns the state after the call.</summary>
        internal ILayerSwitch RangeRingsSwitch;
        internal ILayerSwitch ContactInfoSwitch;

        /// <summary>range_rings {on?} | contact_info {on?}. Main thread. Null = not ours.</summary>
        private string LayerSwitchCmd(string id, string name, Dictionary<string, object> cmd)
        {
            ILayerSwitch sw;
            if (name == "range_rings") sw = RangeRingsSwitch;
            else if (name == "contact_info") sw = ContactInfoSwitch;
            else return null;
            if (sw == null) return Err(id, "unsupported", name + " module is off");
            bool? on = null;
            object o;
            if (cmd != null && cmd.TryGetValue("on", out o) && o != null)
            {
                if (!(o is bool)) return Err(id, "bad_args", "on (true/false) optional");
                on = (bool)o;
            }
            bool now = sw.Switch(on);
            return Ok(id, "\"on\":" + Json.Bool(now));
        }
    }
}
