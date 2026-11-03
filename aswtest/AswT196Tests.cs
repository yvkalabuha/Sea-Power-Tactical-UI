using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-196: the ASW planners count the preparation of a hangar row (ready_s, relief_single, relief_lanes, assign_rank, eta_controller, timeline_no_gap).</summary>
    internal static class AswT196Tests
    {
        private static readonly string[] Names = { "ready_s", "relief_single", "relief_lanes", "assign_rank", "eta_controller", "timeline_no_gap" };

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
                case "ready_s": return ReadyS();
                case "relief_single": return ReliefSingle();
                case "relief_lanes": return ReliefLanes();
                case "assign_rank": return AssignRank();
                case "eta_controller": return EtaController();
                default: return Timeline();
            }
        }

        private static AswMember Row(int uid, string where, string key, double prep, double readyIn = 0)
        {
            var m = AswFakeGame.Helo(uid, where);
            m.Task = "dip"; m.DeckKey = key; m.HangarPrepS = prep; m.ReadyInS = readyIn; m.TransitS = 600; m.LaunchS = 120;
            return m;
        }

        private static AswMember Air(int uid, double remainS)
        {
            var m = AswFakeGame.Helo(uid, "air", dip: true);
            m.Task = "dip"; m.TransitS = 0; m.EnduranceS = 10000; m.FuelFrac = remainS / 0.9 / 10000;   // RemainS = FuelFrac * Endurance * 0.9
            return m;
        }

        private static AswParams Prm() { return new AswParams { Standby = 0 }; }
        private static AswTaskSpec Spec(int want) { return new AswTaskSpec { Id = "dip", Tactic = "dip", Want = want }; }
        private const string Hangar = "hangar:SH-3H#1", Task = "task:t1";

        private static int Launches(List<ReliefAction> a, int uid)
        {
            int n = 0; foreach (var x in a) if (x.Kind == "launch" && x.Uid == uid) n++;
            return n;
        }

        private static string ReadyS()
        {
            double v;
            if ((v = AswMath.ReadyS(Row(1, "deck_ready", Hangar, 900))) != 900) return "hangar ready " + v;
            if ((v = AswMath.ReadyS(Row(1, "deck_service", Hangar, 180, 900))) != 180) return "hangar service casual " + v;
            if ((v = AswMath.ReadyS(Row(1, "deck_service", Hangar, -1, 900))) != 900) return "old hangar row " + v;
            if ((v = AswMath.ReadyS(Row(1, "deck_ready", Task, -1))) != 0) return "task ready " + v;
            if ((v = AswMath.ReadyS(Row(1, "deck_prep", Task, -1, 240))) != 240) return "task prep " + v;
            if ((v = AswMath.ReadyS(Row(1, "deck_service", "cool:c1", -1, 300))) != 300) return "cool " + v;
            if ((v = AswMath.ReadyS(Air(1, 100))) != 0) return "air " + v;
            if ((v = AswMath.ReadyS(null)) != 0) return "null " + v;
            return null;
        }

        private static string ReliefSingle()
        {
            var air = Air(1, 1200);
            var h = Row(2, "deck_ready", Hangar, 900);
            if (Launches(AswMath.PlanRelief(new List<AswMember> { air, h }, new List<AswTaskSpec> { Spec(1) }, 0, Prm()), 2) != 1) return "no early order from the hangar (lead 900+720+60 > 1200)";
            var t = Row(2, "deck_ready", Task, -1);
            if (Launches(AswMath.PlanRelief(new List<AswMember> { Air(1, 1200), t }, new List<AswTaskSpec> { Spec(1) }, 0, Prm()), 2) != 0) return "a ready task row launched too early";
            var t2 = Row(3, "deck_ready", Task, -1);
            var plan = AswMath.PlanRelief(new List<AswMember> { Air(1, 700), Row(2, "deck_ready", Hangar, 900), t2 }, new List<AswTaskSpec> { Spec(1) }, 0, Prm());
            if (Launches(plan, 3) != 1 || Launches(plan, 2) != 0) return "the ready task row must go before the hangar row";
            return null;
        }

        private static string ReliefLanes()
        {
            var plan = AswMath.PlanRelief(new List<AswMember> { Air(1, 1200), Air(2, 5000), Row(3, "deck_ready", Hangar, 900) }, new List<AswTaskSpec> { Spec(2) }, 0, Prm());
            if (Launches(plan, 3) != 1) return "hangar row not launched for the low lane";
            plan = AswMath.PlanRelief(new List<AswMember> { Air(1, 1200), Air(2, 5000), Row(3, "deck_ready", Task, -1) }, new List<AswTaskSpec> { Spec(2) }, 0, Prm());
            if (Launches(plan, 3) != 0) return "task row launched (lead 780 < 1200)";
            plan = AswMath.PlanRelief(new List<AswMember> { Air(1, 5000), Air(2, 5000), Row(3, "deck_ready", Hangar, 900) }, new List<AswTaskSpec> { Spec(2) }, 0, Prm());
            if (Launches(plan, 3) != 0) return "launched with no low aircraft";
            return null;
        }

        private static string AssignRank()
        {
            var h = Row(1, "deck_ready", Hangar, 900); h.Task = null; h.Dipping = true;
            var t = Row(2, "deck_ready", Task, -1); t.Task = null; t.Dipping = true;
            var r = AswMath.Assign(new List<AswMember> { h, t }, new[] { "dip" });
            if (r[2] != "dip" || r[1] != "reserve") return "hangar row beat the ready task row: " + r[1] + "/" + r[2];
            r = AswMath.Assign(new List<AswMember> { h }, new[] { "dip" });
            if (r[1] != "dip") return "a lone hangar row must still get the tactic";
            return null;
        }

        private static string EtaController()
        {
            var f = new AswFakeGame();
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            var h = Row(1, "deck_ready", Hangar, 900);
            var lane = (LaneMember)typeof(AswController).GetMethod("LaneOf", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(c, new object[] { h });
            if (lane.EtaS != 1020 || lane.ReadyInS != 900) return "LaneOf eta " + lane.EtaS + " ready " + lane.ReadyInS;
            var eta = (double)typeof(AswController).GetMethod("EtaOf", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(c, new object[] { null, h });
            if (eta != 1620) return "EtaOf " + eta;
            var t = Row(2, "deck_ready", Task, -1);
            lane = (LaneMember)typeof(AswController).GetMethod("LaneOf", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(c, new object[] { t });
            if (lane.EtaS != 120) return "task row eta " + lane.EtaS;
            return null;
        }

        /// <summary>A pure timeline: one aircraft runs out, the hangar row is ordered ahead and is on station before it ends.</summary>
        private static string Timeline()
        {
            double endurance = 10000, frac0 = 0.34;   // about 3060 s of work
            var hangar = Row(2, "deck_ready", Hangar, 900);
            double launchAt = -1, onStationAt = -1, endAt = -1;
            for (double t = 0; t <= 3600; t += 10)
            {
                var a = Air(1, 0); a.FuelFrac = frac0 - t / endurance;
                double rem = AswMath.RemainS(a, null);
                if (rem <= 0 && endAt < 0) endAt = t;
                if (launchAt < 0)
                {
                    foreach (var x in AswMath.PlanRelief(new List<AswMember> { a, hangar }, new List<AswTaskSpec> { Spec(1) }, t, Prm()))
                        if (x.Kind == "launch" && x.Uid == 2) { launchAt = t; onStationAt = t + 900 + 120 + 600; if (rem < 900 + 720 + 60 - 10) return "ordered late: remain " + rem; }
                }
            }
            if (launchAt < 0) return "no order";
            if (endAt < 0) return "the aircraft never ran out";
            if (onStationAt > endAt) return "gap: on station " + onStationAt + " after the end " + endAt;
            return null;
        }
    }
}
