using System;

namespace VoiceBotBridge
{
    // T-148 stand entry point. Prints "capwin <name> selftest ok|FAILED: ..." per section.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            bool all = a == "all";
            if (all || a == "panel") bad += Report("panel", CapOrderPanel.SelfTest());
            if (all || a == "layout") bad += Report("layout", CapWindowTests.Layout());
            if (all || a == "clicks") bad += Report("clicks", CapWindowTests.Clicks());
            if (all || a == "widgets") bad += Report("widgets", CapWindowTests.Widgets());
            if (all || a == "roe") bad += Report("roe", CapWindowTests.RoeLines());
            if (all || a == "alert179") bad += Report("alert179", CapWindowTests.Alert179());
            if (all || a == "t200") bad += Report("t200", CapWindowTests.T200Available());
            if (all || a == "t325") bad += Report("t325", CapWindowTests.T325Loadout());
            if (all) bad += Report("rename", CapWindowTests.Rename(null));
            if (a == "rename") return Report("rename" + (args.Length > 1 ? " " + args[1] : ""), CapWindowTests.Rename(args.Length > 1 ? args[1] : null));
            if (all || a == "drawinput") bad += Report("drawinput", UiMapDrawInput.SelfTest());
            if (all || a == "mouseblock") bad += Report("mouseblock", UiMouseBlock.SelfTest());
            if (all || a == "zone_worldflat") bad += Report("zone_worldflat", CapWindowTests.ZoneWorldFlat());
            if (all || a == "handles") bad += Report("handles", ZoneHandleDrag.SelfTest());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("capwin " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
