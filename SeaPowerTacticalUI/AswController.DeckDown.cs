using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class AswController
    {
        // T-231: a knocked-out flight deck takes no orders (live 0.30.11: a strike re-ordered from deck 103 every 38 s, 15x deck_inoperable).
        private readonly Dictionary<string, double> _deckDownAt = new Dictionary<string, double>();   // group|deck -> the mission time of the "deck down" line

        /// <summary>True = the deck is knocked out: no order goes to it. First time per (group, deck): a journal line and the warning; when the
        /// deck is back: "deck up" and the warning goes. Marks of a time ahead of the clock (a loaded save) are forgotten.</summary>
        private bool DeckOut(Grp g, int deckUid)
        {
            if (g == null || deckUid == 0) return false;
            string key = g.Name + "|" + I(deckUid);
            double at;
            bool known = _deckDownAt.TryGetValue(key, out at);
            if (known && _now < at) { _deckDownAt.Remove(key); known = false; }
            if (_relief.DeckDown(deckUid))
            {
                if (!known)
                {
                    _deckDownAt[key] = _now;
                    _game.Log("asw deck down " + g.Name + " deck=" + I(deckUid) + ": no orders until it is repaired");
                    Warn(g, "deck_down", "deck " + I(deckUid), 600);
                }
                else if (!_warnings.Exists(w => w.Group == g.Name && w.Code == "deck_down"))
                {
                    _warnAt.Remove(g.Name + "|deck_down");   // T-238: the line is gone (a load, the cap) and the deck is still down: it comes back, no new journal line
                    Warn(g, "deck_down", "deck " + I(deckUid), 600);
                }
                return true;
            }
            if (known)
            {
                _deckDownAt.Remove(key);
                _game.Log("asw deck up " + g.Name + " deck=" + I(deckUid));
                bool other = false;
                foreach (string k in _deckDownAt.Keys) if (k.StartsWith(g.Name + "|", StringComparison.Ordinal)) other = true;
                if (!other) ClearWarn(g, "deck_down");
            }
            return false;
        }

        /// <summary>T-238: once per tick, DeckOut for every deck a member of any group stands on, so a knocked-out deck keeps its warning (also after a load) until "deck up".</summary>
        private void SweepDecks()
        {
            foreach (var g in _groups)
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (!_live.TryGetValue(u, out m) || m.DeckUid == 0 || m.Where == null || !m.Where.StartsWith("deck", StringComparison.Ordinal)) continue;
                    DeckOut(g, m.DeckUid);
                }
        }

        /// <summary>T-238 test: a warning of a group as the controller raises it (to fill the 6-line cap).</summary>
        internal void WarnForTest(string group, string code, string detail) { Warn(Find(group), code, detail, 0); }
        /// <summary>T-238 test: what a load does to the warnings (the list and the rate-limit keys are cleared).</summary>
        internal void ForgetWarningsForTest() { _warnings.Clear(); _warnAt.Clear(); }

        /// <summary>Removes one warning line of the group and its rate-limit key (ClearWarnings drops them all).</summary>
        private void ClearWarn(Grp g, string code)
        {
            if (g == null) return;
            _warnings.RemoveAll(w => w.Group == g.Name && w.Code == code);
            _warnAt.Remove(g.Name + "|" + code);
        }
    }
}
