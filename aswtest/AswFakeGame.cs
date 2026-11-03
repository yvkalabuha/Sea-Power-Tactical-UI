using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class DropCall { public int Uid; public string Ammo; public List<GeoPt> Pts; public double At; }

    /// <summary>Simulated game for the controller selftest: a moving target, helicopters that burn fuel, land, get serviced and re-armed.</summary>
    internal sealed class AswFakeGame : IAswGame, IAirOps
    {
        public double Now { get; set; } = 1000;
        public double TimeScale { get; set; } = 1;
        public bool AllowAttack { get; set; } = true;
        private string _lastSavePath;
        public int SavePathReads, MembersCalls;                   // T-126: the idle tick must still read the save marker and must not ask for members
        public string LastSavePath { get { SavePathReads++; return _lastSavePath; } set { _lastSavePath = value; } }
        public string SaveDir { get; set; }
        public List<AswMember> Mem = new List<AswMember>();
        public AswTarget Tgt = new AswTarget { Uid = 500, Name = "Yorktown", Lat = 35, Lon = 20, SpeedKn = 15, CourseDeg = 90, WidthNm = 4 };
        public List<AswContact> Subs = new List<AswContact>();
        public List<string> Events = new List<string>(), Logs = new List<string>(), Calls = new List<string>();
        public List<DropCall> Drops = new List<DropCall>();
        public List<List<GeoPt>> Dips = new List<List<GeoPt>>();
        public List<int> Attacks = new List<int>();
        public bool BlockDrops, RenameOnLaunch, LaunchViaUnit;
        public string AirType, AirLabel;   // type key / label of the launched aircraft (default: as on the deck)
        public double LaunchUnitS = 5;
        public bool GhostLaunch;                                  // the task disappears and no aircraft ever shows up
        public HashSet<int> FixPos = new HashSet<int>();          // aircraft that keep their own position
        public Dictionary<int, AswTarget> Decks = new Dictionary<int, AswTarget>();
        private int _nextReal = 7000;
        public HashSet<int> Changed = new HashSet<int>();
        public Dictionary<int, double> LaunchedAt = new Dictionary<int, double>();

        /// <summary>T-150: the realistic deck of one ship (order of events as in the game); null = the old simple model.</summary>
        public AswFakeDeck RealDeck;

        public IList<AswMember> Members()
        {
            MembersCalls++;
            var l = new List<AswMember>();
            foreach (var m in Mem) l.Add((AswMember)Clone(m));
            if (RealDeck != null) { l.AddRange(RealDeck.Rows()); l.AddRange(RealDeck.AirMembers()); }
            return l;
        }

        // ---- T-150: route guard inputs ----
        public Func<GeoPt, double> Height;                       // null = open sea everywhere
        public List<AswHazard> HazardList = new List<AswHazard>();
        public double HeightM(GeoPt p) { return Height == null ? -200 : Height(p); }
        public IList<AswHazard> Hazards() { return HazardList; }
        public Func<int, int> Queue;                             // null = unknown
        public int QueuedTasks(int uid) { return Queue == null ? -1 : Queue(uid); }

        private static object Clone(AswMember m)
        {
            return typeof(AswMember).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(m, null);
        }

        public AswTarget Target(int uid) { AswTarget d; if (Decks.TryGetValue(uid, out d)) return d; return uid == Tgt.Uid ? Tgt : null; }
        public AswTarget TargetByName(string name) { return name == Tgt.Name ? Tgt : null; }
        public AswTarget Selected;                                // null = Tgt
        public AswTarget SelectedShip() { return Selected ?? Tgt; }
        public List<AswTargetOption> TargetList = new List<AswTargetOption>();
        public IList<AswTargetOption> CourseTargets() { return TargetList; }
        public IList<AswContact> SubContacts() { return Subs; }
        public List<AswBuoyObs> Buoys = new List<AswBuoyObs>();
        public IList<AswBuoyObs> OwnBuoys() { return Buoys; }

        /// <summary>T-172: per-aircraft buoy types (replaces the default three for BuoyTypes(uid)).</summary>
        public Dictionary<int, List<BuoyType>> TypesOf = new Dictionary<int, List<BuoyType>>();

        public List<BuoyType> BuoyTypes(int uid)
        {
            List<BuoyType> own;
            if (TypesOf.TryGetValue(uid, out own)) return new List<BuoyType>(own);
            return new List<BuoyType>
            {
                new BuoyType { Ammo = "usn_ssq-53", Sensor = "SSQ-53_Sonar", PassiveKm = 10, LifeS = 28800 },
                new BuoyType { Ammo = "usn_ssq-62", Sensor = "SSQ-62_Sonar", ActiveKm = 8, LifeS = 14400, Active = true },
                new BuoyType { Ammo = "usn_ssq-77", Sensor = "SSQ-77_Sonar", PassiveKm = 20, LifeS = 28800, Vlad = true }
            };
        }

        public double DipRange = 5.4 * 0.5;
        public double DipRangeNm(int uid) { return DipRange; }

        private AswMember M(int uid)
        {
            foreach (var m in Mem) if (m.Uid == uid) return m;
            return RealDeck == null ? null : RealDeck.FindAir(uid);
        }

        public string Drop(int uid, string ammo, IList<GeoPt> pts)
        {
            var m = M(uid);
            if (m == null) return "not_air_unit";
            bool active = ammo == "usn_ssq-62", vlad = ammo == "usn_ssq-77";
            int have = active ? m.ActiveBuoys : vlad ? m.Vlad : m.Passive;
            int byAmmo;
            if (m.BuoysByAmmo != null && m.BuoysByAmmo.TryGetValue(ammo, out byAmmo)) have = byAmmo;   // T-172: the game checks the count of the very type (Asw.cs:212)
            if (DropError != null) { Drops.Add(new DropCall { Uid = uid, Ammo = ammo, Pts = new List<GeoPt>(pts), At = Now }); return DropError; }
            if (pts.Count > have) return "not_enough";
            AddRoute(uid, pts);
            Drops.Add(new DropCall { Uid = uid, Ammo = ammo, Pts = new List<GeoPt>(pts), At = Now });
            if (BlockDrops) return null;
            if (DropByPathKn > 0)
            {
                // T-334: a point falls when the aircraft gets to it along the path (no stop at a point); the aircraft's position (FixPos) moves along the path, so a test can
                // read the ship's place at the moment of the drop
                var from0 = (m.Lat != 0 || m.Lon != 0) ? new GeoPt(m.Lat, m.Lon) : pts[0];
                double tt = Now; var cur = from0; var mv = new PathMover { Uid = uid };
                foreach (var p in pts)
                {
                    double t1 = tt + AswMath.DistNm(cur, p) / DropByPathKn * 3600.0;
                    mv.Legs.Add(new PathLeg { A = cur, B = p, T0 = tt, T1 = t1 });
                    _pending.Add(new PendingDrop { Uid = uid, Ammo = ammo, P = p, At = t1, Active = active });
                    tt = t1; cur = p;
                }
                Movers.RemoveAll(x => x.Uid == uid); Movers.Add(mv); FixPos.Add(uid);
                return null;
            }
            if (DropDelayS <= 0) { for (int i = 0; i < pts.Count; i++) Consume(m, ammo); Fall(uid, ammo, pts); return null; }
            // ADR-0063: a buoy falls only when the aircraft gets to its point (the first after DropDelayS, then one each DropEveryS); a cleared route drops nothing more
            double at = Now + DropDelayS;
            foreach (var p in pts) { _pending.Add(new PendingDrop { Uid = uid, Ammo = ammo, P = p, At = at, Active = active }); at += DropEveryS; }
            return null;
        }

        /// <summary>ADR-0063: drops that take time (0 = the old instant model). Fallen = the points where a buoy really fell; SimBuoys also lists them in OwnBuoys.</summary>
        public double DropDelayS, DropEveryS = 30;
        /// <summary>T-334: > 0 = the aircraft flies the package at this speed and each point falls when it is reached (0 = the old models).</summary>
        public double DropByPathKn;
        public sealed class FallRec { public int Uid; public GeoPt P, Ship; public double At; }
        /// <summary>T-334: every fall of the path model with the ship's place at that moment.</summary>
        public List<FallRec> FallLog = new List<FallRec>();
        private sealed class PathLeg { public GeoPt A, B; public double T0, T1; }
        private sealed class PathMover { public int Uid; public List<PathLeg> Legs = new List<PathLeg>(); }
        private readonly List<PathMover> Movers = new List<PathMover>();
        public bool SimBuoys;
        public string DropError, DipError, GotoError;                        // T-172: not null = Drop / Dip fail with this code (the call is still recorded)
        public List<int> GotoAlts = new List<int>();               // T-172: altFt of every Goto
        public double ReleaseDelayS;                               // T-172: after a successful Attack Torpedoes-- this many seconds later (0 = on the next Advance, < 0 = never)
        private readonly List<double> _release = new List<double>();
        private readonly List<int> _releaseUid = new List<int>();

        private static void Consume(AswMember m, string ammo)
        {
            if (ammo == "usn_ssq-62") m.ActiveBuoys--; else if (ammo == "usn_ssq-77") m.Vlad--; else m.Passive--;
            int n;
            if (m.BuoysByAmmo != null && m.BuoysByAmmo.TryGetValue(ammo, out n)) m.BuoysByAmmo[ammo] = n - 1;
        }
        public List<GeoPt> Fallen = new List<GeoPt>();
        private sealed class PendingDrop { public int Uid; public string Ammo; public GeoPt P; public double At; public bool Active; }
        private readonly List<PendingDrop> _pending = new List<PendingDrop>();
        private int _buoyId = 90000;

        private void Fall(int uid, string ammo, IList<GeoPt> pts)
        {
            foreach (var p in pts)
            {
                Fallen.Add(p);
                if (SimBuoys) Buoys.Add(new AswBuoyObs { Id = _buoyId++, OwnerUid = uid, Lat = p.Lat, Lon = p.Lon, DroppedS = Now, Ammo = ammo });
            }
        }

        private void FallDue()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                var d = _pending[i];
                if (d.At > Now) continue;
                var m = M(d.Uid);
                _pending.RemoveAt(i--);
                if (m == null) continue;
                Consume(m, d.Ammo);
                Fall(d.Uid, d.Ammo, new[] { d.P });
                if (DropByPathKn > 0) FallLog.Add(new FallRec { Uid = d.Uid, P = d.P, Ship = new GeoPt(Tgt.Lat, Tgt.Lon), At = Now });
            }
        }

        /// <summary>Waypoints queued in the "game" per aircraft: drops and dips append, goto and ClearRoute replace/clear.</summary>
        public Dictionary<int, List<GeoPt>> Route = new Dictionary<int, List<GeoPt>>();

        private void AddRoute(int uid, IList<GeoPt> pts)
        {
            List<GeoPt> r;
            if (!Route.TryGetValue(uid, out r)) Route[uid] = r = new List<GeoPt>();
            r.AddRange(pts);
        }

        public string ClearRoute(int uid) { Movers.RemoveAll(x => x.Uid == uid); Route.Remove(uid); _pending.RemoveAll(d => d.Uid == uid); Calls.Add("clear:" + uid); return null; }
        public List<int> DipUids = new List<int>();
        public string Dip(int uid, IList<GeoPt> pts) { Dips.Add(new List<GeoPt>(pts)); DipUids.Add(uid); if (DipError != null) return DipError; AddRoute(uid, pts); return null; }
        public Dictionary<int, string> GotoErrors = new Dictionary<int, string>();   // T-214: per uid, before the global GotoError
        public string Goto(int uid, IList<GeoPt> pts, int altFt) { Calls.Add("goto:" + uid); GotoAlts.Add(altFt); string ue; if (GotoErrors.TryGetValue(uid, out ue)) return ue; if (GotoError != null) return GotoError; Route.Remove(uid); AddRoute(uid, pts); return null; }

        public string SetDepth(int uid, string mode, out string previous)
        {
            previous = "alternate"; Calls.Add("depth:" + uid + ":" + mode); return null;
        }

        public string HoldRoe(int uid, out string previous) { previous = "weapons_free"; Calls.Add("roe:" + uid); return null; }
        public string RestoreRoe(int uid, string roe) { Calls.Add("restore_roe:" + uid + ":" + roe); return null; }

        public string ResolveError;                               // set: ResolveLoadout fails with this code
        public List<string> ResolveLog = new List<string>();

        public string ResolveLoadout(int deckUid, string type, string loadoutsCsv, out string err)
        {
            err = null;
            ResolveLog.Add(deckUid + ":" + type + ":" + loadoutsCsv);
            if (ResolveError != null) { err = ResolveError; return null; }
            bool onDeck = RealDeck != null && RealDeck.ShipUid == deckUid && RealDeck.HasType(type);
            foreach (var m in Mem) if (m.DeckUid == deckUid) onDeck = true;
            if (!onDeck) { err = "no_deck"; return null; }
            foreach (string lo in (loadoutsCsv ?? "").Split(',')) if (lo.Trim().Length > 0) return lo.Trim();
            err = "unknown_loadout";
            return null;
        }

        /// <summary>The old ASW launch of the test game (before T-119): the first machine of the deck is prepared (readyOnly) or launched.</summary>
        private string AswLaunch(int deckUid, bool readyOnly, out string key)
        {
            key = null;
            foreach (var m in Mem)
            {
                if (m.DeckUid != deckUid) continue;
                key = m.DeckKey;
                if (readyOnly) { Calls.Add("prepare:" + m.Uid); if (m.Where == "deck_service") { m.Where = "deck_prep"; m.ReadyInS = Math.Max(m.ReadyInS, 1); } return null; }
                if (m.Where != "deck_ready") return "not_ready";
                m.Where = "air"; m.FuelFrac = 1.0; if (RenameOnLaunch) m.Uid += 1000; LaunchedAt[m.Uid] = Now; Calls.Add("launch:" + m.Uid );
                return null;
            }
            return "no_deck";
        }

        public List<string> Allows = new List<string>();
        public string AllowError;
        public bool AllowStaysOnDeck;

        public string AllowLaunch(int deckUid, string deckKey)
        {
            Allows.Add(deckUid + ":" + deckKey);
            if (AllowError != null) return AllowError;
            if (AllowStaysOnDeck) return null;   // 0.30.1: the deck took the allow, the machine waits for a free spot (stays a deck_ready row)
            if (RealDeck != null && RealDeck.ShipUid == deckUid && RealDeck.UidOfTask(deckKey) != 0) return RealDeck.Allow(deckKey);
            foreach (var m in Mem)
            {
                if (m.DeckUid != deckUid || m.DeckKey != deckKey) continue;
                if (m.Where != "deck_ready" && m.Where != "deck_prep") return "launch_blocked";
                if (GhostLaunch) { Mem.Remove(m); return null; }
                // a launched deck task becomes a real air unit with a new uid
                var a = AswFakeGame.Helo(m.Uid > 0 ? m.Uid + 2000 : _nextReal++, "air");
                a.DeckUid = m.DeckUid; a.Type = AirType ?? m.Type; a.TypeLabel = AirLabel ?? m.TypeLabel; a.Dipping = m.Dipping; a.Mad = m.Mad;
                a.Helicopter = m.Helicopter; a.Capacity = m.Capacity; a.BestCapacity = m.BestCapacity; a.Loadout = m.Loadout; a.Base = m.Base;
                if (LaunchViaUnit) { a.Where = "deck_launching"; a.DeckKey = "unit:" + a.Uid; a.ReadyInS = LaunchUnitS; a.FuelFrac = 1; a.Type = m.Type; a.TypeLabel = m.TypeLabel; }   // the unit: row has the deck type (T-150), the air unit may report another one
                Mem.Remove(m); Mem.Add(a); LaunchedAt[a.Uid] = Now;
                return null;
            }
            return "deck_task_not_found";
        }

        public bool TasksChangedByOther(int uid) { return Changed.Contains(uid); }
        public string AttackWeapon(int uid) { return "Mk46"; }
        /// <summary>0.27.10: contacts the main map paints red (MapContactVisuals "Enemy"). A contact with a Side is checked like the real
        /// AswGame.Attack does (AttackHostility.Route); a contact without a Side (older cases) is taken as hostile.</summary>
        public HashSet<int> MapRed = new HashSet<int>();
        public List<string> AttackRoutes = new List<string>();
        public double AttackRangeNm = -1;   // 0.27.10: > 0 = the torpedo reaches only this far (else out_of_range, as the game's attack)
        public string Attack(int uid, int contactId) { return Shoot(uid, contactId, false); }

        // ---- T-173: the strike group's view of the game ----
        public List<AswOwnSub> OwnSubsList = new List<AswOwnSub>();
        public bool OwnSubsUnreadable;
        public List<AswShipPos> ShipsList = new List<AswShipPos>();
        public List<AswTorpObs> Torps = new List<AswTorpObs>();
        public bool TorpsUnreadable, SimTorps;                       // SimTorps: a release adds a torpedo object for TorpRunS seconds
        public double TorpRunS = 60;
        public Dictionary<int, int> Hits = new Dictionary<int, int>();   // absent = 0
        public bool HitsOff;
        public Dictionary<int, AswDropSpec> DropSpecs = new Dictionary<int, AswDropSpec>();
        public AswDropSpec DefaultDrop;
        public List<string> AttackTrackCalls = new List<string>();
        public string AttackTrackError;                              // null | code
        public bool TruthTrap;                                       // the controller must never ask for the truth about a foreign submarine
        public string TrueSideOf(int id) { throw new InvalidOperationException("the controller read the true side of track " + id); }
        private int _torpId = 5000;
        private readonly List<int> _releaseTarget = new List<int>();
        private readonly List<KeyValuePair<AswTorpObs, double>> _torpEnd = new List<KeyValuePair<AswTorpObs, double>>();

        public IList<AswOwnSub> OwnSubs() { return OwnSubsUnreadable ? null : OwnSubsList; }
        public IList<AswShipPos> OwnShips() { return ShipsList; }
        public IList<AswTorpObs> TorpedoesInWater() { return TorpsUnreadable ? null : Torps; }
        public int HitsOn(int trackId) { if (HitsOff) return -1; int n; return Hits.TryGetValue(trackId, out n) ? n : 0; }
        public AswDropSpec DropSpec(int uid) { AswDropSpec d; return DropSpecs.TryGetValue(uid, out d) ? d : DefaultDrop; }

        public string AttackTrack(int uid, int contactId, bool unknownOk)
        {
            AttackTrackCalls.Add(uid + ":" + contactId + ":" + (unknownOk ? "u" : "-"));
            if (AttackTrackError != null) return AttackTrackError;
            return Shoot(uid, contactId, unknownOk);
        }

        private string Shoot(int uid, int contactId, bool unknownOk)
        {
            var sub = Subs.Find(s => s.Id == contactId);
            if (AttackRangeNm > 0 && sub == null) return "unknown_target";
            if (AttackRangeNm > 0 && sub != null)
            {
                var sh = Mem.Find(x => x.Uid == uid);
                if (sh != null && AswMath.DistNm(new GeoPt(sh.Lat, sh.Lon), new GeoPt(sub.Lat, sub.Lon)) > AttackRangeNm) return "out_of_range";
            }
            if (sub != null && sub.Side != null && !(unknownOk && sub.Side == "unknown"))
            {
                string route = AttackHostility.Route(sub.Side, MapRed.Contains(contactId));
                AttackRoutes.Add(route);
                if (route == "not_hostile") return route;
            }
            Attacks.Add(contactId);
            if (ReleaseDelayS >= 0) { _release.Add(Now + ReleaseDelayS); _releaseUid.Add(uid); _releaseTarget.Add(contactId); }
            return null;
        }
        public void Event(string name, string dataJson) { Events.Add(name + " " + dataJson); }
        public void Log(string line) { Logs.Add(line); }

        // ---- T-119: the air tick on the test side ----------------------------------------------------------------------

        public AirOwnership Own;
        public AirDeckBroker Broker;

        /// <summary>The air tick: the broker is created (and wired into the controller) on first use; one step = BeginTick, Tick, EndTick.</summary>
        public void Tick(AswController c)
        {
            if (Own == null) Own = c.Ownership ?? new AirOwnership();
            if (c.Ownership == null) c.Ownership = Own;
            if (Broker == null) Broker = new AirDeckBroker(this, Own, new AirDeckParams(), s => Logs.Add(s), null);
            c.Deck = Broker;
            Broker.BeginTick(Now);
            c.Tick(Now);
            Broker.EndTick(Now);
        }

        /// <summary>The frame end outside the tick: requests of window commands (stop, disband) go out.</summary>
        public void Flush() { if (Broker != null) Broker.Flush(Now); }

        public bool OnStation(AswMember m)
        {
            if (m.Where != "air") return false;
            double t;
            return !LaunchedAt.TryGetValue(m.Uid, out t) || Now - t >= m.TransitS + m.LaunchS;
        }

        public void Advance(double dt)
        {
            Now += dt;
            FallDue();
            for (int i = 0; i < _release.Count; i++)
                if (_release[i] <= Now)
                {
                    var sh = M(_releaseUid[i]); if (sh != null && sh.Torpedoes > 0) sh.Torpedoes--;
                    if (SimTorps) { var to = new AswTorpObs { Id = ++_torpId, LauncherUid = _releaseUid[i], TargetId = _releaseTarget[i] }; Torps.Add(to); _torpEnd.Add(new KeyValuePair<AswTorpObs, double>(to, Now + TorpRunS)); }
                    _release.RemoveAt(i); _releaseUid.RemoveAt(i); _releaseTarget.RemoveAt(i); i--;
                }
            for (int i = 0; i < _torpEnd.Count; i++) if (_torpEnd[i].Value <= Now) { Torps.Remove(_torpEnd[i].Key); _torpEnd.RemoveAt(i--); }
            if (RealDeck != null) RealDeck.Advance(dt, Tgt);
            var g = new GeoPt(Tgt.Lat, Tgt.Lon);
            var n = AswMath.Offset(g, Tgt.CourseDeg, Tgt.SpeedKn * dt / 3600.0);
            Tgt.Lat = n.Lat; Tgt.Lon = n.Lon;
            Tgt.Track.Add(new[] { n.Lat, n.Lon });
            if (Tgt.Track.Count > 180) Tgt.Track.RemoveAt(0);
            foreach (var mv in Movers)
            {
                var am = M(mv.Uid); if (am == null) continue;
                PathLeg leg = mv.Legs[mv.Legs.Count - 1]; GeoPt pos = leg.B;
                foreach (var l in mv.Legs) if (Now <= l.T1) { leg = l; double f = l.T1 > l.T0 ? Math.Max(0, Math.Min(1, (Now - l.T0) / (l.T1 - l.T0))) : 1; pos = new GeoPt(l.A.Lat + (l.B.Lat - l.A.Lat) * f, l.A.Lon + (l.B.Lon - l.A.Lon) * f); break; }
                am.Lat = pos.Lat; am.Lon = pos.Lon;
            }
            foreach (var m in Mem)
            {
                if (!FixPos.Contains(m.Uid)) { m.Lat = Tgt.Lat; m.Lon = Tgt.Lon; }
                if (m.Where == "air")
                {
                    m.FuelFrac -= dt / m.EnduranceS;
                    if ((m.FuelFrac * m.EnduranceS - m.TransitS) * 0.9 <= 0)
                    {
                        m.Where = "deck_service"; m.ReadyInS = 2700; m.FuelFrac = 1; m.Passive = 40; m.ActiveBuoys = 8; m.Vlad = 4;
                    }
                }
                else if (m.Where == "deck_launching")
                {
                    m.ReadyInS -= dt;
                    if (m.ReadyInS <= 0) { m.ReadyInS = 0; m.Where = "air"; m.DeckKey = null; LaunchedAt[m.Uid] = Now; if (AirType != null) m.Type = AirType; if (AirLabel != null) m.TypeLabel = AirLabel; }
                }
                else if (m.Where == "deck_service" || m.Where == "deck_prep")
                {
                    m.ReadyInS -= dt;
                    if (m.ReadyInS <= 0) { m.ReadyInS = 0; m.Where = "deck_ready"; }
                }
            }
        }

        public bool HasEvent(string name, string sub = null)
        {
            foreach (var e in Events) if (e.StartsWith(name + " ", StringComparison.Ordinal) && (sub == null || e.Contains(sub))) return true;
            return false;
        }

        public string EventData(string name)
        {
            foreach (var e in Events) if (e.StartsWith(name + " ", StringComparison.Ordinal)) return e.Substring(name.Length + 1);
            return null;
        }

        public static AswMember Helo(int uid, string where, bool dip = false, int passive = 40)
        {
            return new AswMember { Uid = uid, DeckUid = 9000 + uid, Name = "Helo " + uid, Type = "SH-3H", Where = where, Base = "Yorktown",
                FuelFrac = where == "air" ? 0.9 : 1.0, EnduranceS = 9000, TransitS = 600, LaunchS = 120, Passive = passive, ActiveBuoys = 8, Vlad = 4,
                Torpedoes = 2, Dipping = dip, Mad = false, Loadout = "ASWHunter", Lat = 35, Lon = 20, Helicopter = true };
        }

