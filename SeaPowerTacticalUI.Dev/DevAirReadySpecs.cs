namespace VoiceBotBridge.Dev
{
    /// <summary>
    /// QA cheat air_ready (dev mod only, never in the Workshop package): the game members it touches, in the T-120 registry format (ADR-0043).
    /// All optional: a missing member only makes air_ready answer "unsupported"; selfcheck ui prints them as "dev_air_ready ..." and never fails on them.
    /// The same members as AirOpsSpecs / CoreSpecs of the public DLL (the dev DLL does not reference it, ADR-0046).
    /// </summary>
    internal static class DevAirReadySpecs
    {
        private const string Cap = "dev_air_ready";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Tf = "SeaPower.Taskforce";
        private const string Task = "SeaPower.FlightDeckTask";

        // own side: SeaPower.Globals._playerTaskforce (as GameApi.FindOwnUnit)
        public static readonly MemberSpec PlayerTaskforce = MemberSpec.Field(Cap, "SeaPower.Globals", "_playerTaskforce", false, true);
        public static readonly MemberSpec TfVessels = MemberSpec.Field(Cap, Tf, "_taskforceVessels", false);
        public static readonly MemberSpec ObjUniqueId = MemberSpec.Prop(Cap, Ob, "UniqueID", false);
        public static readonly MemberSpec ObjObp = MemberSpec.Field(Cap, Ob, "_obp", false);
        public static readonly MemberSpec ObpFlightDeck = MemberSpec.Field(Cap, "SeaPower.ObjectBaseParameters", "_flightDeck", false);

        // the deck and its tasks (PendingLaunchTask: ready-up, CoolDownTask: turnaround after landing)
        public static readonly MemberSpec DeckTasks = MemberSpec.Prop(Cap, "SeaPower.FlightDeck", "FlightDeckTasks", false);
        public static readonly MemberSpec TaskUid = MemberSpec.Field(Cap, Task, "_uid", false);
        public static readonly MemberSpec TaskDuration = MemberSpec.Field(Cap, Task, "_duration", false);   // 0.8.7: the rest of the task, HandleReadyUpTask subtracts dt*multiplier*crew
        public static readonly MemberSpec TaskVehicleType = MemberSpec.Prop(Cap, Task, "VehicleType", false);
        public static readonly MemberSpec TaskStateMachine = MemberSpec.Field(Cap, Task, "_stateMachine", false);
        public static readonly MemberSpec MachineState = MemberSpec.Prop(Cap, "SeaPower.StateMachine", "CurrentState", false);
        public static readonly MemberSpec VtFileName = MemberSpec.Field(Cap, "SeaPower.VehicleTypeOnBoard", "_fileName", false);

        /// <summary>Members without which nothing can be done (the rest only enrich the answer).</summary>
        public static readonly MemberSpec[] Core = { PlayerTaskforce, TfVessels, ObjUniqueId, ObjObp, ObpFlightDeck, DeckTasks, TaskUid, TaskDuration };

        public static readonly MemberSpec[] All =
        {
            PlayerTaskforce, TfVessels, ObjUniqueId, ObjObp, ObpFlightDeck, DeckTasks, TaskUid, TaskDuration, TaskVehicleType, TaskStateMachine, MachineState, VtFileName,
        };
    }
}
