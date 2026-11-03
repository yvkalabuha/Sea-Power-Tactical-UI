using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-226 (Q-T192-2): a hangar row is not "ready at once": ReadyInS = HangarPrepS (ui_state, strike view), the SAR next launch has no extra
    /// cool-down for a hangar row, a strike keeps N in the air with the soonest machine (hangar_ready_in, soonest_launch, ctl_hangar_view, ctl_sar_next).</summary>
    internal static class AswT226Tests
    {
        private static readonly string[] Names = { "hangar_ready_in", "soonest_launch", "ctl_hangar_view", "ctl_sar_next" };
        private const string Sh3 = "usn_sh-3h";

        public static string Run(string only)
        {
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                string e = Case(n);
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string Case(string n)
        {
            switch (n)
            {
                case "hangar_ready_in": return HangarReadyIn();
                case "soonest_launch": return SoonestLaunch();
                case "ctl_hangar_view": return CtlHangarView();
                default: return CtlSarNext();
            }
        }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static string HangarReadyIn()
        {
            var m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "hangar:" + Sh3 + "#1", State = "ready", HangarPrepS = 900, Helicopter = true });
            if (m.Where != "deck_ready" || m.ReadyInS != 900 || AswMath.ReadyS(m) != 900) return "ready row " + m.Where + "/" + m.ReadyInS + "/" + AswMath.ReadyS(m);
            m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "hangar:" + Sh3 + "#1", State = "hangar", ReadyInS = 900, HangarPrepS = 180, Helicopter = true });
            if (m.ReadyInS != 180) return "raw 900, prep 180 -> " + m.ReadyInS;
            m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "hangar:" + Sh3 + "#1", State = "ready", HangarPrepS = -1, Helicopter = true });
            if (m.ReadyInS != 0) return "unknown prep, ready -> " + m.ReadyInS;
            m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "hangar:" + Sh3 + "#1", State = "hangar", ReadyInS = 900, HangarPrepS = -1, Helicopter = true });
            if (m.ReadyInS != 900) return "unknown prep, raw -> " + m.ReadyInS;
            m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "task:t1", State = "ready", HangarPrepS = 900, Helicopter = true });
            if (m.ReadyInS != 0) return "task ready -> " + m.ReadyInS;
            m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "task:t1", State = "prep", ReadyInS = 240, Helicopter = true });
            if (m.ReadyInS != 240) return "task prep -> " + m.ReadyInS;
            return null;
        }

        private static AswMember Row(int uid, string where, string key, double prep, double readyIn = 0)
        {
            var m = AswFakeGame.Helo(uid, where);
            m.Task = "dip"; m.DeckKey = key; m.HangarPrepS = prep; m.ReadyInS = readyIn; m.Loadout = "ASWHunter"; m.Torpedoes = 2;
            return m;
        }

        private static string SoonestLaunch()
        {
            var task0 = Row(1, "deck_ready", "task:t1", -1);
            var task120 = Row(2, "deck_prep", "task:t2", -1, 120);
            var hang = Row(3, "deck_ready", "hangar:" + Sh3 + "#1", 900);
            Func<AswMember, bool> any = x => true;
            var rows = new List<AswMember> { task120, hang, task0 };
            var s = AswMath.SoonestDeckLaunch(rows, any);
            if (s == null || s.Uid != 1) return "ready task first: " + (s == null ? "null" : s.Uid.ToString());
            s = AswMath.SoonestDeckLaunch(new List<AswMember> { hang, task120 }, any);
            if (s == null || s.Uid != 2) return "prep task before hangar: " + (s == null ? "null" : s.Uid.ToString());
            s = AswMath.SoonestDeckLaunch(new List<AswMember> { hang }, any);
            if (s == null || s.Uid != 3) return "hangar only";
            if (AswMath.SoonestDeckLaunch(rows, x => false) != null) return "ok filters everything -> not null";
            s = AswMath.SoonestDeckLaunch(rows, x => x.Uid != 1);
            if (s == null || s.Uid != 2) return "ok skips the ready task: " + (s == null ? "null" : s.Uid.ToString());
            var air = Row(4, "air", "unit:4", -1); var svc = Row(5, "deck_service", "cool:x", -1, 100);
            if (AswMath.SoonestDeckLaunch(new List<AswMember> { air, svc }, any) != null) return "air / service rows are not picked";
            // a tie: the task, not the hangar row
            var hang0 = Row(6, "deck_ready", "hangar:" + Sh3 + "#2", 0);
            s = AswMath.SoonestDeckLaunch(new List<AswMember> { hang0, Row(7, "deck_ready", "task:t7", -1) }, any);
            if (s == null || s.Uid != 7) return "tie -> task: " + (s == null ? "null" : s.Uid.ToString());
            if (AswMath.SoonestDeckLaunch(null, any) != null) return "null rows";
            return null;
        }

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        // the controller: ui_state ready_in_s of a hangar row = hangar_prep_s, the task row keeps its remainder; the strike view shows "15:00"
        private static string CtlHangarView()
        {
            var f = new AswFakeGame();
            var d = new AswFakeDeck { HangarReady = true, HangarPrepS = 900 };
            d.AddHangar(Sh3, "SH-3H", 1);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            var c = Make(f);
            for (int i = 0; i < 3; i++) { f.Advance(1); f.Tick(c); }
            int slot = AswMath.HangarSlotUid(77, Sh3, 1);
            string r = c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + slot + "]}"));
            if (AswController.IsErr(r)) return "create " + r;
            for (int i = 0; i < 2; i++) { f.Advance(1); f.Tick(c); }
            string js = c.StateJson();
            if (!js.Contains("\"ready_in_s\":900") || !js.Contains("\"hangar_prep_s\":900")) return "ui_state: " + js;
            var g = c.View().Groups[0];
            if (g.MemberRows.Count != 1 || g.MemberRows[0].ReadyInS != 900) return "row ready_in " + (g.MemberRows.Count == 0 ? "none" : g.MemberRows[0].ReadyInS.ToString());
            r = c.Order(P("{\"group\":\"G\",\"order\":\"strike\"}"));
            if (AswController.IsErr(r)) return "strike order " + r;
            c.View();
            var sg = c.View().Groups[0];
            if (sg.Order != "strike") return "order " + sg.Order;
            if (sg.MemberRows.Count != 1) return "strike members " + sg.MemberRows.Count + " (excluded: " + c.View().ExcludedStrike.Count + ")";
            if (sg.MemberRows[0].StrikeTask != "deck" || sg.MemberRows[0].StrikeArg != "15:00") return "strike arg " + sg.MemberRows[0].StrikeTask + "/" + sg.MemberRows[0].StrikeArg;
            return null;
        }

        // SAR (the old value was cool-down + preparation = 1800 s): a hangar row with no machine on the deck: the next launch is the preparation of the SAR loadout, no cool-down; a cool: row adds its remainder
        private static string CtlSarNext()
        {
            foreach (string kind in new[] { "hangar", "cool" })
            {
                var f = new AswFakeGame();
                f.Mode = "Realistic";
                f.LoadoutList.Add(new LoadoutOption { Name = "Default", Display = "Default", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
                var s = new SarController(f.SarWorld, new SarParams(), null);
                var c = Make(f); c.Sar = s;
                var h = AswFakeGame.Helo(1, "deck_service");
                h.Type = "SH-3H"; h.Loadout = "Default"; h.Capacity = 2; h.BestCapacity = 2; h.DeckUid = 9000; h.Passive = 0; h.ActiveBuoys = 0; h.Vlad = 0; h.Torpedoes = 0;
                if (kind == "hangar") { h.DeckKey = "hangar:SH-3H#1"; h.HangarPrepS = 900; h.ReadyInS = 900; }
                else { h.DeckKey = "cool:x"; h.ReadyInS = 300; h.NextPrepS = 900; }
                f.Mem.Add(h);
                f.NoAirframeDecks.Add(9000);   // the deck refuses the order: the row stays as it is
                string r = c.Group(P("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[1]}"));
                if (AswController.IsErr(r)) return kind + " create " + r;
                r = c.SarOrder(P("{\"group\":\"SAR-1\"}"));
                if (AswController.IsErr(r)) return kind + " order " + r;
                for (int i = 0; i < 3; i++) { f.Advance(1); f.Tick(c); }
                AswGroupView g = null;
                foreach (var x in c.View().Groups) if (x.Name == "SAR-1") g = x;
                if (g == null) return kind + ": no group";
                double want = kind == "hangar" ? 900 : 1200;
                if (g.SarNextLaunchS > want + 1e-6 || g.SarNextLaunchS < want - 5) // the fake counts the cool-down down
                     return kind + ": next_launch_s " + g.SarNextLaunchS + " want " + want;
            }
            return null;
        }
    }
}
