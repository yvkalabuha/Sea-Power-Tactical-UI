using System;

namespace VoiceBotBridge
{
    // T-176: stand of the pure ASW pattern math v3. Arguments: area | course | plan | localize | guard | lat | all.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            if (a == "area" || a == "all") bad += Report("asw t176 area", AswPatternMath.SelfTest() ?? PatternAreaTests.Run());
            if (a == "course" || a == "all") bad += Report("asw t176 course", PatternCourseTests.Run());
            if (a == "plan" || a == "all") bad += Report("asw t176 plan", PatternPlanTests.Run());
            if (a == "localize" || a == "all") bad += Report("asw t176 localize", AswLocalizeMath.SelfTest() ?? LocalizeTests.Run());
            if (a == "guard" || a == "all") bad += Report("asw t176 guard", AswRouteGuard.SelfTest() ?? GuardTests.Run());
            if (a == "lat" || a == "all") bad += Report("asw t348 lat", PatternLatTests.Run());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
