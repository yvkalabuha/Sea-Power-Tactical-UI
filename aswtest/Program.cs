using System;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            if (args.Length > 0 && args[0] == "save-sample") { Console.WriteLine(AswSave.Sample()); return 0; }
            if (args.Length > 0 && args[0] == "deck") return Report("asw deck", AswDeckTests.Run());
            if (args.Length > 0 && args[0] == "adopt") return Report("asw adopt", AswAdoptTests.Run());
            if (args.Length > 0 && args[0] == "deck2") return Report("asw deck2", AswFakeDeckTests.Run());
            if (args.Length > 0 && args[0] == "t186") return Report("asw t186" + (args.Length > 1 ? " " + args[1] : ""), AswT186Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t181") return Report("asw t181" + (args.Length > 1 ? " " + args[1] : ""), AswT181Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "link") return Report("asw launch link", AswLaunchLinkTests.Run());
            if (args.Length > 0 && args[0] == "lifecycle") return Report("asw lifecycle", AswLifecycleTests.Run());
            if (args.Length > 0 && args[0] == "t142") return Report("asw t142", AswT142Tests.Run());
            if (args.Length > 0 && args[0] == "tick") return Report("asw tick", AswTickTests.Run());
            if (args.Length > 0 && args[0] == "route") return Report("asw route", AswRouteGuard.SelfTest() ?? AswRouteTests.Run());
            if (args.Length > 0 && args[0] == "names") return Report("asw names", AswGroupNames.SelfTest() ?? AirOwnership.SelfTest() ?? AswNameTests.Run());
            if (args.Length > 0 && args[0] == "spacing") return Report("asw spacing", AswSpacingTests.Run());
            if (args.Length > 0 && args[0] == "spacing-report") return Report("asw spacing report", AswSpacingTests.Report());
            if (args.Length > 0 && args[0] == "diptasks") return Report("asw diptasks", AswDipTaskTests.Run());
            if (args.Length > 0 && args[0] == "t172-routes") return Report("asw t172 routes", AswT172RoutesTests.Run());
            if (args.Length > 0 && args[0] == "t172-buoys") return Report("asw t172 buoys", AswT172BuoysTests.Run());
            if (args.Length > 0 && args[0] == "t172-contacts") return Report("asw t172 contacts", AswT172ContactsTests.Run());
            if (args.Length > 0 && args[0] == "t174") return Report("asw t174", AswT174Tests.Run());
            if (args.Length > 0 && args[0] == "t173-math") return Report("asw t173 math", AswT173MathTests.Run());
            if (args.Length > 0 && args[0] == "t173-attack") return Report("asw t173 attack", AswT173AttackTests.Run());
            if (args.Length > 0 && args[0] == "t173-strike") return Report("asw t173 strike", AswT173StrikeTests.Run());
            if (args.Length > 0 && args[0] == "t176") return Report("asw t176" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t229") return Report("asw t229" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT229(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t238") return Report("asw t238" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT238(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t245") return Report("asw t245" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT245(args.Length > 1 ? args[1] : null));   // T-245
            if (args.Length > 0 && args[0] == "t251") return Report("asw t251" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT251(args.Length > 1 ? args[1] : null));   // T-251
            if (args.Length > 0 && args[0] == "t260") return Report("asw t260" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT260(args.Length > 1 ? args[1] : null));   // T-260
            if (args.Length > 0 && args[0] == "t266") return Report("asw t266" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT266(args.Length > 1 ? args[1] : null));   // T-266
            if (args.Length > 0 && args[0] == "t288") return Report("asw t288" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT288(args.Length > 1 ? args[1] : null));   // T-288
            if (args.Length > 0 && args[0] == "t274") return Report("asw t274" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT274(args.Length > 1 ? args[1] : null));   // T-274
            if (args.Length > 0 && args[0] == "t241") return Report("asw t241" + (args.Length > 1 ? " " + args[1] : ""), AswT241Tests.Run(args.Length > 1 ? args[1] : null));   // T-241
            if (args.Length > 0 && args[0] == "t252") return Report("asw t252" + (args.Length > 1 ? " " + args[1] : ""), AswT252Tests.Run(args.Length > 1 ? args[1] : null));   // T-252
            if (args.Length > 0 && args[0] == "t316") return Report("asw t316" + (args.Length > 1 ? " " + args[1] : ""), AswT316Tests.Run(args.Length > 1 ? args[1] : null));   // T-316
            if (args.Length > 0 && args[0] == "t298") return Report("asw t298" + (args.Length > 1 ? " " + args[1] : ""), AswT298Tests.Run(args.Length > 1 ? args[1] : null));   // T-298
            if (args.Length > 0 && args[0] == "t322") return Report("asw t322" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT322(args.Length > 1 ? args[1] : null));   // T-322
            if (args.Length > 0 && args[0] == "t331") return Report("asw t331" + (args.Length > 1 ? " " + args[1] : ""), AswT331Tests.Run(args.Length > 1 ? args[1] : null));   // T-331
            if (args.Length > 0 && args[0] == "t334") return Report("asw t334" + (args.Length > 1 ? " " + args[1] : ""), AswT176Tests.RunT334(args.Length > 1 ? args[1] : null));   // T-334
            if (args.Length > 0 && args[0] == "t334x") return Report("asw t334x" + (args.Length > 1 ? " " + args[1] : ""), AswT334Excl.Run(args.Length > 1 ? args[1] : null));   // T-334
            if (args.Length > 0 && args[0] == "t339") return Report("asw t339" + (args.Length > 1 ? " " + args[1] : ""), AswT339Tests.Run(args.Length > 1 ? args[1] : null));   // T-339
            if (args.Length > 0 && args[0] == "t343elig") return Report("asw t343elig" + (args.Length > 1 ? " " + args[1] : ""), AswT343Elig.Run(args.Length > 1 ? args[1] : null));   // T-343
            if (args.Length > 0 && args[0] == "t344") return Report("asw t344" + (args.Length > 1 ? " " + args[1] : ""), AswT344Tests.Run(args.Length > 1 ? args[1] : null));   // T-344
            if (args.Length > 0 && args[0] == "t346") return Report("asw t346" + (args.Length > 1 ? " " + args[1] : ""), AswT346Tests.Run(args.Length > 1 ? args[1] : null));   // T-346
            if (args.Length > 0 && args[0] == "t344deck") return Report("asw t344deck" + (args.Length > 1 ? " " + args[1] : ""), AswT344Deck.Run(args.Length > 1 ? args[1] : null));   // T-344
            if (args.Length > 0 && args[0] == "t343") return Report("asw t343" + (args.Length > 1 ? " " + args[1] : ""), AswT343Tests.Run(args.Length > 1 ? args[1] : null));   // T-343
            if (args.Length > 0 && args[0] == "t328") return Report("asw t328" + (args.Length > 1 ? " " + args[1] : ""), AswT328Tests.Run(args.Length > 1 ? args[1] : null));   // T-328
            if (args.Length > 0 && args[0] == "t255") return Report("asw t255" + (args.Length > 1 ? " " + args[1] : ""), AswT255Tests.Run(args.Length > 1 ? args[1] : null));   // T-255
            if (args.Length > 0 && args[0] == "t180-filter") return Report("asw t180 filter" + (args.Length > 1 ? " " + args[1] : ""), AswT180Filter.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "world") return Report("asw world", AswMath.WorldTest());   // T-348
            if (args.Length > 0 && args[0] == "steer") return Report("asw steer", AirRetreatSteer.SelfTest());
            if (args.Length > 0 && args[0] == "t175") return Report("asw t175" + (args.Length > 1 ? " " + args[1] : ""), AswT175Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t318") return Report("asw t318" + (args.Length > 1 ? " " + args[1] : ""), AswT318Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t180-retreat") return Report("asw t180 retreat" + (args.Length > 1 ? " " + args[1] : ""), AswT180Retreat.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t188") return Report("asw t188" + (args.Length > 1 ? " " + args[1] : ""), AswT188Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t191") return Report("asw t191" + (args.Length > 1 ? " " + args[1] : ""), AswT191Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t192") return Report("asw t192" + (args.Length > 1 ? " " + args[1] : ""), AswT192Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t196") return Report("asw t196" + (args.Length > 1 ? " " + args[1] : ""), AswT196Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t226") return Report("asw t226" + (args.Length > 1 ? " " + args[1] : ""), AswT226Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t213") return Report("asw t213" + (args.Length > 1 ? " " + args[1] : ""), AswT213Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t214") return Report("asw t214" + (args.Length > 1 ? " " + args[1] : ""), AswT214Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t222") return Report("asw t222" + (args.Length > 1 ? " " + args[1] : ""), AswT222Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t231") return Report("asw t231" + (args.Length > 1 ? " " + args[1] : ""), AswT231Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t231sar") return Report("asw t231sar" + (args.Length > 1 ? " " + args[1] : ""), AswT231SarTests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t211") return Report("asw t211" + (args.Length > 1 ? " " + args[1] : ""), AswT211Tests.Run(args.Length > 1 ? args[1] : null));
            if (args.Length > 0 && args[0] == "t198") return Report("asw t198" + (args.Length > 1 ? " " + args[1] : ""), AswT198Tests.Run(args.Length > 1 ? args[1] : null));
            int bad = 0;
            bad += Report("asw math", AswMath.SelfTest());
            bad += Report("asw save", AswSave.SelfTest());
            bad += Report("asw commands", AswCommandsParse.SelfTest());
            bad += Report("asw controller", AswControllerTests.Run());
            bad += Report("asw adopt", AswAdoptTests.Run());
            bad += Report("asw deck", AswDeckTests.Run());
            bad += Report("asw deck2", AswFakeDeckTests.Run());
            bad += Report("asw t181", AswT181Tests.Run(null));
            bad += Report("asw t186", AswT186Tests.Run(null));
            bad += Report("asw t188", AswT188Tests.Run(null));
            bad += Report("asw t191", AswT191Tests.Run(null));
            bad += Report("asw t192", AswT192Tests.Run(null));
            bad += Report("asw t196", AswT196Tests.Run(null));
            bad += Report("asw t198", AswT198Tests.Run(null));
            bad += Report("asw t213", AswT213Tests.Run(null));
            bad += Report("asw t226", AswT226Tests.Run(null));
            bad += Report("asw t211", AswT211Tests.Run(null));
            bad += Report("asw t214", AswT214Tests.Run(null));
            bad += Report("asw t222", AswT222Tests.Run(null));
            bad += Report("asw launch link", AswLaunchLinkTests.Run());
            bad += Report("asw lifecycle", AswLifecycleTests.Run());
            bad += Report("asw t142", AswT142Tests.Run());
            bad += Report("asw tick", AswTickTests.Run());
            bad += Report("asw route", AswRouteGuard.SelfTest() ?? AswRouteTests.Run());
            bad += Report("asw names", AswGroupNames.SelfTest() ?? AirOwnership.SelfTest() ?? AswNameTests.Run());
            bad += Report("asw diptasks", AswDipTaskTests.Run());
            bad += Report("asw spacing", AswSpacingTests.Run());
            bad += Report("asw t172 routes", AswT172RoutesTests.Run());
            bad += Report("asw t172 buoys", AswT172BuoysTests.Run());
            bad += Report("asw t172 contacts", AswT172ContactsTests.Run());
            bad += Report("asw t174", AswT174Tests.Run());
            bad += Report("asw t175", AswT175Tests.Run());
            bad += Report("asw t318", AswT318Tests.Run());   // T-318
            bad += Report("asw t176", AswT176Tests.Run());
            bad += Report("asw t229", AswT176Tests.RunT229());
            bad += Report("asw t238", AswT176Tests.RunT238());
            bad += Report("asw t245", AswT176Tests.RunT245());
            bad += Report("asw t251", AswT176Tests.RunT251());
            bad += Report("asw t260", AswT176Tests.RunT260());   // T-260
            bad += Report("asw t266", AswT176Tests.RunT266());   // T-266
            bad += Report("asw t274", AswT176Tests.RunT274());   // T-274
            bad += Report("asw t288", AswT176Tests.RunT288());   // T-288
            bad += Report("asw t298", AswT298Tests.Run());   // T-298
            bad += Report("asw t322", AswT176Tests.RunT322());   // T-322
            bad += Report("asw t334", AswT176Tests.RunT334());   // T-334
            bad += Report("asw t334x", AswT334Excl.Run(null));   // T-334
            bad += Report("asw t316", AswT316Tests.Run(null));   // T-316
            bad += Report("asw t339", AswT339Tests.Run(null));   // T-339
            bad += Report("asw t343elig", AswT343Elig.Run(null));   // T-343
            bad += Report("asw t343", AswT343Tests.Run(null));   // T-343
            bad += Report("asw t344", AswT344Tests.Run(null));   // T-344
            bad += Report("asw t344deck", AswT344Deck.Run(null));   // T-344
            bad += Report("asw t346", AswT346Tests.Run(null));   // T-346
            bad += Report("asw t241", AswT241Tests.Run());
            bad += Report("asw t173 math", AswT173MathTests.Run());
            bad += Report("asw t173 attack", AswT173AttackTests.Run());
            bad += Report("asw t173 strike", AswT173StrikeTests.Run());
            bad += Report("asw window", AswWindowModel.SelfTest());
#if SAR_WIP
            bad += Report("asw sar controller", AswSarTests.Run());
#endif
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
