using System;

namespace VoiceBotBridge
{
    // T-352 S4: the CAP zone window follows the world flag: flat (stable) 1 deg = 60 nm east-west, the beta globe keeps cos(lat).
    internal static partial class CapWindowTests
    {
        public static string ZoneWorldFlat()
        {
            foreach (bool globe in new[] { false, true })
                using (AswMath.WorldScope(globe))
                {
                    var r = new Rig(1.0, 580, Data(1)); r.Frame();
                    var zone = new[] { 59.5, 10.0, 60.5, 10.5 };   // mid latitude 60 exactly
                    r.Click("cap.draw"); r.B.OnRect(zone); r.Frame();
                    r.B.BeginEdit();
                    if (!r.B.Editing) return "zone_worldflat: no edit";
                    r.B.EditZone(ZoneHandle.E, 10, 0);
                    double dLon = r.B.DraftZone[3] - 10.5;
                    double want = globe ? 10.0 / (60.0 * Math.Cos(60.0 * Math.PI / 180.0)) : 10.0 / 60.0;
                    if (Math.Abs(dLon - want) > 1e-9) return "zone_worldflat globe=" + globe + ": dLon " + dLon + " want " + want;
                    r.B.EndEdit(false);
                    string lab = CapWindowBody.SizeLabel(new[] { 59.5, 10.0, 60.5, 11.0 });
                    string wantLab = globe ? "30 × 60" : "60 × 60";
                    if (lab == null || !lab.StartsWith(wantLab)) return "zone_worldflat globe=" + globe + ": label " + lab;
                }
            return null;
        }
    }
}
