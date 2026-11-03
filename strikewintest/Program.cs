using System;

namespace VoiceBotBridge
{
    // T-341 stand entry point. Prints "strikewin <name> selftest ok|FAILED: ..." per section.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            bool all = a == "all";
            if (all || a == "win") bad += Report("win", StrikeWindow.SelfTest());
            if (all || a == "pick") bad += Report("pick", StrikeTargetPick.SelfTest());
            if (all || a == "ov") bad += Report("ov", StrikeOverlayMath.SelfTest());
            if (all || a == "layout") bad += Report("layout", StrikeWinTests.Layout());
            if (all || a == "body-groups") bad += Report("body-groups", StrikeWinTests.Groups());
            if (all || a == "body-air") bad += Report("body-air", StrikeWinTests.Air());
            if (all || a == "body-target") bad += Report("body-target", StrikeWinTests.Target());
            if (all || a == "body-cards") bad += Report("body-cards", StrikeWinTests.Cards());
            if (all || a == "body-goal") bad += Report("body-goal", StrikeWinTests.Goal());
            if (all || a == "body-sink") bad += Report("body-sink", StrikeWinTests.Sink());
            if (all || a == "body-red-confirm") bad += Report("body-red-confirm", StrikeWinTests.RedConfirm());
            if (all || a == "body-deck") bad += Report("body-deck", StrikeWinTests.DeckBlock());
            if (all || a == "body-deck-cancel") bad += Report("body-deck-cancel", StrikeWinTests.DeckCancel());
            if (all || a == "body-counts") bad += Report("body-counts", StrikeWinTests.DeckCounts());
            if (all || a == "body-auto") bad += Report("body-auto", StrikeWinTests.BodyAuto());
            if (all || a == "card-clear-pause") bad += Report("card-clear-pause", StrikeWinTests.CardClearPause());
            if (all || a == "body-clear") bad += Report("body-clear", StrikeWinTests.BodyClear());
            if (all || a == "body-ew") bad += Report("body-ew", StrikeWinTests.Ew());
            if (all || a == "body-ew-fighter") bad += Report("body-ew-fighter", StrikeWinTests.EwFighter());
            if (all || a == "body-exec") bad += Report("body-exec", StrikeWinTests.Exec());
            if (all || a == "body-result") bad += Report("body-result", StrikeWinTests.Result());
            if (a == "dump") { Console.Write(StrikeWinTests.Dump(args.Length > 1 ? args[1] : "ru")); return 0; }
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("strikewin " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
