using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): the game members of the radial menu (module radial: RadialGame.cs, Radial.cs). radial_menu = the ring can block the game's mouse
    /// handling and its layout parsed; radial_lmb = left-button opening (what the cursor is over, hit test, camera); radial_map = the ring on the
    /// tactical map (the icon under the cursor, the unit's point on the map). The Harmony targets of the ring are PatchSpecs (capability radial_map).
    /// </summary>
    internal static class RadialSpecs
    {
        private const string Menu = "radial_menu";
        private const string Lmb = "radial_lmb";
        private const string Map = "radial_map";

        public static readonly MemberSpec MouseOverContextMenu = MemberSpec.Method(Menu, "SeaPower.MouseControlState", "setMouseIsOverContextMenu", new[] { "bool" }, "void");

        public static readonly MemberSpec HudVisible = MemberSpec.Method(Lmb, "SeapowerUI.ViewModels.MainGameViewModel", "GetHudVisible", new string[0], "bool", true, true);
        public static readonly MemberSpec CameraManagerType = MemberSpec.TypeOf(Lmb, "SeaPower.CameraManager", false);
        public static readonly MemberSpec MouseOnWorldDisplay = MemberSpec.Prop(Lmb, "SeaPower.CameraManager", "MouseOnWorldDisplay");
        public static readonly MemberSpec MenuOpen = MemberSpec.Field(Lmb, "SeaPower.InputHandler", "menuOpen");
        public static readonly MemberSpec TypingActive = MemberSpec.Field(Lmb, "SeaPower.InputHandler", "typingActive");
        public static readonly MemberSpec MessagePause = MemberSpec.Prop(Lmb, "SeaPower.InputHandler", "IsInMessagePauseMode");
        public static readonly MemberSpec ContextMenuCanvas = MemberSpec.Field(Lmb, "SeapowerUI.MainGameView", "ContextMenuCanvas");
        public static readonly MemberSpec MainViewInstance = MemberSpec.Field(Lmb, "SeapowerUI.MainGameView", "Instance", true, true);
        public static readonly MemberSpec PipMouseOver = MemberSpec.Prop(Lmb, "SeapowerUI.Pip3DView", "MouseOver", true, true);
        public static readonly MemberSpec RenderSelected = MemberSpec.Method(Lmb, "SeaPower.RenderPosition", "getSelectedObject", new string[0], "SeaPower.ObjectBase");
        public static readonly MemberSpec UiScale = MemberSpec.Prop(Lmb, "SeaPower.OptionsManager", "UIScale");
        public static readonly MemberSpec CameraMain = MemberSpec.Prop(Lmb, "SeaPower.CameraManager", "Main", true, true);
        public static readonly MemberSpec UnityPosition = MemberSpec.Method(Lmb, "SeaPower.ObjectBase", "getUnityPosition", new string[0], "UnityEngine.Vector3", false);
        public static readonly MemberSpec TreeType = MemberSpec.TypeOf(Lmb, "Noesis.VisualTreeHelper", false);
        public static readonly MemberSpec HitTest = MemberSpec.Method(Lmb, "Noesis.VisualTreeHelper", "HitTest", new[] { "Noesis.Visual", "Noesis.Point" }, "Noesis.HitTestResult", true, true);
        public static readonly MemberSpec GetParent = MemberSpec.Method(Lmb, "Noesis.VisualTreeHelper", "GetParent", new[] { "Noesis.DependencyObject" }, "Noesis.DependencyObject", true, true);
        public static readonly MemberSpec PipType = MemberSpec.TypeOf(Lmb, "SeapowerUI.Pip3DView", false);
        public static readonly MemberSpec ContextMenu = MemberSpec.Prop(Lmb, "Noesis.FrameworkElement", "ContextMenu", false);
        public static readonly MemberSpec MenuIsOpen = MemberSpec.Prop(Lmb, "Noesis.ContextMenu", "IsOpen", false);
        public static readonly MemberSpec HitVisual = MemberSpec.Prop(Lmb, "Noesis.HitTestResult", "VisualHit", false);

        public static readonly MemberSpec WindowType = MemberSpec.TypeOf(Lmb, "SeapowerUI.Window", false);
        public static readonly MemberSpec MapUnitVmType = MemberSpec.TypeOf(Map, "SeapowerUI.MapUnitViewModel", false);
        public static readonly MemberSpec MapBaseType = MemberSpec.TypeOf(Map, "MapControl.MapBase");
        public static readonly MemberSpec UnitOfViewModel = MemberSpec.Prop(Map, "SeapowerUI.MapUnitViewModel", "Unit");
        public static readonly MemberSpec VehicleBase = MemberSpec.Prop(Map, "SeaPower.Vehicle", "BaseObject");
        public static readonly MemberSpec LocationToView = MemberSpec.Method(Map, "MapControl.MapBase", "LocationToView", new[] { "SeaPower.GeoPosition" }, "Noesis.Point");
        public static readonly MemberSpec ObjPosition = MemberSpec.Prop(Map, "SeaPower.ObjectBase", "Position");

        // Harmony targets of the ring (behind Radial.InputPatches, default false): keep the left button hold of the open ring away from the camera and
        // the map. radial_camera is optional (a missing target only logs); the three map patches are all-or-nothing and carry the capability radial_map.
        public static readonly PatchSpec PatchCamera = new PatchSpec("radial_camera", Map, "SeaPower.CameraBase", "handleRotation", new string[0], "void", PatchKind.Prefix, "radial_input", false);
        public static readonly PatchSpec PatchMapPan = new PatchSpec("radial_map_pan", Map, "SeapowerUI.SceneEdit.Behaviours.MapDragger", "MapDrag", new[] { "object", "Noesis.DragDeltaEventArgs" }, "void", PatchKind.Prefix, "radial_input");
        public static readonly PatchSpec PatchMapDrag = new PatchSpec("radial_map_drag", Map, "SeapowerUI.SceneEdit.Behaviours.MapDraggableObject", "ObjectDragged", new[] { "object", "Noesis.MouseEventArgs" }, "void", PatchKind.Prefix, "radial_input");
        public static readonly PatchSpec PatchMapClick = new PatchSpec("radial_map_click", Map, "SeapowerUI.ViewModels.InteractionStateBroker", "MouseEvent", new[] { "object", "Noesis.MouseEventArgs" }, "void", PatchKind.Prefix, "radial_input");

        /// <summary>RoutedEventArgs.Handled: the prefixes mark the mouse event handled (radial ring and smart pick).</summary>
        public static readonly MemberSpec EventHandled = MemberSpec.Prop(Map, "Noesis.RoutedEventArgs", "Handled", false);

        // duck members: the parents up the visual tree are any DependencyObject; the Point type is taken from the parameter of PointToScreen
        public static readonly MemberSpec DataContext = MemberSpec.DuckMember(Lmb, "DataContext", MemberKind.Property, null, "asked for every visual up the tree; most are no FrameworkElement");
        public static readonly MemberSpec PointX = MemberSpec.DuckMember(Map, "X", MemberKind.Property, null, "the Point type is taken from the parameter of PointToScreen");
        public static readonly MemberSpec PointY = MemberSpec.DuckMember(Map, "Y", MemberKind.Property, null, "the Point type is taken from the parameter of PointToScreen");

        /// <summary>radial_menu also needs the parsed ring layout; lmb needs the menu, map needs lmb.</summary>
        public static void Derive(GameApi api, ISet<string> caps)
        {
            bool menu = caps.Contains("radial_menu") && api.RadialLayoutReady();
            if (!menu) caps.Remove("radial_menu");
            if (!menu) caps.Remove("radial_lmb");
            if (!caps.Contains("radial_lmb")) caps.Remove("radial_map");
        }

        public static readonly SpecSet Set = SpecSet.Of("radial", typeof(RadialSpecs));
    }
}
