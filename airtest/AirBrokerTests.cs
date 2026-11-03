using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Broker scenarios of ADR-0042 on the fake deck. Each section returns null = ok.</summary>
    internal static class AirBrokerTests
    {
        private sealed class H
        {
            public readonly FakeDeck F = new FakeDeck();
            public readonly List<string> Logs = new List<string>(), Evts = new List<string>();
            public readonly AirDeckBroker B;
            public H(double yieldMax = 120, bool debug = false)
            {
                B = new AirDeckBroker(F, new AirOwnership(), new AirDeckParams { YieldMaxS = yieldMax, Debug = debug }, Logs.Add, (n, d) => Evts.Add(n + " " + d));
            }
            public bool Log(string part) { foreach (var l in Logs) if (l.Contains(part)) return true; return false; }
            public int LogCount(string part) { int n = 0; foreach (var l in Logs) if (l.Contains(part)) n++; return n; }
            public string Calls() { return string.Join("|", F.Calls); }
        }

        private static AirGroupPrio Cap(string n, int seq, bool flag) { return new AirGroupPrio { Name = n, Order = "cap", CapGapUnderThreat = flag, WindowIndex = seq, Window = "cap" }; }
        private static AirGroupPrio Asw(string n, int seq, bool contact, string order = "asw") { return new AirGroupPrio { Name = n, Order = order, HasContact = contact, WindowIndex = seq, Window = "asw" }; }

        private static DeckRequest Rq(int deck, AirGroupPrio pr, DeckKind k, string key = null, bool allow = false)
        {
            return new DeckRequest { DeckUid = deck, Window = pr.Window, Group = pr.Name, Kind = k, Key = key, Type = "heli", Loadout = "L", AllowLaunch = allow, Prio = pr };
        }

        private sealed class Rs { public readonly List<DeckResult> L = new List<DeckResult>(); public Action<DeckResult> Cb { get { return L.Add; } } public DeckResult Last { get { return L[L.Count - 1]; } } }

        private static string Join(IEnumerable<string> l) { return string.Join("|", l); }

        // ------------------------------------------------------------------ owner

        public static string Owner()
        {
            var o = new AirOwnership();
            o.RestoreSeq("cap", "C1", 4); o.RestoreSeq("asw", "A1", 2);
            if (o.GroupSeq("cap", "C1") != 4 || o.GroupSeq("asw", "A1") != 2) return "restored numbers";
            if (o.GroupSeq("asw", "A2") != 5) return "next after restore";
            return null;
        }

        // ------------------------------------------------------------------ broker

        public static string Broker()
        {
            // 1. two asw + one cap on one deck: FlushOrder (tier 2 all: seq 1 asw A, 2 cap C, 3 asw B)
            {
                var h = new H(); var rs = new Rs();
                var a = Asw("A", 1, false); var c = Cap("C", 2, false); var b = Asw("B", 3, false);
                h.B.BeginTick(0);
                h.B.Declare("asw", b, new[] { 1 }); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, b, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, c, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb);
                if (rs.L.Count != 0) return "done before EndTick";
                h.B.EndTick(0);
                if (rs.L.Count != 3) return "done count " + rs.L.Count;
                if (h.Calls() != "order 1 task:101|order 1 task:102|order 1 task:103") return "calls " + h.Calls();
                if (rs.L[0].Ticket != 3 || rs.L[1].Ticket != 2 || rs.L[2].Ticket != 1) return "result order (A, C, B)";
                if (h.F.Of(1)[0].Loadout != "L") return "loadout passed";
                h.B.EndTick(1);
                if (rs.L.Count != 3) return "done called twice";
            }
            // 2. abort -> allow -> order of one group
            {
                var h = new H(); var rs = new Rs();
                var a = Asw("A", 1, false);
                string k1, k2; double r;
                h.F.OrderLaunch(1, "heli", "L", null, false, out k1, out r); h.F.OrderLaunch(1, "heli", "L", null, false, out k2, out r); h.F.Calls.Clear();
                h.B.BeginTick(0); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, a, DeckKind.Allow, k1), rs.Cb); h.B.Submit(Rq(1, a, DeckKind.Abort, k2), rs.Cb);
                h.B.EndTick(0);
                if (h.Calls() != "abort 1 " + k2 + "|allow 1 " + k1 + "|order 1 task:103") return "kind order: " + h.Calls();
            }
            // 2b. live 0.28.3: the deck knocked out (FlightDeck.KillAircraft cleared every task): the vanish is journaled with the deck state,
            //     orders and allows are refused without a game call, aborts still go out and are journaled with the caller's reason
            {
                var h = new H(); var rs = new Rs();
                var a = Asw("A", 1, false); var c = Cap("C", 2, false);
                h.B.BeginTick(0); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", c, new[] { 1 });
                h.B.Submit(Rq(1, c, DeckKind.Order, null, true), rs.Cb);
                h.B.EndTick(0);
                string ck = rs.Last.Key;
                h.B.BeginTick(1); h.B.EndTick(1);   // seen on the deck
                h.F.Of(1).Clear(); h.F.Down.Add(1);
                h.B.BeginTick(2); h.B.Declare("cap", c, new[] { 1 }); h.B.EndTick(2);
                if (!h.Log("[Air] deck 1 inoperable")) return "down not journaled: " + Join(h.Logs);
                if (!h.Log("[Air] deck 1 task gone " + ck + "(cap/C) inoperable=True deck_tasks=-")) return "vanish not journaled: " + Join(h.Logs);
                h.F.Calls.Clear();
                h.B.BeginTick(3); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, a, DeckKind.Order, null, true), rs.Cb);
                h.B.EndTick(3);
                if (rs.Last.Err != "deck_inoperable" || h.F.Calls.Count != 0) return "order on a dead deck: " + rs.Last.Err + " " + h.Calls();
                h.B.BeginTick(4);
                var ab = Rq(1, c, DeckKind.Abort, "task:999"); ab.Reason = "cap stop";
                h.B.Submit(ab, rs.Cb);
                h.B.EndTick(4);
                if (h.Calls() != "abort 1 task:999" || !h.Log("[Air] deck 1 abort task:999 by cap/C why=cap stop -> deck_task_not_found")) return "abort journal: " + Join(h.Logs);
                if (h.LogCount("[Air] deck 1 inoperable") != 1) return "down journaled twice";
                h.F.Down.Clear();
                h.B.BeginTick(5); h.B.Declare("asw", a, new[] { 1 }); h.B.EndTick(5);
                if (!h.Log("[Air] deck 1 operable again")) return "up not journaled";
            }
            // 3. two decks sort independently
            {
                var h = new H(); var rs = new Rs();
                var c = Cap("C", 5, true); var a = Asw("A", 1, true);
                h.B.BeginTick(0); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1, 2 });
                h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb); h.B.Submit(Rq(2, a, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, c, DeckKind.Order), rs.Cb);
                h.B.EndTick(0);
                // deck 1: cap (tier 0) first, then asw; deck 2 has no flag, asw goes alone
                if (h.Calls() != "order 1 task:101|order 1 task:102|order 2 task:103") return "decks: " + h.Calls();
                if (h.F.Of(1)[0].Type != "heli") return "deck1 content";
            }
            // 4. a request outside a tick goes out on Flush
            {
                var h = new H(); var rs = new Rs(); var a = Asw("A", 1, false);
                h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb);
                if (h.F.Calls.Count != 0 || rs.L.Count != 0) return "executed before Flush";
                h.B.Flush(5);
                if (h.F.Calls.Count != 1 || rs.L.Count != 1 || rs.Last.Err != null || rs.Last.Key == null) return "flush";
                h.B.Flush(6);
                if (rs.L.Count != 1) return "flush twice";
            }
            // 5. Reset answers "reset"
            {
                var h = new H(); var rs = new Rs(); var a = Asw("A", 1, false);
                h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb);
                h.B.Reset();
                if (rs.L.Count != 1 || rs.Last.Err != "reset") return "reset result";
                h.B.EndTick(1);
                if (h.F.Calls.Count != 0) return "queue not cleared";
            }
            // 6. the Declare of this tick beats r.Prio
            {
                var h = new H(); var rs = new Rs();
                var aOld = Asw("A", 1, false); var cOld = Cap("C", 2, false);
                h.B.BeginTick(0);
                h.B.Declare("cap", Cap("C", 2, true), new[] { 1 }); h.B.Declare("asw", Asw("A", 1, true), new[] { 1 });
                h.B.Submit(Rq(1, aOld, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, cOld, DeckKind.Order), rs.Cb);
                h.B.EndTick(0);
                if (h.Calls() != "order 1 task:101|order 1 task:102" || rs.L[0].Key != "task:101" || h.F.Of(1)[0].Type != "heli") return "declare wins (cap first): " + h.Calls();
                // cap is Ticket 2, so it must be the first result
                if (rs.L[0].Ticket != 2) return "declare wins: cap result first";
            }
            // 7. order journal: on change only; format C1(t0) A2(t1) S3(t2)
            {
                var h = new H();
                var c = Cap("C", 1, true); var a = Asw("A", 2, true); var s = Asw("S", 3, true, "sar");
                var rs = new Rs();
                h.B.BeginTick(0);
                h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("asw", s, new[] { 1 });
                h.B.Submit(Rq(1, c, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, s, DeckKind.Order), rs.Cb);
                h.B.EndTick(0);
                if (h.LogCount("[Air] deck 1 order: C1(t0) A2(t1) S3(t2)") != 1) return "order line: " + Join(h.Logs);
                for (int t = 1; t < 4; t++) { h.B.BeginTick(t); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("asw", s, new[] { 1 }); h.B.EndTick(t); }
                if (h.LogCount("order:") != 1) return "order line repeated: " + Join(h.Logs);
                h.F.Of(1).Reverse();
                h.B.BeginTick(5); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("asw", s, new[] { 1 }); h.B.EndTick(5);
                if (!h.Log("order: S3(t2) A2(t1) C1(t0)") ) return "order change not logged: " + Join(h.Logs);
            }
            // 8. events only at Debug
            {
                var off = new H(); var on = new H(debug: true); var a = Asw("A", 1, false);
                foreach (var h in new[] { off, on })
                {
                    h.B.BeginTick(0); h.B.Declare("asw", a, new[] { 1 });
                    h.B.Submit(Rq(1, a, DeckKind.Order), null); h.B.EndTick(0);
                }
                if (off.Evts.Count != 0) return "events without Debug";
                if (on.Evts.Count != 1 || !on.Evts[0].StartsWith("deck_order")) return "deck_order event: " + Join(on.Evts);
                if (off.Logs.Count != 1) return "log lines always";
            }
            // 9. a failed order is reported, nothing registered
            {
                var h = new H(); var rs = new Rs(); var a = Asw("A", 1, false);
                h.B.BeginTick(0); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(new DeckRequest { DeckUid = 1, Window = "asw", Group = "A", Kind = DeckKind.Allow, Key = "task:999", Prio = a }, rs.Cb);
                h.B.EndTick(0);
                if (rs.Last.Err != "deck_task_not_found" || rs.Last.Held) return "allow of a missing task";
            }
            return null;
        }

        // ------------------------------------------------------------------ yield

        /// <summary>Deck 1: CAP group C (seq 1) with a gap under threat, ASW group A (seq 2) with a contact. Returns the key of A's order.</summary>
        private static string Setup(H h, bool flag, out DeckResult aRes)
        {
            var c = Cap("C", 1, flag); var a = Asw("A", 2, true);
            var rs = new Rs();
            h.B.BeginTick(0); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 });
            h.B.Submit(Rq(1, a, DeckKind.Order, null, true), rs.Cb);
            h.B.Submit(Rq(1, c, DeckKind.Order, null, false), null);
            h.B.EndTick(0);
            aRes = rs.Last;
            return rs.Last.Key;
        }

        private static void Tick(H h, double t, bool flag, params KeyValuePair<AirGroupPrio, DeckRequest>[] subs)
        {
            h.B.BeginTick(t);
            h.B.Declare("cap", Cap("C", 1, flag), new[] { 1 });
            h.B.Declare("asw", Asw("A", 2, true), new[] { 1 });
            foreach (var kv in subs) h.B.Submit(kv.Value, null);
            h.B.EndTick(t);
        }

        private static KeyValuePair<AirGroupPrio, DeckRequest> AllowA(string key)
        {
            var a = Asw("A", 2, true);
            return new KeyValuePair<AirGroupPrio, DeckRequest>(a, Rq(1, a, DeckKind.Allow, key));
        }

        public static string Yield()
        {
            DeckResult ar;
            // order after the CAP order; the allow of the ASW order is held
            {
                var h = new H(debug: true);
                string key = Setup(h, true, out ar);
                if (h.Calls() != "order 1 task:101|order 1 task:102") return "order after cap: " + h.Calls();
                if (h.F.Of(1)[0].Type != "heli" || h.F.Of(1)[0].Key != "task:101") return "cap first on the deck";
                // task:101 is the cap one, task:102 is the asw one
                if (!ar.Held || ar.Err != null || ar.Key != "task:102") return "asw order not held: held=" + ar.Held + " key=" + ar.Key;
                if (h.F.Of(1)[1].Allowed) return "asw task allowed despite the hold";
                if (h.F.Calls.Contains("order 1 task:102 allow")) return "asw order issued with allowLaunch";
                if (h.B.HeldS("asw", "A") != 0) return "HeldS 0 at the start";
                // flag gone at 30 s
                h.F.Calls.Clear();
                Tick(h, 30, false);
                if (h.Calls() != "allow 1 task:102") return "release calls: " + h.Calls();
                if (!h.Log("[Air] deck 1 yield A held=30s reason=flag_off")) return "flag_off line: " + Join(h.Logs);
                if (h.B.HeldS("asw", "A") != -1) return "HeldS after release";
                if (h.Evts.FindAll(e => e.StartsWith("deck_yield")).Count != 1) return "deck_yield event";
            }
            // the flag holds: timeout at 120 s; a repeated allow at 60 s does not reset the timer; no hold after the timeout
            {
                var h = new H();
                string key = Setup(h, true, out ar);
                h.F.Calls.Clear();
                Tick(h, 60, true, AllowA(key));
                if (h.F.Calls.Count != 0) return "held allow executed at 60 s: " + h.Calls();
                if (Math.Abs(h.B.HeldS("asw", "A") - 60) > 1e-6) return "HeldS 60 (timer reset?)";
                Tick(h, 119, true);
                if (h.F.Calls.Count != 0) return "released early";
                Tick(h, 120, true);
                if (h.Calls() != "allow 1 task:102") return "timeout calls: " + h.Calls();
                if (!h.Log("yield A held=120s reason=timeout")) return "timeout line";
                h.F.Calls.Clear();
                Tick(h, 125, true, AllowA(key));
                if (h.Calls() != "allow 1 task:102") return "second allow after timeout must pass: " + h.Calls();
                if (h.B.HeldS("asw", "A") != -1) return "held again after the timeout";
            }
            // review: a repeated allow arriving exactly on the timeout tick must not re-create the hold
            {
                var h = new H();
                string key = Setup(h, true, out ar);
                h.F.Calls.Clear();
                Tick(h, 120, true, AllowA(key));
                if (h.Calls() != "allow 1 task:102") return "allow on the timeout tick: " + h.Calls();
                if (h.B.HeldS("asw", "A") != -1) return "stale hold after repeated allow on the timeout tick: " + h.B.HeldS("asw", "A");
            }
            // review: ADR-0041 tier 1 - ASW with a contact goes before CAP without a flag (CAP has the smaller GroupSeq)
            {
                var h = new H(); var c = Cap("C", 1, false); var a = Asw("A", 2, true); var rs = new Rs();
                h.B.BeginTick(0); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, c, DeckKind.Order, null, false), null);
                h.B.Submit(Rq(1, a, DeckKind.Order, null, true), rs.Cb);
                h.B.EndTick(0);
                if (h.Calls() != "order 1 task:101 allow|order 1 task:102") return "asw contact before cap without flag: " + h.Calls();
                if (rs.Last.Held || rs.Last.Key != "task:101") return "asw not first/held: " + rs.Last.Key;
            }
            // ASW on another deck does not wait
            {
                var h = new H(); var c = Cap("C", 1, true); var a = Asw("A", 2, true); var rs = new Rs();
                h.B.BeginTick(0); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 2 });
                h.B.Submit(Rq(1, c, DeckKind.Order), null); h.B.Submit(Rq(2, a, DeckKind.Order, null, true), rs.Cb);
                h.B.EndTick(0);
                if (rs.Last.Held || !h.F.Calls.Contains("order 2 task:102 allow")) return "other deck waits: " + h.Calls();
            }
            // abort does not wait and drops the hold
            {
                var h = new H(); string key = Setup(h, true, out ar); h.F.Calls.Clear();
                var a = Asw("A", 2, true); var rs = new Rs();
                h.B.BeginTick(10); h.B.Declare("cap", Cap("C", 1, true), new[] { 1 }); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, a, DeckKind.Abort, key), rs.Cb);
                h.B.EndTick(10);
                if (h.Calls() != "abort 1 " + key || rs.Last.Err != null || rs.Last.Held) return "abort: " + h.Calls();
                if (h.B.HeldS("asw", "A") != -1) return "hold survives the abort";
                h.F.Calls.Clear(); Tick(h, 40, false);
                if (h.F.Calls.Count != 0) return "allow after abort: " + h.Calls();
                if (h.Log("yield A")) return "abort leaves a yield line";
            }
            // SAR of helicopters (Order sar, window asw) waits the same way
            {
                var h = new H(); var c = Cap("C", 1, true); var s = Asw("S", 2, false, "sar"); var rs = new Rs();
                h.B.BeginTick(0); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", s, new[] { 1 });
                h.B.Submit(Rq(1, s, DeckKind.Order, null, true), rs.Cb); h.B.Submit(Rq(1, c, DeckKind.Order), null);
                h.B.EndTick(0);
                if (!rs.Last.Held || h.Calls() != "order 1 task:101|order 1 task:102") return "sar order: " + h.Calls();
                if (h.B.HeldS("asw", "S") < 0) return "sar HeldS";
            }
            // CAP without the flag does not make anybody wait
            {
                var h = new H(); string key = Setup(h, false, out ar);
                if (ar.Held || !h.F.Calls.Contains("order 1 task:101 allow")) return "waits without the flag: " + h.Calls();
            }
            // YieldMaxS = 0 turns the yield off
            {
                var h = new H(0); string key = Setup(h, true, out ar);
                if (ar.Held || !h.F.Calls.Contains("order 1 task:102 allow")) return "yield with 0: " + h.Calls();
            }
            // the task disappeared: one line, the hold is dropped
            {
                var h = new H(); string key = Setup(h, true, out ar);
                h.F.Of(1).RemoveAll(t => t.Key == key); h.F.Calls.Clear();
                Tick(h, 30, false);
                Tick(h, 31, false);
                if (h.LogCount("task_gone") != 1) return "task_gone line: " + Join(h.Logs);
                if (h.F.Calls.Count != 1) return "allow of a vanished task once: " + h.Calls();
                if (h.B.HeldS("asw", "A") != -1) return "hold of a vanished task";
            }
            // a CAP group that declares the deck but has no request this tick still raises the flag
            {
                var h = new H(); var a = Asw("A", 2, true); var rs = new Rs();
                h.B.BeginTick(0); h.B.Declare("cap", Cap("C", 1, true), new[] { 1 }); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, a, DeckKind.Order, null, true), rs.Cb); h.B.EndTick(0);
                if (!rs.Last.Held) return "declared deck without a request";
            }
            return null;
        }

        // ------------------------------------------------------------------ prioritise

        public static string Prioritise()
        {
            // an ASW task stands before a CAP task under the flag: Prioritise(cap), after GameTick cap is first
            {
                var h = new H(debug: true); var rs = new Rs();
                var c = Cap("C", 1, false); var a = Asw("A", 2, true);
                // the ASW order is issued first (no flag yet), the CAP order after: cap is behind
                h.B.BeginTick(0); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, a, DeckKind.Order, null, false), rs.Cb); h.B.EndTick(0);
                string aKey = rs.Last.Key;
                h.B.BeginTick(1); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 });
                h.B.Submit(Rq(1, c, DeckKind.Order, null, false), rs.Cb); h.B.EndTick(1);
                string cKey = rs.Last.Key;
                if (h.F.Keys(1) != aKey + "," + cKey) return "start order " + h.F.Keys(1);
                var cf = Cap("C", 1, true);
                h.F.Calls.Clear();
                h.B.BeginTick(2); h.B.Declare("cap", cf, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 }); h.B.EndTick(2);
                if (h.Calls() != "prioritise 1 " + cKey) return "prioritise call: " + h.Calls();
                h.F.GameTick();
                if (h.F.Keys(1) != cKey + "," + aKey) return "cap not first: " + h.F.Keys(1);
                if (!h.Log("[Air] deck 1 prioritise C")) return "prioritise line";
                if (h.Evts.FindAll(e => e.StartsWith("deck_prioritise")).Count != 1) return "deck_prioritise event";
                // repeat not earlier than 5 s: the move did not happen (no GameTick), cap still behind
                h.F.Of(1).Reverse(); h.F.Calls.Clear();
                h.B.BeginTick(3); h.B.Declare("cap", cf, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 }); h.B.EndTick(3);
                if (h.F.Calls.Count != 0) return "repeat within 5 s: " + h.Calls();
                h.B.BeginTick(7.5); h.B.Declare("cap", cf, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 }); h.B.EndTick(7.5);
                if (h.Calls() != "prioritise 1 " + cKey) return "repeat after 5 s: " + h.Calls();
                // no flag: no move
                h.F.Calls.Clear(); h.F.Of(1).Reverse();
                h.B.BeginTick(30); h.B.Declare("cap", c, new[] { 1 }); h.B.Declare("asw", a, new[] { 1 }); h.B.EndTick(30);
                if (h.F.Calls.Count != 0) return "move without the flag: " + h.Calls();
            }
            // two CAP tasks: reverse order, the final order is the target order (C1 before C2)
            {
                var h = new H(); var rs = new Rs();
                var c1 = Cap("C1", 1, true); var c2 = Cap("C2", 2, true); var a = Asw("A", 3, true);
                h.B.BeginTick(0); h.B.Declare("asw", a, new[] { 1 });
                h.B.Declare("cap", Cap("C1", 1, false), new[] { 1 }); h.B.Declare("cap", Cap("C2", 2, false), new[] { 1 });
                h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, Cap("C1", 1, false), DeckKind.Order), rs.Cb); h.B.Submit(Rq(1, Cap("C2", 2, false), DeckKind.Order), rs.Cb);
                h.B.EndTick(0);
                // A has a contact (tier 1) and goes first; C1, C2 follow (the flag was not up yet)
                var l = h.F.Of(1);
                string aKey = l[0].Key, k1 = l[1].Key, k2 = l[2].Key;
                h.F.Calls.Clear();
                h.B.BeginTick(1); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", c1, new[] { 1 }); h.B.Declare("cap", c2, new[] { 1 }); h.B.EndTick(1);
                if (h.Calls() != "prioritise 1 " + k2 + "|prioritise 1 " + k1) return "reverse order: " + h.Calls();
                h.F.GameTick();
                if (h.F.Keys(1) != k1 + "," + k2 + "," + aKey) return "final order: " + h.F.Keys(1);
            }
            // a CAP task in await_spawn is not moved
            {
                var h = new H(); var rs = new Rs(); var c = Cap("C", 1, false); var a = Asw("A", 2, true);
                h.B.BeginTick(0); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", c, new[] { 1 });
                h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb); h.B.EndTick(0);
                h.B.BeginTick(1); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", c, new[] { 1 });
                h.B.Submit(Rq(1, c, DeckKind.Order), rs.Cb); h.B.EndTick(1);
                h.F.Of(1)[1].State = "await_spawn"; h.F.Calls.Clear();
                h.B.BeginTick(2); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", Cap("C", 1, true), new[] { 1 }); h.B.EndTick(2);
                if (h.F.Calls.Count != 0) return "await_spawn moved: " + h.Calls();
            }
            // a foreign task is not moved and not counted
            {
                var h = new H(); var rs = new Rs(); var c = Cap("C", 1, false);
                string fk = h.F.AddForeign(1);
                h.B.BeginTick(0); h.B.Declare("cap", c, new[] { 1 });
                h.B.Submit(Rq(1, c, DeckKind.Order), rs.Cb); h.B.EndTick(0);
                h.F.Calls.Clear();
                h.B.BeginTick(1); h.B.Declare("cap", Cap("C", 1, true), new[] { 1 }); h.B.EndTick(1);
                if (h.F.Calls.Count != 0) return "foreign task makes a move: " + h.Calls();
                // even a foreign key submitted as allow by a CAP group is never moved by itself; a foreign ASW-less deck stays untouched
                if (!h.F.Of(1)[0].Key.Equals(fk)) return "foreign first";
            }
            // unsupported: one line per mission, then no more calls
            {
                var h = new H(); var rs = new Rs(); var c = Cap("C", 1, false); var a = Asw("A", 2, true);
                h.F.PrioUnsupported = true;
                h.B.BeginTick(0); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", c, new[] { 1 }); h.B.Submit(Rq(1, a, DeckKind.Order), rs.Cb); h.B.EndTick(0);
                h.B.BeginTick(1); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", c, new[] { 1 }); h.B.Submit(Rq(1, c, DeckKind.Order), rs.Cb); h.B.EndTick(1);
                h.F.Calls.Clear();
                for (int t = 2; t < 20; t += 6) { h.B.BeginTick(t); h.B.Declare("asw", a, new[] { 1 }); h.B.Declare("cap", Cap("C", 1, true), new[] { 1 }); h.B.EndTick(t); }
                if (h.F.Calls.Count != 1) return "unsupported repeated: " + h.Calls();
                if (h.LogCount("unsupported") != 1) return "unsupported line";
                h.B.Reset();
                if (h.F.Calls.Count != 1) return "reset made calls";
            }
            return null;
        }

        // ------------------------------------------------------------------ clock

        public static string Clock()
        {
            var c = new AirTickClock();
            int n = 0;
            // x1: game +0.033 s and real +0.033 s per frame -> one step per 30 frames, as before
            for (int i = 0; i < 300; i++) if (c.Due(100 + i * 0.033, i * 0.033)) n++;
            if (n < 9 || n > 11) return "x1 steps in 10 s: " + n;
            // x16: game +0.53 s per frame, real +0.033 s: at most 9 steps in 60 frames (2 s)
            c.Reset(); n = 0;
            for (int i = 0; i < 60; i++) if (c.Due(500 + i * 0.53, i * 0.033)) n++;
            if (n > 9) return "x16 steps in 60 frames: " + n;
            if (n < 5) return "x16 too few steps: " + n;
            // game time going back (load) -> a step at once, even with no real time passed
            if (!c.Due(10, 1.99)) return "back";
            c.Reset();
            if (!c.Due(10, 0)) return "reset";
            // a fixed 1 s game step at x1 with 1 s real: every step
            c.Reset(); n = 0;
            for (int t = 0; t < 10; t++) if (c.Due(t, t)) n++;
            if (n != 10) return "1s/1s: " + n;
            return null;
        }
    }
}
