using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    // T-119 stream B: the ASW controller talks to the deck only through IAirDeck (requests, facts, shared GroupSeq).
    internal static class AswDeckTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        /// <summary>A broker stand-in: records facts and requests; answers at once with <see cref="Reply"/> (null = never answers).</summary>
        private sealed class RecDeck : IAirDeck
        {
            public readonly List<DeckRequest> Reqs = new List<DeckRequest>();
            public readonly List<AirGroupPrio> Decl = new List<AirGroupPrio>();
            public readonly List<string> DeclWin = new List<string>();
            public Func<DeckRequest, DeckResult> Reply;
            public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids) { Decl.Add(prio); DeclWin.Add(window); }
            public int Submit(DeckRequest r, Action<DeckResult> done)
            {
                r.Ticket = Reqs.Count + 1;
                Reqs.Add(r);
                if (Reply != null && done != null) { var res = Reply(r); res.Ticket = r.Ticket; done(res); }
                return r.Ticket;
            }
            public double Held = -1;
            public double HeldS(string window, string group) { return window == "asw" ? Held : -1; }
            public bool DeckDown(int d) { return false; }
        }

        private static AswMember DeckHelo(int uid, string where, string key)
        {
            var m = AswFakeGame.Helo(uid, where);
            m.DeckKey = key; m.Uid = uid; m.DeckUid = 77; m.Dipping = true;
            return m;
        }

        private static void Step(AswController c, AswFakeGame f, int seconds, double dt = 1)
        {
            for (int i = 0; i < seconds; i++) { f.Advance(dt); f.Tick(c); }
        }

        public static string Run()
        {
            string e;
            if ((e = Requests()) != null) return "requests: " + e;
            if ((e = HeldAndInFlight()) != null) return "held/in flight: " + e;
            if ((e = Resolve()) != null) return "resolve: " + e;
            if ((e = SeqSave()) != null) return "seq save: " + e;
            if ((e = NoBroker()) != null) return "no broker: " + e;
            if ((e = DeckWait()) != null) return "deck wait: " + e;
            return null;
        }

        // T-119 I1: the window view carries how long the group's tasks wait for the deck (-1 = not waiting)
        private static string DeckWait()
        {
            var f = new AswFakeGame();
            f.Mem.Add(DeckHelo(-11, "deck_ready", "task:g1"));
            var rec = new RecDeck { Held = 75 };
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = new AirOwnership(), Deck = rec };
            c.Group(P("{\"op\":\"create\",\"group\":\"ASW-1\",\"uids\":[-11]}"));
            f.Advance(1); c.Tick(f.Now);
            if (c.View().Groups[0].DeckWaitS != 75) return "held " + c.View().Groups[0].DeckWaitS;
            rec.Held = -1; f.Advance(1); c.Tick(f.Now);
            if (c.View().Groups[0].DeckWaitS != -1) return "released " + c.View().Groups[0].DeckWaitS;
            var c0 = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            c0.Group(P("{\"op\":\"create\",\"group\":\"ASW-2\",\"uids\":[-11]}"));
            if (c0.View().Groups[0].DeckWaitS != -1) return "no broker " + c0.View().Groups[0].DeckWaitS;
            return null;
        }

        // an ASW and a SAR group declare themselves in window "asw" with WindowIndex = GroupSeq and submit Allow / Order requests
        private static string Requests()
        {
            var f = new AswFakeGame();
            f.Mem.Add(DeckHelo(-11, "deck_ready", "task:g1"));
            f.LoadoutList.Add(new LoadoutOption { Name = "ASWHunter", Display = "ASWHunter", Capacity = 2, Available = true });
            var own = new AirOwnership();
            own.GroupSeq("cap", "CAP-1");   // the CAP window already has a group: ASW numbers continue
            var rec = new RecDeck { Reply = r => new DeckResult { Key = r.Key ?? "task:n" } };
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own, Deck = rec };
            c.Group(P("{\"op\":\"create\",\"group\":\"ASW-1\",\"uids\":[-11,-12]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            f.Advance(1); c.Tick(f.Now);
            if (rec.Decl.Count == 0) return "no Declare";
            foreach (var w in rec.DeclWin) if (w != "asw") return "declare window " + w;
            var d = rec.Decl[rec.Decl.Count - 1];
            if (d.Name != "ASW-1" || d.Window != "asw" || d.Order != "asw") return "declare prio " + d.Name + "/" + d.Window + "/" + d.Order;
            if (d.WindowIndex != own.GroupSeq("asw", "ASW-1") || d.WindowIndex != 2) return "WindowIndex " + d.WindowIndex;
            DeckRequest allow = null, order = null;
            foreach (var r in rec.Reqs) if (r.Kind == DeckKind.Allow) allow = r;
            // a hangar machine is ordered by type: the loadout is resolved first, the launch is requested with allow
            var fh = new AswFakeGame();
            var hm = DeckHelo(-12, "deck_ready", "hangar:SH-3H"); hm.DeckUid = 78;
            fh.Mem.Add(hm);
            var rech = new RecDeck { Reply = r => new DeckResult { Key = "task:n" } };
            var ch = new AswController(fh, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own, Deck = rech };
            ch.Group(P("{\"op\":\"create\",\"group\":\"ASW-9\",\"uids\":[-12]}"));
            ch.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            fh.Advance(1); ch.Tick(fh.Now);
            foreach (var r in rech.Reqs) if (r.Kind == DeckKind.Order) order = r;
            if (allow == null || allow.Key != "task:g1" || allow.DeckUid != 77 || allow.Window != "asw" || allow.Group != "ASW-1") return "allow request";
            if (order == null || order.Type != "SH-3H" || order.Loadout != "ASWHunter" || !order.AllowLaunch || order.MissionType != null || order.DeckUid != 78) return "order request " + (order == null ? "null" : order.Loadout + "/" + order.AllowLaunch);
            if (rec.Reqs.Exists(r => r.Prio == null || r.Prio.WindowIndex != 2)) return "request prio";
            // a SAR group: same window, own number
            var w2 = new AswFakeGame();
            w2.Mode = "Arcade";
            w2.LoadoutList.Add(new LoadoutOption { Name = "Default", Display = "Default", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            var m = AswFakeGame.Helo(1, "deck_ready"); m.Type = "SH-3H"; m.DeckKey = "task:t1"; m.Loadout = "Default"; m.Capacity = 2; m.BestCapacity = 2;
            m.Passive = 0; m.ActiveBuoys = 0; m.Vlad = 0; m.Torpedoes = 0; m.DeckUid = 9000;
            w2.Mem.Add(m);
            w2.SarWorld.AddRaft(11, 6, 3, 2);
            var own2 = new AirOwnership();
            var rec2 = new RecDeck { Reply = r => new DeckResult { Key = "task:n" } };
            var sar = new SarController(w2.SarWorld, new SarParams(), null);
            var c2 = new AswController(w2, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Ownership = own2, Deck = rec2, Sar = sar };
            c2.Group(P("{\"op\":\"create\",\"group\":\"SAR-1\",\"uids\":[1]}"));
            string r2 = c2.SarOrder(P("{\"group\":\"SAR-1\"}"));
            if (AswController.IsErr(r2)) return "sar order " + r2;
            Step2(c2, w2, 5);
            bool sarDecl = false;
            foreach (var x in rec2.Decl) if (x.Name == "SAR-1" && x.Order == "sar" && x.Window == "asw" && x.WindowIndex == 1) sarDecl = true;
            if (!sarDecl) return "sar declare";
            if (!rec2.Reqs.Exists(r => r.Window == "asw" && r.Group == "SAR-1" && r.Kind != DeckKind.Abort && r.MissionType != null || r.Kind == DeckKind.Allow && r.Group == "SAR-1")) return "no sar request";
            return null;
        }

        private static void Step2(AswController c, AswFakeGame f, int s)
        {
            for (int i = 0; i < s; i++) { f.Advance(1); c.Tick(f.Now); }
        }

        // Held = success (no launch_failed, the machine is adopted), and the same request is not repeated before the retry interval;
        // a request without an answer is not submitted twice
        private static string HeldAndInFlight()
        {
            var f = new AswFakeGame();
            f.Mem.Add(DeckHelo(-11, "deck_ready", "task:g1"));
            var rec = new RecDeck { Reply = r => new DeckResult { Held = true, HeldS = 7 } };
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Deck = rec };
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Step2(c, f, 3);
            if (rec.Reqs.Count != 1) return "held: requests " + rec.Reqs.Count;
            foreach (var l in f.Logs) if (l.StartsWith("asw warn launch_failed", StringComparison.Ordinal)) return "held counted as a failure";
            bool log = false;
            foreach (var l in f.Logs) if (l.StartsWith("asw launch ", StringComparison.Ordinal) && l.Contains("-> ok held=7s")) log = true;
            if (!log) return "held log: " + string.Join(" | ", f.Logs.ToArray());
            Step2(c, f, 60);
            if (rec.Reqs.Count != 1) return "held: repeated before the interval, " + rec.Reqs.Count;
            // no answer at all: still one request after the retry interval
            var f2 = new AswFakeGame();
            f2.Mem.Add(DeckHelo(-12, "deck_ready", "task:g2"));
            var rec2 = new RecDeck();
            var c2 = new AswController(f2, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Deck = rec2 };
            c2.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Step2(c2, f2, 200);
            if (rec2.Reqs.Count != 1) return "in flight: " + rec2.Reqs.Count + " requests";
            return null;
        }

        private static string Resolve()
        {
            var f = new AswFakeGame { ResolveError = "sea_state" };
            f.Mem.Add(DeckHelo(5, "deck_ready", "hangar:SH-3H"));
            var rec = new RecDeck { Reply = r => new DeckResult() };
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }) { Deck = rec };
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Step2(c, f, 3);
            if (rec.Reqs.Count != 0) return "a request was sent although the loadout was not resolved";
            bool warn = false;
            foreach (var l in f.Logs) if (l.StartsWith("asw warn launch_failed", StringComparison.Ordinal) && l.Contains("sea_state")) warn = true;
            if (!warn) return "no sea_state warning: " + string.Join(" | ", f.Logs.ToArray());
            if (f.ResolveLog.Count == 0) return "ResolveLoadout not asked";
            return null;
        }

        // seq survives save and load; an old file without seq numbers by the order of loading
        private static string SeqSave()
        {
            string dir = Path.Combine(Path.GetTempPath(), "aswdeck-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "air"));
                var own = new AirOwnership();
                own.GroupSeq("cap", "CAP-1"); own.GroupSeq("cap", "CAP-2");
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }) { Ownership = own };
                c.Group(P("{\"op\":\"create\",\"group\":\"ASW-A\",\"uids\":[1]}"));
                c.Group(P("{\"op\":\"create\",\"group\":\"ASW-B\",\"uids\":[2]}"));
                if (own.GroupSeq("asw", "ASW-A") != 3 || own.GroupSeq("asw", "ASW-B") != 4) return "numbers at creation";
                c.SearchCourse(P("{\"group\":\"ASW-A\",\"tactics\":[\"rows\"]}"));
                Step2(c, f, 3);
                f.LastSavePath = "/s/B.sav";
                Step2(c, f, 1);
                string file = Path.Combine(dir, AswSave.Key("/s/B.sav") + ".json");
                if (!File.Exists(file)) return "no save";
                string json = File.ReadAllText(file);
                if (!json.Contains("\"seq\":3") || !json.Contains("\"seq\":4")) return "seq not written: " + json.Substring(0, Math.Min(300, json.Length));
                // load into a fresh ownership: the numbers are the saved ones
                var g = new AswFakeGame { SaveDir = dir, Now = f.Now };
                var a = AswFakeGame.Helo(11, "air"); a.Name = "Helo 1"; var b = AswFakeGame.Helo(12, "air"); b.Name = "Helo 2";
                g.Mem.Add(a); g.Mem.Add(b);
                var own2 = new AirOwnership();
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }) { Ownership = own2 };
                c2.OnMissionChanged("/s/b.sav", f.Now + 2);
                if (own2.GroupSeq("asw", "ASW-A") != 3 || own2.GroupSeq("asw", "ASW-B") != 4) return "restored numbers " + own2.GroupSeq("asw", "ASW-A") + "/" + own2.GroupSeq("asw", "ASW-B");
                if (own2.GroupSeq("cap", "CAP-X") != 5) return "next number after the restored ones";
                // old file: no "seq" -> numbers in the order of loading
                File.WriteAllText(file, json.Replace("\"seq\":3,", "").Replace("\"seq\":4,", ""));
                var own3 = new AirOwnership();
                own3.GroupSeq("cap", "CAP-1");
                var c3 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }) { Ownership = own3 };
                c3.OnMissionChanged("/s/b.sav", f.Now + 2);
                if (own3.GroupSeq("asw", "ASW-A") != 2 || own3.GroupSeq("asw", "ASW-B") != 3) return "old file numbers " + own3.GroupSeq("asw", "ASW-A") + "/" + own3.GroupSeq("asw", "ASW-B");
                return null;
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        // without a broker nothing is submitted and the log says so (once a minute)
        private static string NoBroker()
        {
            var f = new AswFakeGame();
            f.Mem.Add(DeckHelo(-11, "deck_ready", "task:g1"));
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Step2(c, f, 30);
            int n = 0; foreach (var l in f.Logs) if (l.StartsWith("asw deck broker is off", StringComparison.Ordinal)) n++;
            if (n != 1) return "off lines " + n;
            if (f.Allows.Count != 0) return "launched without a broker";
            return null;
        }
    }
}
