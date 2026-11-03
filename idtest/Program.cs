using System;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main()
        {
            UiText.LoadForTests("ru");
            int bad = 0;
            bad += Report("identify store", IdentifyTests.Store());
            bad += Report("identify classify", IdentifyTests.Classify());
            bad += Report("identify save", IdentifyTests.Save());
            bad += Report("identify text", IdentifyTests.Text());
            bad += Report("identify keeper", IdentifyTests.Keeper());
            bad += Report("identify capture", IdentifyTests.Capture());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
