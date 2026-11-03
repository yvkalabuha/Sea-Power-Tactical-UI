using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-101 A2-A5: SubEvasionController on the fake game.</summary>
    internal static class SubEvasionControllerTests
    {
        private static string Req(bool ok, string msg) { return ok ? null : msg; }

        private static bool Near(double a, double b, double e) { return Math.Abs(a - b) <= e; }

        private static SubEvasionController Mk(SubEvasionFakeGame g, Action<SubEvasionParams> tune = null)
        {
            var p = new SubEvasionParams();
            if (tune != null) tune(p);
            return new SubEvasionController(g, p);
        }

        private static void Ticks(SubEvasionController c, double from, double to)
        {
            for (double t = from; t <= to + 1e-9; t += 1) c.Tick(1, t);
        }

        private static int IndexOf(SubEvasionFakeGame g, string call) { return g.Calls.IndexOf(call); }

        // ---------------------------------------------------------------- A2

        public static string AutoStartNative()
        {
            var g = new SubEvasionFakeGame { }; g.U.StateName = "PlayerOverride"; g.U.StatePriority = 0;
            var c = Mk(g);
            c.Tick(1, 0);
            int rem = IndexOf(g, "Remember"), st = IndexOf(g, "StartNative");
            return Req(rem >= 0 && st > rem, "snapshot before StartNative: " + string.Join(",", g.Calls))
                ?? Req(c.Holds(1) && c.ModeOf(1) == "native", "native run")
                ?? Req(g.Events.Exists(e => e.StartsWith("auto_evade ", StringComparison.Ordinal) && e.Contains("\"kind\":\"sub\"") && e.Contains("\"phase\":\"start\"")), "event: " + string.Join(";", g.Events))
                ?? Req(g.Logs.Exists(l => l.Contains("sub start") && l.Contains("mode=native by=auto") && l.Contains("state_before=PlayerOverride")), "start log: " + string.Join(";", g.Logs));
        }

        public static string NativeConfirmFailedOwn()
        {
            var g = new SubEvasionFakeGame { NativeHolds = false };
            var c = Mk(g);
            c.Tick(1, 0);
            if (!c.Holds(1) || c.ModeOf(1) != "native") return "native run at t=0";
            c.Tick(1, 1);
            return Req(c.ModeOf(1) == "own", "mode after failed confirm: " + c.ModeOf(1))
                ?? Req(g.LogCount("sub switch_own uid=1 reason=start_failed") == 1, "switch_own log")
                ?? Req(g.Count("PlaceCourse") >= 1, "own course placed");
        }

        public static string NativeFailedNoOwn()
        {
            var g = new SubEvasionFakeGame { NativeHolds = false, OwnOkV = false };
            var c = Mk(g);
            c.Tick(1, 0); c.Tick(1, 1);
            return Req(!c.Holds(1), "run must end")
                ?? Req(g.LogCount("sub end uid=1 mode=native reason=start_failed") == 1, "end log: " + string.Join(";", g.Logs))
                ?? Req(g.Calls.Contains("Restore full=false"), "partial restore (PlayerOverride may have been cleared)");
        }

        private static SubEvasionController NativeRunning(SubEvasionFakeGame g, Action<SubEvasionParams> tune = null)
        {
            var c = Mk(g, tune);
            c.Tick(1, 0); c.Tick(1, 1);
            return c;
        }

        public static string NativeFinishedRestore()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            if (!c.Holds(1)) return "run lost";
            ForceState(g, "Default");
            Ticks(c, 2, 3);
            return Req(!c.Holds(1), "run must end")
                ?? Req(g.Calls.Contains("Restore full=true"), "full restore")
                ?? Req(g.LogCount("reason=finished") == 1, "finished log")
                ?? Req(g.Count("SetUnitAutoEvade") == 0, "flag untouched")
                ?? Req(g.LastRestored != null && g.LastRestored.CavForbidden == null, "native: cavitation limit not restored by the mod");
        }

        private static void ForceState(SubEvasionFakeGame g, string s) { g.U.StateName = s; }

        public static string NativeOverridden()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            ForceState(g, "PlayerOverride");
            Ticks(c, 2, 3);
            return Req(!c.Holds(1), "run must end")
                ?? Req(g.Calls.Contains("Restore full=false") && !g.Calls.Contains("Restore full=true"), "partial restore only: " + string.Join(",", g.Calls))
                ?? Req(g.LogCount("reason=overridden") == 1, "overridden log");
        }

        public static string RestoreOff()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g, p => p.Restore = false);
            ForceState(g, "Default");
            Ticks(c, 2, 3);
            return Req(!c.Holds(1), "run must end") ?? Req(g.Calls.Contains("Restore full=false") && !g.Calls.Contains("Restore full=true"), "no full restore: " + string.Join(",", g.Calls));
        }

        public static string FlagOffMidway()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            g.Flag = false;
            Ticks(c, 2, 6);
            if (!c.Holds(1)) return "the manoeuvre must not be interrupted when the flag goes off";
            ForceState(g, "Default");
            Ticks(c, 7, 8);
            return Req(!c.Holds(1) && g.Calls.Contains("Restore full=true"), "restore after the end");
        }

        public static string CooldownAfterEnd()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            ForceState(g, "Default");
            c.Tick(1, 2);                      // ends at t=2
            if (c.Holds(1)) return "run must end";
            c.Tick(1, 4);                      // 2 s later, threat again
            if (c.Holds(1)) return "cooldown: must not start 2 s after the end";
            c.Tick(1, 6);
            if (c.Holds(1)) return "cooldown: must not start 4 s after the end";
            c.Tick(1, 7.5);
            return Req(c.Holds(1), "start 5.5 s after the end")
                ?? Req(g.LogCount("sub skip uid=1 reason=cooldown") == 1, "cooldown skip logged once");
        }

        public static string TickPacing()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            ForceState(g, "Default");
            c.Tick(1, 1.5);
            if (!c.Holds(1)) return "tick 0.5 s after the previous must be ignored";
            c.Tick(1, 2.0);
            return Req(!c.Holds(1), "tick after 1 s");
        }

        public static string SkipReasonOnce()
        {
            var g = new SubEvasionFakeGame();
            g.T.Known = false;
            var c = Mk(g);
            Ticks(c, 0, 4);
            if (g.LogCount("sub skip uid=1 reason=no_known_torpedo") != 1) return "unknown torpedo logged " + g.LogCount("sub skip uid=1 reason=no_known_torpedo") + " times";
            g.T.Known = true; g.U.StateName = "Other"; g.U.StatePriority = 2;
            Ticks(c, 5, 8);
            if (g.LogCount("sub skip uid=1 reason=state_prio") != 1) return "state_prio logged " + g.LogCount("sub skip uid=1 reason=state_prio") + " times";
            return Req(!c.Holds(1) && g.Count("StartNative") == 0, "nothing started")
                ?? Req(g.LogCount("reason=no_flag") == 0 && g.LogCount("reason=no_threat") == 0, "idle reasons are not logged");
        }

        // ---------------------------------------------------------------- A3 noisemaker and depth

        public static string NmGameResetsTimer()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            g.U.NoisemakersLeft = 3;           // the game launched
            c.Tick(1, 2);
            if (g.LogCount("sub noisemaker uid=1 by=game left=3") != 1) return "game launch not seen";
            Ticks(c, 3, 31);
            if (g.Count("LaunchNoisemaker") != 0) return "mod launched at t<32 though the game launched at t=2";
            Ticks(c, 32, 32);
            return Req(g.Count("LaunchNoisemaker") == 1, "mod launches at 30 s after the game launch: " + g.Count("LaunchNoisemaker"))
                ?? Req(c.DiagJson(5).Contains("\"noisemaker_game\":1") && c.DiagJson(5).Contains("\"noisemaker_mod\":1"), c.DiagJson(5));
        }

        public static string NmModAfterGrace()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            Ticks(c, 2, 5);
            if (g.Count("LaunchNoisemaker") != 0) return "launch inside the 6 s window";
            Ticks(c, 6, 6);
            if (g.Count("LaunchNoisemaker") != 1) return "no launch at 6 s";
            Ticks(c, 7, 8);
            return Req(g.Count("LaunchNoisemaker") == 1 && g.LogCount("by=mod") == 1 && g.LogCount("by=game") == 0, "own launch counted as mod only: " + string.Join(";", g.Logs));
        }

        public static string NmNoDouble()
        {
            var g = new SubEvasionFakeGame();
            g.U.NoisemakersLeft = 10;
            var c = NativeRunning(g);
            var launches = new List<double>();
            for (double t = 2; t <= 75; t += 1)
            {
                int before = g.U.NoisemakersLeft;
                if (Near(t, 2, 0.01)) g.U.NoisemakersLeft--;          // the game launches
                int afterGame = g.U.NoisemakersLeft;
                if (afterGame < before) launches.Add(t);
                int calls = g.Count("LaunchNoisemaker");
                c.Tick(1, t);
                if (g.Count("LaunchNoisemaker") > calls) launches.Add(t);
            }
            for (int i = 1; i < launches.Count; i++)
                if (launches[i] - launches[i - 1] < 29.5) return "two launches " + (launches[i] - launches[i - 1]) + " s apart: " + string.Join(",", launches);
            return Req(launches.Count >= 3, "expected game + two mod launches: " + string.Join(",", launches));
        }

        public static string NmAssistOff()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g, p => p.NoisemakerAssist = false);
            Ticks(c, 2, 60);
            return Req(g.Count("LaunchNoisemaker") == 0, "assist off: no mod launch");
        }

        public static string NmUnknownCount()
        {
            var g = new SubEvasionFakeGame();
            g.U.NoisemakersLeft = -1;
            var c = NativeRunning(g);
            Ticks(c, 2, 60);
            return Req(g.Count("LaunchNoisemaker") == 0, "unknown count: no mod launch (cannot see the game's launch)")
                ?? Req(g.LogCount("by=game") == 0, "no game launch is invented");
        }

        public static string DeepDiveOffPreStartOwn()
        {
            var g = new SubEvasionFakeGame();
            g.T.TorpMaxDepthUnity = 1.0; g.T.Preset6Ft = 900; g.T.SeabedUnity = -10;
            var c = Mk(g, p => p.DeepDive = false);
            c.Tick(1, 0);
            return Req(c.ModeOf(1) == "own", "mode " + c.ModeOf(1))
                ?? Req(g.Count("StartNative") == 0, "native must not start")
                ?? Req(g.LogCount("sub switch_own uid=1 reason=deep_dive_off") == 1, "switch_own log");
        }

        public static string DeepDiveOffSeenPreset6Switch()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g, p => p.DeepDive = false);
            if (c.ModeOf(1) != "native") return "native expected before preset 6";
            g.U.Preset = 6;
            c.Tick(1, 2);
            return Req(c.ModeOf(1) == "own", "mode " + c.ModeOf(1))
                ?? Req(g.Count("FinishNative") == 1, "native state finished")
                ?? Req(g.LogCount("sub switch_own uid=1 reason=deep_dive_off") == 1, "switch_own log");
        }

        public static string DeepDiveOnNativeUntouched()
        {
            var g = new SubEvasionFakeGame();
            g.T.TorpMaxDepthUnity = 1.0; g.T.Preset6Ft = 900; g.T.SeabedUnity = -10;
            var c = NativeRunning(g);
            g.U.Preset = 6;
            Ticks(c, 2, 5);
            return Req(c.ModeOf(1) == "native", "mode " + c.ModeOf(1))
                ?? Req(g.Count("FinishNative") == 0 && g.CountPrefix("SetPreset") == 0 && g.CountPrefix("SetTelegraph") == 0 && g.Count("PlaceCourse") == 0, "no orders in native: " + string.Join(",", g.Calls));
        }

        // ---------------------------------------------------------------- A4 own

        public static string OwnModeSetting()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g, p => p.Mode = "own");
            c.Tick(1, 0);
            return Req(c.ModeOf(1) == "own", "mode " + c.ModeOf(1))
                ?? Req(g.Count("StartNative") == 0, "no native start")
                ?? Req(Near(EvasionMath.Diff(g.LastCourse, 300), 0, 0.5), "beam course " + g.LastCourse)
                ?? Req(g.Logs.Exists(l => l.Contains("mode=own by=auto")), "start log");
        }

        public static string OwnOrders()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g, p => { p.Mode = "own"; p.NoisemakerAssist = false; });
            c.Tick(1, 0);
            if (g.Count("SetTelegraph 4") != 1 || g.Count("SetPreset 5") != 1) return "far: telegraph 4, preset 5: " + string.Join(",", g.Calls);
            if (g.CountPrefix("SetCavitationForbidden") != 0) return "far: cavitation limit untouched";
            int placed = g.Count("PlaceCourse");
            g.U.Telegraph = 4; g.U.Preset = 5;
            c.Tick(1, 1);
            if (g.Count("PlaceCourse") != placed) return "course below 3 deg change must not be placed again";
            g.T.BrgDeg = 60; g.T.DistNm = 1.5;                      // close: stern, step 5, cavitation limit off
            c.Tick(1, 2);
            if (!Near(EvasionMath.Diff(g.LastCourse, 240), 0, 0.5)) return "stern course " + g.LastCourse;
            if (g.Count("SetTelegraph 5") != 1) return "close: telegraph 5";
            if (g.Count("SetCavitationForbidden false") != 1) return "close: cavitation limit off once";
            g.U.Telegraph = 5; g.U.CavForbidden = false;
            c.Tick(1, 3);
            if (g.CountPrefix("SetCavitationForbidden") != 1) return "cavitation limit set once";
            g.T.DistNm = 9; c.Tick(1, 4);
            g.T = null;
            Ticks(c, 5, 34);
            return Req(!c.Holds(1), "run ends 30 s after the last sight")
                ?? Req(g.Calls.Contains("Restore full=true"), "full restore")
                ?? Req(g.LastRestored != null && g.LastRestored.CavForbidden == true, "cavitation limit of the snapshot is given back");
        }

        public static string OwnFirstDecoy()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g, p => p.Mode = "own");
            c.Tick(1, 0);
            if (g.Count("LaunchNoisemaker") != 1) return "first noisemaker at once";
            Ticks(c, 1, 29);
            if (g.Count("LaunchNoisemaker") != 1) return "second noisemaker too early";
            Ticks(c, 30, 30);
            return Req(g.Count("LaunchNoisemaker") == 2, "second at 30 s: " + g.Count("LaunchNoisemaker"));
        }

        public static string OwnDone()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g, p => { p.Mode = "own"; p.NoisemakerAssist = false; });
            c.Tick(1, 0);
            g.T = null;
            Ticks(c, 1, 250);
            if (!c.Holds(1)) return "last distance 3 nm: the run goes on until the 5 min limit";
            Ticks(c, 251, 301);
            return Req(!c.Holds(1), "ends at the 5 min limit")
                ?? Req(g.LogCount("reason=finished") == 1, "finished log");
        }

        public static string OwnPlayerOverrideNotOverridden()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g, p => p.Mode = "own");
            c.Tick(1, 0);
            g.U.StateName = "PlayerOverride";
            Ticks(c, 1, 5);
            return Req(c.Holds(1), "our own waypoints put the game into PlayerOverride: not an override")
                ?? Req(g.LogCount("reason=overridden") == 0, "no overridden log");
        }

        // ---------------------------------------------------------------- A5 button, release, lifecycle

        public static string ButtonStartedIgnoresPrioAndCooldown()
        {
            var g = new SubEvasionFakeGame { Flag = false };
            g.U.StateName = "Other"; g.U.StatePriority = 2;
            var c = Mk(g);
            string r = c.Button(1, 10);
            if (r != "started") return "button: " + r;
            if (!g.Calls.Contains("SetUnitAutoEvade") || !g.Calls.Contains("TimeToNormal")) return "flag + time to normal";
            // cooldown: end it, press again right away
            c.Tick(1, 11);
            g.U.StateName = "Default";
            c.Tick(1, 12);
            if (c.Holds(1)) return "run must end";
            g.U.StateName = "Other";
            string again = c.Button(1, 13);
            return Req(again == "started", "button right after the end: " + again);
        }

        public static string ButtonFromPlayerOverride()
        {
            var g = new SubEvasionFakeGame();
            g.U.StateName = "PlayerOverride"; g.U.StatePriority = 0;
            g.Snap = new SubSnapshot { PlayerOverride = true, Telegraph = 2 };
            var c = Mk(g);
            string r = c.Button(1, 5);
            return Req(r == "started", "button: " + r) ?? Req(g.Count("StartNative") == 1, "native start") ?? Req(IndexOf(g, "Remember") < IndexOf(g, "StartNative"), "snapshot first");
        }

        public static string ButtonArmed()
        {
            var g = new SubEvasionFakeGame();
            g.T = null;
            var c = Mk(g);
            string r = c.Button(1, 5);
            if (r != "armed") return "no torpedo: " + r;
            if (!g.Calls.Contains("SetUnitAutoEvade") || !g.Calls.Contains("TimeToNormal")) return "flag + time to normal";
            g.T = new SubThreat { TrackId = 3, BrgDeg = 10, DistNm = 5, Known = false };
            string r2 = c.Button(1, 6);
            return Req(r2 == "armed" && !c.Holds(1) && g.Count("StartNative") == 0, "unknown torpedo: " + r2);
        }

        public static string ButtonAlready()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g);
            if (c.Button(1, 5) != "started") return "first press";
            if (c.Button(1, 6) != "already") return "second press";
            var g2 = new SubEvasionFakeGame();
            g2.U.StateName = "TorpedoEvasion";
            var c2 = Mk(g2);
            return Req(c2.Button(1, 5) == "already" && g2.Count("StartNative") == 0, "the game's own evasion is running");
        }

        public static string ButtonEmergencySkipped()
        {
            var g = new SubEvasionFakeGame();
            g.U.StateName = "EmergencySurface"; g.U.StatePriority = 1;
            var c = Mk(g);
            string r = c.Button(1, 5);
            return Req(r == "skipped:emergency_surface", "button: " + r) ?? Req(g.Count("StartNative") == 0 && !c.Holds(1), "nothing started");
        }

        public static string ButtonUnsupported()
        {
            var g = new SubEvasionFakeGame { NativeOkV = false, OwnOkV = false };
            var c = Mk(g);
            string r = c.Button(1, 5);
            var g2 = new SubEvasionFakeGame { UnitGone = true };
            string r2 = Mk(g2).Button(1, 5);
            return Req(r == "skipped:unsupported", "no members: " + r) ?? Req(r2 == "skipped:no_unit", "no unit: " + r2);
        }

        public static string ReleaseRestores()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            c.Release(1);
            if (c.Holds(1)) return "released run must be gone";
            if (g.Count("FinishNative") != 1 || !g.Calls.Contains("Restore full=true")) return "finish + restore: " + string.Join(",", g.Calls);
            if (g.LogCount("reason=released") != 1) return "released log";
            if (!c.DiagJson(5).Contains("\"released\":1")) return "counter";
            var g2 = new SubEvasionFakeGame();
            var c2 = Mk(g2, p => p.Mode = "own");
            c2.Tick(1, 0);
            c2.Release(1);
            return Req(g2.Count("FinishNative") == 0 && g2.Calls.Contains("Restore full=true") && !c2.Holds(1), "own: restore only");
        }

        public static string SweepUnitGone()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            g.UnitGone = true;
            c.Sweep(5);
            return Req(!c.Holds(1), "run must end") ?? Req(g.LogCount("reason=unit_gone") == 1, "unit_gone log") ?? Req(g.CountPrefix("Restore") == 0, "no restore of a gone unit");
        }

        public static string LeaveMission()
        {
            var g = new SubEvasionFakeGame();
            var c = NativeRunning(g);
            c.LeaveMission();
            if (c.Holds(1)) return "runs dropped";
            if (g.LogCount("reason=mission_left") != 1 || g.CountPrefix("Restore") != 0) return "log only, no restore";
            g.U.StateName = "Default";
            c.Tick(1, 2);
            return Req(c.Holds(1), "a new run may start at once in the next mission (no cooldown)");
        }

        public static string Holds()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g);
            if (c.Holds(1) || c.ModeOf(1) != null) return "before";
            c.Tick(1, 0);
            if (!c.Holds(1) || c.ModeOf(1) != "native") return "during";
            c.Tick(1, 1);
            g.U.StateName = "Default"; c.Tick(1, 2);
            return Req(!c.Holds(1) && c.ModeOf(1) == null, "after") ?? Req(!c.Holds(2), "other unit");
        }

        public static string DiagJson()
        {
            var g = new SubEvasionFakeGame();
            var c = Mk(g);
            c.Tick(1, 0); c.Tick(1, 1);            // run 1: native, confirmed
            g.U.StateName = "Default"; c.Tick(1, 2);   // finished
            g.U.NoisemakersLeft = 4;
            c.Tick(1, 8); c.Tick(1, 9);            // run 2
            g.U.StateName = "PlayerOverride"; c.Tick(1, 10);   // overridden
            string j = c.DiagJson(5);
            return Req(j.Contains("\"started\":2") && j.Contains("\"finished\":1") && j.Contains("\"overridden\":1") && j.Contains("\"released\":0")
                       && j.Contains("\"noisemaker_game\":0") && j.Contains("\"noisemaker_mod\":0") && j.Contains("\"run_count\":0"), j);
        }
    }
}
