using System;

namespace VoiceBotBridge
{
    // T-175 stream L stand entry point: "airloss <part> selftest ok|FAILED".
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            bool all = a == "all";
            if (all || a == "math") bad += Report("math", AirLossMath.SelfTest());
            if (all || a == "toast_model") bad += Report("toast_model", UiToastStackModel.SelfTest());
            if (all || a == "tracker") bad += Report("tracker", AirLossTests.Tracker(args.Length > 1 ? args[1] : null));
            if (all || a == "body") bad += Report("body", AirLossTests.Bodies());
            if (all || a == "stack_ids")   // T-291
            {
                string only = args.Length > 1 ? args[1] : null;
                Func<string, bool> want = n => only == null || only == n;
                if (want("two_datum")) bad += ReportCase("two_datum", ToastIdsTests.TwoDatum());
                if (want("two_air_threat")) bad += ReportCase("two_air_threat", ToastIdsTests.TwoAirThreat());
                if (want("two_undo")) bad += ReportCase("two_undo", ToastIdsTests.TwoUndo());
                if (want("close_ids")) bad += ReportCase("close_ids", ToastIdsTests.CloseIds());
                if (want("no_duplicate_ids")) bad += ReportCase("no_duplicate_ids", ToastIdsTests.NoDuplicateIds());
            }
            return bad == 0 ? 0 : 1;
        }

        private static int ReportCase(string name, string err)
        {
            Console.WriteLine("airloss stack_ids " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("airloss " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
