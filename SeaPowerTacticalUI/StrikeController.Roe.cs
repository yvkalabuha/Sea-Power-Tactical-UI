using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase C (ADR-0132 section 6, plan 24.3): how the aircraft of a group are kept from firing on their own.
    //  veto - the weapon status is left alone; the veto chain of the mod (participant "strike") holds the automatic anti-ship launches of the members.
    //  hold - the weapon status of every member is "hold" while it is a member; the previous status comes back when the aircraft leaves the group (removal, return of control, a leave of the tick, disband).
    // Without the patch of the game's fire veto the veto cannot hold anything: the mode is "hold" then (the log says so once).
    internal sealed partial class StrikeController
    {
        /// <summary>false when the patch of the engage veto is not applied (the feature sets it from the patch list of the mod).</summary>
        public bool FireVetoPatched = true;

        private readonly Dictionary<int, string> _prevRoe = new Dictionary<int, string>();
        private bool _roeFallbackLogged;

        /// <summary>The mode the mod really uses: veto | hold.</summary>
        public string EffectiveRoe
        {
            get
            {
                if (string.Equals(_p.RoeMode, "hold", StringComparison.OrdinalIgnoreCase)) return "hold";
                return FireVetoPatched ? "veto" : "hold";
            }
        }

        /// <summary>Applies the mode to the members of the group (hold: every member not done yet); returns the effective mode.</summary>
        private string ApplyRoe(StrikeGroup g)
        {
            string mode = EffectiveRoe;
            if (mode == "veto") return mode;
            if (!FireVetoPatched && !string.Equals(_p.RoeMode, "hold", StringComparison.OrdinalIgnoreCase) && !_roeFallbackLogged)
            {
                _roeFallbackLogged = true;
                _log("[Strike] strike roe fallback why=no_fire_veto");
            }
            foreach (int uid in g.Members)
            {
                if (_prevRoe.ContainsKey(uid)) continue;
                string prev;
                string err = null;
                try { err = _g.SetRoe(uid, "hold", out prev); } catch (Exception) { prev = null; err = "failed"; }
                if (err == null) { _prevRoe[uid] = prev ?? "free"; _log("[Strike] strike roe " + g.Name + " mode=hold uid=" + I(uid) + " prev=" + (prev ?? "free")); }
                else _log("[Strike] strike roe " + g.Name + " mode=hold uid=" + I(uid) + " -> " + err);
            }
            return mode;
        }

        /// <summary>The previous weapon status of an aircraft that leaves its group; nothing if the mod did not change it.</summary>
        private void RoeBack(int uid)
        {
            string prev;
            if (!_prevRoe.TryGetValue(uid, out prev)) return;
            _prevRoe.Remove(uid);
            string was;
            string err = null;
            try { err = _g.SetRoe(uid, prev, out was); } catch (Exception) { err = "failed"; }
            _log("[Strike] strike roe back uid=" + I(uid) + " mode=" + prev + (err == null ? "" : " -> " + err));
        }
    }
}
