using System;

namespace VoiceBotBridge
{
    // T-175 stand entry point. Prints "airguard <part> selftest ok|FAILED: ..." per part.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            bool all = a == "all";
            foreach (var part in new[] { "radius", "eta", "stage", "cover", "raid", "soft", "rehold", "lat" })
                if (all || a == part) bad += Report(part, AirGuardMath.SelfTest(part));
            foreach (var part in new[] { "controller", "alert", "ignore", "ghost", "unknown_warn", "qa01", "perf", "soft_resume" })
                if (all || a == part) bad += Report(part, AirGuardTests.Run(part));
            foreach (var part in new[] { "t192_keep_ship", "t192_same_cover", "t192_keep_track", "t192_cover_for", "t195_held_not_threat" })
                if (all || a == part) bad += Report(part, AirGuardT192Tests.Run(part));
            foreach (var part in AirGuardT207Tests.Parts)
                if (all || a == part) bad += Report(part, AirGuardT207Tests.Run(part));
            foreach (var part in AirGuardT236Tests.Parts)
                if (all || a == part) bad += Report(part, AirGuardT236Tests.Run(part));
            if (all || a == "tc") bad += Report("tc", AirTimeX1.SelfTest());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("airguard " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
