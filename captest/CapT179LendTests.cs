using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-179 / ADR-0079 section 5: lending an alert bird to a patrol group (alert-lend), the refusals (alert-lend-off), the end of a patrol group with a lent bird.
    internal static class CapT179LendTests
    {
        private static CapT179Tests.Chk NewChk() { return new CapT179Tests.Chk(); }

        public static string Run(string only)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("alert-lend") && (e = Lend()) != null) return "alert-lend: " + e;
            if (want("alert-lend-off") && (e = LendOff()) != null) return "alert-lend-off: " + e;
            if (want("alert-lend-end") && (e = LendEnd()) != null) return "alert-lend-end: " + e;
            return only == null || only == "alert-lend" || only == "alert-lend-off" || only == "alert-lend-end" ? null : "unknown scenario";
        }

        // patrol ПВО-1 (F-15C 5, 6, two on station) + alert ПВО-2 (F-14A 1, 2, 3). A raid sends 1 and 2; during it bird 5 leaves for fuel; after the raid
        // the first bird is lent to ПВО-1 (the free slot), the second finds the patrol full
        private static CapT179Tests.Rig Setup(string alertExtra, string patrolExtra = "")
        {
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.Create("5,6"); r.Patrol("ПВО-1", 2, 0, patrolExtra);
            r.Create("1,2,3"); r.Alert("ПВО-2", ",\"pursuit_nm\":100" + alertExtra);
            r.Step(900);
            return r;
        }

        private static void RaidAndGap(CapT179Tests.Rig r)
        {
            r.G.Contacts.Add(CapT179Tests.Raid(50, 70));
            r.Step(60);
            r.G.Bird(5).ReserveNm = 10;   // the patrol bird leaves for fuel while the raid is on
            r.Step(5);
            r.G.Contacts.Clear();
            r.Step(125);
        }

        private static string Lend()
        {
            var k = NewChk();
            var r = Setup("");
            k.Is("setup: two on station, two ready", r.Airborne(5, 6) == 2 && r.View("ПВО-2").ReadyNow == 2);
            if (k.Err != null) return k.Err;
            r.G.Contacts.Add(CapT179Tests.Raid(50, 70));
            r.Step(60);
            k.Is("scrambled", r.Airborne(1, 2, 3) == 2);
            r.G.Bird(5).ReserveNm = 10;
            r.Step(5);
            k.Is("patrol bird left for fuel", r.G.LogHas("leave uid=5 reason=fuel"));
            r.G.Contacts.Clear();
            r.Step(125);
            k.Is("lend to the patrol slot", r.G.LogHas("alert clear uid=1 group=ПВО-2 -> lend to=ПВО-1 slot=0") || r.G.LogHas("alert clear uid=2 group=ПВО-2 -> lend to=ПВО-1 slot=0"));
            int lentUid = r.G.LogHas("alert clear uid=1 group=ПВО-2 -> lend") ? 1 : 2;
            int otherUid = lentUid == 1 ? 2 : 1;
            k.Is("log facts", r.G.LogHas("reserve_nm=") && r.G.LogHas("need_nm="));
            k.Is("event lend", r.Events("cap_alert").Contains("\"op\":\"lend\"") && r.Events("cap_alert").Contains("\"to\":\"ПВО-1\""));
            var lm = r.Member("ПВО-1", lentUid);
            k.Is("a member of the patrol now, lent_from the alert group", lm != null && lm.LentFrom == "ПВО-2" && lm.Slot == 0 && !lm.Alert);
            string w;
            k.Is("owned by the patrol", r.Own.Owner(lentUid, out w) == "ПВО-1");
            var av = r.View("ПВО-2");
            k.Is("the alert group keeps a shadow", av.Lent.Count == 1 && av.Lent[0].Key == lentUid && av.Lent[0].Value == "ПВО-1" && !av.Members.Exists(m => m.Uid == lentUid));
            k.Is("cap_state lent", r.C.StateJson().Contains("\"lent\":[{\"uid\":" + lentUid + ",\"to\":\"ПВО-1\"}]") && r.C.StateJson().Contains("\"lent_from\":\"ПВО-2\""));
            k.Is("the second bird: the patrol is full", r.G.LogHas("alert clear uid=" + otherUid + " group=ПВО-2 -> rtb_alert why=patrol_full"));
            k.Is("patrol flies it by its ROE", r.G.Bird(lentUid).Roe == "tight" && r.G.Bird(lentUid).State == "Air");
            // the lent bird holds the slot: the patrol does not send a relief for it
            int orders = r.Jn("order 100 F-15C");
            r.Step(30);
            k.Is("no relief launched for the lent slot", r.Jn("order 100 F-15C") == orders);
            // the save keeps the loan
            var d = r.C.Save();
            string err; var back = CapSave.Parse(CapSave.ToJson(d), out err);
            var r2 = new CapT179Tests.Rig(null, "Cheater");
            r2.C.Load(back, r2.G.Now);
            var av2 = r2.View("ПВО-2"); var pv2 = r2.View("ПВО-1");
            k.Is("loaded: the loan is restored on both sides " + err, av2 != null && av2.Lent.Count == 1 && pv2.Members.Exists(m => m.LentFrom == "ПВО-2"));
            // the lent bird leaves for fuel, lands and goes back to stand alert
            r.G.Bird(lentUid).ReserveNm = 10;
            r.Step(900);
            k.Is("alert back", r.G.LogHas("alert back uid=" + lentUid + " from=ПВО-1 to=ПВО-2") && r.Events("cap_alert").Contains("\"op\":\"back\""));
            av = r.View("ПВО-2");
            k.Is("ready again, no shadow", av.Lent.Count == 0 && av.Members.Exists(m => m.Uid == lentUid) && r.Member("ПВО-1", lentUid) == null);
            k.Is("owner is the alert group again", r.Own.Owner(lentUid, out w) == "ПВО-2");
            k.Is("prepared again", av.ReadyNow >= 1);
            return k.Err;
        }

        private static string LendOff()
        {
            var k = NewChk();
            // accept_alert = false: no_patrol
            var r = Setup("", ",\"accept_alert\":false");
            RaidAndGap(r);
            k.Is("accept off: rtb_alert no_patrol", r.G.LogHas("-> rtb_alert why=no_patrol") && !r.G.LogHas("-> lend"));
            // after = alert: back to the alert, even with a free patrol slot
            var q = Setup(",\"after\":\"alert\"");
            RaidAndGap(q);
            k.Is("after=alert: rtb_alert", q.G.LogHas("-> rtb_alert why=after_alert") && !q.G.LogHas("-> lend"));
            q.Step(900);
            k.Is("they stand ready again", q.View("ПВО-2").ReadyNow == 2);
            // no patrol at all: no_patrol
            var n = new CapT179Tests.Rig(null, "Cheater");
            n.Create("1,2,3"); n.Alert("ПВО-1", ",\"pursuit_nm\":100");
            n.Step(900);
            n.G.Contacts.Add(CapT179Tests.Raid(50, 70));
            n.Step(60); n.G.Contacts.Clear(); n.Step(125);
            k.Is("no patrol group: no_patrol", n.G.LogHas("-> rtb_alert why=no_patrol"));
            // little fuel: unfit
            var u = Setup("");
            u.G.Contacts.Add(CapT179Tests.Raid(50, 70));
            u.Step(60);
            u.G.Bird(5).ReserveNm = 10;
            u.G.Bird(1).ReserveNm = 60; u.G.Bird(2).ReserveNm = 60;
            u.Step(5);
            u.G.Contacts.Clear();
            u.Step(125);
            k.Is("short of fuel: unfit " + (u.G.LogHas("leave uid=1") ? "left" : ""), u.G.LogHas("-> rtb_alert why=unfit") || u.G.LogHas("leave uid=1 reason=fuel"));
            return k.Err;
        }

        // the patrol group ends while it flies a lent bird: the bird goes home and back to its alert group; the alert group ends: the bird stays until it lands, then is free
        private static string LendEnd()
        {
            var k = NewChk();
            var r = Setup("");
            RaidAndGap(r);
            int lentUid = r.G.LogHas("alert clear uid=1 group=ПВО-2 -> lend") ? 1 : 2;
            k.Is("lent", r.Member("ПВО-1", lentUid) != null);
            r.C.Stop(CapT179Tests.J("{\"group\":\"ПВО-1\"}"));
            k.Is("stop of the patrol: the lent bird is back in the alert group at once", r.Member("ПВО-2", lentUid) != null && r.Member("ПВО-1", lentUid) == null && r.View("ПВО-2").Lent.Count == 0);
            k.Is("it flies home on hold", r.G.Bird(lentUid).Rtb && r.G.Bird(lentUid).Roe == "hold");
            r.Step(900);
            k.Is("then it stands ready", r.View("ПВО-2").ReadyNow == 2 && r.Airborne(1, 2, 3) == 0);
            // the alert group stops while a bird is lent
            var q = Setup("");
            RaidAndGap(q);
            int lu = q.G.LogHas("alert clear uid=1 group=ПВО-2 -> lend") ? 1 : 2;
            q.C.Stop(CapT179Tests.J("{\"group\":\"ПВО-2\"}"));
            k.Is("the bird stays with the patrol", q.Member("ПВО-1", lu) != null && q.G.Bird(lu).State == "Air");
            q.G.Bird(lu).ReserveNm = 10;
            q.Step(900);
            string w;
            k.Is("lands and is a free machine", q.Member("ПВО-1", lu) == null && q.Member("ПВО-2", lu) == null && q.Own.Owner(lu, out w) == null);
            return k.Err;
        }
    }
}
