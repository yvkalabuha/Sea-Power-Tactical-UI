using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-204: a landed member of a CAP group comes back (the cool-down of a landed machine is typed from iniRef, the deck still recovers it, the game's
    // ready-up task as the spare machine). Scenarios of `captest t204 <name>`.
    internal static class CapT204Tests
    {
        private const string F14 = "F-14A";
        private static readonly string[] Names = { "cool_type", "readopt_rank", "recover_gap", "recover_gap_none", "landed_game_task", "cool_to_game_task", "alert_land_mission", "alert_prep_view", "alert_prep_line", "alert_prep_scramble" };

        private static CapT179Tests.Chk NewChk() { return new CapT179Tests.Chk(); }

        /// <summary>CAPT204_DUMP=1: the log of the last rig of a failed scenario (debugging aid).</summary>
        public static string Run(string only = null)
        {
            string res = RunInner(only);
            if (res != null && Environment.GetEnvironmentVariable("CAPT204_DUMP") == "1" && CapT179Tests.Rig.All.Count > 0)
                foreach (string l in CapT179Tests.Rig.All[CapT179Tests.Rig.All.Count - 1].G.Logs) Console.WriteLine(l);
            return res;
        }

        private static string RunInner(string only)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "cool_type": e = CoolType(); break;
                    case "readopt_rank": e = ReadoptRank(); break;
                    case "recover_gap": e = RecoverGap(); break;
                    case "recover_gap_none": e = RecoverGapNone(); break;
                    case "landed_game_task": e = LandedGameTask(); break;
                    case "cool_to_game_task": e = CoolToGameTask(); break;
                    case "alert_land_mission": e = CapT204AlertTests.AlertLandMission(); break;
                    case "alert_prep_view": e = CapT204AlertTests.PrepView(); break;
                    case "alert_prep_line": e = CapT204AlertTests.PrepLine(); break;
                    default: e = CapT204AlertTests.PrepScramble(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        private static string CoolType()
        {
            var k = NewChk();
            k.Is("vehicle type first", AirOpsMath.DeckTaskType("usn_f-14a", "other") == "usn_f-14a");
            k.Is("vehicle type only", AirOpsMath.DeckTaskType("usn_f-14a", null) == "usn_f-14a");
            k.Is("iniRef of a cool-down", AirOpsMath.DeckTaskType(null, "usn_f-14a") == "usn_f-14a");
            k.Is("iniRef when the type is empty", AirOpsMath.DeckTaskType("", "usn_f-14a") == "usn_f-14a");
            k.Is("none", AirOpsMath.DeckTaskType("", "") == null && AirOpsMath.DeckTaskType(null, null) == null);
            return k.Err;
        }

        // the table of CapMath.ReadoptRank
        private static string ReadoptRank()
        {
            var k = NewChk();
            k.Is("landed: cool 0", CapMath.ReadoptRank("deck_cool", false, false, true, false) == 0);
            k.Is("landed: hangar 1", CapMath.ReadoptRank("hangar", false, false, true, false) == 1);
            k.Is("landed: game task 2", CapMath.ReadoptRank("deck_prep", true, true, true, false) == 2 && CapMath.ReadoptRank("deck_ready", true, true, true, false) == 2);
            k.Is("landed: a task of the mod is not allowed", CapMath.ReadoptRank("deck_prep", true, false, true, false) == -1);
            k.Is("landed: ready without a key is not allowed", CapMath.ReadoptRank("deck_ready", false, false, true, false) == -1);
            k.Is("after cool: hangar 0, ready 1, game task 2", CapMath.ReadoptRank("hangar", false, false, false, true) == 0 && CapMath.ReadoptRank("deck_ready", false, false, false, true) == 1 && CapMath.ReadoptRank("deck_prep", true, true, false, true) == 2);
            k.Is("plain readopt: no task rows", CapMath.ReadoptRank("deck_prep", true, true, false, false) == -1 && CapMath.ReadoptRank("hangar", false, false, false, false) == 0);
            k.Is("a flying row is never allowed", CapMath.ReadoptRank("to_zone", false, false, true, true) == -1);
            return k.Err;
        }

        private static CapT179Tests.Rig PatrolRig(Action<CapFakeGame> cfg)
        {
            var r = new CapT179Tests.Rig();
            cfg(r.G);
            r.Create("1"); r.Patrol("ПВО-1", 1, 0);
            r.Step(2500);
            r.G.NoHangar = true;   // the mission has Numbers = 0: set once the machine is out (the order needs a hangar row)
            return r;
        }

        private static bool Landed(CapT179Tests.Rig r, int uid)
        {
            r.G.Bird(uid).ReserveNm = 60;
            for (int t = 0; t < 1500 && r.Jn("landed " + uid) == 0; t++) r.Step(1);
            return r.Jn("landed " + uid) == 1;
        }

        // the deck recovers the type for 60 s (no row, no hangar): the member waits and takes the cool-down row once it is there
        private static string RecoverGap()
        {
            var k = NewChk();
            var r = PatrolRig(g => { g.RemoveOnLand = true; g.CoolS = 600; g.RecoverGapS = 60; });
            k.Is("on station", r.View("ПВО-1").Line.OnStation == 1);
            k.Is("landed", Landed(r, 1));
            r.Step(30);
            k.Is("30 s: still in the group, no member out", !r.G.LogHas("member out") && r.View("ПВО-1").Members.Count == 1);
            r.Step(20);   // 50 s after the landing: past AdoptGraceS, the deck still recovers
            k.Is("50 s: no member out", !r.G.LogHas("member out") && r.View("ПВО-1").Members.Count == 1);
            r.Step(30);
            k.Is("landed -> cool-down", r.G.LogHas("landed uid=") && r.G.LogHas("-> deck 100 cool-down F-14A"));
            k.Is("member in deck_cool", r.View("ПВО-1").Members.Count == 1 && r.View("ПВО-1").Members[0].Where == "deck_cool");
            k.Is("never dropped", !r.G.LogHas("member out"));
            return k.Err;
        }

        // the unit is gone and the deck does not recover it any more (no row): dropped after 15 s as before
        private static string RecoverGapNone()
        {
            var k = NewChk();
            var r = PatrolRig(g => { g.RemoveOnLand = true; g.LandNoAirframe = true; });
            k.Is("landed", Landed(r, 1));
            r.Step(10);
            k.Is("10 s: waiting", !r.G.LogHas("member out"));
            r.Step(15);
            k.Is("dropped after the grace", r.G.LogHas("member out") && r.G.LogHas("landed, no airframe on deck 100"));
            return k.Err;
        }

        private static CapT179Tests.Rig GameRig(Action<CapFakeGame> cfg, out FakeBird f1, out FakeBird f2)
        {
            var r = new CapT179Tests.Rig();
            r.G.SlotHangar = true;
            while (r.G.TakeOut(F14)) { }
            cfg(r.G);
            f1 = r.G.AddForeignReady(F14, "AirToAirLongRange"); f2 = r.G.AddForeignReady(F14, "AirToAirLongRange");
            return r;
        }

        private static int TUid(FakeBird b) { return AirOpsMath.TaskUid(b.Key); }

        // no cool-down visible, no hangar: the landed member takes a free ready-up task of the game; the task of another group stays
        private static string LandedGameTask()
        {
            var k = NewChk();
            FakeBird f1, f2;
            var r = GameRig(g => { g.RemoveOnLand = true; g.CoolS = 0; g.NoHangar = true; }, out f1, out f2);
            string key1 = f1.Key, key2 = f2.Key;
            int u1 = TUid(f1), u2 = TUid(f2);
            r.Create(u1.ToString());
            r.Create(u2.ToString());   // ПВО-2 holds the second game task
            r.Patrol("ПВО-1", 1, 0);
            r.Step(300);
            var free = r.G.AddGameTask(F14);
            string freeKey = free.Key; int freeUid = TUid(free);
            k.Is("adopted the game task", r.G.LogHas("adopt deck task uid=" + u1));
            int air = f1.Uid;
            for (int t = 0; t < 900 && r.G.Bird(air).State != "Air"; t++) r.Step(1);
            k.Is("airborne " + r.G.Bird(air).State, r.G.Bird(air).State == "Air");
            r.Step(400);
            k.Is("landed", Landed(r, air));
            r.Step(5);
            k.Is("took the free game task", r.G.LogHas("-> deck 100 game task F-14A uid=" + freeUid + " key=" + freeKey));
            k.Is("the task of the other group is untouched", !r.G.LogHas("uid=" + u2 + " group=ПВО-1") && r.Own.Owner(u2, out _) == "ПВО-2");
            k.Is("not dropped", !r.G.LogHas("member out") && r.View("ПВО-1").Members.Count == 1);
            r.Step(20);
            k.Is("adopted as a game task, ours=false", r.G.LogHas("adopt deck task uid=" + freeUid + " key=" + freeKey + " ours=false"));
            k.Is("no order of the mod", r.Jn("order ") == 0);
            k.Is("keys " + key1 + key2, true);
            return k.Err;
        }

        // the cool-down ends and the game makes a ready-up task of the airframe: the member takes it, no "lost"
        private static string CoolToGameTask()
        {
            var k = NewChk();
            FakeBird f1, f2;
            var r = GameRig(g => { g.RemoveOnLand = true; g.CoolS = 100; g.NoHangar = true; g.CoolToGameTask = true; }, out f1, out f2);
            r.Create(TUid(f1).ToString());
            r.Patrol("ПВО-1", 1, 0);
            int air = f1.Uid;
            for (int t = 0; t < 1200 && r.G.Bird(air).State != "Air"; t++) r.Step(1);
            r.Step(300);
            k.Is("landed", Landed(r, air));
            r.Step(5);
            k.Is("cool-down", r.View("ПВО-1").Members.Count == 1 && r.View("ПВО-1").Members[0].Where == "deck_cool");
            r.Step(150);
            var m = r.View("ПВО-1").Members;
            k.Is("still one member " + (m.Count == 1 ? m[0].Where : "?"), m.Count == 1 && m[0].Where != "deck_cool");
            k.Is("no lost", !r.G.LogHas("(lost)") && !r.G.LogHas("member out"));
            k.Is("game task readopt", r.G.LogHas("readopt (task gone) game task key=task:g"));
            return k.Err;
        }
    }
}
