using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    // T-136, ADR-0045 §1: immutable snapshot rows and the pure-data contracts of the contact_info module.
    // Missing values are NaN / null, never 0 placeholders. No game or Unity types here.
    internal sealed class ContactInfoRow
    {
        public int Key; public bool Own;                    // Key = Vehicle.Id (track) or UniqueID (own unit)
        public bool Air;                                    // own aircraft / helicopter (no speed+noise line, ADR-0045 Supplement 1)
        public string Name;                                 // own unit name / null for tracks
        public double DepthFt = double.NaN, DepthErrFt = double.NaN, LayerOffsetFt = double.NaN; // depth > 0 down; offset > 0 = above layer
        public string LayerSide;                            // "above" | "below" | "none" | "nosol" | null (not subsurface)
        public double SpeedKn = double.NaN; public bool SpeedEstimated;
        public double TtgS = double.NaN; public int TtgTargetKey = -1; public string TtgTargetName;
        public bool TtgExact;                               // own weapon (exact TTG, no "~"); added in A (plan §4.1 had no field for it)
        public double CpaNm = double.NaN, TcpaS = double.NaN; public string CpaRefName;
        public double AgeS = double.NaN; public bool Stale;
        public double NoiseDb = double.NaN, CavOnsetKn = double.NaN; public bool Cavitating; public string CavState; // "ok"|"near"|"cav"|null
        public double Lat = double.NaN, Lon = double.NaN;   // label anchor (track / own position)
    }

    internal interface IContactInfo
    {
        ContactInfoRow Get(int trackId);       // contacts by Vehicle.Id; null = unknown
        ContactInfoRow GetOwn(int uniqueId);   // own units by UniqueID; null = unknown
        IList<int> OwnKeys { get; }            // own units in the last snapshot (ASW noise list)
        int Version { get; }                   // +1 on every full snapshot
    }

    // raw inputs the game adapter gives the controller (pure data, no game references)
    internal sealed class CiOwn { public int Uid; public string Name; public bool IsSub, IsShip, IsAir; public double Lat, Lon, VxKn, VzKn, DepthFt = double.NaN, SpeedKn = double.NaN, NoiseDb = double.NaN, CavOnsetKn = double.NaN; public bool Cavitating; public string FormationKey; }
    // CiTrack has no target field on purpose: a foreign weapon's CurrentTarget cannot even be passed (ADR-0045 §2)
    internal sealed class CiTrack { public int Id; public string Kind; public bool Live, HasVel, Subsurface, IsWeapon, HasDepthSolution; public double Lat, Lon, VxKn, VzKn, DepthFt = double.NaN, DepthErrFt = double.NaN, EpochS = double.NaN, LayerDepthFt = double.NaN; }
    internal sealed class CiOwnWeapon { public int TrackId; public int TargetKey; public string TargetName; public double RangeNm = double.NaN, SpeedKn = double.NaN; }
    internal sealed class CiWaypoint { public int OwnUid; public double RouteNm = double.NaN, SpeedKn = double.NaN, LastLat, LastLon; public double ClockS = double.NaN; } // ClockS = mission time of day, NaN = unknown

    /// <summary>T-157: mission hooks the lifecycle (MissionGame, order 65) calls on the contact_info service.</summary>
    internal interface IContactMissionHooks { void OnMissionEntered(); void OnMissionLeft(); }

    internal interface IContactSource     // implemented by GameContactSource (B) and by the fake in the stand (C)
    {
        bool InMission { get; }
        double MissionTimeS { get; }      // NaN = unknown
        int SelectedOwnUid { get; }       // 0 = none or not own
        IList<int> TrackIds();            // cheap: ids of PlottingTable vehicles
        CiTrack ReadTrack(int id);        // track only (no truth of the object)
        IList<CiOwn> ReadOwn();           // player's taskforce only
        IList<CiOwnWeapon> ReadOwnWeapons();
        CiWaypoint ReadWaypoint(int ownUid);
        IList<int> TakenLayerNumbers();
        bool WorldGlobe { get; }          // T-352: AswMath.WorldGlobe (false = the flat world of stable)   // LayerNumber of every item in Layers + keys of LayersById
    }

    internal interface IModProbe { bool HarmonyHas(string harmonyId); bool AssemblyLoaded(string name); }

    /// <summary>
    /// T-137 D: map labels of other modules (range rings, T-138 intercept course) drawn by the same label layer. Implemented by
    /// ContactInfoController; modules reach it with <c>ctx.Get&lt;IContactInfo&gt;() as IMapExtraLabels</c> (IContactInfo itself stays as is).
    /// </summary>
    internal interface IMapExtraLabels
    {
        /// <summary>owner = "range_rings", "intercept", ...; null/empty removes the owner's labels. At most 24 per owner are kept.
        /// Shown while contact_info is enabled and a mission runs, independent of Master; not subject to MaxLabels. Keys must not clash
        /// with track ids / unit uids (use negative keys).</summary>
        void SetExtraLabels(string owner, IList<LabelItem> items);
        /// <summary>A distance in the ContactInfo.Units of the moment ("25 nm" or "46.3 km").</summary>
        string FmtDistance(double nm);
    }

    /// <summary>LeftOfIcon: drawn right-aligned to the left of the unit icon, clear of the game's own name block on the right (own speed+noise line, ADR-0045 Supplement 1).</summary>
    internal sealed class LabelItem { public int Key; public double Lat, Lon; public string Text; public bool Dim; public uint Argb; public bool LeftOfIcon; }
    internal interface IContactLabels      // ContactLabelLayer (D); a fake in the stand
    {
        bool Ready { get; }                // Canvas attached or IMGUI fallback active
        string Mode { get; }               // "noesis" | "imgui" | "off"
        void Show(IList<LabelItem> items, int version); // re-layout only when version or projection changed
        void Hide();
    }

    internal sealed class ContactInfoSettings   // ADR-0045 §8 (filled by ContactInfoFeature.Bind)
    {
        public bool Enabled = true; public double PeriodS = 1.0; public int MaxLabels = 80; public string Units = "nm,kn,ft";
        public double AgeShowS = 15, StaleS = 30; public string CpaTarget = "selected"; public double CpaMaxMin = 30;
        public double TtgHitNm = 1.0, CavMarginKn = 1.0; public string Waypoint = "dist", BetterTacMap = "auto", NoiseDisplay = "auto";
        public string Hotkey = "LeftAlt+N|LeftAlt+E";
        public bool OwnSpeedNoise = true;                 // ADR-0045 Supplement 1: one "12 kn · 158 dB" line at own ships and submarines
        public bool[] Show = { true, false, true, true, true, true, true };       // index = CiLayer
        public string[] LayerHotkeys = { "LeftAlt+LeftShift+Keypad1", "LeftAlt+LeftShift+Keypad2", "LeftAlt+LeftShift+Keypad3",
                                         "LeftAlt+LeftShift+Keypad4", "LeftAlt+LeftShift+Keypad5", "LeftAlt+LeftShift+Keypad6", "LeftAlt+LeftShift+Keypad7" };
    }
    internal enum CiLayer { Depth = 0, Speed = 1, Ttg = 2, Cpa = 3, Age = 4, Noise = 5, Waypoint = 6 }
}
