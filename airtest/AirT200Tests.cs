using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-200: the ASW slot line is always logged; the CAP handover moves the ASW member to the freed number (ADR-0082 s.2 and s.3).</summary>
    internal static class AirT200Tests
    {
        private const string F14 = "F-14A";
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";
        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static int S(int k) { return AirOpsMath.HangarSlotUid(100, F14, k); }

        public static string Run(string section)
        {
            string e;
            if ((section == null || section == "log_always") && (e = LogAlways()) != null) return "log_always: " + e;
            if ((section == null || section == "handover_move") && (e = HandoverMove()) != null) return "handover_move: " + e;
            return null;
        }

        private static string LogAlways()
        {
            foreach (string l in new[] { "asw slot ASW-9 uid=1 -> 2 k=2->1 n=1", "asw handover x", "asw member x", "asw cmd x", "asw drop x", "asw adopt x", "asw skip x" })
                if (!AswMath.LogAlways(l)) return "not always: " + l;
            foreach (string l in new[] { "asw route x", "asw standby x", "asw frame x", "air slots deck=1" })
                if (AswMath.LogAlways(l)) return "always: " + l;
            return null;
        }

        // CAP holds #1, ASW holds #2; the CAP order takes #1 out of the hangar: the ASW member moves to #1 (the uid of the freed number), nothing is lost
        private static string HandoverMove()
        {
            var a = new AswFakeGame();
            var c = new CapFakeGame { SlotHangar = true };
            a.Now = c.Now;
            var deck = new AswFakeDeck { ShipUid = 100, PrepS = 60, HangarReady = true };
            deck.AddHangar(F14, "F-14A", 4);
            a.RealDeck = deck;
            a.Decks[100] = new AswTarget { Uid = 100, Name = deck.ShipName, Lat = 35, Lon = 20 };
            var own = new AirOwnership();
            var logs = new List<string>();
            var mux = new MuxAirOps(a, c);
            var broker = new AirDeckBroker(mux, own, new AirDeckParams(), logs.Add, null);
            var asw = new AswController(a, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own, Deck = broker };
            var cap = new CapController(c, new CapParams { Debug = true }, own) { Deck = broker };
            own.Register("asw", asw.GroupNameOf);
            own.Slots.Log = logs.Add;
            bool shrunk = false;
            // as the game tick: the air tick rebinds the book before the ASW tick; the ordered machine leaves the hangar when the CAP member is handed over to its task
            Action<int> step = n => { for (int i = 0; i < n; i++) { a.Advance(1); c.Advance(1); mux.GameTick(); mux.TickNo++; double t = c.Now; broker.BeginTick(t); own.Slots.Rebind(t, (d, ty) => c.HangarNumbers(ty)); asw.Tick(t); cap.Tick(t); broker.EndTick(t); if (!shrunk && c.Logs.Exists(l => l.Contains("handover ПВО-1 slot="))) { shrunk = true; deck.TakeFromHangar(F14, 1); } } };
            for (int i = 0; i < 2; i++) { c.TakeOut(F14); deck.TakeFromHangar(F14, 1); }   // two machines left in the hangar
            step(2);
            string r = asw.Group(J("{\"op\":\"create\",\"group\":\"ASW-9\",\"uids\":[" + S(2) + "]}"));
            if (AswController.IsErr(r)) return "asw create " + r;
            r = cap.Group(J("{\"op\":\"create\",\"uids\":[" + S(1) + "]}"));
            step(2);
            var gv = cap.View().Groups.Count > 0 ? cap.View().Groups[0] : null;
            if (gv == null || gv.Members.Count != 1 || gv.Members[0].Uid != S(1)) return "cap member is not #1: " + r;
            string w;
            if (own.Owner(S(2), out w) != "ASW-9") return "asw does not hold #2";

            r = cap.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":1,\"ready\":0}"));
            if (r != null && r.Contains("\"ok\":false")) return "cap order " + r;
            step(12);
            var capLogs = string.Join(" | ", c.Logs.FindAll(l => l.Contains("handover") || l.Contains("slot") || l.Contains("lost") || l.Contains("readopt") || l.Contains("hangar_taken")));
            bool handover = false; foreach (var l in c.Logs) if (l.Contains("handover ПВО-1 slot=")) handover = true;
            if (!handover) return "no cap handover: " + capLogs;
            bool lost = false; foreach (var l in c.Logs) if (l.Contains("(lost)") || l.Contains("readopt") || l.Contains("hangar_taken")) lost = true;
            if (lost) return "cap lost the member: " + capLogs;
            bool moved = false; foreach (var l in a.Logs) if (l.Contains("asw slot ASW-9 uid=" + S(2) + " -> " + S(1) + " k=2->1 n=1")) moved = true;
            if (!moved) return "no asw slot move: book=" + own.Slots.DiagJson(5) + " aswlogs=" + string.Join(" / ", a.Logs.FindAll(l => l.Contains("asw "))) + " " + string.Join(" | ", a.Logs.FindAll(l => l.Contains("slot"))) + " air: " + string.Join(" | ", logs.FindAll(l => l.Contains("air slots"))) + " cap: " + capLogs;
            bool airSlots = logs.Exists(l => l.Contains("air slots") && l.Contains("n=1 moves=1 drops=0"));
            if (!airSlots) return "no 'air slots ... n=1 moves=1 drops=0': " + string.Join(" | ", logs.FindAll(l => l.Contains("air slots")));
            if (own.Owner(S(1), out w) != "ASW-9") return "the freed number is not held by asw: " + own.Owner(S(1), out w);
            foreach (var l in a.Logs) if (l.Contains("hangar_taken") || l.Contains("(lost)") || l.Contains("readopt")) return "asw lost the member: " + l;
            if (asw.View().Groups.Count != 1 || asw.View().Groups[0].Members.Count != 1 || asw.View().Groups[0].Members[0] != S(1)) return "asw member is not #1";
            return null;
        }
    }
}
