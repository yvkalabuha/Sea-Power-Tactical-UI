using System;

namespace VoiceBotBridge
{
    internal static class LayerTestProgram
    {
        // T-113 A1: map selection by host window (rank), best pick, scan back-off
        private static string MapPickChecks()
        {
            if (CoverageMath.MapRank(false, "main", true, true) != -1) return "MapRank unseen";
            if (CoverageMath.MapRank(true, "other", true, true) != -1) return "MapRank other";
            if (CoverageMath.MapRank(true, "main", true, true) != 2) return "MapRank main max";
            if (CoverageMath.MapRank(true, "main", false, true) != 1) return "MapRank main small allowed";
            if (CoverageMath.MapRank(true, "main", false, false) != -1) return "MapRank main small denied";
            if (CoverageMath.MapRank(true, "main", true, false) != 2) return "MapRank main max, small denied";
            if (CoverageMath.MapRank(true, "none", false, false) != 0) return "MapRank none";
            var ranks = new System.Collections.Generic.List<int> { 1, 2, 0 };
            var rects = new System.Collections.Generic.List<double[]> { new[] { 0.0, 0, 1920, 1080 }, new[] { 0.0, 0, 100, 100 }, new[] { 0.0, 0, 3000, 3000 } };
            if (CoverageMath.PickBest(ranks, rects) != 1) return "PickBest rank beats area";
            ranks = new System.Collections.Generic.List<int> { 1, 1 };
            rects = new System.Collections.Generic.List<double[]> { new[] { 0.0, 0, 10, 10 }, new[] { 0.0, 0, 20, 20 } };
            if (CoverageMath.PickBest(ranks, rects) != 1) return "PickBest area tie-break";
            ranks = new System.Collections.Generic.List<int> { -1, -1 };
            if (CoverageMath.PickBest(ranks, rects) != -1) return "PickBest none";
            if (CoverageMath.PickBest(null, null) != -1) return "PickBest null";
            double[] want = { 2, 4, 8, 8, 8, 8 };
            for (int i = 0; i < want.Length; i++)
                if (CoverageMath.NextScanDelayS(i) != want[i]) return "NextScanDelayS " + i;
            return null;
        }

        private static int Main()
        {
            UiText.LoadForTests("ru");
            string r = MapLayerMath.SelfTest();
            if (r != null) { Console.WriteLine("map layer selftest FAILED: " + r); return 1; }
            r = MapPickChecks();
            if (r != null) { Console.WriteLine("map layer selftest FAILED: " + r); return 1; }
            r = CoverageMath.LegendSelfTest();
            if (r != null) { Console.WriteLine("map layer selftest FAILED: " + r); return 1; }
            r = TerrainBroker.SelfTest();   // T-225
            if (r != null) { Console.WriteLine("map layer selftest FAILED: " + r); return 1; }
            Console.WriteLine("map layer selftest ok");
            return 0;
        }
    }
}
