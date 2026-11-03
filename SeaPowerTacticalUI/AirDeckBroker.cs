using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-119 / ADR-0042: the deck broker. Pure C#: no Unity, no reflection (the game is reached through IAirOps only).

    /// <summary>Numeric value = order in the sort (abort, then allow, then order).</summary>
    internal enum DeckKind { Abort = 0, Allow = 1, Order = 2 }

    internal sealed class DeckRequest
    {
        public int DeckUid;
        public string Window;          // "asw" (ASW and helicopter SAR) | "cap"
        public string Group;
        public DeckKind Kind;
        public string Type, Loadout, MissionType;   // Order; Loadout is one exact name
        public bool AllowLaunch;       // Order: createLaunchTask(..., allowLaunch)
        public string Key;             // Allow / Abort: "task:<uid>"
        public string Reason;          // Allow / Abort: why the caller asks (journal only)
        public AirGroupPrio Prio;      // facts at submit time; the Declare of the current tick wins at flush
        public int Ticket;             // set by Submit
    }

    internal sealed class DeckResult
    {
        public int Ticket;
        public string Err;             // null = ok; IAirOps codes, "reset"
        public string Key;             // Order: key of the created task
        public double ReadyS;
        public bool Held;              // Err == null: the allow is held by the broker (CAP yield); the broker releases it itself
        public double HeldS;           // how long it has waited (from the first hold of the key)
    }

    internal interface IAirDeck
    {
        /// <summary>Facts of a group for this tick: the tier (prio) and the decks it launches from. Decks of the group's requests are added automatically.</summary>
        void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids);
        /// <summary>Queues a request; done is called once, in the same frame, from EndTick/Flush. Returns the ticket.</summary>
        int Submit(DeckRequest r, Action<DeckResult> done);
        /// <summary>Max HeldS over the held tasks of the group; -1 = not waiting (window label).</summary>
        double HeldS(string window, string group);
        /// <summary>T-231: the flight deck is knocked out (cached by the broker per tick); unknown = false.</summary>
        bool DeckDown(int deckUid);
    }

    internal sealed class AirDeckParams { public double YieldMaxS = 120; public bool Debug; }

    internal sealed class AirDeckBroker : IAirDeck
    {
        private sealed class Pend
        {
            public DeckRequest R; public Action<DeckResult> Done;
            public bool Synth; public string Reason; public double HeldS;   // release of a held allow
            public int Tier, Seq;
        }

        private sealed class Decl { public AirGroupPrio Prio; public string Window; public HashSet<int> Decks = new HashSet<int>(); }
        private sealed class KeyInfo { public string Window, Group; public int Deck; public AirGroupPrio Prio; public double Added; public bool Seen; }
        private sealed class Hold { public int Deck; public string Key, Window, Group; public double Since; }

        private const double PrioRepeatS = 5;

        private readonly IAirOps _ops;
        private readonly AirOwnership _own;
        private readonly AirDeckParams _p;
        private readonly Action<string> _log;
        private readonly Action<string, string> _evt;

        private readonly List<Pend> _queue = new List<Pend>();
        private readonly Dictionary<string, Decl> _decl = new Dictionary<string, Decl>();
        private readonly Dictionary<string, KeyInfo> _keys = new Dictionary<string, KeyInfo>();     // deck\nkey
        private readonly Dictionary<string, Hold> _held = new Dictionary<string, Hold>();           // deck\nkey
        private readonly Dictionary<string, int> _released = new Dictionary<string, int>();         // deck\nkey -> deck
        private readonly Dictionary<string, double> _prioAt = new Dictionary<string, double>();     // deck\nkey -> last Prioritise
        private readonly Dictionary<int, string> _orderLine = new Dictionary<int, string>();
        private int _ticket;
        private double _now;
        private bool _noPrio;
        private readonly HashSet<string> _releasing = new HashSet<string>();
        // live 0.28.3: decks the mod has worked with and their last known health (an AS-4 hit knocked Eisenhower's deck out: the game cleared every task)
        private readonly HashSet<int> _watch = new HashSet<int>();
        private readonly Dictionary<int, bool> _down = new Dictionary<int, bool>();
        private readonly Dictionary<int, bool> _downNow = new Dictionary<int, bool>();   // per flush cache of IAirOps.DeckInoperable   // holds released in the current flush (a repeated allow of the key must not re-create the hold)

        public AirDeckBroker(IAirOps ops, AirOwnership own, AirDeckParams p, Action<string> log, Action<string, string> evt)
        {
            _ops = ops; _own = own ?? new AirOwnership(); _p = p ?? new AirDeckParams(); _log = log; _evt = evt;
        }

        public AirDeckParams P { get { return _p; } }

        private static string K(int deck, string key) { return deck + "\n" + key; }
        private static string G(string window, string group) { return window + "\n" + group; }
        private void Log(string line) { if (_log != null) try { _log(line); } catch (Exception) { } }
        private void Evt(string name, string detail) { if (_p.Debug && _evt != null) try { _evt(name, detail); } catch (Exception) { } }
        private static string F0(double v) { return Math.Round(v).ToString("0", CultureInfo.InvariantCulture); }

        // ---------------------------------------------------------------- IAirDeck

        public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids)
        {
            if (prio == null) return;
            var d = new Decl { Prio = prio, Window = window };
            if (deckUids != null) foreach (int u in deckUids) { d.Decks.Add(u); if (u != 0) _watch.Add(u); }
            _decl[G(window, prio.Name)] = d;
        }

        public int Submit(DeckRequest r, Action<DeckResult> done)
        {
            if (r == null) return 0;
            r.Ticket = ++_ticket;
            if (r.DeckUid != 0) _watch.Add(r.DeckUid);
            _queue.Add(new Pend { R = r, Done = done });
            return r.Ticket;
        }

        public double HeldS(string window, string group)
        {
            double best = -1;
            foreach (var h in _held.Values)
                if (h.Window == window && h.Group == group) best = Math.Max(best, Math.Max(0, _now - h.Since));
            return best;
        }

        // ---------------------------------------------------------------- tick

        public void BeginTick(double now) { _now = now; _decl.Clear(); }

        public void EndTick(double now)
        {
            _now = now;
            DeckHealth();
            Execute(true);
            Prioritise();
            OrderLog();
        }

        public void Flush(double now)
        {
            _now = now;
            Execute(false);
        }

        public void Reset()
        {
            var q = new List<Pend>(_queue);
            _queue.Clear();
            foreach (var p in q) Reply(p, new DeckResult { Ticket = p.R.Ticket, Err = "reset" });
            _decl.Clear(); _keys.Clear(); _held.Clear(); _released.Clear(); _prioAt.Clear(); _orderLine.Clear();
            _watch.Clear(); _down.Clear(); _downNow.Clear();
            _noPrio = false;
        }

        // ---------------------------------------------------------------- flag

        /// <summary>Decks (with the CAP groups under the flag) of this tick: a CAP group with a gap under threat and the decks it declared or ordered from.</summary>
        private Dictionary<int, List<AirGroupPrio>> FlaggedDecks()
        {
            var res = new Dictionary<int, List<AirGroupPrio>>();
            foreach (var d in _decl.Values)
            {
                if (d.Window != "cap" || !d.Prio.CapGapUnderThreat) continue;
                var decks = new HashSet<int>(d.Decks);
                foreach (var p in _queue) if (p.R.Window == "cap" && p.R.Group == d.Prio.Name) decks.Add(p.R.DeckUid);
                foreach (var kv in _keys) if (kv.Value.Window == "cap" && kv.Value.Group == d.Prio.Name) decks.Add(kv.Value.Deck);
                foreach (int u in decks)
                {
                    List<AirGroupPrio> l;
                    if (!res.TryGetValue(u, out l)) { l = new List<AirGroupPrio>(); res[u] = l; }
                    l.Add(d.Prio);
                }
            }
            return res;
        }

        private AirGroupPrio PrioOf(string window, string group, AirGroupPrio fallback)
        {
            Decl d;
            if (_decl.TryGetValue(G(window, group), out d)) return d.Prio;
            return fallback;
        }

        // ---------------------------------------------------------------- flush

        private void Reply(Pend p, DeckResult r)
        {
            if (p.Done == null) return;
            try { p.Done(r); } catch (Exception) { }
        }

        private void Execute(bool tick)
        {
            if (!tick) _downNow.Clear();
            var flagged = FlaggedDecks();
            var work = new List<Pend>(_queue);
            _queue.Clear();
            _releasing.Clear();
            if (tick) ReleaseHolds(flagged, work);
            if (work.Count == 0) return;
            foreach (var p in work)
            {
                var pr = PrioOf(p.R.Window, p.R.Group, p.R.Prio);
                p.Tier = AirOpsMath.Tier(pr);
                p.Seq = pr == null ? 0 : pr.WindowIndex;
            }
            var decks = new SortedDictionary<int, List<Pend>>();
            foreach (var p in work)
            {
                List<Pend> l;
                if (!decks.TryGetValue(p.R.DeckUid, out l)) { l = new List<Pend>(); decks[p.R.DeckUid] = l; }
                l.Add(p);
            }
            foreach (var kv in decks)
            {
                var keys = new List<DeckReqKey>();
                foreach (var p in kv.Value)
                    keys.Add(new DeckReqKey { Tier = p.Tier, Seq = p.Seq, Kind = (int)p.R.Kind, Ticket = p.R.Ticket, Window = p.R.Window, Group = p.R.Group });
                bool flag = flagged.ContainsKey(kv.Key);
                foreach (int i in AirOpsMath.FlushOrder(keys)) Run(kv.Value[i], flag);
            }
        }

        private void ReleaseHolds(Dictionary<int, List<AirGroupPrio>> flagged, List<Pend> work)
        {
            if (_held.Count == 0) return;
            var gone = new List<string>();
            foreach (var kv in _held)
            {
                var h = kv.Value;
                double held = Math.Max(0, _now - h.Since);
                string reason = null;
                if (!flagged.ContainsKey(h.Deck)) reason = "flag_off";
                else if (_p.YieldMaxS <= 0 || held >= _p.YieldMaxS) reason = "timeout";
                if (reason == null) continue;
                gone.Add(kv.Key);
                _releasing.Add(kv.Key);
                work.Add(new Pend
                {
                    Synth = true, Reason = reason, HeldS = held,
                    R = new DeckRequest { DeckUid = h.Deck, Window = h.Window, Group = h.Group, Kind = DeckKind.Allow, Key = h.Key, Prio = PrioOf(h.Window, h.Group, null), Ticket = ++_ticket }
                });
            }
            foreach (var k in gone) _held.Remove(k);
        }

        private bool ShouldHold(DeckRequest r, bool flag)
        {
            return flag && r.Window == "asw" && _p.YieldMaxS > 0 && !_released.ContainsKey(K(r.DeckUid, r.Key ?? ""));
        }

        private void Register(DeckRequest r, string key, AirGroupPrio pr)
        {
            if (key == null) return;
            string k = K(r.DeckUid, key);
            KeyInfo ki;
            if (!_keys.TryGetValue(k, out ki)) { ki = new KeyInfo { Added = _now }; _keys[k] = ki; }
            ki.Window = r.Window; ki.Group = r.Group; ki.Deck = r.DeckUid; ki.Prio = pr ?? ki.Prio;
        }

        private void Run(Pend p, bool flag)
        {
            var r = p.R;
            var res = new DeckResult { Ticket = r.Ticket };
            var pr = PrioOf(r.Window, r.Group, r.Prio);
            switch (r.Kind)
            {
                case DeckKind.Abort:
                {
                    Register(r, r.Key, pr);
                    _held.Remove(K(r.DeckUid, r.Key ?? ""));   // an abort never waits and drops the hold silently
                    res.Err = Safe(() => _ops.AbortLaunch(r.DeckUid, r.Key));
                    Log("[Air] deck " + r.DeckUid + " abort " + r.Key + " by " + r.Window + "/" + r.Group + " why=" + (r.Reason ?? "-") + " -> " + (res.Err ?? "ok"));
                    if (res.Err == null || res.Err == "deck_task_not_found" || res.Err == "not_ours") Forget(r.DeckUid, r.Key);
                    break;
                }
                case DeckKind.Allow:
                {
                    Register(r, r.Key, pr);
                    string hk = K(r.DeckUid, r.Key ?? "");
                    Hold h;
                    if (!p.Synth && _releasing.Contains(hk)) break;   // the release of this key runs in this flush (Err stays null)
                    if (!p.Synth && ShouldHold(r, flag))
                    {
                        if (!_held.TryGetValue(hk, out h))
                        {
                            h = new Hold { Deck = r.DeckUid, Key = r.Key, Window = r.Window, Group = r.Group, Since = _now };
                            _held[hk] = h;
                        }
                        res.Held = true; res.HeldS = Math.Max(0, _now - h.Since);
                        break;
                    }
                    double heldS = p.Synth ? p.HeldS : 0;
                    string reason = p.Synth ? p.Reason : null;
                    if (!p.Synth && _held.TryGetValue(hk, out h))
                    {
                        // a repeated allow of a held key, but the flag is gone or the time is up: release it now
                        heldS = Math.Max(0, _now - h.Since);
                        reason = !flag ? "flag_off" : "timeout";
                        _held.Remove(hk);
                    }
                    res.Err = DeckDown(r.DeckUid) ? "deck_inoperable" : Safe(() => _ops.AllowLaunch(r.DeckUid, r.Key));
                    Log("[Air] deck " + r.DeckUid + " allow " + r.Key + " by " + r.Window + "/" + r.Group + " why=" + (reason ?? r.Reason ?? "request") + " -> " + (res.Err ?? "ok"));
                    if (reason != null)
                    {
                        _released[hk] = r.DeckUid;
                        if (res.Err == "deck_task_not_found")
                            Log("[Air] deck " + r.DeckUid + " yield " + r.Group + " task_gone key=" + r.Key);
                        else
                        {
                            Log("[Air] deck " + r.DeckUid + " yield " + r.Group + " held=" + F0(heldS) + "s reason=" + reason);
                            Evt("deck_yield", "deck=" + r.DeckUid + " group=" + r.Group + " held=" + F0(heldS) + " reason=" + reason);
                        }
                    }
                    break;
                }
                default:
                {
                    if (DeckDown(r.DeckUid))
                    {
                        // the game runs no task of a knocked-out deck: a new one would sit in its first state for good (live 0.28.3: CAP re-ordered 4 F-14s, "1800 s" forever)
                        res.Err = "deck_inoperable";
                        Log("[Air] deck " + r.DeckUid + " order " + r.Type + " by " + r.Window + "/" + r.Group + " -> deck_inoperable");
                        break;
                    }
                    bool hold = r.AllowLaunch && r.Window == "asw" && flag && _p.YieldMaxS > 0;
                    string key = null; double ready = 0;
                    res.Err = Safe(() => { string k; double rd; string e = _ops.OrderLaunch(r.DeckUid, r.Type, r.Loadout, r.MissionType, r.AllowLaunch && !hold, out k, out rd); key = k; ready = rd; return e; });
                    if (res.Err == null)
                    {
                        res.Key = key; res.ReadyS = ready;
                        Register(r, key, pr);
                        if (hold && key != null)
                        {
                            _held[K(r.DeckUid, key)] = new Hold { Deck = r.DeckUid, Key = key, Window = r.Window, Group = r.Group, Since = _now };
                            res.Held = true; res.HeldS = 0;
                        }
                    }
                    break;
                }
            }
            Reply(p, res);
        }

        private void Forget(int deck, string key)
        {
            if (key == null) return;
            _keys.Remove(K(deck, key)); _held.Remove(K(deck, key)); _released.Remove(K(deck, key)); _prioAt.Remove(K(deck, key));
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); }
            catch (Exception e) { return "launch_failed:" + e.GetType().Name; }
        }

        // ---------------------------------------------------------------- deck health

        /// <summary>The deck is knocked out (cached for the current flush). Unknown = operable.</summary>
        public bool DeckDown(int deck)
        {
            if (deck == 0) return false;
            bool v;
            if (_downNow.TryGetValue(deck, out v)) return v;
            try { v = _ops.DeckInoperable(deck); } catch (Exception) { v = false; }
            _downNow[deck] = v;
            return v;
        }

        /// <summary>Once per tick: a journal line (and an event) when a deck the mod works with goes out of action or comes back.</summary>
        private void DeckHealth()
        {
            _downNow.Clear();
            foreach (var ki in _keys.Values) if (ki.Deck != 0) _watch.Add(ki.Deck);
            foreach (int deck in _watch)
            {
                bool now = DeckDown(deck), was;
                if (_down.TryGetValue(deck, out was) && was == now) continue;
                bool first = !_down.ContainsKey(deck);
                _down[deck] = now;
                if (first && !now) continue;
                if (now)
                {
                    string dump; try { dump = _ops.DeckDump(deck); } catch (Exception e) { dump = "error:" + e.GetType().Name; }
                    Log("[Air] deck " + deck + " inoperable: flight deck knocked out, the game cleared its tasks; no orders until it is repaired deck_tasks=" + dump);
                    Evt("deck_down", "deck=" + deck);
                }
                else
                {
                    Log("[Air] deck " + deck + " operable again");
                    Evt("deck_up", "deck=" + deck);
                }
            }
        }

        // ---------------------------------------------------------------- prioritise

        private List<AirDeckTask> TasksOf(int deck, Dictionary<int, List<AirDeckTask>> cache)
        {
            List<AirDeckTask> l;
            if (cache.TryGetValue(deck, out l)) return l;
            try { l = _ops.DeckTasks(deck) ?? new List<AirDeckTask>(); } catch (Exception) { l = new List<AirDeckTask>(); }
            cache[deck] = l;
            return l;
        }

        private readonly Dictionary<int, List<AirDeckTask>> _cache = new Dictionary<int, List<AirDeckTask>>();

        private void Prioritise()
        {
            _cache.Clear();
            if (_noPrio) return;
            var flagged = FlaggedDecks();
            foreach (var kv in flagged)
            {
                int deck = kv.Key;
                if (DeckDown(deck)) continue;
                var groups = kv.Value;
                var rankOf = new Dictionary<string, int>();
                var names = AirOpsMath.DeckOrder(groups);
                for (int i = 0; i < names.Count; i++) rankOf[names[i]] = i;
                var tasks = TasksOf(deck, _cache);
                Func<string, int> rank = key =>
                {
                    KeyInfo ki;
                    int rk;
                    if (!_keys.TryGetValue(K(deck, key), out ki) || ki.Window != "cap") return -1;
                    return rankOf.TryGetValue(ki.Group ?? "", out rk) ? rk : -1;
                };
                Func<string, bool> yielding = key =>
                {
                    KeyInfo ki;
                    return _keys.TryGetValue(K(deck, key), out ki) && ki.Window == "asw";
                };
                var calls = AirOpsMath.PrioritiseCalls(tasks, rank, yielding);
                if (calls.Count == 0) continue;
                bool throttled = false;
                foreach (var key in calls)
                {
                    double at;
                    if (_prioAt.TryGetValue(K(deck, key), out at) && _now - at < PrioRepeatS) throttled = true;
                }
                if (throttled) continue;
                var logged = new HashSet<string>();
                foreach (var key in calls)
                {
                    string err = Safe(() => _ops.Prioritise(deck, key));
                    if (err == "unsupported")
                    {
                        _noPrio = true;
                        Log("[Air] deck " + deck + " prioritise unsupported (member missing); no reordering this mission");
                        return;
                    }
                    KeyInfo ki = _keys[K(deck, key)];
                    Log("[Air] deck " + deck + " prioritise " + key + " by " + ki.Window + "/" + ki.Group + " why=cap_gap_under_threat -> " + (err ?? "ok"));
                    if (err != null) continue;
                    _prioAt[K(deck, key)] = _now;
                    if (logged.Add(ki.Group ?? ""))
                    {
                        Log("[Air] deck " + deck + " prioritise " + ki.Group);
                        Evt("deck_prioritise", "deck=" + deck + " group=" + ki.Group);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- order journal

        private void OrderLog()
        {
            var decks = new SortedDictionary<int, bool>();
            foreach (var ki in _keys.Values) decks[ki.Deck] = true;
            foreach (int deck in new List<int>(decks.Keys))
            {
                var tasks = TasksOf(deck, _cache);
                var present = new HashSet<string>();
                foreach (var t in tasks) if (t != null && t.Key != null) present.Add(t.Key);
                var sb = new StringBuilder();
                var drop = new List<string>();
                foreach (var kv in _keys)
                {
                    var ki = kv.Value;
                    if (ki.Deck != deck) continue;
                    string key = kv.Key.Substring(kv.Key.IndexOf('\n') + 1);
                    if (present.Contains(key)) ki.Seen = true;
                    else if (ki.Seen || _now - ki.Added > 60) drop.Add(kv.Key);
                }
                if (drop.Count > 0)
                {
                    // who removed a task the mod ordered? the deck's whole list at this moment (a launched task is a LaunchTask now; an empty deck
                    // with "inoperable=True" is FlightDeck.KillAircraft; a refund without either is an abort)
                    var gone = new StringBuilder();
                    foreach (var d in drop)
                    {
                        var gk = _keys[d];
                        if (!gk.Seen) continue;
                        if (gone.Length > 0) gone.Append(' ');
                        gone.Append(d.Substring(d.IndexOf('\n') + 1)).Append('(').Append(gk.Window).Append('/').Append(gk.Group).Append(')');
                    }
                    if (gone.Length > 0)
                    {
                        string dump; try { dump = _ops.DeckDump(deck); } catch (Exception e) { dump = "error:" + e.GetType().Name; }
                        Log("[Air] deck " + deck + " task gone " + gone + " inoperable=" + DeckDown(deck) + " deck_tasks=" + dump);
                    }
                }
                foreach (var d in drop) { _keys.Remove(d); _held.Remove(d); _prioAt.Remove(d); }
                foreach (var t in tasks)
                {
                    KeyInfo ki;
                    if (t == null || t.Key == null || !_keys.TryGetValue(K(deck, t.Key), out ki)) continue;
                    var pr = PrioOf(ki.Window, ki.Group, ki.Prio);
                    int seq = pr != null ? pr.WindowIndex : _own.GroupSeq(ki.Window, ki.Group);
                    char c = ki.Window == "cap" ? 'C' : (pr != null && pr.Order == "sar" ? 'S' : 'A');
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(c).Append(seq.ToString(CultureInfo.InvariantCulture)).Append("(t").Append(AirOpsMath.Tier(pr).ToString(CultureInfo.InvariantCulture)).Append(')');
                }
                // released keys of vanished tasks
                var rel = new List<string>();
                foreach (var kv in _released)
                    if (kv.Value == deck && !present.Contains(kv.Key.Substring(kv.Key.IndexOf('\n') + 1))) rel.Add(kv.Key);
                foreach (var k in rel) _released.Remove(k);
                string line = sb.ToString();
                string prev;
                if (line.Length == 0) { _orderLine.Remove(deck); continue; }
                if (_orderLine.TryGetValue(deck, out prev) && prev == line) continue;
                _orderLine[deck] = line;
                Log("[Air] deck " + deck + " order: " + line);
                Evt("deck_order", "deck=" + deck + " order=" + line);
            }
        }

        // ---------------------------------------------------------------- diag (T-139 section "deck")

        /// <summary>Optional: ship name of a deck for the diag section (set by the air tick module).</summary>
        public Func<int, string> DeckName;

        /// <summary>Read-only snapshot for the dev mod (IUiDiag "deck"): per deck our keys with tier and hold, the earliest yield deadline, the last order line. No game calls.</summary>
        public string DiagJson(int limit)
        {
            var decks = new SortedDictionary<int, bool>();
            foreach (var ki in _keys.Values) decks[ki.Deck] = true;
            foreach (var h in _held.Values) decks[h.Deck] = true;
            foreach (int d in _orderLine.Keys) decks[d] = true;
            var sb = new StringBuilder("{\"decks\":[");
            bool firstDeck = true;
            foreach (int deck in decks.Keys)
            {
                if (!firstDeck) sb.Append(',');
                firstDeck = false;
                string name = null;
                if (DeckName != null) try { name = DeckName(deck); } catch (Exception) { }
                sb.Append("{\"ship_uid\":").Append(deck.ToString(CultureInfo.InvariantCulture)).Append(",\"ship\":").Append(Json.Str(name ?? "")).Append(",\"queue\":[");
                int n = 0;
                double until = -1;
                foreach (var kv in _keys)
                {
                    var ki = kv.Value;
                    if (ki.Deck != deck) continue;
                    if (limit > 0 && n >= limit) break;
                    string key = kv.Key.Substring(kv.Key.IndexOf('\n') + 1);
                    var pr = PrioOf(ki.Window, ki.Group, ki.Prio);
                    Hold h;
                    bool held = _held.TryGetValue(kv.Key, out h);
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"key\":").Append(Json.Str(key)).Append(",\"kind\":").Append(Json.Str(ki.Window == "cap" ? "cap" : (pr != null && pr.Order == "sar" ? "sar" : "asw")));
                    sb.Append(",\"group\":").Append(Json.Str(ki.Group ?? "")).Append(",\"prio\":").Append(AirOpsMath.Tier(pr).ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"hold_s\":").Append(held ? Json.Num(Math.Round(Math.Max(0, _now - h.Since))) : "-1").Append('}');
                }
                foreach (var h in _held.Values)
                {
                    if (h.Deck != deck || _p.YieldMaxS <= 0) continue;
                    double left = Math.Max(0, h.Since + _p.YieldMaxS - _now);
                    if (until < 0 || left < until) until = left;
                }
                sb.Append("],\"yield_until_s\":").Append(until < 0 ? "-1" : Json.Num(Math.Round(until))).Append(",\"last_order\":");
                string line;
                if (_orderLine.TryGetValue(deck, out line)) sb.Append(Json.Str(line)); else sb.Append("\"\"");
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }

        // ---------------------------------------------------------------- selftest

        private sealed class SelfTestOps : IAirOps
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<AirDeckTask> Tasks = new List<AirDeckTask>();
            private int _n;
            public string DeckTimingsMode() { return "Realistic"; }
            public List<LoadoutOption> Loadouts(int deckUid, string vehicleType) { return new List<LoadoutOption>(); }
            public List<AirDeckTask> DeckTasks(int deckUid) { return new List<AirDeckTask>(Tasks); }
            public string OrderLaunch(int deckUid, string vehicleType, string loadoutName, string missionType, bool allowLaunch, out string key, out double readyS)
            {
                key = "task:" + (++_n); readyS = 10;
                Tasks.Add(new AirDeckTask { Key = key, ShipUid = deckUid, Type = vehicleType, Loadout = loadoutName, State = "ready_up", Ours = true });
                Calls.Add("order " + deckUid + " " + key + (allowLaunch ? " allow" : ""));
                return null;
            }
            public string AllowLaunch(int deckUid, string key) { Calls.Add("allow " + deckUid + " " + key); return null; }
            public string AbortLaunch(int deckUid, string key) { Calls.Add("abort " + deckUid + " " + key); return null; }
            public bool Down;
            public bool DeckInoperable(int deckUid) { return Down; }
            public string DeckDump(int deckUid) { return Tasks.Count == 0 ? "-" : string.Join(" ", Tasks.ConvertAll(t => t.Key)); }
            public string Prioritise(int deckUid, string key)
            {
                Calls.Add("prio " + deckUid + " " + key);
                for (int i = 0; i < Tasks.Count; i++) if (Tasks[i].Key == key) { var t = Tasks[i]; Tasks.RemoveAt(i); Tasks.Insert(0, t); break; }
                return null;
            }
        }

        private static DeckRequest Req(string win, string grp, DeckKind k, string key, bool allow, AirGroupPrio pr)
        {
            return new DeckRequest { DeckUid = 1, Window = win, Group = grp, Kind = k, Key = key, Type = "x", Loadout = "L", AllowLaunch = allow, Prio = pr };
        }

        /// <summary>null = ok. The detailed scenarios live in the airtest stand; this is the quick check that also runs in selfcheck ui.</summary>
        public static string SelfTest()
        {
            var ops = new SelfTestOps();
            var logs = new List<string>();
            var br = new AirDeckBroker(ops, new AirOwnership(), new AirDeckParams { YieldMaxS = 120 }, logs.Add, null);
            var capP = new AirGroupPrio { Name = "C", Order = "cap", CapGapUnderThreat = true, WindowIndex = 2, Window = "cap" };
            var aswP = new AirGroupPrio { Name = "A", Order = "asw", HasContact = true, WindowIndex = 1, Window = "asw" };
            int done = 0; var ress = new List<DeckResult>();
            Action<DeckResult> cb = r => { done++; ress.Add(r); };

            br.BeginTick(0);
            br.Declare("cap", capP, new[] { 1 }); br.Declare("asw", aswP, new[] { 1 });
            br.Submit(Req("asw", "A", DeckKind.Order, null, true, aswP), cb);
            br.Submit(Req("cap", "C", DeckKind.Order, null, true, capP), cb);
            if (done != 0) return "done before EndTick";
            br.EndTick(0);
            if (done != 2) return "done count " + done;
            if (ops.Calls.Count != 2 || !ops.Calls[0].StartsWith("order 1 task:1 allow") || ops.Calls[1] != "order 1 task:2") return "order/hold calls: " + string.Join("|", ops.Calls);
            if (!ress[1].Held || ress[1].Key != "task:2") return "asw order not held";
            if (br.HeldS("asw", "A") < 0) return "HeldS";
            ops.Calls.Clear();
            br.BeginTick(30);
            br.Declare("asw", aswP, new[] { 1 });
            br.EndTick(30);   // flag gone -> release
            if (ops.Calls.Count != 1 || ops.Calls[0] != "allow 1 task:2") return "release calls: " + string.Join("|", ops.Calls);
            if (br.HeldS("asw", "A") != -1) return "HeldS after release";
            bool yl = false; foreach (var l in logs) if (l.Contains("yield A held=30s reason=flag_off")) yl = true;
            if (!yl) return "yield log";
            br.Reset();
            br.Submit(Req("cap", "C", DeckKind.Abort, "task:1", false, capP), cb);
            br.Reset();
            if (ress[ress.Count - 1].Err != "reset") return "reset";
            // live 0.28.3: a knocked-out deck takes no order / allow; the change is journaled once each way; a vanished task is journaled with the deck list
            ops.Calls.Clear(); logs.Clear(); ops.Down = true;
            br.BeginTick(40);
            br.Declare("cap", capP, new[] { 1 });
            br.Submit(Req("cap", "C", DeckKind.Order, null, true, capP), cb);
            br.EndTick(40);
            if (ress[ress.Count - 1].Err != "deck_inoperable" || ops.Calls.Count != 0) return "down order: " + ress[ress.Count - 1].Err + " " + string.Join("|", ops.Calls);
            bool dl = false; foreach (var l in logs) if (l.StartsWith("[Air] deck 1 inoperable")) dl = true;
            if (!dl) return "down log";
            ops.Down = false;
            br.BeginTick(41); br.Declare("cap", capP, new[] { 1 }); br.EndTick(41);
            bool ul = false; foreach (var l in logs) if (l == "[Air] deck 1 operable again") ul = true;
            if (!ul) return "up log";
            return null;
        }
    }

    /// <summary>
    /// One step of 1 s of game time and at least MinRealS of real time (T-126: at x16 a game second passes in ~62 ms, so the game-time rule alone fires almost every frame).
    /// Due: both gaps are over, or the game time went back (load) -> the base moves to t.
    /// </summary>
    internal sealed class AirTickClock
    {
        public const double MinRealS = 0.25;
        private double _last = double.NaN;
        private double _lastReal = double.NaN;

        public bool Due(double gameT, double realT)
        {
            if (double.IsNaN(_last) || gameT < _last
                || (gameT - _last >= 1.0 - 1e-9 && (double.IsNaN(_lastReal) || realT < _lastReal || realT - _lastReal >= MinRealS - 1e-9)))
            {
                _last = gameT; _lastReal = realT; return true;
            }
            return false;
        }

        public void Reset() { _last = double.NaN; _lastReal = double.NaN; }

        public static string SelfTest()
        {
            var c = new AirTickClock();
            if (!c.Due(10, 0)) return "first";
            if (c.Due(10.5, 1)) return "half";
            if (!c.Due(11, 2)) return "one";
            if (c.Due(11.9, 3)) return "base moved";
            if (!c.Due(5, 4)) return "back";
            if (c.Due(5.5, 5)) return "new base";
            if (!c.Due(6, 6)) return "after back";
            c.Reset();
            if (!c.Due(6.1, 7)) return "reset";
            // x16: a game second per ~62 ms of real time; the real floor holds the steps back
            c.Reset();
            int n = 0;
            for (int i = 0; i < 60; i++) if (c.Due(100 + i * 0.53, i * 0.033)) n++;
            if (n > 9 || n < 6) return "x16 steps: " + n;
            return null;
        }
    }
}
