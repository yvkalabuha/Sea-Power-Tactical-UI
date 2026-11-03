using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-159: the dipping-sonar range read (sensor name -> R_eff) and the "tasks changed by another hand" test of AswGame.
    internal static class AswDipTaskTests
    {
        private sealed class FakeSensor { public string SystemName; public bool Dipping; public bool Sonar = true; }

        private static double Range(IEnumerable<FakeSensor> list, Dictionary<string, double> table)
        {
            return AswDipTasks.DipRangeNm(list, s => ((FakeSensor)s).Sonar && ((FakeSensor)s).Dipping, s => ((FakeSensor)s).SystemName,
                n => { double r; return table.TryGetValue(n, out r) ? r : 0; });
        }

        public static string Run()
        {
            var table = new Dictionary<string, double> { { "AN/AQS-13", 2.7 } };
            // a dipping-sonar helicopter row: the range comes from the sonar system name (BaseSystem._systemName)
            var helo = new List<FakeSensor> { new FakeSensor { SystemName = "APS-116", Sonar = false }, new FakeSensor { SystemName = "AN/AQS-13", Dipping = true } };
            if (Range(helo, table) <= 0) return "dipping helicopter gives no range";
            if (Math.Abs(Range(helo, table) - 2.7) > 1e-9) return "range value " + Range(helo, table);
            // the dip step differs from the old fallback (2.0 nm) now that the range is known
            if (Math.Abs(AswMath.DipStep(Range(helo, table)) - AswMath.DipStep(2.0)) < 1e-9) return "dip step still the fallback";
            // fallbacks stay 0: no dipping sonar, no name (the old 0.8.6 behaviour), unknown sonar, no list
            if (Range(new List<FakeSensor> { new FakeSensor { SystemName = "AN/AQS-13", Dipping = false } }, table) != 0) return "not dipping";
            if (Range(new List<FakeSensor> { new FakeSensor { SystemName = null, Dipping = true } }, table) != 0) return "no name";
            if (Range(new List<FakeSensor> { new FakeSensor { SystemName = "X", Dipping = true } }, table) != 0) return "unknown sonar";
            if (Range(null, table) != 0) return "null list";
            // the first sonar without a known range does not hide the next one
            var two = new List<FakeSensor> { new FakeSensor { SystemName = "X", Dipping = true }, new FakeSensor { SystemName = "AN/AQS-13", Dipping = true } };
            if (Range(two, table) <= 0) return "second dipping sonar skipped";

            // tasks changed by another hand
            var last = new Dictionary<int, List<string>>();
            var own = new[] { "GoToWaypointTask", "DropSonobuoyTask" };
            if (AswDipTasks.ChangedByOther(last, 1, own)) return "first snapshot is a baseline";
            if (AswDipTasks.ChangedByOther(last, 1, new[] { "GoToWaypointTask", "AttackAtWaypoint", "GoToWaypointTask" })) return "own kinds are not a takeover";
            if (!AswDipTasks.ChangedByOther(last, 1, new[] { "GoToWaypointTask", "GoToRelativePoint" })) return "foreign task kind not detected";
            if (AswDipTasks.ChangedByOther(last, 1, new[] { "GoToRelativePoint" })) return "the same foreign kind reported twice";
            if (AswDipTasks.ChangedByOther(last, 2, new[] { "GoToRelativePoint" })) return "units are independent";
            if (AswDipTasks.ChangedByOther(last, 1, new string[0])) return "empty queue";
            // a second new foreign kind next to an already known one is still seen (the first foreign kind must not hide it)
            AswDipTasks.ChangedByOther(last, 3, new[] { "GoToRelativePoint" });
            if (!AswDipTasks.ChangedByOther(last, 3, new[] { "GoToRelativePoint", "GoToRelativeToStationPoint" })) return "second foreign kind hidden by the first";
            return null;
        }
    }
}
