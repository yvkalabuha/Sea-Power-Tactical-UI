using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): game members that several modules of the UI mod share and that belong to no one function: the player's taskforce,
    /// the own-unit lists, the game's text handler, the ammunition table. Not a module: its set is handed to the registry by the first core pump
    /// (DialogsFeature.SelfCheck). Capability "core" is not a player function (no row in UiDegradation); "units" is the same capability as in Common.
    /// A module that needs the same member for a different capability declares its own spec of it.
    /// </summary>
    internal static class CoreSpecs
    {
        public static readonly MemberSpec Globals = MemberSpec.TypeOf("core", "SeaPower.Globals");
        /// <summary>T-125: SeaPower.ObjectsManager - the mission singleton (InMission, MissionRef) for FrameState.</summary>
        public static readonly MemberSpec ObjectsManagerType = MemberSpec.TypeOf("core", "SeaPower.ObjectsManager");
        public static readonly MemberSpec ObjectBaseType = MemberSpec.TypeOf("core", "SeaPower.ObjectBase");
        public static readonly MemberSpec PlayerTaskforce = MemberSpec.Field("core", "SeaPower.Globals", "_playerTaskforce", true, true);
        public static readonly MemberSpec TaskforceVessels = MemberSpec.Field("units", "SeaPower.Taskforce", "_taskforceVessels", false);
        public static readonly MemberSpec TaskforceSubmarines = MemberSpec.Field("units", "SeaPower.Taskforce", "_taskforceSubmarines", false);

        /// <summary>The game's text table (getText); the instance comes from GameApi.Singleton.</summary>
        public static readonly MemberSpec LanguageHandler = MemberSpec.TypeOf("core", "SeaPower.LanguageResourceHandler");

        /// <summary>ObjectBase.AmmunitionAmountDictionary: ReactiveDictionary&lt;string,int&gt; ammunition file -> count.</summary>
        public static readonly MemberSpec AmmoCounts = MemberSpec.Prop("core", "SeaPower.ObjectBase", "AmmunitionAmountDictionary", false);
        public static readonly MemberSpec AmmoByName = MemberSpec.Method("core", "SeaPower.ObjectBase", "getAmmunitionByName", new[] { "string" }, "SeaPower.Ammunition", false);

        // KeyValuePair<K,V> entries of the game's dictionaries: K and V differ per dictionary
        public static readonly MemberSpec PairKey = MemberSpec.DuckMember("core", "Key", MemberKind.Property, null, "KeyValuePair<K,V> of a game dictionary: K and V differ");
        public static readonly MemberSpec PairValue = MemberSpec.DuckMember("core", "Value", MemberKind.Property, null, "KeyValuePair<K,V> of a game dictionary: K and V differ");

        public static readonly MemberSpec ObjName = MemberSpec.Prop("core", "SeaPower.ObjectBase", "Name", false);
        public static readonly MemberSpec ObjIsPlayer = MemberSpec.Prop("core", "SeaPower.ObjectBase", "IsPlayerObject", false);
        public static readonly MemberSpec ObjIsControllable = MemberSpec.Prop("core", "SeaPower.ObjectBase", "IsControllable", false);

        public static readonly MemberSpec ObjFormation = MemberSpec.Prop("core", "SeaPower.ObjectBase", "Formation", false);

        /// <summary>SeaPower.GameTime.TimeCompression (static): the time acceleration (x1, x2, x4 ...).</summary>
        public static readonly MemberSpec TimeCompression = MemberSpec.Prop("core", "SeaPower.GameTime", "TimeCompression", false, true);

        /// <summary>The game's own alert line (the alert list at the top of the screen): used by the smart attack and by the notice about switched-off functions.</summary>
        public static readonly MemberSpec MainViewModel = MemberSpec.Field("core", "SeaPower.Globals", "_mainGameViewModel", false, true);
        public static readonly MemberSpec RaiseAlertText = MemberSpec.Method("core", "SeapowerUI.ViewModels.MainGameViewModel", "RaiseAlert", new[] { "string" }, "void", false);
        public static readonly MemberSpec RaiseAlertFrom = MemberSpec.Method("core", "SeapowerUI.ViewModels.MainGameViewModel", "RaiseAlert", new[] { "ObjectBase", "string" }, "void", false);
        public static readonly MemberSpec GamePaused = MemberSpec.Method("core", "SeaPower.GameTime", "IsPaused", new string[0], "bool", false, true);

        public static readonly MemberSpec VehId = MemberSpec.Prop("core", "SeaPower.Vehicle", "Id", false);
        /// <summary>Vehicle.Class: Nullable&lt;SourcedProperty&lt;string&gt;&gt; (the class of a track as the player knows it); its text is SourcedValue.</summary>
        public static readonly MemberSpec VehClass = MemberSpec.Field("core", "SeaPower.Vehicle", "Class", false);
        public static readonly MemberSpec SourcedValue = MemberSpec.DuckMember("core", "Value", MemberKind.Property, null, "SourcedProperty<T>.Value: T differs per property");
        public static readonly MemberSpec VehObject = MemberSpec.Field("core", "SeaPower.Vehicle", "Object", false);   // leak: the true object behind a track

        // singletons that several modules ask (InputHandler, MouseControlState)
        public static readonly MemberSpec InputType = MemberSpec.TypeOf("core", "SeaPower.InputHandler", false);
        public static readonly MemberSpec MouseStateType = MemberSpec.TypeOf("core", "SeaPower.MouseControlState", false);

        // the game's own menus (Esc pause menu, its Options screen and confirmations, the briefing, the After Action Report): the mod windows hide,
        // the mouse block is lifted and the map render texture pauses while one is open (GameApi.PumpGameMenu). Optional: without them the windows stay as before.
        /// <summary>InputHandler.menuOpen: set by CancelAction (pause menu + Options + confirm), MissionBriefViewModel, MissionManager.ExitToMainMenu (the AAR).</summary>
        public static readonly MemberSpec GameMenuOpen = MemberSpec.Field("core", "SeaPower.InputHandler", "menuOpen", false);
        /// <summary>MenuSystemViewModel.CurrentWindow: the game's menu page (BlankMenuView in play, MissionDebriefView = the After Action Report).</summary>
        public static readonly MemberSpec MenuSystemType = MemberSpec.TypeOf("core", "SeapowerUI.ViewModels.MenuSystemViewModel", false);
        public static readonly MemberSpec MenuCurrentWindow = MemberSpec.Prop("core", "SeapowerUI.ViewModels.MenuSystemViewModel", "CurrentWindow", false);
        public static readonly MemberSpec DebriefViewType = MemberSpec.TypeOf("core", "SeapowerUI.MissionDebriefView", false);
        /// <summary>0.28.1: the mission message popup (Trigger -> RichMissionMessages.CreateMissionPopupModal, pauseOnLoad): InputHandler.IsInMessagePauseMode,
        /// and any SeapowerUI.Modal (RichMissionMessageModal too) in WindowHostManager.CurrentHost.Windows (a popup without the pause).</summary>
        public static readonly MemberSpec GameMessagePause = MemberSpec.Prop("core", "SeaPower.InputHandler", "IsInMessagePauseMode", false);
        public static readonly MemberSpec WindowHostManagerType = MemberSpec.TypeOf("core", "SeapowerUI.WindowHostManager", false);
        public static readonly MemberSpec WindowHostCurrent = MemberSpec.Prop("core", "SeapowerUI.WindowHostManager", "CurrentHost", false);
        public static readonly MemberSpec WindowHostWindows = MemberSpec.Prop("core", "SeapowerUI.WindowHost", "Windows", false);
        public static readonly MemberSpec ModalType = MemberSpec.TypeOf("core", "SeapowerUI.Modal", false);

        // T-199: the loading screen sign. LoadScreen.cameraObject is enabled from the start of ExecuteLoadScreen until "Done"; the percent stays >= 100 on the last steps.
        public static readonly MemberSpec LoadScreenType = MemberSpec.TypeOf("core", "SeaPower.LoadScreen", false);
        public static readonly MemberSpec LoadCamera = MemberSpec.Field("core", "SeaPower.LoadScreen", "cameraObject", false);

        // SeaPower.GeoPosition (struct): the fields the radar, the Overview and others read
        public static readonly MemberSpec GeoType = MemberSpec.TypeOf("core", "SeaPower.GeoPosition", false);
        public static readonly MemberSpec GeoLat = MemberSpec.Field("core", "SeaPower.GeoPosition", "_latitude", false);
        public static readonly MemberSpec GeoLon = MemberSpec.Field("core", "SeaPower.GeoPosition", "_longitude", false);
        public static readonly MemberSpec GeoHeight = MemberSpec.Field("core", "SeaPower.GeoPosition", "_height", false);

        // Noesis geometry that the radar overlay, the radial ring and the smart attack pick share (Noesis is not loaded by the offline selfcheck alone: Coverage.LoadNoesisOffline)
        public static readonly MemberSpec PointToScreen = MemberSpec.Method("core", "Noesis.Visual", "PointToScreen", new[] { "Noesis.Point" }, "Noesis.Point", false);
        public static readonly MemberSpec PointFromScreen = MemberSpec.Method("core", "Noesis.Visual", "PointFromScreen", new[] { "Noesis.Point" }, "Noesis.Point", false);
        public static readonly MemberSpec LocationToView = MemberSpec.Method("core", "MapControl.MapBase", "LocationToView", new[] { "SeaPower.GeoPosition" }, "Noesis.Point", false);

        /// <summary>The own vessel and submarine lists of the player's taskforce (in this order).</summary>
        public static readonly MemberSpec[] OwnShipLists = { TaskforceVessels, TaskforceSubmarines };

        public static readonly SpecSet Set = SpecSet.Of("core", typeof(CoreSpecs));
    }
}
