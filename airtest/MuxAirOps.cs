using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// One deck for both controller fakes (D1): IAirOps that forwards a call to the ASW fake or the CAP fake (by vehicle type for orders, by key for the rest),
    /// keeps ONE journal of every call and ONE ordered task list per deck (creation + Prioritise applied on the game tick).
    /// </summary>
    internal sealed class MuxAirOps : IAirOps
    {
        public const string AswType = "SH-3H";

        public readonly AswFakeGame Asw;
        public readonly CapFakeGame Cap;
        public readonly List<string> Calls = new List<string>();     // "<tick> order|allow|abort|prioritise <deck> <key> <window>"
        public int TickNo;
        private readonly Dictionary<int, List<string>> _order = new Dictionary<int, List<string>>();
        private readonly Dictionary<int, List<string>> _prio = new Dictionary<int, List<string>>();

        public MuxAirOps(AswFakeGame asw, CapFakeGame cap) { Asw = asw; Cap = cap; }

        private static bool IsAswKey(string key) { return key != null && (key.StartsWith("task:", StringComparison.Ordinal) || key.StartsWith("hangar:", StringComparison.Ordinal)); }
        private static string Win(string key) { return IsAswKey(key) ? "asw" : "cap"; }

        private List<string> Of(int deck) { List<string> l; if (!_order.TryGetValue(deck, out l)) { l = new List<string>(); _order[deck] = l; } return l; }
        private void Note(int deck, string key) { var l = Of(deck); if (key != null && !l.Contains(key)) l.Add(key); }
        private void Log(string what, int deck, string key) { Calls.Add(TickNo + " " + what + " " + deck + " " + key + " " + Win(key)); }

        public string DeckTimingsMode() { return Cap.DeckTimingsMode(); }
        public List<LoadoutOption> Loadouts(int deckUid, string vehicleType) { return vehicleType == AswType ? Asw.Loadouts(deckUid, vehicleType) : Cap.Loadouts(deckUid, vehicleType); }

        public string OrderLaunch(int deckUid, string vehicleType, string loadoutName, string missionType, bool allowLaunch, out string key, out double readyS)
        {
            string e = vehicleType == AswType ? Asw.OrderLaunch(deckUid, vehicleType, loadoutName, missionType, allowLaunch, out key, out readyS)
                                              : Cap.OrderLaunch(deckUid, vehicleType, loadoutName, missionType, allowLaunch, out key, out readyS);
            if (e == null) { Note(deckUid, key); Log(allowLaunch ? "order+allow" : "order", deckUid, key); }
            return e;
        }

        public string AllowLaunch(int deckUid, string key)
        {
            Note(deckUid, key); Log("allow", deckUid, key);
            return IsAswKey(key) ? Asw.AllowLaunch(deckUid, key) : Cap.AllowLaunch(deckUid, key);
        }

        public bool DeckInoperable(int deckUid) { return Cap.DeckInoperable(deckUid) || Asw.DeckInoperable(deckUid); }
        public string DeckDump(int deckUid) { return "asw[" + Asw.DeckDump(deckUid) + "] cap[" + Cap.DeckDump(deckUid) + "]"; }

        public string AbortLaunch(int deckUid, string key)
        {
            Log("abort", deckUid, key);
            return IsAswKey(key) ? Asw.AbortLaunch(deckUid, key) : Cap.AbortLaunch(deckUid, key);
        }

        public string Prioritise(int deckUid, string key)
        {
            Log("prioritise", deckUid, key);
            List<string> q;
            if (!_prio.TryGetValue(deckUid, out q)) { q = new List<string>(); _prio[deckUid] = q; }
            if (!q.Contains(key)) q.Add(key);
            return null;
        }

        /// <summary>The game tick: Prioritise requests are applied in call order, each one goes to the head.</summary>
        public void GameTick()
        {
            foreach (var kv in _prio)
            {
                var l = Of(kv.Key);
                foreach (string key in kv.Value) { l.Remove(key); l.Insert(0, key); }
                kv.Value.Clear();
            }
        }

        public List<AirDeckTask> DeckTasks(int deckUid)
        {
            var all = new List<AirDeckTask>();
            foreach (var t in Cap.DeckTasks(deckUid)) { t.State = t.State == "Prep" ? "ready_up" : "await_spawn"; all.Add(t); }
            foreach (var t in Asw.DeckTasks(deckUid)) all.Add(t);
            var res = new List<AirDeckTask>();
            foreach (string k in Of(deckUid)) foreach (var t in all) if (t.Key == k) res.Add(t);
            foreach (var t in all) if (!res.Contains(t)) { res.Add(t); Note(deckUid, t.Key); }
            return res;
        }

        /// <summary>Keys of the calls of the given kind in the order they reached the game: "key".</summary>
        public List<string> Keys(string kind)
        {
            var res = new List<string>();
            foreach (string c in Calls)
            {
                var p = c.Split(' ');
                if (p[1] == kind || (kind == "order" && p[1] == "order+allow")) res.Add(p[3]);
            }
            return res;
        }

        public int IndexOf(string kindPrefix, string window)
        {
            for (int i = 0; i < Calls.Count; i++)
            {
                var p = Calls[i].Split(' ');
                if (p[1].StartsWith(kindPrefix, StringComparison.Ordinal) && p[4] == window) return i;
            }
            return -1;
        }
    }
}
