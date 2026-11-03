using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase C (ADR-0133 section 4, 5; plan 23.2, 24.3): the pure rules of the auto mode and of the clearing with a pause. Nothing here touches the game: the controller reads the world,
    // builds the input, and executes the action that comes back. No Unity, no reflection.
    internal struct StrikeAutoVariant { public int Index; public string Kind; public bool Red, AllAirborne; public string Block; }   // Block: null | gate | launch_land | no_rows | wait_no_sea

    internal sealed class StrikeAutoInput
    {
        public bool On; public int Waves, MaxWaves;
        public string Plan;                  // none | draft | armed | stale | executing | cancelled
        public bool ExecRunning, ReplanQueued;
        public double Now, LastReplanT = -1;
        public int LiveDeck;                 // live deck orders (failed / no_reply / cancelled not counted)
        public string ClearStage;            // null or StrikeClearState.Stage
        public int TargetsLeft, Members, Missiles;   // valid not-sunk targets of the set; members; strike missiles of the airborne members
        public int Chosen = -1; public bool ChosenByPlayer;
        public IList<StrikeAutoVariant> Variants;
    }

    internal sealed class StrikeAutoAction { public string Kind = "none"; public string Why; public int Variant = -1; public bool Red; }   // none | wait | replan | launch | stop

    internal sealed class StrikeClearInput
    {
        public string Stage, Neighbour;      // Neighbour: destroyed | alive | gone | unknown
        public int NeighbourHits;            // own hits on the neighbour (the ledger): gone with hits = sunk (Fate)
        public bool ExecRunning, Auto, Stage1Missiles;
        public int Waves, MaxWaves;
        public double Now, TimeoutT, FuelT;  // FuelT -1 = no fuel bound
    }

    internal static class StrikeAutoMath
    {
        private static StrikeAutoAction Act(string kind, string why, int variant = -1, bool red = false) { return new StrikeAutoAction { Kind = kind, Why = why, Variant = variant, Red = red }; }

        /// <summary>A clear_pause that is still running (the auto waits for it); timeout | fuel | lost are final: the player decides.</summary>
        public static bool ClearActive(string stage) { return stage == "stage1" || stage == "wait_sunk" || stage == "release" || stage == "released" || stage == "red_wait" || stage == "stale_wait"; }

        /// <summary>The rules of plan 23.2, the first that fires. Pure: the controller executes the action.</summary>
        public static StrikeAutoAction Decide(StrikeAutoInput x, StrikeParams p)
        {
            if (x == null || !x.On) return Act("none", null);
            if (x.ClearStage != null && ClearActive(x.ClearStage)) return Act("wait", "clear");
            if (x.ExecRunning || x.Plan == "executing") return Act("wait", "executing");
            if (x.TargetsLeft <= 0) return Act("stop", "no_targets");
            if (x.Members <= 0 && x.LiveDeck <= 0) return Act("stop", "empty_group");
            if (x.LiveDeck > 0) return Act("wait", "deck");
            if (x.ReplanQueued) return Act("wait", "replan");
            if (x.Plan == null || x.Plan == "none" || x.Plan == "stale" || x.Plan == "cancelled")
            {
                if (x.LastReplanT >= 0 && x.Now >= x.LastReplanT && x.Now - x.LastReplanT < StrikeCalib.AutoReplanGapS) return Act("wait", "replan_gap");
                return Act("replan", x.Plan ?? "none");
            }
            if (x.Variants == null || x.Variants.Count == 0) return Act("stop", x.Missiles <= 0 ? "no_missiles" : "no_plan");
            if (x.Waves >= x.MaxWaves) return Act("stop", "waves");
            bool red;
            int vi = Pick(x.Variants, x.Chosen, x.ChosenByPlayer, out red);
            if (vi < 0)
            {
                string block = null;
                foreach (var v in x.Variants) if (v.Block != null) { block = v.Block; break; }
                return Act("stop", "blocked:" + (block ?? "no_rows"));
            }
            StrikeAutoVariant pv = default(StrikeAutoVariant);
            foreach (var v in x.Variants) if (v.Index == vi) { pv = v; break; }
            if (!pv.AllAirborne) return Act("wait", "gather");
            return Act("launch", pv.Kind, vi, red);
        }

        /// <summary>The variant the auto launches: the choice of the player (this wave only, even a red one), else the first green one in the order of Variants, else the first red one (damage_only); -1 = none can be launched.</summary>
        public static int Pick(IList<StrikeAutoVariant> vs, int chosen, bool chosenByPlayer, out bool red)
        {
            red = false;
            if (vs == null) return -1;
            if (chosenByPlayer)
                foreach (var v in vs) if (v.Index == chosen && v.Block == null) { red = v.Red; return v.Index; }
            foreach (var v in vs) if (!v.Red && v.Block == null) return v.Index;
            foreach (var v in vs) if (v.Red && v.Block == null) { red = true; return v.Index; }
            return -1;
        }

        /// <summary>What the clearing does next (plan 24.3): none | release | rewave | draft_rewave | timeout | fuel | lost.</summary>
        public static string ClearStep(StrikeClearInput c)
        {
            if (c == null) return "none";
            if (c.ExecRunning)
                return c.FuelT >= 0 && c.Now >= c.FuelT ? "fuel" : "none";   // the timeout is checked only after the Finish, so the missiles in flight are not abandoned
            bool sunk = c.Neighbour == "destroyed" || (c.Neighbour == "gone" && c.NeighbourHits > 0);
            if (sunk) return "release";
            if (c.FuelT >= 0 && c.Now >= c.FuelT) return "fuel";
            if (c.Now >= c.TimeoutT) return "timeout";
            if (c.Neighbour == "gone") return "lost";
            if (c.Stage == "stage1" || c.Stage == "wait_sunk") return c.Auto && c.Waves < c.MaxWaves && c.Stage1Missiles ? "rewave" : "draft_rewave";
            return "none";
        }

        /// <summary>How long the stage-2 aircraft can wait at the gate: the fuel as the CAP reads it (path left before the return), minus the margin, minus the way gate -> launch point and back; -1 = not known.</summary>
        public static double FuelWaitS(StrikeAircraft a, GeoPt gate, GeoPt launch, StrikeParams p)
        {
            if (a == null || a.ReserveNm < 0) return -1;
            double kn = a.CruiseKn > 0 ? a.CruiseKn : 450;
            double left = a.ReserveNm - (p == null ? 5 : p.FuelMarginNm) - 2 * AswMath.DistNm(gate, launch);
            return Math.Max(0, left) / kn * 3600.0;
        }

        // ---- self test ----

        private static StrikeAutoVariant V(int i, string kind, bool red = false, bool air = true, string block = null) { return new StrikeAutoVariant { Index = i, Kind = kind, Red = red, AllAirborne = air, Block = block }; }

        private static StrikeAutoInput In(params StrikeAutoVariant[] vs)
        {
            return new StrikeAutoInput { On = true, Plan = "draft", Now = 1000, MaxWaves = 3, TargetsLeft = 1, Members = 2, Missiles = 8, Variants = vs, Chosen = 0 };
        }

        private static string K(StrikeAutoAction a) { return a.Kind + " " + a.Why + " v=" + a.Variant + " red=" + a.Red; }

        public static string SelfTest(string part = "all")
        {
            string e;
            if ((part == "all" || part == "decide") && (e = SelfTestDecide()) != null) return "decide: " + e;
            if ((part == "all" || part == "pick") && (e = SelfTestPick()) != null) return "pick: " + e;
            if ((part == "all" || part == "limit") && (e = SelfTestLimit()) != null) return "limit: " + e;
            if ((part == "all" || part == "deck") && (e = SelfTestDeck()) != null) return "deck: " + e;
            if ((part == "all" || part == "stop") && (e = SelfTestStop()) != null) return "stop: " + e;
            if ((part == "all" || part == "clear") && (e = SelfTestClear()) != null) return "clear: " + e;
            if ((part == "all" || part == "fuel") && (e = SelfTestFuel()) != null) return "fuel: " + e;
            return null;
        }

        private static string SelfTestDecide()
        {
            var p = new StrikeParams();
            var x = In(V(0, "min_missiles")); x.On = false;
            if (Decide(x, p).Kind != "none") return "off";
            x = In(V(0, "min_missiles")); x.ClearStage = "wait_sunk";
            var a = Decide(x, p); if (a.Kind != "wait" || a.Why != "clear") return "clear " + K(a);
            x.ClearStage = "timeout"; x.Variants = new[] { V(0, "min_missiles") };
            a = Decide(x, p); if (a.Kind != "launch") return "a final clear does not hold the auto " + K(a);
            x = In(V(0, "min_missiles")); x.ExecRunning = true;
            a = Decide(x, p); if (a.Kind != "wait" || a.Why != "executing") return "executing " + K(a);
            x = In(V(0, "min_missiles")); x.TargetsLeft = 0;
            a = Decide(x, p); if (a.Kind != "stop" || a.Why != "no_targets") return "no targets " + K(a);
            x = In(V(0, "min_missiles")); x.Members = 0;
            a = Decide(x, p); if (a.Kind != "stop" || a.Why != "empty_group") return "empty group " + K(a);
            x = In(V(0, "damage_only", true)); x.LiveDeck = 1;
            a = Decide(x, p); if (a.Kind != "wait" || a.Why != "deck") return "live order + red " + K(a);
            x = In(V(0, "min_missiles")); x.Members = 0; x.LiveDeck = 2;
            a = Decide(x, p); if (a.Kind != "wait" || a.Why != "deck") return "no members but live orders " + K(a);
            x = In(V(0, "min_missiles")); x.ReplanQueued = true;
            a = Decide(x, p); if (a.Kind != "wait" || a.Why != "replan") return "queued " + K(a);
            x = In(); x.Plan = "stale"; x.LastReplanT = -1;
            a = Decide(x, p); if (a.Kind != "replan" || a.Why != "stale") return "stale " + K(a);
            x = In(); x.Plan = "stale"; x.LastReplanT = 995;
            a = Decide(x, p); if (a.Kind != "wait" || a.Why != "replan_gap") return "stale 5 s after a replan " + K(a);
            x = In(); x.Plan = "stale"; x.LastReplanT = 985;
            a = Decide(x, p); if (a.Kind != "replan") return "stale 15 s after a replan " + K(a);
            x = In(); x.Plan = "none";
            a = Decide(x, p); if (a.Kind != "replan" || a.Why != "none") return "no plan " + K(a);
            x = In(); x.Missiles = 0;
            a = Decide(x, p); if (a.Kind != "stop" || a.Why != "no_missiles") return "no variants, no missiles " + K(a);
            x = In();
            a = Decide(x, p); if (a.Kind != "stop" || a.Why != "no_plan") return "no variants " + K(a);
            x = In(V(0, "min_missiles", false, false));
            a = Decide(x, p); if (a.Kind != "wait" || a.Why != "gather") return "gather " + K(a);
            x = In(V(0, "min_missiles"));
            a = Decide(x, p); if (a.Kind != "launch" || a.Variant != 0 || a.Red) return "green " + K(a);
            x = In(V(0, "damage_only", true));
            a = Decide(x, p); if (a.Kind != "launch" || !a.Red) return "red " + K(a);
            x = In(V(0, "min_missiles")); x.Plan = "armed";
            a = Decide(x, p); if (a.Kind != "launch") return "armed " + K(a);
            return null;
        }

        private static string SelfTestPick()
        {
            bool red;
            // the player chose a red card: it is launched (this wave only)
            var vs = new[] { V(0, "min_missiles"), V(1, "damage_only", true) };
            if (Pick(vs, 1, true, out red) != 1 || !red) return "the choice of the player, even red";
            if (Pick(vs, 1, false, out red) != 0 || red) return "no player choice: the first green";
            vs = new[] { V(0, "damage_only", true), V(1, "max_targets"), V(2, "min_missiles") };
            if (Pick(vs, 0, false, out red) != 1 || red) return "the first green in the order of the plan";
            vs = new[] { V(0, "damage_only", true) };
            if (Pick(vs, 0, false, out red) != 0 || !red) return "no green: damage_only";
            vs = new[] { V(0, "min_missiles", false, true, "gate"), V(1, "damage_only", true, true, "launch_land") };
            if (Pick(vs, 0, true, out red) != -1) return "all blocked";
            vs = new[] { V(0, "clear_pause"), V(1, "damage_only", true) };
            if (Pick(vs, 0, false, out red) != 0 || red) return "a green clear_pause before the red damage_only";
            vs = new[] { V(0, "min_missiles", false, true, "gate"), V(1, "flank") };
            if (Pick(vs, 0, true, out red) != 1) return "the choice of the player is blocked: the next launchable";
            if (Pick(null, 0, false, out red) != -1 || Pick(new StrikeAutoVariant[0], 0, false, out red) != -1) return "no variants";
            return null;
        }

        private static string SelfTestLimit()
        {
            var p = new StrikeParams();
            var x = In(V(0, "min_missiles")); x.Waves = 3; x.MaxWaves = 3;
            var a = Decide(x, p); if (a.Kind != "stop" || a.Why != "waves") return "3 of 3 " + K(a);
            x = In(V(0, "min_missiles")); x.Waves = 1; x.MaxWaves = 1;
            a = Decide(x, p); if (a.Kind != "stop" || a.Why != "waves") return "1 of 1 " + K(a);
            x = In(V(0, "min_missiles")); x.Waves = 2; x.MaxWaves = 3;
            a = Decide(x, p); if (a.Kind != "launch") return "2 of 3 " + K(a);
            x = In(V(0, "min_missiles")); x.Waves = 3; x.MaxWaves = 3; x.Plan = "stale";
            a = Decide(x, p); if (a.Kind != "replan") return "the limit is checked on the plan that exists, not before the replan " + K(a);
            x = In(V(0, "min_missiles", false, false)); x.Waves = 3; x.MaxWaves = 3;
            a = Decide(x, p); if (a.Kind != "stop" || a.Why != "waves") return "the limit before the gather " + K(a);
            return null;
        }

        private static string SelfTestDeck()
        {
            var p = new StrikeParams();
            foreach (bool red in new[] { false, true })
                foreach (string plan in new[] { "draft", "armed", "stale", "none" })
                {
                    var x = In(V(0, red ? "damage_only" : "min_missiles", red)); x.LiveDeck = 1; x.Plan = plan;
                    var a = Decide(x, p);
                    if (a.Kind != "wait" || a.Why != "deck") return "live order first: red=" + red + " plan=" + plan + " " + K(a);
                }
            var y = In(V(0, "min_missiles")); y.LiveDeck = 0;
            if (Decide(y, p).Kind != "launch") return "no live order: launch";
            return null;
        }

        private static string SelfTestStop()
        {
            var p = new StrikeParams();
            var cases = new List<(StrikeAutoInput x, string kind, string why)>();
            var x1 = In(V(0, "min_missiles")); x1.TargetsLeft = 0; cases.Add((x1, "stop", "no_targets"));
            var x2 = In(V(0, "min_missiles")); x2.Members = 0; cases.Add((x2, "stop", "empty_group"));
            var x3 = In(); x3.Missiles = 0; cases.Add((x3, "stop", "no_missiles"));
            var x4 = In(); cases.Add((x4, "stop", "no_plan"));
            var x5 = In(V(0, "min_missiles")); x5.Waves = 3; cases.Add((x5, "stop", "waves"));
            var x6 = In(V(0, "min_missiles", false, true, "gate")); cases.Add((x6, "stop", "blocked:gate"));
            var x7 = In(V(0, "min_missiles", false, true, "wait_no_sea")); cases.Add((x7, "stop", "blocked:wait_no_sea"));
            var x8 = In(V(0, "min_missiles", false, true, "no_rows")); cases.Add((x8, "stop", "blocked:no_rows"));
            // waits are never stops
            var w1 = In(V(0, "min_missiles")); w1.LiveDeck = 2; cases.Add((w1, "wait", "deck"));
            var w2 = In(V(0, "min_missiles", false, false)); cases.Add((w2, "wait", "gather"));
            var w3 = In(V(0, "min_missiles")); w3.ReplanQueued = true; cases.Add((w3, "wait", "replan"));
            var w4 = In(V(0, "min_missiles")); w4.ClearStage = "release"; cases.Add((w4, "wait", "clear"));
            var w5 = In(V(0, "min_missiles")); w5.ExecRunning = true; cases.Add((w5, "wait", "executing"));
            foreach (var c in cases)
            {
                var a = Decide(c.x, p);
                if (a.Kind != c.kind || a.Why != c.why) return c.why + " -> " + K(a);
            }
            return null;
        }

        private static StrikeClearInput Ci(string stage = "stage1", string nb = "alive")
        {
            return new StrikeClearInput { Stage = stage, Neighbour = nb, Now = 1000, TimeoutT = 1900, FuelT = -1, MaxWaves = 3, Waves = 1, Auto = true, Stage1Missiles = true };
        }

        private static string SelfTestClear()
        {
            var c = Ci("stage1", "destroyed");
            if (ClearStep(c) != "release") return "destroyed after stage 1";
            c = Ci("stage1", "destroyed"); c.ExecRunning = true;
            if (ClearStep(c) != "none") return "destroyed while stage 1 flies";
            c = Ci("stage1", "gone"); c.NeighbourHits = 2;
            if (ClearStep(c) != "release") return "gone with hits";
            c = Ci("stage1", "gone"); c.NeighbourHits = 0;
            if (ClearStep(c) != "lost") return "gone without hits";
            c = Ci("stage1", "alive");
            if (ClearStep(c) != "rewave") return "alive in the auto";
            c = Ci("wait_sunk", "alive"); c.Waves = 3;
            if (ClearStep(c) != "draft_rewave") return "alive at 3 of 3: a draft";
            c = Ci("wait_sunk", "alive"); c.Auto = false;
            if (ClearStep(c) != "draft_rewave") return "alive without the auto: a draft";
            c = Ci("wait_sunk", "alive"); c.Stage1Missiles = false;
            if (ClearStep(c) != "draft_rewave") return "alive and no missiles of stage 1: a draft";
            c = Ci("stage1", "alive"); c.Now = 1900;
            if (ClearStep(c) != "timeout") return "timeout";
            c = Ci("stage1", "alive"); c.Now = 1900; c.ExecRunning = true;
            if (ClearStep(c) != "none") return "the timeout only after the Finish";
            c = Ci("stage1", "alive"); c.FuelT = 1000; c.ExecRunning = true;
            if (ClearStep(c) != "fuel") return "fuel even while the strike runs";
            c = Ci("wait_sunk", "alive"); c.FuelT = 999;
            if (ClearStep(c) != "fuel") return "fuel after the Finish";
            c = Ci("stage1", "destroyed"); c.FuelT = 900;
            if (ClearStep(c) != "release") return "sunk before the fuel counts: release";
            c = Ci("release", "alive");
            if (ClearStep(c) != "none") return "release stage waits for the replan";
            c = Ci("stage1", "unknown");
            if (ClearStep(c) != "rewave") return "unknown is alive";
            if (!ClearActive("red_wait") || !ClearActive("stale_wait") || !ClearActive("released") || ClearActive("timeout") || ClearActive("fuel") || ClearActive("lost") || ClearActive(null)) return "active stages";
            return null;
        }

        private static string SelfTestFuel()
        {
            var p = new StrikeParams();
            var a = new StrikeAircraft { ReserveNm = -1, CruiseKn = 450 };
            var g = new GeoPt(40, 10);
            if (FuelWaitS(a, g, g, p) != -1) return "unknown reserve";
            a.ReserveNm = 300;
            var l = AswMath.Offset(g, 0, 40);
            double w = FuelWaitS(a, g, l, p);
            if (Math.Abs(w - (300 - 5 - 80) / 450.0 * 3600) > 1) return "formula " + w.ToString("0.0", CultureInfo.InvariantCulture);
            a.ReserveNm = 50;
            if (FuelWaitS(a, g, l, p) != 0) return "no fuel left: 0";
            return null;
        }
    }
}
