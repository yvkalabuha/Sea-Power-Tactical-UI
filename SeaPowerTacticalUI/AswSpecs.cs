namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M13 (ADR-0043): the game members of the ASW module (Asw.cs, AswGame.cs): sonobuoys, dipping sonar, sonobuoy depth, the Flight Operations
    /// source of the deck aircraft (asw_deck_ops), the class data for the air-defence reach. The members shared with SAR and CAP are in AirOpsSpecs.
    /// Capability asw_info = sonobuoy and dipping sonar orders; the optional reads (T-142, T-150) never gate it.
    /// </summary>
    internal static class AswSpecs
    {
        private const string Info = "asw_info";
        private const string Deck = "asw_deck_ops";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Ap = "SeaPower.AmmunitionParameters";
        private const string Sonar = "SeaPower.SonarParameters";
        private const string Task = "SeaPower.FlightDeckTask";
        private const string Pending = "SeaPower.PendingLaunchTask";
        private const string Deploy = "SeaPower.Bomb+SonobuoyDeployState";

        // ---- orders on a unit ----
        /// <summary>The 10-argument overload (the other one takes WaypointData).</summary>
        public static readonly MemberSpec AttackAtWaypoint = MemberSpec.Method(Info, Ob, "SetAttackAtWaypointTask", new[] { "string", "ObjectBase", "GeoPosition", "GeoPosition", "int", "VisualActionTask", "SalvoType", "float", "bool", "bool" }, "AttackAtWaypoint");
        public static readonly MemberSpec DipSonarAtWaypoint = MemberSpec.Method(Info, Ob, "SetDipSonarAtWaypointTask", new[] { "GeoPosition", "VisualActionTask" }, "GoToWaypointTask");
        public static readonly MemberSpec SonoDeploy = MemberSpec.Prop(Info, Ob, "SonoDeployState");   // ReactiveProperty<SonobuoyDeployState>
        public static readonly MemberSpec DeployEnum = MemberSpec.TypeOf(Info, Deploy);
        public static readonly MemberSpec DeployAbove = MemberSpec.Field(Info, Deploy, "AboveLayer", true, true);
        public static readonly MemberSpec AmmoAmountByName = MemberSpec.Method(Info, Ob, "getAmmunitionAmountByName", new[] { "string" }, "int");

        /// <summary>A new object means a new mission: the range caches are dropped.</summary>
        public static readonly MemberSpec SonarEnvironment = MemberSpec.Field(Info, "SeaPower.Globals", "_sonarEnvironmentParameters", false, true);

        // ---- ammunition and sensors ----
        public static readonly MemberSpec AmmoParamsOf = MemberSpec.Field(Info, "SeaPower.Ammunition", "_ap", false);
        public static readonly MemberSpec ApSubType = MemberSpec.Field(Info, Ap, "_subType", false);
        public static readonly MemberSpec ApType = MemberSpec.Field(Info, Ap, "_type", false);
        public static readonly MemberSpec ApSonarList = MemberSpec.Field(Info, Ap, "_sonarList");
        public static readonly MemberSpec ApSonarDepth = MemberSpec.Field(Info, Ap, "_sonarDepth", false);   // T-176: SonarDepth(ft) * -0.00453571 (0.8.7 AmmunitionParameters); optional, without it the shallow limit is the config
        public static readonly MemberSpec ApLifeTime = MemberSpec.Field(Info, Ap, "_lifeTime");
        public static readonly MemberSpec ApLaunchAltitudes = MemberSpec.Field(Info, Ap, "_launchAltitudesInUnity", false);   // UnityEngine.Vector2
        public static readonly MemberSpec ApTargetType = MemberSpec.Field(Info, Ap, "_targetType", false);
        public static readonly MemberSpec ApMaxLaunchRange = MemberSpec.Field(Info, Ap, "_maxLaunchRangeInMiles", false);
        public static readonly MemberSpec ApGetShared = MemberSpec.Method(Info, Ap, "GetShared", new[] { "string" }, "AmmunitionParameters", false, true);
        public static readonly MemberSpec Vec2X = MemberSpec.DuckMember(Info, "x", MemberKind.Field, null, "UnityEngine.Vector2 component (a Unity struct, not loaded by the offline selfcheck)");
        public static readonly MemberSpec Vec2Y = MemberSpec.DuckMember(Info, "y", MemberKind.Field, null, "UnityEngine.Vector2 component (a Unity struct, not loaded by the offline selfcheck)");

        public static readonly MemberSpec ScorerEstimate = MemberSpec.Method(Info, "SeaPower.PointValueSensorScorer", "EstimatePassiveSonarRangeNmi", new[] { "string", "SensorData", "float", "bool" }, "float", true, true);
        public static readonly MemberSpec SonarCtor = MemberSpec.Ctor(Info, Sonar, new[] { "string" }, false);
        public static readonly MemberSpec SonarPassiveKm = MemberSpec.Field(Info, Sonar, "_passiveRangeInKm");
        public static readonly MemberSpec SonarActiveKm = MemberSpec.Field(Info, Sonar, "_activeRangeInKm", false);
        public static readonly MemberSpec SonarZones = MemberSpec.Field(Info, Sonar, "_maxConvergenceZones", false);
        public static readonly MemberSpec SensorDipping = MemberSpec.Field(Info, "SeaPower.SensorSystem", "_isDippingSonar");

        /// <summary>The sonar name (T-159): BaseSystem._systemName, a public field (SensorSystem has no _name / Name in 0.8.6).</summary>
        public static readonly MemberSpec SensorSystemName = MemberSpec.Field(Info, "SeaPower.BaseSystem", "_systemName", false);

        // ---- T-150: serials, the queue of a unit, the class registry for the air-defence reach (all optional) ----
        public static readonly MemberSpec CoolIniRef = MemberSpec.Field(Info, "SeaPower.CoolDownTask", "iniRef", false);
        public static readonly MemberSpec CoolSerial = MemberSpec.Field(Info, "SeaPower.CoolDownTask", "serialNumber", false);
        public static readonly MemberSpec ObjUserRoot = MemberSpec.Field(Info, Ob, "_userRoot", false);
        public static readonly MemberSpec UserRootTasks = MemberSpec.Field(Info, "SeaPower.UserRootNode", "TaskViewModels", false);
        public static readonly MemberSpec MetaType = MemberSpec.TypeOf(Info, "SeaPower.UnitMetadataProvider", false);
        public static readonly MemberSpec MetaUnitData = MemberSpec.Prop(Info, "SeaPower.UnitMetadataProvider", "UnitData", false);
        public static readonly MemberSpec MetaAmmoData = MemberSpec.Prop(Info, "SeaPower.UnitMetadataProvider", "AmmunitionData", false);
        public static readonly MemberSpec LoAccountable = MemberSpec.Field(Info, "SeaPower.Loadout", "_accountableAmmunition", false);
        public static readonly MemberSpec SopLoadouts = MemberSpec.Field(Info, "SeaPower.SimpleObjectParameters", "_loadouts", false);
        public static readonly MemberSpec TupleItem2 = MemberSpec.DuckMember(Info, "Item2", MemberKind.Field, null, "ValueTuple<string,string> / ValueTuple<..., SimpleObjectParameters> of the metadata dictionary: the generic arguments differ");
        public static readonly MemberSpec SimpleTargetType = MemberSpec.Field(Info, "SeaPower.AmmunitionSimpleParameters", "_targetType", false);

        // ---- T-172 (optional): the life of a buoy in the water, the MAD deployment altitude ----
        /// <summary>Seconds of life left (ReactiveProperty&lt;float&gt;); -1 until the buoy has landed on the water, 0 = dead.</summary>
        public static readonly MemberSpec BombRemainingLife = MemberSpec.Prop(Info, "SeaPower.Bomb", "RemainingLife", false);
        /// <summary>Highest altitude (game units; ft x 0.00453571) at which the MAD works (SensorSystemMAD.cs:43).</summary>
        public static readonly MemberSpec MadMaxDeployAlt = MemberSpec.Field(Info, "SeaPower.SensorSystemMAD", "_maxDeployAltitude", false);

        // ---- T-188 (optional): does the type carry a dipping sonar the game really loads (ini of the type, sensors.ini type of the sonar) ----
        public static readonly MemberSpec MetaFindCached = MemberSpec.Method(Info, "SeaPower.UnitMetadataProvider", "FindCached", new[] { "string", "string" }, "SimpleObjectParameters", false, true);
        public static readonly MemberSpec SopLoadSimple = MemberSpec.Method(Info, "SeaPower.SimpleObjectParameters", "LoadSimpleParameters", new[] { "string", "string", "int" }, "SimpleObjectParameters", false, true);
        public static readonly MemberSpec SopIni = MemberSpec.Field(Info, "SeaPower.SimpleObjectParameters", "_ini", false);
        public static readonly MemberSpec IniReadStr = MemberSpec.Method(Info, "SeaPower.IniHandler", "readValue", new[] { "string", "string", "string", "bool" }, "string", false);
        public static readonly MemberSpec IniHasSection = MemberSpec.Method(Info, "SeaPower.IniHandler", "doesSectionExist", new[] { "string" }, "bool", false);
        public static readonly MemberSpec SonarTypeField = MemberSpec.Field(Info, Sonar, "_type", false);

        public static readonly MemberSpec FormationName = MemberSpec.Prop(Info, "SeaPower.UnitFormation", "Name", false);

        // ---- the Flight Operations source of the deck aircraft (T-107) ----
        public static readonly MemberSpec TaskAircraftType = MemberSpec.Prop(Deck, Task, "AircraftType");
        public static readonly MemberSpec TaskSquadron = MemberSpec.Prop(Deck, Task, "Squadron");
        public static readonly MemberSpec TaskLabel = MemberSpec.Prop(Deck, Task, "FlightDeckTaskLabel");
        public static readonly MemberSpec TaskVehicleType = MemberSpec.Prop(Deck, Task, "VehicleType");
        public static readonly MemberSpec TaskVehicle = MemberSpec.Prop(Deck, Task, "Vehicle");
        public static readonly MemberSpec TaskUid = MemberSpec.Field(Deck, Task, "_uid");
        public static readonly MemberSpec TaskStartTime = MemberSpec.Field(Deck, Task, "_startTime");
        public static readonly MemberSpec TaskDuration = MemberSpec.Field(Deck, Task, "_duration");
        public static readonly MemberSpec TaskStateMachine = MemberSpec.Field(Deck, Task, "_stateMachine", false);   // optional: without it the toggle path is used
        /// <summary>Remaining flight time of a helicopter (s, at the current consumption): Helicopter is not an Aircraft, AirOpsSpecs.AircraftEndurance never reads it (live 0.8.7: "81% · 0 min").</summary>
        public static readonly MemberSpec HeliEndurance = MemberSpec.Prop(Deck, "SeaPower.Helicopter", "EnduranceInSec", false);   // ReactiveProperty<float>
        public static readonly MemberSpec TaskLtp = MemberSpec.Field(Deck, Task, "_ltp", false);   // optional: a LaunchTask keeps the LaunchTaskParameters object of the PendingLaunchTask it was spawned from (adoption link)
        public static readonly MemberSpec PendingLoadoutIndex = MemberSpec.Field(Deck, Pending, "_loadoutIndex");
        public static readonly MemberSpec PendingCrew = MemberSpec.Prop(Deck, Pending, "AssignedGroundCrew", false);
        public static readonly MemberSpec PendingLaunchCount = MemberSpec.Prop(Deck, Pending, "LaunchCount");
        public static readonly MemberSpec PendingLaunchAllowed = MemberSpec.Field(Deck, Pending, "launchAllowed");
        public static readonly MemberSpec PendingAllowLaunch = MemberSpec.Method(Deck, Pending, "AllowLaunchFunc", new[] { "object" }, "void");
        public static readonly MemberSpec PendingToggleLaunch = MemberSpec.Method(Deck, Pending, "ToggleLaunchAllowedFunc", new[] { "object" }, "void");
        public static readonly MemberSpec DeckTasks = MemberSpec.Prop(Deck, "SeaPower.FlightDeck", "FlightDeckTasks");
        public static readonly MemberSpec DeckVehicles = MemberSpec.Field(Deck, "SeaPower.FlightDeck", "_vehiclesOnBoard");

        // the player took the unit over: the task list is ObjectBase._userRoot.TaskViewModels (ObjUserRoot, UserRootTasks above, T-159); each item has a public Task
        public static readonly MemberSpec TaskVmTask = MemberSpec.DuckMember(Info, "Task", MemberKind.Property, null, "ITaskViewModel.Task (WaypointViewModel): the item type is an interface implemented by several view models");

        public static readonly SpecSet Set = SpecSet.Of("asw", typeof(AswSpecs));
    }
}
