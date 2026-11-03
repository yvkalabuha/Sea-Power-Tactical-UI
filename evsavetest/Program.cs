using System;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main()
        {
            int bad = 0;
            bad += Report("evasion_save json", EvasionSave.SelfTestJson());
            bad += Report("evasion_save bind", EvasionSave.SelfTestBind());
            bad += Report("evasion_save memory", EvasionSave.SelfTestMemory());
            bad += Report("control_memory", ControlMemory.SelfTest());
            bad += Report("ship_mobility", ShipMobility.SelfTest());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
