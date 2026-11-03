using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Calibration constants of the line (T-341 phase E1). A file of the LINE: the stable branch gets the fitted values, the main tree (beta, E2) keeps the estimates. Not cfg (ADR-0132 section 9).</summary>
    internal static class StrikeCalib
    {
        // phase A-D
        public const double Eta = 0.5, EtaNm = 10, SharedFactor = 0.5, ClearDeltaS = 45, FlankSaveFrac = 0.2, TonBandFrac = 0.15, PCap = 0.95, SupersonicKn = 1000;
        public const bool Estimate = true;                 // stable after E1: false
        public const int FormulaVersion = 153;             // beta (E2): 160
        // phase B: the "clearing" card is shown only when it is not worse than the direct strike by more than this factor; the time of flight from the probes of 2026-10-31
        public const double ClearShowFrac = 1.1, TofBaseS = 2, TofSpeedFrac = 528.0 / 540.0;
        // phase C: the time-of-flight cache (key cell in nm) and the clamp of the sim/rough scale
        public const double TofCacheNm = 1, TofScaleMin = 0.8, TofScaleMax = 1.25;
        // phase E1
        public const string CalibId = "estimate";          // after the fit: "e1-<yyyymmdd>"
        public const double KScale = 1.0;                  // global alpha on K/S (cache mode only)
        public const double ClearGain = 1.0;               // share of a hit neighbour's air defence that is gone; 1.0 + skip = the phase A rule
        public const bool UseDifficulty = true;            // p_SAM x (1 - Globals._missileInterceptChanceReduction)
        public const double DifficultyMin = 0.1, DifficultyMax = 1.5;

        // T-342 phase A (estimates until E1; the same in both trees)
        public const double KillMarginAd = 1, KillMarginWeak = 0, PriorHitWeight = 0.7, BonusFallback = 0.25, TofBandNm = 20, CiwsMaxNm = 3;
        public const int KillMaxM = 64;
        public const bool LaterWaveSigma = false, KillEstimate = true;   // "estimate" mark on P(sunk) until E1
        public const double TofScaleSup = 1.0;   // C4: the branch gets the value of the E1/E1b fit; the main tree (beta) stays 1.0 until E2

        // T-342 phase B (the same in both trees)
        public const int DeckEvalMax = 12;          // the most plan probes of one deck decision
        public const double DeckRefreshS = 5;       // the cache of the deck read
        public const double DeckNoAmmoS = 600;      // a loadout refused with no_ammo is not offered again for this long (as SAR g.NoAmmo)

        // T-342 phase C
        public const double AutoReplanGapS = 10;     // the auto replans one group at most this often
        public const int ReplanPerTick = 1;          // the queued replans served per tick (never in the tick of a cold warm-up)
        public const double DeckReplyS = 30;         // review B (1): an order without the answer of the broker this long is failed (no_reply)
        public const double DeckLaunchGraceS = 180;  // review B (2): an order in state launch fails late only this long after ready + DeckLateS
        public const int AutoNotesMax = 6;

        // T-342 phase D (the same in both trees)
        public const double EwFighterClearS = 30;     // no threat for the jammer this long -> the object jam is switched off
        public const double EwFighterExitMult = 1.5;  // T-346 D3: a threat stays while its ETA is at most EwFighterEtaS x this
        public const double EwFighterGoneS = 30;      // T-346 D3: ... and goes after this long without the stay condition
        public const double EwFighterFarMult = 2;     // farther than 2 x R_t is no threat any more
        public const double EwFighterTickS = 1;       // the fighter table is read at most this often
        public const double EwFighterPreemptS = 60;   // a new threat takes the jammer only if its ETA is this much earlier
        public const double EwFighterSwitchS = 15;    // the least time between two switches of one jammer (no flapping)
        public const double EwStandoffS = 10;         // the standoff point is re-sent at most this often (and only when it moved > 3 nm)

        // phase E1b (T-341): the turn model of the heavy carriers. The game turns an aircraft at a lateral acceleration of about 0.84 * MaxG * g (Tu-22M2 at 537 kn: 4.3 deg/s measured on the tacview of the live runs, Tu-16K at ~400 kn: 5.7 deg/s,
        // A-6E (MaxG 6.5) 13-14 deg/s), so the rate falls with the speed. Only the types listed here (MaxG from the aircraft ini <= 3) use the turn model of the run in; every other type keeps the E1 behaviour (a fixed reserve).
        public const double TurnGFrac = 0.84, GravityMs2 = 9.80665;
        internal static readonly Dictionary<string, double> HeavyMaxG = new Dictionary<string, double>
        {
            { "wp_tu-22m2", 2.5 }, { "wp_tu-16", 2.5 }, { "wp_tu-16k", 2.5 }, { "wp_tu-16p", 2.5 }, { "wp_tu-95rt", 2.5 }, { "wp_tu-142m", 2.5 }, { "wp_il-38", 2.5 }, { "wp_p-42", 3.0 },
            { "pla_h-6a", 2.5 }, { "pla_h-6d", 2.5 }, { "plan_sh-5", 2.5 }, { "usn_p-3c", 2.5 }, { "usn_p-2h", 2.5 }, { "usaf_b-52d", 2.0 }, { "usaf_b-52g", 2.0 },
        };

        /// <summary>The turn rate of a heavy carrier in deg/s at speedKn (0.84 * MaxG * g / v); 0 = not a heavy type (the E1 model).</summary>
        public static double TurnRateDps(string type, double speedKn)
        {
            double g;
            if (type == null || !HeavyMaxG.TryGetValue(type, out g)) return 0;
            double v = (speedKn > 50 ? speedKn : 450) * 0.514444;
            return TurnGFrac * g * GravityMs2 / v * 180.0 / Math.PI;
        }

        /// <summary>"usn_ddg_kidd" -> alpha; empty until the fit. Other classes use KScale.</summary>
        internal static readonly Dictionary<string, double> ClassTable = new Dictionary<string, double>();

        // T-350: the flight profile of the AS-4 (Kh-22N): tof = OffsetS + range / CruiseKn. Fit of tests/data/t350_as4 (Tacview records of E1/E1b, 53 hits, 140-153 nm):
        // strike_calib.py --tof-profile tests/data/t350_as4/tracks.csv -> offset 41.8 s, cruise 2336 kn, climb + dive 65 nm, residual <= 3.8 s. Below MinNm (and outside 125-160 nm) = extrapolation (logged as tof_prof extrap).
        // Both trees get it (customer decision D1 = b, 2026-11-03); the beta is to be checked at the T-342 E calibration. Replaces rough * Scale * TofScaleSup for these missiles; TofScaleSup stays for the others.
        internal static readonly Dictionary<string, StrikeTofProfile> TofProfiles = new Dictionary<string, StrikeTofProfile>
        {
            { "wp_as-4", new StrikeTofProfile { OffsetS = 41.8, CruiseKn = 2336, MinNm = 65 } },
        };

        public static double ClassScale(string classIni)
        {
            double a;
            return classIni != null && ClassTable.TryGetValue(classIni, out a) ? a : KScale;
        }
    }
}
