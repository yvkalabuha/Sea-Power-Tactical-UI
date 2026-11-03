using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-236: the "Task:" labels of the air_guard source - a bird of the CAP alert group gets no "Retreat" label, a bird of an ASW group in the same spot does.
    internal static class AirGuardT236Tests
    {
        public static readonly string[] Parts = { "t236_cap_alert_no_retreat_label" };

        public static string Run(string part)
        {
            try
            {
                var g = new AirGuardFakeGame();
                g.Owns.Add(new AirOwn { Uid = 1, Name = "F-14", Lat = 0, Lon = 0, SpeedKn = 90, CourseDeg = 90, Helicopter = true, Window = "cap", Group = "CAP-A" });
                g.Owns.Add(new AirOwn { Uid = 2, Name = "SH-60", Lat = 0, Lon = 0, SpeedKn = 90, CourseDeg = 90, Helicopter = true, Window = "asw", Group = "ASW-1" });
                g.Tracks.Add(new AirTrack { Id = 10, Cls = "MiG-21bis", Lat = 62.0 / 60.0, Lon = 0, CourseDeg = 180, SpeedKn = 580, Hostile = true, RNm = 10, RSrc = "class" });
                var alert = new AirGuardGroup { Window = "cap", Name = "CAP-A", Kind = "cap_alert" }; alert.Uids.Add(1);
                var asw = new AirGuardGroup { Window = "asw", Name = "ASW-1", Kind = "area" }; asw.Uids.Add(2);
                var c = new AirGuardController(g, new AirGuardParams());
                c.RegisterGroups("cap", () => new[] { alert });
                c.RegisterGroups("asw", () => new[] { asw });
                for (double t = 0; t <= 3; t++) { g.Now = t; c.Tick(t); }
                var u2 = c.ForUnit(2);
                if (u2 == null || u2.Stage != AirStage.Retreat) return "setup: the ASW bird must be in the retreat";
                var into = new List<UnitActionLabel>();
                ((IUnitActionSource)c).CollectActions(3, into);
                bool has1 = false, has2 = false;
                foreach (var l in into) { if (l.Uid == 1) has1 = true; if (l.Uid == 2 && l.Source == "air_guard" && l.CmdKey == "ua.cmd.retreat") has2 = true; }
                if (has1) return "a CAP alert bird must not get the Retreat label";
                if (!has2) return "an ASW group bird in the retreat must keep the Retreat label";
            }
            catch (Exception e) { return part + ": " + e.GetType().Name + ": " + e.Message; }
            return null;
        }
    }
}

namespace VoiceBotBridge
{
    /// <summary>Stand stub of the retreat steering (the real one lives in the ASW controller); no steering = no orbit state.</summary>
    internal sealed class AirRetreatSteer
    {
        internal bool TryState(int uid, out string mode, out bool orbiting) { mode = null; orbiting = false; return false; }
    }
}
