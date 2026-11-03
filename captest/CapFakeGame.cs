// T-112 D1: fake game for CapController. Deck "Nimitz" (uid 100), 4 x F-14A + 1 x A-7E, movement along the route, fuel burn, AAM,
// deck requests (Prep -> Ready -> launch), contacts and a command journal. Flat world: 1 nm = 1/60 deg on both axes.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class FakeBird
    {
        public int Uid; public string Name, Type = "F-14A", Loadout = "AirToAirLongRange", State = "Hangar";   // Hangar | Prep | Ready | Air
        public double Lat, Lon, ReserveNm = 800, MaxRangeNm = 2000, SpeedKn = 420, FuelFrac = -1, RingNm = -1;   // RingNm: the game's map ring (RangeOnMap, nm), -1 = not given
        public int AamNow = 8, AamStart = 8, LongNow = 4;
        public string AiState;      // T-317: StateMachine.CurrentStateName of the game AI (null = unknown)
        public double RouteDevNm;   // T-317: the last waypoint is this far (nm) from the one the mod placed; Patrol resets it
        public bool Selected;       // T-317: the unit is the selected one in the game
        public bool Engaged, Evading, GameRtb, GameWinchester, GameBingo, Rtb, Pursuing, Taken, InMissionZone;
        public string Roe = "free";
        public List<GeoPt> Route = new List<GeoPt>();
        public int Idx; public string Key;
        public double DistFlown, TaxiLeft;
        public int AltFt;          // T-244: the altitude of the last Patrol order
        public int Throttle = 2;   // step id 1..4, cruise = 2
        public bool ReadyGuess;    // a hangar row without a CAP loadout: CapGame plans with a guessed 900 s and marks it unknown
        public bool Hover;         // T-201: stays where it is (no route flying) - a bird that neither enters nor leaves a circle
        public bool Recovering;    // in the deck's recovery task (RemoveOnLand only)
        public double AamMaxNm;    // T-175: the alert intercept hands over to the game AI inside this range + 5 nm
        public double CoolLeft;    // State "Cool": the deck cool-down of a landed airframe
        public int TargetId = -1;  // T-333: the track id of the AI target when TargetKind != 0
        public int TargetKind;     // T-175: 0 = derived (air when Engaged, else none), 1 air, 2 surface, -1 unreadable
        public Dictionary<string, int> Weapons;   // T-325: current ammunition shown to the mod (null = unknown: the old behaviour)
        public string LoadoutLabel;               // T-325: the game's display name of the variant
        public string ShownLoadout;               // T-325: the variant name the mod sees instead of Loadout (null = Loadout)
    }

    internal sealed class FakeTask { public string Key, Type, Loadout, State = "Prep"; public double Remain; public bool Ours, Allow; public int BirdUid; public double Born; }

    internal sealed class CapFakeGame : ICapGame, IAirOps
    {
        public const int DeckUid = 100, ShipUid = 200;
        public double NowS = 1000, Scale = 1;
        public GeoPt Deck = new GeoPt(10.0, 10.0);
        public GeoPt? Ship = new GeoPt(10.0, 11.0);
        public string Mode = "Cheater";       // FlightDeckTimingsMode (Cheater: ready ~ 0 s; Arcade x10; Casual x5; else x1)
        public bool DropOrdersAtTc;            // time-compression: Patrol is silently ignored while Scale > 1
        public string SavePath, SaveDirectory;
        public readonly List<FakeBird> All = new List<FakeBird>();
        public readonly List<FakeTask> Tasks = new List<FakeTask>();
        public readonly List<CapContact> Contacts = new List<CapContact>();
        public readonly List<string> Journal = new List<string>();
        public readonly List<string> Logs = new List<string>();
        public readonly List<KeyValuePair<string, string>> Events = new List<KeyValuePair<string, string>>();
        public readonly HashSet<string> NoAmmo = new HashSet<string>(), Unavail = new HashSet<string>();
        public int Patrols;
        private int _key;

        public CapFakeGame()
        {
            for (int i = 1; i <= 4; i++)
                All.Add(new FakeBird { Uid = i, Name = "Tomcat " + i, Lat = Deck.Lat, Lon = Deck.Lon });
            for (int i = 5; i <= 6; i++)
                All.Add(new FakeBird { Uid = i, Name = "Eagle " + i, Type = "F-15C", Loadout = "AirToAir", LongNow = 0, Lat = Deck.Lat, Lon = Deck.Lon });
            All.Add(new FakeBird { Uid = 9, Name = "Corsair", Type = "A-7E", Loadout = "Strike", AamNow = 2, AamStart = 2, LongNow = 0, Lat = Deck.Lat, Lon = Deck.Lon });
        }

        public FakeBird Bird(int uid) { foreach (var b in All) if (b.Uid == uid) return b; return null; }
        public int Count(string what) { int n = 0; foreach (string j in Journal) if (j.StartsWith(what + " ", StringComparison.Ordinal) || j == what) n++; return n; }
        public int EventCount(string name) { int n = 0; foreach (var e in Events) if (e.Key == name) n++; return n; }
        public string LastEvent(string name) { string r = null; foreach (var e in Events) if (e.Key == name) r = e.Value; return r; }
        public bool LogHas(string s) { foreach (string l in Logs) if (l.Contains(s)) return true; return false; }

        // ---------------- simulation ----------------
        public void Advance(double dt)
        {
            NowS += dt;
            foreach (var t in Tasks)
            {
                if (t.State == "Prep") { t.Remain -= dt; if (t.Remain <= 0) { t.Remain = 0; t.State = "Ready"; var pb = Bird(t.BirdUid); if (pb != null && pb.State == "Prep") pb.State = "Ready"; } }
                if (t.State == "Ready" && t.Allow) Launch(t);
            }
            Tasks.RemoveAll(t => t.State == "Done");
            foreach (var b in All)
            {
                if (b.State == "Recover")   // T-204: the RecoveryTask is still on the deck, the cool-down task is not there yet
                {
                    b.CoolLeft -= dt;
                    if (b.CoolLeft <= 0) StartCool(b);
                    continue;
                }
                if (b.State == "Cool")
                {
                    b.CoolLeft -= dt;
                    if (b.CoolLeft <= 0)
                    {
                        b.CoolLeft = 0;
                        if (CoolToGameTask)   // T-204: a mission with FlightDeck_ReadyUpTasks: the cooled airframe is at once a ready-up task of the game
                        {
                            int n = ++_foreignReady;
                            b.State = "Prep"; b.Key = "task:g" + n; b.Loadout = "AirToAirLongRange";
                            Tasks.Add(new FakeTask { Key = b.Key, Type = b.Type, Loadout = b.Loadout, State = "Prep", Remain = 30, Ours = false, Allow = false, BirdUid = b.Uid, Born = NowS });
                            Journal.Add("game task " + b.Key);
                        }
                        else { b.State = "Hangar"; b.Uid = ++_newUid; Journal.Add("hangar " + b.Uid); }
                    }
                    continue;
                }
                if (b.State == "Taxi") { b.TaxiLeft -= dt; if (b.TaxiLeft <= 0) { b.State = "Air"; Journal.Add("airborne " + b.Uid); } continue; }
                if (b.State != "Air") continue;
                double step = b.SpeedKn / 3600.0 * dt;
                b.ReserveNm = Math.Max(0, b.ReserveNm - step * Math.Max(1, 1 + (b.SpeedKn - 420) / 200));
                b.DistFlown += step;
                if (b.Rtb)
                {
                    if (RemoveOnLand && AswMath.DistNm(new GeoPt(b.Lat, b.Lon), Deck) < 10) b.Recovering = true;
                    if (Move(b, Deck, step) || AswMath.DistNm(new GeoPt(b.Lat, b.Lon), Deck) < 1) Land(b);
                    continue;
                }
                if (!b.Hover && b.Idx < b.Route.Count && Move(b, b.Route[b.Idx], step)) b.Idx++;
            }
        }

        // true = arrived (carry-over of the step is dropped, enough for a coarse fake)
        private static bool Move(FakeBird b, GeoPt to, double stepNm)
        {
            var from = new GeoPt(b.Lat, b.Lon);
            double d = AswMath.DistNm(from, to);
            if (d <= stepNm) { b.Lat = to.Lat; b.Lon = to.Lon; return true; }
            b.Lat += (to.Lat - b.Lat) * stepNm / d; b.Lon += (to.Lon - b.Lon) * stepNm / d;
            return false;
        }

        private void Launch(FakeTask t)
        {
            var b = Bird(t.BirdUid);
            t.State = "Done";
            if (b == null) return;
            if (RenumberOnTakeoff) b.Uid += 2000;
            b.State = TaxiS > 0 ? "Taxi" : "Air"; b.TaxiLeft = TaxiS; b.Key = null; b.Lat = Deck.Lat; b.Lon = Deck.Lon; b.Route.Clear(); b.Idx = 0; b.Rtb = false; b.Pursuing = false;
            b.Throttle = 2; b.SpeedKn = 420; b.Recovering = false;
            b.Loadout = t.Loadout;
            b.AamNow = b.AamStart = Type8(b.Type); b.LongNow = t.Loadout == "AirToAirLongRange" ? 4 : 0;
            b.ReserveNm = LaunchReserveNm >= 0 ? LaunchReserveNm : b.MaxRangeNm;   // the game: the path at take-off is the full-tank path of the loadout
            Journal.Add("launched " + b.Uid);
        }

        private static int Type8(string type) { return type == "A-7E" ? 2 : 8; }

        private void Land(FakeBird b)
        {
            b.State = "Hangar"; b.Rtb = false; b.Route.Clear(); b.Idx = 0; b.Lat = Deck.Lat; b.Lon = Deck.Lon;
            b.ReserveNm = 800; b.AamNow = Type8(b.Type); b.Engaged = false; b.Throttle = 2; b.SpeedKn = 420;
            Journal.Add("landed " + b.Uid);
            b.Recovering = false;
            if (!RemoveOnLand) return;
            // the real game: the unit is removed at landing; the airframe cools down under another uid, then is a hangar row (yet another uid)
            b.Uid = ++_newUid;
            if (LandNoAirframe) { b.State = "Gone"; return; }
            if (RecoverGapS > 0) { b.State = "Recover"; b.CoolLeft = RecoverGapS; Journal.Add("recover " + b.Uid); return; }
            StartCool(b);
        }

        private void StartCool(FakeBird b)
        {
            if (CoolS > 0) { b.State = "Cool"; b.CoolLeft = CoolS; Journal.Add("cool " + b.Uid); }
            else { b.State = "Hangar"; Journal.Add("hangar " + b.Uid); }
        }

        // ---- T-204 ----
        public double RecoverGapS;     // after Land the airframe is in the deck's RecoveryTask for this long (no row; Landing() = true)
        public bool NoHangar;          // the mission has Numbers = 0: hangar rows are never listed
        public bool CoolToGameTask;    // the end of a cool-down gives a ready-up task of the game instead of a hangar row
        public bool Landing(int deckUid, string type) { foreach (var b in All) if (b.State == "Recover" && b.Type == type) return true; return false; }
        /// <summary>A free ready-up task of the game (FlightDeck_ReadyUpTasks), as T-187.</summary>
        public FakeBird AddGameTask(string type) { return AddForeignReady(type, "AirToAirLongRange"); }

        // ---------------- ICapGame ----------------
        public double Now { get { return NowS; } }
        public double TimeScale { get { return Scale; } }
        public string LastSavePath { get { return SavePath; } }
        public string SaveDir { get { return SaveDirectory; } }

        public int BirdsCalls;
        public IList<CapBird> Birds()
        {
            BirdsCalls++;
            var l = new List<CapBird>();
            var slotRows = new List<CapBird>(); var slotCount = new Dictionary<string, int>();   // T-186: hangar machines of a type as slot rows
            foreach (var b in All)
            {
                if (b.State == "Gone" || b.State == "Recover" || HideUids.Contains(b.Uid)) continue;
                if (NoHangar && b.State == "Hangar") continue;
                if (SlotHangar && (b.State == "Prep" || b.State == "Ready") && TaskLagging(b.Key)) continue;
                bool eng = CapMath.EngagedNow(b.AiState, b.Engaged ? 1 : 0);   // T-319
                var c = new CapBird
                {
                    Uid = b.Uid, Name = b.Name, DeckUid = DeckUid, Type = b.Type, Loadout = b.Loadout, Lat = b.Lat, Lon = b.Lon,
                    AamNow = b.AamNow, LongNow = b.LongNow, ReserveNm = b.MaxRangeNm <= 0 ? 0 : b.ReserveNm, MaxRangeNm = b.MaxRangeNm,
                    PatrolKn = b.SpeedKn, CruiseKn = b.SpeedKn, FuelFrac = b.FuelFrac, FuelFracLive = b.FuelFrac >= 0 && b.MaxRangeNm > 0, RingNm = b.RingNm,
                    Engaged = eng, AiState = b.AiState, Evading = b.Evading, GameRtb = b.GameRtb, GameWinchester = b.GameWinchester, GameBingo = b.GameBingo,
                    Returning = b.State == "Air" && (b.Rtb || b.GameRtb), Recovering = b.State == "Air" && b.Recovering,
                    TargetKind = b.TargetKind != 0 ? b.TargetKind : (eng ? 1 : 0), HasTarget = b.TargetKind != 0, TargetId = b.TargetKind != 0 ? b.TargetId : -1, AamMaxNm = b.AamMaxNm
                };
                if (b.State == "Hangar") { c.Where = "hangar"; if (b.ReadyGuess) { c.ReadyInS = 900; c.ReadyKnown = false; } }
                else if (b.State == "Prep") c.Where = "deck_prep";
                else if (b.State == "Ready") c.Where = "deck_ready";
                else if (b.State == "Cool") { c.Where = "deck_cool"; c.ReadyInS = b.CoolLeft; c.AamNow = 0; c.LongNow = 0; c.Loadout = null; }
                else if (b.State == "Taxi") { if (!ListTaxi) continue; c.Where = "deck_launch"; }
                else { c.Where = "to_zone"; c.AamStart = 0; }
                if (b.ShownLoadout != null) c.Loadout = b.ShownLoadout;
                if (b.Weapons != null && c.Where != "hangar" && c.Where != "deck_cool") { c.Weapons = c.LoadWeapons = b.Weapons; c.LoadoutLabel = b.LoadoutLabel; }   // T-325
                if (SlotHangar && (b.State == "Prep" || b.State == "Ready")) { c.Uid = AirOpsMath.TaskUid(b.Key); c.PendingKey = b.Key; c.Name = null; }
                if (SlotHangar && b.State == "Hangar")
                {
                    if (b.Taken) continue;
                    int cnt; slotCount.TryGetValue(b.Type, out cnt); slotCount[b.Type] = cnt + 1;
                    if (cnt == 0) slotRows.Add(c);
                    continue;
                }
                l.Add(c);
            }
            foreach (var proto in slotRows)
            {
                int n = slotCount[proto.Type];
                for (int k = 1; k <= n && k <= AirOpsMath.MaxSlots; k++)
                {
                    var r = proto.Copy();
                    r.Uid = AirOpsMath.HangarSlotUid(DeckUid, proto.Type, k); r.HangarK = k; r.HangarN = n; r.Name = null;
                    l.Add(r);
                }
            }
            return l;
        }

        // ---- T-186 (ADR-0082): hangar machines as slot rows, the task row of an order listed late, a machine taken from outside ----
        public bool SlotHangar;
        public double TaskRowLag;
        private bool TaskLagging(string key) { foreach (var t in Tasks) if (t.Key == key) return NowS - t.Born < TaskRowLag; return false; }

        /// <summary>A machine of the type leaves the hangar from outside (a player order, the game, a hit). false = none left.</summary>
        public bool TakeOut(string type)
        {
            for (int i = All.Count - 1; i >= 0; i--) if (All[i].State == "Hangar" && All[i].Type == type && !All[i].Taken) { All[i].State = "Gone"; return true; }
            return false;
        }

        public int HangarNumbers(string type) { int n = 0; foreach (var b in All) if (b.State == "Hangar" && b.Type == type && !b.Taken) n++; return n; }

        /// <summary>type -> VehicleTypeOnBoard._type as the game reports it (Plane | Helicopter | ...); missing -> "Plane".</summary>
        public readonly Dictionary<string, string> VehicleClass = new Dictionary<string, string>();
        public readonly Dictionary<string, List<LoadoutOption>> ExtraLoadouts = new Dictionary<string, List<LoadoutOption>>();
        public readonly Dictionary<string, CapAmmo> ExtraAmmo = new Dictionary<string, CapAmmo>();

        private readonly Dictionary<string, string> _adm = new Dictionary<string, string>();
        public string TypeAdmission(string type)
        {
            string r, vc;
            if (_adm.TryGetValue(type ?? "", out r)) return r;
            r = CapMath.TypeAdmission(VehicleClass.TryGetValue(type ?? "", out vc) ? vc : "Plane", Loadouts(DeckUid, type), Ammo);
            _adm[type ?? ""] = r;
            return r;
        }

        public List<LoadoutOption> Loadouts(int deckUid, string type)
        {
            List<LoadoutOption> extra;
            if (ExtraLoadouts.TryGetValue(type ?? "", out extra)) return extra;
            var l = new List<LoadoutOption>();
            if (type == "F-14A")
            {
                l.Add(Lo("AirToAirLongRange", "AIM-54", 4, "AIM-7", 2, "AIM-9", 2, "Tank", 2));
                l.Add(Lo("AirToAir", "AIM-7", 4, "AIM-9", 4));
                l.Add(Lo("Strike", "Mk82", 6, "AIM-9", 2));
            }
            else if (type == "F-15C") l.Add(Lo("AirToAir", "AIM-7", 4, "AIM-9", 4));
            else l.Add(Lo("Strike", "Mk82", 6, "AIM-9", 2));
            return l;
        }

        private LoadoutOption Lo(string name, params object[] kv)
        {
            var lo = new LoadoutOption { Name = name, Display = name, Capacity = 8, Available = !Unavail.Contains(name), Weapons = new Dictionary<string, int>() };
            for (int i = 0; i + 1 < kv.Length; i += 2) lo.Weapons[(string)kv[i]] = (int)kv[i + 1];
            return lo;
        }

        public CapAmmo Ammo(string name)
        {
            CapAmmo ex;
            if (name != null && ExtraAmmo.TryGetValue(name, out ex)) return ex;
            switch (name)
            {
                case "AIM-54": return new CapAmmo { Name = name, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 100 };
                case "AIM-7": return new CapAmmo { Name = name, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 25 };
                case "AIM-9": return new CapAmmo { Name = name, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 10 };
                case "Mk82": return new CapAmmo { Name = name, Type = "Bomb", TargetType = "Surface" };
                case "Tank": return new CapAmmo { Name = name, Type = "Tank", TargetType = "None", FuelKg = 1500 };
                case "R-27R": return new CapAmmo { Name = name, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 40 };   // T-325
                case "R-73": return new CapAmmo { Name = name, Type = "Missile", TargetType = "AAW", MaxLaunchRangeNm = 8 };
                case "FAB-500": return new CapAmmo { Name = name, Type = "Bomb", TargetType = "ASuW" };
                case "Kh-31A": return new CapAmmo { Name = name, Type = "Missile", TargetType = "ASuW", MaxLaunchRangeNm = 30 };
                case "Kh-31P": return new CapAmmo { Name = name, Type = "Missile", TargetType = "ASuW", MaxLaunchRangeNm = 50 };
                case "GSh": return new CapAmmo { Name = name, Type = "Projectile", TargetType = "AAW" };
            }
            return null;
        }

        public IList<CapContact> AirContacts() { return new List<CapContact>(Contacts); }

        public GeoPt? UnitPos(int uid)
        {
            if (uid == DeckUid) return Deck;
            if (uid == ShipUid) return Ship;
            return null;
        }

        public string DeckTimingsMode() { return Mode; }

        public List<AirDeckTask> DeckTasks(int deckUid)
        {
            var l = new List<AirDeckTask>();
            foreach (var t in Tasks) if (t.State != "Done" && !(SlotHangar && NowS - t.Born < TaskRowLag)) l.Add(new AirDeckTask { Key = t.Key, ShipUid = DeckUid, Type = t.Type, Loadout = t.Loadout, State = t.State, RemainS = t.Remain, Ours = t.Ours });
            return l;
        }

        public bool RemapOnOrder;
        public readonly HashSet<int> HideUids = new HashSet<int>();   // 0.27.8: rows Birds() misses for a while (the task is still on the deck)
        /// <summary>0.27.8: the ADR-0036 §11 detector as CapGame had it before the fix (ROE differs from the one the mod set = player); with
        /// GameRtbHold the RTB puts weapons on hold as the game's deck return route does.</summary>
        public bool RoeDetector, GameRtbHold;
        private readonly Dictionary<int, string> _roeByMod = new Dictionary<int, string>();
        public bool RenumberOnTakeoff;     // the airborne unit has yet another uid than the deck task
        public double TaxiS;               // 0.8.7: after the pending task the machine is spawned (final uid) and taxis out this long before it is in flight
        public bool ListTaxi = true;
        /// <summary>0.28.9 live: the unit is removed at landing (Recovering is listed on approach); the airframe cools down CoolS under a new uid (0 = straight
        /// to the hangar), then is a hangar row under yet another uid.</summary>
        public bool RemoveOnLand;
        public double CoolS = 120;
        public bool LandNoAirframe;
        public double LaunchReserveNm = -1;   // -1 = MaxRangeNm (full tanks); path at take-off (live 0.28.10: F-14A with 6 x AIM-54 had ~290 nm of a 2107 nm clean MaxRange)        // the landed airframe never shows up on the deck
        private int _newUid = 7000;       // the game lists the taxiing machine as a deck_launch row (false = the old gap)

        public int TaskBirdUid(int deckUid, string key)
        {
            foreach (var t in Tasks) if (t.Key == key && t.State != "Done") return SlotHangar ? (NowS - t.Born < TaskRowLag ? 0 : AirOpsMath.TaskUid(key)) : t.BirdUid;
            return 0;
        }

        public string OrderLaunch(int deckUid, string type, string loadout, string missionType, bool allowLaunch, out string key, out double readyS)
        {
            key = null; readyS = 0;
            Journal.Add("order " + deckUid + " " + type + " " + loadout + " " + missionType + " " + (allowLaunch ? "allow" : "hold"));
            if (OrderErr != null) return OrderErr;   // T-187: the deck refuses every order with this code
            if (NoAmmo.Contains(loadout)) return "no_ammo";
            FakeBird pick = null;
            foreach (var b in All) if (b.State == "Hangar" && b.Type == type && !b.Taken) { pick = b; break; }
            if (pick == null) return "launch_failed";
            var lo = FindLo(type, loadout);
            readyS = AirOpsMath.ReadyS(lo, Mode, 0);
            var t = new FakeTask { Key = "k" + (++_key), Type = type, Loadout = loadout, Remain = readyS, Ours = true, Allow = allowLaunch, BirdUid = pick.Uid, Born = NowS };
            pick.State = "Prep"; pick.Key = t.Key;
            if (RemapOnOrder) { pick.Uid += 1000; t.BirdUid = pick.Uid; }   // the real game shows the task under another uid
            Tasks.Add(t); key = t.Key;
            return null;
        }

        private LoadoutOption FindLo(string type, string name) { foreach (var lo in Loadouts(DeckUid, type)) if (lo.Name == name) return lo; return null; }

        // live 0.28.3: the flight deck knocked out (BaseSystem.MakeInoperable -> FlightDeck.KillAircraft: every task cleared, no refund)
        public bool DeckOut;
        public bool DeckDown(int deckUid) { return DeckOut; }
        public bool DeckInoperable(int deckUid) { return DeckOut; }
        public string DeckDump(int deckUid)
        {
            var parts = new List<string>();
            foreach (var t in Tasks) if (t.State != "Done") parts.Add(t.Type + ":" + t.State + ":" + t.Key);
            return parts.Count == 0 ? "-" : string.Join(" ", parts);
        }
        /// <summary>As the game's KillAircraft: the tasks go, their airframes are not given back.</summary>
        public void KnockOutDeck()
        {
            DeckOut = true;
            foreach (var t in Tasks) { if (t.State == "Done") continue; t.State = "Done"; var b = Bird(t.BirdUid); if (b != null && b.State != "Air") b.State = "Gone"; }
        }

        public List<string> PrioLog = new List<string>();
        public string Prioritise(int deckUid, string key) { PrioLog.Add(deckUid + ":" + key); return null; }

        // ---- T-119: the air tick on the test side ----------------------------------------------------------------------

        public AirDeckBroker Broker;
        public AirDeckParams BrokerParams = new AirDeckParams();

        /// <summary>One step of the air tick: the broker is created (shared ownership of the controller) and wired on first use; BeginTick, Tick, EndTick.</summary>
        public void Tick(CapController c)
        {
            if (Broker == null) Broker = new AirDeckBroker(this, c.Ownership, BrokerParams, null, null);
            c.Deck = Broker;
            Broker.BeginTick(Now);
            c.Tick(Now);
            Broker.EndTick(Now);
        }

        /// <summary>The frame end outside the tick (window commands).</summary>
        public void Flush() { if (Broker != null) Broker.Flush(Now); }

        public string AllowLaunch(int deckUid, string key)
        {
            Journal.Add("allow " + key);
            foreach (var t in Tasks) if (t.Key == key) { t.Allow = true; return null; }
            return "unknown_key";
        }

        public string AbortLaunch(int deckUid, string key)
        {
            Journal.Add("abort " + key);
            foreach (var t in Tasks)
                if (t.Key == key)
                {
                    t.State = "Done"; var b = Bird(t.BirdUid); if (b != null && b.State != "Air") { b.State = "Hangar"; b.Key = null; }
                    return null;
                }
            return "unknown_key";
        }

        /// <summary>T-187: when set, OrderLaunch refuses with this code ("no_airframes", "unknown_loadout").</summary>
        public string OrderErr;
        private int _foreignReady;
        /// <summary>T-218: key prefix of the game's own ready tasks; a second stand (the world after a load) uses another one: the game gives new GUIDs.</summary>
        public string ForeignKeyPrefix = "task:f";

        /// <summary>T-187: a machine the game itself prepared on the deck (FlightDeck_ReadyUpTasks): Ready, a task of someone else with key "task:f&lt;n&gt;", launch not allowed.</summary>
        public FakeBird AddForeignReady(string type, string loadout)
        {
            int n = ++_foreignReady;
            var b = new FakeBird { Uid = 500 + n, Name = "Ready " + n, Type = type, Loadout = loadout, State = "Ready", Key = ForeignKeyPrefix + n, Lat = Deck.Lat, Lon = Deck.Lon };
            All.Add(b);
            Tasks.Add(new FakeTask { Key = b.Key, Type = type, Loadout = loadout, State = "Ready", Remain = 0, Ours = false, Allow = false, BirdUid = b.Uid, Born = NowS });
            return b;
        }

        /// <summary>T-226: a machine the game itself is preparing on the deck: Prep, a task of someone else with key "task:f&lt;n&gt;", remainS seconds left.</summary>
        public FakeBird AddForeignPrep(string type, string loadout, double remainS)
        {
            int n = ++_foreignReady;
            var b = new FakeBird { Uid = 500 + n, Name = "Prep " + n, Type = type, Loadout = loadout, State = "Prep", Key = ForeignKeyPrefix + n, Lat = Deck.Lat, Lon = Deck.Lon };
            All.Add(b);
            Tasks.Add(new FakeTask { Key = b.Key, Type = type, Loadout = loadout, State = "Prep", Remain = remainS, Ours = false, Allow = false, BirdUid = b.Uid, Born = NowS });
            return b;
        }

        /// <summary>A task of someone else (not ours): shows up in DeckTasks, never aborted by the controller.</summary>
        public FakeTask AddForeignTask(string type, string loadout)
        {
            var t = new FakeTask { Key = "foreign" + (++_key), Type = type, Loadout = loadout, Remain = 600, Ours = false };
            Tasks.Add(t); return t;
        }

        public string Patrol(int uid, IList<GeoPt> pts, int altFt, int keepStep = 0)
        {
            Journal.Add("patrol " + uid + " " + pts.Count);
            var b = Bird(uid);
            if (b == null || b.State != "Air") return "not_airborne";
            if (DropOrdersAtTc && Scale > 1) return null;   // silently ignored
            Patrols++; b.AltFt = altFt;
            if (keepStep > 0) Journal.Add("patrol_keep " + uid + " " + keepStep);
            if (StaleTelegraph != null && keepStep <= 0 && NowS - LastSetAt < 4 && b.Throttle != StaleTelegraph.Value) { Journal.Add("patrol_stale " + uid); b.Throttle = StaleTelegraph.Value; b.SpeedKn = StepKn[b.Throttle - 1]; }   // T-257: the real Patrol put back a stale read
            b.Route = new List<GeoPt>(pts); b.Idx = 0; b.Rtb = false; b.Pursuing = false; b.RouteDevNm = 0;
            return null;
        }

        public int WaypointsLeft(int uid) { var b = Bird(uid); return b == null || b.Rtb ? 0 : Math.Max(0, b.Route.Count - b.Idx); }

        public string SetRoe(int uid, string roe, out string previous)
        {
            var b = Bird(uid); previous = null;
            if (b == null) return "unknown_unit";
            previous = b.Roe; b.Roe = roe; _roeByMod[uid] = roe;
            Journal.Add("roe " + uid + " " + roe + " prev=" + previous);
            return null;
        }

        public int RtbStep = -1;   // T-197: >= 1 = Rtb() sets this throttle step like the real game; -1 = do not touch
        public string Rtb(int uid)
        {
            Journal.Add("rtb " + uid);
            var b = Bird(uid); if (b == null) return "unknown_unit";
            b.Rtb = true; b.Route.Clear(); if (GameRtbHold) b.Roe = "hold";
            if (RtbStep >= 1) { b.Throttle = RtbStep; b.SpeedKn = StepKn[RtbStep - 1]; }   // T-197: the game sets the cruise step on Rtb
            return null;
        }

        /// <summary>T-175: null = the surface task is cleared; ClearSurfaceError (e.g. "unsupported") = the member of the game is missing.</summary>
        public string ClearSurfaceError;
        public string ClearSurfaceTarget(int uid)
        {
            Journal.Add("clear_surface " + uid);
            var b = Bird(uid); if (b == null) return "unknown_unit";
            if (ClearSurfaceError != null) return ClearSurfaceError;
            b.TargetKind = 0; b.Engaged = false; b.Pursuing = false; return null;
        }

        public string BreakPursuit(int uid)
        {
            Journal.Add("break " + uid);
            var b = Bird(uid); if (b == null) return "unknown_unit";
            b.Pursuing = false; return null;
        }

        public string Restore(int uid)
        {
            Journal.Add("restore " + uid);
            var b = Bird(uid); if (b == null) return "unknown_unit";
            b.Roe = "free";
            b.Route.Clear(); b.Idx = 0; return null;
        }

        // ---- speed steps (ADR-0040 D2) ----
        public bool GameK;                 // true: the fake reports burn coefficients K (1, 1, 1.3, 1.6); false: unknown (0)
        public bool NoSteps;
        public double LastSetAt = -1e9;
        public int? StaleTelegraph;   // T-257: Patrol without keepStep puts this (old) step back
        public static readonly string[] StepNames = { "Loiter", "Cruise", "Military", "Max" };
        public static readonly double[] StepKn = { 300, 420, 520, 620 };
        public int Throttle(int uid) { var b = Bird(uid); return b == null ? -1 : b.Throttle; }
        public string SetThrottle(int uid, int step)
        {
            Journal.Add("throttle " + uid + " " + step);
            var b = Bird(uid); if (b == null) return "unknown_unit";
            if (step < 1 || step > 4) return "bad_step";
            b.Throttle = step; b.SpeedKn = StepKn[step - 1]; LastSetAt = NowS; return null;
        }
        public IList<CapStep> ThrottleSteps(int uid)
        {
            var l = new List<CapStep>();
            if (NoSteps) return l;
            double[] k = { 1, 1, 1.3, 1.6 };
            for (int i = 0; i < 4; i++) l.Add(new CapStep { Step = i + 1, Kn = StepKn[i], K = GameK ? k[i] : 0, Name = StepNames[i] });
            return l;
        }

        // T-317 (ADR-0124): the same tracker as the game layer; b.Taken = a route deviation outside any window rule (the old flag)
        public readonly CapTakeoverTracker Take = new CapTakeoverTracker();
        private readonly Dictionary<int, string> _takeDetail = new Dictionary<int, string>();
        private readonly HashSet<int> _takeDrift = new HashSet<int>();

        private TakeProbe ProbeOf(FakeBird b, bool withRoute)
        {
            string r;
            var p = new TakeProbe { State = b.AiState, EngageTasks = b.Engaged ? 1 : 0, RoeNow = b.Roe };
            if (RoeDetector && b.State == "Air" && _roeByMod.TryGetValue(b.Uid, out r)) p.RoeSet = r;
            if (withRoute) { p.Wps = Math.Max(1, b.Route.Count); p.DevNm = b.Taken ? 99 : b.RouteDevNm; }
            return p;
        }

        public bool TakenByPlayer(int uid)
        {
            var b = Bird(uid);
            if (b == null) return false;
            if (b.Selected) Take.NoteSelected(uid, NowS);
            string why;
            var p = ProbeOf(b, true);
            var v = Take.Check(uid, p, NowS, out why);
            if (v == TakeVerdict.None) { return false; }
            bool win = Take.InWindow(uid, NowS), sel = Take.SelectedInWindow(uid, NowS);
            _takeDetail[uid] = "why=" + why + " " + CapTakeoverMath.Detail(p) + " sel=" + (sel ? 1 : 0) + " win=" + (win ? 1 : 0);
            if (v == TakeVerdict.AiDrift)
            {
                if (why == "roe") _roeByMod[uid] = b.Roe; else b.RouteDevNm = 0;
                _takeDrift.Add(uid);
                if (!Take.DriftLogged(uid)) { Take.NoteDriftLogged(uid); Log("[Cap] ai drift uid=" + uid + " " + _takeDetail[uid]); }
                return false;
            }
            return true;
        }
        public string TakenDetail(int uid) { string d; return _takeDetail.TryGetValue(uid, out d) ? d : null; }
        public bool TakeAiDrift(int uid) { return _takeDrift.Remove(uid); }
        public bool AiWindow(int uid)
        {
            var b = Bird(uid); if (b == null) return false;
            if (b.Selected) Take.NoteSelected(uid, NowS);
            Take.Touch(uid, ProbeOf(b, false), NowS);
            return Take.InWindow(uid, NowS) && !Take.SelectedInWindow(uid, NowS);
        }
        // T-333: track state of the intercept targets: trackId -> "destroyed" | "lost" (a test sets it; a seeded id without an entry reads "lost")
        public readonly Dictionary<int, string> Gone = new Dictionary<int, string>();
        public readonly HashSet<int> Seeded = new HashSet<int>();
        public void IcptSeed(int trackId) { Seeded.Add(trackId); }
        public void IcptDrop(int trackId) { Seeded.Remove(trackId); }
        public string TrackGone(int trackId)
        {
            if (!Seeded.Contains(trackId)) return null;
            string g; if (Gone.TryGetValue(trackId, out g)) return g;
            foreach (var c in Contacts) if (c.Id == trackId) return null;
            return "lost";
        }
        public bool UnitAlive(int uid) { var b = Bird(uid); return b != null && b.State != "Gone"; }
        public bool InGameCapZone(int uid) { var b = Bird(uid); return b != null && b.InMissionZone; }

        public void Event(string name, string dataJson) { Events.Add(new KeyValuePair<string, string>(name, dataJson)); }
        public void Log(string line) { Logs.Add(line); }
    }
}
