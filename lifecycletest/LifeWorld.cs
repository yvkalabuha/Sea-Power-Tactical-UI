using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Stand-in for the ObjectsManager of one mission plus the game objects (DTOs) the fakes hand to the controllers.</summary>
    internal sealed class LifeWorld
    {
        private static int _ids;
        public readonly int Id = ++_ids;
        public string Name;
        public string SavePath;                                   // the .sav the mission is loaded from (null = new mission)
        public double MissionTimeS;
        public bool HasSmart;                                     // the smart attack fake is advanced only for a mission that uses it
        public List<AswMember> Asw = new List<AswMember>();
        public List<FakeBird> Cap = new List<FakeBird>();
        public Dictionary<int, SarUnit> Sar = new Dictionary<int, SarUnit>();
        public List<SarRaft> Rafts = new List<SarRaft>();
        public List<object> FlagUnits = new List<object>();       // keys of the doctrine flag store
        public TorpSnap Torp;                                     // the wire torpedo the game brings back with a loaded save
        public byte[] Ballast = new byte[1024];                   // makes the world a real heap object

        public static LifeWorld MakeA(double t, string sav)
        {
            var w = new LifeWorld { Name = "A", MissionTimeS = t, SavePath = sav, HasSmart = true };
            w.Asw.Add(AswFakeGame.Helo(1, "air")); w.Asw.Add(AswFakeGame.Helo(2, "deck_ready"));
            foreach (var b in new CapFakeGame().All) { b.Uid += 10; w.Cap.Add(b); }
            var sw = new SarFakeGame();
            var ship = sw.AddUnit(30, "ship", 0, 0, 3); ship.Name = "Yorktown";
            w.Sar[30] = ship;
            w.Rafts.Add(sw.AddRaft(11, 4, 4, 1));
            w.FlagUnits.Add(new object()); w.FlagUnits.Add(new object());
            return w;
        }

        public static LifeWorld MakeB(double t, string sav)
        {
            var w = new LifeWorld { Name = "B", MissionTimeS = t, SavePath = sav };
            w.Asw.Add(AswFakeGame.Helo(51, "deck_ready"));
            w.FlagUnits.Add(new object());
            return w;
        }
    }
}
