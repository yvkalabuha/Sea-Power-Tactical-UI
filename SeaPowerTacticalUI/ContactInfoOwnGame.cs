using System;
using System.Collections;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-136 / ADR-0045 §2: game side of the contact_info module for the PLAYER'S OWN units (the truth about them is
    /// the player's knowledge). Every Ci* method here starts with the CiIsOwn guard. Scanner: test_sea_power_contact_info_leak.py.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>The registry predicate of OwnOnly members (GameApi.SetOwnPredicate): a unit of the player, or a weapon launched by one.</summary>
        internal bool IsOwnForRegistry(object o)
        {
            try
            {
                if (o == null || !IsAlive(o)) return false;
                if (BoolOf(Get(o, ContactInfoSpecs.ObjIsPlayer))) return true;
                var platform = Get(o, ContactInfoSpecs.WeaponPlatform);
                return platform != null && IsAlive(platform) && BoolOf(Get(platform, ContactInfoSpecs.ObjIsPlayer));
            }
            catch { return false; }
        }

        /// <summary>Wires the OwnOnly guard of the registry (ADR-0045 section 2); called when the contact_info module starts.</summary>
        internal void WireOwnership() { SetOwnPredicate(IsOwnForRegistry); }

        /// <summary>Selfcheck: the guard answers false for null and foreign objects, and WireOwnership puts it in place. null = ok.</summary>
        internal string SelfTestOwnWiring()
        {
            try
            {
                SetOwnPredicate(null);
                if (GetOwn(new object(), ContactInfoSpecs.ObjAltitude) != null) return "own wiring: without a predicate nobody is own";
                WireOwnership();
                if (IsOwnForRegistry(null) || IsOwnForRegistry(new object())) return "own wiring: null and foreign objects must not be own";
                if (GetOwn(new object(), ContactInfoSpecs.ObjAltitude) != null || CallOwn(new object(), ContactInfoSpecs.ObjNoise) != null) return "own wiring: GetOwn / CallOwn of a foreign object must be null";
                return null;
            }
            catch (Exception e) { return "own wiring: " + e.GetType().Name + ": " + e.Message; }
            finally { SetOwnPredicate(null); }
        }

        private bool CiIsOwn(object u)
        {
            return u != null && IsAlive(u) && BoolOf(Get(u, ContactInfoSpecs.ObjIsPlayer));
        }

        private CiOwn CiOwnOf(object u)
        {
            if (!CiIsOwn(u)) return null;
            try
            {
                double lat, lon, h;
                if (!OwnPos(u, out lat, out lon, out h)) return null;
                var o = new CiOwn { Uid = UidOf(u), Name = NameOf(u), Lat = lat, Lon = lon };
                string type = StrOrNull(Get(u, ContactInfoSpecs.ObjType));
                o.IsSub = type == "Submarine"; o.IsAir = type == "Aircraft" || type == "Helicopter"; o.IsShip = !o.IsSub && !o.IsAir;
                var vel = GetOwn(u, ContactInfoSpecs.ObjVelocity);
                o.VxKn = (D(Get(vel, ContactInfoSpecs.VecX)) ?? 0) * OverviewMath.KnPerUnity;
                o.VzKn = (D(Get(vel, ContactInfoSpecs.VecZ)) ?? 0) * OverviewMath.KnPerUnity;
                var sp = D(CallOwn(u, ContactInfoSpecs.ObjVelocityKn));
                if (sp.HasValue) o.SpeedKn = Math.Abs(sp.Value);
                if (o.IsSub)
                {
                    var alt = D(RxGetOwn(u, ContactInfoSpecs.ObjAltitude));
                    if (alt.HasValue) o.DepthFt = -alt.Value * SaFtPerUnity;
                }
                if (o.IsSub || o.IsShip) CiReadNoise(u, o);
                return o;
            }
            catch { return null; }
        }

        private void CiReadNoise(object u, CiOwn o)
        {
            if (!CiIsOwn(u)) return;
            try { var n = D(CallOwn(u, ContactInfoSpecs.ObjNoise)); if (n.HasValue) o.NoiseDb = n.Value; } catch { }
            try { var c = D(CallOwn(u, ContactInfoSpecs.ObjCavitationOnset)); if (c.HasValue) o.CavOnsetKn = c.Value; } catch { }
            o.Cavitating = BoolOf(GetOwn(u, ContactInfoSpecs.ObjCavitating));
        }

        /// <summary>The player's taskforce units (position, velocity, depth, speed, noise, cavitation).</summary>
        internal IList<CiOwn> CiReadOwn()
        {
            var res = new List<CiOwn>();
            foreach (var u in OwnUnits()) if (CiIsOwn(u))
            {
                var o = CiOwnOf(u);
                if (o != null) res.Add(o);
            }
            return res;
        }

        /// <summary>Own weapons in flight. The target comes from the player's TRACK of it (VehicleForObject), never from the object's position.</summary>
        internal IList<CiOwnWeapon> CiReadOwnWeapons()
        {
            var res = new List<CiOwnWeapon>();
            foreach (var u in OwnUnits())
            {
                if (!CiIsOwn(u)) continue;
                try
                {
                    var systems = Get(Get(u, ContactInfoSpecs.ObjObp), ContactInfoSpecs.ObpWeaponSystems) as IEnumerable;
                    if (systems == null) continue;
                    foreach (var ws in systems)
                    {
                        var launched = Get(ws, ContactInfoSpecs.WsLaunched) as IEnumerable;
                        if (launched == null) continue;
                        foreach (var w in launched)
                        {
                            var cw = CiOwnWeaponOf(u, w);
                            if (cw != null) res.Add(cw);
                        }
                    }
                }
                catch { }
            }
            return res;
        }

        private CiOwnWeapon CiOwnWeaponOf(object owner, object w)
        {
            if (!CiIsOwn(owner) || w == null || !IsAlive(w) || BoolOf(Get(w, ContactInfoSpecs.ObjIsDestroyed))) return null;
            try
            {
                var own = VehicleForObject(w, owner);
                var d = own == null ? null : D(Get(own, ContactInfoSpecs.VehId));
                if (!d.HasValue) return null;
                var cw = new CiOwnWeapon { TrackId = (int)d.Value, TargetKey = -1 };
                var tgt = GetOwn(w, ContactInfoSpecs.WeaponTarget);
                var track = tgt == null ? null : VehicleForObject(tgt, owner);   // the target's position is the player's track of it
                var tid = track == null ? null : D(Get(track, ContactInfoSpecs.VehId));
                double tla, tlo, wla, wlo, h;
                if (!tid.HasValue || !GeoOf(RxGet(track, ContactInfoSpecs.VehPosition), out tla, out tlo) || !OwnPos(w, out wla, out wlo, out h)) return cw;
                cw.TargetKey = (int)tid.Value;
                cw.RangeNm = WorldDistanceNm(wla, wlo, tla, tlo);
                var sp = D(CallOwn(w, ContactInfoSpecs.ObjVelocityKn));
                if (sp.HasValue) cw.SpeedKn = Math.Abs(sp.Value);
                return cw;
            }
            catch { return null; }
        }

        /// <summary>Route length to the last waypoint of an own unit, its speed and the mission clock (NaN when unknown).</summary>
        internal CiWaypoint CiReadWaypoint(int ownUid)
        {
            var u = FindOwnUnit(ownUid);
            if (!CiIsOwn(u)) return null;
            try
            {
                double lat, lon, h;
                if (!OwnPos(u, out lat, out lon, out h)) return null;
                var wps = Waypoints(u);
                if (wps.Count == 0) return null;
                var w = new CiWaypoint { OwnUid = ownUid, LastLat = wps[wps.Count - 1][0], LastLon = wps[wps.Count - 1][1] };
                w.RouteNm = ContactInfoMath.RouteNm(lat, lon, wps, AswMath.WorldGlobe);
                var sp = D(CallOwn(u, ContactInfoSpecs.ObjVelocityKn));
                if (sp.HasValue) w.SpeedKn = Math.Abs(sp.Value);
                // ClockS stays NaN: no verified game member for the mission time of day (eta then shows the countdown); T-136 journal
                return w;
            }
            catch { return null; }
        }

        /// <summary>UniqueID of the selected unit when it is the player's own, else 0.</summary>
        internal int CiSelectedOwnUid()
        {
            string src;
            var u = SelectedUnit(out src);
            if (!CiIsOwn(u)) return 0;
            return UidOf(u);
        }
    }
}
