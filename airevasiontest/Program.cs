using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-100 stand. Usage: airevasiontest [math|controller|all|&lt;case&gt;]. One line per case: "air evasion &lt;case&gt; selftest ok|FAILED: ...".</summary>
    internal static class Program
    {
        private static KeyValuePair<string, Func<string>> C(string n, Func<string> f) { return new KeyValuePair<string, Func<string>>(n, f); }

        private static readonly List<KeyValuePair<string, Func<string>>> Math = new List<KeyValuePair<string, Func<string>>>
        {
            C("tti", AirEvasionMathTests.Tti), C("closing", AirEvasionMathTests.Closing), C("button", AirEvasionMathTests.Button),
            C("auto", AirEvasionMathTests.Auto), C("cooldown", AirEvasionMathTests.Cooldown), C("beam", AirEvasionMathTests.Beam),
            C("replace", AirEvasionMathTests.Replace), C("beamend", AirEvasionMathTests.BeamEnd), C("lat", AirEvasionMathTests.Lat), C("t356", AirEvasionMathTests.Antimeridian),
        };

        private static readonly List<KeyValuePair<string, Func<string>>> Controller = new List<KeyValuePair<string, Func<string>>>
        {
            C("button_native", AirEvasionControllerTests.ButtonNative), C("button_large", AirEvasionControllerTests.ButtonLarge),
            C("button_no_native_cap", AirEvasionControllerTests.ButtonNoNativeCap), C("button_beam", AirEvasionControllerTests.ButtonBeam),
            C("button_skips", AirEvasionControllerTests.ButtonSkips), C("button_player_control_ok", AirEvasionControllerTests.ButtonPlayerControlOk),
            C("native_end", AirEvasionControllerTests.NativeEnd), C("native_failed", AirEvasionControllerTests.NativeFailed),
            C("native_cease_fire", AirEvasionControllerTests.NativeCeaseFire), C("auto_start", AirEvasionControllerTests.AutoStart),
            C("auto_no_missile", AirEvasionControllerTests.AutoNoMissile), C("auto_cooldown", AirEvasionControllerTests.AutoCooldown),
            C("auto_muted", AirEvasionControllerTests.AutoMuted),
            C("combo_course", AirEvasionControllerTests.ComboCourse), C("combo_end", AirEvasionControllerTests.ComboEnd),
            C("overridden", AirEvasionControllerTests.Overridden), C("replace_after_guard", AirEvasionControllerTests.ReplaceAfterGuard),
            C("beam_upgrade", AirEvasionControllerTests.BeamUpgrade), C("beam_quiet", AirEvasionControllerTests.BeamQuiet),
            C("beam_timeout", AirEvasionControllerTests.BeamTimeout), C("chaff_once", AirEvasionControllerTests.ChaffOnce),
            C("chaff_hold", AirEvasionControllerTests.ChaffHold), C("option_hint_once", AirEvasionControllerTests.OptionHintOnce),
            C("release_combo", AirEvasionControllerTests.ReleaseCombo), C("release_native", AirEvasionControllerTests.ReleaseNative),
            C("release_flag_kept", AirEvasionControllerTests.ReleaseFlagKept), C("leave_clears", AirEvasionControllerTests.LeaveClears),
            C("holds", AirEvasionControllerTests.Holds), C("postlaunch_superseded", AirEvasionControllerTests.PostLaunchSuperseded),
            C("diag_json", AirEvasionControllerTests.DiagJson), C("globe_flag", AirEvasionControllerTests.GlobeFlag),
        };

        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
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
                Console.WriteLine("air evasion " + c.Key + " selftest " + (err == null ? "ok" : "FAILED: " + err));
                if (err != null) bad++;
            }
            if (list.Count == 0) { Console.WriteLine("air evasion " + a + " selftest FAILED: unknown section"); return 1; }
            return bad == 0 ? 0 : 1;
        }
    }
}
