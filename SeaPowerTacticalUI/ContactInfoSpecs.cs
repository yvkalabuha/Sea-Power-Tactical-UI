using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M18 (ADR-0043, ADR-0045 section 10): the game members of the contact information on the map (ContactInfoGame.cs for foreign units,
    /// ContactInfoOwnGame.cs for the player's own, ContactLabelLayer.cs for the Noesis label canvas). contact_info = the player's track (Vehicle) and the
    /// map layer manager; the optional parts have a capability each (age, layer, noise, waypoint, canvas) and a row in UiDegradation.
    /// OwnOnly members (ADR-0045 section 2) are read only through GetOwn / CallOwn / RxGetOwn: for an object that is not the player's they answer null.
    /// </summary>
    internal static class ContactInfoSpecs
    {
        private const string Cap = "contact_info";
        private const string Veh = "SeaPower.Vehicle";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Gizmo = "SeapowerUI.GizmoLayerManager";

        // ---- the player's track (foreign units: this is all that is read about them) ----
        public static readonly MemberSpec VehPosition = MemberSpec.Prop(Cap, Veh, "Position");   // ReactiveProperty<GeoPosition>
        public static readonly MemberSpec VehVelocity = MemberSpec.Prop(Cap, Veh, "UnityVelocityVector");
        public static readonly MemberSpec VehAltitude = MemberSpec.Field(Cap, Veh, "Altitude");   // track: the depth estimate of the player's sonar track
        public static readonly MemberSpec VehHasLive = MemberSpec.Prop(Cap, Veh, "HasLiveTrack");
        public static readonly MemberSpec VehId = MemberSpec.Prop(Cap, Veh, "Id");
        public static readonly MemberSpec GetMapVehicle = MemberSpec.Method(Cap, Ob, "GetMapVehicle", new[] { "Taskforce" }, "Vehicle");
        public static readonly MemberSpec MissionSessionTime = MemberSpec.Field(Cap, "SeaPower.GameTime", "missionSessionTime", true, true);
        public static readonly MemberSpec GizmoInstance = MemberSpec.Prop(Cap, Gizmo, "Instance", true, true);
        public static readonly MemberSpec GizmoLayers = MemberSpec.Field(Cap, Gizmo, "Layers");
        public static readonly MemberSpec GizmoLayersById = MemberSpec.Field(Cap, Gizmo, "LayersById");
        public static readonly MemberSpec LayerNumber = MemberSpec.Prop(Cap, "SeapowerUI.MapLayer", "LayerNumber", false);

        // values inside generic or struct values of the game
        public static readonly MemberSpec TrackValue = MemberSpec.DuckMember(Cap, "Value", MemberKind.Property, null, "SourcedProperty<T>: the track's depth, T differs");
        public static readonly MemberSpec EstimateOf = MemberSpec.DuckMember(Cap, "Estimate", MemberKind.Field, null, "PointEstimate.Estimate of the depth track");
        public static readonly MemberSpec ErrorOf = MemberSpec.DuckMember(Cap, "Error", MemberKind.Field, null, "PointEstimate.Error of the depth track");
        public static readonly MemberSpec VecX = MemberSpec.DuckMember(Cap, "x", MemberKind.Field, null, "UnityEngine.Vector3 component (a Unity struct, not loaded by the offline selfcheck)");
        public static readonly MemberSpec VecZ = MemberSpec.DuckMember(Cap, "z", MemberKind.Field, null, "UnityEngine.Vector3 component (a Unity struct, not loaded by the offline selfcheck)");

        // ---- optional parts, one capability each ----
        public static readonly MemberSpec VehEpoch = MemberSpec.Field("contact_info_age", Veh, "_detTrackEpochLive", false);
        public static readonly MemberSpec EpochValue = MemberSpec.DuckMember("contact_info_age", "Value", MemberKind.Property, null, "older games hold the epoch in a SourcedProperty<float>");
        public static readonly MemberSpec EnvironmentType = MemberSpec.TypeOf("contact_info_layer", "SeaPower.Environment", false);
        public static readonly MemberSpec EnvLayerHeight = MemberSpec.Method("contact_info_layer", "SeaPower.Environment", "getLayerHeight", new[] { "GeoPosition" }, "float", false);
        public static readonly MemberSpec ObjNoise = MemberSpec.Method("contact_info_noise", Ob, "getNoise", new string[0], "float", required: false, ownOnly: true);
        public static readonly MemberSpec ObjCavitationOnset = MemberSpec.Method("contact_info_noise", Ob, "CavitationOnsetKts", new string[0], "float", required: false, ownOnly: true);
        public static readonly MemberSpec ObjCavitating = MemberSpec.Field("contact_info_noise", Ob, "_isCavitating", required: false, ownOnly: true);
        public static readonly MemberSpec ObjWaypoints = MemberSpec.Method("contact_info_waypoint", Ob, "GetAllWaypointsGeoPositions", new[] { "bool" }, "List`1", false);

        // ---- the player's own units (every read here is behind GetOwn / CallOwn) ----
        public static readonly MemberSpec ObjIsPlayer = MemberSpec.Prop(Cap, Ob, "IsPlayerObject", false);
        public static readonly MemberSpec ObjType = MemberSpec.Field(Cap, Ob, "_type", false);
        public static readonly MemberSpec ObjVelocity = MemberSpec.Prop(Cap, Ob, "UnityVelocityVector", required: false, ownOnly: true);
        public static readonly MemberSpec ObjVelocityKn = MemberSpec.Method(Cap, Ob, "getVelocityInKnots", new string[0], "float", required: false, ownOnly: true);
        public static readonly MemberSpec ObjAltitude = MemberSpec.Prop(Cap, Ob, "Altitude", required: false, ownOnly: true);   // ReactiveProperty<float>: the depth of an own submarine
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Cap, Ob, "_obp", false);
        public static readonly MemberSpec ObpWeaponSystems = MemberSpec.Field(Cap, "SeaPower.ObjectBaseParameters", "_weaponSystems", false);
        public static readonly MemberSpec WsLaunched = MemberSpec.Field(Cap, "SeaPower.WeaponSystem", "_launchedWeapons", false);
        public static readonly MemberSpec ObjIsDestroyed = MemberSpec.Prop(Cap, Ob, "IsDestroyed", false);
        public static readonly MemberSpec WeaponTarget = MemberSpec.Prop(Cap, "SeaPower.WeaponBase", "CurrentTarget", required: false, ownOnly: true);
        public static readonly MemberSpec WeaponPlatform = MemberSpec.Field(Cap, "SeaPower.WeaponBase", "_launchPlatform", false);

        // ---- the label canvas on the map (Noesis); contact_info_canvas = all of it present, else the labels are drawn with IMGUI ----
        private const string Nc = "contact_info_canvas";
        public static readonly MemberSpec CanvasType = MemberSpec.TypeOf(Nc, "Noesis.Canvas", false);
        public static readonly MemberSpec TextType = MemberSpec.TypeOf(Nc, "Noesis.TextBlock", false);
        public static readonly MemberSpec BrushType = MemberSpec.TypeOf(Nc, "Noesis.SolidColorBrush", false);
        public static readonly MemberSpec ColorType = MemberSpec.TypeOf(Nc, "Noesis.Color", false);
        public static readonly MemberSpec PanelType = MemberSpec.TypeOf(Nc, "Noesis.Panel", false);
        public static readonly MemberSpec CollectionType = MemberSpec.TypeOf(Nc, "Noesis.UIElementCollection", false);
        public static readonly MemberSpec PanelChildren = MemberSpec.Prop(Nc, "Noesis.Panel", "Children", false);
        public static readonly MemberSpec CollectionCount = MemberSpec.Prop(Nc, "Noesis.BaseUICollection", "Count", false);
        public static readonly MemberSpec ElementVisibility = MemberSpec.Prop(Nc, "Noesis.UIElement", "Visibility", false);
        public static readonly MemberSpec ElementHitTest = MemberSpec.Prop(Nc, "Noesis.UIElement", "IsHitTestVisible", false);
        public static readonly MemberSpec ElementWidth = MemberSpec.Prop(Nc, "Noesis.FrameworkElement", "Width", false);
        public static readonly MemberSpec ElementHeight = MemberSpec.Prop(Nc, "Noesis.FrameworkElement", "Height", false);
        public static readonly MemberSpec ElementHAlign = MemberSpec.Prop(Nc, "Noesis.FrameworkElement", "HorizontalAlignment", false);
        public static readonly MemberSpec ElementVAlign = MemberSpec.Prop(Nc, "Noesis.FrameworkElement", "VerticalAlignment", false);
        public static readonly MemberSpec TextText = MemberSpec.Prop(Nc, "Noesis.TextBlock", "Text", false);
        public static readonly MemberSpec TextForeground = MemberSpec.Prop(Nc, "Noesis.TextBlock", "Foreground", false);
        public static readonly MemberSpec TextBackground = MemberSpec.Prop(Nc, "Noesis.TextBlock", "Background", false);
        public static readonly MemberSpec TextFontSize = MemberSpec.Prop(Nc, "Noesis.TextBlock", "FontSize", false);
        public static readonly MemberSpec CanvasSetLeft = MemberSpec.Method(Nc, "Noesis.Canvas", "SetLeft", new[] { "Noesis.UIElement", "float" }, "void", false, true);
        public static readonly MemberSpec CanvasSetTop = MemberSpec.Method(Nc, "Noesis.Canvas", "SetTop", new[] { "Noesis.UIElement", "float" }, "void", false, true);
        public static readonly MemberSpec PanelSetZ = MemberSpec.Method(Nc, "Noesis.Panel", "SetZIndex", new[] { "Noesis.DependencyObject", "int" }, "void", false, true);
        public static readonly MemberSpec PanelGetZ = MemberSpec.Method(Nc, "Noesis.Panel", "GetZIndex", new[] { "Noesis.DependencyObject" }, "int", false, true);
        public static readonly MemberSpec ListInsert = MemberSpec.Method(Nc, "Noesis.UIElementCollection", "Insert", new[] { "int", "Noesis.UIElement" }, "void", false);
        public static readonly MemberSpec ListRemove = MemberSpec.Method(Nc, "Noesis.UIElementCollection", "Remove", new[] { "Noesis.UIElement" }, "bool", false);
        public static readonly MemberSpec ListIndexOf = MemberSpec.Method(Nc, "Noesis.UIElementCollection", "IndexOf", new[] { "Noesis.UIElement" }, "int", false);
        public static readonly MemberSpec ListItem = MemberSpec.Method(Nc, "Noesis.UIElementCollection", "get_Item", new[] { "int" }, "Noesis.UIElement", false);
        public static readonly MemberSpec ListAdd = MemberSpec.Method(Nc, "Noesis.UIElementCollection", "Add", new[] { "Noesis.UIElement" }, "void", false);
        public static readonly MemberSpec ColorFromArgb = MemberSpec.Method(Nc, "Noesis.Color", "FromArgb", new[] { "byte", "byte", "byte", "byte" }, "Noesis.Color", false, true);
        public static readonly MemberSpec BrushCtor = MemberSpec.Ctor(Nc, "Noesis.SolidColorBrush", new[] { "Noesis.Color" }, false);
        public static readonly MemberSpec CanvasCtor = MemberSpec.Ctor(Nc, "Noesis.Canvas", new string[0], false);
        public static readonly MemberSpec TextCtor = MemberSpec.Ctor(Nc, "Noesis.TextBlock", new string[0], false);

        public static void Derive(GameApi api, ISet<string> caps)
        {
            if (api != null && !api.Capabilities.Contains("contacts")) caps.Remove(Cap);   // the track list itself comes from Common
        }

        public static readonly SpecSet Set = SpecSet.Of("contact_info", typeof(ContactInfoSpecs));
    }
}
