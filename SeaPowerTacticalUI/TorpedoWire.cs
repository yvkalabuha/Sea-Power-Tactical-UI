using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>
    /// Wire-guided torpedo access of the smart attack (T-102, ADR-0038 section 9): reflection only, no state, no Physics/IMGUI,
    /// so the offline selfcheck covers every member. Torpedo parameters come from the game (AmmunitionParameters), never from a list in code.
    /// </summary>
    internal sealed partial class GameApi
    {
        internal const double SaUnityPerNm = 27.559496;
        internal const double SaFtPerUnity = 220.47266;
        internal const double SaKnPerUnitPerS = 1.0 / 0.0076554087;

        // ---------- reading ----------

        /// <summary>Boxed Vector3 / GeoPosition -> plane in nautical miles (x east, y north, Unity space of the current centre tile).</summary>
        internal SaVec SaPlane(object geo)
        {
            var v = (UnityEngine.Vector3)Call(geo, SmartAttackSpecs.GeoToUnity);
            return new SaVec(v.x / SaUnityPerNm, v.z / SaUnityPerNm);
        }

        /// <summary>AmmunitionParameters (or an Ammunition holding one in _ap) -> WireAmmo; null when it is not wire-guided or unreadable. Never throws.</summary>
        internal WireAmmo ReadWireAmmo(object ammo)
        {
            try
            {
                if (ammo == null) return null;
                var ap = Get(ammo, SmartAttackSpecs.AmmoParamsOf) ?? ammo;
                var mcc = Get(ap, SmartAttackSpecs.ApMidCourse);
                if (mcc == null || !BoolOf(Get(mcc, SmartAttackSpecs.MidCourseWire))) return null;
                var w = new WireAmmo
                {
                    File = Get(ap, SmartAttackSpecs.ApFileName) as string,
                    WireGuided = true,
                    WireControlsDepth = BoolOf(Get(ap, SmartAttackSpecs.ApWireControlsDepth)),
                    WireBreakKn = D(Get(ap, SmartAttackSpecs.ApWireBreakKn)) ?? 20,
                    MaxSpeedOnWireKn = SaMaxSpeedOnWire(D(Get(ap, SmartAttackSpecs.ApMaxSpeedOnWireKn))),
                    SeafloorClearanceFt = (D(Get(ap, SmartAttackSpecs.ApWireClearance)) ?? 0) * SaFtPerUnity,
                    TerminalKn = D(Get(ap, SmartAttackSpecs.ApTerminalKn)) ?? 0,
                    ActivateFraction = D(Get(ap, SmartAttackSpecs.ApActivateFraction)) ?? 0,
                    SeekerFovDeg = D(Get(ap, SmartAttackSpecs.ApSeekerFov)) ?? 0,
                    MaxDepthFt = (D(Get(ap, SmartAttackSpecs.ApMaxDepth)) ?? 0) * SaFtPerUnity
                };
                string t1 = (Get(ap, SmartAttackSpecs.ApTargetType) ?? "").ToString(), t2 = (Get(ap, SmartAttackSpecs.ApSecondaryTarget) ?? "").ToString();
                w.Asw = t1 == "ASW" || t2 == "ASW";
                w.Asuw = t1 == "ASuW" || t2 == "ASuW";
                var steps = Get(ap, SmartAttackSpecs.ApSpeedSettings) as IEnumerable;
                var sp = new List<double>(); var rg = new List<double>();
                if (steps != null)
                    foreach (var s in steps)
                    {
                        sp.Add(D(Get(s, SmartAttackSpecs.VecX)) ?? 0);
                        rg.Add((D(Get(s, SmartAttackSpecs.VecY)) ?? 0) / SaUnityPerNm);
                    }
                w.SpeedsKn = sp.ToArray(); w.RangesNm = rg.ToArray();
                // seeker range by the guidance type, as SeekerBase does
                string g = (Get(ap, SmartAttackSpecs.ApGuidance) ?? "").ToString();
                double act = D(Get(ap, SmartAttackSpecs.ApSeekerActive)) ?? 0, pas = D(Get(ap, SmartAttackSpecs.ApSeekerPassive)) ?? 0, wake = D(Get(ap, SmartAttackSpecs.ApSeekerWake)) ?? 0;
                double r;
                if (g == "ActiveRadarHoming" || g == "ActiveSonarHoming") r = act;
                else if (g == "ActivePassiveSonarHoming") r = Math.Max(act, pas);
                else if (g == "WakeHoming") r = wake;
                else r = pas;
                w.SeekerRangeNm = r / SaUnityPerNm;
                w.Count = 0;
                return w;
            }
            catch { return null; }
        }

        private static double SaMaxSpeedOnWire(double? v)
        {
            return !v.HasValue || v.Value >= 1e30 ? double.PositiveInfinity : v.Value;
        }

        /// <summary>Snapshot of a launched torpedo (never throws; null on a null object).</summary>
        internal TorpSnap ReadTorpedo(object t)
        {
            if (t == null) return null;
            var s = new TorpSnap { Id = UidOf(t) };
            try
            {
                s.Alive = IsAlive(t) && !BoolOf(Get(t, SmartAttackSpecs.ObjIsDestroyed));
                if (!s.Alive) return s;
                s.OnWire = BoolOf(Get(t, SmartAttackSpecs.WbOnWire));
                s.WireLost = BoolOf(RxGet(t, SmartAttackSpecs.WbConnectionLost)) || BoolOf(RxGet(t, SmartAttackSpecs.WbConnectionLostForever));
                var seeker = Get(t, SmartAttackSpecs.WbSeeker);
                try { s.SeekerActive = seeker != null && BoolOf(Call(seeker, SmartAttackSpecs.SeekerIsActive)); } catch { }
                s.Terminal = (Get(t, SmartAttackSpecs.WbFlightStage) ?? "").ToString() == "TerminalApproach";
                s.Pos = SaPlane(RxGet(t, SmartAttackSpecs.ObjPosition));
                var vel = Get(t, SmartAttackSpecs.ObjVelocityVec);   // grid-ok: smart-attack island (positions SaPlane = GeoPosition.ToUnity, grid axes; the wire commands go back to the grid)
                double vx = D(Get(vel, SmartAttackSpecs.VecX)) ?? 0, vz = D(Get(vel, SmartAttackSpecs.VecZ)) ?? 0;
                s.SpeedKn = Math.Sqrt(vx * vx + vz * vz) * SaKnPerUnitPerS;
                if (s.SpeedKn > 0.5) s.CourseDeg = ((Math.Atan2(vx, vz) * 180.0 / Math.PI) % 360 + 360) % 360;
                else s.CourseDeg = ((D(RxGet(t, SmartAttackSpecs.ObjHeading)) ?? 0) % 360 + 360) % 360;   // grid-ok: smart-attack island (grid course next to SaPlane positions)
                s.RangeLeftNm = (D(RxGet(t, SmartAttackSpecs.ObjRangeOnMap)) ?? 0) / SaUnityPerNm;
                try { s.MinRunFt = -(D(Call(t, SmartAttackSpecs.MinRunDepth)) ?? 0) * SaFtPerUnity; } catch { }
                s.SpeedIndex = (int)(D(RxGet(t, SmartAttackSpecs.SpeedSetting)) ?? -1);
                s.OrderedDepthFt = D(RxGet(t, SmartAttackSpecs.OrderedWireDepthFt)) ?? -1;
            }
            catch { }
            return s;
        }

        // ---------- orders ----------

        internal void SetWireDepth(object t, double ft)
        {
            Call(t, SmartAttackSpecs.OrderWireDepth, (float)ft);
        }

        internal void SetWireSpeed(object t, int index)
        {
            Call(t, SmartAttackSpecs.SetWireSpeedSetting, index);
        }

        /// <summary>shiftNm = aim shift in the Unity plane in miles (x east, y north) with the dog-leg phase Legging; null = zero shift and Direct.</summary>
        internal void SetAimShift(object t, SaVec? shiftNm)
        {
            var fShift = Member(SmartAttackSpecs.WireAimShift) as FieldInfo;
            var fPhase = Member(SmartAttackSpecs.WireDogLegPhase) as FieldInfo;
            if (fShift == null || fPhase == null) throw new MissingMemberException(t.GetType().Name, SmartAttackSpecs.WireAimShift.Member);
            if (shiftNm.HasValue)
                fShift.SetValue(t, new UnityEngine.Vector3((float)(shiftNm.Value.X * SaUnityPerNm), 0f, (float)(shiftNm.Value.Y * SaUnityPerNm)));
            else
                fShift.SetValue(t, UnityEngine.Vector3.zero);
            fPhase.SetValue(t, Enum.ToObject(fPhase.FieldType, shiftNm.HasValue ? 1 : 0));   // WireDogLeg: Direct = 0, Legging = 1
        }
    }
}
