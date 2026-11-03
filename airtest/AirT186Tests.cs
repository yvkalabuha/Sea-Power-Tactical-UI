using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-186 / ADR-0082: the real ASW and CAP controllers (fakes) on one deck share the hangar uid, the ownership and the slot book.</summary>
    internal static class AirT186Tests
    {
        private const string F14 = "F-14A", Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static int S(string type, int k) { return AirOpsMath.HangarSlotUid(100, type, k); }

        public static string Run(string section)
        {
            string e;
            if ((section == null || section == "shared") && (e = Shared()) != null) return "shared: " + e;
            return null;
        }

        private static string Shared()
        {
            var a = new AswFakeGame();
            var c = new CapFakeGame { SlotHangar = true };
            a.Now = c.Now;
            var deck = new AswFakeDeck { ShipUid = 100, PrepS = 60, HangarReady = true };
            deck.AddHangar(F14, "F-14A", 4).AddHangar(Sh3, "SH-3H", 3);
            a.RealDeck = deck;
            a.Decks[100] = new AswTarget { Uid = 100, Name = deck.ShipName, Lat = 35, Lon = 20 };
            var own = new AirOwnership();
            var logs = new List<string>();
            var mux = new MuxAirOps(a, c);
            var broker = new AirDeckBroker(mux, own, new AirDeckParams(), logs.Add, null);
            var asw = new AswController(a, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own, Deck = broker };
            var cap = new CapController(c, new CapParams { Debug = true }, own) { Deck = broker };
            own.Register("asw", asw.GroupNameOf);
            Action<int> step = n => { for (int i = 0; i < n; i++) { a.Advance(1); c.Advance(1); mux.GameTick(); mux.TickNo++; double t = c.Now; broker.BeginTick(t); asw.Tick(t); cap.Tick(t); broker.EndTick(t); } };
            step(2);

            // the ASW window takes k1 of the F-14A and k1 of the SH-3H
            string r = asw.Group(J("{\"op\":\"create\",\"group\":\"ASW-9\",\"uids\":[" + S(F14, 1) + "," + S(Sh3, 1) + "]}"));
            if (AswController.IsErr(r)) return "asw create " + r;
            step(2);
            if (own.Slots.DiagJson(5).IndexOf("\"uid\":" + S(F14, 1) + ",\"window\":\"asw\"", StringComparison.Ordinal) < 0) return "book misses the asw claim: " + own.Slots.DiagJson(5);

            // CAP: the same uid is a row of its own list, taken by the other window
            CapCandidateView cand = null;
            foreach (var x in cap.View().Candidates) if (x.Uid == S(F14, 1)) cand = x;
            if (cand == null || cand.Reason != "in_group:ASW-9" || cand.OwnerWindow != "asw" || cand.HangarK != 1) return "cap row of k1: " + (cand == null ? "none" : cand.Reason + " " + cand.OwnerWindow);
            r = cap.Group(J("{\"op\":\"create\",\"uids\":[" + S(F14, 1) + "]}"));
            var gv = cap.View().Groups.Count > 0 ? cap.View().Groups[0] : null;
            if (gv == null || gv.Members.Count != 1 || gv.Members[0].Uid != S(F14, 2)) return "cap create [k1] must take k2: " + r;

            // the ASW window sees the same uid as taken
            step(2);
            bool seen = false;
            foreach (var x in asw.View().Excluded) if (x.Row.Uid == S(F14, 2)) seen = x.Reason == "in_group:ПВО-1" && x.OwnerWindow == "cap";
            if (!seen) { var dbg = new List<string>(); foreach (var x in asw.View().Excluded) dbg.Add(x.Row.Uid + ":" + x.Reason + ":" + x.OwnerWindow); return "asw view of k2 (in_group:ПВО-1 / cap): " + string.Join(" ", dbg) + " want " + S(F14, 2); }

            // machines leave from outside: 4 -> 1, the CAP member took last and goes with hangar_taken; the ASW members stay; the SH-3H pair is not touched by the CAP numbers
            for (int i = 0; i < 3; i++) { c.TakeOut(F14); deck.TakeFromHangar(F14, 1); }
            step(8);
            bool taken = false; foreach (var l in c.Logs) if (l.Contains("member hangar_taken ПВО-1 uid=" + S(F14, 2))) taken = true;
            if (!taken) return "cap member not dropped with hangar_taken: " + own.Slots.DiagJson(5) + " " + string.Join(" | ", c.Logs.FindAll(l => l.Contains("slot") || l.Contains("member"))) + " " + string.Join(" | ", logs.FindAll(l => l.Contains("air slots")));
            if (cap.View().Groups[0].Members.Count != 0) return "cap member still there";
            if (asw.View().Groups.Count != 1 || asw.View().Groups[0].Members.Count != 2) return "asw lost a member: " + asw.View().Groups[0].Members.Count;
            return null;
        }
    }
}
