using System;

namespace VoiceBotBridge
{
    // T-174 stand entry point. Prints "toolbar <name> selftest ok|FAILED: ..." per section; "sheet <png>" writes the preview sheet.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            bool all = a == "all";
            if (all || a == "math") bad += Report("math", ToolbarMath.SelfTest() ?? ToolbarTests.Math());
            if (all || a == "glyphs") bad += Report("glyphs", ToolbarGlyphs.SelfTest());
            if (a == "sheet")
            {
                if (args.Length < 2) { Console.WriteLine("toolbar sheet selftest FAILED: no path"); return 1; }
                ToolbarSheet.Write(args[1]);
                Console.WriteLine("toolbar sheet selftest ok");
            }
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("toolbar " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
