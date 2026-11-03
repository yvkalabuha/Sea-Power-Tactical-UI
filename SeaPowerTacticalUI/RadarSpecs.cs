using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): the game members of the radar coverage zone (module radar: Coverage.cs, CoverageOverlay.cs, CoverageLayer.cs, CoverageLegendRt.cs).
    /// radar_coverage = what the radar snapshot and the terrain sampler read (required ones gate it); radar_noesis = the Noesis image layer inside the
    /// game's map (UiDegradation row radar_layer, fallback IMGUI); radar_overlay_aux / radar_coverage_aux = members that are read but never gated anything
    /// (not player functions; no row in UiDegradation). The capability radar_overlay itself is a run-time result of the overlay init (RadarFeature).
    /// </summary>
    internal static class RadarSpecs
    {
        private const string Cov = "radar_coverage";
        private const string Aux = "radar_coverage_aux";
        private const string Ovl = "radar_overlay_aux";
        private const string Nos = "radar_noesis";
        private const string Jam = "radar_jam";

        // ---------- the radars of own units ----------
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Cov, "SeaPower.ObjectBase", "_obp");
        public static readonly MemberSpec ObjEmcon = MemberSpec.Prop(Cov, "SeaPower.ObjectBase", "Emcon");
        public static readonly MemberSpec ObjIsAirUnit = MemberSpec.Prop(Cov, "SeaPower.ObjectBase", "IsAirUnit");
        public static readonly MemberSpec ParamsSearch = MemberSpec.Field(Cov, "SeaPower.ObjectBaseParameters", "_searchRadarSystems");
        public static readonly MemberSpec ParamsVisibleHeight = MemberSpec.Field(Cov, "SeaPower.ObjectBaseParameters", "_visibleHeight");
        public static readonly MemberSpec ParamsTargeting = MemberSpec.Field(Cov, "SeaPower.ObjectBaseParameters", "_targetingRadarSystems", false);
        public static readonly MemberSpec ObjSensorData = MemberSpec.Field(Cov, "SeaPower.ObjectBase", "_sensorData", false);
        public static readonly MemberSpec SysRadar = MemberSpec.Field(Cov, "SeaPower.SensorSystemRadar", "_radar");
        public static readonly MemberSpec SysType = MemberSpec.Field(Cov, "SeaPower.SensorSystemRadar", "_type");
        public static readonly MemberSpec SysGetRange = MemberSpec.Method(Cov, "SeaPower.SensorSystemRadar", "GetRange", new[] { "SeaPower.ObjectBase" }, null, false);
        public static readonly MemberSpec RadarRp = MemberSpec.Field(Cov, "SeaPower.Radar", "_rp");
        public static readonly MemberSpec RpMinRange = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_minRange");
        public static readonly MemberSpec RpMaxRange = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_maxRange");
        public static readonly MemberSpec RpMinAltitude = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_minAltitude");
        public static readonly MemberSpec RpMaxAltitude = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_maxAltitude");
        public static readonly MemberSpec RpPeakPower = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_peakPower");
        public static readonly MemberSpec RpGainFactor = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_gainFactor");
        public static readonly MemberSpec RpWaveLength = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_waveLength");
        public static readonly MemberSpec RpRole = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_role");
        public static readonly MemberSpec RpHasDataLink = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_hasDataLink");
        public static readonly MemberSpec RpRotatable = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_radarRotatable");
        public static readonly MemberSpec RpSystemName = MemberSpec.Field(Cov, "SeaPower.RadarParameters", "_systemName");
        public static readonly MemberSpec SysHArc = MemberSpec.Field(Cov, "SeaPower.SensorSystemRadar", "_horizontalViewArc");
        public static readonly MemberSpec SysVArc = MemberSpec.Field(Cov, "SeaPower.SensorSystemRadar", "_verticalViewArc");
        public static readonly MemberSpec SysLookDownMul = MemberSpec.Field(Cov, "SeaPower.SensorSystemRadar", "_lookDownMultiplier");
        public static readonly MemberSpec SysLookDownRange = MemberSpec.Field(Cov, "SeaPower.SensorSystemRadar", "_lookDownRange");
        public static readonly MemberSpec SysMount = MemberSpec.Field(Cov, "SeaPower.SensorSystemRadar", "_mountObject");
        public static readonly MemberSpec SysPosition = MemberSpec.Prop(Cov, "SeaPower.SensorSystemRadar", "SensorPosition");
        public static readonly MemberSpec SysIsOn = MemberSpec.Prop(Cov, "SeaPower.SensorSystemRadar", "IsOn");
        public static readonly MemberSpec SysInoperable = MemberSpec.Prop(Cov, "SeaPower.SensorSystemRadar", "Inoperable");

        // ---------- physics and terrain ----------
        public static readonly MemberSpec RadarHorizon = MemberSpec.Method(Cov, "SeaPower.RadarCalculator", "getRadarHorizon", new[] { "float", "float", "bool", "float" }, "float", true, true);
        public static readonly MemberSpec TerrainReal = MemberSpec.Method(Cov, "SeaPower.TerrainUtils", "GetRealHeightInMetersAt", new[] { "SeaPower.GeoPosition", "bool" }, "float", true, true);
        public static readonly MemberSpec RcsValues = MemberSpec.Prop(Cov, "SeaPower.Globals", "RcsValues", true, true);
        public static readonly MemberSpec RcsMinPower = MemberSpec.Field(Cov, "SeaPower.RCSValues", "_minDetectableReturnPowerRadar");
        public static readonly MemberSpec LowAltClutter = MemberSpec.Field(Cov, "SeaPower.Globals", "_lowAltitudeClutter", false, true);
        public static readonly MemberSpec TerrainDem = MemberSpec.Method(Cov, "SeaPower.TerrainUtils", "GetDEMHeightAt", new[] { "SeaPower.GeoPosition" }, "float", false, true);
        public static readonly MemberSpec UnityHeightToMeter = MemberSpec.Method(Cov, "SeaPower.Utils", "unityHeightToMeter", new[] { "float" }, "float", false, true);
        public static readonly MemberSpec TerrainEnabled = MemberSpec.Field(Cov, "SeaPower.Globals", "_isTerrainEnabled", false, true);
        public static readonly MemberSpec EnvSeaState = MemberSpec.Prop(Cov, "SeaPower.Environment", "SeaState");
        public static readonly MemberSpec WaveHeights = MemberSpec.Field(Cov, "SeaPower.Constants", "WAVE_HEIGHTS", true, true);

        // ---------- read but never gated anything ----------
        public static readonly MemberSpec EnvType = MemberSpec.TypeOf(Aux, "SeaPower.Environment", false);
        public static readonly MemberSpec ObjIsDestroyed = MemberSpec.Prop(Aux, "SeaPower.ObjectBase", "IsDestroyed", false);
        public static readonly MemberSpec DataMaxRcs = MemberSpec.Field(Aux, "SeaPower.SensorData", "_maxRCS", false);
        public static readonly MemberSpec DistDistance = MemberSpec.Field(Aux, "SeaPower.SensorSystem+SensorDistanceData", "Distance", false);
        public static readonly MemberSpec DistIssue = MemberSpec.Field(Aux, "SeaPower.SensorSystem+SensorDistanceData", "Issue", false);
        /// <summary>Submarine.IsSubmerged: asked for every own unit, the others have none.</summary>
        public static readonly MemberSpec IsSubmerged = MemberSpec.DuckMember(Aux, "IsSubmerged", MemberKind.Property, null, "declared on Submarine only; asked for every own unit");

        // ---------- the map and the overlay (optional: without them the overlay falls back to SceneMapViewModel matching) ----------
        public static readonly MemberSpec TypingActive = MemberSpec.Field(Ovl, "SeaPower.InputHandler", "typingActive", false);
        public static readonly MemberSpec OptionsType = MemberSpec.TypeOf(Ovl, "SeaPower.OptionsManager", false);
        public static readonly MemberSpec UiScaleProp = MemberSpec.Prop(Ovl, "SeaPower.OptionsManager", "UIScale", false);
        public static readonly MemberSpec WindowMaximized = MemberSpec.Prop(Ovl, "SeapowerUI.Window", "Maximized", false);
        public static readonly MemberSpec MainVmMap = MemberSpec.Prop(Ovl, "SeapowerUI.ViewModels.MainGameViewModel", "Map", false);
        public static readonly MemberSpec SceneMapFullscreen = MemberSpec.Prop(Ovl, "SeapowerUI.ViewModels.SceneMapViewModel", "IsFullscreen", false);
        public static readonly MemberSpec WindowType = MemberSpec.TypeOf(Ovl, "SeapowerUI.Window", false);
        public static readonly MemberSpec MapBaseType = MemberSpec.TypeOf(Ovl, "MapControl.MapBase", false);
        public static readonly MemberSpec SceneMapType = MemberSpec.TypeOf(Ovl, "SeapowerUI.ViewModels.SceneMapViewModel", false);
        public static readonly MemberSpec MainViewType = MemberSpec.TypeOf(Ovl, "SeapowerUI.MainGameView", false);
        public static readonly MemberSpec MainViewInstance = MemberSpec.Field(Ovl, "SeapowerUI.MainGameView", "Instance", false, true);
        public static readonly MemberSpec ElementWidth = MemberSpec.Prop(Ovl, "Noesis.FrameworkElement", "ActualWidth", false);
        public static readonly MemberSpec ElementHeight = MemberSpec.Prop(Ovl, "Noesis.FrameworkElement", "ActualHeight", false);
        public static readonly MemberSpec ElementDataContext = MemberSpec.Prop(Ovl, "Noesis.FrameworkElement", "DataContext", false);
        public static readonly MemberSpec ElementIsVisible = MemberSpec.Prop(Ovl, "Noesis.UIElement", "IsVisible", false);
        public static readonly MemberSpec TreeType = MemberSpec.TypeOf(Ovl, "Noesis.VisualTreeHelper", false);
        public static readonly MemberSpec TreeChildCount = MemberSpec.Method(Ovl, "Noesis.VisualTreeHelper", "GetChildrenCount", new[] { "Noesis.DependencyObject" }, "int", false, true);
        public static readonly MemberSpec TreeChild = MemberSpec.Method(Ovl, "Noesis.VisualTreeHelper", "GetChild", new[] { "Noesis.DependencyObject", "int" }, "Noesis.DependencyObject", false, true);
        public static readonly MemberSpec TreeParent = MemberSpec.Method(Ovl, "Noesis.VisualTreeHelper", "GetParent", new[] { "Noesis.DependencyObject" }, "Noesis.DependencyObject", false, true);
        public static readonly MemberSpec TreeHitTest = MemberSpec.Method(Ovl, "Noesis.VisualTreeHelper", "HitTest", new[] { "Noesis.Visual", "Noesis.Point" }, "Noesis.HitTestResult", false, true);
        /// <summary>X / Y of a Noesis point as it comes out of PointToScreen / LocationToView (the point type is found by reflection).</summary>
        public static readonly MemberSpec PointX = MemberSpec.DuckMember(Ovl, "X", MemberKind.Property, null, "the Point type is taken from the parameter of PointToScreen");
        public static readonly MemberSpec PointY = MemberSpec.DuckMember(Ovl, "Y", MemberKind.Property, null, "the Point type is taken from the parameter of PointToScreen");
        public static readonly MemberSpec HitVisual = MemberSpec.Prop(Ovl, "Noesis.HitTestResult", "VisualHit", false);
        /// <summary>Customer 2026-10-22: the game's time-compression panel (top-right): the element whose DataContext is a TimeCompressionViewModel
        /// (CoverageLayer.TimePanelProbe). The legends stand right under it; without this member they use a fixed place scaled to the screen.</summary>
        public static readonly MemberSpec TimeCompressionVmType = MemberSpec.TypeOf(Ovl, "SeapowerUI.ViewModels.TimeCompressionViewModel", false);

        // ---------- the Noesis layer (radar_noesis gates the layer; IMGUI otherwise) ----------
        public static readonly MemberSpec ImageType = MemberSpec.TypeOf(Nos, "Noesis.Image");
        public static readonly MemberSpec TextureSourceType = MemberSpec.TypeOf(Nos, "Noesis.TextureSource", false);
        public static readonly MemberSpec PanelType = MemberSpec.TypeOf(Nos, "Noesis.Panel", false);
        public static readonly MemberSpec CollectionType = MemberSpec.TypeOf(Nos, "Noesis.UIElementCollection", false);
        public static readonly MemberSpec ImgSource = MemberSpec.Prop(Nos, "Noesis.Image", "Source");
        public static readonly MemberSpec ImgStretch = MemberSpec.Prop(Nos, "Noesis.Image", "Stretch");
        public static readonly MemberSpec ImgHitTestVisible = MemberSpec.Prop(Nos, "Noesis.Image", "IsHitTestVisible");
        public static readonly MemberSpec ImgVisibility = MemberSpec.Prop(Nos, "Noesis.Image", "Visibility");
        public static readonly MemberSpec ImgWidth = MemberSpec.Prop(Nos, "Noesis.Image", "Width");
        public static readonly MemberSpec ImgHeight = MemberSpec.Prop(Nos, "Noesis.Image", "Height");
        public static readonly MemberSpec ImgHAlign = MemberSpec.Prop(Nos, "Noesis.Image", "HorizontalAlignment", false);
        public static readonly MemberSpec ImgVAlign = MemberSpec.Prop(Nos, "Noesis.Image", "VerticalAlignment", false);
        public static readonly MemberSpec PanelChildren = MemberSpec.Prop(Nos, "Noesis.Panel", "Children");
        public static readonly MemberSpec PanelSetZ = MemberSpec.Method(Nos, "Noesis.Panel", "SetZIndex", new[] { "Noesis.DependencyObject", "int" }, "void", true, true);
        public static readonly MemberSpec PanelGetZ = MemberSpec.Method(Nos, "Noesis.Panel", "GetZIndex", new[] { "Noesis.DependencyObject" }, "int", true, true);
        public static readonly MemberSpec CollCount = MemberSpec.Prop(Nos, "Noesis.UIElementCollection", "Count");
        public static readonly MemberSpec CollInsert = MemberSpec.Method(Nos, "Noesis.UIElementCollection", "Insert", new[] { "int", "Noesis.UIElement" }, "void");
        public static readonly MemberSpec CollRemove = MemberSpec.Method(Nos, "Noesis.UIElementCollection", "Remove", new[] { "Noesis.UIElement" }, "bool");
        public static readonly MemberSpec CollIndexOf = MemberSpec.Method(Nos, "Noesis.UIElementCollection", "IndexOf", new[] { "Noesis.UIElement" }, "int");
        public static readonly MemberSpec CollItem = MemberSpec.Method(Nos, "Noesis.UIElementCollection", "get_Item", new[] { "int" });
        public static readonly MemberSpec MapChildren = MemberSpec.Prop(Nos, "MapControl.MapBase", "Children");
        public static readonly MemberSpec TextureSourceCtor = MemberSpec.Ctor(Nos, "Noesis.TextureSource", new[] { "UnityEngine.Texture" });

        // ---------- T-182 (ADR-0081): jamming of the zone. radar_jam gates the cut; without it the zone works as before ----------
        public static readonly MemberSpec ObjJammers = MemberSpec.Field(Jam, "SeaPower.ObjectBase", "_ECMSystemsTryingToJamMe");   // List<SensorSystemECM>: ALL jammers incl. unknown ones; read only through the player's track filter
        public static readonly MemberSpec ObjJamBearings = MemberSpec.Field(Jam, "SeaPower.ObjectBase", "_jamBearings");   // List<float>: what the game itself shows the player
        public static readonly MemberSpec RpFrequency = MemberSpec.Field(Jam, "SeaPower.RadarParameters", "_frequency");
        public static readonly MemberSpec RpJamResistance = MemberSpec.Field(Jam, "SeaPower.RadarParameters", "_jamResistance");
        public static readonly MemberSpec EcmSysEcm = MemberSpec.Field(Jam, "SeaPower.SensorSystemECM", "_ecm");
        public static readonly MemberSpec EcmEp = MemberSpec.Field(Jam, "SeaPower.ECM", "_ep");   // leak: ECM parameters of a jammer; allowed only after the track is identified
        public static readonly MemberSpec EpType = MemberSpec.Field(Jam, "SeaPower.ECMParameters", "_type");
        public static readonly MemberSpec EpFrequencies = MemberSpec.Field(Jam, "SeaPower.ECMParameters", "_frequencies");
        public static readonly MemberSpec EpPeakPower = MemberSpec.Field(Jam, "SeaPower.ECMParameters", "_peakPower");
        public static readonly MemberSpec EpGainFactor = MemberSpec.Field(Jam, "SeaPower.ECMParameters", "_gainFactor");   // linear, 10^(dB/10)
        public static readonly MemberSpec EpJamEffectiveness = MemberSpec.Field(Jam, "SeaPower.ECMParameters", "_jamEffectiveness");
        public static readonly MemberSpec EcmIsOn = MemberSpec.Prop(Jam, "SeaPower.SensorSystemECM", "IsOn");   // ReactiveProperty<bool>
        public static readonly MemberSpec EcmInoperable = MemberSpec.Prop(Jam, "SeaPower.SensorSystemECM", "Inoperable");   // ReactiveProperty<bool>
        public static readonly MemberSpec EcmIsDestroyed = MemberSpec.Prop(Jam, "SeaPower.SensorSystemECM", "IsDestroyed");
        public static readonly MemberSpec EcmBaseObject = MemberSpec.Field(Jam, "SeaPower.SensorSystemECM", "_baseObject");   // leak: the true object of the jammer, only for GetMapVehicle(player)
        public static readonly MemberSpec ObjMapVehicleJam = MemberSpec.Method(Jam, "SeaPower.ObjectBase", "GetMapVehicle", new[] { "SeaPower.Taskforce" }, "SeaPower.Vehicle");
        public static readonly MemberSpec VehPosEstimate = MemberSpec.Prop(Jam, "SeaPower.Vehicle", "PositionEstimate");   // (GeoPosition, PositionEigpairs, Entity)?
        public static readonly MemberSpec VehIdentifiedJam = MemberSpec.Prop(Jam, "SeaPower.Vehicle", "Identified");   // ReactiveProperty<bool>
        public static readonly MemberSpec VehIdJam = MemberSpec.Prop(Jam, "SeaPower.Vehicle", "Id");
        public static readonly MemberSpec VehBearingFan = MemberSpec.Prop(Jam, "SeaPower.Vehicle", "BearingFan", false);   // not null = only a bearing fan, no fresh fix
        public static readonly MemberSpec ObjCrew = MemberSpec.Field(Jam, "SeaPower.ObjectBase", "_crew", false);
        public static readonly MemberSpec CrewBurnFactor = MemberSpec.Field(Jam, "SeaPower.Crew", "_burntroughRangeReduction", false);
        public static readonly MemberSpec RcsJ2S = MemberSpec.Field(Jam, "SeaPower.RCSValues", "_jamToSignalBurnThroughRatio", false);
        /// <summary>The ValueTuple item of PositionEstimate / the unit tuple elements are found on the boxed value.</summary>
        public static readonly MemberSpec TupleItem1 = MemberSpec.DuckMember(Jam, "Item1", MemberKind.Field, null, "ValueTuple`3.Item1 of Vehicle.PositionEstimate: GeoPosition of the estimate");
        public static readonly MemberSpec VehDetectingSensors = MemberSpec.Prop(Jam, "SeaPower.Vehicle", "DetectingSensors", false);   // SensorTypeSet flags of the player's track
        public static readonly MemberSpec TupleItem2 = MemberSpec.DuckMember(Jam, "Item2", MemberKind.Field, null, "ValueTuple`3.Item2 of Vehicle.PositionEstimate: PositionEigpairs");
        public static readonly MemberSpec EigMajorStdDev = MemberSpec.Field(Jam, "SeaPower.PositionEigpairs", "MajorStdDev", false);   // metres
        public static readonly MemberSpec FcrRange = MemberSpec.Method(Jam, "SeaPower.RadarCalculator", "GetFCRDetectionRange",
            new[] { "UnityEngine.Vector3", "float", "float", "float", "float", "List`1", "SeaPower.Globals+Frequency", "SeaPower.Crew", "float", "SeaPower.ObjectBase", "SeaPower.ObjectBase", "bool" }, "float", false, true);   // reference only (dev mod): takes the TRUE position of the jammer

        /// <summary>File name of the Noesis assembly: offline (selfcheck) it is loaded from the game's Managed folder when it is not loaded yet.</summary>
        public const string NoesisAssembly = "Noesis.NoesisGUI.dll";

        public static readonly SpecSet Set = SpecSet.Of("radar", typeof(RadarSpecs));
    }
}
