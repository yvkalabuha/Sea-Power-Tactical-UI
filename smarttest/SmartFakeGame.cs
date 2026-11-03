using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>Stand-only fake of the game for SmartAttackController: a sub, one tracked target, one wire torpedo with simple kinematics.</summary>
    internal sealed class SmartFakeGame : ISmartAttackGame
    {
        public double NowS; public bool IsPaused; public string SavePath = "/saves/test.sav"; public string Dir;
        public int Frame;
        public SubSnap SubS = new SubSnap
        {
            Uid = 21, Name = "Los Angeles", Type = "SSN", Alive = true, IsSub = true, FreeWire = true, Pos = new SaVec(0, 0), DepthFt = 100, BottomFt = 2000, SpeedKn = 10,
            Telegraph = 3, TelegraphKn = new double[] { -5, 0, 6, 10, 15, 20, 25 }, LayerFt = 300, TileKey = "t", Preset = 2,
        };
        public TrackInfo Info = new TrackInfo { Id = 12, Relation = "hostile", Domain = "Surface", Cls = "DDG", State = "live", SameTile = true };
        public SaVec TargetPos = new SaVec(0, 10), TargetVel = new SaVec(0, 0);
        public double? TargetDepth;
        public bool TrackPresent = true, HitRemovesTrack = true, SampleNull, NoHitReport;
        public int HitCount;                                   // the game's weapon report on the track; -1 = the counter is off
        public List<NeutralTrack> NeutralList = new List<NeutralTrack>();
        public bool NeutralsThrow;
        public string Direct;                                  // null | neutral_near | neutral_check_failed
        public List<WireAmmo> Ammos = new List<WireAmmo>();
        public string AmmoErr;
        public string FireError;
        public double LaunchDelayS = 3;
        public double DiveFtPerS = 8;
        public bool IgnoreShift, IgnoreDepthOrders;
        public int DepthIgnoreCount;
        public Dictionary<int, double> PresetFt = new Dictionary<int, double> { { 1, 60 }, { 2, 150 }, { 3, 250 }, { 4, 400 } };
        public Dictionary<string, int> LaunchSpeeds = new Dictionary<string, int>();
        public List<string> Calls = new List<string>();
        public List<string> Events = new List<string>();
        public List<string> Messages = new List<string>();
        public List<string> Logs = new List<string>();
        public int ShiftCalls;
        public SaVec? LastShift;
        public bool LastShiftNull;

        // torpedo
        public TorpSnap Torp;                                  // null until launched
        public WireAmmo TorpAmmo;
        private bool _fired; private double _fireT; private string _fireAmmo; private ISet<int> _all = new HashSet<int>();
        private double _fuelFrac = 1.0;
        private SaVec _shift; private bool _direct = true;
        public int FoundFrame = -1, DepthFrame = -1, SpeedFrame = -1, LaunchRestoreFrame = -1;
        public double? DepthOrdered;
        public int WireDepthCalls;
        private bool _foundReported;

        public SmartFakeGame(WireAmmo a) { Ammos.Add(a); }

        // ---- interface -------------------------------------------------------------------------------------------------
        public double Now { get { return NowS; } }
        public bool Paused { get { return IsPaused; } }
        public string LastSavePath { get { return SavePath; } }
        public string SaveDir { get { return Dir; } }
        public SubSnap Sub(int uid) { return uid == SubS.Uid ? SubS : null; }
        public IList<WireAmmo> WireAmmoFor(int uid, int trackId, out string err) { err = Ammos.Count == 0 ? (AmmoErr ?? "no_wire_torpedo") : null; return Ammos; }
        public TrackInfo Track(int id) { return TrackPresent && id == Info.Id ? Info : null; }
        public TrackSample? Sample(int id)
        {
            if (!TrackPresent || id != Info.Id || SampleNull) return null;
            return new TrackSample { T = NowS, Pos = TargetPos, VelKn = TargetVel, DepthFt = TargetDepth };
        }
        public int HitsOn(int id) { return HitCount < 0 ? -1 : (id == Info.Id ? HitCount : 0); }
        public IList<NeutralTrack> Neutrals(int except) { if (NeutralsThrow) throw new InvalidOperationException("boom"); return NeutralList; }
        public string DirectCorridor(int uid, int trackId, out string hitsJson) { hitsJson = "[]"; return Direct; }
        public string SetTelegraph(int uid, int t) { Calls.Add("telegraph:" + t); SubS.Telegraph = t; return null; }
        public string SetOwnDepthPreset(int uid, int p) { Calls.Add("preset:" + p); SubS.Preset = p; SubS.OrderedDepthFt = PresetFt[p]; return null; }
        public string SetOwnDepthFt(int uid, double ft) { Calls.Add("depthft:" + ft); SubS.OrderedDepthFt = ft; return null; }
        public int GetLaunchSpeed(int uid, string ammo) { int v; return LaunchSpeeds.TryGetValue(ammo, out v) ? v : 1; }
        public void SetLaunchSpeed(int uid, string ammo, int index) { Calls.Add("launchspeed:" + index); LaunchSpeeds[ammo] = index; if (Torp != null) LaunchRestoreFrame = Frame; }
        public string Fire(int uid, int trackId, string ammo, bool confirm)
        {
            Calls.Add("fire:" + ammo + ":" + confirm);
            if (FireError != null) return FireError;
            _fired = true; _fireT = NowS; _fireAmmo = ammo;
            return null;
        }
        public ISet<int> WeaponIds(int uid) { return new HashSet<int>(_all); }
        public TorpSnap FindNewTorpedo(int uid, string ammo, ISet<int> before)
        {
            if (Torp == null || before.Contains(Torp.Id) || _foundReported) return null;
            _foundReported = true; FoundFrame = Frame;
            return Torp;
        }
        public TorpSnap Torpedo(int id) { return Torp != null && Torp.Id == id ? Torp : null; }
        public void SetWireDepth(int id, double ft)
        {
            WireDepthCalls++; DepthFrame = Frame; Calls.Add("wiredepth:" + ft);
            if (IgnoreDepthOrders && DepthIgnoreCount-- > 0) return;
            DepthOrdered = ft; Torp.OrderedDepthFt = ft;
        }
        public void SetWireSpeed(int id, int index) { SpeedFrame = Frame; Calls.Add("wirespeed:" + index); Torp.SpeedIndex = index; }
        public void SetAimShift(int id, SaVec? shift)
        {
            ShiftCalls++; LastShiftNull = !shift.HasValue; LastShift = shift;
            if (shift.HasValue) { _shift = shift.Value; _direct = false; } else { _shift = new SaVec(0, 0); _direct = true; }
            Calls.Add("shift:" + (shift.HasValue ? "v" : "null"));
        }
        public void RememberField(int uid, string key, string current) { Calls.Add("remember:" + key + "=" + current); }
        public void NoteFieldSet(int uid, string key, string value) { Calls.Add("note:" + key + "=" + value); }
        public string FieldNow(int uid, string key)
        {
            if (key == "telegraph") return SubS.Telegraph.ToString();
            if (key == "sa_depth") return SubS.Preset.HasValue ? SubS.Preset.Value.ToString() : null;
            return null;
        }
        public void Event(string name, string dataJson) { Events.Add(name + " " + dataJson); }
        public void Message(string text) { Messages.Add(text); }
        public void Log(string line) { Logs.Add(line); }

        // ---- simulation --------------------------------------------------------------------------------------------------
        public void Advance(double dt)
        {
            NowS += dt;
            TargetPos = TargetPos + TargetVel * (dt / 3600.0);
            // own boat: speed follows the telegraph (1 kn/s), depth follows the ordered depth
            double want = SubS.TelegraphKn[SubS.Telegraph + 1];
            if (SubSpeedOverride.HasValue) SubS.SpeedKn = SubSpeedOverride.Value;
            else SubS.SpeedKn += Math.Max(-dt, Math.Min(dt, want - SubS.SpeedKn));
            if (SubS.OrderedDepthFt.HasValue)
                SubS.DepthFt += Math.Max(-DiveFtPerS * dt, Math.Min(DiveFtPerS * dt, SubS.OrderedDepthFt.Value - SubS.DepthFt));
            if (_fired && Torp == null && NowS - _fireT >= LaunchDelayS)
            {
                TorpAmmo = Ammos.First(x => x.File == _fireAmmo);
                Torp = new TorpSnap
                {
                    Id = 100, Alive = true, OnWire = true, Pos = SubS.Pos, CourseDeg = SaVec.Bearing(SubS.Pos, TargetPos), SpeedKn = TorpAmmo.SpeedsKn[0],
                    RangeLeftNm = TorpAmmo.RangesNm[0], MinRunFt = 40, SpeedIndex = 0, OrderedDepthFt = 20, Ammo = _fireAmmo,
                };
                _all.Add(Torp.Id);
                _shift = new SaVec(0, 0); _direct = true;
            }
            if (Torp != null && Torp.Alive) MoveTorp(dt);
        }

        public double? SubSpeedOverride;

        private void MoveTorp(double dt)
        {
            var a = TorpAmmo;
            double kn = a.SpeedsKn[Torp.SpeedIndex];
            Torp.SpeedKn = kn;
            if (SubS.SpeedKn > a.WireBreakKn + 1 && Torp.OnWire) { Torp.OnWire = false; Torp.WireLost = true; }
            double dist = SaVec.Dist(Torp.Pos, TargetPos);
            if (dist < a.ActivateRadiusNm && !Torp.SeekerActive) { Torp.SeekerActive = true; Torp.Terminal = true; }
            SaVec aimTarget = (Torp.SeekerActive || IgnoreShift || _direct) ? TargetPos : TargetPos + _shift;
            SaVec aim = SmartAttackMath.Intercept(Torp.Pos, kn, aimTarget, TargetVel, a.SeekerFovDeg);
            double want = SaVec.Bearing(Torp.Pos, aim);
            double diff = (want - Torp.CourseDeg + 540.0) % 360.0 - 180.0;
            double maxTurn = 30 * dt;
            Torp.CourseDeg = (Torp.CourseDeg + Math.Max(-maxTurn, Math.Min(maxTurn, diff)) + 360.0) % 360.0;
            double step = kn * dt / 3600.0;
            Torp.Pos = Torp.Pos + SaVec.FromBearing(Torp.CourseDeg, step);
            _fuelFrac -= step / SmartAttackMath.RangeAtNm(a, kn);
            Torp.RangeLeftNm = Math.Max(0, _fuelFrac) * SmartAttackMath.RangeAtNm(a, kn);
            if (SaVec.Dist(Torp.Pos, TargetPos) < 0.06)
            {
                Torp.Alive = false;
                if (HitCount >= 0 && !NoHitReport) HitCount++;
                if (HitRemovesTrack) TrackPresent = false;
            }
            else if (_fuelFrac <= 0) Torp.Alive = false;
        }
    }
}
