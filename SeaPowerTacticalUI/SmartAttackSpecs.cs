using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M15 (ADR-0043): the game members of the smart torpedo attack (SmartAttackGame.cs, TorpedoWire.cs, SmartAttackPickGame.cs). Every
    /// member is optional, as before: capability smart_attack = all of them present plus the Common commands attack, set_telegraph and contacts
    /// (Derive); smart_pick_block (the pick-click patches and the alert) never removes smart_attack.
    /// Leak rule (ADR-0038 section 8, ADR-0045): a spec marked "// leak:" reads the true object behind a track or a hidden value; the static test
    /// tests/test_sea_power_smart_attack_leak.py allows its use only on lines marked "// leak-ok: <reason>".
    /// </summary>
    internal static class SmartAttackSpecs
    {
        private const string Cap = "smart_attack";
        private const string Pick = "smart_pick_block";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Torp = "SeaPower.Torpedo";
        private const string Wb = "SeaPower.WeaponBase";
        private const string Ap = "SeaPower.AmmunitionParameters";
        private const string Sub = "SeaPower.Submarine";
        private const string Veh = "SeaPower.Vehicle";

        // ---- torpedo and wire ----
        public static readonly MemberSpec TorpType = MemberSpec.TypeOf(Cap, Torp, false);
        public static readonly MemberSpec WireAimShift = MemberSpec.Field(Cap, Torp, "_wireAimShiftLocal", false);
        public static readonly MemberSpec WireDogLegPhase = MemberSpec.Field(Cap, Torp, "_wireDogLegPhase", false);
        public static readonly MemberSpec SetWireSpeedSetting = MemberSpec.Method(Cap, Torp, "SetWireSpeedSetting", new[] { "int" }, "void", false);
        public static readonly MemberSpec SpeedSetting = MemberSpec.Prop(Cap, Torp, "SpeedSetting", false);   // ReactiveProperty<int>
        public static readonly MemberSpec OrderWireDepth = MemberSpec.Method(Cap, Torp, "OrderWireDepth", new[] { "float" }, "void", false);
        public static readonly MemberSpec OrderedWireDepthFt = MemberSpec.Prop(Cap, Torp, "OrderedWireDepthFeet", false);   // ReactiveProperty<float>
        public static readonly MemberSpec WireDepthOrdered = MemberSpec.Field(Cap, Torp, "_wireDepthOrdered", false);
        public static readonly MemberSpec CanControlDepth = MemberSpec.Prop(Cap, Torp, "CanControlDepthOverWire", false);
        public static readonly MemberSpec MinRunDepth = MemberSpec.Method(Cap, Torp, "GetMinRunDepth", new string[0], "float", false);
        public static readonly MemberSpec WbOnWire = MemberSpec.Field(Cap, Wb, "_onWire", false);
        public static readonly MemberSpec WbConnectionLost = MemberSpec.Prop(Cap, Wb, "ConnectionLost", false);   // ReactiveProperty<bool>
        public static readonly MemberSpec WbConnectionLostForever = MemberSpec.Prop(Cap, Wb, "ConnectionLostForever", false);
        public static readonly MemberSpec WbLaunchPlatform = MemberSpec.Field(Cap, Wb, "_launchPlatform", false);
        public static readonly MemberSpec WbLaunchTime = MemberSpec.Field(Cap, Wb, "_launchTime", false);
        public static readonly MemberSpec WbFlightStage = MemberSpec.Prop(Cap, Wb, "_flightStage", false);
        public static readonly MemberSpec WbSeeker = MemberSpec.Field(Cap, Wb, "_seeker", false);
        public static readonly MemberSpec WbWeaponSystem = MemberSpec.Field(Cap, Wb, "_weaponSystem", false);
        public static readonly MemberSpec WbAp = MemberSpec.Field(Cap, Wb, "_ap", false);
        public static readonly MemberSpec SeekerIsActive = MemberSpec.Method(Cap, "SeaPower.SeekerBase", "isActive", new string[0], "bool", false);
        public static readonly MemberSpec WsLaunched = MemberSpec.Field(Cap, "SeaPower.WeaponSystem", "_launchedWeapons", false);
        public static readonly MemberSpec ObpWeaponSystems = MemberSpec.Field(Cap, "SeaPower.ObjectBaseParameters", "_weaponSystems", false);

        // ---- the own unit ----
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Cap, Ob, "_obp", false);
        public static readonly MemberSpec ObjAi = MemberSpec.Field(Cap, Ob, "_ai", false);
        public static readonly MemberSpec ObjUniqueId = MemberSpec.Prop(Cap, Ob, "UniqueID", false);
        public static readonly MemberSpec ObjHasFreeWire = MemberSpec.Method(Cap, Ob, "HasFreeWire", new string[0], "bool", false);
        public static readonly MemberSpec ObjMaxWireCount = MemberSpec.Field(Cap, Ob, "_maxWireCount", false);
        public static readonly MemberSpec ObjLaunchSpeeds = MemberSpec.Field(Cap, Ob, "_launchSpeedSettings", false);   // Dictionary<string, ReactiveProperty<int>>
        public static readonly MemberSpec ObjVelocityVec = MemberSpec.Field(Cap, Ob, "_velocityVecInUnity", false);
        public static readonly MemberSpec ObjRangeOnMap = MemberSpec.Prop(Cap, Ob, "RangeOnMap", false);   // ReactiveProperty<float>
        public static readonly MemberSpec ObjHeading = MemberSpec.Prop(Cap, Ob, "Heading", false);   // ReactiveProperty<float>
        public static readonly MemberSpec ObjHeightFt = MemberSpec.Method(Cap, Ob, "getHeightInFeet", new string[0], "float", false);
        public static readonly MemberSpec ObjVelocityKn = MemberSpec.Method(Cap, Ob, "getVelocityInKnots", new string[0], "float", false);
        public static readonly MemberSpec ObjIsUnit = MemberSpec.Method(Cap, Ob, "isUnit", new string[0], "bool", false);
        public static readonly MemberSpec ObjClassName = MemberSpec.Prop(Cap, Ob, "ClassName", false);
        public static readonly MemberSpec ObjIsDestroyed = MemberSpec.Prop(Cap, Ob, "IsDestroyed", false);
        public static readonly MemberSpec ObjPosition = MemberSpec.Prop(Cap, Ob, "Position", false);   // ReactiveProperty<GeoPosition>
        public static readonly MemberSpec ObjTaskforce = MemberSpec.Prop(Cap, Ob, "_taskforce", false);   // leak: the player's taskforce of the own submarine (for GetMapVehicle)
        public static readonly MemberSpec ObjGetMapVehicle = MemberSpec.Method(Cap, Ob, "GetMapVehicle", new[] { "Taskforce" }, "Vehicle", false);   // leak: a 3D click resolves to the player's track, never to the object itself
        public static readonly MemberSpec ObjGetAmmoByName = MemberSpec.Method(Cap, Ob, "getAmmunitionByName", new[] { "string" }, "Ammunition", false);
        public static readonly MemberSpec ObjAmmoCounts = MemberSpec.Prop(Cap, Ob, "AmmunitionAmountDictionary", false);

        // ---- the own submarine ----
        public static readonly MemberSpec SubType = MemberSpec.TypeOf(Cap, Sub, false);
        public static readonly MemberSpec SubSetPresetDepth = MemberSpec.Method(Cap, Sub, "setPresetDepth", new[] { "int", "bool" }, "int", false);
        public static readonly MemberSpec SubPresetDepthFt = MemberSpec.Method(Cap, Sub, "GetPresetDepthFeet", new[] { "int" }, "int", false);
        public static readonly MemberSpec SubSetDepth = MemberSpec.Method(Cap, Sub, "setDepth", new[] { "float" }, "void", false);
        public static readonly MemberSpec SubCurrentPreset = MemberSpec.Field(Cap, Sub, "_currentPresetDepth", false);
        public static readonly MemberSpec SubParams = MemberSpec.Prop(Cap, Sub, "SP", false);
        public static readonly MemberSpec SubTelegraphKn = MemberSpec.Field(Cap, "SeaPower.SubmarineParameters", "_telegraphVelocities", false);

        // ---- ammunition parameters (the field names are the ini keys of ADR-0038 section 9) ----
        public static readonly MemberSpec ApMidCourse = MemberSpec.Field(Cap, Ap, "_midCourseCorrection", false);
        public static readonly MemberSpec MidCourseWire = MemberSpec.Prop(Cap, "SeaPower.AmmunitionParameters+MidCourseCorrection", "IsWireGuided", false);
        public static readonly MemberSpec ApWireControlsDepth = MemberSpec.Field(Cap, Ap, "_wireControlsDepth", false);
        public static readonly MemberSpec ApSpeedSettings = MemberSpec.Field(Cap, Ap, "_torpedoSpeedSettings", false);   // List<Vector2>: x = knots, y = range in Unity units
        public static readonly MemberSpec ApWireBreakKn = MemberSpec.Field(Cap, Ap, "_wireBreakSpeedInKnots", false);
        public static readonly MemberSpec ApMaxSpeedOnWireKn = MemberSpec.Field(Cap, Ap, "_maxSpeedOnWireInKnots", false);
        public static readonly MemberSpec ApWireClearance = MemberSpec.Field(Cap, Ap, "_wireMinSeafloorClearanceUnity", false);
        public static readonly MemberSpec ApTerminalKn = MemberSpec.Field(Cap, Ap, "_terminalVelocityInKnots", false);
        public static readonly MemberSpec ApSeekerActive = MemberSpec.Field(Cap, Ap, "_seekerActiveRange", false);
        public static readonly MemberSpec ApSeekerPassive = MemberSpec.Field(Cap, Ap, "_seekerPassiveRange", false);
        public static readonly MemberSpec ApSeekerWake = MemberSpec.Field(Cap, Ap, "_wakeHomingSeekerRange", false);
        public static readonly MemberSpec ApActivateFraction = MemberSpec.Field(Cap, Ap, "_fractionOfRangeToActivateSeeker", false);
        public static readonly MemberSpec ApSeekerFov = MemberSpec.Field(Cap, Ap, "_seekerFOV", false);
        public static readonly MemberSpec ApMaxDepth = MemberSpec.Field(Cap, Ap, "_maxDepthUnity", false);
        public static readonly MemberSpec ApTargetType = MemberSpec.Field(Cap, Ap, "_targetType", false);
        public static readonly MemberSpec ApSecondaryTarget = MemberSpec.Field(Cap, Ap, "_secondaryTargetType", false);
        public static readonly MemberSpec ApGuidance = MemberSpec.Field(Cap, Ap, "_guidanceType", false);
        public static readonly MemberSpec ApFileName = MemberSpec.Field(Cap, Ap, "_ammunitionFileName", false);
        public static readonly MemberSpec ApRangeAtSpeed = MemberSpec.Method(Cap, Ap, "TorpedoRangeAtSpeed", new[] { "float" }, "float", false);
        public static readonly MemberSpec AmmoParamsOf = MemberSpec.Field(Cap, "SeaPower.Ammunition", "_ap", false);
        public static readonly MemberSpec AmmoTypeNone = MemberSpec.Field(Cap, "SeaPower.Ammunition+Type", "None", false, true);
        public static readonly MemberSpec AiAmmoForTarget = MemberSpec.Method(Cap, "SeaPower.AI", "AmmunitionForTarget", new[] { "ObjectBase", "Type" }, "List`1", false);

        // ---- own layer, bottom, tile ----
        public static readonly MemberSpec EnvironmentType = MemberSpec.TypeOf(Cap, "SeaPower.Environment", false);
        public static readonly MemberSpec EnvLayerHeight = MemberSpec.Method(Cap, "SeaPower.Environment", "getLayerHeight", new[] { "GeoPosition" }, "float", false);   // leak: the layer at the OWN position only (the player knows it from the sonar)
        public static readonly MemberSpec TerrainHeightAt = MemberSpec.Method(Cap, "SeaPower.TerrainUtils", "GetHeightAt", new[] { "GeoPosition", "bool", "bool" }, "float", false, true);
        public static readonly MemberSpec TileOf = MemberSpec.Method(Cap, "SeaPower.Utils", "longLatToTile", new[] { "GeoPosition" }, "Vector2Int", false, true);
        public static readonly MemberSpec GeoToUnity = MemberSpec.Method(Cap, "SeaPower.GeoPosition", "ToUnity", new string[0], "Vector3", false);

        // ---- the player's track (Vehicle) ----
        public static readonly MemberSpec VehPosition = MemberSpec.Prop(Cap, Veh, "Position", false);
        public static readonly MemberSpec VehVelocity = MemberSpec.Prop(Cap, Veh, "UnityVelocityVector", false);
        public static readonly MemberSpec VehHasLiveTrack = MemberSpec.Prop(Cap, Veh, "HasLiveTrack", false);
        public static readonly MemberSpec VehAltitude = MemberSpec.Field(Cap, Veh, "Altitude", false);   // leak: the depth estimate of the track (a sonar / TMA product), null when none
        public static readonly MemberSpec VehBearingFan = MemberSpec.Prop(Cap, Veh, "BearingFan", false);
        public static readonly MemberSpec VehId = MemberSpec.Prop(Cap, Veh, "Id", false);
        public static readonly MemberSpec VehClass = MemberSpec.Field(Cap, Veh, "Class", false);   // Nullable<SourcedProperty<string>>

        // ---- values inside generic or Unity structs: the type differs or is not loaded offline ----
        public static readonly MemberSpec SourcedValue = MemberSpec.DuckMember(Cap, "Value", MemberKind.Property, null, "SourcedProperty<T>.Value / ReactiveProperty<T>.Value: T differs per property");
        public static readonly MemberSpec EstimateOf = MemberSpec.DuckMember(Cap, "Estimate", MemberKind.Property, null, "PointEstimate.Estimate of the depth track (SourcedProperty<PointEstimate>)");
        public static readonly MemberSpec VecX = MemberSpec.DuckMember(Cap, "x", MemberKind.Field, null, "UnityEngine.Vector2 / Vector3 / Vector2Int component (Unity structs are not loaded by the offline selfcheck)");
        public static readonly MemberSpec VecY = MemberSpec.DuckMember(Cap, "y", MemberKind.Field, null, "UnityEngine.Vector2 / Vector2Int component (Unity structs are not loaded by the offline selfcheck)");
        public static readonly MemberSpec VecZ = MemberSpec.DuckMember(Cap, "z", MemberKind.Field, null, "UnityEngine.Vector3 component (Unity structs are not loaded by the offline selfcheck)");

        // ---- the pick on the map ----
        public static readonly MemberSpec MapUnitOf = MemberSpec.Prop(Cap, "SeapowerUI.MapUnitViewModel", "Unit", false);
        public static readonly MemberSpec ElementWidth = MemberSpec.Prop(Pick, "Noesis.FrameworkElement", "ActualWidth", false);
        public static readonly MemberSpec ElementHeight = MemberSpec.Prop(Pick, "Noesis.FrameworkElement", "ActualHeight", false);
        public static readonly MemberSpec TreeChildCount = MemberSpec.Method(Pick, "Noesis.VisualTreeHelper", "GetChildrenCount", new[] { "Noesis.DependencyObject" }, "int", false, true);
        public static readonly MemberSpec TreeChild = MemberSpec.Method(Pick, "Noesis.VisualTreeHelper", "GetChild", new[] { "Noesis.DependencyObject", "int" }, "Noesis.DependencyObject", false, true);

        // keep the pick click away from the game while GameApi.SmartPickBlocking (both patches or none: the capability smart_pick_block)
        public static readonly PatchSpec PatchPick3d = new PatchSpec("smart_pick_3d", Pick, "SeaPower.InputHandler", "handleMouseInputs", new string[0], "void", PatchKind.Prefix, "always");
        public static readonly PatchSpec PatchPickMap = new PatchSpec("smart_pick_map", Pick, "SeapowerUI.ViewModels.InteractionStateBroker", "MouseEvent", new[] { "object", "Noesis.MouseEventArgs" }, "void", PatchKind.Prefix, "always");

        public static void Derive(GameApi api, ISet<string> caps)
        {
            if (api == null) return;
            var c = api.Capabilities;
            if (!(c.Contains("attack") && c.Contains("set_telegraph") && c.Contains("contacts"))) caps.Remove(Cap);
        }

        public static readonly SpecSet Set = SpecSet.Of("smart_attack", typeof(SmartAttackSpecs));
    }
}
