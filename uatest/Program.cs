using System;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main()
        {
            UiText.LoadForTests("ru");
            int bad = 0;
            bad += Report("unit_action registry", UnitActionSelfTest.Registry());
            bad += Report("unit_action text", UnitActionSelfTest.Text());
            bad += Report("unit_action writer", UnitActionSelfTest.Writer());
            bad += Report("unit_action map", UnitActionMap.SelfTest());
            bad += Report("unit_action", UnitActionSelfTest.Run());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
