namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M11/M12 (ADR-0043): the game members that the aviation family shares (SAR, ASW, CAP, the deck tick): the unit parameters, the waypoint
    /// and telegraph orders, the patrol zone, the flight deck and its tasks, the loadouts. Capability air_ops: one missing = every function that
    /// needs aircraft or decks is off (rows sar, sar_air, asw, cap, air_deck of UiDegradation). deck_prioritise is the optional deck lever (T-119, ADR-0042).
    /// </summary>
    internal static class AirOpsSpecs
    {
        private const string Cap = "air_ops";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Obp = "SeaPower.ObjectBaseParameters";

        // ---- the unit and its parameters ----
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Cap, Ob, "_obp");
        public static readonly MemberSpec ObpIniName = MemberSpec.Field(Cap, Obp, "_objectIniName");
        public static readonly MemberSpec ObpTransportCapacity = MemberSpec.Field(Cap, Obp, "_transportCapacity");
        public static readonly MemberSpec ObpTransported = MemberSpec.Prop(Cap, Obp, "CurrentTransportedOnBoard");   // ReactiveProperty<int>
        public static readonly MemberSpec ObjIsDestroyed = MemberSpec.Prop(Cap, Ob, "IsDestroyed");
        public static readonly MemberSpec ObjVelocityKn = MemberSpec.Method(Cap, Ob, "getVelocityInKnots", new string[0], "float");
        public static readonly MemberSpec ObjHeightFt = MemberSpec.Method(Cap, Ob, "getHeightInFeet", new string[0], "float");
        public static readonly MemberSpec ObjSetTelegraph = MemberSpec.Method(Cap, Ob, "setTelegraph", new[] { "int" }, "void");
        public static readonly MemberSpec ObjRemoveWaypoints = MemberSpec.Method(Cap, Ob, "RemoveWaypoints", new string[0], "void");
        public static readonly MemberSpec ObjSetWaypoint = MemberSpec.Method(Cap, Ob, "setWaypointTask", new[] { "GeoPosition", "string", "WaypointHeightState" }, "GoToWaypointTask");
        public static readonly MemberSpec ObjCommandPosition = MemberSpec.Prop(Cap, Ob, "CommandPosition");   // Nullable<GeoPosition>
        public static readonly MemberSpec ObjPatrolZone = MemberSpec.Field(Cap, Ob, "_patrolZone");
        public static readonly MemberSpec PzZoneType = MemberSpec.Field(Cap, "SeaPower.PatrolZone", "_zoneType");

        // ---- optional reads shared by ASW and CAP (a missing one is logged once; the reader then keeps its default) ----
        public static readonly MemberSpec ObjHomeBase = MemberSpec.Field(Cap, Ob, "_homeBase", false);
        public static readonly MemberSpec ObjClassName = MemberSpec.Prop(Cap, Ob, "ClassName", false);
        public static readonly MemberSpec ObjHeading = MemberSpec.Prop(Cap, Ob, "Heading", false);   // ReactiveProperty<float>
        public static readonly MemberSpec ObjDesiredAltitude = MemberSpec.Prop(Cap, Ob, "DesiredAltitude", false);   // ReactiveProperty<float>
        public static readonly MemberSpec ObjRangeKm = MemberSpec.Prop(Cap, Ob, "RangeInKm", false);   // ReactiveProperty<float>
        public static readonly MemberSpec ObpLoadoutVariant = MemberSpec.Field(Cap, Obp, "_loadoutVariant", false);
        public static readonly MemberSpec ObpSerial = MemberSpec.Field(Cap, Obp, "_objectSerialNumber", false);
        public static readonly MemberSpec ObpMaxRangeKm = MemberSpec.Field(Cap, Obp, "_maxRangeInKm", false);
        public static readonly MemberSpec ObpSensorSystems = MemberSpec.Field(Cap, Obp, "_sensorSystems", false);
        public static readonly MemberSpec AircraftEndurance = MemberSpec.Prop(Cap, "SeaPower.Aircraft", "EnduranceInSec", false);   // ReactiveProperty<float>
        public static readonly MemberSpec VtType = MemberSpec.Field(Cap, "SeaPower.VehicleTypeOnBoard", "_type", false);
        public static readonly MemberSpec VtAswCapability = MemberSpec.Field(Cap, "SeaPower.VehicleTypeOnBoard", "_aswCapability", false);
        public static readonly MemberSpec VtDisplayName = MemberSpec.Prop(Cap, "SeaPower.VehicleTypeOnBoard", "DisplayName", false);
        public static readonly MemberSpec VtNumbers = MemberSpec.Prop(Cap, "SeaPower.VehicleTypeOnBoard", "Numbers", false);
        public static readonly MemberSpec DeckCanOperateHelo = MemberSpec.Method(Cap, "SeaPower.FlightDeck", "CanOperateHelicopterInCurrentSeaState", new[] { "VehicleTypeOnBoard" }, "bool", false);
        public static readonly MemberSpec LoServiceYear = MemberSpec.Field(Cap, "SeaPower.Loadout", "ServiceEntryYear", false);
        public static readonly MemberSpec BombLaunchPlatform = MemberSpec.Field(Cap, "SeaPower.WeaponBase", "_launchPlatform", false);

        // ---- the flight deck (the ship's _flightDeck itself is read by Common: capability aviation) ----
        private const string Fd = "SeaPower.FlightDeck";
        private const string Vt = "SeaPower.VehicleTypeOnBoard";
        private const string Task = "SeaPower.FlightDeckTask";
        private const string Pending = "SeaPower.PendingLaunchTask";
        private const string Lo = "SeaPower.Loadout";

        public static readonly MemberSpec DeckCreateLaunch = MemberSpec.Method(Cap, Fd, "createLaunchTask", new[] { "VehicleTypeOnBoard", "Loadout", "Squadron", "string", "LaunchTaskParameters", "bool", "float" }, "PendingLaunchTask");
        public static readonly MemberSpec DeckAbortLaunch = MemberSpec.Method(Cap, Fd, "abortLaunchTask", new[] { "Guid", "bool" }, "void").WithAltParams(new[] { "Guid" });   // 0.8.7: (Guid, bool delayed = false); 0.8.6: (Guid). delayed=false = the old immediate removal
        public static readonly MemberSpec DeckCanLaunch = MemberSpec.Method(Cap, Fd, "CanLaunchVehicle", new[] { "Loadout", "int" }, "bool");
        public static readonly MemberSpec DeckTasks = MemberSpec.Prop(Cap, Fd, "FlightDeckTasks");
        public static readonly MemberSpec DeckVehicles = MemberSpec.Field(Cap, Fd, "_vehiclesOnBoard");
        // live 0.28.3: an AS-4 hit on Eisenhower -> BaseSystem.MakeInoperable -> FlightDeck.KillAircraft cleared every deck task (no refund) and destroyed the
        // taxiing aircraft; FlightDeck.OnUpdate/OnFixedUpdate then return at once, so a new launch task never leaves its first state until damage control repairs it
        public static readonly MemberSpec DeckInoperable = MemberSpec.Prop(Cap, Fd, "Inoperable", false);   // ReactiveProperty<bool> of BaseSystem
        public static readonly MemberSpec DeckPrioritise = MemberSpec.Field("deck_prioritise", Fd, "_flightDeckTasksToPrioritise", false);   // T-119: the deck broker's lever (ADR-0042)

        public static readonly MemberSpec LtpType = MemberSpec.TypeOf(Cap, "SeaPower.LaunchTaskParameters");
        public static readonly MemberSpec LtpLaunchCount = MemberSpec.Field(Cap, "SeaPower.LaunchTaskParameters", "_launchCount");
        public static readonly MemberSpec LtpMissionType = MemberSpec.Field(Cap, "SeaPower.LaunchTaskParameters", "_missionType");
        public static readonly MemberSpec MissionTypeEnum = MemberSpec.TypeOf(Cap, "SeaPower.FlightDeckTask+MissionType", false);

        public static readonly MemberSpec TaskUid = MemberSpec.Field(Cap, Task, "_uid");
        public static readonly MemberSpec TaskDuration = MemberSpec.Field(Cap, Task, "_duration");
        public static readonly MemberSpec TaskStartTime = MemberSpec.Field(Cap, Task, "_startTime", false);
        public static readonly MemberSpec CoolIniRef = MemberSpec.Field(Cap, "SeaPower.CoolDownTask", "iniRef", false);   // T-204: a CoolDownTask has no VehicleType; its iniRef is the file name of the landed type (as AswSpecs.CoolIniRef)
        public static readonly MemberSpec TaskVehicleType = MemberSpec.Prop(Cap, Task, "VehicleType", false);
        public static readonly MemberSpec TaskVehicle = MemberSpec.Prop(Cap, Task, "Vehicle", false);   // LaunchTask: the spawned aircraft that is taxiing out (0.8.7: spawned when the pending task finishes)
        public static readonly MemberSpec TaskStateMachine = MemberSpec.Field(Cap, Task, "_stateMachine", false);
        public static readonly MemberSpec MachineState = MemberSpec.Prop(Cap, "SeaPower.StateMachine", "CurrentState", false);
        public static readonly MemberSpec PendingCrew = MemberSpec.Prop(Cap, Pending, "AssignedGroundCrew", false);
        public static readonly MemberSpec PendingLoadoutIndex = MemberSpec.Field(Cap, Pending, "_loadoutIndex");
        public static readonly MemberSpec PendingLaunchAllowed = MemberSpec.Field(Cap, Pending, "launchAllowed");
        public static readonly MemberSpec PendingAllowLaunch = MemberSpec.Method(Cap, Pending, "AllowLaunchFunc", new[] { "object" }, "void", false);
        public static readonly MemberSpec PendingToggleLaunch = MemberSpec.Method(Cap, Pending, "ToggleLaunchAllowedFunc", new[] { "object" }, "void", false);

        public static readonly MemberSpec VtFileName = MemberSpec.Field(Cap, Vt, "_fileName", false);
        public static readonly MemberSpec VtLoadouts = MemberSpec.Prop(Cap, Vt, "Loadouts", false);
        public static readonly MemberSpec VtLoadoutsField = MemberSpec.Field(Cap, Vt, "_loadouts", false);
        public static readonly MemberSpec VtSquadrons = MemberSpec.Prop(Cap, Vt, "Squadrons", false);
        public static readonly MemberSpec VtUnitRoles = MemberSpec.Field(Cap, Vt, "_unitRoles", false);
        public static readonly MemberSpec SquadronNumbers = MemberSpec.Prop(Cap, "SeaPower.Squadron", "Numbers", false);
        public static readonly MemberSpec SquadronCallsigns = MemberSpec.Prop(Cap, "SeaPower.Squadron", "Callsigns", false);

        public static readonly MemberSpec LoName = MemberSpec.Prop(Cap, Lo, "Name", false);
        public static readonly MemberSpec LoDisplayName = MemberSpec.Prop(Cap, Lo, "DisplayName", false);
        public static readonly MemberSpec LoReadyUp = MemberSpec.Field(Cap, Lo, "_readyUpDuration");
        public static readonly MemberSpec LoCoolDown = MemberSpec.Field(Cap, Lo, "_coolDownTime");
        public static readonly MemberSpec LoAmmoAmounts = MemberSpec.Prop(Cap, Lo, "AmmunitionAmounts", false);   // Dictionary<string,int>

        public static readonly MemberSpec SarCapacity = MemberSpec.Method(Cap, "SeaPower.PatrolZone", "GetSARTransportCapacity", new[] { "Loadout" }, "int", true, true);

        public static readonly MemberSpec OptionsType = MemberSpec.TypeOf(Cap, "SeaPower.OptionsManager");
        public static readonly MemberSpec DeckTimingsMode = MemberSpec.Prop(Cap, "SeaPower.OptionsManager", "FlightDeckTimingsMode");

        public static readonly SpecSet Set = SpecSet.Of("air_tick", typeof(AirOpsSpecs));
    }
}
