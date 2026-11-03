// T-112 §3.3 CapController (logic on ICapGame; no Unity, no reflection). Owner: stream D.
// One tick per game second. Groups are stateful, the relief planner (CapMath.PlanRelief) is stateless and repeats its actions until the
// controller has recorded the deck request (PendingKey).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoiceBotBridge
{
    internal interface ICapGame
    {
        double Now { get; }
        double TimeScale { get; }
        string LastSavePath { get; }
        string SaveDir { get; }
        IList<CapBird> Birds();                                   // own Aircraft/VTOL of admitted types
        string TypeAdmission(string type);                        // CapMath.TypeAdmission on game data, cached per type
        List<LoadoutOption> Loadouts(int deckUid, string type);   // IAirOps.Loadouts (Weapons filled)
        CapAmmo Ammo(string name);
        IList<CapContact> AirContacts();                          // player-known air tracks, Hostile flag
        GeoPt? UnitPos(int uid);                                  // anchor ship, deck
        string DeckTimingsMode();
        List<AirDeckTask> DeckTasks(int deckUid);
        // T-112 coordinator follow-up (additive): the Birds() uid that represents the launch task <key> (the planner's hangar row uid changes once the task exists); 0 = unknown
        int TaskBirdUid(int deckUid, string key);
        // live 0.28.3 (additive): the flight deck is knocked out (IAirOps.DeckInoperable): the game cleared its tasks and runs none until it is repaired
        bool DeckDown(int deckUid);
        // T-204: a RecoveryTask of this type is still on the deck (the landed unit is gone, its cool-down row is not listed yet)
        bool Landing(int deckUid, string type);
        string Patrol(int uid, IList<GeoPt> pts, int altFt, int keepStep = 0);   // keepStep > 0 (T-257): the telegraph Patrol puts back afterwards, instead of the one it reads   // setWaypointTask, mod waypoint noted in ControlMemory
        int WaypointsLeft(int uid);
        string SetRoe(int uid, string roe, out string previous);  // RoeCmd path + ControlMemory.RememberRoe
        string Rtb(int uid);                                      // setOrder(ReturnToBase, homeBase)
        string BreakPursuit(int uid);                             // leash: clear _objectToDestroy, route back
        string ClearSurfaceTarget(int uid);                       // T-175: drop the fire task on a surface / ground target ("unsupported" = member missing -> ROE hold fallback)
        string Restore(int uid);                                  // as "Return control"
        bool TakenByPlayer(int uid);                              // ADR-0036 §11
        string TakenDetail(int uid);                              // T-317: "why=route st=... dev_nm=... sel=0 win=1" of the last deviating TakenByPlayer check; null = none
        bool TakeAiDrift(int uid);                                // T-317: the last check rebased a route/ROE (ai_drift): the controller drops m.RouteSig; read-and-reset
        bool AiWindow(int uid);                                   // T-317: the AI combat window of the aircraft is open and the player did not select it (DashTick)
        bool InGameCapZone(int uid);                              // PatrolZone._attachedUnits
        bool UnitAlive(int uid);                                  // 0.27.8: the own unit still exists (a leaving member is never dropped while it does)
        // ADR-0040 D2 (additive): speed steps (telegraphs) of an aircraft
        int Throttle(int uid);                                    // current step id, -1 unknown
        string SetThrottle(int uid, int step);                    // null = ok
        IList<CapStep> ThrottleSteps(int uid);                    // ascending by speed, afterburner excluded; empty = no dash
        // T-333 (ADR-0129, additive): the intercept target's track. IcptSeed caches its Vehicle (trackId -> Vehicle, own cache), TrackGone reads it: "destroyed" | "lost" (alive, not tracked) | null (not seeded), IcptDrop forgets it
        void IcptSeed(int trackId);
        string TrackGone(int trackId);
        void IcptDrop(int trackId);
        void Event(string name, string dataJson);
        void Log(string line);                                    // the controller adds the "[Cap] " prefix
    }

    // ---- window model (View) -------------------------------------------------------------------------------------------------------
    internal sealed class CapMemberView
    {
        public int Uid, Slot, AamNow, AamStart, ShipUid, HangarK, HangarN; public string Name, Type, Where, Reason, Loadout, TypeLabel, ShipName, Serial;   // HangarK/HangarN: T-186 hangar slot k and Numbers of the pair (0 = not a slot)
        public bool Air;   // in the air (a unit on the map); false = a deck row
        public double ReserveNm = -1, LeaveInS = -1, ReadyInS = -1, FuelPct = -1, TakeoffInS = -1;   // TakeoffInS: ready + launch delay of a launch that is allowed, else -1
        public bool ReadyKnown = true;   // false: ReadyInS is a planning guess, the window shows no time
        public bool Alert;               // T-175: a machine of an alert group (T-179)
        public string AlertState;        // T-179: null (stands) | scramble | return
        public string LentFrom;          // T-179: lent by this alert group (patrol side)
        public bool AutoFill;            // T-179: taken from the deck by "Fill from deck"
        public string Radar;             // T-241: null | on | player | untouched | wait
        // T-333 (ADR-0129): the zone intercept of the bird. Icpt = phase (guide | handed | fight | lost), null = none; Ff = seconds the friendly-fire guard holds its fire (-1 = not holding)
        public string Icpt; public int IcptTarget, IcptRound; public double IcptEtaS = -1, IcptLostS = -1, Ff = -1;
        public bool HasIp; public double IpLat, IpLon;   // the point the bird is guided to (map line)
    }
    internal sealed class CapCandidateView { public int Uid, ShipUid, HangarK, HangarN; public string Name, Type, Where, Reason, TypeLabel, ShipName, Serial, OwnerWindow, LoadoutText; public bool Air; }   // T-325: LoadoutText = the loadout shown in the Available table ("" = none)   // T-186: HangarK/HangarN of a slot row; OwnerWindow "cap"|"asw" when Reason is in_group
    internal sealed class CapGroupView
    {
        public CapGroupLine Line; public string Roe, Anchor; public double[] Rect; public int N, K; public bool Ordered;
        public bool AutoName = true;   // T-326: the name is automatic (empty field in the window = back to it)
        // T-179 / ADR-0079: the kind of the order (patrol | alert), the settings of an alert group, "accept alert aircraft" of a patrol, the counters of an alert group
        public string Kind = "patrol"; public CapAlertSpec AlertSpec; public bool AcceptAlert = true;
        public int PrepN; public double PrepInS = -1;   // T-204: alert members preparing / cooling and the nearest ready time (-1 = unknown)
        public int ReadyNow, Airborne; public List<KeyValuePair<int, string>> Lent = new List<KeyValuePair<int, string>>();   // Lent: uid -> patrol group
        public string ScrambleBlock;          // null = "Scramble now" can be pressed; else the reason (no_ready | not_alert)
        public bool AirOnly = true;           // T-175: the group's [Cap] AirOnly (the window warns when it is off)
        public double AltFt;                  // T-166: patrol altitude of the group, ft (0 = the game's default)
        public int OrderSeq;                  // T-166: grows with every order / parameter change of the group (the window re-reads its fields)
        public bool HasPolar; public double PolarBearing, PolarDistNm, PolarSizeNm, PolarWNm;   // the polar form of the order, when it was given so
        public Dictionary<string, string> Loadouts = new Dictionary<string, string>();
        public CapInterceptSpec Icpt;         // T-333: the intercept settings of a patrol group
        public int IcptBirds, IcptTargets;    // T-333: birds that intercept now and the number of targets they serve
        public double CoverNm;                // T-235: cover radius of the group (nm); 0 = not computed yet
        public List<CapLane> Lanes;           // T-235: the lanes the controller gives the birds (patrol order); the overlay draws these
        public List<CapMemberView> Members = new List<CapMemberView>();
    }
    internal sealed class CapView
    {
        public List<CapGroupView> Groups = new List<CapGroupView>();
        public List<CapCandidateView> Candidates = new List<CapCandidateView>();
        public bool RestorePending;
        public bool RestoreWaiting;   // T-211: the file is read, the groups wait for the world after a load (ADR-0088)
    }

    internal sealed partial class CapController
    {
        private const string Win = "cap";
        private const double BanS = 600, LeashGapS = 30, RtbAgainGapS = 180;

        private sealed class Mem
        {
            public int Uid, DeckUid, Slot = -1, PlanSlot = -1, AamStart, LongStart, Fail;
            public int HangarK, HangarN;   // T-186: k of the hangar slot the member sits on (0 = none: an order, a bird in the air, a fake row) and Numbers of the pair
            public long JoinedAt;          // T-186: global "taken at" number of the book (0 = from a save)
            public double KeyAt = -1;      // T-186: when the order was handed over (the task row is awaited OwnRowWaitS)
            public string Name, Type, Where = "hangar", Reason, Key, Leaving, Postponed, PrevRoe, RouteSig, Loadout, TypeLabel, ShipName, Serial;
            public bool Pending;   // a deck request of this member is submitted and not answered yet (ADR-0042)
            public bool Airborne, OnStation, RoeSet, Allowed, ReliefEvt, TcWarned, NoFuelWarned, Lost, Dash, TaskRowLogged, Recovered, UnlistedLogged;
            public double LastRtb = -1e9; public string RtbErr, RtbHold;
            public double LastStationThr = -1e9, StationSetAt = -1e9, StationLogAt = -1e9, FromSaveAt = -1e9;   // T-257: last check, when the mod set the station step, last 'station speed' line, restored from a file
            public int StationSetStep;      // T-257: that step
            public bool WasCombat;          // T-257: in combat at the last check
            public string StationLogText, StationErr;
            public double LastCircleRouteAt = -1e9;   // T-250: when the last circle route was handed over
            public int PrevThrottle = -1, DashStep = -1;
            public double AirSince = -1, LastLeash = -1e9, ReserveNm = -1, LeaveInS = -1, ReadyInS = -1, FuelPct = -1, TakeoffInS = -1;
            public bool ReadyKnown = true;
            public int AamNow;
            /// <summary>Launched by the group's own deck order: the largest reserve seen since take-off is the full-tank path with this loadout.</summary>
            public bool OwnLaunch; public double TopReserveNm = -1, GameMaxNm = -1;
            /// <summary>Correction 2026-10-22: the game's map ring (nm, -1 unknown) and since when the fuel threshold holds (-1 = not below it).</summary>
            public double RingNm = -1, FuelLowSince = -1, ReserveOkNm = -1;
            public string FuelLowBy;   // T-315: floor | margin | ring while the fuel threshold holds
            /// <summary>Airborne uids seen while this member still waited on deck; a new airborne bird of the same type that is not in it is the launched aircraft (uid changes at take-off).</summary>
            public HashSet<int> KnownAir; public double MissingS = -1;
            // T-175: the alert pair (CapController.Alert.cs) and the surface-target ROE fallback
            public bool Alert, SurfaceHold, Handed, AutoFill, Barrage;
            public string LentFrom;                                 // T-179: this bird is lent to the patrol group it sits in by that alert group
            public bool LentOrphan;                                 // T-195: the lending alert group ended; the bird stays with the patrol until it lands
            public List<int> RaidIds;                               // contacts of the raid this bird was sent to
            public string AlertState, AlertRaid;                    // AlertState: null (waiting) | scramble | return
            public double LastClear = -1e9, LastGuide = -1e9, FarSince = -1;
            public int AlertThrottle = -1;                          // the speed step before the scramble dash
            public int RestoreDeck = -1;      // T-218: -1 = not a restored deck-row member; 0 = any deck (old file); >0 = ship uid (CapController.Restore.cs)
            public string RestoreWhere;       // T-218: saved row state (rank 0 of CapMath.RestoreRowRank)
            public string RowLoadout;         // T-218: loadout of the deck row the member sits on (saved; Loadout is the one of the flight)
            public double RestoredAt = -1; public bool RestoreWaitLogged;   // T-211: restored from a file at this time (-1 = found in the game lists since): not dropped for RestoreGraceS
            public string AlertKey; public RaidMemo Tgt; public double BindRestoredAt = -1;   // T-219 (ADR-0090): AirGuard key at the scramble, last target point, when the saved binding came back (-1 = none)
            public Icpt Icpt;                 // T-333 (CapController.Intercept.cs): the zone intercept of this bird, null = none
            public double LeashStartNm = -1, LeashT0 = -1; public bool LeashIn;   // T-197: distance to the protected unit at the first alert tick in the air; the bird was inside the leash once
        }

        private const double AdoptGraceS = 15;
        private const double RestoreGraceS = 20;   // T-211 (ADR-0088): a restored member that the game lists do not show yet is not dropped this long
        private const double LandWaitS = 180;   // T-204: max wait for the cool-down row while the deck still recovers the type

        private sealed class Grp
        {
            public bool AutoName = true;   // T-326
            public string Name; public int Seq, N, K = -1; public string Roe; public CapZone Zone = new CapZone();
            public bool Ordered, Gap, Prio, AnchorWarned; public double GapS = -1; public int OrderSeq;
            public Dictionary<string, string> Loadouts = new Dictionary<string, string>();
            public CapParams P; public List<Mem> Members = new List<Mem>(); public CapThreatLatch Latch = new CapThreatLatch();
            public HashSet<string> Warned = new HashSet<string>(); public List<string> Warns = new List<string>();
            public Dictionary<string, double> Banned = new Dictionary<string, double>();
            public bool PrevGap;
            public Dictionary<int, CapPhaseFix> Phase; public double LastPhaseLog = -1e9;   // T-250: phase plan of the birds on the circle, last 'phase' log line
            public double CoverNm; public string CoverSrc = "fallback"; public string LanesSig; public int AutoKLogged = -1;   // T-235: cover radius of the group (ADR-0097), where it comes from, last logged lane shape
            // T-179 / ADR-0079: kind patrol | alert, settings of the alert kind, "accept alert aircraft" of a patrol, the contacts of served raids, warnings once per raid
            public string Kind = "patrol"; public CapAlertSpec A; public bool AcceptAlert = true;
            public List<LentRec> Lent = new List<LentRec>();   // alert group: its birds that fly in a patrol group
            public HashSet<int> Served = new HashSet<int>();
            public HashSet<string> ServedRaids = new HashSet<string>(); public Dictionary<int, double> RaidSeen = new Dictionary<int, double>();   // T-197: AirGuard raid keys served; first sight of a track
            public HashSet<string> AlertWarned = new HashSet<string>();
            public List<RaidMemo> Memos = new List<RaidMemo>();   // T-219 (ADR-0090): place memory of the served raids
            public List<AlertRaid> RaidsNow; public double RaidsAt = -1e9;   // T-219: raids of the last alert tick (one assembly per group and tick)
            public CapInterceptSpec I; public bool IcptOffLogged; public HashSet<int> IcptWaitLogged = new HashSet<int>();   // T-333: intercept settings, log-once marks
            public Dictionary<string, OrderGate> Gates = new Dictionary<string, OrderGate>();   // T-187: refused deck orders, key deckUid + "|" + type
        }

        /// <summary>T-187: the pause after a refused deck order of a pair (deck, type): <c>WaitS</c> from <c>At</c>, or sooner when the hangar of the pair grew.</summary>
        private sealed class OrderGate { public string Err; public double At, WaitS; public int Fails, Free; public bool Retry; }

        private readonly ICapGame _g;
        private readonly CapParams _p;
        private readonly AirOwnership _own;
        private readonly List<Grp> _groups = new List<Grp>();
        private readonly HashSet<string> _ourKeys = new HashSet<string>();
        private readonly Dictionary<string, List<LoadoutOption>> _losCache = new Dictionary<string, List<LoadoutOption>>();
        /// <summary>Test hook: log the group tick order.</summary>
        internal bool TraceOrder = false;
        private IList<CapBird> _lastBirds;
        private readonly HashSet<int> _strikeLogged = new HashSet<int>();   // T-325: one "refuse strike_loadout" line per uid
        private readonly Dictionary<int, string[]> _viewLoad = new Dictionary<int, string[]>();   // T-325: uid -> {reason, loadout text}; rebuilt with _lastBirds
        private double _viewLoadAt = -1e9;

        /// <summary>T-325: "strike_loadout" when the aircraft carries (or its variant carries) strike / anti-ship / ASW weapons; null otherwise or when it cannot be read.</summary>
        private string StrikeWhy(CapBird b)
        {
            string r = null;
            try { r = CapMath.StrikeExclusion(b, _g.Ammo, _p.StrikeMixedAamShare); } catch (Exception) { r = null; }
            if (r != null && _strikeLogged.Add(b.Uid))
            {
                string sum = ""; try { sum = CapMath.LoadoutSummary(b.LoadWeapons ?? b.Weapons, _g.Ammo, 8); } catch (Exception) { }
                Log("refuse strike_loadout uid=" + b.Uid + " type=" + b.Type + " loadout=" + (b.Loadout ?? "-") + " weapons=" + sum);
            }
            return r;
        }

        private string LoadoutTextOf(CapBird b)
        {
            if (b.Where == "hangar" || b.Where == "deck_cool") return "";
            string t = "";
            try { t = CapMath.LoadoutSummary(b.Weapons, _g.Ammo); } catch (Exception) { t = ""; }
            if (t.Length == 0) t = CapWindowModel.LoadoutText(b.Loadout, b.LoadoutLabel);
            return t ?? "";
        }
        private double _birdsAt = -1e9;
        private CapSaveData _pending;
        private CapSaveData _deferred;   // T-211: the checked file waits for the gate after a load (ApplyDeferred)
        private string _deferredMode;
        private int _no;
        private double _now;

        public CapController(ICapGame g, CapParams p, AirOwnership own) { _g = g; _relief = new AirRelief(Win, Log, ""); _p = p ?? new CapParams(); _own = own ?? new AirOwnership(); _own.Slots.Register(Win, SlotClaims, ApplySlot, SlotNumbers); }

        /// <summary>The ownership object shared with the ASW window (group numbers, one owner per machine); the air tick takes it from here.</summary>
        public AirOwnership Ownership { get { return _own; } }

        /// <summary>T-119 (ADR-0042): the deck broker. Null = no deck requests are submitted (one log line a minute).</summary>
        public IAirDeck Deck { get { return _relief.Deck; } set { _relief.Deck = value; } }
        private readonly AirRelief _relief;

        /// <summary>Submits a deck request; <paramref name="body"/> runs when the broker answers (same frame). False = not submitted.</summary>
        private bool SubmitDeck(Grp g, DeckRequest r, Action<DeckResult> body)
        {
            if (g == null) return false;
            return _relief.Submit(null, g.Name, () => new AirGroupPrio { Name = g.Name, Order = "cap", Window = Win, HasContact = false, WindowIndex = _own.GroupSeq(Win, g.Name), CapGapUnderThreat = g.Prio }, r, _now, body);
        }

        /// <summary>T-257: a patrol bird on station outside combat flies the patrol step (the game AI leaves the combat step after an engagement, Patrol keeps it).</summary>
        private void StationThrottle(Grp g, Mem m, CapBird b, double now)
        {
            if (g.P.StationSpeed != "cruise" || g.Kind != "patrol" || m.Alert || m.Dash || m.Leaving != null) return;
            // N5: the combat mark is set on every bird tick (cheap), so a fight shorter than the check interval still gives after_combat
            bool fighting = b.Engaged || b.HasTarget || b.Evading || m.Postponed == "combat";
            if (fighting) m.WasCombat = true;
            if (now - m.LastStationThr < g.P.StationThrottleCheckS) return;
            m.LastStationThr = now;
            if (fighting) return;
            int cur = -1, st = -1; IList<CapStep> steps = null; string src = null;
            try { cur = _g.Throttle(m.Uid); steps = _g.ThrottleSteps(m.Uid); st = CapMath.StationStep(steps, out src); } catch (Exception) { }
            if (cur < 0 || st < 0 || !CapMath.FasterThan(steps, cur, st)) { m.WasCombat = false; return; }
            string err = null;
            try { err = _g.SetThrottle(m.Uid, st); } catch (Exception e) { err = e.GetType().Name; }
            if (err != null)
            {
                if (err != m.StationErr) { m.StationErr = err; Log("station speed failed uid=" + m.Uid + " err=" + err); }
                return;
            }
            m.StationErr = null; m.StationSetStep = st; m.StationSetAt = now;
            double kc = 0, ks = 0; foreach (var x in steps) { if (x.Step == cur) kc = x.Kn; if (x.Step == st) ks = x.Kn; }
            string reason = m.WasCombat ? "after_combat" : (now - m.FromSaveAt < 120 ? "restored" : "station");
            string text = "step " + cur + "->" + st + " kn=" + F(kc) + "->" + F(ks) + " src=" + src + " reason=" + reason + " group=" + g.Name;
            // flip-flop guard: if something puts the old step back, the same line is not repeated sooner than StationLogGapS
            if (text != m.StationLogText || now - m.StationLogAt >= g.P.StationLogGapS) { Log("station speed uid=" + m.Uid + " " + text); m.StationLogText = text; m.StationLogAt = now; }
            m.WasCombat = false;
        }

        /// <summary>Facts of every group for the broker: the tier mark and the decks the members still launch from.</summary>
        private void DeclareDecks()
        {
            if (Deck == null) return;
            var prio = DeckPrio();
            for (int i = 0; i < _groups.Count; i++)
            {
                var decks = new HashSet<int>();
                foreach (var m in _groups[i].Members) if (!m.Airborne && m.DeckUid != 0) decks.Add(m.DeckUid);
                Deck.Declare(Win, prio[i], decks);
            }
        }

        // ================================================= helpers ====================================================================
        private void Log(string s) { _g.Log("[Cap] " + s); }
        private void Dbg(string s) { if (_p.Debug) _g.Log("[Cap] " + s); }
        private static string F(double d) { return d.ToString("0.#", CultureInfo.InvariantCulture); }
        private static string F3(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }   // T-337: a fraction 0..1 must not print as "0"
        private static string Q(string s) { return Json.Str(s ?? ""); }
        private static bool Deckish(string w) { return w == "deck_ready" || w == "deck_prep" || w == "deck_cool" || w == "hangar"; }
        private Grp Find(string name) { foreach (var g in _groups) if (g.Name == name) return g; return null; }
        private static Mem MemOf(Grp g, int uid) { foreach (var m in g.Members) if (m.Uid == uid) return m; return null; }
        private Grp GroupOf(int uid, out Mem m)
        {
            foreach (var g in _groups) { m = MemOf(g, uid); if (m != null) return g; }
            m = null; return null;
        }
        private static string Ack(string group, string extra)
        {
            return "{\"ok\":true,\"group\":" + Q(group) + (extra == null ? "" : "," + extra) + "}";
        }

        private void Evt(string name, string json) { try { _g.Event(name, json); } catch (Exception) { } }

        private Dictionary<int, CapBird> Index(IList<CapBird> birds)
        {
            var d = new Dictionary<int, CapBird>();
            if (birds != null) foreach (var b in birds) if (b != null) d[b.Uid] = b;
            return d;
        }

        private void Warn(Grp g, string code, string type = null, string extra = null)
        {
            string key = code + "|" + type;
            if (!g.Warned.Add(key)) return;
            if (!g.Warns.Contains(code)) g.Warns.Add(code);
            Log("warn " + code + " group=" + g.Name + (type != null ? " type=" + type : ""));
            Evt("cap_warn", "{\"group\":" + Q(g.Name) + ",\"code\":" + Q(code) + (type != null ? ",\"type\":" + Q(type) : "") + (extra != null ? "," + extra : "") + "}");
        }

        private void ClearWarn(Grp g, string code)
        {
            g.Warns.Remove(code);
            g.Warned.RemoveWhere(k => k.StartsWith(code + "|", StringComparison.Ordinal));
        }

        // ---- T-187: the order gate (a refused deck order is not repeated every tick) ----
        /// <summary>False = the pair is paused after a refusal. The pause ends by time (AirRelief.OrderBackoffS) or when the hangar of the pair grew.</summary>
        private bool OrderDue(Grp g, int deckUid, string type, string who)
        {
            OrderGate og;
            if (!g.Gates.TryGetValue(deckUid + "|" + type, out og)) return true;
            if (_now >= og.At && _now - og.At < og.WaitS && FreeHangar(deckUid, type) <= og.Free) return false;
            if (!og.Retry) { og.Retry = true; Log("order retry " + who + " type=" + type + " group=" + g.Name + " after " + F(_now - og.At) + " s (was " + og.Err + ")"); }
            return true;
        }

        private void OrderFailed(Grp g, int deckUid, string type, string err, string who)
        {
            string gk = deckUid + "|" + type;
            OrderGate og;
            if (!g.Gates.TryGetValue(gk, out og)) { og = new OrderGate(); g.Gates[gk] = og; }
            bool first = og.Err != err;
            og.Fails++; og.Err = err; og.At = _now; og.Retry = false; og.Free = FreeHangar(deckUid, type);
            og.WaitS = AirRelief.OrderBackoffS(err, og.Fails);
            if (first) Log("order launch failed " + who + " " + err + " (" + AirRelief.OrderErrText(err) + "; next try in " + F(og.WaitS) + " s)");
            Warn(g, err == "no_airframes" ? "no_reserve_deck" : "launch_failed", type,
                 "\"err\":" + Q(err) + ",\"text\":" + Q(AirRelief.OrderErrText(err)) + ",\"retry_s\":" + F(og.WaitS));
        }

        private void OrderOk(Grp g, int deckUid, string type)
        {
            if (!g.Gates.Remove(deckUid + "|" + type) || g.Gates.Count > 0) return;
            ClearWarn(g, "no_reserve_deck"); ClearWarn(g, "launch_failed");
        }

        /// <summary>Free hangar rows of the pair in the last Birds() of the tick.</summary>
        private int FreeHangar(int deckUid, string type)
        {
            int n = 0;
            if (_lastBirds != null) foreach (var b in _lastBirds) if (b != null && b.Where == "hangar" && b.DeckUid == deckUid && b.Type == type) n++;
            return n;
        }

        /// <summary>T-187: a deck row of a task made by the game itself (FlightDeck_ReadyUpTasks, not ours) is taken as the member's request: its key is
        /// allowed like ours, a new order is never made for it. The key stays out of <c>_ourKeys</c>: it is not aborted and not saved.</summary>
        private void AdoptRowKey(Grp g, Mem m, CapBird b, Dictionary<int, List<AirDeckTask>> cache)
        {
            if (m.Key != null || m.Pending || m.Airborne || b.PendingKey == null || _ourKeys.Contains(b.PendingKey)) return;
            List<AirDeckTask> tasks;
            if (!cache.TryGetValue(b.DeckUid, out tasks)) { try { tasks = _g.DeckTasks(b.DeckUid); } catch (Exception) { tasks = null; } cache[b.DeckUid] = tasks; }
            if (tasks == null) return;
            foreach (var x in tasks)
                if (x.Key == b.PendingKey)
                {
                    if (x.Ours) return;
                    m.Key = x.Key; m.Allowed = false;
                    Log("adopt deck task uid=" + m.Uid + " key=" + m.Key + " ours=false group=" + g.Name);
                    return;
                }
        }

        private List<LoadoutOption> RawLoadouts(int deck, string type)
        {
            string k = deck + "|" + type; List<LoadoutOption> l;
            if (_losCache.TryGetValue(k, out l)) return l;
            try { l = _g.Loadouts(deck, type); } catch (Exception) { l = null; }
            _losCache[k] = l;
            return l;
        }

        // Loadouts of the deck with the options that failed "no_ammo" recently marked unavailable.
        private Func<int, string, IList<LoadoutOption>> DeckLos(Grp g)
        {
            return (deck, type) =>
            {
                var raw = RawLoadouts(deck, type);
                if (raw == null || g.Banned.Count == 0) return raw;
                var res = new List<LoadoutOption>();
                foreach (var lo in raw)
                {
                    double until;
                    bool ban = g.Banned.TryGetValue(deck + "|" + type + "|" + lo.Name, out until) && until > _now;
                    res.Add(!ban ? lo : new LoadoutOption { Name = lo.Name, Display = lo.Display, Capacity = lo.Capacity, ReadyUpMin = lo.ReadyUpMin,
                                                           CoolDownMin = lo.CoolDownMin, Available = false, Roles = lo.Roles, Weapons = lo.Weapons });
                }
                return res;
            };
        }

        private double NearestHostileTo(IList<CapContact> c, GeoPt pos)
        {
            double best = double.MaxValue;
            if (c == null) return best;
            foreach (var k in c) if (k != null && k.Hostile) best = Math.Min(best, AswMath.DistNm(pos, new GeoPt(k.Lat, k.Lon)));
            return best;
        }

        // ================================================= commands ===================================================================
        private string TryAdd(Grp g, int uid, IDictionary<int, CapBird> birds, out string reason)
        {
            int took;
            return TryAdd(g, uid, birds, out reason, out took);
        }

        /// <summary>T-328: the admission rules of an add, without side effects (TryAdd and the ring's AdmitWhy share them). null = ok, else the reason code.
        /// A unit owned by another group is "in_group:X" unless that owner is (ignoreWin, ignoreGroup), the group the unit leaves in a move.
        /// took = the uid the member would get (a taken hangar slot is replaced by the free slot of the same pair); use = its bird.</summary>
        private string AdmitCore(Grp g, int uid, IDictionary<int, CapBird> birds, string ignoreWin, string ignoreGroup, out int took, out CapBird use)
        {
            took = uid; use = null;
            CapBird b;
            if (!birds.TryGetValue(uid, out b)) return "not_found";
            string adm = _g.TypeAdmission(b.Type);
            if (adm != null) { Dbg("hidden " + adm + " uid=" + uid + " type=" + b.Type); return "hidden"; }
            if (_g.InGameCapZone(uid)) return "game_cap_zone";
            if (_g.TakenByPlayer(uid)) return "player";
            if (StrikeWhy(b) != null) return "strike_loadout";   // T-325
            if (b.HangarK > 0 && MemOf(g, uid) == null && !SlotFree(b))
            {
                string ow; string o = _own.Owner(uid, out ow);
                if (!AirOpsMath.TakeFreeWhenTaken) return "in_group:" + (o ?? "?");
                var alt = FreeSlot(b.DeckUid, b.Type, birds.Values);
                if (alt == null) return "hangar_taken";
                b = alt; took = alt.Uid;
            }
            string ow2; string owner = _own.Owner(took, out ow2);
            if (owner != null && !(ow2 == Win && owner == g.Name) && !(ignoreGroup != null && ow2 == ignoreWin && owner == ignoreGroup)) return "in_group:" + owner;
            use = b;
            return null;
        }

        /// <summary>took = the uid the member got: a taken hangar slot is replaced by the free slot of the same pair (ADR-0082 s.3, Q2).</summary>
        private string TryAdd(Grp g, int uid, IDictionary<int, CapBird> birds, out string reason, out int took)
        {
            CapBird b;
            reason = AdmitCore(g, uid, birds, null, null, out took, out b);
            if (reason != null) return null;
            string own = _own.Claim(took, Win, g.Name);
            if (own != null) { reason = own; return null; }
            if (MemOf(g, took) == null)
                g.Members.Add(new Mem { Uid = took, DeckUid = b.DeckUid, Name = b.Name, Type = b.Type, TypeLabel = b.TypeLabel, ShipName = b.ShipName, Serial = b.Serial, Alert = g.Kind == "alert" && g.Ordered,
                                        HangarK = b.HangarK, HangarN = b.HangarN, JoinedAt = b.HangarK > 0 ? _own.Slots.NextJoin() : 0 });
            return "ok";
        }

        /// <summary>Number after the last '-' of a group name ("ПВО-3", "CAP-5" -> 3, 5); 0 if none. Any prefix is accepted (saves from another language).</summary>
        private static int GroupNo(string name)
        {
            int i = name == null ? -1 : name.LastIndexOf('-'), n;
            return i >= 0 && int.TryParse(name.Substring(i + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        private Grp NewGroup(string name)
        {
            bool auto = string.IsNullOrEmpty(name) || AswGroupNames.IsAuto(name, CapPrefixes());   // T-326
            if (string.IsNullOrEmpty(name)) name = NextAutoName();
            var g = new Grp { AutoName = auto, Name = name, P = _p.Clone(), N = _p.DefaultOnStation, Roe = _p.Roe, AcceptAlert = _p.AcceptAlert, I = CapInterceptSpec.FromParams(_p) };
            g.Seq = _own.GroupSeq(Win, name);
            _groups.Add(g);
            return g;
        }

        public string Group(Dictionary<string, object> c)
        {
            if (_deferred != null) return "!restore_waiting";
            if (c != null && c.ContainsKey("op") && c["op"] as string == "rename") return RenameCmd(c);   // T-326
            var r = CapCommands.ParseGroup(c);
            if (r.err != null) return "!" + r.err;
            var birds = Index(_g.Birds());
            if (r.op == "disband")
            {
                var gd = Find(r.group);
                if (gd == null) return "!unknown_group";
                Disband(gd);
                return Ack(gd.Name, null);
            }
            Grp g = null; bool created = false;
            if (r.op == "create")
            {
                if (!string.IsNullOrEmpty(r.group) && Find(r.group) != null) return "!group_exists";
                // create only when at least one unit is accepted: test on a scratch group first
                g = new Grp { Name = r.group, P = _p.Clone() };
                created = true;
            }
            else
            {
                g = Find(r.group);
                if (g == null) return "!unknown_group";
            }
            if (r.op == "remove")
            {
                foreach (int uid in r.uids) { var m = MemOf(g, uid); if (m != null) DropMember(g, m, true, "removed"); }
                return Ack(g.Name, null);
            }
            var added = new List<int>(); var rej = new List<string>(); string firstRej = null;
            Grp real = created ? null : g;
            if (created)
            {
                real = NewGroup(r.group);
            }
            foreach (int uid in r.uids)
            {
                string reason; int took;
                if (TryAdd(real, uid, birds, out reason, out took) != null) added.Add(took);
                else { rej.Add("{\"uid\":" + uid + ",\"reason\":" + Q(reason) + "}"); if (firstRej == null) firstRej = reason; }
            }
            if (created && added.Count == 0)
            {
                _groups.Remove(real);
                return "!" + firstRej;
            }
            Log((created ? "group create " : "group add ") + real.Name + " added=" + added.Count + " rejected=" + rej.Count);
            return Ack(real.Name, "\"added\":[" + string.Join(",", added) + "],\"rejected\":[" + string.Join(",", rej) + "]");
        }

        // The request key stays with the member: when the game shows the task under another uid, the member follows it (no lost key, no repeated order).
        private void Rekey(Grp g, Mem m, IDictionary<int, CapBird> birds = null)
        {
            int nu = 0;
            try { nu = _g.TaskBirdUid(m.DeckUid, m.Key); } catch (Exception) { }
            if (nu == 0 || nu == m.Uid || MemOf(g, nu) != null) return;
            if (birds != null && !birds.ContainsKey(nu)) return;
            string own = _own.Claim(nu, Win, g.Name);
            if (own != null) return;
            _own.Release(m.Uid);
            Log("member uid " + m.Uid + " -> " + nu + " key=" + m.Key);
            m.Uid = nu;
        }

        private bool TryAdoptAirborne(Grp g, Mem m, Dictionary<int, CapBird> birds, out CapBird raw)
        {
            raw = null;
            if (m.KnownAir == null || m.Airborne || m.Type == null || m.Key == null) return false;
            GeoPt? deck = m.DeckUid > 0 ? _g.UnitPos(m.DeckUid) : null;
            double bd = double.MaxValue;
            foreach (var b in birds.Values)
            {
                if (Deckish(b.Where) || b.Where == "lost" || b.Returning || b.Type != m.Type || MemOf(g, b.Uid) != null) continue;
                // the spawned machine of an allowed task can already be listed (deck_launch) in the tick its pending task is still on the deck
                if (m.KnownAir.Contains(b.Uid) && !(m.Allowed && b.Where == "deck_launch" && (b.DeckUid == m.DeckUid || b.DeckUid == 0 || m.DeckUid == 0))) continue;
                if (b.Where == "deck_launch" && b.DeckUid != m.DeckUid && b.DeckUid != 0 && m.DeckUid != 0) continue;   // taxiing out of another deck
                string w;
                if (_own.Owner(b.Uid, out w) != null) continue;
                if (StrikeWhy(b) != null) continue;   // T-325
                double d = deck.HasValue ? AswMath.DistNm(deck.Value, new GeoPt(b.Lat, b.Lon)) : 0;
                if (raw == null || d < bd) { raw = b; bd = d; }
            }
            if (raw == null) return false;
            if (_own.Claim(raw.Uid, Win, g.Name) != null) { raw = null; return false; }
            _own.Release(m.Uid);
            Log("member uid " + m.Uid + " -> " + raw.Uid + " airborne key=" + m.Key);
            m.Uid = raw.Uid; m.KnownAir = null; m.MissingS = -1;
            return true;
        }

        /// <summary>The deck row of a member's own launch task read straight from the deck (Birds() may miss it for a tick); null = the task is gone.</summary>
        private CapBird TaskRow(Mem m, Dictionary<int, List<AirDeckTask>> cache)
        {
            if (m.Key == null || m.DeckUid == 0) return null;
            List<AirDeckTask> tasks;
            if (!cache.TryGetValue(m.DeckUid, out tasks)) { try { tasks = _g.DeckTasks(m.DeckUid); } catch (Exception) { tasks = null; } cache[m.DeckUid] = tasks; }
            if (tasks == null) return null;
            foreach (var t in tasks)
            {
                if (t == null || t.Key != m.Key) continue;
                if (!m.TaskRowLogged) { m.TaskRowLogged = true; Log("deck row missing, task present uid=" + m.Uid + " key=" + m.Key + " state=" + (t.State ?? "-")); }
                GeoPt? dp = _g.UnitPos(m.DeckUid);
                return new CapBird
                {
                    Uid = m.Uid, DeckUid = m.DeckUid, Type = m.Type ?? t.Type, Loadout = t.Loadout, PendingKey = m.Key, Ours = _ourKeys.Contains(m.Key),
                    Where = t.RemainS > 1 || t.State == "await_deck" || t.State == "await_crew" ? "deck_prep" : "deck_ready", ReadyInS = Math.Max(0, t.RemainS),
                    AamNow = m.AamStart, AamStart = m.AamStart, LongStart = m.LongStart, Name = m.Name, TypeLabel = m.TypeLabel, ShipName = m.ShipName,
                    Lat = dp.HasValue ? dp.Value.Lat : 0, Lon = dp.HasValue ? dp.Value.Lon : 0, PatrolKn = 420, CruiseKn = 450,
                };
            }
            return null;
        }

        /// <summary>T-204: the best free row for a member that lost its own (CapMath.ReadoptRank); null when none. Never a row another group, the player or
        /// the mod itself holds (ADR-0080: whoever took it last); a game ready-up task counts as foreign when it is neither the mod's nor held.</summary>
        private CapBird PickRow(Grp g, Mem m, Dictionary<int, CapBird> birds, bool landed, bool afterCool)
        {
            CapBird raw = null; int best = int.MaxValue;
            foreach (var b in birds.Values)
            {
                if (b.Type != m.Type || (m.DeckUid != 0 && b.DeckUid != m.DeckUid) || MemOf(g, b.Uid) != null) continue;
                bool hasKey = b.PendingKey != null;
                bool foreignTask = hasKey && !b.Ours && !_ourKeys.Contains(b.PendingKey);
                int rank = CapMath.ReadoptRank(b.Where, hasKey, foreignTask, landed, afterCool);
                if (rank < 0) continue;
                string w;
                if (_own.Owner(b.Uid, out w) != null || _g.TakenByPlayer(b.Uid)) continue;
                if (StrikeWhy(b) != null) continue;   // T-325
                if (b.HangarK > 0 && !SlotFree(b)) continue;   // T-186: a slot another window claims
                if (rank < best || (rank == best && raw != null && b.HangarK > 0 && raw.HangarK > 0 && (b.DeckUid == raw.DeckUid ? b.HangarK < raw.HangarK : b.DeckUid < raw.DeckUid))) { best = rank; raw = b; }   // T-186: the lowest k
            }
            return raw;
        }

        /// <summary>The member's deck task vanished without a launch: take a free airframe row of the same type on the same deck (hangar first; after a cool-down also a game ready-up task).</summary>
        private bool ReadoptDeck(Grp g, Mem m, Dictionary<int, CapBird> birds, out CapBird raw)
        {
            raw = null;
            if (m.Type == null || m.Pending) return false;
            raw = PickRow(g, m, birds, false, m.Where == "deck_cool");
            if (raw == null) return false;
            if (_own.Claim(raw.Uid, Win, g.Name) != null) { raw = null; return false; }
            _own.Release(m.Uid);
            m.HangarK = raw.HangarK; m.HangarN = raw.HangarN; m.JoinedAt = raw.HangarK > 0 ? _own.Slots.NextJoin() : 0; m.KeyAt = -1;
            Log("member uid " + m.Uid + " -> " + raw.Uid + " readopt (task gone)" + (raw.PendingKey != null ? " game task key=" + raw.PendingKey : "") + " key=" + (m.Key ?? "-"));
            if (m.Key != null) _ourKeys.Remove(m.Key);
            m.Uid = raw.Uid; m.Key = null; m.Allowed = false; m.PlanSlot = -1; m.KnownAir = null; m.MissingS = -1; m.TaskRowLogged = false;
            return true;
        }

        /// <summary>ADR-0040 §3.4: a leaving member landed (the game removed the unit and starts the cool-down of the airframe under another uid). The member
        /// takes a free row of its type on its deck - the cool-down row first (its own airframe), then a hangar row, then (T-204) a free game ready-up task - and stays in the group.</summary>
        private bool ReadoptLanded(Grp g, Mem m, Dictionary<int, CapBird> birds, out CapBird raw)
        {
            raw = null;
            if (m.Type == null) return false;
            raw = PickRow(g, m, birds, true, false);
            if (raw == null) return false;
            if (_own.Claim(raw.Uid, Win, g.Name) != null) { raw = null; return false; }
            _own.Release(m.Uid);
            m.HangarK = raw.HangarK; m.HangarN = raw.HangarN; m.JoinedAt = raw.HangarK > 0 ? _own.Slots.NextJoin() : 0; m.KeyAt = -1;
            int deck = raw.DeckUid > 0 ? raw.DeckUid : m.DeckUid;
            Log("landed uid=" + m.Uid + " group=" + g.Name + " -> deck " + deck + " " + (raw.PendingKey != null ? "game task" : raw.Where == "deck_cool" ? "cool-down" : "hangar") + " " + m.Type + " uid=" + raw.Uid + (raw.PendingKey != null ? " key=" + raw.PendingKey : "") + " (re-arm)");
            int old = m.Uid;
            m.Uid = raw.Uid; m.DeckUid = deck;
            LandedReset(m);
            m.Recovered = false; m.UnlistedLogged = false; m.KnownAir = null; m.MissingS = -1; m.TaskRowLogged = false;
            return true;
        }

        /// <summary>Back on deck: the flight state of a member is cleared (the next launch is a new sortie).</summary>
        private void LandedReset(Mem m)
        {
            m.Airborne = false; m.Leaving = null; m.Postponed = null; m.Slot = -1; m.OnStation = false; m.AamStart = 0; m.LongStart = 0;
            m.RouteSig = null; m.LastCircleRouteAt = -1e9; m.AirSince = -1; m.RoeSet = false; m.ReliefEvt = false; m.Fail = 0; m.TcWarned = false; m.Allowed = false; m.PlanSlot = -1;
            m.Reason = null; m.OwnLaunch = false; m.TopReserveNm = -1; m.RingNm = -1; m.FuelLowSince = -1; m.ReserveOkNm = -1;
            m.AlertState = null; m.AlertRaid = null; m.RaidIds = null; m.Handed = false; m.Barrage = false; m.SurfaceHold = false; m.FarSince = -1; m.AlertThrottle = -1; m.LeashStartNm = -1; m.LeashIn = false;
            m.AlertKey = null; m.Tgt = null; m.BindRestoredAt = -1;   // T-219
            RadarRelease(m, false);   // T-241: landed - the game sets EMCON itself
            RestoreThrottle(m);
            if (m.Icpt != null) { int it = m.Icpt.Target; m.Icpt = null; DropTargetCache(it); }   // T-333
        }

        /// <summary>The deck of a member is knocked out (one game read per deck and tick); the group gets the warning once per outage.</summary>
        private bool DeckIsDown(Grp g, int deck, Dictionary<int, bool> cache)
        {
            if (deck <= 0) return false;
            bool v;
            if (!cache.TryGetValue(deck, out v)) { try { v = _g.DeckDown(deck); } catch (Exception) { v = false; } cache[deck] = v; }
            if (v && !g.Warns.Contains("deck_down")) Warn(g, "deck_down", null, "\"deck\":" + deck);
            return v;
        }

        /// <summary>Who took the task? The deck's task list at the moment the member's own task is found missing (the broker logs the full list too).</summary>
        private void TaskGoneLog(Mem m, Dictionary<int, List<AirDeckTask>> cache)
        {
            List<AirDeckTask> tasks = null;
            if (m.DeckUid != 0 && !cache.TryGetValue(m.DeckUid, out tasks)) { try { tasks = _g.DeckTasks(m.DeckUid); } catch (Exception) { tasks = null; } cache[m.DeckUid] = tasks; }
            var sb = new StringBuilder();
            if (tasks != null) foreach (var t in tasks) { if (t == null) continue; if (sb.Length > 0) sb.Append(' '); sb.Append(t.Type ?? "?").Append(':').Append(t.State ?? "-").Append(':').Append(t.Key).Append(t.Ours ? "*" : ""); }
            bool down = false;
            try { down = m.DeckUid != 0 && _g.DeckDown(m.DeckUid); } catch (Exception) { }
            Log("task gone uid=" + m.Uid + " key=" + m.Key + " deck=" + m.DeckUid + " inoperable=" + down + " allowed=" + m.Allowed + " deck_tasks=" + (tasks == null ? "?" : sb.Length == 0 ? "-" : sb.ToString()));
        }

        private void AbortKey(Mem m, string reason)
        {
            if (m.Key == null) return;
            if (_ourKeys.Contains(m.Key))
            {
                string key = m.Key;
                SubmitDeck(FindGroupOf(m), AirRelief.Abort(m.DeckUid, key, "cap " + reason + " uid=" + m.Uid), res => { });
                _ourKeys.Remove(key);
            }
            m.Key = null; m.Allowed = false;
        }

        private Grp FindGroupOf(Mem m) { foreach (var g in _groups) if (g.Members.Contains(m)) return g; return _groups.Count > 0 ? _groups[0] : null; }

        private void DropMember(Grp g, Mem m, bool restore, string why, string detail = null)
        {
            AbortKey(m, "member_out:" + why);
            if (m.LentFrom != null && !m.LentOrphan) { var la = Find(m.LentFrom); if (la != null) la.Lent.RemoveAll(r => r.M == m); }
            if (m.Icpt != null) EndIntercept(g, m, null, "member_out:" + why, restore && m.Airborne);   // T-333
            if (restore && m.Airborne) { RestoreThrottle(m); try { _g.Restore(m.Uid); } catch (Exception) { } }
            RadarRelease(m, restore && m.Airborne);   // T-241
            _own.Release(m.Uid);
            g.Members.Remove(m);
            Log("member out uid=" + m.Uid + " group=" + g.Name + " (" + why + ")" + (detail != null ? " " + detail : ""));
        }

        private void Disband(Grp g)
        {
            if (g.Kind == "alert") OrphanLent(g); else ReleaseLent(g, "disband");
            foreach (var m in new List<Mem>(g.Members)) DropMember(g, m, true, "disband");
            _own.ReleaseGroup(Win, g.Name);
            _groups.Remove(g);
            Log("group disband " + g.Name);
            Evt("cap_disbanded", "{\"group\":" + Q(g.Name) + "}");
        }

        public string Order(Dictionary<string, object> c)
        {
            if (_deferred != null) return "!restore_waiting";
            string err;
            var cmd = CapCommands.ParseOrder(c, out err);
            if (cmd == null) return "!" + err;
            var g = Find(cmd.Group);
            if (g == null) return "!unknown_group";
            // T-175 (ADR-0075 item 8): the loadouts of the order are taken before the first deck request of the group (the planner reads them on the next tick)
            if (cmd.Loadouts != null)
            {
                foreach (var kv in cmd.Loadouts)
                {
                    g.Loadouts[kv.Key] = kv.Value;
                    var del = new List<string>();
                    foreach (var k in g.Banned.Keys) if (k.Contains("|" + kv.Key + "|")) del.Add(k);
                    foreach (var k in del) g.Banned.Remove(k);
                    Log("loadout " + g.Name + " " + kv.Key + "=" + kv.Value + " (order)");
                }
                ClearWarn(g, "loadout_fallback"); ClearWarn(g, "no_ammo");
            }
            if (cmd.Kind == "alert") return OrderAlertGroup(g, cmd);
            var z = cmd.Zone;
            if (z.Rect == null)
            {
                int ou = cmd.OriginUid;
                if (!cmd.Origin.HasValue && ou == 0)   // no origin given: the group's carrier, the same default as the CAP window (live finding 37)
                {
                    var decks = new List<int>();
                    foreach (var m in g.Members) decks.Add(m.DeckUid);
                    ou = CapWindowModel.OriginShipOfDecks(decks, 0);
                }
                var o = cmd.Origin ?? (ou != 0 ? _g.UnitPos(ou) : null);
                if (o == null) return "!no_origin";
                z.Rect = CapCommands.PolarRect(o.Value, cmd);
            }
            if (z.Anchor == "unit")
            {
                var pos = _g.UnitPos(z.AnchorUid);
                if (pos == null) return "!anchor_lost";
                z.OffLat = (z.Rect[0] + z.Rect[2]) / 2 - pos.Value.Lat;
                z.OffLon = GeoLon.DLon(pos.Value.Lon, GeoLon.MidLon(z.Rect[1], z.Rect[3]));
            }
            if (g.Kind == "alert") SwitchToPatrol(g);
            if (cmd.AcceptAlert >= 0) g.AcceptAlert = cmd.AcceptAlert == 1;
            g.Zone = z; g.OrderSeq++;
            if (cmd.AltFt >= 0) g.P.PatrolAltFt = cmd.AltFt;
            g.N = cmd.OnStation >= 0 ? cmd.OnStation : (g.N > 0 ? g.N : g.P.DefaultOnStation);
            g.K = cmd.Ready;
            g.Roe = cmd.Roe ?? g.P.Roe;
            g.Ordered = true; g.AnchorWarned = false; g.Gap = false; g.PrevGap = false; g.GapS = -1;
            g.Warned.Clear(); g.Warns.Clear(); g.Banned.Clear();
            ApplyIcptOrder(g, cmd);   // T-333
            foreach (var m in g.Members)
            {
                m.RouteSig = null;
                if (m.Airborne && m.Leaving == null) { string prev; if (_g.SetRoe(m.Uid, g.Roe, out prev) == null) { if (!m.RoeSet) m.PrevRoe = prev; m.RoeSet = true; } }   // T-175: not a bird that flies home on hold
            }
            // zone_far hint: a type whose range cannot cover the trip
            var centre = new GeoPt((z.Rect[0] + z.Rect[2]) / 2, GeoLon.MidLon(z.Rect[1], z.Rect[3]));
            var seen = new HashSet<string>();
            foreach (var b in _g.Birds() ?? new List<CapBird>())
            {
                var m = MemOf(g, b.Uid);
                if (m == null || b.MaxRangeNm <= 0 || b.PatrolKn <= 0 || !seen.Add(b.Type ?? "")) continue;
                var deck = _g.UnitPos(b.DeckUid);
                double dist = AswMath.DistNm(deck ?? new GeoPt(b.Lat, b.Lon), centre);
                if (CapMath.ZoneHours(b.MaxRangeNm, dist, b.PatrolKn) <= 0) Warn(g, "zone_far", b.Type);
            }
            double w = GeoLon.DLon(z.Rect[1], z.Rect[3]) * 60.0 * AswMath.LonK(z.Rect[0], z.Rect[2]), h = (z.Rect[2] - z.Rect[0]) * 60.0;
            Log("order " + g.Name + " zone " + F(w) + "x" + F(h) + " nm N=" + g.N + " K=" + (g.K < 0 ? "auto" : g.K.ToString()) + " roe=" + g.Roe + " anchor=" + z.Anchor
                + (g.I != null ? " intercept=" + (g.I.On ? "on margin_nm=" + F(g.I.MarginNm) + " per_target=" + g.I.PerTarget + " keep=" + g.I.Keep + " targets=" + g.I.Targets + " speed=" + g.I.Speed : "off") : ""));
            Evt("cap_started", "{\"group\":" + Q(g.Name) + ",\"n\":" + g.N + ",\"k\":" + g.K + ",\"anchor\":" + Q(z.Anchor) + "}");
            return Ack(g.Name, "\"n\":" + g.N + ",\"k\":" + g.K);
        }

        public string Loadout(Dictionary<string, object> c)
        {
            if (_deferred != null) return "!restore_waiting";
            string group, type, lo, err;
            if (!CapCommands.ParseLoadout(c, out group, out type, out lo, out err)) return "!" + err;
            var g = Find(group);
            if (g == null) return "!unknown_group";
            g.Loadouts[type] = lo;
            var del = new List<string>();
            foreach (var k in g.Banned.Keys) if (k.Contains("|" + type + "|")) del.Add(k);
            foreach (var k in del) g.Banned.Remove(k);
            ClearWarn(g, "loadout_fallback"); ClearWarn(g, "no_ammo");
            Log("loadout " + g.Name + " " + type + "=" + lo);
            return Ack(g.Name, null);
        }

        public string Params(Dictionary<string, object> c)
        {
            if (_deferred != null) return "!restore_waiting";
            string err;
            string group = null; object o;
            if (c.TryGetValue("group", out o)) group = o as string;
            if (!string.IsNullOrEmpty(group))
            {
                var g = Find(group);
                if (g == null) return "!unknown_group";
                if (!CapCommands.ParseParams(c, g.P, out err)) return "!" + err;
                if (c.ContainsKey("alt_ft")) g.OrderSeq++;
                Log("params " + g.Name);
                return Ack(g.Name, null);
            }
            if (!CapCommands.ParseParams(c, _p, out err)) return "!" + err;
            foreach (var g in _groups) CapCommands.ParseParams(c, g.P, out err);
            Log("params all");
            return Ack("all", null);
        }

        public string Stop(Dictionary<string, object> c)
        {
            if (_deferred != null) return "!restore_waiting";
            string err;
            string name = CapCommands.ParseStop(c, out err);
            if (name == null) return "!" + err;
            var list = new List<Grp>();
            if (name == "all") list.AddRange(_groups);
            else { var g = Find(name); if (g == null) return "!unknown_group"; list.Add(g); }
            foreach (var g in list)
            {
                bool wasAlertKind = g.Kind == "alert" && g.Ordered;
                g.Ordered = false; g.Gap = false; g.PrevGap = false; g.Prio = false; g.GapS = -1;
                if (g.Kind == "alert") { if (wasAlertKind) ReleaseAlertGroup(g, true); }   // ADR-0079: the stop of an alert group: the designation goes, prepared tasks are aborted below
                else ReleaseLent(g, "stop");
                foreach (var m in g.Members)
                {
                    AbortKey(m, "stop");
                    if (m.Icpt != null) EndIntercept(g, m, null, "stop");   // T-333
                    if (m.Airborne && m.Leaving == null) { RestoreThrottle(m); try { _g.Rtb(m.Uid); } catch (Exception) { } m.Leaving = "stop"; m.LastRtb = _now; m.RtbErr = null; m.Recovered = false; }
                    m.Slot = -1; m.OnStation = false; m.RouteSig = null;
                }
                Log("stop " + g.Name);
                Evt("cap_stopped", "{\"group\":" + Q(g.Name) + "}");
            }
            return Ack(name, null);
        }

        public string Busy(int uid)
        {
            string w; string o = _own.Owner(uid, out w);
            return o == null ? null : "in_group:" + o;
        }

        public void ReleaseUid(int uid)
        {
            Mem m; var g = GroupOf(uid, out m);
            if (g == null) return;
            DropMember(g, m, true, "return control");
            Evt("cap_released", "{\"unit\":" + uid + ",\"group\":" + Q(g.Name) + "}");
        }

        // ================================================= tick =======================================================================
        public void Tick(double now)
        {
            _now = now;
            if (_groups.Count == 0) return;
            _losCache.Clear();
            var list = _g.Birds();
            _lastBirds = list; _birdsAt = _now;
            _icptClaims.Clear();   // T-333
            // T-333 review: the raid birds of every alert group count before any patrol group plans (the deck order may tick a patrol group first)
            foreach (var ag in _groups) if (ag.Kind != "patrol" && ag.Ordered && ag.A != null) foreach (var am in ag.Members) if (am.Airborne && am.Leaving == null && am.RaidIds != null) foreach (int rid in am.RaidIds) { int cn; _icptClaims.TryGetValue(rid, out cn); _icptClaims[rid] = cn + 1; }
            _own.Slots.Rebind(now);   // T-186: the shared book rebinds the slots of both windows once per tick (the air tick already did it with the game numbers)
            var birds = Index(list);
            var contacts = _g.AirContacts();
            foreach (string name in AirOpsMath.DeckOrder(DeckPrio()))
            {
                if (TraceOrder) Log("tick " + name);
                var g = Find(name);
                if (g != null) TickGroup(g, birds, contacts, now);
            }
            DeclareDecks();
            RebuildCapAir();   // T-333
        }

        private void TickGroup(Grp g, Dictionary<int, CapBird> birds, IList<CapContact> contacts, double now)
        {
            if (g.Ordered && g.Zone.Anchor == "unit")
            {
                bool was = g.Zone.AnchorLost;
                CapMath.AnchoredRect(g.Zone, _g.UnitPos(g.Zone.AnchorUid));
                if (g.Zone.AnchorLost && !was && !g.AnchorWarned) { g.AnchorWarned = true; Warn(g, "anchor_lost"); }
            }
            if (g.Ordered && g.Kind == "patrol") UpdateCover(g, birds);
            var lanes = g.Ordered ? CapMath.Lanes(g.Zone, Math.Max(1, g.N), g.P, CoverOf(g)) : new List<CapLane>();
            if (g.Ordered && g.Kind == "patrol") LogLanes(g, lanes);
            UpdatePhase(g, birds, lanes, now);
            var tasksByDeck = new Dictionary<int, List<AirDeckTask>>();
            var copies = new List<CapBird>();
            HashSet<int> airNow = null;
            bool waiting = false, anyDown = false;
            var downNow = new Dictionary<int, bool>();
            foreach (var m in new List<Mem>(g.Members))
            {
                CapBird raw;
                if (!birds.ContainsKey(m.Uid) && m.Key != null) Rekey(g, m, birds);
                if (!birds.TryGetValue(m.Uid, out raw))
                {
                    if (m.HangarK > 0 && !m.Airborne && _own.Slots.Short(Win, m.Uid) > 0) continue;   // T-186: the machine of the slot was taken: the book drops the claim after AbortTicks ticks (hangar_taken)
                    // 0.27.8 live: a deck member whose launch task is still on the deck is never dropped (the row may be missing from Birds() for a while)
                    raw = !m.Airborne ? TaskRow(m, tasksByDeck) : null;
                    if (raw != null) { m.MissingS = -1; birds[m.Uid] = raw; }
                    // deck task -> airborne unit: the game gives the aircraft a new uid at take-off (as AswController.ProcessAdopts)
                    else if (!TryAdoptAirborne(g, m, birds, out raw))
                    {
                        // T-218: a restored deck-row member (the game re-created the deck tasks with new GUIDs) takes a free row of its type on its ship at once
                        if (m.RestoredAt >= 0 && m.RestoreDeck >= 0 && !m.Airborne && ReadoptRestored(g, m, birds, out raw)) { }
                        else {
                        // T-211 (ADR-0088): a member restored from a file is given RestoreGraceS to show up in the game lists (the lists may still be filling after a load)
                        if (m.RestoredAt >= 0 && now - m.RestoredAt < RestoreGraceS)
                        {
                            waiting = true;
                            if (!m.RestoreWaitLogged) { m.RestoreWaitLogged = true; Log("restore wait uid=" + m.Uid + " group=" + g.Name); }
                            continue;
                        }
                        if (!m.Airborne && m.Key != null && m.KeyAt >= 0 && now - m.KeyAt <= OwnRowWaitS) { waiting = true; continue; }   // T-186: the task row of a handed-over order is listed late
                        if (!m.Airborne && m.Key != null && m.MissingS < 0) TaskGoneLog(m, tasksByDeck);   // the moment the task is missed: the deck as it is now
                        if (m.KnownAir != null && m.Key != null && !m.Airborne && now - (m.MissingS < 0 ? (m.MissingS = now) : m.MissingS) < AdoptGraceS) { waiting = true; continue; }
                        // a leaving aircraft that is gone has landed (the game removes the unit and starts the deck cool-down under another uid) or was shot down on the way
                        if (m.Airborne && m.Leaving != null)
                        {
                            bool alive = false;
                            try { alive = _g.UnitAlive(m.Uid); } catch (Exception) { }
                            if (alive)
                            {
                                if (!m.UnlistedLogged) { m.UnlistedLogged = true; Log("leaving uid=" + m.Uid + " alive but not listed, kept group=" + g.Name); }
                                continue;
                            }
                            if (!m.Recovered) { DropMember(g, m, false, "gone while leaving"); continue; }
                            // ADR-0040 §3.4: a landed member stays in the group - it takes its airframe back (the deck cool-down row of its type, else a free
                            // hangar row) and the planner relaunches it with the group loadout when it is ready (rotation keeps N on station)
                            if (!ReadoptLanded(g, m, birds, out raw))
                            {
                                bool landing = false;
                                try { landing = m.DeckUid != 0 && _g.Landing(m.DeckUid, m.Type); } catch (Exception) { }
                                if (now - (m.MissingS < 0 ? (m.MissingS = now) : m.MissingS) < (landing ? LandWaitS : AdoptGraceS)) { waiting = true; continue; }   // T-204: the deck still recovers the type - its cool-down row comes later
                                DropMember(g, m, false, "landed, no airframe on deck " + m.DeckUid); continue;
                            }
                        }
                        // the deck task vanished without a launch: the member takes a free airframe of its type on its deck (the planner orders a new launch)
                        else if (m.Airborne || !ReadoptDeck(g, m, birds, out raw))
                        {
                            // the cool-down of a landed member ended: its airframe goes back to the hangar row (may be listed a tick later)
                            if (!m.Airborne && m.Where == "deck_cool" && now - (m.MissingS < 0 ? (m.MissingS = now) : m.MissingS) < AdoptGraceS) { waiting = true; continue; }
                            m.Lost = true; DropMember(g, m, false, "lost"); continue;
                        }
                        }
                    }
                }
                m.RestoredAt = -1;   // T-211: found in the game lists
                // 0.27.8 live: the mod's own RTB (and the game's RTB / bingo / winchester) puts the aircraft on the deck's return route with weapons
                // on hold - that is not the player; a leaving member is tracked until it lands (ADR-0036 §11 check only while it serves the group)
                if (m.Leaving == null && _g.TakenByPlayer(m.Uid))
                {
                    Warn(g, "player_took", null, "\"unit\":" + m.Uid);
                    DropMember(g, m, false, "player_took", _g.TakenDetail(m.Uid) ?? "why=?");   // T-317: the reason is always in the log
                    continue;
                }
                if (m.Leaving == null && _g.TakeAiDrift(m.Uid)) m.RouteSig = null;   // T-317 (ADR-0124): the game moved route/ROE in its air combat: the patrol is issued again after it
                if (_g.InGameCapZone(m.Uid)) { m.Where = "game_zone"; m.Reason = "game_cap_zone"; continue; }
                // live 0.28.3: an AS-4 hit knocked Eisenhower's deck out (FlightDeck.KillAircraft cleared every task); a machine on such a deck waits
                // out of the plan (no launch order: the game would never run it) until the deck is repaired
                if (Deckish(raw.Where) && DeckIsDown(g, raw.DeckUid > 0 ? raw.DeckUid : m.DeckUid, downNow))
                {
                    m.DeckUid = raw.DeckUid > 0 ? raw.DeckUid : m.DeckUid;
                    m.Where = "deck_down"; m.Reason = "deck_inoperable"; m.ReadyInS = -1; m.ReadyKnown = false; m.TakeoffInS = -1;
                    anyDown = true;
                    continue;
                }
                if (raw.Where == "deck_launch")
                {
                    // the task became a LaunchTask: the machine is spawned (final uid) and taxis out; it counts as the relief on its way, no orders until it flies
                    if (m.Airborne) { Log("landed uid=" + m.Uid + " group=" + g.Name); RadarRelease(m, false); m.Airborne = false; m.Leaving = null; m.Slot = -1; m.OnStation = false; m.RouteSig = null; m.LastCircleRouteAt = -1e9; }
                    m.KnownAir = null; m.MissingS = -1; m.DeckUid = raw.DeckUid > 0 ? raw.DeckUid : m.DeckUid;
                    if (!string.IsNullOrEmpty(raw.Name)) m.Name = raw.Name;
                    if (!string.IsNullOrEmpty(raw.TypeLabel)) m.TypeLabel = raw.TypeLabel;
                    if (!string.IsNullOrEmpty(raw.ShipName)) m.ShipName = raw.ShipName;
                    m.Where = "deck_launch"; m.ReadyInS = 0; m.ReadyKnown = true; m.TakeoffInS = -1; m.Reason = null; m.ReserveNm = -1; m.LeaveInS = -1; m.FuelPct = -1;
                    var lb = raw.Copy();
                    lb.Where = "to_zone"; lb.Slot = m.PlanSlot; lb.PendingKey = null; lb.Controllable = true;
                    if (!m.Alert) copies.Add(lb);   // T-175: the alert pair is outside the relief plan
                    continue;
                }
                var b = raw.Copy();
                b.Name = m.Name; b.Slot = m.Slot; b.AamStart = m.AamStart; b.LongStart = m.LongStart;
                m.AamNow = raw.AamNow; m.DeckUid = raw.DeckUid > 0 ? raw.DeckUid : m.DeckUid;
                if (raw.Loadout != null && Deckish(raw.Where)) m.RowLoadout = raw.Loadout;   // T-218
                if (raw.HangarK > 0) m.HangarN = raw.HangarN;
                if (!string.IsNullOrEmpty(raw.Name)) m.Name = raw.Name;   // T-155: the unit's own name once it is known (a deck row has none)
                if (!string.IsNullOrEmpty(raw.TypeLabel)) m.TypeLabel = raw.TypeLabel;
                if (!string.IsNullOrEmpty(raw.ShipName)) m.ShipName = raw.ShipName;
                if (!string.IsNullOrEmpty(raw.Serial)) m.Serial = raw.Serial;
                bool deck = Deckish(raw.Where);
                if (deck && m.LentFrom != null) { m.Where = raw.Where; BackFromLend(g, m); continue; }   // T-179: a lent bird landed - back to its alert group
                if (deck)
                {
                    if (airNow == null) { airNow = new HashSet<int>(); foreach (var o in birds.Values) if (!Deckish(o.Where) && o.Where != "lost") airNow.Add(o.Uid); }
                    m.KnownAir = airNow; m.MissingS = -1;
                }
                if (deck) ClassifyDeck(g, m, b, tasksByDeck);
                else TickAirborne(g, m, b, lanes, contacts, now);
                m.Where = b.Where;
                RadarTick(m);   // T-241
                if (b.Where != "lost" && !m.Alert) copies.Add(b);   // T-175: a bird of the alert pair never takes part in the rotation
            }
            if (!anyDown && g.Warns.Contains("deck_down")) { ClearWarn(g, "deck_down"); Log("deck back in service group=" + g.Name); }
            if (g.Kind == "alert") { AlertTick(g, birds, contacts, now); return; }   // T-179: the alert kind has its own tick (no zone, no relief plan)
            if (!g.Ordered) { UpdatePriority(g, contacts, now, 0); return; }
            if (waiting) { UpdatePriority(g, contacts, now, 0); return; }   // a launched member is being matched to its airborne unit: do not plan on a stale picture
            if (g.Kind == "patrol") InterceptTick(g, birds, contacts, now);   // T-333 (ADR-0129): zone intrusion intercept: the pairs; the guidance is in TickAirborne

            int onStation = 0;
            foreach (var m in g.Members) if (m.Where == "zone") onStation++;
            int reserveN = 0;
            foreach (var m in g.Members) if (Deckish(m.Where)) reserveN++;
            int plain = 0;
            foreach (var m in g.Members) if (!m.Alert && m.LentFrom == null) plain++;   // T-179: a bird still guided to a raid and a lent one are not a reserve
            int k = g.K >= 0 ? g.K : Math.Max(0, Math.Min(g.N, plain - g.N));
            if (g.K < 0 && g.AutoKLogged != k)
            {
                g.AutoKLogged = k;   // T-244 (customer answer): K=auto counts only the birds of the group; free birds of the deck are never lifted for a relief
                Log("relief k=auto group=" + g.Name + " k=" + k + " (own birds only: " + plain + " in the group, N=" + g.N + "; free birds outside the group are not lifted)");
            }

            bool gap; double gapS;
            var acts = CapMath.PlanRelief(copies, g.N, k, lanes, FastLoadouts(g, copies), DeckLos(g), _g.Ammo, _g.DeckTimingsMode(), now, g.P, out gap, out gapS);
            foreach (var a in acts) Exec(g, a, copies);
            if (gap && !g.PrevGap) Evt("cap_warn", "{\"group\":" + Q(g.Name) + ",\"code\":\"continuity_gap\",\"gap_s\":" + Json.Num(gapS) + "}");
            if (gap && !g.Warns.Contains("continuity_gap")) { g.Warns.Add("continuity_gap"); Log("warn continuity_gap group=" + g.Name + " gap_s=" + F(gapS)); }
            if (!gap) g.Warns.Remove("continuity_gap");
            g.PrevGap = gap; g.Gap = gap; g.GapS = gap ? gapS : -1;
            UpdatePriority(g, contacts, now, onStation);
        }

        private void UpdatePriority(Grp g, IList<CapContact> contacts, double now, int onStation)
        {
            bool prio = false;
            if (g.Ordered && g.Zone.Rect != null)
            {
                GeoPt? deck = null;
                foreach (var m in g.Members) if (m.DeckUid > 0) { deck = _g.UnitPos(m.DeckUid); if (deck != null) break; }
                g.Latch.Update(contacts, g.Zone, deck, now, g.P);
                prio = (g.Gap || onStation < g.N) && g.Latch.On;
            }
            if (prio != g.Prio)
            {
                g.Prio = prio;
                Log("deck priority " + (prio ? "on" : "off") + " group=" + g.Name + (prio ? " threat=" + g.Latch.ThreatUid + " nm=" + F(g.Latch.ThreatNm) : ""));
                Evt("cap_priority", "{\"group\":" + Q(g.Name) + ",\"on\":" + Json.Bool(prio) + ",\"threat_uid\":" + g.Latch.ThreatUid + ",\"threat_nm\":" + Json.Num(g.Latch.ThreatNm) + "}");
            }
        }

        // A bird on deck / in hangar: landing bookkeeping, deck request state.
        private void ClassifyDeck(Grp g, Mem m, CapBird b, Dictionary<int, List<AirDeckTask>> cache)
        {
            if (m.Airborne)
            {
                Log("landed uid=" + m.Uid + " group=" + g.Name);
                LandedReset(m);
            }
            AdoptRowKey(g, m, b, cache);
            if (m.Key != null)
            {
                List<AirDeckTask> tasks;
                if (!cache.TryGetValue(b.DeckUid, out tasks)) { try { tasks = _g.DeckTasks(b.DeckUid); } catch (Exception) { tasks = null; } cache[b.DeckUid] = tasks; }
                AirDeckTask t = null;
                if (tasks != null) foreach (var x in tasks) if (x.Key == m.Key) { t = x; break; }
                if (t == null) { Log("request key gone uid=" + m.Uid + " key=" + m.Key); _ourKeys.Remove(m.Key); m.Key = null; m.Allowed = false; }
                else
                {
                    b.PendingKey = m.Key; b.Ours = _ourKeys.Contains(m.Key);
                    b.Where = t.RemainS > 0 ? "deck_prep" : "deck_ready";
                    b.ReadyInS = Math.Max(0, t.RemainS); b.ReadyKnown = true;
                }
            }
            m.ReadyInS = b.ReadyInS; m.ReadyKnown = b.ReadyKnown; m.Reason = null; m.ReserveNm = -1; m.LeaveInS = -1; m.FuelPct = -1;
            m.TakeoffInS = m.Key != null && m.Allowed && (b.Where == "deck_prep" || b.Where == "deck_ready") ? Math.Max(0, b.ReadyInS) + g.P.LaunchDelayS : -1;
        }

        /// <summary>T-315: the threshold shown in the log: the ring margin with a live fuel fraction, else the old clean-range floor.</summary>
        private static double ThrNm(CapBird b, Grp g)
        {
            return CapMath.FracFuelKnown(b) ? CapMath.RingMarginNm(b.PatrolKn, 0, g.P) : CapMath.FuelThresholdNm(b.PatrolKn, 0, b.MaxRangeNm, g.P);
        }

        private void TickAirborne(Grp g, Mem m, CapBird b, List<CapLane> lanes, IList<CapContact> contacts, double now)
        {
            var pos = new GeoPt(b.Lat, b.Lon);
            if (!m.Airborne) Adopt(g, m, b, now);
            b.AamStart = m.AamStart; b.LongStart = m.LongStart; b.Slot = m.Slot;
            b.PendingKey = null;
            bool gameFuel = b.ReserveNm > 0 || b.MaxRangeNm > 0;   // false: FuelFallback models the tanks (its max is the reference)
            FuelFallback(g, m, b, now);
            // live 0.28.10: the game's MaxRange (_maxRangeInKm) is the clean full-tank path; with a heavy loadout (F-14A, 6 x AIM-54) the path at
            // take-off was 280-300 nm, under the 15 % floor of 2107 nm, and fresh machines left for fuel before the zone. The floor of a machine we
            // launched is taken from its own full path (the largest reserve seen since take-off) when that is shorter.
            // correction 2026-10-22 (ADR-0040 D5): the reserve is the game's own map ring (RangeOnMap) out and back, what the player sees on the map
            m.RingNm = b.RingNm;
            if (gameFuel && b.RingNm >= 0) b.ReserveNm = CapMath.RingReserveNm(b.RingNm);
            m.GameMaxNm = b.MaxRangeNm;
            if (gameFuel && m.OwnLaunch && b.ReserveNm > 0 && b.MaxRangeNm > 0)
            {
                m.TopReserveNm = Math.Max(m.TopReserveNm, b.ReserveNm);
                if (m.TopReserveNm < b.MaxRangeNm) b.MaxRangeNm = m.TopReserveNm;
            }
            m.ReserveNm = b.ReserveNm; m.ReadyInS = 0; m.ReadyKnown = true; m.TakeoffInS = -1; m.UnlistedLogged = false;
            // 0.27.8: fuel % from the same quantity as the reserve and the leave threshold (path left before the return / full-tank path)
            m.FuelPct = CapMath.FuelPct(b.FuelFrac, b.ReserveNm, b.MaxRangeNm);   // T-197: the game's fraction, not the map ring
            // correction 2026-10-22 (ADR-0040 D5): the ring must stay under the threshold FuelHoldS before "fuel"; meanwhile the planner sees the reserve from before
            if (gameFuel && (b.RingNm >= 0 || CapMath.FracFuelKnown(b)))
            {
                string lowBy = CapMath.FuelLowBy(b, 0, g.P);
                bool low = lowBy != null;
                m.FuelLowBy = lowBy;
                if (!low) { m.FuelLowSince = -1; m.ReserveOkNm = b.ReserveNm; }
                else if (m.FuelLowSince < 0)
                {
                    m.FuelLowSince = now;
                    Log("fuel low uid=" + m.Uid + " by=" + lowBy + " game_ring_nm=" + F(b.RingNm) + " reserve_nm=" + F(b.ReserveNm) + " fuel_frac=" + F3(b.FuelFrac)
                        + " frac_reserve_nm=" + F(CapMath.FracReserveNm(b.FuelFrac, m.GameMaxNm)) + (lowBy == "hard" ? " reserve_unknown=hard_floor_" + F(CapMath.HardFloorPct * 100) + "pct" : "") + " (leave after " + F(CapMath.FuelHoldFor(b, lowBy, g.P)) + " s below)");
                }
                else if (CapMath.InCombat(b, g.P) && !CapMath.FuelOverridesPostpone(b)) m.FuelLowSince = now;   // T-315 (ADR-0122): the hold does not run in combat; T-337: except at live fuel <= 10 %
                b.FuelLowS = low ? now - m.FuelLowSince : 0;
                b.HeldReserveNm = low && b.FuelLowS < CapMath.FuelHoldFor(b, lowBy, g.P) && m.ReserveOkNm >= 0 ? m.ReserveOkNm : -1;
            }
            else m.FuelLowBy = null;
            if (m.Leaving != null)
            {
                if (m.Icpt != null) EndIntercept(g, m, b, "leaving");   // T-333
                b.Where = "leaving"; m.Reason = m.Leaving; m.LeaveInS = -1; m.SurfaceHold = false;   // the window shows "—": already on the way home
                if (b.Recovering) m.Recovered = true;
                if (m.Dash) DashTick(g, m, b, pos);
                ReRtb(g, m, b, now);
                return;
            }
            if (m.Alert) { AlertAirborne(g, m, b, contacts, now); return; }   // T-175: scrambled alert pair: guidance, hand-over, return
            if (g.P.AirOnly) SurfaceFilter(g, m, b, NearestHostileTo(contacts, pos));
            // T-244 (ADR-0102): with a circle lane "on station" = on the circle (radius + tolerance), not inside the (smaller) square
            bool onCircle = m.Slot >= 0 && m.Slot < lanes.Count && lanes[m.Slot].Circle;
            bool inZone = g.Ordered && (onCircle ? CapMath.OnCircle(pos, lanes[m.Slot], g.P) : CapMath.DistToZoneNm(pos, g.Zone) <= 0);
            b.Where = inZone ? "zone" : "to_zone";
            if (inZone && !m.OnStation)
            {
                m.OnStation = true;
                Log("on station uid=" + m.Uid + " slot=" + m.Slot + " group=" + g.Name);
                Evt("cap_on_station", "{\"group\":" + Q(g.Name) + ",\"unit\":" + m.Uid + ",\"slot\":" + m.Slot + "}");
            }
            double v = b.PatrolKn > 0 ? b.PatrolKn : 300;
            m.LeaveInS = b.MaxRangeNm > 0 ? CapMath.FuelLeaveInS(b, g.P) : -1;
            if (!g.Ordered) return;

            // leave decision: game rules first, then our thresholds (relief arrival is planned by CapMath.PlanRelief from the same margin)
            string postponed;
            double nh = NearestHostileTo(contacts, pos);
            string reason = CapMath.LeaveReason(b, 0, nh, g.P, out postponed);
            if (reason != null)
            {
                LogPostponeOverride(m, b, reason, nh, g.P);
                if (m.Postponed != null) { Evt("cap_postponed", "{\"unit\":" + m.Uid + ",\"reason\":" + Q(m.Postponed) + ",\"on\":false}"); Log("postpone end uid=" + m.Uid); m.Postponed = null; }
                StartLeave(g, m, b, reason, nh);
                return;
            }
            if (postponed != m.Postponed)
            {
                if (postponed != null) { Log("postpone uid=" + m.Uid + " " + postponed + " hostile_nm=" + (nh == double.MaxValue ? "-" : F(nh))); Evt("cap_postponed", "{\"unit\":" + m.Uid + ",\"reason\":" + Q(postponed) + ",\"on\":true}"); }
                else { Log("postpone end uid=" + m.Uid); Evt("cap_postponed", "{\"unit\":" + m.Uid + ",\"reason\":" + Q(m.Postponed) + ",\"on\":false}"); }
                m.Postponed = postponed;
            }
            m.Reason = m.Postponed;
            if (g.Kind == "patrol" && InterceptAirborne(g, m, b, contacts, now)) return;   // T-333 (ADR-0129): guided to a target in / near the zone: no leash, no station speed, no lane route

            // leash
            if (m.OnStation && CapMath.BeyondLeash(pos, g.Zone, g.P) && now - m.LastLeash >= LeashGapS)
            {
                m.LastLeash = now; m.RouteSig = null;
                try { _g.BreakPursuit(m.Uid); } catch (Exception) { }
                double d = CapMath.DistToZoneNm(pos, g.Zone);
                Log("leash uid=" + m.Uid + " dist_nm=" + F(d));
                Evt("cap_leash", "{\"unit\":" + m.Uid + ",\"dist_nm\":" + Json.Num(d) + "}");
            }

            // route. A member without a slot (an adopted extra) takes the first free slot, else it holds over the zone (an orbit at the zone centre);
            // T-179: PlanRelief does not count it as a holder or a relief - the "join_spare" mechanism of ADR-0075 supplement 1 is gone
            if (m.Slot < 0)
            {
                int fs = FreeSlot(g, m);
                if (fs >= 0) { m.Slot = fs; m.RouteSig = null; b.Slot = fs; Log("extra uid=" + m.Uid + " takes slot=" + fs + " group=" + g.Name); }
            }
            if (b.Evading) return;
            if (m.OnStation) StationThrottle(g, m, b, now);   // T-257
            CapLane lane;
            if (m.Slot >= 0 && m.Slot < lanes.Count) lane = lanes[m.Slot];
            else if (m.Slot < 0 && g.Zone.Rect != null && g.Zone.Rect.Length >= 4)
                lane = new CapLane { Slot = -1, Orbit = true, Center = new GeoPt((g.Zone.Rect[0] + g.Zone.Rect[2]) / 2, GeoLon.MidLon(g.Zone.Rect[1], g.Zone.Rect[3])) };
            else return;
            if (lane.Track == null) lane.Track = new List<GeoPt> { lane.Center };
            string sig = RouteSig(lane, g.P);
            int left = _g.WaypointsLeft(m.Uid);
            // T-250: a bird on the circle with a phase record gets a route from the point ahead of it, radius = the correction; the early
            // (fix-driven) re-issue comes at most once per lap, the natural one (left <= 1) and a changed signature are never held back
            CapPhaseFix fix = null;
            if (lane.Circle && m.OnStation && g.Phase != null) g.Phase.TryGetValue(m.Uid, out fix);
            bool need = m.RouteSig != sig || left <= 1;   // T-331: an orbit lane is a circle now (no one-point route that is finished)
            if (!need && fix != null && fix.Fix && now - m.LastCircleRouteAt >= CapMath.LapS(fix.RadiusNm, b.PatrolKn, g.P)) need = true;
            if (!need) return;
            var route = fix != null ? CapMath.CircleRouteFrom(lane, fix.PhaseDeg, fix.RadiusNm, fix.Fix ? 1 : 2, g.P) : CapMath.PatrolRoute(lane, g.P);
            string err = _g.Patrol(m.Uid, route, lane.AltFt > 0 ? lane.AltFt : (int)g.P.PatrolAltFt,
                m.StationSetStep > 0 && now - m.StationSetAt < g.P.StationKeepS ? m.StationSetStep : 0);
            bool ok = err == null && _g.WaypointsLeft(m.Uid) > 0;
            if (ok)
            {
                m.RouteSig = sig; m.Fail = 0;
                if (lane.Circle) m.LastCircleRouteAt = now;
                if (fix != null && fix.Fix) Log("phase fix uid=" + m.Uid + " err_deg=" + F(fix.ErrDeg) + " r_nm=" + F(fix.RadiusNm) + " group=" + g.Name);
                if (m.TcWarned) { m.TcWarned = false; ClearWarn(g, "tc_blocked"); }
                Dbg("route uid=" + m.Uid + " slot=" + m.Slot + " pts=" + route.Count);
            }
            else
            {
                m.Fail++;
                if (m.Fail == 1) Log("route not accepted uid=" + m.Uid + " " + (err ?? "silent"));
                if (m.Fail >= 2 && _g.TimeScale > 1 && !m.TcWarned) { m.TcWarned = true; Warn(g, "tc_blocked", null, "\"unit\":" + m.Uid); }
            }
        }

        /// <summary>0.27.8 live: the deck can cancel the return (landing circuit cancelled, home base lost) and the aircraft flies on with no order.
        /// A member the mod sent home gets the RTB again at most every 3 min (T-332; not while it fights/evades) until it is returning (no home base: the game searches a new one).</summary>
        private void ReRtb(Grp g, Mem m, CapBird b, double now)
        {
            if (b.Returning || m.Leaving.StartsWith("game_", StringComparison.Ordinal)) return;
            // T-332: the game AI overrides the order while the bird fights or evades (state MoveToEngage...), so a repeat only spams "Roger, returning to base":
            // wait the fight out; on the way home by the game's own states (RTB/bingo/winchester) nothing to repeat either
            string why = b.Engaged || b.AiActive || b.Evading ? "fight" : (b.GameRtb || b.GameBingo || b.GameWinchester || b.AiState == "ReturnToBase" ? "game_return" : null);
            if (why != null) { if (m.RtbHold != why) { m.RtbHold = why; Log("rtb again held uid=" + m.Uid + " group=" + g.Name + " reason=" + why + " state=" + (b.AiState ?? "-")); } return; }
            m.RtbHold = null;
            if (now - m.LastRtb < RtbAgainGapS) return;
            m.LastRtb = now;
            string err = null;
            try { err = _g.Rtb(m.Uid); } catch (Exception e) { err = e.GetType().Name; }
            if (err != m.RtbErr || err == null) Log("rtb again uid=" + m.Uid + " group=" + g.Name + " (not returning, state=" + (b.AiState ?? "-") + ", gap_s=" + F(RtbAgainGapS) + ") -> " + (err ?? "ok"));
            m.RtbErr = err;
        }

        private double CoverOf(Grp g) { return g.CoverNm > 0 ? g.CoverNm : g.P.CoverFallbackNm; }

        /// <summary>T-235 (ADR-0097): Rc of the group = CoverAamFrac x the shortest longest-AAM range of its aircraft. A bird on a deck counts with the AAMs of the loadout
        /// of the group order (else of its deck row, else the auto pick); an airborne bird with what it carries now; no data -> CoverFallbackNm.</summary>
        private void UpdateCover(Grp g, Dictionary<int, CapBird> birds)
        {
            var rs = new List<double>();
            foreach (var m in g.Members)
            {
                if (m.Alert) continue;
                CapBird raw; birds.TryGetValue(m.Uid, out raw);
                double r = 0;
                if (m.Airborne) { if (raw != null && !Deckish(raw.Where)) r = raw.AamMaxNm; }
                else { try { r = DeckAamNm(g, m, raw); } catch (Exception) { r = 0; } }
                if (r > 0) rs.Add(r);
            }
            g.CoverNm = CapMath.CoverNm(rs, g.P);
            g.CoverSrc = rs.Count > 0 ? "aam" : "fallback";
        }

        private double DeckAamNm(Grp g, Mem m, CapBird raw)
        {
            int deck = m.DeckUid > 0 ? m.DeckUid : (raw != null ? raw.DeckUid : 0);
            string type = m.Type ?? (raw != null ? raw.Type : null);
            if (deck <= 0 || type == null) return 0;
            var los = RawLoadouts(deck, type);
            if (los == null || los.Count == 0) return 0;
            string name; g.Loadouts.TryGetValue(type, out name);
            if (string.IsNullOrEmpty(name) || name == "auto") name = m.RowLoadout ?? (raw != null ? raw.Loadout : null);
            LoadoutOption pick = null;
            if (!string.IsNullOrEmpty(name)) foreach (var lo in los) if (string.Equals(lo.Name, name, StringComparison.OrdinalIgnoreCase)) { pick = lo; break; }
            if (pick == null) { int i = CapMath.PickLoadout(los, _g.Ammo, g.P, null); if (i >= 0) pick = los[i]; }
            if (pick == null || pick.Weapons == null) return 0;
            double best = 0;
            foreach (var kv in pick.Weapons)
            {
                if (kv.Value <= 0) continue;
                var a = _g.Ammo(kv.Key);
                if (a != null && CapMath.IsAam(a)) best = Math.Max(best, a.MaxLaunchRangeNm);
            }
            return best;
        }

        /// <summary>T-250: phase plan of the birds that fly the shared circle (on station, not leaving / evading / alert); one 'phase' log line per CirclePhaseLogS.</summary>
        private void UpdatePhase(Grp g, Dictionary<int, CapBird> birds, List<CapLane> lanes, double now)
        {
            g.Phase = null;
            if (!g.Ordered || g.Kind != "patrol" || lanes.Count == 0 || !lanes[0].Circle) return;
            var uids = new List<int>(); var ph = new List<double>(); var kn = new List<double>();
            foreach (var m in g.Members)
            {
                CapBird b;
                if (!m.Airborne || !m.OnStation || m.Leaving != null || m.Alert || m.Icpt != null || m.Slot < 0 || m.Slot >= lanes.Count) continue;
                if (!birds.TryGetValue(m.Uid, out b) || b.Evading) continue;
                var pos = new GeoPt(b.Lat, b.Lon);
                if (!CapMath.OnCircle(pos, lanes[m.Slot], g.P)) continue;
                uids.Add(m.Uid); ph.Add(CapMath.CirclePhaseDeg(pos, lanes[0].Center)); kn.Add(b.PatrolKn);
            }
            var plan = CapMath.PhasePlan(uids, ph, kn, lanes[0].RadiusNm, g.P);
            if (plan.Count == 0) return;
            g.Phase = new Dictionary<int, CapPhaseFix>();
            foreach (var f in plan) g.Phase[f.Uid] = f;
            if (now - g.LastPhaseLog >= g.P.CirclePhaseLogS)
            {
                g.LastPhaseLog = now;
                double mx; string txt = CapMath.PhaseText(plan, out mx);
                Log("phase group=" + g.Name + " k=" + plan.Count + " err_max_deg=" + F(mx) + " birds=" + txt.Replace(' ', ','));
            }
        }

        private void LogLanes(Grp g, List<CapLane> lanes)
        {
            if (lanes == null || lanes.Count == 0) return;
            var l0 = lanes[0];
            string sig = g.N + "|" + l0.Legs + "|" + F(l0.CoverNm) + "|" + (l0.Circle ? "c" + F(l0.RadiusNm) : l0.Orbit ? "o" : "r") + "|" + g.CoverSrc;
            if (sig == g.LanesSig) return;
            g.LanesSig = sig;
            Log("lanes group=" + g.Name + " n=" + lanes.Count + " legs=" + l0.Legs + " spacing_nm=" + F(l0.LegSpacingNm) + " cover_nm=" + F(l0.CoverNm) + " src=" + g.CoverSrc + (l0.Orbit ? " orbit" : "")
                + (l0.Circle ? " circle r_nm=" + F(l0.RadiusNm) + " points=" + l0.Track.Count + " alt_step_ft=" + F(g.P.CircleAltStepFt) + " alt_ft=" + l0.AltFt + ".." + lanes[lanes.Count - 1].AltFt : ""));
        }

        private static string RouteSig(CapLane l, CapParams p)
        {
            var sb = new StringBuilder();
            foreach (var pt in l.Track) sb.Append(pt.Lat.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',').Append(pt.Lon.ToString("0.0000", CultureInfo.InvariantCulture)).Append(';');
            return sb.Append((int)p.PatrolAltFt).Append('|').Append(l.AltFt).ToString();
        }

        private void Adopt(Grp g, Mem m, CapBird raw, double now)
        {
            m.Airborne = true; m.AirSince = now;
            m.AamStart = raw.AamStart > 0 ? raw.AamStart : raw.AamNow;
            m.LongStart = raw.LongStart > 0 ? raw.LongStart : raw.LongNow;
            m.Loadout = raw.Loadout;
            m.OwnLaunch = m.Key != null; m.TopReserveNm = -1; m.FuelLowSince = -1; m.ReserveOkNm = -1;
            if (m.Key != null) { _ourKeys.Remove(m.Key); m.Key = null; }
            m.Allowed = false;
            if (m.Alert) { string prevA; if (_g.SetRoe(m.Uid, g.Roe, out prevA) == null) { m.PrevRoe = prevA; m.RoeSet = true; } }   // T-175: the pair fights by the ROE of the group
            if (g.Ordered && !m.Alert)
            {
                int s = m.PlanSlot;
                if (s < 0 || s >= g.N)
                {
                    var used = new HashSet<int>();
                    foreach (var o in g.Members) if (o != m && o.Slot >= 0 && o.Leaving == null) used.Add(o.Slot);
                    s = 0; while (s < g.N && used.Contains(s)) s++;
                    if (s >= g.N) s = -1;
                }
                m.Slot = s;
                string prev;
                if (_g.SetRoe(m.Uid, g.Roe, out prev) == null) { m.PrevRoe = prev; m.RoeSet = true; }
            }
            m.PlanSlot = -1; m.ReliefEvt = false;
            Log("adopt uid=" + m.Uid + " group=" + g.Name + " slot=" + m.Slot + " aam=" + m.AamStart + " long=" + m.LongStart + " roe=" + g.Roe + " prev=" + m.PrevRoe);
        }

        // Birds without any fuel data would never go home (CapMath treats 0/0 as unlimited). Conservative fallback (T-112, coordinator note).
        private void FuelFallback(Grp g, Mem m, CapBird b, double now)
        {
            if (b.ReserveNm > 0 || b.MaxRangeNm > 0) return;
            double v = b.PatrolKn > 0 ? b.PatrolKn : 300;
            double max = v * g.P.NoFuelDataOnStationS / 3600.0;
            b.PatrolKn = v; b.MaxRangeNm = max;
            bool frac = b.FuelFrac >= 0;
            b.ReserveNm = frac ? Math.Min(1.0, b.FuelFrac) * max : Math.Max(0, max - v * Math.Max(0, now - m.AirSince) / 3600.0);
            if (!m.NoFuelWarned)
            {
                m.NoFuelWarned = true;
                Warn(g, "no_fuel_data", b.Type, "\"unit\":" + m.Uid + ",\"fallback\":" + Q(frac ? "fuel_fraction" : "fixed_time"));
            }
        }

        /// <summary>T-337: log a fuel leave that combat / hostile_near would have postponed (live fuel <= HardFloorPct).</summary>
        private void LogPostponeOverride(Mem m, CapBird b, string reason, double nh, CapParams p)
        {
            if (reason != "fuel" || !CapMath.FuelOverridesPostpone(b)) return;
            string held = CapMath.Postpone(b, nh, p);
            if (held == null) return;
            Log("postpone override uid=" + m.Uid + " " + held + " by=" + (m.FuelLowBy ?? "-") + " fuel_frac=" + F3(b.FuelFrac) + " hard_floor_" + F(CapMath.HardFloorPct * 100) + "pct");
        }

        private void StartLeave(Grp g, Mem m, CapBird b, string reason, double nh)
        {
            if (m.Icpt != null) EndIntercept(g, m, b, "leave:" + reason);   // T-333
            RestoreThrottle(m); m.AlertThrottle = -1;   // T-197: the dash of an intercept ends before the way home (the game's Rtb sets the cruise step, DashTick would read it as the player)
            m.Leaving = reason; m.Reason = reason; m.Slot = -1; m.OnStation = false; m.RouteSig = null; m.LastCircleRouteAt = -1e9; m.LastRtb = _now; m.RtbErr = null; m.Recovered = false;
            bool game = reason.StartsWith("game_", StringComparison.Ordinal);
            // ADR-0040 D2: the speed step is read before the RTB order (the order may change it)
            bool dash = g.P.DashOnMissileRtb && (reason == "missiles" || reason == "long_range" || (reason == "game_winchester" && b.Controllable));
            int cur = -1; IList<CapStep> steps = null;
            if (dash) { try { cur = _g.Throttle(m.Uid); steps = _g.ThrottleSteps(m.Uid); } catch (Exception) { } }
            if (!game) { try { _g.Rtb(m.Uid); } catch (Exception) { } }
            string speed = "cruise", dnote = "";
            if (dash)
            {
                var dp = m.DeckUid > 0 ? _g.UnitPos(m.DeckUid) : null;
                if (steps == null || steps.Count == 0 || cur < 0 || dp == null) dnote = "dash=cruise (no " + (dp == null ? "deck" : "steps") + ")";
                else
                {
                    double d = AswMath.DistNm(new GeoPt(b.Lat, b.Lon), dp.Value);
                    string note;
                    int pick = CapMath.DashStep(steps, cur, d, b.ReserveNm, b.MaxRangeNm, double.MaxValue, g.P, out note);
                    dnote = "dash D=" + F(d) + " reserve=" + F(b.ReserveNm) + " step=" + cur + "->" + pick + " " + note;
                    if (pick != cur && _g.SetThrottle(m.Uid, pick) == null)
                    {
                        m.Dash = true; m.PrevThrottle = cur; m.DashStep = pick;
                        int top = steps[0].Step; double topKn = steps[0].Kn;
                        foreach (var x in steps) if (x.Kn > topKn) { topKn = x.Kn; top = x.Step; }
                        speed = pick == top ? "max" : pick.ToString(CultureInfo.InvariantCulture);
                    }
                    else dnote += " (cruise)";
                }
            }
            Log("leave uid=" + m.Uid + " reason=" + reason + " aam=" + b.AamNow + "/" + m.AamStart + " reserve_nm=" + F(b.ReserveNm)
                + " frac_reserve_nm=" + F(CapMath.FracReserveNm(b.FuelFrac, m.GameMaxNm)) + " low_by=" + (m.FuelLowBy ?? "-")
                + " thr_nm=" + F(ThrNm(b, g)) + (CapMath.FracFuelKnown(b) ? " floor_pct=" + F(g.P.FuelFloorPct * 100) : "") + " max_nm=" + F(b.MaxRangeNm) + " game_max_nm=" + F(m.GameMaxNm)
                + " fuel_pct=" + F(m.FuelPct) + (m.RingNm >= 0 ? " game_ring_nm=" + F(m.RingNm) + " low_s=" + (m.FuelLowSince < 0 ? "0" : F(_now - m.FuelLowSince)) : "") + " hostile_nm=" + (nh == double.MaxValue ? "-" : F(nh)) + " speed=" + speed + (dnote != "" ? " " + dnote : ""));
            Evt("cap_rtb", "{\"group\":" + Q(g.Name) + ",\"unit\":" + m.Uid + ",\"reason\":" + Q(reason) + ",\"speed\":" + Q(speed) + "}");
            if (reason == "game_bingo") Warn(g, "bingo_fuel", null, "\"unit\":" + m.Uid);
            if (g.P.RtbRoeHold && b.Controllable) RoeHold(m, "rtb");   // T-175 (ADR-0075 item 5.2): no fire on the way home; the ROE of the group comes back with the next sortie
            b.Where = "leaving";
        }

        // Fast return: every second re-check with the current D and reserve; only step down (ADR-0040 D2 p.4). A foreign throttle change = player.
        private void DashTick(Grp g, Mem m, CapBird b, GeoPt pos)
        {
            int cur;
            try { cur = _g.Throttle(m.Uid); } catch (Exception) { return; }
            if (m.DashStep < 0) m.DashStep = cur;
            if (cur != m.DashStep && _g.AiWindow(m.Uid))
            {
                Log("dash throttle ai uid=" + m.Uid + " " + m.DashStep + "->" + cur);   // T-317 (ADR-0124): the game AI changed the telegraph in its combat
                m.DashStep = cur;
            }
            if (cur != m.DashStep)
            {
                Warn(g, "player_took", null, "\"unit\":" + m.Uid);
                Log("throttle changed outside the mod uid=" + m.Uid + " " + m.DashStep + "->" + cur + " " + (_g.TakenDetail(m.Uid) ?? ""));
                DropMember(g, m, false, "player_took");
                return;
            }
            var dp = m.DeckUid > 0 ? _g.UnitPos(m.DeckUid) : null;
            var steps = _g.ThrottleSteps(m.Uid);
            if (dp == null || steps == null || steps.Count == 0)
            {
                // no deck position or no steps: fall back to the saved cruise step (only down) and stop the fast return
                if (m.PrevThrottle >= 0 && m.PrevThrottle != m.DashStep && _g.SetThrottle(m.Uid, m.PrevThrottle) == null)
                    Log("dash_down uid=" + m.Uid + " step " + m.DashStep + "->" + m.PrevThrottle + " (no deck position or steps)");
                m.Dash = false; m.DashStep = -1;
                return;
            }
            double curKn = 0, kn0 = 0;
            foreach (var x in steps) { if (x.Step == m.DashStep) curKn = x.Kn; if (x.Step == m.PrevThrottle) kn0 = x.Kn; }
            if (curKn <= kn0) return;   // already cruise
            string note;
            int pick = CapMath.DashStep(steps, m.PrevThrottle, AswMath.DistNm(pos, dp.Value), b.ReserveNm, b.MaxRangeNm, curKn, g.P, out note);
            if (pick != m.DashStep && _g.SetThrottle(m.Uid, pick) == null)
            {
                Log("dash_down uid=" + m.Uid + " step " + m.DashStep + "->" + pick + " reserve=" + F(b.ReserveNm) + " " + note);
                m.DashStep = pick;
            }
        }

        private void RestoreThrottle(Mem m)
        {
            if (!m.Dash) return;
            if (m.PrevThrottle >= 0) { try { _g.SetThrottle(m.Uid, m.PrevThrottle); } catch (Exception) { } }
            Log("throttle restored uid=" + m.Uid + " step=" + m.PrevThrottle);
            m.Dash = false; m.DashStep = -1; m.PrevThrottle = -1;
        }

        private void Exec(Grp g, CapAction a, IList<CapBird> copies)
        {
            Mem m = MemOf(g, a.Uid);
            switch (a.Kind)
            {
                case "warn":
                    if (a.Code == "continuity_gap") return;   // edge-triggered in TickGroup
                    Warn(g, a.Code, a.Type, a.Loadout != null ? "\"loadout\":" + Q(a.Loadout) : null);
                    return;
                case "abort":
                    if (m == null || m.Key != a.Key) return;
                    Log("abort request uid=" + m.Uid + " key=" + a.Key + " (" + a.Reason + ")");
                    AbortKey(m, "planner:" + a.Reason);
                    return;
                case "allow":
                    if (m == null || m.Key == null) return;
                    if (m.Allowed) { m.PlanSlot = a.Slot; return; }
                    if (m.Pending) return;
                    {
                        var am = m; var aa = a;
                        if (SubmitDeck(g, AirRelief.Allow(m.DeckUid, m.Key, "cap planner:" + a.Reason + " uid=" + m.Uid), res =>
                        {
                            am.Pending = false;
                            Log("allow uid=" + am.Uid + " key=" + am.Key + " -> " + (res.Err ?? "ok"));
                            if (res.Err == null) { am.Allowed = true; am.PlanSlot = aa.Slot; ReliefEvent(g, am, aa); }
                        })) m.Pending = true;
                    }
                    return;
                case "launch":
                case "order":
                    if (m == null || m.Key != null || m.Pending) return;
                    if (m.Where == "deck_cool") return;   // a landed airframe in its cool-down: ordered when it is back in the hangar (its ETA counts the cool-down)
                    if (!OrderDue(g, a.DeckUid, a.Type, "uid=" + m.Uid)) return;   // T-187: the deck refused this pair a moment ago
                    {
                        bool allow = a.Kind == "launch" || a.Reason == "relief";
                        var om = m; var oa = a;
                        var req = AirRelief.Order(a.DeckUid, a.Type, a.Loadout, g.P.LaunchMissionType, allow);
                        if (SubmitDeck(g, req, res =>
                        {
                            om.Pending = false;
                            string e = res.Err, key = res.Key;
                            if (e == null)
                            {
                                OrderOk(g, oa.DeckUid, oa.Type);
                                om.Key = key; om.Allowed = allow; om.PlanSlot = allow ? oa.Slot : -1; om.DeckUid = oa.DeckUid; _ourKeys.Add(key);
                                AfterOrder(g, om);
                                Log("order launch uid=" + om.Uid + " type=" + oa.Type + " loadout=" + oa.Loadout + " allow=" + allow + " ready_s=" + F(res.ReadyS) + " slot=" + oa.Slot);
                                if (allow) ReliefEvent(g, om, oa);
                            }
                            else if (e == "no_ammo")
                            {
                                g.Banned[oa.DeckUid + "|" + oa.Type + "|" + oa.Loadout] = _now + BanS;
                                Log("no ammo for " + oa.Loadout + " type=" + oa.Type + ", trying the next loadout");
                                Warn(g, "loadout_fallback", oa.Type, "\"from\":" + Q(oa.Loadout));
                            }
                            else OrderFailed(g, oa.DeckUid, oa.Type, e, "uid=" + om.Uid);
                        })) m.Pending = true;
                    }
                    return;
            }
        }

        private void ReliefEvent(Grp g, Mem relief, CapAction a)
        {
            if (relief.ReliefEvt || a.Slot < 0) return;
            Mem holder = null;
            foreach (var h in g.Members) if (h != relief && h.Slot == a.Slot && h.Airborne && h.Leaving == null) { holder = h; break; }
            if (holder == null) return;
            relief.ReliefEvt = true;
            var hb = new CapBird { AamNow = holder.AamNow, AamStart = holder.AamStart, LongStart = holder.LongStart };
            string why = CapMath.MissileLeave(hb, g.P) != null ? "missiles" : "fuel";
            Log("relief slot=" + a.Slot + " out=" + holder.Uid + " in=" + relief.Uid + " reason=" + why + " eta_s=" + F(a.EtaS));
            Evt("cap_relief", "{\"group\":" + Q(g.Name) + ",\"out\":" + holder.Uid + ",\"in\":" + relief.Uid + ",\"reason\":" + Q(why) + "}");
        }

        // ================================================= air threat service (ADR-0079 section 8) ====================================
        /// <summary>The alert groups for the air threat service (cards, x1, "bird lost"): window "cap", kind "cap_alert", the protected ship and the decks as the area.</summary>
        internal IList<AirGuardGroup> AirGuardGroups()
        {
            var res = new List<AirGuardGroup>();
            foreach (var g in _groups)
            {
                if (g.Kind != "alert" || !g.Ordered || g.A == null) continue;
                var d = new AirGuardGroup { Window = Win, Name = g.Name, Kind = "cap_alert" };
                foreach (var m in g.Members)
                {
                    d.Uids.Add(m.Uid);
                    if (m.DeckUid != 0 && !d.DeckUids.Contains(m.DeckUid)) d.DeckUids.Add(m.DeckUid);
                }
                var rp = AlertRefPoint(g);
                if (rp.HasValue) d.AreaPts.Add(rp.Value);
                foreach (int du in d.DeckUids) { var dp = _g.UnitPos(du); if (dp.HasValue) d.AreaPts.Add(dp.Value); }
                res.Add(d);
            }
            return res;
        }

        // ================================================= deck priority ==============================================================
        public List<AirGroupPrio> DeckPrio()
        {
            var l = new List<AirGroupPrio>();
            foreach (var g in _groups)
                l.Add(new AirGroupPrio { Name = g.Name, Order = "cap", Window = Win, HasContact = false, WindowIndex = _own.GroupSeq(Win, g.Name), CapGapUnderThreat = g.Prio });
            return l;
        }

        // ================================================= view / state ===============================================================
        /// <summary>Light view for the overlay and the reference point: groups only, no candidates, never touches the game (Birds()).</summary>
        public CapView ViewLight() { return BuildView(false); }

        /// <summary>Full view for the window: candidates come from the Birds() cache refreshed at most once per tick second.</summary>
        public CapView View() { return BuildView(true); }

        private CapView BuildView(bool candidates)
        {
            var v = new CapView { RestorePending = _pending != null, RestoreWaiting = _deferred != null };
            var inGroup = new HashSet<int>();
            foreach (var g in _groups)
            {
                var gv = new CapGroupView { AutoName = g.AutoName, Roe = g.Roe, Anchor = g.Zone.Anchor, Rect = g.Zone.Rect, N = g.N, Ordered = g.Ordered, K = g.K,
                                       AltFt = g.P.PatrolAltFt, OrderSeq = g.OrderSeq, HasPolar = g.Zone.HasPolar, PolarBearing = g.Zone.PolarBearing,
                                       PolarDistNm = g.Zone.PolarDistNm, PolarSizeNm = g.Zone.PolarSizeNm, PolarWNm = g.Zone.PolarWNm, AirOnly = g.P.AirOnly,
                                       Kind = g.Kind, AlertSpec = g.A == null ? null : g.A.Clone(), AcceptAlert = g.AcceptAlert, Icpt = g.Kind == "patrol" && g.I != null ? g.I.Clone() : null };
                foreach (var kv in g.Loadouts) gv.Loadouts[kv.Key] = kv.Value;
                if (g.Ordered && g.Kind == "patrol" && g.Zone.Rect != null)
                {
                    gv.CoverNm = CoverOf(g);
                    gv.Lanes = CapMath.Lanes(g.Zone, Math.Max(1, g.N), g.P, gv.CoverNm);
                }
                int on = 0, rel = 0, ready = 0; double nextRelief = -1;
                var ffHold = FfHoldMap(); var icptTg = new HashSet<int>();
                foreach (var m in g.Members)
                {
                    inGroup.Add(m.Uid);
                    if (m.Icpt != null) { gv.IcptBirds++; icptTg.Add(m.Icpt.Target); }
                    if (m.Where == "zone") on++;
                    if (m.Key != null && m.Allowed) rel++;
                    if (m.Where == "deck_ready") ready++;
                    if (m.Airborne && g.Kind == "alert") gv.Airborne++;
                    if (g.Kind == "alert" && m.Alert && m.LentFrom == null && m.AlertState == null && !m.Airborne && m.Where != "deck_down" && m.Where != "deck_cool" && Standing(m)
                        && (g.A != null && g.A.Level == "hangar" ? (m.Key == null || !m.Allowed) : (m.Key != null && !m.Allowed && (m.Where == "deck_ready" || m.Where == "deck_prep")))) gv.ReadyNow++;
                    if (g.Kind == "alert" && m.Alert && m.LentFrom == null && m.AlertState == null && !m.Airborne && !m.Allowed && (m.Where == "deck_cool" || m.Where == "deck_prep"))
                    {
                        gv.PrepN++;   // T-204: not ready yet - cooling after a landing or preparing; the window says "preparing N" instead of an empty group
                        if (m.ReadyInS >= 0) gv.PrepInS = gv.PrepInS < 0 ? m.ReadyInS : Math.Min(gv.PrepInS, m.ReadyInS);
                    }
                    if (m.Key != null && m.Allowed && m.ReadyInS >= 0) nextRelief = nextRelief < 0 ? m.ReadyInS : Math.Min(nextRelief, m.ReadyInS);
                    gv.Members.Add(new CapMemberView { Uid = m.Uid, Name = m.Name, Type = m.Type, Slot = m.Slot, Where = m.Where, Reason = m.Reason, AamNow = m.AamNow, AamStart = m.AamStart,
                                                      ShipUid = m.DeckUid, ShipName = m.ShipName, TypeLabel = m.TypeLabel, Serial = m.Serial, Air = CapWindowModel.IsAirWhere(m.Where),
                                                      ReserveNm = m.ReserveNm, LeaveInS = m.LeaveInS, ReadyInS = m.ReadyInS, ReadyKnown = m.ReadyKnown, TakeoffInS = m.TakeoffInS, FuelPct = m.FuelPct, Loadout = m.Loadout, Alert = m.Alert, HangarK = m.HangarK, HangarN = m.HangarN,
                                                      AlertState = m.AlertState, LentFrom = m.LentOrphan ? null : m.LentFrom, AutoFill = m.AutoFill, Radar = RadarState(m.Uid) });
                    FillIcptView(g, m, gv.Members[gv.Members.Count - 1], 0, ffHold);   // T-333
                }
                gv.IcptTargets = icptTg.Count;
                if (g.Kind == "alert") foreach (var lr in g.Lent) gv.Lent.Add(new KeyValuePair<int, string>(lr.M.Uid, lr.To));
                if (g.Kind == "alert" && g.A != null && g.A.Level == "hangar") gv.ReadyNow = Math.Min(gv.ReadyNow, g.A.Ready);
                gv.ScrambleBlock = g.Kind != "alert" || !g.Ordered ? "not_alert" : (gv.ReadyNow == 0 ? "no_ready" : null);
                double w = g.Zone.Rect == null ? 0 : GeoLon.DLon(g.Zone.Rect[1], g.Zone.Rect[3]) * 60.0 * AswMath.LonK(g.Zone.Rect[0], g.Zone.Rect[2]), h = g.Zone.Rect == null ? 0 : (g.Zone.Rect[2] - g.Zone.Rect[0]) * 60.0;
                gv.Line = new CapGroupLine { Group = g.Name, WNm = w, HNm = h, OnStation = on, N = g.N, Relieving = rel, Ready = ready, NextReliefS = nextRelief, GapS = g.GapS,
                                             DeckPriority = g.Prio, Warns = new List<string>(g.Warns) };
                v.Groups.Add(gv);
            }
            if (!candidates) return v;
            if (_lastBirds == null || _now - _birdsAt >= 1.0 || _now < _birdsAt) { _lastBirds = _g.Birds(); _birdsAt = _now; }
            if (_viewLoadAt != _birdsAt) { _viewLoad.Clear(); _viewLoadAt = _birdsAt; }
            foreach (var b in _lastBirds ?? new List<CapBird>())
            {
                if (inGroup.Contains(b.Uid) || _g.TypeAdmission(b.Type) != null) continue;
                string[] vl;
                if (!_viewLoad.TryGetValue(b.Uid, out vl)) { vl = new[] { StrikeWhy(b), LoadoutTextOf(b) }; _viewLoad[b.Uid] = vl; }   // T-325: once per refresh of the birds
                string reason = null;
                string w; string o = _own.Owner(b.Uid, out w);
                if (o != null) reason = "in_group:" + o;
                else if (b.HangarK > 0 && !SlotFree(b)) { string hw, hg; _own.Slots.Held(b.DeckUid, b.Type, b.HangarK, out hw, out hg); o = hg; w = hw; reason = "in_group:" + hg; }   // T-186: claimed by a holder without an ownership record
                else if (_g.InGameCapZone(b.Uid)) reason = "game_cap_zone";
                else if (_g.TakenByPlayer(b.Uid)) reason = "player";
                else if (vl[0] != null) reason = vl[0];   // T-325: strike_loadout
                if (reason != null && reason.StartsWith("in_group:", StringComparison.Ordinal) && b.HangarK > 0 && !AirOpsMath.ShowForeignSlots) continue;   // T-186 (Q3)
                v.Candidates.Add(new CapCandidateView { Uid = b.Uid, Name = b.Name, Type = b.Type, Where = b.Where, Reason = reason, HangarK = b.HangarK, HangarN = b.HangarN, OwnerWindow = o != null ? w : null, ShipUid = b.DeckUid, ShipName = b.ShipName, TypeLabel = b.TypeLabel, Serial = b.Serial, LoadoutText = vl[1], Air = CapWindowModel.IsAirWhere(b.Where) });
            }
            return v;
        }

        public string StateJson()
        {
            var v = View();
            var sb = new StringBuilder("{\"groups\":[");
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i]; var gv = v.Groups[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"group\":").Append(Q(g.Name)).Append(",\"seq\":").Append(g.Seq).Append(",\"n\":").Append(g.N).Append(",\"k\":").Append(g.K);
                sb.Append(",\"ordered\":").Append(Json.Bool(g.Ordered)).Append(",\"roe\":").Append(Q(g.Roe)).Append(",\"kind\":").Append(Q(g.Kind)).Append(",\"accept_alert\":").Append(Json.Bool(g.AcceptAlert)).Append(",\"auto_name\":").Append(Json.Bool(g.AutoName));
                if (gv.Lanes != null && gv.Lanes.Count > 0) sb.Append(",\"cover_nm\":").Append(Json.Num(gv.CoverNm)).Append(",\"legs\":").Append(gv.Lanes[0].Legs).Append(",\"leg_spacing_nm\":").Append(Json.Num(gv.Lanes[0].LegSpacingNm));   // T-235
                if (g.Kind == "alert" && g.A != null)
                {
                    var A = g.A;
                    sb.Append(",\"alert\":{\"ref\":").Append(A.RefUid).Append(",\"ready\":").Append(A.Ready).Append(",\"per_raid\":").Append(A.PerRaid).Append(",\"level\":").Append(Q(A.Level))
                      .Append(",\"radius_nm\":").Append(Json.Num(A.RadiusNm)).Append(",\"targets\":[");
                    string tcsv = CapAlertMath.TargetsCsv(A.Targets); bool tf = true;
                    foreach (string t in tcsv.Split(',')) { if (t.Length == 0) continue; if (!tf) sb.Append(','); tf = false; sb.Append(Q(t)); }
                    sb.Append("],\"pursuit_nm\":").Append(Json.Num(A.PursuitNm)).Append(",\"speed\":").Append(Q(A.Speed)).Append(",\"roe\":").Append(Q(A.Roe)).Append(",\"after\":").Append(Q(A.After))
                      .Append(",\"fill\":").Append(Json.Bool(A.Fill)).Append('}');
                    sb.Append(",\"ready_now\":").Append(gv.ReadyNow).Append(",\"airborne\":").Append(gv.Airborne).Append(",\"lent\":[");
                    for (int j = 0; j < gv.Lent.Count; j++) { if (j > 0) sb.Append(','); sb.Append("{\"uid\":").Append(gv.Lent[j].Key).Append(",\"to\":").Append(Q(gv.Lent[j].Value)).Append('}'); }
                    sb.Append(']');
                }
                sb.Append(",\"anchor\":").Append(Q(g.Zone.Anchor)).Append(",\"anchor_uid\":").Append(g.Zone.AnchorUid).Append(",\"alt_ft\":").Append(Json.Num(g.P.PatrolAltFt));
                sb.Append(",\"rect\":[");
                if (g.Zone.Rect != null) for (int j = 0; j < 4; j++) { if (j > 0) sb.Append(','); sb.Append(Json.Num(g.Zone.Rect[j])); }
                sb.Append("],\"on_station\":").Append(gv.Line.OnStation).Append(",\"relieving\":").Append(gv.Line.Relieving).Append(",\"ready\":").Append(gv.Line.Ready);
                sb.Append(",\"gap\":").Append(Json.Bool(g.Gap)).Append(",\"gap_s\":").Append(Json.Num(g.GapS)).Append(",\"deck_priority\":").Append(Json.Bool(g.Prio));
                sb.Append(",\"loadouts\":{");
                bool first = true;
                foreach (var kv in g.Loadouts) { if (!first) sb.Append(','); first = false; sb.Append(Q(kv.Key)).Append(':').Append(Q(kv.Value)); }
                sb.Append("},\"p\":{\"missile_pct\":").Append(Json.Num(g.P.MissilePct)).Append(",\"fuel_margin_s\":").Append(Json.Num(g.P.FuelMarginS)).Append(",\"ring_margin_nm\":").Append(Json.Num(g.P.RingMarginNm))
                  .Append(",\"fuel_floor_pct\":").Append(Json.Num(g.P.FuelFloorPct)).Append(",\"need_long_range\":").Append(Json.Bool(g.P.NeedLongRange)).Append('}');
                sb.Append(",\"members\":[");
                for (int j = 0; j < gv.Members.Count; j++)
                {
                    var m = gv.Members[j]; if (j > 0) sb.Append(',');
                    sb.Append("{\"uid\":").Append(m.Uid).Append(",\"name\":").Append(Q(m.Name)).Append(",\"type\":").Append(Q(m.Type)).Append(",\"slot\":").Append(m.Slot);
                    sb.Append(",\"where\":").Append(Q(m.Where)).Append(",\"reason\":").Append(m.Reason == null ? "null" : Q(m.Reason));
                    sb.Append(",\"aam_now\":").Append(m.AamNow).Append(",\"aam_start\":").Append(m.AamStart).Append(",\"reserve_nm\":").Append(Json.Num(m.ReserveNm));
                    sb.Append(",\"leave_in_s\":").Append(Json.Num(m.LeaveInS)).Append(",\"ready_in_s\":").Append(Json.Num(m.ReadyInS));
                    if (m.HangarK > 0) sb.Append(",\"deck_key\":").Append(Q(AirOpsMath.SlotKey(m.Type, m.HangarK))).Append(",\"hangar_k\":").Append(m.HangarK).Append(",\"hangar_n\":").Append(m.HangarN);   // T-186
                    if (m.AlertState != null) sb.Append(",\"alert_state\":").Append(Q(m.AlertState));
                    if (m.LentFrom != null) sb.Append(",\"lent_from\":").Append(Q(m.LentFrom));
                    if (m.Radar != null) sb.Append(",\"radar\":").Append(Q(m.Radar));   // T-241
                    if (m.Icpt != null)   // T-333
                    {
                        sb.Append(",\"intercept\":{\"target\":").Append(m.IcptTarget).Append(",\"round\":").Append(m.IcptRound).Append(",\"phase\":").Append(Q(m.Icpt))
                          .Append(",\"eta_s\":").Append(Json.Num(m.IcptEtaS)).Append(",\"lost_s\":").Append(Json.Num(m.IcptLostS));
                        if (m.HasIp) sb.Append(",\"ip\":[").Append(Json.Num(m.IpLat)).Append(',').Append(Json.Num(m.IpLon)).Append(']');
                        sb.Append('}');
                    }
                    if (m.Ff >= 0) sb.Append(",\"ff\":{\"since_s\":").Append(Json.Num(Math.Round(m.Ff, 1))).Append('}');
                    sb.Append('}');
                }
                sb.Append(']');
                if (g.Kind == "patrol") AppendIcptJson(sb, gv, gv.Members);   // T-333
                sb.Append(",\"warns\":[");
                for (int j = 0; j < g.Warns.Count; j++) { if (j > 0) sb.Append(','); sb.Append(Q(g.Warns[j])); }
                sb.Append("]}");
            }
            sb.Append("],\"candidates\":[");
            for (int i = 0; i < v.Candidates.Count; i++)
            {
                var c = v.Candidates[i]; if (i > 0) sb.Append(',');
                sb.Append("{\"uid\":").Append(c.Uid).Append(",\"name\":").Append(Q(c.Name)).Append(",\"type\":").Append(Q(c.Type)).Append(",\"where\":").Append(Q(c.Where))
                  .Append(",\"reason\":").Append(c.Reason == null ? "null" : Q(c.Reason))
                  .Append(",\"loadout\":").Append(string.IsNullOrEmpty(c.LoadoutText) ? "null" : Q(c.LoadoutText));   // T-325
                if (c.HangarK > 0) sb.Append(",\"deck_key\":").Append(Q(AirOpsMath.SlotKey(c.Type, c.HangarK))).Append(",\"slot\":").Append(c.HangarK).Append(",\"hangar_n\":").Append(c.HangarN);   // T-186 (ADR-0082 s.5)
                if (c.OwnerWindow != null) sb.Append(",\"owner_window\":").Append(Q(c.OwnerWindow));
                sb.Append('}');
            }
            return sb.Append("],\"restore_pending\":").Append(Json.Bool(v.RestorePending)).Append(",\"restore_waiting\":").Append(Json.Bool(v.RestoreWaiting)).Append('}').ToString();
        }

        // ================================================= save / load ================================================================
        public CapSaveData Save()
        {
            var d = new CapSaveData { MissionTimeS = _now };
            if (_deferred != null && _groups.Count == 0)
            {
                // T-211: a save while the groups wait for the world after a load keeps the file as it was (the time is the running one)
                ShiftBindTimes(_deferred, _now);   // T-219: the times of the bindings move to the running clock
                d.Groups = new List<CapGroupDto>(_deferred.Groups); d.Memory = new List<CapMemDto>(_deferred.Memory);
                return d;
            }
            foreach (var g in _groups)
            {
                var dto = new CapGroupDto
                {
                    Group = g.Name, Seq = g.Seq, OnStation = g.N, Ready = g.K, Roe = g.Roe, Rect = g.Ordered && g.Kind == "patrol" ? g.Zone.Rect : null,
                    Anchor = g.Zone.Anchor, AnchorUid = g.Zone.AnchorUid, OffLat = g.Zone.OffLat, OffLon = g.Zone.OffLon,
                    Loadouts = new Dictionary<string, string>(g.Loadouts), P = g.P.Clone(), Members = new List<CapMemberDto>(), Keys = new List<string>(),
                    Kind = g.Kind == "alert" && g.Ordered && g.A != null ? "alert" : "patrol", AlertSpec = g.Kind == "alert" && g.A != null ? g.A.Clone() : null, AcceptAlert = g.AcceptAlert, AutoName = g.AutoName,
                    Icpt = g.Kind == "patrol" && g.I != null ? g.I.Clone() : null   // T-333
                };
                if (dto.Kind == "alert")
                {
                    // T-211 (ADR-0088): the raids this group served survive a load (no second scramble to the same raid)
                    if (g.Served.Count > 0) { dto.Served = new List<int>(g.Served); dto.Served.Sort(); }
                    if (g.ServedRaids.Count > 0) { dto.ServedRaids = new List<string>(g.ServedRaids); dto.ServedRaids.Sort(StringComparer.Ordinal); }
                    if (g.Memos.Count > 0) { dto.Memos = new List<RaidMemo>(); foreach (var mm in g.Memos) dto.Memos.Add(mm.Clone()); }   // T-219
                }
                foreach (var m in g.Members)
                {
                    // Slot: -2 = flies home; -3 = a guided / barrage bird of an alert group (no slot of its own)
                    int slot = m.Leaving != null && m.Airborne ? -2 : (m.Alert && m.Airborne && g.Kind == "alert" ? -3 : m.Slot);
                    var md0 = new CapMemberDto { Uid = m.Uid, Name = m.Name, Type = m.Type, Slot = slot, AamStart = m.AamStart, LongStart = m.LongStart,
                                                      LentFrom = m.LentOrphan ? null : m.LentFrom, AutoFill = m.AutoFill,
                                                      Deck = m.HangarK > 0 && !m.Airborne ? m.DeckUid : 0 };
                    if (!m.Airborne && m.HangarK <= 0 && Deckish(m.Where) && m.DeckUid > 0) { md0.TaskDeck = m.DeckUid; md0.Where = m.Where; md0.Loadout = m.RowLoadout; }   // T-218: the uid of a task row changes on a load
                    if (g.Kind == "alert") md0.Bind = SaveBind(m);   // T-219 (ADR-0090)
                    dto.Members.Add(md0);
                    if (m.Key != null && _ourKeys.Contains(m.Key)) dto.Keys.Add(m.Uid + "|" + m.DeckUid + "|" + m.Key);
                    var rdto = _radar != null ? _radar.Export(m.Uid) : null;   // T-241
                    if (m.RoeSet || m.Dash) d.Memory.Add(new CapMemDto { Uid = m.Uid, Roe = m.PrevRoe, Route = new List<double[]>(), Throttle = m.PrevThrottle, Dash = m.Dash, Radar = rdto });
                    else if (rdto != null) d.Memory.Add(new CapMemDto { Uid = m.Uid, Route = new List<double[]>(), Throttle = -1, Radar = rdto, RadarOnly = true });
                }
                foreach (var lr in g.Lent) dto.Members.Add(new CapMemberDto { Uid = lr.M.Uid, Name = lr.M.Name, Type = lr.M.Type, LentTo = lr.To });   // shadow of a bird that flies in a patrol group
                d.Groups.Add(dto);
            }
            return d;
        }

        private void ClearAll()
        {
            foreach (var g in _groups) _own.ReleaseGroup(Win, g.Name);
            _groups.Clear(); _ourKeys.Clear(); _pending = null; _deferred = null;
            _own.Slots.Forget(Win); _hangarWarnAt.Clear();
            if (_radar != null) _radar.ReleaseAll(Win);   // T-241: the memory goes, the radars stay as they are
        }

        public void Load(CapSaveData d, double now)
        {
            ClearAll();
            _now = now;
            if (d == null) return;
            var shadows = new List<KeyValuePair<Grp, CapMemberDto>>();
            Dictionary<int, CapBird> loadBirds = null; int missed = 0;
            var order = new List<CapGroupDto>(d.Groups);
            order.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            foreach (var dto in order)
            {
                var g = new Grp { Name = dto.Group, P = dto.P ?? _p.Clone(), N = Math.Max(1, dto.OnStation), K = dto.Ready, Roe = dto.Roe ?? _p.Roe, AcceptAlert = dto.AcceptAlert, AutoName = dto.AutoName ?? AswGroupNames.IsAuto(dto.Group, CapPrefixes()) };
                g.I = dto.Icpt != null ? dto.Icpt.Clone() : CapInterceptSpec.FromParams(_p);   // T-333: an old file has no block: the [Cap] defaults
                bool alertKind = dto.Kind == "alert" && dto.AlertSpec != null;
                if (alertKind)
                {
                    g.Kind = "alert"; g.A = dto.AlertSpec.Clone(); g.Ordered = true; g.Roe = g.A.Roe;
                    if (dto.Migrated == "alert_group")
                    {
                        // a format 1 file: the number and the reference ship are kept, the other settings are the [Cap] defaults of today
                        var def = CapAlertSpec.FromParams(_p); def.Ready = g.A.Ready; def.RefUid = g.A.RefUid; g.A = def; g.Roe = def.Roe;
                        Log("load migrate group=" + g.Name + " alert=" + def.Ready + " -> alert_group");
                    }
                }
                _own.RestoreSeq(Win, g.Name, dto.Seq);   // ADR-0042: the saved creation number survives the load
                g.Seq = _own.GroupSeq(Win, g.Name);
                g.Loadouts = dto.Loadouts != null ? new Dictionary<string, string>(dto.Loadouts) : new Dictionary<string, string>();
                g.Zone = new CapZone { Anchor = dto.Anchor ?? "geo", AnchorUid = dto.AnchorUid, OffLat = dto.OffLat, OffLon = dto.OffLon };
                if (!alertKind && dto.Rect != null && dto.Rect.Length >= 4) { g.Zone.Rect = dto.Rect; g.Ordered = true; }
                if (dto.Migrated == "dropped") { Warn(g, "alert_migrated"); Log("load migrate group=" + g.Name + " -> dropped"); }
                var remap = new Dictionary<int, int>();
                if (dto.Members != null)
                    foreach (var md in dto.Members)
                    {
                        if (md.LentTo != null) { shadows.Add(new KeyValuePair<Grp, CapMemberDto>(g, md)); continue; }   // resolved after every group is in
                        // T-186 (ADR-0082 s.6): a member of a hangar (format 3: "deck"; before: a positional uid) takes the free slot of its pair; the old k is not an identity
                        CapBird srow = null; int legacyShip;
                        bool legacy = md.Slot < 0 && md.LentTo == null && AirOpsMath.IsLegacyCapHangarUid(md.Uid, out legacyShip);
                        if (md.Slot < 0 && (md.Deck > 0 || legacy))
                        {
                            if (loadBirds == null) { loadBirds = Index(_g.Birds()); }
                            CapBird r0;
                            if (md.Deck > 0 && loadBirds.TryGetValue(md.Uid, out r0) && r0.HangarK > 0 && r0.Type == md.Type && SlotFree(r0)) srow = r0;
                            if (srow == null) { int ship; AirOpsMath.IsLegacyCapHangarUid(md.Uid, out ship); srow = FreeSlot(md.Deck > 0 ? md.Deck : ship, md.Type, loadBirds.Values); }
                            if (srow == null) srow = FreeSlot(0, md.Type, loadBirds.Values);
                            if (srow == null) { missed++; Log("restore hangar miss " + g.Name + " uid=" + md.Uid + " type=" + md.Type); continue; }
                            if (legacy) Log("restore hangar legacy " + g.Name + " uid=" + md.Uid + " -> " + srow.Uid + " deck=" + srow.DeckUid + " k=" + srow.HangarK);
                        }
                        int muid = srow != null ? srow.Uid : md.Uid;
                        if (_own.Claim(muid, Win, g.Name) != null) { if (srow != null) missed++; continue; }
                        remap[md.Uid] = muid;
                        var m = new Mem { Uid = muid, Name = md.Name, Type = md.Type, Slot = md.Slot, AamStart = md.AamStart, LongStart = md.LongStart, LentFrom = md.LentFrom, AutoFill = md.AutoFill, Alert = alertKind };
                        if (srow != null) { m.DeckUid = srow.DeckUid; m.HangarK = srow.HangarK; m.HangarN = srow.HangarN; m.ShipName = srow.ShipName; m.TypeLabel = srow.TypeLabel; m.JoinedAt = 0; }
                        if (md.Slot >= 0) { m.Airborne = true; m.OnStation = true; m.AirSince = now; }
                        else if (md.Slot == -2) { m.Slot = -1; m.Airborne = true; m.Leaving = "restored"; m.AirSince = now; }
                        else if (md.Slot == -3)
                        {
                            // a guided / barrage bird of an alert group. With the binding of the file (T-219, ADR-0090) the guidance goes on; an old file has none: the clear at once (ADR-0079 section 11)
                            m.Slot = -1; m.Airborne = true; m.AirSince = now;
                            if (md.Bind == null) { m.AlertState = "scramble"; m.Barrage = true; m.FarSince = now - ReturnFarS; }
                        }
                        foreach (var mem in d.Memory)
                            if (mem.Uid == md.Uid)
                            {
                                if (!mem.RadarOnly)
                                {
                                    m.PrevRoe = mem.Roe; m.RoeSet = true;
                                    if (mem.Dash) { m.Dash = true; m.PrevThrottle = mem.Throttle; m.DashStep = -1; }
                                }
                                if (mem.Radar != null && _radar != null) _radar.Import(muid, Win, mem.Radar);   // T-241
                            }
                        m.RestoredAt = now; m.FromSaveAt = now;   // T-211
                        // T-218: a deck member on a task row: its uid is new after the load; an old file (0.30.9) has no ship - any deck
                        m.RestoreDeck = md.TaskDeck > 0 ? md.TaskDeck : (md.Slot == -1 && md.Deck == 0 && srow == null && md.LentTo == null && AirOpsMath.IsTaskUid(md.Uid) ? 0 : -1);
                        m.RestoreWhere = md.Where; m.RowLoadout = md.Loadout; if (md.TaskDeck > 0) { if (m.DeckUid == 0) m.DeckUid = md.TaskDeck; if (md.Where != null && Deckish(md.Where)) m.Where = md.Where; }
                        if (alertKind && md.Bind != null) ApplyBind(g, m, md.Bind, now, d.MissionTimeS);   // T-219
                        g.Members.Add(m);
                    }
                if (alertKind && dto.Memos != null) foreach (var mm in dto.Memos) { var c = mm.Clone(); c.At = now - (d.MissionTimeS - mm.At); g.Memos.Add(c); }   // T-219
                if (alertKind) { if (dto.Served != null) foreach (int sid in dto.Served) g.Served.Add(sid); if (dto.ServedRaids != null) foreach (string sk in dto.ServedRaids) g.ServedRaids.Add(sk); }   // T-211: the raids this group served
                if (dto.Keys != null)
                    foreach (string k in dto.Keys)
                    {
                        string[] p = k.Split('|');
                        int uid, deck;
                        if (p.Length < 3 || !int.TryParse(p[0], out uid) || !int.TryParse(p[1], out deck)) continue;
                        int ruid; if (remap.TryGetValue(uid, out ruid)) uid = ruid;
                        var m = MemOf(g, uid); if (m == null) continue;
                        m.Key = string.Join("|", p, 2, p.Length - 2); m.DeckUid = deck; m.Allowed = false; _ourKeys.Add(m.Key);
                    }
                _groups.Add(g);
                if (g.AutoName) { int no = GroupNo(g.Name); if (no > _no) _no = no; }   // T-326: only automatic names move the counter
            }
            foreach (var sh in shadows)
            {
                var patrol = Find(sh.Value.LentTo);
                var lm = patrol == null ? null : MemOf(patrol, sh.Value.Uid);
                if (lm != null && lm.LentFrom == sh.Key.Name) sh.Key.Lent.Add(new LentRec { M = lm, To = patrol.Name });
            }
            _own.Slots.Rebind(now, null, true);   // T-186: the slots follow the machines left in the hangars right after the load
            Log("load " + _groups.Count + " groups" + (missed > 0 ? " missed=" + missed : ""));
            Evt("cap_restored", "{\"groups\":" + _groups.Count + ",\"missed\":" + missed + "}");
        }

        private SaveSlotStore _store;
        private string _storeDir;

        /// <summary>T-122: slot store in SaveDir/cap. The file lands in cap/cap/ as before (kept on purpose so saves of players stay readable).</summary>
        private SaveSlotStore Store
        {
            get
            {
                if (string.IsNullOrEmpty(_g.SaveDir)) return null;
                string dir = Path.Combine(_g.SaveDir, "cap");
                if (_store == null || _storeDir != dir) { _store = new SaveSlotStore(dir, Log); _storeDir = dir; }
                return _store;
            }
        }

        private string FilePath()
        {
            var st = Store;
            return st == null ? null : st.PathFor(_g.LastSavePath);
        }

        /// <summary>Atomic write of cap/&lt;sha1&gt;.json (+ .bak); the game layer calls it on the game save. false = nothing written.</summary>
        public bool PersistNow() { return PersistNow(_g.LastSavePath, null); }

        /// <summary>T-198 (ADR-0086): writes the slot of <paramref name="savPath"/> with the stamp of that game save (null = no stamp, an old-style file).</summary>
        public bool PersistNow(string savPath, SavStamp stamp)
        {
            var st = Store;
            if (st == null || string.IsNullOrEmpty(savPath)) return false;
            try { var data = Save(); data.Sav = stamp; return st.Write(savPath, CapSave.ToJson(data), jtxt => { string perr; return CapSave.Parse(jtxt, out perr) == null ? (perr ?? "bad file") : null; }); }
            catch (Exception e) { Log("persist failed: " + e.Message); return false; }
        }

        /// <summary>T-122 / R8: a new save mark with nothing to keep: drop the file of that slot. The call comes from the game layer (T-130).</summary>
        public bool ClearSlot(string savPath)
        {
            var st = Store;
            if (st == null || string.IsNullOrEmpty(savPath)) return false;
            bool had = st.Delete(savPath);
            if (had) Log("save slot cleared " + SaveSlotStore.Key(savPath));
            return had;
        }

        public bool RestorePending { get { return _pending != null; } }

        /// <summary>Answer to the "ask" prompt.</summary>
        public void ResolveRestore(bool accept)
        {
            if (_deferred != null) return;   // T-211: nothing is asked or loaded before the gate opens (ApplyDeferred)
            var d = _pending; _pending = null;
            if (d != null && accept) Load(d, _now);
        }

        /// <summary>T-211 (ADR-0088): OnMissionChanged keeps the checked file until ApplyDeferred (set by the game layer while the post-load gate is on).</summary>
        public bool DeferRestore { get; set; }

        /// <summary>T-211: a checked file waits for the world after a load.</summary>
        public bool RestoreWaiting { get { return _deferred != null; } }

        /// <summary>T-217 (ADR-0089): uids of the members of the waiting file that the post-load gate expects in the game lists: Uid &gt; 0, not a deck member
        /// (Deck != 0) and not a task row (AirOpsMath.IsTaskUid, T-218), no repeats. No file: empty. Reads only, changes nothing.</summary>
        public List<int> DeferredMemberUids()
        {
            var res = new List<int>();
            var d = _deferred;
            if (d == null) return res;
            foreach (var g in d.Groups)
                if (g.Members != null)
                    foreach (var m in g.Members)
                        if (m.Uid > 0 && m.Deck == 0 && !AirOpsMath.IsTaskUid(m.Uid) && !res.Contains(m.Uid)) res.Add(m.Uid);
            return res;
        }

        /// <summary>T-211: the post-load gate opened: the file is applied now (mode "ask": the prompt goes up now).</summary>
        public void ApplyDeferred(double now)
        {
            var d = _deferred; _deferred = null;
            if (d == null) return;
            double waited = Math.Max(0, now - _now);
            _now = now;
            if (_deferredMode == "ask") { _pending = d; Log("restore pending (ask)"); return; }
            Load(d, now);
            Log("restore applied after load wait_s=" + waited.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>RestoreOnLoad auto|ask|off. Groups of the previous mission are dropped.</summary>
        /// <summary>T-130: the mission was left: groups and the caches that hold its birds; no game calls.</summary>
        public void OnMissionLeft()
        {
            ClearAll();
            _lastBirds = null; _birdsAt = -1e9; _losCache.Clear(); _strikeLogged.Clear(); _viewLoad.Clear();
        }

        public void OnMissionChanged(string savPathOrNull, double missionTimeS) { OnMissionChanged(savPathOrNull, missionTimeS, null); }

        /// <summary>T-198 (ADR-0086): <paramref name="loaded"/> = header stamp of the loaded .sav, null = unknown (the old rule: file time within 5 s of the running mission).</summary>
        public void OnMissionChanged(string savPathOrNull, double missionTimeS, SavStamp loaded)
        {
            ClearAll();
            _now = missionTimeS;
            if (savPathOrNull == null) return;
            string mode = _p.RestoreOnLoad ?? "auto";
            if (mode == "off") return;
            var st = Store;
            if (st == null) return;
            CapSaveData d = null;
            string src;
            string text = st.Read(savPathOrNull, t =>
            {
                string e2; CapSaveData x;
                try { x = CapSave.Parse(t, out e2); } catch (Exception ex) { return ex.Message; }
                if (e2 != null) return e2;
                d = x; return null;
            }, out src);
            if (text == null || d.Groups.Count == 0) return;
            string why = SaveFreshness.Check(d.Sav, d.MissionTimeS, loaded, missionTimeS);
            if (why != null) { Log("restore skipped: stale file (" + why + ")"); return; }
            if (DeferRestore) { _deferred = d; _deferredMode = mode; Log("restore deferred until the world is placed groups=" + d.Groups.Count); return; }   // T-211
            if (mode == "ask") { _pending = d; Log("restore pending (ask)"); return; }
            Load(d, missionTimeS);
        }
    }
}
