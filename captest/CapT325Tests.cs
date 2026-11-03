using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-325: CAP does not take aircraft with a strike loadout (TryAdd, Fill, PickRow, TryAdoptAirborne); the Available rows carry the loadout text.
    // Scenarios of `captest t325 <name>`.
    internal static class CapT325Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";
        private const string F14 = "F-14A";
        private static readonly string[] Names = { "refuse-air", "take-air", "hangar-gets-group-loadout", "mixed-default", "mixed-share", "fill-skips", "readopt-skips", "adopt-air-skips", "cool-not-checked", "log-once", "view-strike-row", "view-loadout-text" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams { Debug = true };
            public CapController C;
            public Rig(bool slots = false) { G.SlotHangar = slots; C = new CapController(G, P, Own); }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public string Create(string uids) { return C.Group(J("{\"op\":\"create\",\"uids\":[" + uids + "]}")); }
            public string Add(string group, string uids) { return C.Group(J("{\"op\":\"add\",\"group\":\"" + group + "\",\"uids\":[" + uids + "]}")); }
            public string Patrol(string group, int n, int k) { return C.Order(J("{\"group\":\"" + group + "\"," + ZA + ",\"on_station\":" + n + ",\"ready\":" + k + "}")); }
            public int LogN(string sub) { int n = 0; foreach (string l in G.Logs) if (l.Contains(sub)) n++; return n; }
            public CapCandidateView Cand(int uid) { foreach (var c in C.View().Candidates) if (c.Uid == uid) return c; return null; }
        }

        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static Dictionary<string, int> W(params object[] kv) { var d = new Dictionary<string, int>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = (int)kv[i + 1]; return d; }
        private static Dictionary<string, int> Hornet() { return W("AIM-9", 2, "Mk82", 4); }
        private static Dictionary<string, int> Su33() { return W("R-27R", 2, "R-73", 2, "FAB-500", 4); }
        private static int TUid(FakeBird b) { return AirOpsMath.TaskUid(b.Key); }

        /// <summary>An F-14A in the air with the given weapons.</summary>
        private static FakeBird Air(Rig r, int uid, Dictionary<string, int> w)
        {
            var b = r.G.Bird(uid); b.State = "Air"; b.Weapons = w; b.AiState = "CAP"; b.Loadout = "Default";
            return b;
        }

        public static string Run(string only = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "refuse-air": e = RefuseAir(); break;
                    case "take-air": e = TakeAir(); break;
                    case "hangar-gets-group-loadout": e = HangarGets(); break;
                    case "mixed-default": e = Mixed(1.0); break;
                    case "mixed-share": e = Mixed(0.5); break;
                    case "fill-skips": e = FillSkips(); break;
                    case "readopt-skips": e = ReadoptSkips(); break;
                    case "adopt-air-skips": e = AdoptAirSkips(); break;
                    case "cool-not-checked": e = CoolNotChecked(); break;
                    case "log-once": e = LogOnce(); break;
                    case "view-strike-row": e = ViewStrike(); break;
                    default: e = ViewText(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        private static string RefuseAir()
        {
            var k = new Chk(); var r = new Rig();
            Air(r, 1, Hornet());
            string a = r.Create("1");
            k.Is("create refused: " + a, a.Contains("!strike_loadout"));
            k.Is("not owned", r.Own.Owner(1, out _) == null);
            string a2 = r.Create("2");
            k.Is("create with a clean one", a2.Contains("\"ok\":true"));
            string gn = a2.Substring(a2.IndexOf("\"group\":\"") + 9); gn = gn.Substring(0, gn.IndexOf('"'));
            string b = r.Add(gn, "1");
            k.Is("add rejected: " + b, b.Contains("\"reason\":\"strike_loadout\""));
            return k.Err;
        }

        private static string TakeAir()
        {
            var k = new Chk(); var r = new Rig();
            Air(r, 1, W("AIM-54", 4, "AIM-7", 2, "AIM-9", 2, "Tank", 2));
            string a = r.Create("1");
            k.Is("taken: " + a, a.Contains("\"ok\":true") && r.Own.Owner(1, out _) == "ПВО-1");
            return k.Err;
        }

        private static string HangarGets()
        {
            var k = new Chk(); var r = new Rig(true);
            r.G.Bird(1).Weapons = Hornet();   // not exposed for a hangar row
            string a = r.Create(AirOpsMath.HangarSlotUid(CapFakeGame.DeckUid, F14, 1).ToString());
            r.Step(2);
            k.Is("hangar row taken: " + a, a.Contains("\"ok\":true"));
            string o = r.Patrol("ПВО-1", 1, 0);
            r.Step(5);
            k.Is("order with a CAP loadout: " + string.Join("|", r.G.Journal), r.G.Journal.Exists(j => j.StartsWith("order ") && j.Contains("AirToAir")));
            return k.Err;
        }

        private static string Mixed(double share)
        {
            var k = new Chk(); var r = new Rig();
            r.P.StrikeMixedAamShare = share;
            Air(r, 1, Su33());
            string a = r.Create("1");
            if (share >= 1.0) k.Is("refused: " + a, a.Contains("!strike_loadout"));
            else k.Is("taken: " + a, a.Contains("\"ok\":true"));
            return k.Err;
        }

        private static string FillSkips()
        {
            var k = new Chk(); var r = new Rig(true);
            while (r.G.TakeOut(F14)) { }
            var f1 = r.G.AddForeignReady(F14, "AirToAirLongRange"); var f2 = r.G.AddForeignReady(F14, "Strike"); var f3 = r.G.AddForeignReady(F14, "AirToAirLongRange");
            f2.Weapons = W("Mk82", 6, "AIM-9", 2);
            r.Create(TUid(f1).ToString());
            r.C.Order(J("{\"group\":\"ПВО-1\",\"kind\":\"alert\",\"ref_uid\":" + CapFakeGame.ShipUid + ",\"level\":\"deck\",\"ready\":3,\"fill\":true}"));
            r.Step(10);
            k.Is("strike task skipped", !r.G.LogHas("alert fill uid=" + TUid(f2) + " "));
            k.Is("clean task taken", r.G.LogHas("alert fill uid=" + TUid(f3) + " "));
            return k.Err;
        }

        private static string ReadoptSkips()
        {
            var k = new Chk(); var r = new Rig(true);
            r.G.RemoveOnLand = true; r.G.CoolS = 0; r.G.NoHangar = true;
            while (r.G.TakeOut(F14)) { }
            var f1 = r.G.AddForeignReady(F14, "AirToAirLongRange"); var f2 = r.G.AddForeignReady(F14, "AirToAirLongRange");
            int u1 = TUid(f1), u2 = TUid(f2);
            r.Create(u1.ToString()); r.Create(u2.ToString());
            r.Patrol("ПВО-1", 1, 0);
            r.Step(300);
            var free = r.G.AddGameTask(F14);
            free.Weapons = W("Mk82", 6, "AIM-9", 2); free.ShownLoadout = "Strike";
            int freeUid = TUid(free);
            int air = f1.Uid;
            for (int t = 0; t < 900 && r.G.Bird(air).State != "Air"; t++) r.Step(1);
            r.Step(400);
            r.G.Bird(air).ReserveNm = 60;
            for (int t = 0; t < 1500 && !r.G.Journal.Exists(j => j == "landed " + air); t++) r.Step(1);
            k.Is("landed", r.G.Journal.Exists(j => j == "landed " + air));
            r.Step(30);
            if (Environment.GetEnvironmentVariable("CAPT325_DUMP") == "1") foreach (string l in r.G.Logs) Console.WriteLine(l);
            k.Is("did not take the strike task", !r.G.LogHas("game task F-14A uid=" + freeUid));
            k.Is("refusal logged", r.G.LogHas("refuse strike_loadout uid=" + freeUid));
            return k.Err;
        }

        private static string AdoptAirSkips()
        {
            var k = new Chk();
            foreach (bool strike in new[] { false, true })
            {
                var r = new Rig(); r.G.RemapOnOrder = true; r.G.RenumberOnTakeoff = true;
                r.Create("1,2"); r.Patrol("ПВО-1", 1, 0);
                for (int t = 0; t < 900 && !r.G.Journal.Exists(j => j.StartsWith("launched ")); t++)
                {
                    r.Step(1);
                    if (strike && r.G.Journal.Exists(j => j.StartsWith("order "))) foreach (var b in r.G.All) if (b.Type == F14) { b.Weapons = Hornet(); b.ShownLoadout = "Default"; }
                }
                r.Step(20);
                bool adopted = r.LogN("airborne key=") > 0;
                if (strike) k.Is("strike machine not adopted airborne " + string.Join("|", r.G.Logs.FindAll(l => l.Contains("airborne") || l.Contains("strike"))), !adopted);
                else k.Is("clean machine adopted airborne", adopted);
            }
            return k.Err;
        }

        private static string CoolNotChecked()
        {
            var k = new Chk(); var r = new Rig(true);
            r.G.RemoveOnLand = true; r.G.CoolS = 600;
            var f = r.G.Bird(1); f.Weapons = Hornet(); f.State = "Cool"; f.CoolLeft = 500;
            var c = r.C.View().Candidates; r.Step(2);
            var cv = r.Cand(1);
            k.Is("cool row listed without a strike reason", cv == null || cv.Reason != "strike_loadout");
            string a = r.Create("1");
            k.Is("cool row accepted: " + a, a.Contains("\"ok\":true"));
            return k.Err;
        }

        private static string LogOnce()
        {
            var k = new Chk(); var r = new Rig();
            Air(r, 1, Hornet());
            r.Create("1");
            r.Step(3); r.C.View(); r.Create("1"); r.Step(3);
            k.Is("one line, got " + r.LogN("refuse strike_loadout uid=1"), r.LogN("refuse strike_loadout uid=1") == 1);
            return k.Err;
        }

        private static string ViewStrike()
        {
            var k = new Chk(); var r = new Rig();
            Air(r, 1, Hornet());
            Air(r, 2, W("AIM-54", 4, "AIM-7", 2, "AIM-9", 2));
            r.Step(2);
            var c1 = r.Cand(1); var c2 = r.Cand(2); var c3 = r.Cand(3);
            k.Is("strike row listed with reason", c1 != null && c1.Reason == "strike_loadout");
            k.Is("strike loadout text " + (c1 == null ? "-" : c1.LoadoutText), c1 != null && c1.LoadoutText == "AIM-9×2, Mk82×4");
            k.Is("air row free with AAM text " + (c2 == null ? "-" : c2.LoadoutText), c2 != null && c2.Reason == null && c2.LoadoutText.StartsWith("AIM-54×4"));
            k.Is("hangar row text empty", c3 != null && string.IsNullOrEmpty(c3.LoadoutText));
            string js = r.C.StateJson();
            k.Is("json reason", js.Contains("\"reason\":\"strike_loadout\""));
            k.Is("json loadout", js.Contains("\"loadout\":\"AIM-9×2, Mk82×4\""));
            return k.Err;
        }

        private static string ViewText()
        {
            var k = new Chk(); var r = new Rig();
            var b = Air(r, 1, new Dictionary<string, int>());
            b.LoadoutLabel = "Interceptor";
            r.Step(2);
            var c = r.Cand(1);
            k.Is("label when no weapons " + (c == null ? "-" : c.LoadoutText), c != null && c.LoadoutText == "Interceptor");
            return k.Err;
        }
    }
}
