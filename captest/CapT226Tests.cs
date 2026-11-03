using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-226 (T-187-Q3): a deck task of someone else in preparation (deck_prep) is not a new order for the CAP relief planner: ETA = its remainder,
    // loadout = its own, it counts in reserve K and is never aborted. Scenarios of `captest t226 <name>`.
    internal static class CapT226Tests
    {
        private const string F14 = "F-14A";
        private static readonly string[] Names = { "foreign_prep_eta", "foreign_prep_beats_hangar", "foreign_prep_loadout", "foreign_prep_reserve", "own_unchanged", "ctl_foreign_prep" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private static LoadoutOption L(string name, double readyMin, params object[] kv)
        {
            var lo = new LoadoutOption { Name = name, Display = name, Capacity = 8, Available = true, ReadyUpMin = readyMin, Weapons = new Dictionary<string, int>() };
            for (int i = 0; i + 1 < kv.Length; i += 2) lo.Weapons[(string)kv[i]] = (int)kv[i + 1];
            return lo;
        }

        private static IList<LoadoutOption> Los()
        {
            return new List<LoadoutOption> {
                L("AirToAirLongRange", 30, "AIM-54", 4, "AIM-7", 2, "AIM-9", 2),
                L("AirToAirMedium", 20, "AIM-7", 4, "AIM-9", 4) };
        }

        private static CapAmmo Ammo(string n)
        {
            switch (n)
            {
                case "AIM-54": return new CapAmmo { Name = n, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 100 };
                case "AIM-7": return new CapAmmo { Name = n, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 25 };
                case "AIM-9": return new CapAmmo { Name = n, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 10 };
                default: return null;
            }
        }

        private sealed class World
        {
            public CapParams P = new CapParams();
            public IList<CapLane> Lanes;
            public bool Gap; public double GapS;
            public World() { Lanes = CapMath.Lanes(new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + 40 / 60.0, 20.0 + 20 / 60.0 } }, 1, P); }
            public List<CapAction> Plan(IList<CapBird> m, int k)
            {
                var byType = new Dictionary<string, string> { { F14, "AirToAirLongRange" } };
                return CapMath.PlanRelief(m, 1, k, Lanes, byType, (d, t) => Los(), Ammo, "Realistic", 0, P, out Gap, out GapS);
            }
        }

        /// <summary>A holder in the zone. leaveNow: the fuel is at the margin (relief needed at once); else a lot of fuel.</summary>
        private static CapBird Holder(bool leaveNow)
        {
            return new CapBird { Uid = 11, Slot = 0, Where = "zone", Type = F14, Loadout = "AirToAirLongRange", AamStart = 8, AamNow = 8, ReserveNm = leaveNow ? 0.01 : 1500,
                                 MaxRangeNm = 0, PatrolKn = 400, CruiseKn = 450, Lat = 10.3, Lon = 20.15, Ours = true };
        }

        private static CapBird Deck(int uid, string where, double readyS, string key = null, string loadout = "AirToAirLongRange", bool ours = true)
        {
            return new CapBird { Uid = uid, DeckUid = 100, Where = where, Type = F14, Loadout = loadout, ReadyInS = readyS, PendingKey = key, CruiseKn = 450, Lat = 9.0, Lon = 20.0, Ours = ours, MaxRangeNm = 2000 };
        }

        private static CapAction Find(List<CapAction> a, string kind, int uid = -1)
        {
            foreach (var x in a) if (x.Kind == kind && (uid < 0 || x.Uid == uid)) return x;
            return null;
        }

        private static int Count(List<CapAction> a, string kind, string reason = null)
        {
            int n = 0; foreach (var x in a) if (x.Kind == kind && (reason == null || x.Reason == reason)) n++; return n;
        }

        public static string Run(string only = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "foreign_prep_eta": e = ForeignPrepEta(); break;
                    case "foreign_prep_beats_hangar": e = BeatsHangar(); break;
                    case "foreign_prep_loadout": e = ForeignLoadout(); break;
                    case "foreign_prep_reserve": e = ForeignReserve(); break;
                    case "own_unchanged": e = OwnUnchanged(); break;
                    default: e = CtlForeignPrep(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        // the holder leaves, the reserve is a foreign task with 300 s left: allow by key, ETA = its remainder (not 300 + 1800), no order
        private static string ForeignPrepEta()
        {
            var k = new Chk(); var w = new World();
            var f = Deck(21, "deck_prep", 300, "task:f1", ours: false);
            var acts = w.Plan(new List<CapBird> { Holder(true), f }, 1);
            var a = Find(acts, "allow", 21);
            k.Is("allow foreign", a != null && a.Key == "task:f1");
            k.Is("eta " + (a == null ? -1 : a.EtaS), a != null && Math.Abs(a.EtaS - CapMath.ReliefS(f, w.Lanes[0].Center, w.P)) < 1e-6);
            k.Is("no order", Count(acts, "order") == 0);
            k.Is("no abort", Count(acts, "abort") == 0);
            return k.Err;
        }

        // foreign 1500 s left vs a hangar row (1800 s): the foreign one is taken (before the fix 1500 + 1800 lost to the hangar)
        private static string BeatsHangar()
        {
            var k = new Chk(); var w = new World();
            var acts = w.Plan(new List<CapBird> { Holder(true), Deck(21, "deck_prep", 1500, "task:f1", ours: false), Deck(22, "hangar", 0) }, 0);
            k.Is("allow foreign", Find(acts, "allow", 21) != null);
            k.Is("no hangar order", Count(acts, "order") == 0);
            return k.Err;
        }

        // the loadout of the foreign task is its own; a different one flies as is only against a gap, a near one is kept like our own task (T-175)
        private static string ForeignLoadout()
        {
            var k = new Chk(); var w = new World();
            var far = Deck(21, "deck_prep", 2000, "task:f1", "AirToAirMedium", ours: false);
            var acts = w.Plan(new List<CapBird> { Holder(true), far }, 0);
            var a = Find(acts, "allow", 21);
            k.Is("far: allow with its loadout", a != null && a.Loadout == "AirToAirMedium");
            k.Is("far: a gap is reported", w.Gap);
            var near = Deck(22, "deck_prep", 100, "task:f2", "AirToAirMedium", ours: false);
            acts = w.Plan(new List<CapBird> { Holder(true), near }, 0);
            a = Find(acts, "allow", 22);
            k.Is("near: allow with its loadout", a != null && a.Loadout == "AirToAirMedium");
            k.Is("near: no gap beyond its remainder", a != null && Math.Abs(a.EtaS - CapMath.ReliefS(near, w.Lanes[0].Center, w.P)) < 1e-6);
            // a foreign task of another loadout does not fill reserve K (not compatible) when the wanted one is far sooner
            acts = w.Plan(new List<CapBird> { Holder(false), far, Deck(23, "deck_ready", 0) }, 1);
            k.Is("incompatible not aborted", Count(acts, "abort") == 0);
            return k.Err;
        }

        // reserve K: a foreign prepared task counts and is never aborted
        private static string ForeignReserve()
        {
            var k = new Chk(); var w = new World();
            var f = Deck(21, "deck_prep", 500, "task:f1", ours: false);
            var hang = Deck(22, "hangar", 0);
            var acts = w.Plan(new List<CapBird> { Holder(false), f, hang }, 1);
            k.Is("K=1: no reserve order", Count(acts, "order", "reserve") == 0);
            k.Is("K=1: nothing aborted", Count(acts, "abort") == 0);
            acts = w.Plan(new List<CapBird> { Holder(false), f, hang }, 2);
            k.Is("K=2: one hangar order " + Count(acts, "order", "reserve"), Count(acts, "order", "reserve") == 1 && Find(acts, "order", 22) != null);
            k.Is("K=2: nothing aborted", Count(acts, "abort") == 0);
            // foreign surplus is not aborted either
            var f2 = Deck(23, "deck_prep", 600, "task:f2", ours: false);
            acts = w.Plan(new List<CapBird> { Holder(false), f, f2, Deck(24, "deck_ready", 0) }, 1);
            k.Is("surplus foreign untouched", Count(acts, "abort") == 0);
            return k.Err;
        }

        // our own task: surplus aborted, a prepared one allowed (as before)
        private static string OwnUnchanged()
        {
            var k = new Chk(); var w = new World();
            var acts = w.Plan(new List<CapBird> { Holder(false), Deck(1, "deck_ready", 0), Deck(2, "deck_prep", 600, "k2") }, 1);
            k.Is("own surplus aborted", Find(acts, "abort", 2) != null && Find(acts, "abort", 2).Key == "k2");
            acts = w.Plan(new List<CapBird> { Holder(true), Deck(3, "deck_prep", 200, "k3") }, 0);
            var a = Find(acts, "allow", 3);
            k.Is("own allow", a != null && a.Key == "k3" && Count(acts, "order") == 0);
            return k.Err;
        }

        // controller: a foreign task in preparation is adopted, no order is made for it, it is never aborted; its disappearance is survived
        private static string CtlForeignPrep()
        {
            var k = new Chk();
            var g = new CapFakeGame { SlotHangar = true };
            var own = new AirOwnership(); var p = new CapParams { Debug = true };
            var c = new CapController(g, p, own);
            while (g.TakeOut(F14)) { }
            var f1 = g.AddForeignPrep(F14, "AirToAirLongRange", 600);
            string a = c.Group((Dictionary<string, object>)Json.Parse("{\"op\":\"create\",\"uids\":[" + AirOpsMath.TaskUid(f1.Key) + "]}"));
            k.Is("create " + a, a.Contains("\"ok\":true"));
            c.Order((Dictionary<string, object>)Json.Parse("{\"group\":\"ПВО-1\",\"rect\":[10.5,11,10.83333,11.66667],\"on_station\":1,\"ready\":1}"));
            for (int i = 0; i < 20; i++) { g.Advance(1); g.Tick(c); }
            int adopt = 0, orders = 0, aborts = 0;
            foreach (string l in g.Logs) if (l.Contains("adopt deck task") && l.Contains("ours=false")) adopt++;
            foreach (string j in g.Journal) { if (j.StartsWith("order ", StringComparison.Ordinal)) orders++; if (j.StartsWith("abort ", StringComparison.Ordinal)) aborts++; }
            k.Is("adopted " + adopt, adopt >= 1);
            k.Is("no order " + orders, orders == 0);
            k.Is("no abort", aborts == 0);
            // R1: the game drops the task; the next ticks must not throw and must not abort anything
            g.Tasks.RemoveAll(t => t.Key == f1.Key); f1.State = "Gone";
            for (int i = 0; i < 10; i++) { g.Advance(1); g.Tick(c); }
            aborts = 0;
            foreach (string j in g.Journal) if (j.StartsWith("abort ", StringComparison.Ordinal)) aborts++;
            k.Is("task gone: no abort", aborts == 0);
            return k.Err;
        }
    }
}