#if SAR_WIP
        // ---- T-109 (SAR): deck orders, loadouts with seats, rafts from a SarFakeGame -----------------------------

        public SarFakeGame SarWorld = new SarFakeGame();
        public List<LoadoutOption> LoadoutList = new List<LoadoutOption>();
        public string Mode = "Realistic";
        public HashSet<string> NoAmmo = new HashSet<string>();
        public List<string> OrderLog = new List<string>();
        public Dictionary<int, double> RtbAt = new Dictionary<int, double>();
        private int _taskNo;

        public Dictionary<string, List<LoadoutOption>> LoadoutsByType = new Dictionary<string, List<LoadoutOption>>();   // T-343: the loadouts of each type (else LoadoutList)
        private List<LoadoutOption> ListOf(string vehicleType)
        {
            List<LoadoutOption> l;
            return vehicleType != null && LoadoutsByType.TryGetValue(vehicleType, out l) ? l : LoadoutList;
        }
        public List<LoadoutOption> SarLoadouts(int deckUid, string vehicleType) { return ListOf(vehicleType); }
        public List<LoadoutOption> Loadouts(int deckUid, string vehicleType) { return ListOf(vehicleType); }
        public List<string> PrioLog = new List<string>();
        public string Prioritise(int deckUid, string key) { PrioLog.Add(deckUid + ":" + key); return null; }

        public List<AirDeckTask> DeckTasks(int deckUid)
        {
            var res = new List<AirDeckTask>();
            foreach (var m in Mem)
                if (m.DeckUid == deckUid && m.DeckKey != null && m.DeckKey.StartsWith("task:", StringComparison.Ordinal))
                    res.Add(new AirDeckTask { Key = m.DeckKey, ShipUid = deckUid, Type = m.Type, Loadout = m.Loadout, State = m.Where == "deck_ready" ? "await_spawn" : "ready_up", Ours = true });
            return res;
        }
        public string DeckTimingsMode() { return Mode; }
        public HashSet<int> NoAirframeDecks = new HashSet<int>();
        public int SarRaftsCalls;   // T-192
        public IList<SarRaft> SarRafts() { SarRaftsCalls++; return SarWorld.RaftList; }
        public IList<SarThreat> SarThreats() { return SarWorld.ThreatList; }

        public string OrderLaunch(int deckUid, string vehicleType, string loadoutName, string missionType, bool allowLaunch, out string key, out double readyS)
        {
            key = null; readyS = 0;
            if (missionType == null && RealDeck != null && RealDeck.ShipUid == deckUid)
            {
                Calls.Add((allowLaunch ? "launch" : "prepare") + ":order:" + vehicleType);
                return RealDeck.Order(vehicleType, allowLaunch, out key, out readyS);
            }
            if (missionType == null) return AswLaunch(deckUid, !allowLaunch, out key);   // the old model changes the row in place: the key is the row's own (never a new task)   // the ASW path (no mission type): the old Launch of this fake
            OrderLog.Add(deckUid + ":" + vehicleType + ":" + loadoutName + ":" + missionType + ":" + allowLaunch);
            if (NoAmmo.Contains(loadoutName) || NoAmmo.Contains(vehicleType + "/" + loadoutName) || NoAmmo.Contains(deckUid + ":" + vehicleType + "/" + loadoutName)) return "no_ammo";   // T-344: by name, by type, or by deck and type
            if (NoAirframeDecks.Contains(deckUid)) return "no_airframes";   // 0.28.1: the hangar of this type is empty
            LoadoutOption lo = null;
            foreach (var l in ListOf(vehicleType)) if (l.Name == loadoutName) lo = l;
            if (lo == null) return "unknown_loadout";
            int best = 0; foreach (var l in ListOf(vehicleType)) best = Math.Max(best, l.Capacity);
            key = "task:o" + (++_taskNo);
            readyS = lo.ReadyUpMin * 60 / AirOpsMath.TimingsMultiplier(Mode);
            Mem.Add(new AswMember { Uid = AswMath.TaskUid(key), DeckUid = deckUid, Name = vehicleType + " order " + _taskNo, Type = vehicleType, Where = readyS > 0 ? "deck_prep" : "deck_ready",
                ReadyInS = readyS, DeckKey = key, Loadout = loadoutName, Helicopter = true, Capacity = lo.Capacity, BestCapacity = best, Base = "Yorktown",
                FuelFrac = 1, EnduranceS = 9000, TransitS = 600, LaunchS = 120, Lat = 35, Lon = 20 });
            return null;
        }

        public string DeckState(int deckUid) { return "inoperable=" + DeckOut + " deck_tasks=" + DeckDump(deckUid); }
        public bool DeckOut;   // live 0.28.3: flight deck knocked out
        public HashSet<int> DeckOutUids = new HashSet<int>();   // T-231 D1: single decks knocked out
        public bool DeckInoperable(int deckUid) { return DeckOut || DeckOutUids.Contains(deckUid); }
        public string DeckDump(int deckUid) { var p = new List<string>(); foreach (var t in DeckTasks(deckUid)) p.Add(t.Type + ":" + t.State + ":" + t.Key); return p.Count == 0 ? "-" : string.Join(" ", p); }

        public HashSet<string> ForeignKeys = new HashSet<string>();   // live finding 38: tasks of the player / game AI the game layer refuses to abort
        public string AbortLaunch(int deckUid, string key)
        {
            if (ForeignKeys.Contains(key)) { Calls.Add("abort_refused:" + key); return "not_ours"; }
            foreach (var m in Mem) if (m.DeckKey == key && m.DeckUid == deckUid) { Mem.Remove(m); Calls.Add("abort:" + key); return null; }
            return "deck_task_not_found";
        }

        public string RtbError;   // 2026-10-22: the game refuses the landing (no_home_base)
        public string SarRtb(int uid)
        {
            Calls.Add("rtb:" + uid);
            if (RtbError != null) return RtbError;
            RtbAt[uid] = Now;
            StopRoute(uid);
            return null;
        }

        private void StopRoute(int uid)
        {
            SarWorld.Routes.Remove(uid);
            SarUnit u;
            if (SarWorld.Units.TryGetValue(uid, out u)) { u.Waypoints = 0; u.RaftPickup = false; }
        }

        public string SarHover(int uid) { Calls.Add("hover:" + uid); StopRoute(uid); return null; }
        public string SarHoverOver(int uid, int shipUid) { Calls.Add("hoverover:" + uid + ":" + shipUid); StopRoute(uid); return null; }
#endif
    }
}
