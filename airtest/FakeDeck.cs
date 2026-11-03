using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Fake game deck: an ordered task list per deck and a log of every IAirOps call (including Prioritise).</summary>
    internal sealed class FakeDeck : IAirOps
    {
        internal sealed class FTask { public string Key, Type, Loadout, State = "await_deck"; public bool Allowed, Ours = true; }

        public readonly Dictionary<int, List<FTask>> Decks = new Dictionary<int, List<FTask>>();
        public readonly List<string> Calls = new List<string>();
        private readonly Dictionary<int, List<string>> _toPrioritise = new Dictionary<int, List<string>>();
        public bool PrioUnsupported;
        public string AllowErr;
        private int _uid = 100;

        public List<FTask> Of(int deck)
        {
            List<FTask> l;
            if (!Decks.TryGetValue(deck, out l)) { l = new List<FTask>(); Decks[deck] = l; }
            return l;
        }

        /// <summary>A task of the player or the AI (not created by the mod).</summary>
        public string AddForeign(int deck, string state = "ready_up")
        {
            string key = "task:" + (++_uid);
            Of(deck).Add(new FTask { Key = key, Type = "x", Loadout = "L", State = state, Ours = false });
            return key;
        }

        public string DeckTimingsMode() { return "Realistic"; }
        public List<LoadoutOption> Loadouts(int deckUid, string vehicleType) { return new List<LoadoutOption>(); }

        public List<AirDeckTask> DeckTasks(int deckUid)
        {
            var res = new List<AirDeckTask>();
            foreach (var t in Of(deckUid))
                res.Add(new AirDeckTask { Key = t.Key, ShipUid = deckUid, Type = t.Type, Loadout = t.Loadout, State = t.State, Ours = t.Ours });
            return res;
        }

        public string OrderLaunch(int deckUid, string vehicleType, string loadoutName, string missionType, bool allowLaunch, out string key, out double readyS)
        {
            key = "task:" + (++_uid); readyS = 60;
            Of(deckUid).Add(new FTask { Key = key, Type = vehicleType, Loadout = loadoutName, Allowed = allowLaunch });
            Calls.Add("order " + deckUid + " " + key + (allowLaunch ? " allow" : ""));
            return null;
        }

        private FTask Find(int deck, string key) { foreach (var t in Of(deck)) if (t.Key == key) return t; return null; }

        public string AllowLaunch(int deckUid, string key)
        {
            Calls.Add("allow " + deckUid + " " + key);
            if (AllowErr != null) return AllowErr;
            var t = Find(deckUid, key);
            if (t == null) return "deck_task_not_found";
            t.Allowed = true;
            return null;
        }

        public readonly HashSet<int> Down = new HashSet<int>();
        public bool DeckInoperable(int deckUid) { return Down.Contains(deckUid); }
        public string DeckDump(int deckUid) { var p = new List<string>(); foreach (var t in Of(deckUid)) p.Add(t.Type + ":" + t.State + ":" + t.Key); return p.Count == 0 ? "-" : string.Join(" ", p); }

        public string AbortLaunch(int deckUid, string key)
        {
            Calls.Add("abort " + deckUid + " " + key);
            var t = Find(deckUid, key);
            if (t == null) return "deck_task_not_found";
            Of(deckUid).Remove(t);
            return null;
        }

        public string Prioritise(int deckUid, string key)
        {
            Calls.Add("prioritise " + deckUid + " " + key);
            if (PrioUnsupported) return "unsupported";
            var t = Find(deckUid, key);
            if (t == null) return "deck_task_not_found";
            if (!t.Ours) return "not_ours";
            if (t.State != "await_deck" && t.State != "await_crew" && t.State != "ready_up") return "bad_state";
            List<string> q;
            if (!_toPrioritise.TryGetValue(deckUid, out q)) { q = new List<string>(); _toPrioritise[deckUid] = q; }
            if (!q.Contains(key)) q.Add(key);
            return null;
        }

        /// <summary>The game tick: _flightDeckTasksToPrioritise is applied in call order, each task goes to the head (the last one ends first).</summary>
        public void GameTick()
        {
            foreach (var kv in _toPrioritise)
            {
                var l = Of(kv.Key);
                foreach (var key in kv.Value)
                {
                    var t = Find(kv.Key, key);
                    if (t != null) { l.Remove(t); l.Insert(0, t); }
                }
                kv.Value.Clear();
            }
        }

        public string Keys(int deck)
        {
            var sb = new List<string>();
            foreach (var t in Of(deck)) sb.Add(t.Key);
            return string.Join(",", sb);
        }
    }
}
