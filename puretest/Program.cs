using System;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static readonly string[] Sections = { "geolon", "t356world", "worldgeo", "radial", "radialtext", "overview", "ntds", "coverage", "coveragejam", "coverageworker", "coveragediag", "coverageqa01", "workercrumb", "legend", "contactinfo", "uikit", "winstate", "framemath", "memory", "ovcols", "ovown", "drawguard", "watchdog", "crumb", "logstamp", "settings_model", "settings", "settings_perf", "savestore", "intercept", "framestate", "mapscan", "lightsmoke", "msgcolor", "wpq", "hitestimate", "dropdown", "slider", "segments", "toggles", "compass", "status", "uihit", "uiscale", "unitfmt", "shippick", "undo", "zoneedit", "mappick", "lint", "escguard", "loadscreen", "postload", "proc_threads", "mod_threads", "shutdown", "proc_table", "hb_loop", "hb_stuck" };

        private static string Run(string s)
        {
            switch (s)
            {
                case "radial": return RadialMath.SelfTest();
                case "radialtext": return RadialText.SelfTest();
                case "overview": return OverviewMath.SelfTest();
                case "ntds": return NtdsIcons.SelfTest();
                case "coverage": return CoverageMath.SelfTest();
                case "coveragejam": return CoverageJamMath.SelfTest();
                case "geolon": return GeoLon.SelfTest();   // T-356
                case "t356world": return WorldOrderTests.Run();   // T-356
                case "worldgeo": return WorldGeo.SelfTest();   // T-354
                case "coverageworker": return CoverageWorker.SelfTest();
                case "coveragediag": return CoverageDiag.SelfTest();   // T-320
                case "coverageqa01": return CoverageQa01Live.SelfTest();
                case "workercrumb": return WorkerCrumb.SelfTest();
                case "legend": return CoverageMath.LegendSelfTest();
                case "contactinfo": return ContactInfoMath.SelfTest();
                case "uikit": return UiKitTests.Run();
                case "winstate": return UiWindowStateStore.SelfTest();
                case "framemath": return UiFrameMath.SelfTest();
                case "memory": return MemSelfTest.Run();
                case "ovcols": return OverviewColumns.SelfTest();
                case "ovown": return OverviewOwnMath.SelfTest();   // T-242
                case "drawguard": return DrawGuard.SelfTest();
                case "watchdog": return StallDetector.SelfTest();
                case "crumb": return StageCrumb.SelfTest();
                case "logstamp": return UiLogStamp.SelfTest();
                case "settings_model": return SettingsTests.Model();
                case "settings": return SettingsTests.Run();
                case "settings_perf": return SettingsTests.Perf();   // T-239
                case "savestore": return SaveSlotStore.SelfTest();
                case "intercept": return InterceptMath.SelfTest();
                case "framestate": return FrameState.SelfTest();
                case "mapscan": return MapTreeWalk.SelfTest();
                case "lightsmoke": return LightSmokeMath.SelfTest() ?? LightSmokeRegistry.SelfTest() ?? LightSmokeService.SelfTest();
                case "msgcolor": return MessageColorMath.SelfTest();   // T-329
                case "wpq": return WaypointQuietMath.SelfTest();   // T-331
                case "hitestimate": return HitEstimateMath.SelfTest() ?? HitEstimateGeo.SelfTest() ?? HitLedger.SelfTest() ?? HitEstimateModel.SelfTest() ?? HitEstimateInst.SelfTest() ?? OverviewExpand.SelfTest();
                case "dropdown": return UiDropdownModel.SelfTest() ?? UiKitDropdownTests.Run();
                case "slider": return UiKitDropdownTests.Slider();
                case "segments": return UiKitDropdownTests.Segments();
                case "toggles": return ToggleSet.SelfTest() ?? UiKitTogglesTests.Run();
                case "compass": return CompassMath.SelfTest() ?? UiKitCompassTests.Run();
                case "status": return UiKitStatusTests.Run();
                case "uihit": return UiHit.SelfTest() ?? UiClickLatch.SelfTest() ?? UiKitTests.HitWiring();
                case "uiscale": return UiScale.SelfTest();
                case "unitfmt": return UnitFmt.SelfTest();
                case "shippick": return ShipPickModel.SelfTest() ?? UiKitShipPickTests.Run();
                case "idscope": return UiKitIdScopeTests.Run();   // T-291
                case "undo": return UiUndoSlot.SelfTest() ?? UiUndoTests.Run();
                case "zoneedit": return ZoneEditMath.SelfTest();
                case "mappick": return UiMapPick.SelfTest() ?? UiMapDrawInput.SelfTest();
                case "lint": return UiLintTests.Run();
                case "escguard": return UiEscGuard.SelfTest();
                case "loadscreen": return LoadScreenMath.SelfTest();
                case "postload": return VoiceBotBridge.Ui.PostLoadGate.SelfTest();   // T-211
                case "proc_threads": return ProcThreadsTests.SelfTest();
                case "proc_table": return ProcTableTests.SelfTest();   // T-302
                case "hb_loop": return HbLoopTests.SelfTest() ?? HbLoopTests.NoProcSafe();   // T-302
                case "startup": return StartupGate.SelfTest() ?? StartupProbe.SelfTest() ?? ProcCadence.SelfTest();   // T-336
                case "hb_stuck": return HbLoopTests.StuckSurvives();   // T-305
                case "mod_threads": return ModThreadsTests.SelfTest();
                case "shutdown": return ModShutdown.SelfTest();   // T-220
                case "stat_gated": return StatGatedText.SelfTest();   // T-227
                case "buoyrevive": return BuoyReviveMath.SelfTest();   // T-094
                default: return "unknown section";
            }
        }

        private static int Main(string[] args)
        {
            if (UiText.LoadForTests("ru") <= 0)
            {
                Console.WriteLine("pure lang FAILED: no lang dir");
                return 1;
            }
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0;
            if (a == "all")
            {
                foreach (string s in Sections) bad += Report(s, Run(s));
            }
            else bad += Report(a, Run(a));
            return bad == 0 ? 0 : 1;
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine("pure " + name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
