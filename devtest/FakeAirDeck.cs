using System;
using System.Collections.Generic;

// air_ready (QA cheat): the smallest fake of the game's deck types, found by DevAirReady through AppDomain type lookup like the real ones.
#pragma warning disable 0169, 0414, 0649
namespace SeaPower
{
    internal sealed class Globals { private static Taskforce _playerTaskforce; public static void Set(Taskforce tf) { _playerTaskforce = tf; } }
    internal sealed class Taskforce { public List<ObjectBase> _taskforceVessels = new List<ObjectBase>(); }
    internal sealed class ObjectBase { public int UniqueID { get; set; } public ObjectBaseParameters _obp = new ObjectBaseParameters(); }
    internal sealed class ObjectBaseParameters { public FlightDeck _flightDeck; }
    internal sealed class FlightDeck { public List<FlightDeckTask> FlightDeckTasks { get; } = new List<FlightDeckTask>(); }
    internal sealed class VehicleTypeOnBoard { public string _fileName; }
    internal sealed class HandleReadyUpTask { }
    internal sealed class HandleAwaitGroundCrewTask { }
    internal sealed class StateMachine { public object CurrentState { get; set; } }
    internal class FlightDeckTask
    {
        public Guid _uid = Guid.NewGuid();
        public float _duration;
        public StateMachine _stateMachine = new StateMachine();
        public VehicleTypeOnBoard VehicleType { get; set; }
    }
    internal sealed class PendingLaunchTask : FlightDeckTask { }
    internal sealed class CoolDownTask : FlightDeckTask { }
    internal sealed class LaunchTask : FlightDeckTask { }
}
#pragma warning restore 0169, 0414, 0649
