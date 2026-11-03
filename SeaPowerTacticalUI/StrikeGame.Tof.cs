using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 4: the time of flight from the game's own simulator (MissileSimulator.EstimateShot, no side effects, the same call the range rings make).
    // No spec / no ammunition seen / an exception / the target too close = false: the controller takes the rough formula and writes tof_src=rough.
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            /// <summary>The simulated flight from (fromLat, fromLon, altFt) at kn to the aim point of the ship. The frame is flat and local (the simulator only needs the geometry).</summary>
            public bool Tof(int uid, StrikeMissile m, double fromLat, double fromLon, double altFt, double kn, StrikeShip target, double aimLat, double aimLon, out double tofS)
            {
                tofS = 0;
                try
                {
                    if (m == null || target == null || !_a.Present(StrikeSpecs.SimEstimateShot)) return false;
                    var ap = ApOf(m.Ammo);
                    if (ap == null) return false;
                    double eastNm = GeoLon.DLon(aimLon, fromLon) * 60.0 * (AswMath.WorldGlobe ? Math.Cos(aimLat * Math.PI / 180.0) : 1.0), northNm = (fromLat - aimLat) * 60.0;
                    var launch = new UnityEngine.Vector3((float)(eastNm * UnitsPerNm), (float)(Math.Max(altFt, 0) / FtPerUnit), (float)(northNm * UnitsPerNm));
                    var aim = UnityEngine.Vector3.zero;
                    double hdg = target.CourseDeg * Math.PI / 180.0, v = target.SpeedKn / OverviewMath.KnPerUnity;
                    var tv = new UnityEngine.Vector3((float)(Math.Sin(hdg) * v), 0f, (float)(Math.Cos(hdg) * v));
                    float startKn = (float)(kn > 0 ? kn : 450);
                    var r = _a.CallStaticOpt(StrikeSpecs.SimEstimateShot, ap, launch, startKn, tv, aim, false);
                    if (r == null) return false;
                    if (BoolOf(_a.Get(r, StrikeSpecs.SimTargetTooClose))) return false;
                    double t = GameApi.D(_a.Get(r, StrikeSpecs.SimInterceptTime)) ?? 0;
                    if (double.IsNaN(t) || t <= 0 || t > 3600 * 3) return false;
                    tofS = t;
                    return true;
                }
                catch (Exception) { tofS = 0; return false; }
            }
        }
    }
}
