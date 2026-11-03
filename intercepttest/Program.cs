using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-138 stand. Usage: intercepttest [section|all]. One line per case: "intercept &lt;case&gt; selftest ok|FAILED: ...".
    /// Stream C adds its cases to <see cref="Cases"/>.</summary>
    internal static class Program
    {
        private static readonly List<KeyValuePair<string, Func<string>>> Cases = new List<KeyValuePair<string, Func<string>>>
        {
            new KeyValuePair<string, Func<string>>("route cache", InterceptRouteTests.Cache),
            new KeyValuePair<string, Func<string>>("route cache nodata", InterceptRouteTests.CacheNoData),
            new KeyValuePair<string, Func<string>>("route queue", InterceptRouteTests.Queue),
            new KeyValuePair<string, Func<string>>("route budget", InterceptRouteTests.Budget),
            new KeyValuePair<string, Func<string>>("route replace", InterceptRouteTests.ReplaceCancel),
            new KeyValuePair<string, Func<string>>("route legclear", InterceptRouteTests.LegClear),
            new KeyValuePair<string, Func<string>>("route linehits", InterceptRouteTests.LineHitsLand),
            new KeyValuePair<string, Func<string>>("route island", InterceptRouteTests.Island),
            new KeyValuePair<string, Func<string>>("route bay", InterceptRouteTests.Bay),
            new KeyValuePair<string, Func<string>>("menu ok", InterceptControllerTests.MenuOk),
            new KeyValuePair<string, Func<string>>("menu cpa", InterceptControllerTests.MenuCpa),
            new KeyValuePair<string, Func<string>>("menu opening", InterceptControllerTests.MenuOpening),
            new KeyValuePair<string, Func<string>>("menu reasons", InterceptControllerTests.MenuReasons),
            new KeyValuePair<string, Func<string>>("menu busy", InterceptControllerTests.MenuBusy),
            new KeyValuePair<string, Func<string>>("menu land", InterceptControllerTests.MenuLand),
            new KeyValuePair<string, Func<string>>("menu cancel", InterceptControllerTests.MenuCancelAndReplace),
            new KeyValuePair<string, Func<string>>("start free", InterceptControllerTests.StartFree),
            new KeyValuePair<string, Func<string>>("start island", InterceptControllerTests.StartIsland),
            new KeyValuePair<string, Func<string>>("start landtarget", InterceptControllerTests.StartLandTarget),
            new KeyValuePair<string, Func<string>>("start bay", InterceptControllerTests.StartBay),
            new KeyValuePair<string, Func<string>>("start telegraph", InterceptControllerTests.StartTelegraph),
            new KeyValuePair<string, Func<string>>("start lat60", InterceptControllerTests.Lat60Flat),
            new KeyValuePair<string, Func<string>>("labels build", InterceptControllerTests.LabelsBuild),
            new KeyValuePair<string, Func<string>>("start errors", InterceptControllerTests.StartErrors),
            new KeyValuePair<string, Func<string>>("start calls", InterceptControllerTests.CallsOnly),
            new KeyValuePair<string, Func<string>>("tick replan", InterceptControllerTests.TickReplan),
            new KeyValuePair<string, Func<string>>("tick offroute", InterceptControllerTests.TickOffRoute),
            new KeyValuePair<string, Func<string>>("tick kind", InterceptControllerTests.TickKind),
            new KeyValuePair<string, Func<string>>("tick lastleg", InterceptControllerTests.TickLastLeg),
            new KeyValuePair<string, Func<string>>("tick stale", InterceptControllerTests.TickStale),
            new KeyValuePair<string, Func<string>>("tick lostgone", InterceptControllerTests.TickLostGone),
            new KeyValuePair<string, Func<string>>("tick arrived", InterceptControllerTests.TickArrived),
            new KeyValuePair<string, Func<string>>("tick player", InterceptControllerTests.TickPlayerChanged),
            new KeyValuePair<string, Func<string>>("tick superseded", InterceptControllerTests.TickSuperseded),
            new KeyValuePair<string, Func<string>>("tick suspend", InterceptControllerTests.TickSuspend),
            new KeyValuePair<string, Func<string>>("tick suspend_air", InterceptControllerTests.SuspendAir),
            new KeyValuePair<string, Func<string>>("tick once", InterceptControllerTests.TickOnce),
            new KeyValuePair<string, Func<string>>("tick replannone", InterceptControllerTests.TickReplanNone),
            new KeyValuePair<string, Func<string>>("tick restore", InterceptControllerTests.RestoreOrder),
            new KeyValuePair<string, Func<string>>("cancel keeps", InterceptControllerTests.CancelKeepsRoute),
            new KeyValuePair<string, Func<string>>("cancel unit", InterceptControllerTests.UnitActive),
            new KeyValuePair<string, Func<string>>("cancel release", InterceptControllerTests.ReleaseUnit),
            new KeyValuePair<string, Func<string>>("cancel missionleft", InterceptControllerTests.MissionLeft),
            new KeyValuePair<string, Func<string>>("cancel status", InterceptControllerTests.StatusAndEvents),
            new KeyValuePair<string, Func<string>>("cancel journal", InterceptControllerTests.Journal),
            // T-169 / ADR-0067: enemy weapon zones
            new KeyValuePair<string, Func<string>>("zone math", InterceptZoneTests.Math_),
            new KeyValuePair<string, Func<string>>("zone menu", InterceptZoneTests.Menu),
            new KeyValuePair<string, Func<string>>("zone planwarn", InterceptZoneTests.PlanWarn),
            new KeyValuePair<string, Func<string>>("zone detour", InterceptZoneTests.Detour),
            new KeyValuePair<string, Func<string>>("zone detournone", InterceptZoneTests.DetourNone),
            new KeyValuePair<string, Func<string>>("zone hold", InterceptZoneTests.Hold),
            new KeyValuePair<string, Func<string>>("zone holdplayer", InterceptZoneTests.HoldPlayer),
            new KeyValuePair<string, Func<string>>("zone abort", InterceptZoneTests.Abort),
            new KeyValuePair<string, Func<string>>("zone target", InterceptZoneTests.TargetAndInside),
            new KeyValuePair<string, Func<string>>("zone track", InterceptZoneTests.Track),
            new KeyValuePair<string, Func<string>>("zone submerged", InterceptZoneTests.Submerged),
        };

        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0, ran = 0;
            foreach (var c in Cases)
            {
                if (a != "all" && a != c.Key && !c.Key.StartsWith(a + " ", StringComparison.Ordinal)) continue;
                ran++;
                string err;
                try { err = c.Value(); }
                catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
                Console.WriteLine("intercept " + c.Key + " selftest " + (err == null ? "ok" : "FAILED: " + err));
                if (err != null) bad++;
            }
            if (ran == 0) { Console.WriteLine("intercept " + a + " selftest FAILED: unknown section"); return 1; }
            return bad == 0 ? 0 : 1;
        }
    }
}
