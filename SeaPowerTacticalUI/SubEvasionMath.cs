using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-101 / ADR-0071: the order set of the own (fallback) submarine evasion for one tick.</summary>
    internal sealed class SubOwnOrder { public double CourseDeg; public int Telegraph; public int Preset; public bool CavitationOff; }

    /// <summary>T-101 / ADR-0071: pure core of the submarine torpedo evasion (no Unity, no reflection): start / end / noisemaker decisions,
    /// the game's "can outdive" formula and the course of the own mode.</summary>
    internal static class SubEvasionMath
    {
        public const double GraceS = 6, AssistMaxNm = 4, CooldownS = 5, CloseNm = 2, TickS = 1;
        public const string PlayerOverrideState = "PlayerOverride", EvasionState = "TorpedoEvasion", EmergencySurfaceState = "EmergencySurface";
        /// <summary>The submarine states of priority this low (or lower) are not interrupted by the automatics (the evasion itself has 2).</summary>
        public const int EvasionPriority = 2;

        // the game's CanOutdiveTorpedo constants (Submarine.CanOutdiveTorpedo, decompilation 0.8.7)
        private const double FeetToUnity = 0.00453571, SeabedMargin = 0.453571, TorpedoMargin = 0.34017825;

        /// <summary>null = start; else no_flag|no_threat|no_known_torpedo|already|state_prio|cooldown|emergency_surface.</summary>
        public static string StartDecision(bool flag, bool threatAlive, bool known, string stateName, int? statePrio, double sinceEndS, bool button)
        {
            if (!button && !flag) return "no_flag";
            if (!threatAlive) return "no_threat";
            if (!known) return "no_known_torpedo";
            if (stateName == EvasionState) return "already";
            if (stateName == EmergencySurfaceState) return "emergency_surface";
            if (button) return null;
            if (stateName != PlayerOverrideState && statePrio.HasValue && statePrio.Value <= EvasionPriority) return "state_prio";
            if (sinceEndS < CooldownS - 1e-9) return "cooldown";
            return null;
        }

        /// <summary>null = still running; finished | overridden (only for mode native, after the start was confirmed).</summary>
        public static string NativeEnd(string stateName)
        {
            if (stateName == null || stateName == EvasionState) return null;
            return stateName == PlayerOverrideState ? "overridden" : "finished";
        }

        /// <summary>Any decrease of the noisemaker count is a launch (game or mod); -1 = unknown -> false.</summary>
        public static bool LaunchSeen(int prevLeft, int nowLeft)
        {
            return prevLeft >= 0 && nowLeft >= 0 && nowLeft < prevLeft;
        }

        /// <summary>The mod may launch a noisemaker: past the window of the game's own launch, one interval since the last launch, torpedo known and close.</summary>
        public static bool AssistNow(double inStateS, double sinceLaunchS, double intervalS, bool known, double distNm)
        {
            return known && inStateS >= GraceS - 1e-9 && sinceLaunchS >= intervalS - 1e-9 && distNm <= AssistMaxNm + 1e-9;
        }

        /// <summary>The game's CanOutdiveTorpedo: the preset 6 depth (and the seabed) lie below the torpedo's maximum depth. Unknown input -> false.</summary>
        public static bool CanOutdive(double? torpMaxDepthUnity, double? preset6Ft, double? seabedUnity)
        {
            if (!torpMaxDepthUnity.HasValue || !preset6Ft.HasValue || !seabedUnity.HasValue) return false;
            double num = -torpMaxDepthUnity.Value;
            if (!(num < 0)) return false;
            double floor = Math.Max(-preset6Ft.Value * FeetToUnity, seabedUnity.Value + SeabedMargin);
            return floor <= num - TorpedoMargin;
        }

        /// <summary>null | "deep_dive_off": with the dive switched off a run that saw preset 6 (or a pre-start "can outdive") goes to the own mode.</summary>
        public static string DeepDiveSwitch(bool deepDiveOn, int? presetNow, bool preStartOutdive)
        {
            if (deepDiveOn) return null;
            return presetNow == 6 || preStartOutdive ? "deep_dive_off" : null;
        }

        /// <summary>
        /// The own mode: farther than CloseNm the torpedo is put on the beam (course bearing +-90, the side is sticky), speed step 4, depth preset 5;
        /// closer: the stern (bearing + 180), step 5, cavitation limit off. With DeepDive on and a torpedo that cannot follow: preset 6, stern, step 5.
        /// </summary>
        public static SubOwnOrder Own(double headingDeg, EvTorp t, double? torpCourseDeg, EvState s, bool deepDiveOn, bool canOutdive)
        {
            var o = new SubOwnOrder();
            if (deepDiveOn && canOutdive)
            {
                o.CourseDeg = EvasionMath.Norm360(t.BrgDeg + 180); o.Telegraph = 5; o.Preset = 6; o.CavitationOff = true;
            }
            else if (t.DistNm > CloseNm)
            {
                double a = EvasionMath.Norm360(t.BrgDeg + 90), b = EvasionMath.Norm360(t.BrgDeg - 90);
                double ta = Math.Abs(EvasionMath.Diff(headingDeg, a)), tb = Math.Abs(EvasionMath.Diff(headingDeg, b));
                int side;
                if (s.Side == 0) side = ta <= tb ? 1 : -1;
                else
                {
                    side = s.Side;
                    double tc = side > 0 ? ta : tb, to = side > 0 ? tb : ta;
                    if (tc > to + EvasionMath.HystDeg) side = -side;
                }
                s.Side = side;
                o.CourseDeg = side > 0 ? a : b; o.Telegraph = 4; o.Preset = 5; o.CavitationOff = false;
            }
            else
            {
                o.CourseDeg = EvasionMath.Norm360(t.BrgDeg + 180); o.Telegraph = 5; o.Preset = 5; o.CavitationOff = true;
            }
            s.CourseDeg = o.CourseDeg;
            return o;
        }

        /// <summary>What the restore does with the route: kept | replaced | course | none.</summary>
        public static string RouteAction(int savedPoints, bool firstSavedStillFirst, bool hadCourse)
        {
            if (savedPoints > 0) return firstSavedStillFirst ? "kept" : "replaced";
            return hadCourse ? "course" : "none";
        }

        public static string SelfTest()
        {
            if (StartDecision(true, true, true, "Default", 5, 100, false) != null) return "sub evasion: start decision";
            if (StartDecision(true, true, true, EmergencySurfaceState, 1, 100, true) != "emergency_surface") return "sub evasion: emergency surface";
            if (NativeEnd(PlayerOverrideState) != "overridden") return "sub evasion: native end";
            if (!LaunchSeen(2, 1) || LaunchSeen(-1, 1)) return "sub evasion: launch seen";
            if (AssistNow(5, 100, 30, true, 1) || !AssistNow(6, 30, 30, true, 4)) return "sub evasion: assist";
            if (!CanOutdive(1.0, 900, -10) || CanOutdive(1.0, 150, -10)) return "sub evasion: outdive";
            var o = Own(0, new EvTorp { BrgDeg = 90, DistNm = 3 }, null, new EvState(), false, false);
            if (Math.Abs(EvasionMath.Diff(o.CourseDeg, 0)) > 0.5 && Math.Abs(EvasionMath.Diff(o.CourseDeg, 180)) > 0.5) return "sub evasion: beam course";
            return null;
        }
    }
}
