using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-328 (ADR-0127): the CAP side of the ring group sectors: AdmitWhy (dry run of "add"), the group list, the membership. Scenarios of `captest t328 <name>`.
    internal static class CapT328Tests
    {
        private static CapT179Tests.Chk NewChk() { return new CapT179Tests.Chk(); }
        private static Dictionary<string, object> J(string json) { return CapT179Tests.J(json); }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("admit") && (e = Admit()) != null) return "admit: " + e;
            if (want("ignore") && (e = Ignore()) != null) return "ignore: " + e;
            if (want("no_effects") && (e = NoEffects()) != null) return "no_effects: " + e;
            if (want("same_as_add") && (e = SameAsAdd()) != null) return "same_as_add: " + e;
            if (want("list") && (e = List()) != null) return "list: " + e;
            if (want("move") && (e = Move()) != null) return "move: " + e;
            if (want("slots") && (e = Slots()) != null) return "slots: " + e;
            return null;
        }

        private static CapT179Tests.Rig Two()
        {
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.Create("5,6"); r.Create("1,2");   // ПВО-1 = 5,6; ПВО-2 = 1,2
            return r;
        }

        private static Dictionary<string, int> W(params object[] kv) { var d = new Dictionary<string, int>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = (int)kv[i + 1]; return d; }

        private static string Admit()
        {
            var k = NewChk();
            var r = Two();
            k.Is("free unit may join", r.C.AdmitWhy(3, "ПВО-1", null, null) == null);
            k.Is("not_found", r.C.AdmitWhy(777, "ПВО-1", null, null) == "not_found");
            k.Is("unknown_group", r.C.AdmitWhy(3, "Нет", null, null) == "unknown_group");
            var b4 = r.G.Bird(4); b4.InMissionZone = true; r.C.ForgetRingBirds();
            k.Is("game_cap_zone", r.C.AdmitWhy(4, "ПВО-1", null, null) == "game_cap_zone");
            b4.InMissionZone = false;
            var b3 = r.G.Bird(3); b3.State = "Air"; b3.AiState = "CAP"; b3.Loadout = "Default"; b3.Weapons = W("AIM-9", 2, "Mk82", 4); r.C.ForgetRingBirds();
            k.Is("strike_loadout", r.C.AdmitWhy(3, "ПВО-1", null, null) == "strike_loadout");
            b3.Weapons = W("AIM-54", 4, "AIM-7", 2, "AIM-9", 2); r.C.ForgetRingBirds();
            k.Is("air-to-air load is fine", r.C.AdmitWhy(3, "ПВО-1", null, null) == null);
            r.Own.Claim(3, "asw", "ПЛО-1");
            k.Is("in_group of another window", r.C.AdmitWhy(3, "ПВО-1", null, null) == "in_group:ПЛО-1");
            k.Is("in_group of another cap group", r.C.AdmitWhy(5, "ПВО-2", null, null) == "in_group:ПВО-1");
            k.Is("a member of the same group: ok", r.C.AdmitWhy(5, "ПВО-1", null, null) == null);
            string w9 = r.C.AdmitWhy(9, "ПВО-1", null, null);
            k.Is("strike / self-defence only aircraft is refused: " + w9, w9 == "hidden" || w9 == "strike_loadout");
            var t = typeof(CapController).GetField("_deferred", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            t.SetValue(r.C, new CapSaveData());
            k.Is("restore_waiting", r.C.AdmitWhy(3, "ПВО-1", null, null) == "restore_waiting" && !r.C.RingReady);
            return k.Err;
        }

        private static string Ignore()
        {
            var k = NewChk();
            var r = Two();
            r.Own.Claim(3, "asw", "ПЛО-1");
            k.Is("ignore the pair the unit leaves", r.C.AdmitWhy(3, "ПВО-1", "asw", "ПЛО-1") == null);
            k.Is("another pair does not help", r.C.AdmitWhy(3, "ПВО-1", "asw", "ПЛО-2") == "in_group:ПЛО-1" && r.C.AdmitWhy(3, "ПВО-1", "cap", "ПЛО-1") == "in_group:ПЛО-1");
            k.Is("from a cap group to another", r.C.AdmitWhy(5, "ПВО-2", "cap", "ПВО-1") == null);
            return k.Err;
        }

        private static string NoEffects()
        {
            var k = NewChk();
            var r = Two();
            string w;
            string before = r.C.StateJson();
            r.C.AdmitWhy(3, "ПВО-1", null, null); r.C.AdmitWhy(5, "ПВО-2", null, null); r.C.AdmitWhy(3, "Нет", null, null);
            k.Is("no owner was made", r.Own.Owner(3, out w) == null);
            k.Is("members are the same", r.C.RingGroupOf(3) == null && r.C.RingGroupOf(5) == "ПВО-1" && r.C.RingGroupOf(1) == "ПВО-2");
            k.Is("state is the same", r.C.StateJson() == before);
            return k.Err;
        }

        private static string SameAsAdd()
        {
            var k = NewChk();
            var r = Two();
            var b3 = r.G.Bird(3); b3.State = "Air"; b3.AiState = "CAP"; b3.Loadout = "Default"; b3.Weapons = W("AIM-9", 2, "Mk82", 4);   // strike
            r.G.Bird(4).InMissionZone = true;
            r.Own.Claim(6 + 0, "asw", "ПЛО-9");   // 6 is in ПВО-1 already: the claim of asw would be wrong, so use a free uid below
            r.Own.Release(6);
            var uids = new[] { 1, 2, 3, 4, 5, 6, 9, 777 };
            foreach (int uid in uids)
            {
                var fresh = new CapT179Tests.Rig(null, "Cheater");
                fresh.Create("5,6");
                var fb = fresh.G.Bird(3); fb.State = "Air"; fb.AiState = "CAP"; fb.Loadout = "Default"; fb.Weapons = W("AIM-9", 2, "Mk82", 4);
                fresh.G.Bird(4).InMissionZone = true;
                fresh.Own.Claim(2, "asw", "ПЛО-1");
                fresh.Create("1");   // a second group ПВО-2 with uid 1
                string why = fresh.C.AdmitWhy(uid, "ПВО-2", null, null);
                string ack = fresh.C.Group(J("{\"op\":\"add\",\"group\":\"ПВО-2\",\"uids\":[" + uid + "]}"));
                bool member = fresh.C.RingGroupOf(uid) == "ПВО-2";
                k.Is("uid " + uid + ": AdmitWhy " + (why ?? "null") + " vs add member=" + member + " ack=" + ack, (why == null) == member);
            }
            return k.Err;
        }

        private static string List()
        {
            var k = NewChk();
            var r = Two();
            r.Create("3");
            var l = r.C.RingGroups();
            k.Is("three groups in window order", l.Count == 3 && l[0].Name == "ПВО-1" && l[1].Name == "ПВО-2" && l[2].Name == "ПВО-3");
            k.Is("kind and window", l[0].Kind == "cap" && l[0].Window == "cap");
            k.Is("members", l[0].Members == 2 && l[1].Members == 2 && l[2].Members == 1);
            k.Is("group of", r.C.RingGroupOf(5) == "ПВО-1" && r.C.RingGroupOf(3) == "ПВО-3" && r.C.RingGroupOf(4) == null);
            k.Is("ready", r.C.RingReady);
            return k.Err;
        }

        private static string Move()
        {
            var k = NewChk();
            var r = Two();
            string w;
            string rm = r.C.Group(J("{\"op\":\"remove\",\"group\":\"ПВО-1\",\"uids\":[5]}"));
            k.Is("removed " + rm, rm != null && rm.Contains("\"ok\":true") && r.C.RingGroupOf(5) == null);
            string ad = r.C.Group(J("{\"op\":\"add\",\"group\":\"ПВО-2\",\"uids\":[5]}"));
            k.Is("added " + ad, r.C.RingGroupOf(5) == "ПВО-2" && r.Own.Owner(5, out w) == "ПВО-2" && w == "cap");
            // the ring adapter's logic in one go: dry run with the ignore pair, remove, add
            string why = r.C.AdmitWhy(6, "ПВО-2", "cap", "ПВО-1");
            k.Is("dry run says ok", why == null);
            return k.Err;
        }

        // slot machines of the hangar: a machine of an ASW group is in_group, then hangar_taken when no free machine is left
        private static string Slots()
        {
            var k = NewChk();
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.G.SlotHangar = true;
            var c = new CapController(r.G, r.P, r.Own); r.C = c;
            int S1 = AirOpsMath.HangarSlotUid(CapFakeGame.DeckUid, "F-14A", 1), S2 = AirOpsMath.HangarSlotUid(CapFakeGame.DeckUid, "F-14A", 2),
                S3 = AirOpsMath.HangarSlotUid(CapFakeGame.DeckUid, "F-14A", 3), S4 = AirOpsMath.HangarSlotUid(CapFakeGame.DeckUid, "F-14A", 4);
            r.Create("" + S2);
            r.Own.Claim(S1, "asw", "ПЛО-1");
            k.Is("a free machine of the pair is left: ok", c.AdmitWhy(S1, "ПВО-1", null, null) == null);
            r.Own.Claim(S3, "asw", "ПЛО-1"); r.Own.Claim(S4, "asw", "ПЛО-1");
            k.Is("none left: hangar_taken", c.AdmitWhy(S1, "ПВО-1", null, null) == "hangar_taken");
            return k.Err;
        }
    }
}
