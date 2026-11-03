using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): the game members of the Overview panel (module overview: OverviewGame.cs, Overview.cs). Capabilities: overview (the contacts table),
    /// overview_center (centre the map), overview_select (select a contact), overview_block (mouse block). The members the Common contact snapshot reads
    /// (Vehicle.*) are listed here for the check only: Common keeps its own accessors. ADR-0045: Altitude of an object is OwnOnly and never read for a contact.
    /// </summary>
    internal static class OverviewSpecs
    {
        private const string Cap = "overview";
        private const string Aux = "overview_aux";

        // ---------- the contacts table (required: the panel cannot work without them) ----------
        public static readonly MemberSpec VehObject = MemberSpec.Field(Cap, "SeaPower.Vehicle", "Object");
        public static readonly MemberSpec VehId = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "Id");
        public static readonly MemberSpec VehPosition = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "Position");
        public static readonly MemberSpec VehVelocity = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "UnityVelocityVector");
        public static readonly MemberSpec UnitVehicle = MemberSpec.Prop(Aux, "SeapowerUI.MapUnitViewModel", "Unit", false);   // beta: Vehicle.ViewModel is gone, the view model is found in MapKnownUnits.AllUnits
        public static readonly MemberSpec VehBearingFan = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "BearingFan");
        public static readonly MemberSpec VehHasLiveTrack = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "HasLiveTrack");
        public static readonly MemberSpec VehIsVisible = MemberSpec.Method(Cap, "SeaPower.Vehicle", "IsVisible", new string[0], "bool");
        public static readonly MemberSpec VehClass = MemberSpec.Field(Cap, "SeaPower.Vehicle", "Class");
        public static readonly MemberSpec VehIdentified = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "Identified");
        /// <summary>ADR-0045: the altitude of an object is read for the player's own units only.</summary>
        public static readonly MemberSpec ObjAltitude = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "Altitude", true, false, true);
        public static readonly MemberSpec ObjType = MemberSpec.Field(Cap, "SeaPower.ObjectBase", "_type");
        public static readonly MemberSpec ObjVelocity = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "UnityVelocityVector");
        public static readonly MemberSpec UnitSelected = MemberSpec.Prop(Cap, "SeapowerUI.MapUnitViewModel", "Selected");
        public static readonly MemberSpec BrushColor = MemberSpec.Prop(Cap, "Noesis.SolidColorBrush", "Color");

        // ---------- optional parts of the panel ----------
        public static readonly MemberSpec UnitVisualType = MemberSpec.Prop(Cap, "SeapowerUI.MapUnitViewModel", "VisualType", false);   // T-092: the window no longer depends on the view model colours
        public static readonly MemberSpec UnitVisualPosture = MemberSpec.Prop(Cap, "SeapowerUI.MapUnitViewModel", "VisualPosture", false);
        public static readonly MemberSpec UnitColorBrush = MemberSpec.Prop(Cap, "SeapowerUI.MapUnitViewModel", "UnitColorBrush", false);
        public static readonly MemberSpec VisualsResolve = MemberSpec.Method(Cap, "SeapowerUI.MapContactVisuals", "Resolve", new[] { "SeapowerUI.IUnit", "SeapowerUI.MapViewModel" }, null, false, true).WithAltParams(new[] { "SeapowerUI.IUnit" });
        public static readonly MemberSpec VehIsDormant = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "IsDormant", false);
        public static readonly MemberSpec VehUnitTaskforce = MemberSpec.Prop(Cap, "SeaPower.Vehicle", "UnitTaskforce", false);
        public static readonly MemberSpec IconThickness = MemberSpec.Method(Cap, "SeaPower.UI_Parameters", "TryMapValue", new[] { "string", "bool", "float&" }, "bool", false, true);   // T-310: no ReportMissing -> no error window at game start
        public static readonly MemberSpec ObjClassNameShort = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "ClassNameShort", false);   // T-080
        public static readonly MemberSpec ObjIniName = MemberSpec.Method(Cap, "SeaPower.ObjectBase", "getObjectIniName", new string[0], "string", false);

        // ---------- centre the map (overview_center) ----------
        public static readonly MemberSpec MainVm = MemberSpec.Field("overview_center", "SeaPower.Globals", "_mainGameViewModel", true, true);
        public static readonly MemberSpec MainVmMap = MemberSpec.Prop("overview_center", "SeapowerUI.ViewModels.MainGameViewModel", "Map");
        public static readonly MemberSpec MainVmSecondMap = MemberSpec.Prop("overview_center", "SeapowerUI.ViewModels.MainGameViewModel", "SecondMap");
        public static readonly MemberSpec MainVmMapVisible = MemberSpec.Prop("overview_center", "SeapowerUI.ViewModels.MainGameViewModel", "MapVisible");
        public static readonly MemberSpec SceneDisplayMap = MemberSpec.Prop("overview_center", "SeapowerUI.ViewModels.SceneMapViewModel", "DisplayMap");
        public static readonly MemberSpec MapCenter = MemberSpec.Prop("overview_center", "SeapowerUI.MapViewModel", "Center");

        // ---------- select a contact (overview_select is derived: a writable Selected, or the map's SelectedObject) ----------
        public static readonly MemberSpec SceneSelectedObject = MemberSpec.Field("overview_select_aux", "SeapowerUI.ViewModels.SceneMapViewModel", "SelectedObject", false);

        // ---------- mouse block ----------
        public static readonly MemberSpec MouseOverUi = MemberSpec.Method("overview_block", "SeaPower.MouseControlState", "setMouseIsOverUIWindow", new[] { "bool" }, "void", false);

        // ---------- the game's key bindings (hotkey conflict check; the capability overview_hotkey itself is set by the hotkey binding at run time) ----------
        public static readonly MemberSpec KeyBindings = MemberSpec.Prop("overview_hotkey_aux", "SeaPower.InputHandler", "KeyBindings", false);
        public static readonly MemberSpec BindingKeyCodes = MemberSpec.Field("overview_hotkey_aux", "SeaPower.KeyBinding", "keyCodes", false);
        public static readonly MemberSpec BindingAction = MemberSpec.Prop("overview_hotkey_aux", "SeaPower.KeyBinding", "KeyAction", false);
        public static readonly MemberSpec BindingKeyName = MemberSpec.Field("overview_hotkey_aux", "SeaPower.KeyBinding", "_keyName", false);

        // ---------- read, never gated ----------
        public static readonly MemberSpec ObjIsPlayer = MemberSpec.Prop(Aux, "SeaPower.ObjectBase", "IsPlayerObject", false);
        public static readonly MemberSpec ObjName = MemberSpec.Prop(Aux, "SeaPower.ObjectBase", "Name", false);
        public static readonly MemberSpec Vec3X = MemberSpec.Field(Aux, "UnityEngine.Vector3", "x", false);
        public static readonly MemberSpec Vec3Z = MemberSpec.Field(Aux, "UnityEngine.Vector3", "z", false);
        public static readonly MemberSpec SceneKnownUnits = MemberSpec.Prop(Aux, "SeapowerUI.ViewModels.SceneMapViewModel", "MapKnownUnits", false);
        public static readonly MemberSpec KnownAllUnits = MemberSpec.Prop(Aux, "SeapowerUI.MapKnownUnits", "AllUnits", false);
        public static readonly MemberSpec MainVmHudVisible = MemberSpec.Method(Aux, "SeapowerUI.ViewModels.MainGameViewModel", "GetHudVisible", new string[0], "bool", false, true);
        public static readonly MemberSpec InputMenuOpen = MemberSpec.Field(Aux, "SeaPower.InputHandler", "menuOpen", false);
        public static readonly MemberSpec InputMessagePause = MemberSpec.Prop(Aux, "SeaPower.InputHandler", "IsInMessagePauseMode", false);
        public static readonly MemberSpec RenderSelected = MemberSpec.Method(Aux, "SeaPower.RenderPosition", "getSelectedObject", new string[0], "SeaPower.ObjectBase", false);

        /// <summary>Capabilities with a rule beyond "all required members": overview needs the contact list, centre/select need overview, a writable centre of the right type, a selection path.</summary>
        public static void Derive(GameApi api, ISet<string> caps)
        {
            bool ov = caps.Contains("overview") && api.Capabilities.Contains("contacts");
            if (!ov) caps.Remove("overview");
            var centre = api.Member(MapCenter) as PropertyInfo;
            bool geoType = centre != null && centre.CanWrite && centre.PropertyType == api.TypeOf(CoreSpecs.GeoType);
            api.Checks.Add(new GameApi.Check { Capability = "overview_center", Member = "SeapowerUI.MapViewModel.Center is a writable SeaPower.GeoPosition", Required = true, Present = geoType });
            if (!ov || !geoType) caps.Remove("overview_center");
            var sel = api.Member(UnitSelected) as PropertyInfo;
            bool selSet = sel != null && sel.CanWrite;
            api.Checks.Add(new GameApi.Check { Capability = "overview_select", Member = "SeapowerUI.MapUnitViewModel.Selected has a setter", Required = false, Present = selSet });
            if (ov && (selSet || api.Present(SceneSelectedObject))) caps.Add("overview_select");
        }

        public static readonly SpecSet Set = SpecSet.Of("overview", typeof(OverviewSpecs));
    }
}
