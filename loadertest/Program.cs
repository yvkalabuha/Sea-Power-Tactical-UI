using System;
using SeaPowerTacticalUI.Loader;

internal static class Program
{
    private static int Main()
    {
        string err = LoaderLogic.SelfTest();
        Console.WriteLine("LoaderLogic selftest " + (err == null ? "ok" : "FAILED: " + err));
        return err == null ? 0 : 1;
    }
}
