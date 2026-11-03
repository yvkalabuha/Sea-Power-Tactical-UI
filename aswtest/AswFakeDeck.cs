using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-150 A1: the flight deck of one ship as the game keeps it (order of events as in 0.19.1):
    /// hangar {type: Numbers} -> createLaunchTask (-1 machine, new task:) -> prep -> await -> spawn (one frame with no row at all)
    /// -> unit: row "launching" -> air (same uid) -> low fuel -> unit: row "recovering" (same uid) -> cool: row (same serial) -> hangar +1.
    /// </summary>
    internal sealed class AswFakeDeck
    {
        internal sealed class Hang { public string Type, Label, DipNote; public int Count; public bool? TypeDipping; }
        internal sealed class DeckTask { public int N; public string Type, Label; public double RemainS; public bool Allow; public int Limbo; public string Phase = "ready_up"; public int Hidden; }   // T-181: Phase = the game state of the row, Hidden = ticks the row is not listed yet
        internal sealed class DeckUnit { public AswMember M; public string Phase; public double RemainS; public string Serial, FromKey; }   // Phase: launching | recovering | cooling; FromKey: the task it was spawned from
        public int ShipUid = 77;
        public string ShipName = "Dwight D. Eisenhower CVN-69";
        public double PrepS = 900, TaxiS = 20, RecoverS = 30, CoolS = 600, FuelBurnS = 9000;
        public bool GapFrame = true;          // the spawn frame has neither the task nor the unit row
        public double HangarPrepS = -1;       // T-213: preparation of an order of the hangar rows (-1 = the rows do not tell); cool: rows tell it as NextPrepS
        public bool HangarReady;              // hangar rows can be launched at once (CanLaunchVehicle)
        public bool NoLink;                   // 0.27.8: the LaunchTask row does not name its task (FlightDeckTask._ltp missing)
        public string NewTaskPhase = "ready_up";   // T-181: the game state a new task row starts in (await_deck = the deck has no place for it)
        public List<Hang> Hangar = new List<Hang>();
        public List<DeckTask> Tasks = new List<DeckTask>();
        public List<DeckUnit> Units = new List<DeckUnit>();
        public List<AswMember> Air = new List<AswMember>();
        public List<string> Orders = new List<string>();      // "create type allow" per createLaunchTask
        private int _taskNo, _nextUid = 7100, _serialNo = 100, _nameNo = 1;
        private readonly Dictionary<int, string> _serial = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _name = new Dictionary<int, string>();

        public AswFakeDeck AddHangar(string type, string label, int count, bool? typeDipping = null, string dipNote = null) { Hangar.Add(new Hang { Type = type, Label = label, Count = count, TypeDipping = typeDipping, DipNote = dipNote }); return this; }

        /// <summary>T-188: what the type's sensors say (set through AddHangar); null = unreadable.</summary>
        private Hang TypeInfo(string type) { foreach (var h in Hangar) if (h.Type == type) return h; return null; }
        private bool? TypeDip(string type) { var h = TypeInfo(type); return h == null ? null : h.TypeDipping; }
        private string TypeNote(string type) { var h = TypeInfo(type); return h == null ? null : h.DipNote; }

        public string AddTask(string type, string label, double remainS, bool allow = false)
        {
            var t = new DeckTask { N = ++_taskNo, Type = type, Label = label, RemainS = remainS, Allow = allow, Phase = NewTaskPhase };
            Tasks.Add(t);
            return "task:t" + t.N;
        }

        private static int TaskUid(int n) { return AswMath.TaskUid("task:t" + n); }
        public int UidOfTask(string key) { foreach (var t in Tasks) if ("task:t" + t.N == key) return TaskUid(t.N); return 0; }

        public bool HasType(string type) { foreach (var h in Hangar) if (h.Type == type) return true; return false; }

        /// <summary>createLaunchTask: one machine leaves the hangar and a new task starts. Null = ok.</summary>
        public string Order(string type, bool allow, out string key, out double readyS)
        {
            key = null; readyS = 0;
            foreach (var h in Hangar)
            {
                if (h.Type != type) continue;
                if (h.Count < 1) return "launch_failed";
                h.Count--;
                Orders.Add("create " + type + " " + (allow ? "allow" : "hold"));
                key = AddTask(type, h.Label, PrepS, allow);
                readyS = PrepS;
                return null;
            }
            return "unknown_loadout";
        }

        public string Allow(string key)
        {
            foreach (var t in Tasks) if ("task:t" + t.N == key) { t.Allow = true; return null; }
            return "deck_task_not_found";
        }

        public int TaskCount { get { return Tasks.Count; } }

        /// <summary>T-181: a machine of the type leaves the hangar from outside (the CAP window, the player, a hit). Returns how many were taken.</summary>
        public int TakeFromHangar(string type, int n)
        {
            int taken = 0;
            foreach (var h in Hangar)
                if (h.Type == type) while (taken < n && h.Count > 0) { h.Count--; taken++; }
            return taken;
        }

        /// <summary>T-181: the game state of a pending task row (await_deck, await_crew, ready_up ...).</summary>
        public void SetPhase(string key, string phase) { foreach (var t in Tasks) if ("task:t" + t.N == key) t.Phase = phase; }

        /// <summary>T-181: the row of the task is not listed for some ticks (the game lists it a frame later).</summary>
        public void HideTask(string key, int ticks) { foreach (var t in Tasks) if ("task:t" + t.N == key) t.Hidden = ticks; }

        /// <summary>FlightDeck.abortLaunchTask: the task is removed and its machine goes back to the hangar (0.8.7 also does it when the spawn fails).</summary>
        public bool AbortTask(string key)
        {
            foreach (var t in Tasks)
            {
                if ("task:t" + t.N != key) continue;
                Tasks.Remove(t);
                bool found = false;
                foreach (var h in Hangar) if (h.Type == t.Type) { h.Count++; found = true; }
                if (!found) Hangar.Add(new Hang { Type = t.Type, Label = t.Label, Count = 1 });
                return true;
            }
            return false;
        }

        /// <summary>The task is gone and its machine with it (nothing to follow: destroyed before any scan saw it).</summary>
        public bool VanishTask(string key)
        {
            foreach (var t in Tasks) if ("task:t" + t.N == key) { Tasks.Remove(t); return true; }
            return false;
        }

        /// <summary>A foreign flight of the same deck (F-14, E-2C): a unit: row and then an air unit that nobody ordered.</summary>
        public int LaunchForeign(string type, string label)
        {
            var m = AswFakeGame.Helo(_nextUid++, "deck_launching");
            m.Type = type; m.TypeLabel = label; m.Helicopter = false; m.Passive = m.ActiveBuoys = m.Vlad = m.Torpedoes = 0; m.Dipping = false; m.Mad = false;
            m.DeckUid = ShipUid; m.Base = ShipName; m.DeckKey = "unit:" + m.Uid;
            _name[m.Uid] = label + " " + _nameNo++;
            Units.Add(new DeckUnit { M = m, Phase = "launching", RemainS = TaxiS, Serial = "S" + _serialNo++ });
            return m.Uid;
        }

        public AswMember FindAir(int uid) { foreach (var a in Air) if (a.Uid == uid) return a; return null; }

        /// <summary>Test hook: the machine runs out of fuel now and goes to land.</summary>
        public void ForceRecover(int uid)
        {
            var a = FindAir(uid);
            if (a == null) return;
            a.FuelFrac = 0;
        }

        private AswMember Template(int uid, string type, string label)
        {
            var m = AswFakeGame.Helo(uid, "air");
            m.DeckUid = ShipUid; m.Base = ShipName; m.Type = type; m.TypeLabel = label;
            m.FuelFrac = 1; m.EnduranceS = FuelBurnS;
            return m;
        }

        public void Advance(double dt, AswTarget place)
        {
            foreach (var t in new List<DeckTask>(Tasks))
            {
                if (t.Hidden > 0) { t.Hidden--; continue; }
                if (t.Limbo > 0)
                {
                    t.Limbo--;
                    if (t.Limbo == 0) { Tasks.Remove(t); Units.Add(MakeUnit(t)); }
                    continue;
                }
                t.RemainS -= dt;
                if (t.RemainS > 0 || !t.Allow) continue;
                if (GapFrame) t.Limbo = 1; else { Tasks.Remove(t); Units.Add(MakeUnit(t)); }
            }
            foreach (var u in new List<DeckUnit>(Units))
            {
                u.RemainS -= dt;
                if (u.RemainS > 0) continue;
                if (u.Phase == "launching")
                {
                    Units.Remove(u);
                    u.M.Where = "air"; u.M.DeckKey = null; u.M.FuelFrac = 1; u.M.Lat = place.Lat; u.M.Lon = place.Lon;
                    _serial[u.M.Uid] = u.Serial;
                    Air.Add(u.M);
                }
                else if (u.Phase == "recovering")
                {
                    u.Phase = "cooling"; u.RemainS = CoolS; ++_taskNo; u.M.DeckKey = "cool:c" + _taskNo; u.M.Uid = AswMath.TaskUid(u.M.DeckKey);
                }
                else
                {
                    Units.Remove(u);
                    bool found = false;
                    foreach (var h in Hangar) if (h.Type == u.M.Type) { h.Count++; found = true; }
                    if (!found) Hangar.Add(new Hang { Type = u.M.Type, Label = u.M.TypeLabel, Count = 1 });
                }
            }
            foreach (var a in new List<AswMember>(Air))
            {
                a.Lat = place.Lat; a.Lon = place.Lon;
                if (a.Passive == 0 && a.ActiveBuoys == 0 && a.Vlad == 0 && a.Torpedoes == 0 && !a.Dipping) continue;   // foreign flights just stay up
                a.FuelFrac -= dt / a.EnduranceS;
                if ((a.FuelFrac * a.EnduranceS - a.TransitS) * 0.9 > 0) continue;
                Air.Remove(a);
                a.Where = "deck_recover"; a.DeckKey = "unit:" + a.Uid; a.Passive = 40; a.ActiveBuoys = 8; a.Vlad = 4;
                string ser;
                _serial.TryGetValue(a.Uid, out ser);
                Units.Add(new DeckUnit { M = a, Phase = "recovering", RemainS = RecoverS, Serial = ser ?? "S?" });
            }
        }

        private DeckUnit MakeUnit(DeckTask t)
        {
            var m = Template(_nextUid++, t.Type, t.Label);
            m.Where = "deck_launching"; m.DeckKey = "unit:" + m.Uid; m.ReadyInS = TaxiS;
            _name[m.Uid] = "Griffin " + _nameNo++;
            return new DeckUnit { M = m, Phase = "launching", RemainS = TaxiS, Serial = "S" + _serialNo++, FromKey = "task:t" + t.N };
        }

        private static readonly Dictionary<string, int> Ammo = new Dictionary<string, int> { { "usn_ssq-53", 40 }, { "usn_ssq-62", 8 }, { "usn_ssq-77", 4 }, { "usn_mk46", 2 } };

        /// <summary>The deck lines the way AswGame.Deck builds them (through AswMath.DeckToMember).</summary>
        public List<AswMember> Rows()
        {
            var res = new List<AswMember>();
            foreach (var t in Tasks)
            {
                if (t.Limbo > 0 || t.Hidden > 0) continue;
                res.Add(AswMath.DeckToMember(new AswMath.DeckEntry
                {
                    DeckPhase = t.Phase, Key = "task:t" + t.N, Uid = TaskUid(t.N), ShipUid = ShipUid, ShipName = ShipName, Type = t.Type, Label = t.Label, Loadout = "ASWHunter",
                    State = t.RemainS > 1 ? "prep" : "ready", ReadyInS = Math.Max(0, t.RemainS), Count = 1, Ammo = Ammo, Helicopter = true, AswCapableType = true, TypeDipping = TypeDip(t.Type), DipNote = TypeNote(t.Type)
                }));
            }
            foreach (var u in Units)
            {
                string nm;
                _name.TryGetValue(u.M.Uid, out nm);
                bool cool = u.Phase == "cooling";
                var row = AswMath.DeckToMember(new AswMath.DeckEntry
                {
                    Key = cool ? u.M.DeckKey : "unit:" + u.M.Uid, Uid = u.M.Uid, ShipUid = ShipUid, ShipName = cool ? ShipName : null, Type = u.M.Type,
                    Label = cool ? u.M.TypeLabel : (nm ?? u.M.Name), Loadout = u.M.Loadout, State = cool ? "cooling" : u.Phase == "launching" ? "launching" : "recovering",
                    ReadyInS = u.RemainS, Serial = u.Serial, Ammo = u.M.Passive + u.M.Vlad > 0 ? Ammo : null, Helicopter = u.M.Helicopter, AswCapableType = u.M.Dipping,
                    FromKey = u.Phase == "launching" && !NoLink ? u.FromKey : null, TypeDipping = TypeDip(u.M.Type), DipNote = TypeNote(u.M.Type), NextPrepS = cool ? HangarPrepS : -1
                });
                if (!cool) { row.Name = nm ?? u.M.Name; row.Base = ShipName; }
                res.Add(row);
            }
            foreach (var h in Hangar)
            {
                for (int k = 1; k <= Math.Min(h.Count, AswMath.MaxSlots); k++)
                    res.Add(AswMath.DeckToMember(new AswMath.DeckEntry
                    {
                        Key = AswMath.SlotKey(h.Type, k), Uid = AswMath.HangarSlotUid(ShipUid, h.Type, k), Slot = k, ShipUid = ShipUid, ShipName = ShipName, Type = h.Type, Label = h.Label, Loadout = "ASWHunter",
                        State = HangarReady ? "ready" : "hangar", ReadyInS = HangarReady ? 0 : 900, Count = h.Count, Ammo = Ammo, Helicopter = true, AswCapableType = true, TypeDipping = h.TypeDipping, DipNote = h.DipNote, HangarPrepS = HangarPrepS
                    }));
            }
            return res;
        }

        /// <summary>Air units as live members (name "Griffin N", serial).</summary>
        public List<AswMember> AirMembers()
        {
            var res = new List<AswMember>();
            foreach (var a in Air)
            {
                string nm, ser;
                _name.TryGetValue(a.Uid, out nm); _serial.TryGetValue(a.Uid, out ser);
                var c = (AswMember)typeof(AswMember).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(a, null);
                if (nm != null) c.Name = nm;
                c.Serial = ser;
                res.Add(c);
            }
            return res;
        }
    }
}
