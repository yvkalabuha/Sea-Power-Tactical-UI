using System;
using System.Collections.Generic;
using System.IO;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// The wiring of GameApi.WireMission (MissionGame.cs) and AirTickFeature without the Unity part, on the real controllers and the fakes of the
    /// other stands. tests/test_sea_power_lifecycletest.py compares <see cref="Wiring"/> with the production lists.
    /// </summary>
    internal sealed class LifeRig : IDisposable
    {
        public static readonly (int Order, string Id)[] Wiring = new[]
        {
            (10, "air_tick"), (20, "sar"), (30, "asw"), (40, "cap"), (50, "identify"), (60, "smart_attack"), (70, "doctrine"),
        };

        private sealed class NoOwners : IDoctrineOwners
        {
            public object FormationOf(object unit) { return null; }
            public object TaskforceOf(object owner) { return null; }
            public string Kind4Of(object unit) { return "ship"; }
        }

        public readonly string SaveDir;
        public readonly MissionLifecycle M;
        public readonly List<string> Log = new List<string>(), Calls = new List<string>();
        public AswFakeGame AswG; public AswController Asw;
        public SarFakeGame SarG; public SarController Sar;
        public CapFakeGame CapG; public CapController Cap;
        public SmartFakeGame SmartG; public SmartAttackController Smart;
        public IdentifyStore IdStore; public IdentifyKeeper Id;
        public AirTickClock Clock = new AirTickClock();
        public MemoryFlagStore Flags = new MemoryFlagStore(new NoOwners());
        public AirOwnership Own = new AirOwnership();
        public double Time;                                       // game seconds of the loaded mission
        // T-211 (ADR-0088): the post-load gate of AirTickFeature; GateOn = the production wiring (DeferRestore of asw and cap, the gate armed by a load)
        public readonly PostLoadGate Gate = new PostLoadGate(); public bool GateOn, HideLists; public readonly List<string> Applied = new List<string>();
        public string Marker;                                     // the content of the game's last_save file
        private readonly bool _wrong;
        private int _loadedId = -1;
        private bool _smartLive;
        private string _idSave, _smartSave, _capSave; private bool _capSeen;

        public LifeRig(bool wrongOrder = false)
        {
            _wrong = wrongOrder;
            SaveDir = Path.Combine(Path.GetTempPath(), "lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SaveDir);
            M = new MissionLifecycle(Log.Add);
            AswG = new AswFakeGame { SaveDir = Path.Combine(SaveDir, "asw"), Mode = "Arcade" };
            AswG.LoadoutList.Add(new LoadoutOption { Name = "Default", Display = "Default", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            SarG = AswG.SarWorld;
            CapG = new CapFakeGame { SaveDirectory = SaveDir };
            CapG.All.Clear();
            var ammo = WireAmmo.FromIni("adcap", SmartAttackMath.AdcapIni); ammo.Count = 4;
            SmartG = new SmartFakeGame(ammo) { Dir = Path.Combine(SaveDir, "smart") };
            Asw = new AswController(AswG, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }) { Ownership = Own };
            Own.Register("asw", Asw.GroupNameOf);
            Sar = new SarController(SarG, new SarParams(), null);
            Asw.Sar = Sar;
            Cap = new CapController(CapG, new CapParams { RestoreOnLoad = "auto" }, Own);
            Smart = new SmartAttackController(SmartG, new SmartParams(), false);
            IdStore = new IdentifyStore();
            Id = new IdentifyKeeper(IdStore, Path.Combine(SaveDir, "identify"), Log.Add);
            AswG.SarWorld.Units.Clear();
            Wire();
        }

        private int O(string id)
        {
            foreach (var w in Wiring) if (w.Id == id) { if (_wrong && id == "sar") return 30; if (_wrong && id == "asw") return 20; return w.Order; }
            throw new InvalidOperationException(id);
        }

        private void Sub(string id, Action<MissionEnter> entered, Action<string> left)
        {
            M.Subscribe(O(id), id, entered == null ? (Action<MissionEnter>)null : e => { Calls.Add("E:" + id); entered(e); },
                left == null ? (Action<string>)null : why => { Calls.Add("L:" + id + ":" + why); left(why); });
        }

        private void ResetAir()
        {
            Clock.Reset();
            if (AswG.Broker != null) AswG.Broker.Reset();
            if (CapG.Broker != null) CapG.Broker.Reset();
        }

        // bodies as in MissionGame.cs WireMission / AirTickFeature.Init, minus the *GameImpl calls
        private void Wire()
        {
            Sub("air_tick", e => { ResetAir(); if (GateOn) { if (e.SavePath != null) Gate.Arm(0); else Gate.Disarm(); } }, why => { ResetAir(); Gate.Disarm(); });
            Sub("sar", e => { Sar.OnMissionChanged(); }, why => { Sar.OnMissionLeft(); });
            Sub("asw", e => { Asw.DeferRestore = GateOn && e.SavePath != null; Asw.OnMissionChanged(e.SavePath, e.MissionTimeS); }, why => { Asw.OnMissionLeft(); });
            Sub("cap", e => { Cap.DeferRestore = GateOn && e.SavePath != null; Cap.OnMissionChanged(e.SavePath, e.MissionTimeS); _capSave = e.SavePath; _capSeen = true; }, why => { Cap.OnMissionLeft(); _capSave = null; _capSeen = false; });
            Sub("identify", e => { _idSave = e.SavePath; Id.OnMissionChanged(e.SavePath, e.MissionTimeS); }, why => { Id.OnMissionLeft(); _idSave = null; });
            Sub("smart_attack", e => { _smartSave = e.SavePath; Smart.OnMissionChanged(e.SavePath, e.MissionTimeS); }, why => { Smart.OnMissionLeft(); _smartSave = null; });
            Sub("doctrine", null, why => { Flags.ResetMission(); });
        }

        // ---------------------------------------------------------------- the world

        /// <summary>The game sets last_save (a load or a save of the player).</summary>
        public void SetMarker(string sav)
        {
            Marker = sav; AswG.LastSavePath = sav; CapG.SavePath = sav; SmartG.SavePath = sav;
        }

        private void Fill(LifeWorld w)
        {
            Time = w.MissionTimeS;
            AswG.Now = Time; AswG.SarWorld.Now = Time; CapG.NowS = Time; SmartG.NowS = Time;
            AswG.Mem.Clear(); AswG.Mem.AddRange(w.Asw);
            CapG.All.Clear(); CapG.All.AddRange(w.Cap);
            SarG.Units.Clear(); foreach (var kv in w.Sar) SarG.Units[kv.Key] = kv.Value;
            SarG.RaftList.Clear(); SarG.RaftList.AddRange(w.Rafts);
            _smartLive = w.HasSmart;
            if (w.Torp != null) SmartG.Torp = w.Torp;
            _loadedId = w.Id;
        }

        /// <summary>The objects of the mission are destroyed: the fakes forget them (the controllers are not told).</summary>
        public TorpSnap Destroy()
        {
            var torp = SmartG.Torp;   // a load of the save brings the torpedo back (LifeWorld.Torp)
            AswG.Mem.Clear(); CapG.All.Clear(); SarG.Units.Clear(); SarG.RaftList.Clear(); SarG.Routes.Clear();
            SmartG.Torp = null; _smartLive = false; _loadedId = -1;
            return torp;
        }

        /// <summary>The game shows the mission <paramref name="w"/>. A load sets last_save before the objects change.</summary>
        public void Enter(LifeWorld w, double realT)
        {
            if (w.Id != _loadedId) Fill(w);
            if (HideLists) { AswG.Mem.Clear(); CapG.All.Clear(); }   // T-211: the game lists are still empty in the load frame
            if (w.SavePath != null) SetMarker(w.SavePath);
            M.Tick(w, () => Marker, () => w.MissionTimeS, realT);
        }

        /// <summary>T-211: the lists of the game are filled (the world of the loaded mission is placed).</summary>
        public void Unhide(LifeWorld w) { HideLists = false; AswG.Mem.Clear(); AswG.Mem.AddRange(w.Asw); CapG.All.Clear(); CapG.All.AddRange(w.Cap); }

        /// <summary>T-211: one sample of the gate; when it opens, the deferred restore runs in the order of GameApi.ApplyDeferredRestore (asw, then cap).</summary>
        public bool GateStep(bool loadScreen, int units, string sig, double gameT, double realT)
        {
            if (!Gate.Step(loadScreen, units, sig, gameT, realT)) return false;
            Applied.Add("asw"); Asw.ApplyDeferred(Time);
            Applied.Add("cap"); Cap.ApplyDeferred(Time);
            return true;
        }

        /// <summary>No mission object this frame (menu, or a blink).</summary>
        public void NoMission(double realT) { M.Tick(null, () => Marker, () => Time, realT); }

        // ---------------------------------------------------------------- commands of the player in the mission

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        /// <summary>The player's work in mission A: <paramref name="only"/> = one of asw|cap|sar|smart|identify, null = everything.</summary>
        public void Populate(string only = null)
        {
            if (only == null || only == "asw")
            {
                Must("asw group", Asw.Group(P("{\"op\":\"create\",\"uids\":[1,2]}")));
                Must("asw course", Asw.SearchCourse(P("{\"tactics\":[\"rows\"]}")));
            }
            if (only == null || only == "cap") Must("cap group", Cap.Group(P("{\"op\":\"create\",\"uids\":[11,12]}")));
            if (only == null || only == "sar") Must("sar ship", Sar.ShipCmd(P("{\"uid\":30,\"op\":\"start\",\"radius_nm\":10}")));
            if (only == null || only == "smart")
            {
                string ack; string e = Smart.Start(new SmartAttackArgs { Uid = SmartG.SubS.Uid, TargetId = 12 }, out ack);
                if (e != null) throw new InvalidOperationException("smart start: " + e);
            }
            if (only == null || only == "identify")
            {
                IdStore.Record(100, IdentifyResult.Answered, Time); IdStore.Record(101, IdentifyResult.Refused, Time); IdStore.Record(102, IdentifyResult.Silent, Time);
            }
        }

        public void SetFlags(LifeWorld w)
        {
            foreach (var u in w.FlagUnits) Flags.Set(u, DoctrineLevel.Unit, VbKeys.AutoEvade, true);
        }

        public bool FlagSet(object unit) { return Flags.Get(unit, DoctrineLevel.Unit, VbKeys.AutoEvade) == true; }

        private static void Must(string what, string r)
        {
            if (r != null && (AswController.IsErr(r) || r.StartsWith("!"))) throw new InvalidOperationException(what + ": " + r);
        }

        // ---------------------------------------------------------------- one second of the mod

        public void Run(double gameSeconds)
        {
            for (int i = 0; i < (int)Math.Round(gameSeconds); i++) Step();
        }

        /// <summary>Runs until the smart attack is on its leg (the torpedo is launched and on the wire); false = not reached in maxS.</summary>
        public bool RunToLeg(double maxS)
        {
            for (int i = 0; i < (int)maxS; i++) { if (SmartState() == "Leg") return true; Step(); }
            return SmartState() == "Leg";
        }

        private void Step()
        {
            if (!M.InMission) return;
            Time += 1;
            AswG.Advance(1); AswG.Now = Time;
            CapG.Advance(1); CapG.NowS = Time;
            SarG.Step(1); SarG.Now = Time; AswG.SarWorld.Now = Time;
            if (_smartLive) { SmartG.Frame++; SmartG.Advance(0.5); SmartG.NowS = Time - 0.5; SmartG.Frame++; SmartG.Advance(0.5); SmartG.NowS = Time; }
            else SmartG.NowS = Time;
            AswG.Tick(Asw);
            if (CapG.All.Count > 0 || Cap.View().Groups.Count > 0) CapG.Tick(Cap);
            for (int k = 0; k < 4; k++) Sar.FramePump();
            Sar.Tick(Time);
            // the pumps of the game layer: a new last_save marker -> the module writes its slot
            if (_capSeen && Marker != _capSave) { _capSave = Marker; if (Marker != null && Cap.View().Groups.Count > 0) Cap.PersistNow(); }
            if (Marker != null && Marker != _idSave) { _idSave = Marker; Id.OnSaveMarker(Marker, Time); }
            if (Marker != null && Marker != _smartSave) { _smartSave = Marker; Smart.OnSaveMarker(Marker, Time); }
            Smart.Pump(Time);
        }

        // ---------------------------------------------------------------- views

        public int ShipRuns() { return ((List<object>)Json.Parse(Sar.ShipsJson())).Count; }

        public int SmartActive()
        {
            var o = (Dictionary<string, object>)Json.Parse(Smart.LinkStateJson());
            return (int)(double)o["active"];
        }

        public string SmartState()
        {
            var o = (Dictionary<string, object>)Json.Parse(Smart.LinkStateJson());
            var l = (List<object>)o["attacks"];
            return l.Count == 0 ? "none" : (string)((Dictionary<string, object>)l[0])["state"];
        }

        public string Snapshot()
        {
            return "asw=" + Asw.View().Groups.Count + " sar=" + ShipRuns() + " cap=" + Cap.View().Groups.Count + " smart=" + SmartActive() + " id=" + IdStore.All.Count;
        }

        public string Files()
        {
            var l = new List<string>();
            if (Directory.Exists(SaveDir)) foreach (string f in Directory.GetFiles(SaveDir, "*", SearchOption.AllDirectories)) l.Add(f);
            l.Sort(StringComparer.Ordinal);
            return string.Join("\n", l.ToArray());
        }

        public void Dispose() { try { if (Directory.Exists(SaveDir)) Directory.Delete(SaveDir, true); } catch (Exception) { } }
    }
}
