using System;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-101 A1: SubEvasionMath cases.</summary>
    internal static class SubEvasionMathTests
    {
        private static string Req(bool ok, string msg) { return ok ? null : msg; }

        private static string Eq(string got, string want, string what) { return got == want ? null : what + ": got " + (got ?? "null") + ", want " + (want ?? "null"); }

        private static bool Near(double a, double b, double e) { return Math.Abs(a - b) <= e; }

        private static EvTorp T(double brg, double dist) { return new EvTorp { TrackId = 1, BrgDeg = brg, DistNm = dist }; }

        public static string StartAuto()
        {
            return Eq(SubEvasionMath.StartDecision(true, true, true, "Default", 5, 100, false), null, "all ok")
                ?? Eq(SubEvasionMath.StartDecision(false, true, true, "Default", 5, 100, false), "no_flag", "flag off")
                ?? Eq(SubEvasionMath.StartDecision(true, false, true, "Default", 5, 100, false), "no_threat", "no threat")
                ?? Eq(SubEvasionMath.StartDecision(true, true, false, "Default", 5, 100, false), "no_known_torpedo", "unknown torpedo")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "TorpedoEvasion", 2, 100, false), "already", "already evading")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "Default", null, 100, false), null, "unknown priority is allowed");
        }

        public static string StartPlayerOverride()
        {
            return Eq(SubEvasionMath.StartDecision(true, true, true, "PlayerOverride", 0, 100, false), null, "PlayerOverride starts")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "PlayerOverride", 1, 100, false), null, "PlayerOverride prio 1 starts");
        }

        public static string StartPrio()
        {
            return Eq(SubEvasionMath.StartDecision(true, true, true, "Whatever", 2, 100, false), "state_prio", "prio 2")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "Whatever", 1, 100, false), "state_prio", "prio 1")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "Whatever", 3, 100, false), null, "prio 3");
        }

        public static string StartCooldown()
        {
            return Eq(SubEvasionMath.StartDecision(true, true, true, "Default", 5, 4.9, false), "cooldown", "4.9 s after the end")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "Default", 5, 5.0, false), null, "5 s after the end");
        }

        public static string StartButton()
        {
            return Eq(SubEvasionMath.StartDecision(false, true, true, "Whatever", 2, 0, true), null, "button ignores flag, prio and pause")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "PlayerOverride", 0, 0, true), null, "button from PlayerOverride")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "TorpedoEvasion", 2, 0, true), "already", "button already")
                ?? Eq(SubEvasionMath.StartDecision(true, false, true, "Default", 5, 0, true), "no_threat", "button without threat")
                ?? Eq(SubEvasionMath.StartDecision(true, true, false, "Default", 5, 0, true), "no_known_torpedo", "button unknown torpedo");
        }

        public static string StartEmergency()
        {
            return Eq(SubEvasionMath.StartDecision(true, true, true, "EmergencySurface", 1, 100, false), "emergency_surface", "auto")
                ?? Eq(SubEvasionMath.StartDecision(true, true, true, "EmergencySurface", 1, 100, true), "emergency_surface", "button");
        }

        public static string NativeEnd()
        {
            return Eq(SubEvasionMath.NativeEnd("TorpedoEvasion"), null, "running")
                ?? Eq(SubEvasionMath.NativeEnd("PlayerOverride"), "overridden", "player override")
                ?? Eq(SubEvasionMath.NativeEnd("Default"), "finished", "default state")
                ?? Eq(SubEvasionMath.NativeEnd("EmergencySurface"), "finished", "other state")
                ?? Eq(SubEvasionMath.NativeEnd(null), null, "unknown state keeps running");
        }

        public static string LaunchSeen()
        {
            return Req(SubEvasionMath.LaunchSeen(3, 2), "3 -> 2")
                ?? Req(!SubEvasionMath.LaunchSeen(3, 3), "3 -> 3")
                ?? Req(!SubEvasionMath.LaunchSeen(2, 3), "2 -> 3 (rearm)")
                ?? Req(!SubEvasionMath.LaunchSeen(-1, 2), "unknown before")
                ?? Req(!SubEvasionMath.LaunchSeen(3, -1), "unknown now");
        }

        public static string AssistGrace()
        {
            return Req(!SubEvasionMath.AssistNow(5.9, 1e9, 30, true, 2), "5.9 s in the state")
                ?? Req(SubEvasionMath.AssistNow(6.0, 1e9, 30, true, 2), "6 s in the state");
        }

        public static string AssistInterval()
        {
            return Req(!SubEvasionMath.AssistNow(20, 29.9, 30, true, 2), "29.9 s since the launch")
                ?? Req(SubEvasionMath.AssistNow(20, 30, 30, true, 2), "30 s since the launch");
        }

        public static string AssistFar()
        {
            return Req(!SubEvasionMath.AssistNow(20, 1e9, 30, true, 4.1), "4.1 nm")
                ?? Req(SubEvasionMath.AssistNow(20, 1e9, 30, true, 4.0), "4 nm")
                ?? Req(!SubEvasionMath.AssistNow(20, 1e9, 30, false, 1), "torpedo unknown");
        }

        public static string Outdive()
        {
            // torpedo max depth 1.0 unity, preset 6 = 150 ft -> -0.68; seabed -5 unity: -0.68 <= -1.0-0.34 ? no. Deeper preset: 900 ft = -4.08 <= -1.34 yes
            return Req(SubEvasionMath.CanOutdive(1.0, 900, -10), "deep preset below the torpedo")
                ?? Req(!SubEvasionMath.CanOutdive(1.0, 150, -10), "shallow preset")
                ?? Req(!SubEvasionMath.CanOutdive(1.0, 900, -1.5), "seabed too shallow (-1.5 + 0.45 > -1.34)")
                ?? Req(SubEvasionMath.CanOutdive(1.0, 900, -5.0), "seabed deep enough")
                ?? Req(!SubEvasionMath.CanOutdive(null, 900, -10), "no torpedo depth")
                ?? Req(!SubEvasionMath.CanOutdive(1.0, null, -10), "no preset")
                ?? Req(!SubEvasionMath.CanOutdive(1.0, 900, null), "no seabed")
                ?? Req(!SubEvasionMath.CanOutdive(0, 900, -10), "torpedo depth 0");
        }

        public static string DeepDiveSwitch()
        {
            return Eq(SubEvasionMath.DeepDiveSwitch(true, 6, true), null, "dive on")
                ?? Eq(SubEvasionMath.DeepDiveSwitch(false, 6, false), "deep_dive_off", "preset 6 seen")
                ?? Eq(SubEvasionMath.DeepDiveSwitch(false, null, true), "deep_dive_off", "pre-start outdive")
                ?? Eq(SubEvasionMath.DeepDiveSwitch(false, 5, false), null, "preset 5")
                ?? Eq(SubEvasionMath.DeepDiveSwitch(false, null, false), null, "nothing known");
        }

        public static string OwnFar()
        {
            var s = new EvState();
            SubOwnOrder o = SubEvasionMath.Own(0, T(30, 3), null, s, false, false);
            return Req(Near(EvasionMath.Diff(o.CourseDeg, 300), 0, 0.5), "beam course (smaller turn) " + o.CourseDeg)
                ?? Req(o.Telegraph == 4 && o.Preset == 5 && !o.CavitationOff, "far: tg 4, preset 5, cavitation kept " + o.Telegraph + "/" + o.Preset);
        }

        public static string OwnClose()
        {
            var s = new EvState();
            SubOwnOrder o = SubEvasionMath.Own(0, T(30, 1.5), null, s, false, false);
            SubOwnOrder edge = SubEvasionMath.Own(0, T(30, 2.01), null, new EvState(), false, false);
            return Req(Near(EvasionMath.Diff(o.CourseDeg, 210), 0, 0.5), "stern course " + o.CourseDeg)
                ?? Req(o.Telegraph == 5 && o.Preset == 5 && o.CavitationOff, "close: tg 5, cavitation off")
                ?? Req(edge.Telegraph == 4, "just beyond CloseNm is still far");
        }

        public static string OwnDeep()
        {
            var s = new EvState();
            SubOwnOrder o = SubEvasionMath.Own(0, T(30, 3), null, s, true, true);
            SubOwnOrder off = SubEvasionMath.Own(0, T(30, 3), null, new EvState(), true, false);
            SubOwnOrder noDive = SubEvasionMath.Own(0, T(30, 3), null, new EvState(), false, true);
            return Req(o.Preset == 6 && o.Telegraph == 5 && o.CavitationOff, "deep dive order")
                ?? Req(Near(EvasionMath.Diff(o.CourseDeg, 210), 0, 0.5), "deep dive course " + o.CourseDeg)
                ?? Req(off.Preset == 5, "can not outdive: preset 5")
                ?? Req(noDive.Preset == 5, "dive off: preset 5");
        }

        public static string OwnSticky()
        {
            var s = new EvState();
            SubEvasionMath.Own(0, T(30, 3), null, s, false, false);
            int side0 = s.Side;
            // the torpedo bearing wobbles: the side must not flip
            double[] drift = { 0, 8, -6, 10, -10, 4, -8, 9, -3, 6 };
            for (int i = 0; i < drift.Length; i++)
            {
                SubEvasionMath.Own(0, T(EvasionMath.Norm360(30 + drift[i]), 3), null, s, false, false);
                if (s.Side != side0 || s.Side == 0) return "side changed at " + i;
            }
            // big change: the other turn is much shorter -> one flip
            var f = new EvState { Side = 1 };
            SubEvasionMath.Own(180, T(300, 3), null, f, false, false);   // brg 300: +90 = 30 (turn 150), -90 = 210 (turn 30)
            return Req(f.Side == -1, "side must flip when the other turn is much shorter: " + f.Side);
        }

        public static string RouteAction()
        {
            return Eq(SubEvasionMath.RouteAction(2, true, false), "kept", "kept")
                ?? Eq(SubEvasionMath.RouteAction(2, false, false), "replaced", "replaced")
                ?? Eq(SubEvasionMath.RouteAction(0, false, true), "course", "course")
                ?? Eq(SubEvasionMath.RouteAction(0, false, false), "none", "none");
        }
    }
}
