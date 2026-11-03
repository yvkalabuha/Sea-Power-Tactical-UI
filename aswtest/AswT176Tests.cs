using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 / ADR-0076: the controller scenarios of the search patterns v3 (AswFakeGame). One scenario per name (the pytest cases), or all (null).
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names =
        {
            "v3-migrate", "v3-reanchor", "v3-filter", "v3-area-sketch", "v3-area-split", "v3-area-dip", "v3-corridor", "v3-corridor-short",
            "v3-barrier", "v3-dip", "v3-relief", "multi-tactic", "v3-reseed", "cross-spook", "lost-ring", "v3-view",
        };

        public static string Run(string only = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                if ((e = Scenario(n)) != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown scenario " + only;
            return null;
        }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }
        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }

        private static string Scenario(string name)
        {
            switch (name)
            {
                case "v3-migrate": return Migrate();
                case "v3-reanchor": return Reanchor();
                case "v3-filter": return Filter();
                case "v3-area-sketch": return AreaSketch();
                case "v3-area-split": return AreaSplit();
                case "v3-area-dip": return AreaDipScenario();
                case "v3-corridor": return Corridor();
                case "v3-corridor-short": return CorridorShort();
                case "v3-barrier": return Barrier();
                case "v3-dip": return DipScenario();
                case "v3-relief": return Relief();
                case "multi-tactic": return MultiTactic();
                case "v3-reseed": return Reseed();
                case "cross-spook": return CrossSpook();
                case "lost-ring": return LostRing();
                case "v3-view": return ViewScenario();
                default: return null;   // the scenario is added by its step of the plan (C1..C12)
            }
        }
    }
}
