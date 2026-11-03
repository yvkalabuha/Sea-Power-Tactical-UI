using System;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (UiText.LoadForTests("ru") == 0) { Console.WriteLine("doctrine flags selftest FAILED: lang tables not found"); return 1; }
            int bad = 0;
            bad += Report("doctrine flags", DoctrineFlagsTests.SelfTest());
            bad += Report("air defense ready", AirDefenseReadyRules.SelfTest());
            bad += Report("evasion", EvasionMath.SelfTest());
            bad += Report("evasion t268 scenarios", EvasionHoldScenarios.SelfTest());
            bad += Report("evasion t272 guard", EvasionCourseGuard.SelfTest());
            bad += Report("evasion t272 scenarios", EvasionTerrainScenarios.SelfTest());
            bad += Report("evasion t278 return", EvasionReturnGuard.SelfTest());
            bad += Report("evasion t278 scenarios", EvasionReturnScenarios.SelfTest());
            bad += Report("evasion t279 helm", EvasionHelmMath.SelfTest());
            bad += Report("evasion t279 scenarios", EvasionHelmScenarios.SelfTest());
            bad += Report("evasion t286 decoy", EvasionDecoyMath.SelfTest());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
