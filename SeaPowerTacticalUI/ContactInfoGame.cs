using System;
using System.Collections;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-136 / ADR-0045 §2: game side of the contact_info module for FOREIGN units. Reads the player's track only
    /// (Vehicle of the PlottingTable): never the object behind it, never its weapon target, never its noise.
    /// Own units live in ContactInfoOwnGame.cs. Scanner: tests/test_sea_power_contact_info_leak.py.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>The contact_info service (set by ContactInfoFeature.Init, cleared in Dispose).</summary>
        internal IContactInfo ContactInfo;

        private readonly Dictionary<int, object> _ciVehicles = new Dictionary<int, object>();

        /// <summary>T-130: the mission was left; the cached vehicles are objects of it.</summary>
        internal void CiLeaveMission() { _ciVehicles.Clear(); var h = ContactInfo as IContactMissionHooks; if (h != null) h.OnMissionLeft(); }

        /// <summary>T-157: the mission was entered; the controller probes Better TacMap / Noise Display now.</summary>
        internal void CiEnterMission() { var h = ContactInfo as IContactMissionHooks; if (h != null) h.OnMissionEntered(); }

        // ---------------------------------------------------------------- tracks

        /// <summary>Ids of the PlottingTable vehicles; fills the id -> vehicle cache that CiReadTrack uses.</summary>
        internal IList<int> CiTrackIds()
        {
            var ids = new List<int>();
            _ciVehicles.Clear();
            try
            {
                var vehicles = Vehicles();
                if (vehicles == null) return ids;
                foreach (var v in vehicles)
                {
                    var id = D(Get(v, ContactInfoSpecs.VehId));
                    if (!id.HasValue) continue;
                    int k = (int)id.Value;
                    if (_ciVehicles.ContainsKey(k)) continue;
                    _ciVehicles[k] = v;
                    ids.Add(k);
                }
            }
            catch { }
            return ids;
        }

        /// <summary>One track: Vehicle members only. Any failure of a member gives NaN / false, never an exception.</summary>
        internal CiTrack CiReadTrack(int id)
        {
            object v;
            if (!_ciVehicles.TryGetValue(id, out v)) return null;
            try
            {
                var t = new CiTrack { Id = id };
                t.Kind = Kind(v);
                t.IsWeapon = t.Kind == "Missile" || t.Kind == "Torpedo";
                t.Subsurface = t.Kind == "Submarine" || t.Kind == "Torpedo" || t.Kind == "Biologic";
                var live = Get(v, ContactInfoSpecs.VehHasLive);
                t.Live = !(live is bool) || (bool)live;
                var pos = RxGet(v, ContactInfoSpecs.VehPosition);
                double lat, lon;
                if (!GeoOf(pos, out lat, out lon)) return null;
                t.Lat = lat; t.Lon = lon;
                double vx, vz;   // T-308: true axes at the track's position
                if (GridVelTrue(v, ContactInfoSpecs.VehVelocity, ContactInfoSpecs.VecX, ContactInfoSpecs.VecZ, lat, lon, out vx, out vz)) { t.HasVel = true; t.VxKn = vx * OverviewMath.KnPerUnity; t.VzKn = vz * OverviewMath.KnPerUnity; }
                t.EpochS = CiEpoch(v);
                if (t.Subsurface) CiReadDepth(v, pos, t);
                return t;
            }
            catch { return null; }
        }

        private double CiEpoch(object v)
        {
            try
            {
                var e = Get(v, ContactInfoSpecs.VehEpoch);
                var d = D(e) ?? D(Get(e, ContactInfoSpecs.EpochValue));
                // 0.27.8: Vehicle._detTrackEpochLive stays 0 until a TrackComponent gives it an epoch (a fresh dipping-sonar contact): 0 is "unknown",
                // not "seen at session start" (live: "seen 46:40 ago" right after the first detection)
                return d.HasValue && d.Value > 0 ? d.Value : double.NaN;
            }
            catch { return double.NaN; }
        }

        private void CiReadDepth(object v, object pos, CiTrack t)
        {
            try
            {
                var alt = Get(v, ContactInfoSpecs.VehAltitude);   // track: the depth estimate of the player's sonar track, null when none
                var pe = Get(alt, ContactInfoSpecs.TrackValue);
                var est = D(Get(pe, ContactInfoSpecs.EstimateOf));
                if (est.HasValue)
                {
                    t.HasDepthSolution = true;
                    t.DepthFt = -est.Value * SaFtPerUnity;
                    var err = D(Get(pe, ContactInfoSpecs.ErrorOf));
                    if (err.HasValue) t.DepthErrFt = Math.Abs(err.Value) * SaFtPerUnity;
                }
                var env = SingletonInstance(ContactInfoSpecs.EnvironmentType);
                if (env != null && pos != null)
                {
                    var lh = D(Call(env, ContactInfoSpecs.EnvLayerHeight, pos));
                    if (lh.HasValue && lh.Value < 0) t.LayerDepthFt = -lh.Value * SaFtPerUnity;
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- layers, mission

        /// <summary>Layer numbers already used: LayerNumber of every item in Layers plus the keys of LayersById.</summary>
        internal IList<int> CiTakenLayerNumbers()
        {
            var res = new List<int>();
            try
            {
                var mgr = GetStatic(ContactInfoSpecs.GizmoInstance);
                var layers = Get(mgr, ContactInfoSpecs.GizmoLayers) as IEnumerable;
                if (layers != null)
                    foreach (var l in layers)
                    {
                        var n = D(Get(l, ContactInfoSpecs.LayerNumber));
                        if (n.HasValue) res.Add((int)n.Value);
                    }
                var byId = Get(mgr, ContactInfoSpecs.GizmoLayersById) as IDictionary;
                if (byId != null)
                    foreach (var k in byId.Keys)
                    {
                        var n = D(k);
                        if (n.HasValue) res.Add((int)n.Value);
                    }
            }
            catch { }
            return res;
        }

        internal double CiMissionTimeS()
        {
            var t = Frame.MissionTimeS;
            return t.HasValue ? t.Value : double.NaN;
        }

    }

    namespace Ui
    {
        /// <summary>Forwards the IContactSource calls to the GameApi adapters (track part and own part).</summary>
        internal sealed class GameContactSource : IContactSource
        {
            private readonly GameApi _api;
            public GameContactSource(GameApi api) { _api = api; }
            public bool InMission { get { return _api.Frame.InMission; } }
            public double MissionTimeS { get { return _api.CiMissionTimeS(); } }
            public int SelectedOwnUid { get { return _api.CiSelectedOwnUid(); } }
            public IList<int> TrackIds() { return _api.CiTrackIds(); }
            public CiTrack ReadTrack(int id) { return _api.CiReadTrack(id); }
            public IList<CiOwn> ReadOwn() { return _api.CiReadOwn(); }
            public IList<CiOwnWeapon> ReadOwnWeapons() { return _api.CiReadOwnWeapons(); }
            public CiWaypoint ReadWaypoint(int ownUid) { return _api.CiReadWaypoint(ownUid); }
            public IList<int> TakenLayerNumbers() { return _api.CiTakenLayerNumbers(); }
            public bool WorldGlobe { get { return AswMath.WorldGlobe; } }
        }
    }
}
