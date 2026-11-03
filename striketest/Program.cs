using System;

namespace VoiceBotBridge
{
    // T-341 stand entry point. Prints "strike <name> selftest ok|FAILED: ..." per section.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            StrikeController.DefaultGoal = "breach";   // the v1 sections have no kill budget; the T-342 sections set sink themselves
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            bool all = a == "all";
            if (a == "kill-grid") { Console.Write(StrikeKillMath.Grid()); return 0; }
            if (a == "ad-grid") { Console.Write(StrikeAdMath.Grid()); return 0; }
            if (all || a == "air") bad += Report("air", StrikeAirMath.SelfTest());
            if (all || a == "ad") bad += Report("ad", StrikeAdMath.SelfTest("ad"));
            if (all || a == "pk") bad += Report("pk", StrikeAdMath.SelfTest("pk"));
            if (all || a == "calib") bad += Report("calib", StrikeAdMath.SelfTest("calib"));
            if (all || a == "calib-log") bad += Report("calib-log", StrikeCalibLog.SelfTest());
            if (all || a == "cache") bad += Report("cache", StrikeAdMath.SelfTest("cache"));
            if (all || a == "geo") bad += Report("geo", StrikeGeoMath.SelfTest());
            if (all || a == "tot") bad += Report("tot", StrikeTotMath.SelfTest());
            if (all || a == "prio") bad += Report("prio", StrikePlanMath.SelfTest("prio"));
            if (all || a == "plan") bad += Report("plan", StrikePlanMath.SelfTest("plan"));
            if (all || a == "plan-survivors") bad += Report("plan-survivors", StrikePlanMath.SelfTest("plan-survivors"));
            if (all || a == "plan-sink") bad += Report("plan-sink", StrikePlanMath.SelfTest("plan-sink"));
            if (all || a == "mix") bad += Report("mix", StrikePlanMath.SelfTest("mix"));
            if (all || a == "mix-e1b") bad += Report("mix-e1b", StrikePlanMath.SelfTest("mix-e1b"));
            if (all || a == "exec") bad += Report("exec", StrikeExecMath.SelfTest());
            if (all || a == "tofc") bad += Report("tofc", StrikeTofCache.SelfTest());
            if (a == "tof-band") bad += Report("tof-band", StrikeTofCache.SelfTest("band"));
            if (a == "tof-sup") bad += Report("tof-sup", StrikeTofCache.SelfTest("sup"));
            if (all || a == "wait") bad += Report("wait", StrikeWaitMath.SelfTest());
            if (all || a == "stale") bad += Report("stale", StrikeStaleMath.SelfTest());
            if (all || a == "book") bad += Report("book", StrikeLaunchBook.SelfTest());
            if (all || a == "veto") bad += Report("veto", EngageVetoChain.SelfTest());
            if (a == "t341") return Report("t341" + (args.Length > 1 ? " " + args[1] : ""), StrikeT341CtlTests.Run(args.Length > 1 ? args[1] : null));
            if (all) bad += Report("t341", StrikeT341CtlTests.Run(null));
            if (all || a == "kill" || a == "kill-h") bad += Report("kill-h", StrikeKillMath.SelfTest("h"));
            if (all || a == "kill" || a == "kill-p") bad += Report("kill-p", StrikeKillMath.SelfTest("p"));
            if (all || a == "kill" || a == "kill-need") bad += Report("kill-need", StrikeKillMath.SelfTest("need"));
            if (all || a == "kill" || a == "kill-mixed") bad += Report("kill-mixed", StrikeKillMath.SelfTest("mixed"));
            if (all || a == "kill" || a == "kill-residual") bad += Report("kill-residual", StrikeKillMath.SelfTest("residual"));
            if (all || a == "hit-ledger") bad += Report("hit-ledger", VoiceBotBridge.Ui.HitLedger.SelfTest());
            foreach (string part in new[] { "offers", "rank", "decide", "idem", "surplus", "track", "budget" })
                if (all || a == "deck-" + part) bad += Report("deck-" + part, StrikeDeckPlanner.SelfTest(part));
            if (all || a == "plan-probe") bad += Report("plan-probe", StrikePlanMath.SelfTest("probe"));
            foreach (string part in new[] { "decide", "pick", "limit", "deck", "stop", "clear", "fuel" })
                if (all || a == "auto-" + part) bad += Report("auto-" + part, StrikeAutoMath.SelfTest(part));
            if (all || a == "plan-clear") bad += Report("plan-clear", StrikePlanMath.SelfTest("plan-clear"));
            if (all || a == "exec-class") bad += Report("exec-class", StrikeExecMath.SelfTest("class"));
            if (all || a == "deck-noreply") bad += Report("deck-noreply", StrikeDeckPlanner.SelfTest("noreply"));
            if (all || a == "deck-launch-late") bad += Report("deck-launch-late", StrikeDeckPlanner.SelfTest("launch-late"));
            if (a == "t346") return Report("t346" + (args.Length > 1 ? " " + args[1] : ""), RunT346(args.Length > 1 ? args[1] : null));
            if (all) bad += Report("t346", RunT346(null));
            if (a == "t342") return Report("t342" + (args.Length > 1 ? " " + args[1] : ""), RunT342(args.Length > 1 ? args[1] : null));
            if (all) bad += Report("t342", RunT342(null));
            if (a == "t349") return Report("t349" + (args.Length > 1 ? " " + args[1] : ""), RunT349(args.Length > 1 ? args[1] : null));
            if (all) bad += Report("t349", RunT349(null));
            if (a == "t353") return Report("t353" + (args.Length > 1 ? " " + args[1] : ""), RunGroups(args.Length > 1 ? args[1] : null, new (string[] names, Func<string, string> run)[] { (StrikeT353Tests.Names, StrikeT353Tests.Dispatch) }));
            if (all) bad += Report("t353", RunGroups(null, new (string[] names, Func<string, string> run)[] { (StrikeT353Tests.Names, StrikeT353Tests.Dispatch) }));
            if (all || a == "probe") bad += Report("probe", StrikeProbe.SelfTest());
            foreach (string part in new[] { "eligibility", "sectors", "station", "timing", "decide", "return", "plan" })
                if (all || a == "ew" || a == "ew-" + part) bad += Report("ew-" + part, StrikeEwMath.SelfTest(part));
            foreach (string part in new[] { "threat", "assign", "standoff", "mode", "budget", "hyst", "role", "lat", "seeker" })
                if (all || a == "ewf" || a == "ewf-" + part) bad += Report("ewf-" + part, StrikeEwFighterMath.SelfTest(part));
            if (all || a == "ewlat") bad += Report("ewlat", StrikeEwMath.SelfTest("lat") ?? StrikeEwFighterMath.SelfTest("lat"));
            foreach (string part in new[] { "return-fighter", "reserve" })
                if (all || a == "ew-" + part) bad += Report("ew-" + part, StrikeEwMath.SelfTest(part));
            if (all || a == "types") bad += Report("types", StrikeTypes.SelfTest());
            return bad == 0 ? 0 : 1;
        }

        private static string RunT342(string only)
        {
            return RunGroups(only, new (string[] names, Func<string, string> run)[]
            {
                (StrikeT342PlanTests.Names, StrikeT342PlanTests.Dispatch), (StrikeT342ExecTests.Names, StrikeT342ExecTests.Dispatch),
                (StrikeT342EwTests.Names, StrikeT342EwTests.Dispatch), (StrikeT342KillTests.Names, StrikeT342KillTests.Dispatch),
                (StrikeT342DeckTests.Names, StrikeT342DeckTests.Dispatch), (StrikeT342AutoTests.Names, StrikeT342AutoTests.Dispatch),
                (StrikeT342EwfTests.Names, StrikeT342EwfTests.Dispatch),
            });
        }

        private static string RunT349(string only)
        {
            return RunGroups(only, new (string[] names, Func<string, string> run)[] { (StrikeT349Tests.Names, StrikeT349Tests.Dispatch) });
        }

        private static string RunT346(string only)
        {
            return RunGroups(only, new (string[] names, Func<string, string> run)[]
            {
                (StrikeT346Tests.Names, StrikeT346Tests.Dispatch), (StrikeT346EwfTests.Names, StrikeT346EwfTests.Dispatch),
            });
        }

        private static string RunGroups(string only, (string[] names, Func<string, string> run)[] groups)
        {
            bool found = only == null;
            foreach (var g in groups)
                foreach (string n in g.names)
                {
                    if (only != null && only != n) continue;
                    found = true;
                    string e;
                    try { e = g.run(n); } catch (Exception ex) { e = "exception " + ex; }
                    if (e != null) return n + ": " + e;
                }
            return found ? null : "unknown section " + only;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("strike " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
