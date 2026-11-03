using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-121 (ADR-0042): the gateway of "change the birds" requests to the deck broker, shared by the ASW controller (relief, SAR) and the
    /// CAP controller. One place for: the in-flight guard, the filter of the "reset" answer, the "deck broker is off" journal line (once a
    /// minute), the per-key pauses and the request factories. No game types, so every stand can compile it.
    /// </summary>
    internal sealed class AirRelief
    {
        private readonly string _window;
        private readonly Action<string> _log;
        private readonly string _logPrefix;
        private readonly HashSet<string> _inFlight = new HashSet<string>();   // requests submitted and not answered yet
        private readonly Dictionary<string, double> _paused = new Dictionary<string, double>();
        private double _noDeckLogAt = -1e18;

        /// <param name="window">"asw" | "cap": the window of every request.</param>
        /// <param name="log">The journal sink of the owner.</param>
        /// <param name="logPrefix">Prefix of the "deck broker is off" line ("asw " for ASW; "" for CAP, whose sink adds [Cap]).</param>
        public AirRelief(string window, Action<string> log, string logPrefix)
        {
            _window = window; _log = log; _logPrefix = logPrefix ?? "";
        }

        /// <summary>The deck broker (set by the air tick module). Null = nothing is submitted (a journal line once a minute).</summary>
        public IAirDeck Deck { get; set; }

        /// <summary>
        /// Queues a request; <paramref name="body"/> runs when the broker answers (same frame), not on "reset". False = not submitted
        /// (no broker, or the key is in flight). <paramref name="flightKey"/> null = no in-flight guard. <paramref name="prio"/> is evaluated only
        /// when the request goes out.
        /// </summary>
        public bool Submit(string flightKey, string group, Func<AirGroupPrio> prio, DeckRequest r, double now, Action<DeckResult> body)
        {
            if (Deck == null)
            {
                if (now - _noDeckLogAt >= 60) { _noDeckLogAt = now; _log(_logPrefix + "deck broker is off: " + group + " " + r.Kind + " not submitted"); }
                return false;
            }
            if (flightKey != null && !_inFlight.Add(flightKey)) return false;
            r.Window = _window; r.Group = group; r.Prio = prio();
            Deck.Submit(r, res =>
            {
                if (flightKey != null) _inFlight.Remove(flightKey);
                if (res == null || res.Err == "reset") return;
                body(res);
            });
            return true;
        }

        /// <summary>T-231: the deck is knocked out (the broker's per-tick cache); no broker or no deck = false.</summary>
        public bool DeckDown(int deckUid) { return Deck != null && deckUid != 0 && Deck.DeckDown(deckUid); }

        public bool InFlight(string flightKey) { return flightKey != null && _inFlight.Contains(flightKey); }

        /// <summary>True when the pause of the key (from the last <see cref="Mark"/>) is not over yet.</summary>
        public bool Paused(string key, double now, double everyS)
        {
            double last;
            return _paused.TryGetValue(key, out last) && now - last < everyS;
        }

        public void Mark(string key, double now) { _paused[key] = now; }

        /// <summary>True = may act now; the moment is remembered.</summary>
        public bool Due(string key, double now, double everyS)
        {
            if (Paused(key, now, everyS)) return false;
            Mark(key, now);
            return true;
        }

        /// <summary>Mission change: the in-flight keys and the pauses go (the "broker is off" line keeps its own clock).</summary>
        public void Reset() { _inFlight.Clear(); _paused.Clear(); }

        public static DeckRequest Allow(int deckUid, string key, string reason = null) { return new DeckRequest { DeckUid = deckUid, Kind = DeckKind.Allow, Key = key, Reason = reason }; }

        /// <param name="reason">Why the caller takes the task back (journal of the broker: "[Air] deck N abort ... reason=").</param>
        public static DeckRequest Abort(int deckUid, string key, string reason = null) { return new DeckRequest { DeckUid = deckUid, Kind = DeckKind.Abort, Key = key, Reason = reason }; }

        public static DeckRequest Order(int deckUid, string type, string loadout, string missionType, bool allowLaunch)
        {
            return new DeckRequest { DeckUid = deckUid, Kind = DeckKind.Order, Type = type, Loadout = loadout, MissionType = missionType, AllowLaunch = allowLaunch };
        }

        public static string HeldTag(DeckResult res) { return res.Held ? " held=" + Math.Round(res.HeldS).ToString("0", CultureInfo.InvariantCulture) + "s" : ""; }

        // ---- self-test (stand airtest, section "relief") -----------------------------------------------------------------

        private sealed class FakeDeck : IAirDeck
        {
            public readonly List<DeckRequest> Sent = new List<DeckRequest>();
            public readonly List<Action<DeckResult>> Done = new List<Action<DeckResult>>();
            public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids) { }
            public int Submit(DeckRequest r, Action<DeckResult> done) { Sent.Add(r); Done.Add(done); return Sent.Count; }
            public double HeldS(string window, string group) { return -1; }
            public bool DeckDown(int d) { return false; }
        }

        /// <summary>Seconds before a refused deck order is tried again on an unchanged deck (0.28.1 for SAR, T-187 for CAP): an empty hangar waits long
        /// (a machine has to come back), any other refusal doubles from 60 s up to 600 s.</summary>
        public static double OrderBackoffS(string err, int fails)
        {
            if (err == "no_airframes") return 600;
            double w = 60;
            for (int i = 1; i < fails && w < 600; i++) w *= 2;
            return Math.Min(600, w);
        }

        public static string OrderErrText(string err)
        {
            switch (err)
            {
                case "no_airframes": return "no machine of this type left in the hangar";
                case "no_callsign": return "the squadron has no free callsign";
                case "no_ammo": return "the deck has no ammunition points for this loadout";
                case "unknown_loadout": return "the loadout or vehicle type is unknown to the deck";
                case "unit_not_found": return "the deck ship is gone";
                default: return "refused by the game";
            }
        }

        public static string SelfTest()
        {
            var log = new List<string>();
            var rel = new AirRelief("asw", s => log.Add(s), "asw ");
            int bodies = 0;
            Func<AirGroupPrio> prio = () => new AirGroupPrio { Name = "g" };
            // no broker: false, one journal line per minute
            if (rel.Submit("k", "G1", prio, Order(7, "t", "l", null, true), 10, r => bodies++)) return "no broker submitted";
            if (rel.Submit("k", "G1", prio, Allow(7, "task:1"), 40, r => bodies++)) return "no broker submitted 2";
            if (log.Count != 1 || log[0] != "asw deck broker is off: G1 Order not submitted") return "broker-off line: " + string.Join("|", log);
            rel.Submit("k", "G1", prio, Allow(7, "task:1"), 71, r => bodies++);
            if (log.Count != 2) return "broker-off line after 60 s";
            // in flight: the second submit with the same key is refused until the answer
            var deck = new FakeDeck(); rel.Deck = deck;
            if (!rel.Submit("k", "G1", prio, Order(7, "t", "l", "mt", false), 100, r => bodies++)) return "submit";
            if (deck.Sent.Count != 1 || deck.Sent[0].Window != "asw" || deck.Sent[0].Group != "G1" || deck.Sent[0].Prio.Name != "g") return "request fields";
            if (!rel.InFlight("k")) return "in flight";
            if (rel.Submit("k", "G1", prio, Order(7, "t", "l", null, false), 101, r => bodies++)) return "second submit in flight";
            if (!rel.Submit("other", "G1", prio, Allow(7, "task:2"), 101, r => bodies++)) return "other key";
            deck.Done[0](new DeckResult { Key = "task:9" });
            if (bodies != 1 || rel.InFlight("k")) return "answer runs body, frees the key";
            // a reset answer does not run the body and frees the key
            if (!rel.Submit("k", "G1", prio, Allow(7, "task:3"), 102, r => bodies++)) return "resubmit";
            deck.Done[2](new DeckResult { Err = "reset" });
            if (bodies != 1 || rel.InFlight("k")) return "reset answer";
            deck.Done[2](null);
            // null key: no in-flight guard
            if (!rel.Submit(null, "G1", prio, Allow(7, "a"), 103, r => bodies++) || !rel.Submit(null, "G1", prio, Allow(7, "a"), 103, r => bodies++)) return "null key";
            // T-187: order back-off and refusal texts (shared by SAR and CAP)
            if (OrderBackoffS("launch_failed", 1) != 60 || OrderBackoffS("launch_failed", 3) != 240 || OrderBackoffS("launch_failed", 9) != 600 || OrderBackoffS("no_airframes", 1) != 600) return "order backoff";
            if (OrderErrText("no_airframes") != "no machine of this type left in the hangar" || OrderErrText("x") != "refused by the game") return "order err text";
            // pauses
            if (!rel.Due("launch5", 200, 120)) return "due first";
            if (rel.Due("launch5", 319, 120)) return "due inside the pause";
            if (!rel.Due("launch5", 320, 120)) return "due after the pause";
            if (rel.Paused("x", 0, 1) || !rel.Due("x", 0, 1)) return "paused unknown key";
            // reset
            rel.Reset();
            if (rel.InFlight("other") || rel.Paused("launch5", 321, 120)) return "reset";
            // factories
            var o = Order(3, "T", "L", "M", true);
            if (o.DeckUid != 3 || o.Kind != DeckKind.Order || o.Type != "T" || o.Loadout != "L" || o.MissionType != "M" || !o.AllowLaunch) return "Order fields";
            var a = Allow(4, "task:1");
            if (a.DeckUid != 4 || a.Kind != DeckKind.Allow || a.Key != "task:1") return "Allow fields";
            var ab = Abort(5, "task:2");
            if (ab.Kind != DeckKind.Abort || ab.Key != "task:2") return "Abort fields";
            if (HeldTag(new DeckResult()) != "" || HeldTag(new DeckResult { Held = true, HeldS = 12.4 }) != " held=12s") return "HeldTag";
            // CAP flavour: empty prefix
            var capLog = new List<string>();
            new AirRelief("cap", capLog.Add, null).Submit(null, "ПВО-1", prio, Allow(1, "k"), 0, r => { });
            if (capLog.Count != 1 || capLog[0] != "deck broker is off: ПВО-1 Allow not submitted") return "cap broker-off line";
            return null;
        }
    }
}
