using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VoiceBotBridge
{
    // T-095 / ADR-0036: stateful ASW controller. Works through IAswGame, so it is testable without the game (aswtest).

    internal sealed class AswConfig
    {
        /// <summary>Candidates joined by | ; the first one no game binding uses is taken (HotkeyBinding).</summary>
        public string Hotkey = "LeftAlt+U|LeftAlt+J|F7", RestoreOnLoad = "auto", Loadouts = "ASWHunter,ASW,ASWKiller";
        public bool Debug;
        public bool AutoRadar = true;   // T-241 (ADR-0099): [Asw] AutoRadar, live
        public AswStrikeParams Strike = new AswStrikeParams();   // T-173 (ADR-0074): the strike group, read every tick (Live)
        public AswLocParams Loc = new AswLocParams();            // T-173 (ADR-0072): the attack run and the localization
    }

    /// <summary>A sonobuoy that exists in the water, seen by the game (not issued by an order of the controller).</summary>
    internal sealed class AswBuoyObs
    {
        public int Id, OwnerUid;
        public double Lat, Lon, LifeS = 28800, DroppedS = -1;
        public double RemainingS = -1;   // T-172 R6: seconds of life the game reports for the buoy (Bomb.RemainingLife); -1 = unknown
        public string Ammo;
    }

    internal sealed class AswTarget
    {
        public int Uid;
        public string Name;
        public double Lat, Lon, SpeedKn, CourseDeg, WidthNm;
        public double? NextWpBrg;
        public double? RouteBrg;          // T-238: bearing to the next point of the player's route (>= RouteNearNm away); null = no route
        public bool ModSteered;           // T-238: the ship's current route is the mod's (torpedo evasion / RBU turn, ControlMemory)
        public double? SavedCourseDeg;    // T-238: the demanded course before the mod took the ship (ControlEntry.SavedCourseDeg)
        public string ImmobileWhy;        // T-266: null = the ship can move (ShipMobility)
        public bool WayPending; public int? Telegraph;   // T-274: telegraph != 0 but the way is below ShipMobility.StandKn
        public List<double[]> Track = new List<double[]>();   // {lat, lon}, last ~3 min, oldest first
    }

    internal sealed class AswContact
    {
        public int Id;
        public string Name, Source, Side;
        public double Lat, Lon;
        public bool Hostile = true;     // triggers the ASW reaction: hostile or still unidentified (ADR-0036 s.7: side is not friendly)
        public bool AttackOk = true;    // classified hostile: only then an attack is proposed
        public bool Live = true;        // T-172: false = a stale track (the game does not hold it any more); kept for the estimate only, never a new contact
        public bool Killed;             // T-172: the submarine was destroyed (asw_contact_killed, no lost-contact square)
        public string Decoy;            // 0.27.10: non-null = the track reads as a noisemaker / decoy (the name that says so); never a contact
        // T-173 (ADR-0074): what the player knows about the track, for the strike group (null / NaN / -1 = not readable)
        public string ClassName;                // display class of the track
        public bool? Classified;                // the game classified the track (Vehicle.IsClassified)
        public string Biologic;                 // non-null = the track reads as a whale / school (the name that says so): never a target, never a contact
        public string MapPosture, VmPosture;    // the posture of the player's map (Neutral / Friendly set by the player excludes an unidentified track)
        public double CourseDeg = double.NaN, SpeedKn = double.NaN, DepthFt = double.NaN;
        public bool Surfaced;                   // the track reads as surfaced
        public const double MadNotRead = -2;   // T-316 / ADR-0123: the game gives no MAD detection reading (ADR-0072 s.5.2: the pass is skipped)
        public double MadFixS = -1, FixS = -1, ErrNm = -1;   // mission time of the last MAD detection / of the last live fix of this track, error ellipse; -1 = unknown; MadFixS: -1 = readable, no mark; -2 (MadNotRead) = not readable
    }

    internal interface IAswGame
    {
        double Now { get; }                 // mission time, s
        double TimeScale { get; }
        bool AllowAttack { get; }
        string LastSavePath { get; }        // <persistentDataPath>/last_save marker, null if none
        string SaveDir { get; }
        IList<AswMember> Members();
        AswTarget Target(int uid);
        AswTarget TargetByName(string name);
        AswTarget SelectedShip();
        IList<AswTargetOption> CourseTargets();   // T-142 K: our surface ships and formations (leader) the "by course" order can follow
        IList<AswContact> SubContacts();    // subsurface tracks the player knows (hostile and unidentified); the strike group filters them (AswStrikeMath.Eligible)
        IList<AswBuoyObs> OwnBuoys();       // own sonobuoys in the water (to take the ones dropped before the order into the group)
        List<BuoyType> BuoyTypes(int uid);
        double DipRangeNm(int uid);
        string Drop(int uid, string ammo, IList<GeoPt> pts);          // null = ok, else error code
        string Dip(int uid, IList<GeoPt> pts);
        string Goto(int uid, IList<GeoPt> pts, int altFt);
        string ClearRoute(int uid);                                   // drop the queued waypoints/tasks of an old order (T-142); null = ok
        string SetDepth(int uid, string mode, out string previous);   // mode: above|below|alternate; previous in the same terms
        string HoldRoe(int uid, out string previous);
        string RestoreRoe(int uid, string roe);
        /// <summary>T-119: the first loadout of the CSV that the game's launch rule would take (name, display name, substring; year of service; CanLaunchVehicle).
        /// Null + err: "sea_state" (helicopter), "no_ammo", "unknown_loadout", "no_deck". The launch itself goes through the deck broker (IAirDeck).</summary>
        string ResolveLoadout(int deckUid, string type, string loadoutsCsv, out string err);
        bool TasksChangedByOther(int uid);
        string AttackWeapon(int uid);                                 // null = this aircraft cannot attack
        string Attack(int uid, int contactId);                        // existing attack path (NeutralGuard); null = ok
        void Event(string name, string dataJson);
        void Log(string line);
        // T-109 (SAR groups): deck orders with a mission type, loadouts with seats, raft and threat snapshots, return to base / hover
        List<LoadoutOption> SarLoadouts(int deckUid, string vehicleType);
        string DeckTimingsMode();
        /// <summary>live 0.28.3 (diagnostics): "inoperable=&lt;bool&gt; deck_tasks=&lt;every task the deck lists&gt;" for the journal when a followed row vanishes.</summary>
        string DeckState(int deckUid);
        IList<SarRaft> SarRafts();
        IList<SarThreat> SarThreats();
        string SarRtb(int uid);
        string SarHover(int uid);
        string SarHoverOver(int uid, int shipUid);   // T-147 H: wait over the ship (its position now); the ship moves, the controller repeats the call
        // T-150: route guard and queue
        double HeightM(GeoPt p);            // terrain height, m (> 0 = land); no data = -200 (open sea)
        int QueuedTasks(int uid);           // tasks still queued for the aircraft; -1 = unknown
        IList<AswHazard> Hazards();         // known hostile surface / ground objects with the reach of their air defence
        // T-173 (ADR-0074)
        IList<AswOwnSub> OwnSubs();            // own subs (true place) + allied subs only as friendly tracks of the player's map; null = not readable (-> hold fire for every target)
        IList<AswShipPos> OwnShips();          // own surface ships
        IList<AswTorpObs> TorpedoesInWater();  // the player's torpedoes in the water; null = not readable
        int HitsOn(int trackId);               // HitLedger (ADR-0064) hits on the track, -1 = off / unknown
        AswDropSpec DropSpec(int uid);         // the ASW torpedo the aircraft would drop; null = unknown
        /// <summary>Attack the track; unknownOk = an unidentified (not map-red) classified sub may be attacked through the mod's own engage path (NeutralGuard stays). null = ok, else out_of_range | neutral_near | not_hostile | ...</summary>
        string AttackTrack(int uid, int contactId, bool unknownOk);
    }

    /// <summary>One warning line of the window; Group is null for global ones.</summary>
    internal sealed class AswWarn { public string Group, Text, Code; }

    internal sealed partial class AswController
    {
        private static readonly string[] DefaultCourse = { "dip", "rows", "mad" };

        private sealed class Job
        {
            public int Uid, Count, BuoysBefore, Ticks;
            public string Tactic, Ammo;              // T-172 B4: a drop package counts what fell by the buoys of its own type (Ammo, Kind), not by the sum of all kinds
            public BuoyType Kind;
            public double DoneAt, FirstAt;           // ADR-0063: FirstAt = expected arrival over the first drop point (+60 s); tc_blocked only after it
            public List<BuoyRec> Recs;                // ADR-0063: the group's buoy records of this package in flight order (the undropped ones go when the route is cancelled)
            public bool IsDrop, NoQueue;   // NoQueue: no tasks were issued (everything was skipped), do not wait for the game queue
            public Action Undo;
            public int BarrierSeq = -1;              // T-229: the row of the sliding barrier the package lays (the cut-off points go back to it)
        }

        private sealed class BuoyRec { public double Lat, Lon, DroppedS, LifeS; public string Ammo; public bool Pre; }

        private sealed class GoneRec { public AswRow Row; public string Reason; public double Until; }

        private sealed class ContactRec
        {
            public int Id, Holder;
            public string Name;
            public double Lat, Lon, FirstS, LastSeenS, NextDipS;
            // T-176 C11: the track's course / speed (NaN = unknown), the side of the next dip, the cross waiting for the attack, the lost-contact ring
            public double CourseDeg = double.NaN, SpeedKn = double.NaN;
            public int Side = 1;
            public bool CrossPending, Ring;
            public double RingFromS, RingNextS, RingR, RingLat, RingLon;
            public List<GeoPt> RingPts = new List<GeoPt>(); public HashSet<int> RingDipped = new HashSet<int>();
        }

        private sealed class Grp
        {
            public AswPlanState Plan = new AswPlanState();                                // T-176: all of the v3 pattern state
            public StrikeState St = new StrikeState();                                   // T-173: the strike group's order (order "strike")
            public string Name, Order = "none", TargetName, Depth = "alternate", RSource = "";
            public int TargetUid;
            public string TargetKind = "ship";                                            // T-142 K: ship | formation
            public double FormWidthNm, RawAxis, LastReplanS = -1, ReplanInS = -1;            // formation width; the held (stable) axis; time of the last (re)plan; wait for the interval
            public Dictionary<string, string> Loadouts = new Dictionary<string, string>();   // T-142 K: vehicle type -> chosen loadout (absent = auto)
            public List<string> Tactics = new List<string>();
            public double[] Rect;
            public AswParams P;
            public List<int> Members = new List<int>();
            public Dictionary<int, AswMemberRef> Refs = new Dictionary<int, AswMemberRef>();
            public Dictionary<int, string> Assign = new Dictionary<int, string>();
            public bool Dirty = true;
            public HashSet<int> Prepared = new HashSet<int>();
            public Dictionary<int, Job> Jobs = new Dictionary<int, Job>();
            public Dictionary<int, Job> LastDrop = new Dictionary<int, Job>();      // ADR-0063: the last drop package per aircraft (kept after the job ends, until its route is replaced)
            public List<BuoyRec> Buoys = new List<BuoyRec>();
            public List<ContactRec> Contacts = new List<ContactRec>();
            public List<AswLineView> Lines = new List<AswLineView>();
            public double Axis, V, PlanAxis, PlanV, RNm = 0, NextCrossAt, NextRingAt, ReliefIn = -1, Covered = 1;
            public bool FrameSet, CursorSet, AreaBuilt, AreaDone;
            public GeoPt Cursor;
            public List<GeoPt> AreaAll, AreaDip, AreaMad;
            public int AreaIdx, AreaDipIdx, AreaMadIdx;
            public string Status = "";
            public bool PreScanned;
            public string LastAxisSrc = ""; public double LastAxisLogS = -1e9, LastAxisLogged = -1000;
            public double BandAxis, BandAxisLogged = -1000; public bool BandAxisSet; public string BandAxisSrc = "";   // T-238: the barrier axis by the player's route (ADR-0098)
            public double? HoldAxis; public double HoldSinceS;          // T-251: the axis of the save holds the barrier after a load until the route confirms it
            public bool WasSteered; public double SteerEndS = -1;       // T-251: the end of the mod's steering, the frame waits for the ship to come back
            public double LastSteerS = -1;   // T-264: the last tick the mod steered the ship; the track holds that leg for AswMath.TrackSpanS
            public bool Frozen;   // T-266: the barrier of an immobile ship holds frame and band
            public double StandSinceS = -1; public string FreezeWaitLogged;   // T-274: start of the stand (band kept while the way is pending); last logged reason of "no freeze"
            public double? SeedAxis;   // T-238: the anchor axis from the save, used once by the first anchor after a load
            public HashSet<int> PreSeen = new HashSet<int>();
            public Dictionary<int, string> LastTactic = new Dictionary<int, string>();
            // order "sar" (T-109)
            public SarArea SarArea = new SarArea();
            public string SarOnComplete = "rtb", SarStatus = "";
            public bool AutoName = true;                                                   // T-150: the name is "<prefix>-<n>" made by the mod (changes with the order); a typed name is kept
            public Dictionary<string, OwnRec> OwnInfo = new Dictionary<string, OwnRec>();   // T-150: what an ordered deck task is for (tactic, type, deck)
            public Dictionary<int, double> StandbyAt = new Dictionary<int, double>();       // T-150: airborne reserve -> time of its last waiting-point order
            public Dictionary<string, double> OwnKeys = new Dictionary<string, double>();   // deck task keys the mod ordered -> time of the order
            public HashSet<string> OwnSeen = new HashSet<string>();
            public Dictionary<string, double> NoAmmo = new Dictionary<string, double>();    // "deck|type|loadout" (SarMath.NoAmmoKey) -> time refused
            public Dictionary<int, string> Leaving = new Dictionary<int, string>();         // uid -> why it was sent home
            public HashSet<int> Released = new HashSet<int>();                               // T-172 B9: aircraft released from a tactic (fuel / buoys): not given a tactic again until they have landed
            public HashSet<int> Dumped = new HashSet<int>();                                 // T-172 B5: aircraft that already dropped their rest before leaving (cleared when they are not airborne)
            public HashSet<int> Hovering = new HashSet<int>();
            public Dictionary<int, double> HoverCheckAt = new Dictionary<int, double>();
            public Dictionary<int, int> LastOnboard = new Dictionary<int, int>();
            public Dictionary<int, double[]> Targets = new Dictionary<int, double[]>();     // uid -> {raft id, distance nm}
            public List<SarReturn> Returns = new List<SarReturn>();
            public int Rescued, UncoveredRafts, UncoveredPeople, Active, AnchorDeck, AreaRafts, AreaPeople;
            public string AnchorType, AnchorLoadout;
            public double NextLaunchS = -1;
            // T-343: own loadouts per (deck, type) - SarMath.LosKey
            public Dictionary<string, List<LoadoutOption>> LosBy = new Dictionary<string, List<LoadoutOption>>();
            public Dictionary<string, double> LosAtBy = new Dictionary<string, double>();
            // T-181 (ADR-0080): hangar slots
            public Dictionary<int, SlotRef> Slots = new Dictionary<int, SlotRef>();   // member uid -> the slot it holds (hangar members only)
            public Dictionary<int, long> Joined = new Dictionary<int, long>();        // member uid -> join order (RebindSlots tie-break)
            public HashSet<int> Allowed = new HashSet<int>();                         // deck members asked to take off (PlanRelief Want)
        }

        // Uid = TaskUid(key) of the handed-over member (0 = an order without a member, SAR "order"); SourceUid/SourceK = the slot it came from; HangarSeen = machines left in the hangar after the order
        private sealed class OwnRec { public string Task, Type; public int DeckUid; public int Uid, SourceUid, SourceK, HangarSeen = -1; public bool Allow; }

        private sealed class SarReturn { public string Type, Base, Name; public double Expires; }

        private readonly IAswGame _game;
        private readonly AswParams _defaults;
        private readonly AswConfig _cfg;
        private readonly List<Grp> _groups = new List<Grp>();
        private readonly Dictionary<int, AswMemDto> _mem = new Dictionary<int, AswMemDto>();
        private readonly List<Adopt> _adopts = new List<Adopt>();
        private readonly Dictionary<string, double> _warnAt = new Dictionary<string, double>();
        private readonly List<AswWarn> _warnings = new List<AswWarn>();
        private Dictionary<int, AswMember> _live = new Dictionary<int, AswMember>();
        private HashSet<int> _airBefore = new HashSet<int>();   // air units of the snapshot before the last RefreshLive
        private readonly Dictionary<string, double> _prepSeen = new Dictionary<string, double>();   // T-213: "deck|type" -> last known preparation of a hangar order
        private readonly Dictionary<string, int> _hangarBefore = new Dictionary<string, int>();   // "deck|type" -> hangar machines of the snapshot before the last RefreshLive
        private readonly Dictionary<int, AswMember> _prevLive = new Dictionary<int, AswMember>();
        private readonly Dictionary<int, AswMember> _lastKnown = new Dictionary<int, AswMember>();
        private readonly Dictionary<int, GoneRec> _gone = new Dictionary<int, GoneRec>();
        private const double GoneShowS = 120;
        private double _now;
        private int _groupNo;
        private string _lastSave;
        private bool _lastSaveSeen;
        private AswSaveData _pendingRestore;
        private AswSaveData _deferred;   // T-211 (ADR-0088): the checked file waits for the post-load gate (ApplyDeferred)
        private AswView _view = new AswView();
        private readonly List<AswLineView> _preview = new List<AswLineView>();
        private double _previewUntil;

        // ---- T-119 (ADR-0042): launches go through the deck broker -------------------------------------------------

        /// <summary>The deck broker (set by the air tick module). Null = no deck requests are submitted (a line in the log once a minute).</summary>
        public IAirDeck Deck { get { return _relief.Deck; } set { _relief.Deck = value; } }

        /// <summary>T-121: the gateway of the relief requests (in-flight guard, pauses, "broker is off" line), shared with the CAP controller.</summary>
        private readonly AirRelief _relief;

        private int Seq(Grp g)
        {
            if (Ownership != null) return Ownership.GroupSeq("asw", g.Name);
            int i = _groups.IndexOf(g);
            return i < 0 ? _groups.Count : i;
        }

        private AirGroupPrio PrioOf(Grp g)
        {
            return new AirGroupPrio { Name = g.Name, Order = g.Order == "sar" ? "sar" : "asw", Window = "asw", HasContact = g.Contacts.Count > 0, WindowIndex = Seq(g) };
        }

        /// <summary>Facts of every working group for the broker (ADR-0042): the decks the members are still waiting on.</summary>
        private void DeclareDecks()
        {
            if (Deck == null) return;
            foreach (var g in _groups)
            {
                if (g.Order == "none") continue;
                var decks = new HashSet<int>();
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (_live.TryGetValue(u, out m) && m.Where != "air" && m.DeckUid != 0) decks.Add(m.DeckUid);
                }
                Deck.Declare("asw", PrioOf(g), decks);
            }
        }

        /// <summary>Queues a deck request; <paramref name="body"/> runs when the broker answers (same frame). False = not submitted (no broker / already in flight).</summary>
        private bool SubmitDeck(Grp g, string flightKey, DeckRequest r, Action<DeckResult> body)
        {
            return _relief.Submit(flightKey, g.Name, () => PrioOf(g), r, _now, body);
        }

        public AswController(IAswGame game, AswParams p, AswConfig cfg)
        {
            _game = game; _relief = new AirRelief("asw", ln => _game.Log(ln), "asw "); _sarAir = new SarAirOrder(this); _defaults = p ?? new AswParams(); _cfg = cfg ?? new AswConfig();
            _defaults.Loadouts = _cfg.Loadouts;
            AswMath.GuardLog = game == null ? null : (Action<string>)game.Log;   // T-154 B3: one journal line when a pattern is refused
        }

        // ---- helpers -----------------------------------------------------------------------------------------------

        private static string N(double d) { return Json.Num(d); }
        private static string I(int i) { return i.ToString(CultureInfo.InvariantCulture); }

        private sealed class Raw { public string S; public Raw(string s) { S = s; } }

        private static string Obj(params object[] kv)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Json.Str((string)kv[i])).Append(':');
                object v = kv[i + 1];
                if (v == null) sb.Append("null");
                else if (v is string) sb.Append(Json.Str((string)v));
                else if (v is bool) sb.Append(Json.Bool((bool)v));
                else if (v is int) sb.Append(I((int)v));
                else if (v is double) sb.Append(N((double)v));
                else if (v is Raw) sb.Append(((Raw)v).S);
                else sb.Append(v);
            }
            return sb.Append('}').ToString();
        }

        private static AswParams Clone(AswParams s)
        {
            return (AswParams)typeof(AswParams).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null);
        }

        private static GeoPt Pos(AswTarget t) { return new GeoPt(t.Lat, t.Lon); }
        private static bool Excluded(AswMember m) { return m.Where == "player" || m.Where == "sar" || m.Where == "game_patrol" || m.Evading; }
        private static int BuoyCount(AswMember m) { return m.Passive + m.ActiveBuoys + m.Vlad; }

        private Grp Find(string name)
        {
            foreach (var g in _groups) if (g.Name == name) return g;
            return null;
        }

        private Grp GroupOf(int uid)
        {
            foreach (var g in _groups) if (g.Members.Contains(uid)) return g;
            return null;
        }

        /// <summary>The number after the last '-' of a group name (any prefix: "ПЛО-3", "ASW-5"); 0 when there is none.</summary>
        internal static int GroupNo(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int i = name.LastIndexOf('-');
            int n;
            return i >= 0 && i < name.Length - 1 && int.TryParse(name.Substring(i + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        private Grp NewGroup(string name)
        {
            var g = new Grp { Name = name ?? AswGroupNames.Auto(UiText.Get("asw.group.prefix"), ++_groupNo), P = Clone(_defaults) };
            g.AutoName = name == null || AswGroupNames.IsAuto(name, KnownPrefixes());
            if (name != null) { int gn = GroupNo(name); if (g.AutoName && gn > _groupNo) _groupNo = gn; }
            _groups.Add(g);
            Seq(g);   // the shared creation number (ADR-0042) is taken when the group is created
            return g;
        }

        private static string[] KnownPrefixes()
        {
            return new[] { UiText.Get("asw.group.prefix"), UiText.Get("sar.group.prefix"), UiText.Get("asw.group.strike_prefix"), "ASW", "SAR", "STRIKE" };
        }

        /// <summary>The name follows the order while it is automatic (ПЛО-3 <-> ПСО-3, same number); a typed name never changes.</summary>
        private void AutoRename(Grp g)
        {
            if (!g.AutoName || g.Order == "none") return;
            int i = g.Name.LastIndexOf('-');
            string pre = i > 0 ? g.Name.Substring(0, i) : "";
            string kind = string.Equals(pre, UiText.Get("sar.group.prefix"), StringComparison.OrdinalIgnoreCase) || pre == "SAR" ? "sar"
                        : string.Equals(pre, UiText.Get("asw.group.strike_prefix"), StringComparison.OrdinalIgnoreCase) || pre == "STRIKE" ? "strike" : "asw";
            if (kind == (g.Order == "sar" ? "sar" : g.Order == "strike" ? "strike" : "asw")) return;   // the prefix already fits the task
            string n = AswGroupNames.ForOrder(g.Name, true, g.Order, UiText.Get("asw.group.prefix"), UiText.Get("sar.group.prefix"), UiText.Get("asw.group.strike_prefix"));
            if (n != g.Name && Find(n) == null) Rename(g, n);
        }

        private void Rename(Grp g, string n)
        {
            string old = g.Name;
            if (Ownership != null) Ownership.RenameSeq("asw", old, n);
            string pre = old + "|";
            foreach (string k in new List<string>(_warnAt.Keys))
                if (k.StartsWith(pre, StringComparison.Ordinal)) { double v = _warnAt[k]; _warnAt.Remove(k); _warnAt[n + "|" + k.Substring(pre.Length)] = v; }
            foreach (var w in _warnings) if (w.Group == old) w.Group = n;
            foreach (var r in _targets.Values) if (r.Group == old) r.Group = n;
            foreach (var al in _alerts.Values) if (al.Group == old) al.Group = n;
            g.Name = n;
            _game.Log("asw group renamed " + old + " -> " + n);
            _game.Event("asw_group_renamed", Obj("old", old, "new", n));
            RebuildView();
        }

        private static readonly HashSet<string> StickyWarn = new HashSet<string> { "deck_down" };   // T-238: never pushed out by the 6-line cap

        private void Warn(Grp g, string code, string detail = null, double every = 120)
        {
            if (g != null && (code == "buoys_short" || code == "corridor_too_wide" || code == "life_short" || code == "barrier_rows_short")) g.Plan.Warn = code;   // T-176: the plan summary shows it
            string key = (g == null ? "" : g.Name) + "|" + code;
            double last;
            if (_warnAt.TryGetValue(key, out last) && _now - last < every) return;
            _warnAt[key] = _now;
            _game.Event("asw_warn", Obj("code", code, "group", g == null ? null : g.Name, "detail", detail));
            string text = code + (g == null ? "" : " (" + g.Name + ")") + (detail == null ? "" : ": " + detail);
            _game.Log("asw warn " + text);
            string gn = g == null ? null : g.Name;
            _warnings.RemoveAll(w => w.Code == code && w.Group == gn);   // one line per kind and group: the latest text wins
            _warnings.Add(new AswWarn { Group = gn, Text = text, Code = code });
            if (_warnings.Count > 6)
            {
                int drop = 0;   // T-238: a sticky warning (deck_down) is never pushed out by the cap while another line can go
                while (drop < _warnings.Count - 1 && StickyWarn.Contains(_warnings[drop].Code)) drop++;
                _warnings.RemoveAt(drop);
            }
        }

        /// <summary>Forgets the warnings (and their rate-limit keys) of a group that is gone.</summary>
        private void ClearWarnings(Grp g)
        {
            _warnings.RemoveAll(w => w.Group == g.Name);
            string prefix = g.Name + "|";
            foreach (string k in new List<string>(_warnAt.Keys)) if (k.StartsWith(prefix, StringComparison.Ordinal)) _warnAt.Remove(k);
        }

        /// <summary>T-112 (ADR-0040 s.11): shared ownership of one machine; null = as before (no owner object or no foreign owner).</summary>
        public AirOwnership Ownership
        {
            get { return _ownership; }
            set
            {
                if (_book != null) _book.Unregister("asw");
                _ownership = value;
                _book = value != null ? value.Slots : _localBook;   // T-186: without a shared object the window keeps its own book (same code path)
                _book.Register("asw", SlotClaims, ApplySlot, SlotNumbers);
            }
        }

        private AirOwnership _ownership;
        private readonly HangarSlotBook _localBook = new HangarSlotBook();
        private HangarSlotBook _book;

        /// <summary>T-186 (ADR-0082): the hangar slot book shared with the CAP window (own book when there is no shared ownership).</summary>
        private HangarSlotBook Book
        {
            get
            {
                if (_book == null) { _book = _localBook; _book.Register("asw", SlotClaims, ApplySlot, SlotNumbers); }
                return _book;
            }
        }

        private string ForeignOwner(int uid)
        {
            string win;
            return ForeignOwner(uid, out win);
        }

        private string ForeignOwner(int uid, out string window)
        {
            window = null;
            if (Ownership == null) return null;
            string win;
            string grp = Ownership.Owner(uid, out win);
            if (grp == null || win == "asw" || win == "sar") return null;
            window = win;
            return grp;
        }

        private static bool Capable(AswMember m) { return AswMath.ExclusionReason(m) == null; }

        // ---- tick --------------------------------------------------------------------------------------------------

        private void RefreshLive()
        {
            var d = new Dictionary<int, AswMember>();
            foreach (var m in _game.Members())
            {
                d[m.Uid] = m;
                if (_noDipUids.Contains(m.Uid) || (_noDipTypes.Count > 0 && _noDipTypes.Contains(DipTypeKey(m)))) { m.Dipping = false; m.DipSrc = "refused"; }   // T-188
            }
            var ab = new HashSet<int>();
            foreach (var m in _live.Values) if (m.Where == "air") ab.Add(m.Uid);
            _airBefore = ab;
            _hangarBefore.Clear();
            foreach (var m in _live.Values) if (KeyIs(m, "hangar:")) _hangarBefore[m.DeckUid + "|" + m.Type] = Math.Max(1, m.DeckCount);
            // a free aircraft that disappeared stays visible for a while under "excluded": landed (back in a hangar) or lost
            foreach (var kv in _prevLive)
            {
                if (d.ContainsKey(kv.Key) || GroupOf(kv.Key) != null || _gone.ContainsKey(kv.Key) || kv.Value.Where != "air") continue;
                bool backOnDeck = false;
                foreach (var m in d.Values) if (m.Type == kv.Value.Type && m.Where.StartsWith("deck", StringComparison.Ordinal)) backOnDeck = true;
                string reason = backOnDeck ? "landed" : "lost";
                _gone[kv.Key] = new GoneRec { Row = ToRow(kv.Value, null), Reason = reason, Until = _now + GoneShowS };
                _game.Log("asw aircraft gone uid=" + I(kv.Key) + " " + kv.Value.Name + " reason=" + reason);
            }
            foreach (int k in new List<int>(_gone.Keys)) if (_now > _gone[k].Until || d.ContainsKey(k)) _gone.Remove(k);
            _prevLive.Clear();
            foreach (var kv in d) { _prevLive[kv.Key] = kv.Value; _lastKnown[kv.Key] = kv.Value; }
            _live = d;
            foreach (var m in d.Values)   // T-213: the last known preparation of a hangar order by deck and type (the hangar has no row while its only machine flies)
            {
                double pr = AswMath.IsHangarRow(m.DeckKey) ? m.HangarPrepS : m.DeckKey != null && m.DeckKey.StartsWith("cool:", StringComparison.Ordinal) ? m.NextPrepS : -1;
                if (pr >= 0) { _prepSeen[m.DeckUid + "|" + m.Type] = pr; _prepSeen["*|" + m.Type] = pr; }
            }
        }

        private static AswRow ToRow(AswMember m, Grp g)
        {
            string tac = null;
            if (g != null) { if (!g.Assign.TryGetValue(m.Uid, out tac)) tac = null; }
            var row = new AswRow
            {
                Uid = m.Uid, Name = m.Name, Type = m.TypeLabel ?? m.Type, Base = m.Base, State = m.Where, Loadout = m.Loadout, Group = g == null ? null : g.Name, Order = g == null ? null : g.Order, Tactic = tac,
                ReadyInS = m.ReadyInS, FuelFrac = m.FuelFrac, MinutesOnStation = Math.Max(0, (m.FuelFrac * m.EnduranceS - m.TransitS) * 0.9 / 60.0),
                Passive = m.Passive, ActiveBuoys = m.ActiveBuoys, Vlad = m.Vlad, Torpedoes = m.Torpedoes, Dipping = m.Dipping, DipSrc = m.DipSrc, DipNote = m.DipNote, Mad = m.Mad, LoadKnown = m.LoadKnown,
                Roles = m.Roles == null ? null : string.Join(",", m.Roles),
                Onboard = m.Onboard, Capacity = m.Capacity,
                HasPos = m.Where == "air" && !(m.Lat == 0 && m.Lon == 0), Lat = m.Lat, Lon = m.Lon, DeckUid = m.DeckUid, ShipUid = m.ShipUid,
                DeckKey = m.DeckKey, Slot = m.Slot, HangarN = IsSlot(m) ? m.DeckCount : 0, HangarPrepS = m.HangarPrepS   // T-181; T-192
            };
            if ((m.Where == "deck_prep" || m.Where == "deck_ready") && KeyIs(m, "task:")) row.TakeoffInS = Math.Max(0, m.ReadyInS) + m.LaunchS;
            double[] tg;
            if (g != null && g.Targets.TryGetValue(m.Uid, out tg)) { row.TargetRaft = (int)tg[0]; row.TargetDistNm = tg[1]; }
            return row;
        }

        // T-126: lazy RefreshLive. The window sets ViewWanted every frame (true by default: a host without the window, tests, always refreshes).
        // An idle controller (no groups, adopts, proposals, gone rows, SAR work) with a closed window only watches the save marker.
        private bool _liveStale;

        /// <summary>The window is open (or wants data). When false and the controller has no work, Tick does not ask the game.</summary>
        public bool ViewWanted
        {
            get { return _viewWanted; }
            set
            {
                bool opening = value && !_viewWanted;
                _viewWanted = value;
                if (opening && _liveStale) { EnsureLive(); RebuildView(); }   // the window opens: fresh rows and a rebuilt view in the same frame
            }
        }
        private bool _viewWanted = true;

        /// <summary>Ticks that skipped the game scan (diag / test).</summary>
        internal int SkippedTicks { get; private set; }

        /// <summary>Before any command: the rows of the last skipped ticks are refreshed now. No-op when the data is fresh.</summary>
        public void EnsureLive()
        {
            if (!_liveStale) return;
            _liveStale = false;
            _prevLive.Clear();   // rows that vanished while idle are not "gone" aircraft of a group
            RefreshLive();
        }

        /// <summary>T-298: a reader of the state (dev "state" asw/sar, link_state) gets fresh rows even with the window closed.
        /// Stale (an idle tick skipped the scan) = EnsureLive + RebuildView; fresh = no-op. At most one game scan per tick (the open-window rate).</summary>
        public bool FreshView()
        {
            if (!_liveStale) return false;
            EnsureLive();
            RebuildView();
            return true;
        }

        private bool Idle()
        {
            if (ViewWanted || _groups.Count > 0 || _adopts.Count > 0 || _targets.Count > 0 || _gone.Count > 0 || _previewUntil > 0) return false;
            return !_sarAir.HasWork();
        }

        public void Tick(double gameTimeS)
        {
            // the 1 s step belongs to the air tick (AirTickClock); every call here is one step
            _now = gameTimeS;
            if (Idle()) { _liveStale = true; SkippedTicks++; CheckSave(); IdleScan(); return; }
            if (_liveStale) { _liveStale = false; _prevLive.Clear(); }   // coming out of idle: nothing vanished "from a group"
            RefreshLive();
            RetryRestoreWait();   // T-211
            _tickNo++;
            Book.Rebind(_now);   // T-181/T-186: slots follow the machines left in the hangars (once per tick for both windows)
            ScanSubs();
            DeclareDecks();
            foreach (var g in Ordered())
            {
                // an empty group stays (Status "empty") until the player disbands it (T-142)
                if (g.Members.Count == 0 && g.Order == "none") continue;
                try { TickGroup(g); }
                catch (Exception e) { _game.Log("asw tick error " + g.Name + ": " + e.Message); }
            }
            SweepDecks();   // T-238: the deck_down warning lives until "deck up"
            if (_previewUntil > 0 && _now >= _previewUntil) ClearPreview();
            TickAttacks();
            UpdateAlerts();
            CheckSave();
            RebuildView();
        }

        private void TickGroup(Grp g)
        {
            ProcessAdopts(g);
            AdoptOwnTasks(g);
            // members that vanished or were taken over
            string ownKey;
            foreach (int u in new List<int>(g.Members))
            {
                AswMember m;
                if (!_live.TryGetValue(u, out m))
                {
                    if (HasAdopt(u)) continue;   // waiting to be adopted: keep the member (T-142)
                    if (SlotPending(u)) continue;   // T-181: a slot whose machine was taken: dropped only after AbortTicks scans (hangar_taken)
                    if (PendingTask(g, u, out ownKey))   // T-181: a handed-over order whose task row has not been listed yet
                    {
                        if (_now - g.OwnKeys[ownKey] <= OwnRowWaitS) continue;
                        if (OrderRowGone(g, u, ownKey)) continue;
                    }
                    AswMember was;
                    _lastKnown.TryGetValue(u, out was);
                    string name = g.Refs.ContainsKey(u) ? g.Refs[u].Name : I(u);
                    string task;
                    g.Assign.TryGetValue(u, out task);
                    // a deck row that launched on its own (task / unit row), or a machine that went down to land: follow it (T-150)
                    if (was != null && (KeyIs(was, "task:") || KeyIs(was, "unit:")) && was.Where != "deck_recover")
                    {
                        StartAdopt(g, u, was, task, "launch");
                        continue;
                    }
                    if (was != null && g.Order != "sar" && (was.Where == "air" || was.Where == "deck_recover" || KeyIs(was, "cool:")))
                    {
                        StartAdopt(g, u, was, task, "land");
                        continue;
                    }
                    if (g.Order == "sar") { _sarAir.Vanished(g, u); continue; }
                    if (was != null && KeyIs(was, "hangar:") && was.Slot == 0)   // a legacy stack row (T-181: slots are never "empty": see RebindAll)
                    {
                        _game.Log("asw member hangar_empty " + g.Name + " uid=" + I(u) + " " + name);
                        RemoveMember(g, u, false); _mem.Remove(u);
                        if (g.Members.Count == 0) Warn(g, "hangar_empty", name, 600);
                        continue;
                    }
                    string reason = was == null ? "unknown" : was.Where == "air" ? "left_air_landed_or_lost" : "left_deck";
                    _game.Log("asw member lost " + g.Name + " uid=" + I(u) + " " + name + " reason=" + reason);
                    Warn(g, "member_lost", name + ": " + reason, 30);
                    RemoveMember(g, u, false); _mem.Remove(u); continue;
                }
                if (CheckTakeover(g, u, m)) continue;   // T-214
                RadarTick(g, u, m);   // T-241
                if (g.Order == "sar" && (m.Where == "sar" || m.GameSarZone))
                {
                    string why = AswMath.SarExclusionReason(m);
                    RemoveMember(g, u, true); Warn(g, m.Where == "sar" ? "sar_took" : "game_sar_zone", m.Name, 600);
                    _game.Log("[Sar] group " + g.Name + " drops " + m.Name + " reason=" + why);
                    continue;
                }
                if (m.Where == "sar") Warn(g, "sar_took", m.Name, 600);
            }
            if (g.Order == "none") return;
            if (!g.Dirty && g.Order != "sar" && g.Order != "strike" && HolderGrounded(g)) g.Dirty = true;   // T-172 B9: a reserve is airborne while the holder of a tactic is still on deck
            if (g.Dirty) { Reassign(g); g.Dirty = false; }
            if (g.Order == "strike") { TickStrike(g); return; }
            if (g.Order == "sar") { _sarAir.TickGroup(g); return; }
            AswTarget t = null;
            if (g.Order == "course")
            {
                t = _game.Target(g.TargetUid) ?? (g.TargetName != null ? _game.TargetByName(g.TargetName) : null);
                if (t == null) { Warn(g, "target_lost"); g.Status = "no_target"; return; }
                g.TargetUid = t.Uid;
                if (g.TargetKind == "formation")
                {
                    if (g.FormWidthNm <= 0) g.FormWidthNm = FormationWidth(t.Uid);   // restored from a save: measured once
                    if (g.FormWidthNm > t.WidthNm) t.WidthNm = g.FormWidthNm;
                }
                UpdateFrame(g, t);
                UpdateBand(g, t);   // T-229: the sliding barrier
            }
            else if (!g.AreaBuilt) BuildArea(g);
            ScanPre(g, t);
            g.Status = "";
            foreach (int u in g.Members)
            {
                AswMember m; if (!_live.TryGetValue(u, out m)) continue;   // waiting to be adopted
                if (m.Where != "air" || Excluded(m)) continue;
                if (Attacking(u)) continue;   // 0.27.10: on its attack run (approach / just released)
                if (AswMath.IsLeaving(m)) continue;   // T-172 B10: the game sends it home (Bingo / Winchester / RTB): no waiting point, no route, no package
                if (AirRetreat(g, m)) continue;   // T-175: stage 2 of the air threat: the aircraft flies to the cover point, the tactic waits
                string tac;
                if (!g.Assign.TryGetValue(u, out tac)) continue;
                if (tac == "reserve") { Standby(g, m, t); continue; }
                if (HoldsContact(g, m)) continue;   // T-172 B7: the dipping aircraft that holds a live contact is not led away by its own tactic
                Prepare(g, m);
                if (g.Order == "course") CourseTactic(g, m, t, tac);
                else AreaTactic(g, m, tac);
            }
            AirResumeCheck(g);
            Contacts(g, t);
            Relief(g, t);
            if (g.Order == "area" && g.AreaBuilt) UpdateAreaView(g);
        }

        /// <summary>
        /// T-150 R5: an airborne reserve is not left circling over its ship: one waiting point (ahead of the barrier on the course, the centre of the area),
        /// repeated when it has drifted more than 3 nm away, at most every 300 s.
        /// </summary>
        private void Standby(Grp g, AswMember m, AswTarget t)
        {
            GeoPt p;
            if (g.Order == "course")
            {
                if (t == null) return;
                p = AswMath.ToGeo(Pos(t), g.Axis, new NmPt(g.P.DMinNm, 0));
            }
            else if (g.Order == "area" && g.Rect != null)
            {
                double north, east;
                AswMath.RectSpans(g.Rect, out p, out north, out east);
            }
            else return;
            double last;
            bool first = !g.StandbyAt.TryGetValue(m.Uid, out last);
            if (!first)
            {
                if (_now - last < 300) return;
                if (m.Lat == 0 && m.Lon == 0) return;
                if (AswMath.DistNm(new GeoPt(m.Lat, m.Lon), p) <= 3) return;
            }
            g.StandbyAt[m.Uid] = _now;
            string err = _game.Goto(m.Uid, new List<GeoPt> { p }, 0);   // T-172 B2: no height of its own (the current one)
            _game.Log("asw standby " + g.Name + " uid=" + I(m.Uid) + " -> " + (err ?? "ok"));
        }

        /// <summary>
        /// Keeps the assignments that are still in g.Tactics; every other member (new, reserve, or holding a tactic that was switched off)
        /// takes the tactics nobody holds yet, else "reserve". A member whose tactic changed loses its running job.
        /// </summary>
        private void Reassign(Grp g)
        {
            if (g.Order == "sar" || g.Order == "strike")   // every member of a SAR / strike group does the one task
            {
                var all = new Dictionary<int, string>();
                foreach (int u in g.Members) all[u] = g.Order;
                g.Assign = all;
                return;
            }
            var keep = new Dictionary<int, string>();
            var held = new HashSet<string>();
            var fresh = new List<AswMember>();
            bool airReserve = AirReserve(g);   // T-172 B9: a holder that is not airborne hands its tactic to an airborne reserve
            foreach (int u in g.Members)
            {
                AswMember m;
                string t, pk;
                if (!_live.TryGetValue(u, out m))
                {
                    if (PendingTask(g, u, out pk) && g.Assign.TryGetValue(u, out t)) { keep[u] = t; if (t != "reserve") held.Add(t); }   // T-181: a handed-over order keeps its tactic
                    continue;
                }
                if (g.Released.Contains(u)) { keep[u] = "reserve"; continue; }   // T-172 B9
                if (g.Assign.TryGetValue(u, out t) && t != "reserve" && g.Tactics.Contains(t) && (t != "dip" || m.Dipping) && (m.Where == "air" || KeyIs(m, "hangar:") || !airReserve)) { keep[u] = t; held.Add(t); }
                else fresh.Add(m);
            }
            var want = new List<string>();
            foreach (var t in g.Tactics) if (!held.Contains(t)) want.Add(t);
            var a = _cfg.Loc.Lanes == "single" ? AswMath.Assign(fresh, want) : AssignFresh(g, fresh, want, keep);   // T-176 C9: several aircraft may fly one tactic (lanes)
            foreach (var kv in a) keep[kv.Key] = kv.Value;
            foreach (int u in g.Members)
            {
                string was, now;
                g.Assign.TryGetValue(u, out was);
                keep.TryGetValue(u, out now);
                if (was != now) g.Jobs.Remove(u);
            }
            bool changed = keep.Count != g.Assign.Count;
            if (!changed) foreach (var kv in keep) { string old; if (!g.Assign.TryGetValue(kv.Key, out old) || old != kv.Value) { changed = true; break; } }
            g.Assign = keep;
            if (!changed) return;
            var sb = new StringBuilder();
            foreach (var kv in keep) sb.Append(' ').Append(I(kv.Key)).Append('=').Append(kv.Value);
            _game.Log("asw assign " + g.Name + ":" + sb);
            LogLanes(g, keep);
        }

        /// <summary>The aircraft holds a live contact of the group with its dipping sonar (the contact code owns its route).</summary>
        private bool HoldsContact(Grp g, AswMember m)
        {
            if (!m.Dipping) return false;
            foreach (var rec in g.Contacts) if (rec.Holder == m.Uid) return true;
            return false;
        }

        /// <summary>An airborne reserve that could take over (not leaving, not just released).</summary>
        private bool AirReserve(Grp g)
        {
            foreach (int u in g.Members)
            {
                AswMember m; string t;
                if (!_live.TryGetValue(u, out m) || m.Where != "air" || Excluded(m) || AswMath.IsLeaving(m) || g.Released.Contains(u)) continue;
                if (!g.Assign.TryGetValue(u, out t) || t == "reserve") return true;
            }
            return false;
        }

        /// <summary>True when a tactic of the group is held by an aircraft that is not airborne while a reserve is (T-172 B9).</summary>
        private bool HolderGrounded(Grp g)
        {
            foreach (var kv in g.Assign)
            {
                AswMember m;
                if (kv.Value == "reserve" || !g.Tactics.Contains(kv.Value) || !_live.TryGetValue(kv.Key, out m) || m.Where == "air") continue;
                if (KeyIs(m, "hangar:")) continue;
                return AirReserve(g);
            }
            return false;
        }

        /// <summary>Hands out the tactics at once (command time), not only on the next tick after the clock starts.</summary>
        private void ReassignNow(Grp g)
        {
            if (g.Order == "none" && g.Tactics.Count == 0) return;
            try { RefreshLive(); Reassign(g); g.Dirty = false; }
            catch (Exception e) { _game.Log("asw reassign error " + g.Name + ": " + e.Message); }
        }

        private void Prepare(Grp g, AswMember m)
        {
            if (g.Prepared.Contains(m.Uid)) return;
            g.Prepared.Add(m.Uid);
            string prev;
            if (!_mem.ContainsKey(m.Uid)) _mem[m.Uid] = new AswMemDto { Uid = m.Uid, Name = m.Name, Type = m.Type };
            string err = _game.HoldRoe(m.Uid, out prev);
            if (err == null && _mem[m.Uid].Roe == null) _mem[m.Uid].Roe = prev;
            if (BuoyCount(m) > 0 || m.Dipping)
            {
                string mode = DepthModeOf(g, m);
                string dprev;
                err = _game.SetDepth(m.Uid, mode, out dprev);
                if (err == null && _mem[m.Uid].Depth == null) _mem[m.Uid].Depth = dprev;
            }
        }

        // ---- relief ------------------------------------------------------------------------------------------------

        /// <summary>T-221: the deck the machine returns to: its own deck row, else (a flying machine has DeckUid 0) the home ship.</summary>
        private static int PrepDeckOf(AswMember m) { return m.DeckUid > 0 ? m.DeckUid : m.ShipUid; }

        /// <summary>T-213/T-221: preparation of a hangar order of the machine's type on its deck: the hangar row in the live list, else the last one seen for the deck, else for the type on any deck; -1 = unknown.</summary>
        private double PrepOfType(AswMember m)
        {
            int deck = PrepDeckOf(m);
            foreach (var r in _live.Values)
            {
                if (!AswMath.IsHangarRow(r.DeckKey) || r.HangarPrepS < 0 || r.Type != m.Type) continue;
                if (deck > 0 && r.DeckUid != deck) continue;
                return r.HangarPrepS;
            }
            double seen;
            if (_prepSeen.TryGetValue(deck + "|" + m.Type, out seen)) return seen;
            return _prepSeen.TryGetValue("*|" + m.Type, out seen) ? seen : -1;
        }

        private void Relief(Grp g, AswTarget t)
        {
            var ms = new List<AswMember>();
            foreach (int u in g.Members)
            {
                AswMember live; string tac;
                if (!_live.TryGetValue(u, out live))   // waiting to be adopted
                {
                    // T-181 s.5: an allowed order whose task row is not listed yet still counts as a holder that is taking off (no second order)
                    string pk; OwnRec prec = null;
                    if (PendingTask(g, u, out pk)) prec = g.OwnInfo[pk];
                    if (prec != null && prec.Allow && g.Assign.TryGetValue(u, out tac))
                    {
                        var pend = new AswMember { Uid = u, DeckUid = prec.DeckUid, Type = prec.Type, Where = "deck_prep", Allowed = true, Task = tac, EnduranceS = 9000, FuelFrac = 1, TransitS = 600, LaunchS = 120 };
                        pend.NextPrepS = PrepOfType(pend);   // T-213
                        ms.Add(pend);
                    }
                    continue;
                }
                if (live.Where != "air" && DeckOut(g, live.DeckUid)) continue;   // T-231 D1: a knocked-out deck gives no relief
                if (live.Where != "air") { g.Dumped.Remove(u); g.Released.Remove(u); }
                else g.Allowed.Remove(u);
                var copy = new AswMember
                {
                    Uid = live.Uid, DeckUid = live.DeckUid, ShipUid = live.ShipUid, Name = live.Name, Type = live.Type, TypeLabel = live.TypeLabel, Where = live.Where, Base = live.Base, Loadout = live.Loadout,
                    ReadyInS = live.ReadyInS, FuelFrac = live.FuelFrac, EnduranceS = live.EnduranceS, TransitS = live.TransitS, LaunchS = live.LaunchS,
                    OnStationS = live.OnStationS, Lat = live.Lat, Lon = live.Lon, Passive = live.Passive, ActiveBuoys = live.ActiveBuoys, Vlad = live.Vlad,
                    Torpedoes = live.Torpedoes, BuoysByAmmo = live.BuoysByAmmo, Dipping = live.Dipping, DipSrc = live.DipSrc, DipNote = live.DipNote, Mad = live.Mad, AiState = live.AiState, DeckKey = live.DeckKey,
                    Allowed = g.Allowed.Contains(u), DeckPhase = live.DeckPhase, Slot = live.Slot, HangarPrepS = live.HangarPrepS,
                    NextPrepS = live.NextPrepS >= 0 ? live.NextPrepS : PrepOfType(live)   // T-213
                };
                copy.Task = g.Assign.TryGetValue(u, out tac) ? tac : "reserve";
                ms.Add(copy);
            }
            // one task per tactic in use; deck aircraft assigned "reserve" are candidates for every task
            var tasks = new List<AswTaskSpec>();
            var seen = new HashSet<string>();
            foreach (var kv in g.Assign)
            {
                if (kv.Value == "reserve" || !seen.Add(kv.Value)) continue;
                double bph = kv.Value == "rows" || kv.Value == "corridor" || kv.Value == "grid" ? 20 : kv.Value == "cross" || kv.Value == "barrier" ? 4 * 3600.0 / Math.Max(600, AswMath.RefreshS(g.P.ANewNm, g.P.DMinNm, Math.Max(g.V, 1), 28800)) : 0;
                int holders = 0;
                foreach (var hv in g.Assign.Values) if (hv == kv.Value) holders++;
                tasks.Add(new AswTaskSpec { Id = kv.Value, Tactic = kv.Value, NeedBuoys = bph > 0 ? 4 : 0, BuoysPerHour = bph, Holding = g.Contacts.Count > 0 && kv.Value == "dip", Want = holders });
            }
            if (tasks.Count == 0) return;
            g.ReliefIn = -1;
            var plan = AswMath.PlanRelief(ms, tasks, _now, g.P);
            foreach (var a in plan)
            {
                switch (a.Kind)
                {
                    case "launch":
                    case "prepare":
                        DeckAction(g, a.Kind, a.Uid, a.TaskId, null);
                        break;
                    case "release":
                        g.Assign[a.Uid] = "reserve"; g.Jobs.Remove(a.Uid);
                        g.Released.Add(a.Uid); g.Dirty = true;   // T-172 B9: the tactic goes to an airborne reserve at once, not to the aircraft that was just released
                        break;
                    case "dump_buoys":
                        DumpBuoys(g, _live[a.Uid], t);
                        break;
                    case "warn":
                        Warn(g, a.Code, a.GapMin > 0 ? N(a.GapMin) + " min" : null, 600);
                        g.ReliefIn = a.GapMin;
                        break;
                }
            }
        }

        // ---- order "sar" (T-109, ADR-0039) -------------------------------------------------------------------------

        /// <summary>Groups in deck order (ASW groups with a contact first, then window order); without SAR simply the window order.</summary>
        private List<Grp> Ordered()
        {
            var res = new List<Grp>(_groups);
            var prio = new List<AirGroupPrio>();
            for (int i = 0; i < _groups.Count; i++)
                prio.Add(PrioOf(_groups[i]));
            var names = AirOpsMath.DeckOrder(prio);
            res.Sort((a, b) => names.IndexOf(a.Name).CompareTo(names.IndexOf(b.Name)));
            return res;
        }

        /// <summary>The SAR controller (ships, submarines, helicopter claims). Set by the plugin; null = SAR is off.</summary>
        public SarController Sar { get; set; }

        /// <summary>The [Sar] settings (standby, tie-break of loadouts, mission type of the deck orders, ...).</summary>
        public SarParams SarOptions { get { return _sarAir.Options; } set { _sarAir.Options = value; } }

        /// <summary>The order "sar" (T-121): SarAirOrder.cs. Field "order sar" of <see cref="Grp"/> stays here.</summary>
        private readonly SarAirOrder _sarAir;

        /// <summary>sar_order: {group, area?, on_complete?}. The group takes the order "sar"; machines that cannot serve go back to "available" with a reason.</summary>
        public string SarOrder(Dictionary<string, object> c) { if (_deferred != null) return Fail("restore_waiting"); return _sarAir.Order(c); }

        /// <summary>sar_ship: {uid, op: start|stop, radius_nm?} for a ship or submarine (the "Старпом" menu); the run itself is the SAR controller's.</summary>
        public string SarShip(Dictionary<string, object> c) { if (_deferred != null) return Fail("restore_waiting"); return _sarAir.Ship(c); }

        /// <summary>Name of the ASW/SAR group the unit belongs to, null = none (wired into SarGame.SarAswGroupOf).</summary>
        public string GroupNameOf(int uid) { var g = GroupOf(uid); return g == null ? null : g.Name; }

        private static string RaftsJson(IList<AswRaftView> l)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < l.Count; i++)
            {
                var r = l[i];
                if (i > 0) sb.Append(',');
                var cb = new StringBuilder("[");
                for (int k = 0; k < r.ClaimedBy.Count; k++) { if (k > 0) cb.Append(','); cb.Append(I(r.ClaimedBy[k])); }
                cb.Append(']');
                sb.Append(Obj("id", r.Id, "lat", r.Lat, "lon", r.Lon, "people", r.People, "state", r.State, "unreachable", r.State == "unreachable", "claimed_by", new Raw(cb.ToString())));
            }
            return sb.Append(']').ToString();
        }

    }
}
