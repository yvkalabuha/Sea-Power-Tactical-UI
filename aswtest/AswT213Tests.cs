using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-213: the preparation of the next hangar order counts in every cycle of the relief plan (next_prep, gap_prep, relief_after_cool, ctl_air_prep, cycle_timeline).</summary>
    internal static class AswT213Tests
    {
        private static readonly string[] Names = { "next_prep", "gap_prep", "relief_after_cool", "ctl_air_prep", "ctl_air_prep_sticky", "cycle_timeline" };

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
                case "next_prep": return NextPrep();
                case "gap_prep": return GapPrep();
                case "relief_after_cool": return ReliefAfterCool();
                default: return AswT213Ctl.Run(n);
            }
        }

        private static AswMember Row(int uid, string where, string key, double prep, double readyIn = 0, double nextPrep = -1)
        {
            var m = AswFakeGame.Helo(uid, where);
            m.Task = "dip"; m.DeckKey = key; m.HangarPrepS = prep; m.ReadyInS = readyIn; m.NextPrepS = nextPrep; m.TransitS = 600; m.LaunchS = 120;
            return m;
        }

        private static AswMember Air(int uid, double remainS, double nextPrep = -1)
        {
            var m = AswFakeGame.Helo(uid, "air", dip: true);
            m.Task = "dip"; m.TransitS = 0; m.EnduranceS = 10000; m.FuelFrac = remainS / 0.9 / 10000; m.NextPrepS = nextPrep;   // RemainS = FuelFrac * Endurance * 0.9
            return m;
        }

        private static AswParams Prm() { return new AswParams { Standby = 0 }; }
        private static AswTaskSpec Spec(int want) { return new AswTaskSpec { Id = "dip", Tactic = "dip", Want = want }; }
        private const string Hangar = "hangar:SH-3H#1";

        private static double Gap(List<AswMember> ms, out bool launched, double turnaroundS = 3000)
        {
            double gap = -1; launched = false;
            foreach (var a in AswMath.PlanRelief(ms, new List<AswTaskSpec> { Spec(1) }, 0, new AswParams { Standby = 0, TurnaroundS = turnaroundS }))
            {
                if (a.Kind == "warn" && a.Code == "continuity_gap") gap = a.GapMin;
                if (a.Kind == "launch") launched = true;
            }
            return gap;
        }

        private static string NextPrep()
        {
            double v;
            if ((v = AswMath.NextPrepS(Row(1, "deck_ready", Hangar, 1800))) != 1800) return "hangar row " + v;
            var cool = Row(1, "deck_service", "cool:x", -1, 600, 1800);
            if ((v = AswMath.NextPrepS(cool)) != 1800) return "cool next prep " + v;
            if ((v = AswMath.ReadyS(cool)) != 2400) return "cool ready " + v;
            if ((v = AswMath.ReadyS(Row(1, "deck_service", "cool:x", -1, 600, -1))) != 600) return "cool unknown prep " + v;
            var air = Air(1, 100, 1800);
            if ((v = AswMath.ReadyS(air)) != 0) return "air ready " + v;
            if ((v = AswMath.NextPrepS(air)) != 1800) return "air next prep " + v;
            if ((v = AswMath.ReadyS(Row(1, "deck_prep", "task:t1", -1, 240))) != 240) return "task prep " + v;
            var m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "cool:x", State = "cooling", ReadyInS = 600, NextPrepS = 1800 });
            if ((v = AswMath.ReadyS(m)) != 2400) return "DeckToMember cool ready " + v;
            if ((v = AswMath.CycleTurnS(new List<AswMember> { Air(1, 100, 600), Air(2, 100, 1800) }, Prm())) != 3000 + 1800) return "CycleTurnS " + v;
            if ((v = AswMath.CycleTurnS(null, Prm())) != 3000) return "CycleTurnS null " + v;
            return null;
        }

        private static string GapPrep()
        {
            bool l; double g;
            if ((g = Gap(new List<AswMember> { Air(1, 5000, 1800) }, out l)) != 80) return "air + prep " + g;
            if ((g = Gap(new List<AswMember> { Air(1, 5000, -1) }, out l)) != 50) return "air, prep unknown " + g;
            if ((g = Gap(new List<AswMember> { Row(1, "deck_ready", Hangar, 1800) }, out l)) != 80) return "hangar row " + g;
            var cool = Row(2, "deck_service", "cool:c1", -1, 600, 1800);
            if ((g = Gap(new List<AswMember> { Air(1, 1000, -1), cool }, out l)) != 35.3) return "air + cool " + g;   // two in rotation: (600 + 1800 + 120 + 600) - 1000 = 2120 s, was 5.3
            return null;
        }

        private static string ReliefAfterCool()
        {
            bool l; Gap(new List<AswMember> { Air(1, 2000), Row(2, "deck_ready", Hangar, 1800) }, out l);
            if (!l) return "no launch with preparation 1800 (2000 < 1800+720+60)";
            Gap(new List<AswMember> { Air(1, 2000), Row(2, "deck_ready", Hangar, 0) }, out l);
            if (l) return "launched with no preparation";
            return null;
        }
    }
}
