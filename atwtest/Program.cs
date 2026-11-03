using System;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int bad = 0;
            bad += Report("atw math", AntiTorpedoMath.SelfTest());
            bad += Report("atw sim", AtwSim.SelfTest());
            bad += Report("atw multi", AtwMultiSim.SelfTest());
            bad += Report("atw order", AtwOrderMath.SelfTest());
            bad += Report("atw turn", AtwTurnMath.SelfTest());
            bad += Report("atw turn sim", AtwTurnSim.SelfTest());
            bad += Report("atw frozen", AtwFrozenSim.SelfTest());
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
