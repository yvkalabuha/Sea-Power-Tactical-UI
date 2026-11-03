using System;

namespace VoiceBotBridge
{
    // T-148 stand entry point. Prints "aswwin <name> selftest ok|FAILED: ..." per section.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            bool all = a == "all";
            if (all || a == "panel") bad += Report("panel", AswPanelTests.Run() ?? AswCourseModel.SelfTest() ?? AswWindowModel.SelfTest() ?? AswMath.SelfTest());
            if (all || a == "layout") bad += Report("layout", AswWindowTests.Layout());
            if (all || a == "clicks") bad += Report("clicks", AswWindowTests.Clicks());
            if (all || a == "t181") bad += Report("t181", AswWindowBody.T181SelfTest());
            if (all || a == "t174") bad += Report("t174", AswT174Tests.Run() ?? AswDrawPlan.SelfTest() ?? UiWindowStateStore.SelfTest() ?? UiFrameMath.SelfTest());
            if (all || a == "t173") bad += Report("t173", AswT173Tests.Run());
            if (all || a == "t176") bad += Report("t176", AswT176Tests.Run());
            if (all || a == "t229") bad += Report("t229", AswT229WinTests.Run());
            if (all || a == "t334") bad += Report("t334", AswT334WinTests.Run());   // T-334
            if (all || a == "t288") bad += Report("t288", AswT288WinTests.Run());   // T-288
            if (all || a == "zone_worldflat") bad += Report("zone_worldflat", AswT178Tests.ZoneWorldFlat());
            if (all || a == "t178") bad += Report("t178", AswT178Tests.Run());
            if (all || a == "t180") bad += Report("t180", AswT180WinTests.Run(null));
            if (a.StartsWith("t180-", StringComparison.Ordinal)) bad += Report(a, AswT180WinTests.Run(a.Substring(5)));
            if (a == "dump") { Console.Write(AswWindowDump.Run(args[1], int.Parse(args[2]), int.Parse(args[3]))); return 0; }
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("aswwin " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
