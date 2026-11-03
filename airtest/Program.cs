using System;

namespace VoiceBotBridge
{
    // T-119 stand entry point. Prints "air <name> selftest ok|FAILED: ..." per section.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            if (a == "t186") return Report("t186" + (args.Length > 1 ? " " + args[1] : ""), AirT186Tests.Run(args.Length > 1 ? args[1] : null));
            if (a == "t200") return Report("t200" + (args.Length > 1 ? " " + args[1] : ""), AirT200Tests.Run(args.Length > 1 ? args[1] : null));
            bool all = a == "all";
            if (all || a == "math") bad += Report("math", AirOpsMath.SelfTest());
            if (all || a == "owner") bad += Report("owner", AirOwnership.SelfTest() ?? AirBrokerTests.Owner());
            if (all || a == "broker") bad += Report("broker", AirDeckBroker.SelfTest() ?? AirBrokerTests.Broker());
            if (all || a == "yield") bad += Report("yield", AirBrokerTests.Yield());
            if (all || a == "prioritise") bad += Report("prioritise", AirBrokerTests.Prioritise());
            if (all || a == "controllers") bad += Report("controllers", AirControllersTests.Run());
            if (all || a == "relief") bad += Report("relief", AirRelief.SelfTest());
            if (all) bad += Report("t186", AirT186Tests.Run(null));
            if (all) bad += Report("t200", AirT200Tests.Run(null));
            if (all || a == "clock") bad += Report("clock", AirTickClock.SelfTest() ?? AirBrokerTests.Clock());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("air " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
