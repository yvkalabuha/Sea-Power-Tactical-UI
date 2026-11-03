using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-100 A2-A5: AirEvasionController on the fake game.</summary>
    internal static class AirEvasionControllerTests
    {
        private sealed class Rig
        {
            public AirEvasionFakeGame G = new AirEvasionFakeGame();
            public AirEvasionParams P = new AirEvasionParams();
            public AirEvasionController C;
            public Rig() { C = new AirEvasionController(G, P); }
            public string Press(out string mode) { return C.Button(AirEvasionFakeGame.Uid, 100, out mode); }
            public string Press() { string m; return C.Button(AirEvasionFakeGame.Uid, 100, out m); }
        }

        private static string Req(bool ok, string msg) { return ok ? null : msg; }

        private static string Dump(Rig r) { return " calls=[" + string.Join(",", r.G.Calls) + "] logs=[" + string.Join(" | ", r.G.Logs) + "]"; }

        /// <summary>A large unit with the fake missile: the plain combo run.</summary>
        private static Rig Combo()
        {
            var r = new Rig(); r.G.U.IsLarge = true;
            string mode; string why = r.Press(out mode);
            if (why != null || mode != "combo_missile") throw new Exception("combo setup: " + why + "/" + mode);
            return r;
        }

        private static Rig Beam()
        {
            var r = new Rig(); r.G.M = null; r.G.Threat = new AeThreat { TrackId = 5, BearingDeg = 45, RangeNm = 30 };
            string mode; string why = r.Press(out mode);
            if (why != null || mode != "beam") throw new Exception("beam setup: " + why + "/" + mode);
            return r;
        }

        // ------------------------------------------------------------ button

        public static string ButtonNative()
        {
            var r = new Rig(); string mode;
            string why = r.Press(out mode);
            return Req(why == null && mode == "native", "native expected: " + why + "/" + mode)
                ?? Req(r.G.Count("ForceNative") == 1 && r.G.Count("Remember") == 1, "force + remember once" + Dump(r))
                ?? Req(r.G.Count("Supersede:true") == 1, "supersede(restoreRoute=true)" + Dump(r))
                ?? Req(r.G.Count("PlaceCourse") == 0 && r.G.Count("SetTelegraph") == 0, "native must not steer" + Dump(r))
                ?? Req(r.C.Holds(AirEvasionFakeGame.Uid) && r.C.RunCount == 1, "run exists");
        }

        public static string ButtonLarge()
        {
            var r = new Rig(); r.G.U.IsLarge = true; string mode;
            string why = r.Press(out mode);
            return Req(why == null && mode == "combo_missile", "combo expected: " + why + "/" + mode)
                ?? Req(r.G.Count("ForceNative") == 0, "large unit must not be forced" + Dump(r))
                ?? Req(r.G.Count("SetTelegraph:4") == 1 && r.G.Count("SetAltPreset:0") == 1 && r.G.Count("PlaceCourse:90") == 1, "combo orders" + Dump(r))
                ?? Req(r.G.Count("Supersede:false") == 1 && r.G.Count("LaunchChaff") == 1, "supersede(false) + chaff" + Dump(r));
        }

        public static string ButtonNoNativeCap()
        {
            var r = new Rig(); r.G.NativeOkValue = false; string mode;
            string why = r.Press(out mode);
            return Req(why == null && mode == "combo_missile" && r.G.Count("ForceNative") == 0, "no native capability -> combo" + Dump(r));
        }

        public static string ButtonBeam()
        {
            var r = new Rig(); r.G.M = null; r.G.Threat = new AeThreat { TrackId = 5, BearingDeg = 45, RangeNm = 30 }; string mode;
            string why = r.Press(out mode);
            return Req(why == null && mode == "beam", "beam expected: " + why + "/" + mode)
                ?? Req(r.G.Count("PlaceCourse:135") == 1 || r.G.Count("PlaceCourse:315") == 1, "course perpendicular to 45" + Dump(r))
                ?? Req(r.G.Count("SetTelegraph:4") == 1 && r.G.Count("SetAltPreset:0") == 1, "beam speed/altitude" + Dump(r));
        }

        public static string ButtonSkips()
        {
            var r = new Rig(); r.G.U.IsAircraft = false;
            string why = r.Press();
            if (why != "not_aircraft") return "not_aircraft: " + why;
            r = new Rig(); r.G.U.AttackRun = true;
            if ((why = r.Press()) != "attack_run") return "attack_run: " + why;
            r = new Rig(); r.Press();
            if ((why = r.Press()) != "already") return "already: " + why;
            r = new Rig(); r.G.M = null;
            if ((why = r.Press()) != "no_threat") return "no_threat: " + why;
            r = new Rig(); r.P.Enabled = false;
            if ((why = r.Press()) != "unsupported") return "unsupported: " + why;
            return Req(r.G.Calls.Count == 0, "a skip must not order anything" + Dump(r))
                ?? Req(r.G.Logged("skipped"), "skip not logged");
        }

        public static string ButtonPlayerControlOk()
        {
            var r = new Rig(); r.G.U.PlayerControl = true; string mode;
            string why = r.Press(out mode);
            return Req(why == null && mode == "native", "manual control must not block the button: " + why);
        }

        // ------------------------------------------------------------ native / auto

        public static string NativeEnd()
        {
            var r = new Rig(); r.Press();
            r.C.Tick(101);
            if (r.C.RunCount != 1) return "native state active: run must stay";
            r.G.U.StateName = "default";
            r.C.Tick(102);
            return Req(r.C.RunCount == 0, "run must end on state exit")
                ?? Req(r.G.Count("Restore") == 0, "native end must not restore" + Dump(r))
                ?? Req(r.G.Logged("reason=state_exit"), "end state_exit not logged" + Dump(r));
        }

        public static string NativeFailed()
        {
            var r = new Rig(); r.G.ForceSetsState = false; r.Press();
            r.C.Tick(101);
            return Req(r.C.RunCount == 1, "run must go on as combo")
                ?? Req(r.G.Logged("native_failed"), "native_failed not logged" + Dump(r))
                ?? Req(r.G.Count("PlaceCourse") == 1 && r.G.Count("SetTelegraph:4") == 1, "combo actuation after failure" + Dump(r));
        }

        public static string NativeCeaseFire()
        {
            var r = new Rig(); r.Press();
            r.C.Tick(101);
            r.G.U.StateName = "default"; r.G.M.IgnoredByGame = true;
            r.C.Tick(102);
            r.G.Auto.Add(AirEvasionFakeGame.Uid);
            r.C.Tick(103);
            return Req(r.C.RunCount == 0, "run must end")
                ?? Req(r.G.Logged("reason=cease_fire"), "end cease_fire not logged" + Dump(r))
                ?? Req(r.G.Count("ForceNative") == 1, "an ignored missile must not start a new run" + Dump(r));
        }

        public static string AutoStart()
        {
            var r = new Rig(); r.G.Auto.Add(AirEvasionFakeGame.Uid);
            r.C.Tick(100);                                   // 20 nm at 900 kn closing = 80 s
            if (r.C.RunCount != 0) return "too early: tti 80 s";
            r.G.MissileAt(0, 5, 600);                        // 5 nm / 900 kn = 20 s
            r.C.Tick(101);
            return Req(r.C.RunCount == 1 && r.G.Count("ForceNative") == 1, "auto start by tti" + Dump(r))
                ?? Req(r.G.Logged("by=auto"), "by=auto not logged" + Dump(r));
        }

        /// <summary>T-348: AeUnit.Globe reaches the math. Missile 0.15 deg east at 60 deg: globe 4.5 nm (tti 13.5 s), flat 9 nm (tti 27 s).</summary>
        public static string GlobeFlag()
        {
            string res = null;
            foreach (bool globe in new[] { true, false })
            {
                var r = new Rig(); r.G.Auto.Add(AirEvasionFakeGame.Uid);
                r.G.U.Lat = 60; r.G.U.Lon = 10; r.G.U.Globe = globe;
                r.G.M = new AeMissile { Key = "m1", Lat = 60, Lon = 10.15, Kn = 1200, CourseDeg = 270, TargetsUnit = true };
                r.C.Tick(100);
                res = res ?? Req((r.C.RunCount == 1) == globe, "globe=" + globe + " must " + (globe ? "start" : "wait") + Dump(r));
            }
            return res;
        }

        public static string AutoNoMissile()
        {
            var r = new Rig(); r.G.Auto.Add(AirEvasionFakeGame.Uid); r.G.M = null;
            r.G.Threat = new AeThreat { TrackId = 5, BearingDeg = 45, RangeNm = 2 };
            for (int i = 0; i < 5; i++) r.C.Tick(100 + i);
            return Req(r.C.RunCount == 0 && r.G.Calls.Count == 0, "auto must never start without a missile" + Dump(r));
        }

        public static string AutoCooldown()
        {
            var r = new Rig(); r.G.Auto.Add(AirEvasionFakeGame.Uid); r.G.MissileAt(0, 5, 600);
            r.C.Tick(100); r.C.Tick(101);
            r.G.U.StateName = "default";
            r.C.Tick(102);                                   // run ends at 102
            if (r.C.RunCount != 0) return "run must end";
            r.C.Tick(105);
            if (r.C.RunCount != 0) return "same missile within the cooldown must wait" + Dump(r);
            r.C.Tick(112.5);
            if (r.C.RunCount != 1) return "same missile after the cooldown must start" + Dump(r);
            r.C.Tick(113); r.G.U.StateName = "default"; r.C.Tick(114);
            if (r.C.RunCount != 0) return "second run must end" + Dump(r);
            r.G.MissileAt(0, 5, 600, "m2");
            r.C.Tick(115);
            return Req(r.C.RunCount == 1, "a new missile starts at once" + Dump(r));
        }

        public static string AutoMuted()
        {
            var r = new Rig(); r.G.U.IsLarge = true; r.Press();
            r.C.Release(AirEvasionFakeGame.Uid);
            r.G.Auto.Add(AirEvasionFakeGame.Uid); r.G.MissileAt(0, 5, 600);
            r.C.Tick(200);
            if (r.C.RunCount != 0) return "a muted missile must not start" + Dump(r);
            r.G.MissileAt(0, 5, 600, "m2");
            r.C.Tick(201);
            return Req(r.C.RunCount == 1, "another missile starts" + Dump(r));
        }

        // ------------------------------------------------------------ combo / beam

        private static void MoveMissile(Rig r, double bearing) { r.G.MissileAt(bearing, 20, 600); }

        public static string ComboCourse()
        {
            var r = Combo();
            r.G.U.HeadingDeg = 90;
            MoveMissile(r, 5); r.C.Tick(101);
            if (r.G.Count("PlaceCourse") != 1) return "5 deg change must not re-place" + Dump(r);
            MoveMissile(r, 15); r.C.Tick(102);
            return Req(r.G.Count("PlaceCourse") == 2 && r.G.Count("PlaceCourse:105") == 1, "15 deg change re-places" + Dump(r));
        }

        public static string ComboEnd()
        {
            var r = Combo();
            r.G.M = null; r.C.Tick(101);
            return Req(r.C.RunCount == 0, "run must end when the missile is gone")
                ?? Req(r.G.Count("Restore") == 1 && ReferenceEquals(r.G.RestoredWith, r.G.SavedGiven), "restore with the remembered state" + Dump(r))
                ?? Req(r.G.Logged("reason=missile_gone"), "missile_gone not logged");
        }

        public static string Overridden()
        {
            var r = Combo();
            r.G.RouteChanged = true; r.C.Tick(101);
            return Req(r.C.RunCount == 0 && r.G.Count("Restore") == 0, "overridden: no restore" + Dump(r))
                ?? Req(r.G.Logged("reason=overridden"), "overridden not logged");
        }

        public static string ReplaceAfterGuard()
        {
            var r = Combo();
            r.G.RouteChanged = true; r.C.RequestReplace(AirEvasionFakeGame.Uid); r.C.Tick(101);
            return Req(r.C.RunCount == 1 && r.G.Count("PlaceCourse") == 2 && r.G.Count("Restore") == 0, "guard replace re-places, not overridden" + Dump(r));
        }

        public static string BeamUpgrade()
        {
            var r = Beam();
            r.G.MissileAt(0, 20, 600); r.C.Tick(101);
            return Req(r.C.RunCount == 1 && r.G.Count("ForceNative") == 1, "upgrade to native" + Dump(r))
                ?? Req(r.G.Count("Restore") == 0 && r.G.Count("Remember") == 1, "same memory, no restore" + Dump(r))
                ?? Req(r.C.DiagJson(5).Contains("\"mode\":\"native\""), "mode native");
        }

        public static string BeamQuiet()
        {
            var r = Beam();
            r.G.Threat = null;
            r.C.Tick(129);
            if (r.C.RunCount != 1) return "29 s quiet goes on";
            r.C.Tick(130);
            return Req(r.C.RunCount == 0 && r.G.Count("Restore") == 1, "30 s quiet restores" + Dump(r))
                ?? Req(r.G.Logged("reason=threat_gone"), "threat_gone not logged");
        }

        public static string BeamTimeout()
        {
            var r = Beam();
            for (int t = 130; t < 280; t += 30) r.C.Tick(t);
            if (r.C.RunCount != 1) return "run must last until 180 s" + Dump(r);
            r.C.Tick(280);
            return Req(r.C.RunCount == 0 && r.G.Count("Restore") == 1, "timeout restores" + Dump(r))
                ?? Req(r.G.Logged("reason=timeout"), "timeout not logged");
        }

        public static string ChaffOnce()
        {
            var r = Combo();
            for (int i = 1; i <= 5; i++) r.C.Tick(100 + i);
            return Req(r.G.Count("LaunchChaff") == 1, "one chaff salvo per run" + Dump(r));
        }

        public static string ChaffHold()
        {
            var r = new Rig(); r.G.U.IsLarge = true; r.G.U.Roe = "hold";
            r.Press();
            for (int i = 1; i <= 5; i++) r.C.Tick(100 + i);
            return Req(r.G.Count("LaunchChaff") == 0, "ROE hold: no mod chaff" + Dump(r))
                ?? Req(r.G.HintCount("chaff_hold") == 1, "hint once per run, got " + r.G.HintCount("chaff_hold"));
        }

        public static string OptionHintOnce()
        {
            var r = new Rig(); r.G.OptionOn = false; r.G.U.IsLarge = true;
            r.Press(); r.G.M = null; r.C.Tick(101);
            r.G.MissileAt(0, 20, 600); r.Press();
            if (r.G.HintCount("option_off") != 1) return "one hint per mission, got " + r.G.HintCount("option_off");
            r = new Rig(); r.G.OptionOn = false; r.P.OptionHint = false; r.Press();
            if (r.G.HintCount("option_off") != 0) return "OptionHint=false: no hint";
            r = new Rig(); r.G.OptionOn = null; r.Press();
            return Req(r.G.HintCount("option_off") == 0, "unknown option: no hint");
        }

        // ------------------------------------------------------------ release / leave / diag

        public static string ReleaseCombo()
        {
            var r = Combo();
            r.C.Release(AirEvasionFakeGame.Uid);
            r.G.Auto.Add(AirEvasionFakeGame.Uid); r.G.MissileAt(0, 5, 600); r.C.Tick(105);
            return Req(r.C.RunCount == 0 && r.G.Count("Restore") == 1, "release restores" + Dump(r))
                ?? Req(r.G.Logged("reason=released"), "end released not logged")
                ?? Req(r.G.Count("ForceNative") == 0, "the missile is muted" + Dump(r));
        }

        public static string ReleaseNative()
        {
            var r = new Rig(); r.Press();
            r.C.Release(AirEvasionFakeGame.Uid);
            return Req(r.C.RunCount == 0 && r.G.Count("FinishNative") == 1 && r.G.Count("Restore") == 0, "release finishes native" + Dump(r))
                ?? Req(r.G.Logged("reason=released"), "end released not logged");
        }

        public static string ReleaseFlagKept()
        {
            var r = Combo();
            r.C.Release(AirEvasionFakeGame.Uid);
            foreach (string c in r.G.Calls) if (c.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) >= 0) return "the controller touched a flag: " + c;
            return Req(r.G.Auto.Count == 0, "unreachable");
        }

        public static string LeaveClears()
        {
            var r = new Rig(); r.G.OptionOn = false; r.G.U.IsLarge = true; r.Press();
            r.C.LeaveMission();
            if (r.C.RunCount != 0 || r.C.Holds(AirEvasionFakeGame.Uid)) return "runs must be cleared";
            r.G.Auto.Add(AirEvasionFakeGame.Uid); r.Press();
            return Req(r.G.HintCount("option_off") == 2, "hint flag cleared on leave, got " + r.G.HintCount("option_off"));
        }

        public static string Holds()
        {
            var r = new Rig();
            if (r.C.Holds(AirEvasionFakeGame.Uid)) return "no run yet";
            r.G.U.IsLarge = true; r.Press();
            if (!r.C.Holds(AirEvasionFakeGame.Uid)) return "run holds";
            r.G.M = null; r.C.Tick(101);
            return Req(!r.C.Holds(AirEvasionFakeGame.Uid), "run ended");
        }

        public static string PostLaunchSuperseded()
        {
            var r = new Rig(); r.G.SupersedeRoute = new List<double[]> { new[] { 1.0, 2.0 }, new[] { 3.0, 4.0 } };
            r.Press();
            if (r.G.Count("Supersede:true") != 1) return "native: restoreRoute true" + Dump(r);
            if (!ReferenceEquals(r.G.RememberOverride, r.G.SupersedeRoute)) return "the superseded route must go to Remember";
            r = new Rig(); r.G.U.IsLarge = true; r.G.SupersedeRoute = new List<double[]> { new[] { 1.0, 2.0 } };
            r.Press();
            return Req(r.G.Count("Supersede:false") == 1 && r.G.RememberOverride != null, "combo: restoreRoute false" + Dump(r));
        }

        public static string DiagJson()
        {
            var r = Combo();
            string j = r.C.DiagJson(10);
            var d = Json.Parse(j) as Dictionary<string, object>;
            if (d == null) return "diag json does not parse: " + j;
            var runs = d["runs"] as List<object>;
            return Req(runs != null && runs.Count == 1 && Convert.ToInt32(d["run_count"]) == 1, "diag content: " + j);
        }
    }
}
