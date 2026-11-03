using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>Game-facing half of the action journal (T-091): unit snapshots, debug log, hook declarations.</summary>
    internal sealed partial class GameApi
    {
        /// <summary>[Menu] Debug (UI mod) / [Actions] Debug (voice mod): log every action step by step.</summary>
        public bool ActionDebug;

        /// <summary>One-line summary of the last action run through RunAction (for Menu/Radial logging). Null when nothing ran.</summary>
        internal string LastActionSummary;

        // Optional partial methods (no access modifier, no body = removed by the compiler when nobody implements them).
        // RestoreControl.cs implements them to remember the ROE and route before the mod's first command.
        partial void BeforeComboStep(object u, ActionStep st);
        partial void AfterComboStep(object u, ActionStep st, string ackJson);

        /// <summary>State of a unit for the before/after lines of the journal. Never throws; unknown members stay null.</summary>
        internal UnitSnap SnapUnit(object u)
        {
            var s = new UnitSnap();
            if (u == null) return s;
            try { var h = D(RxGet(u, "Heading")); if (h.HasValue) s.HeadingDeg = Math.Round(((h.Value % 360) + 360) % 360, 1); } catch { }
            try { var v = D(Call(u, "getVelocityInKnots")); if (v.HasValue) s.SpeedKn = Math.Round(v.Value, 2); } catch { }
            try
            {
                var cc = RxGet(u, "CourseCommand");
                if (cc != null) { var dc = D(Call(cc, "GetDemandedCourse", u)); if (dc.HasValue) s.CourseCmdDeg = Math.Round(((dc.Value % 360) + 360) % 360, 1); }
            }
            catch { }
            try { s.Telegraph = Telegraph(u); } catch { }
            try { s.Waypoints = Waypoints(u).Count; } catch { }
            try { var b = RxGet(u, "Emcon"); if (b is bool) s.Emcon = (bool)b; } catch { }
            try { var b = RxGet(u, "IsAirSearchRadarsOn"); if (b is bool) s.AirRadar = (bool)b; } catch { }
            try { var b = RxGet(u, "IsSurfaceSearchRadarsOn"); if (b is bool) s.SurfaceRadar = (bool)b; } catch { }
            try { s.WeaponStatus = WeaponStatusName(u); } catch { }
            try
            {
                foreach (var r in DecoyRows(u))
                {
                    if (r.Type == "Noisemaker") s.Noisemakers += r.Count;
                    else if (r.Type == "Chaff") s.ChaffAmmo += r.Count;
                }
            }
            catch { }
            try
            {
                var list = Get(Get(u, "_obp"), "_chaffWeapons") as System.Collections.IEnumerable;
                if (list != null)
                    foreach (var ws in list)
                    {
                        if (ws == null) continue;
                        try { if ((bool)Call(ws, "readyToLaunch")) s.ChaffReady++; } catch { }
                    }
            }
            catch { }
            try { s.AutoEvade = ResolveFlag(u, "auto_evade"); s.VetoNeutral = ResolveFlag(u, "veto_neutral"); } catch { }
            try { s.TimeCompression = D(GetStatic("SeaPower.GameTime", "TimeCompression")); } catch { }
            try { var p = CallStatic("SeaPower.GameTime", "IsPaused"); if (p is bool) s.Paused = (bool)p; } catch { }
            return s;
        }

        /// <summary>SnapUnit must tolerate null and foreign objects (offline selfcheck). null = ok.</summary>
        internal static string SnapSelfTest()
        {
            try
            {
                var api = new GameApi();
                var a = api.SnapUnit(null);
                var b = api.SnapUnit(new object());
                if (a == null || b == null) return "null snapshot";
                if (b.HeadingDeg.HasValue || b.Telegraph.HasValue || b.Waypoints != 0) return "foreign object produced values";
                return null;
            }
            catch (Exception e) { return "threw " + e.GetType().Name; }
        }

        private void DebugLog(IEnumerable<string> lines)
        {
            if (lines == null) return;
            foreach (var l in lines) Log(l);
        }

        /// <summary>Flag/mode change line for ActionDebug: value before and after plus when the flag has an effect.</summary>
        private static string FlagEffect(string flag)
        {
            switch (flag)
            {
                case "auto_evade": return "acts when a torpedo threatens and is on the plot";
                case "veto_neutral": return "acts only with the fire_veto patch installed";
                case "post_launch": return "acts after a guided missile launch";
                default: return "";
            }
        }
    }
}
