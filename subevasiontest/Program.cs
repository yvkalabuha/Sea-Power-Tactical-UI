using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-101 stand. Usage: subevasiontest [math|controller|all|&lt;case&gt;]. One line per case: "sub evasion &lt;case&gt; selftest ok|FAILED: ...".</summary>
    internal static class Program
    {
        private static KeyValuePair<string, Func<string>> C(string n, Func<string> f) { return new KeyValuePair<string, Func<string>>(n, f); }

        private static readonly List<KeyValuePair<string, Func<string>>> Math = new List<KeyValuePair<string, Func<string>>>
        {
            C("start_auto", SubEvasionMathTests.StartAuto), C("start_player_override", SubEvasionMathTests.StartPlayerOverride),
            C("start_prio", SubEvasionMathTests.StartPrio), C("start_cooldown", SubEvasionMathTests.StartCooldown),
            C("start_button", SubEvasionMathTests.StartButton), C("start_emergency", SubEvasionMathTests.StartEmergency),
            C("native_end", SubEvasionMathTests.NativeEnd), C("launch_seen", SubEvasionMathTests.LaunchSeen),
            C("assist_grace", SubEvasionMathTests.AssistGrace), C("assist_interval", SubEvasionMathTests.AssistInterval),
            C("assist_far", SubEvasionMathTests.AssistFar), C("outdive", SubEvasionMathTests.Outdive),
            C("deep_dive_switch", SubEvasionMathTests.DeepDiveSwitch), C("own_far", SubEvasionMathTests.OwnFar),
            C("own_close", SubEvasionMathTests.OwnClose), C("own_deep", SubEvasionMathTests.OwnDeep),
            C("own_sticky", SubEvasionMathTests.OwnSticky), C("route_action", SubEvasionMathTests.RouteAction),
            C("selftest", Ui.SubEvasionMath.SelfTest),
        };

        private static readonly List<KeyValuePair<string, Func<string>>> Controller = new List<KeyValuePair<string, Func<string>>>
        {
            C("auto_start_native", SubEvasionControllerTests.AutoStartNative),
            C("native_confirm_failed_own", SubEvasionControllerTests.NativeConfirmFailedOwn),
            C("native_failed_no_own", SubEvasionControllerTests.NativeFailedNoOwn),
            C("native_finished_restore", SubEvasionControllerTests.NativeFinishedRestore),
            C("native_overridden", SubEvasionControllerTests.NativeOverridden),
            C("restore_off", SubEvasionControllerTests.RestoreOff),
            C("flag_off_midway", SubEvasionControllerTests.FlagOffMidway),
            C("cooldown_after_end", SubEvasionControllerTests.CooldownAfterEnd),
            C("tick_pacing", SubEvasionControllerTests.TickPacing),
            C("skip_reason_once", SubEvasionControllerTests.SkipReasonOnce),
            C("nm_game_resets_timer", SubEvasionControllerTests.NmGameResetsTimer),
            C("nm_mod_after_grace", SubEvasionControllerTests.NmModAfterGrace),
            C("nm_no_double", SubEvasionControllerTests.NmNoDouble),
            C("nm_assist_off", SubEvasionControllerTests.NmAssistOff),
            C("nm_unknown_count", SubEvasionControllerTests.NmUnknownCount),
            C("deep_dive_off_pre_start_own", SubEvasionControllerTests.DeepDiveOffPreStartOwn),
            C("deep_dive_off_seen_preset6_switch", SubEvasionControllerTests.DeepDiveOffSeenPreset6Switch),
            C("deep_dive_on_native_untouched", SubEvasionControllerTests.DeepDiveOnNativeUntouched),
            C("own_mode_setting", SubEvasionControllerTests.OwnModeSetting),
            C("own_orders", SubEvasionControllerTests.OwnOrders),
            C("own_first_decoy", SubEvasionControllerTests.OwnFirstDecoy),
            C("own_done", SubEvasionControllerTests.OwnDone),
            C("own_player_override_not_overridden", SubEvasionControllerTests.OwnPlayerOverrideNotOverridden),
            C("button_started_ignores_prio_and_cooldown", SubEvasionControllerTests.ButtonStartedIgnoresPrioAndCooldown),
            C("button_from_player_override", SubEvasionControllerTests.ButtonFromPlayerOverride),
            C("button_armed", SubEvasionControllerTests.ButtonArmed),
            C("button_already", SubEvasionControllerTests.ButtonAlready),
            C("button_emergency_skipped", SubEvasionControllerTests.ButtonEmergencySkipped),
            C("button_unsupported", SubEvasionControllerTests.ButtonUnsupported),
            C("release_restores", SubEvasionControllerTests.ReleaseRestores),
            C("sweep_unit_gone", SubEvasionControllerTests.SweepUnitGone),
            C("leave_mission", SubEvasionControllerTests.LeaveMission),
            C("holds", SubEvasionControllerTests.Holds),
            C("diag_json", SubEvasionControllerTests.DiagJson),
        };

        private static int Main(string[] args)
        {
            string a = args.Length > 0 ? args[0] : "all";
            var list = new List<KeyValuePair<string, Func<string>>>();
            if (a == "all" || a == "math") list.AddRange(Math);
            if (a == "all" || a == "controller") list.AddRange(Controller);
            if (list.Count == 0)
            {
                foreach (var c in Math) if (c.Key == a) list.Add(c);
                foreach (var c in Controller) if (c.Key == a) list.Add(c);
            }
            int bad = 0;
            foreach (var c in list)
            {
                string err;
                try { err = c.Value(); }
                catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
                Console.WriteLine("sub evasion " + c.Key + " selftest " + (err == null ? "ok" : "FAILED: " + err));
                if (err != null) bad++;
            }
            if (list.Count == 0) { Console.WriteLine("sub evasion " + a + " selftest FAILED: unknown section"); return 1; }
            return bad == 0 ? 0 : 1;
        }
    }
}
