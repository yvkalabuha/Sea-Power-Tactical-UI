using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>The game side of the air threat service (implemented by GameApi in AirGuardGame.cs, by AirGuardFakeGame in the stand).</summary>
    internal interface IAirGuardGame
    {
        double Now { get; }
        IList<AirTrack> AirTracks();                // live air / helicopter tracks of the player with class and radius
        IList<AirOwn> OwnAirborne();                // own aircraft in the air (the whole player's side)
        IList<AirSam> OwnSams();                    // own ships with an AAW ring as the game has it (ADR-0055), ammunition > 0
        bool TrackDestroyed(int trackId);
        GeoPt? UnitPos(int uid);                    // position of an own unit (deck ship of an area), null = unknown
        int Aboard(int uid);                        // people on board (SAR), 0 = none / unknown
        void Event(string name, string dataJson);
        void Log(string line);                      // the controller adds the "[AirGuard] " prefix
    }

    /// <summary>T-175 / ADR-0075: the air threat service. One tick per second of game time; decides the stages, owners of the aircraft apply them.</summary>
    internal sealed partial class AirGuardController : IAirThreats, IAirAlertNotes
    {
        private sealed class TrackMem { public AirTrack T; public double Lat0, Lon0, SeenAt, FirstSeen; }
        private sealed class UState
        {
            public AirStage Stage; public double ClearSince = -1, RetreatSince = -1;
            public AirUnitThreat Threat; public AirTrack Worst;
            public AirCover Cover; public double CoverAt, CoverBrg; public int CoverTrackId; public int MissileId;   // CoverTrackId (T-192): the track the Cover was built from
            public HashSet<int> Released = new HashSet<int>(); public bool Receding;   // T-180: tracks that let the helicopter go; the last drop of the stage was a soft one
            public double SoftAt = double.NegativeInfinity; public Dictionary<int, double> CloseSince = new Dictionary<int, double>();   // T-207: the soft resume time; since when a released track approaches
        }
        private sealed class GState
        {
            public string Window, Name; public AirStage Area, Eff, Reported; public double ClearSince = -1, RetreatSince = -1;
            public double IgnoreUntil, IgnoreAt; public AirTrack Worst; public double Nm, EtaS; public int WorstUid;
            public HashSet<int> Released = new HashSet<int>(); public bool Receding;   // T-180: the same for a helicopter group (zone)
            public double SoftAt = double.NegativeInfinity; public Dictionary<int, double> CloseSince = new Dictionary<int, double>();   // T-207
        }
        private sealed class Eval { public AirStage Raw; public bool ClearOk = true; public AirTrack Worst; public double Nm, EtaS; public string Why; public HashSet<int> Soft = new HashSet<int>(); }

        private readonly IAirGuardGame _g;
        private readonly AirGuardParams _p;
        private readonly List<KeyValuePair<string, Func<IList<AirGuardGroup>>>> _providers = new List<KeyValuePair<string, Func<IList<AirGuardGroup>>>>();
        private readonly Dictionary<int, TrackMem> _mem = new Dictionary<int, TrackMem>();
        private readonly Dictionary<int, string> _trackRaid = new Dictionary<int, string>();
        private readonly Dictionary<int, UState> _units = new Dictionary<int, UState>();
        private readonly Dictionary<string, GState> _groups = new Dictionary<string, GState>();
        private readonly Dictionary<int, AirOwn> _own = new Dictionary<int, AirOwn>();
        private readonly Dictionary<int, string> _uidGroup = new Dictionary<int, string>();   // uid -> group key
        private readonly Dictionary<string, AirGuardGroup> _defs = new Dictionary<string, AirGuardGroup>();
        private readonly HashSet<int> _missilesSeen = new HashSet<int>();
        private readonly HashSet<int> _ungroupedReported = new HashSet<int>();
        private readonly Dictionary<int, AirStage> _ungroupedStage = new Dictionary<int, AirStage>();
        private List<AirRaid> _raids = new List<AirRaid>();
        private IList<AirSam> _sams = new List<AirSam>();
        private double _samsAt = -1e9;
        private int _revision;
        private string _lastSig = "";

        public AirGuardController(IAirGuardGame g, AirGuardParams p) { _g = g; _p = p; }

        public AirGuardParams P { get { return _p; } }
        public int Revision { get { return _revision; } }
        public IAirTimeX1 TimeX1 { get; set; }
        public Action<AirThreatCard> CardChanged;

        public void RegisterGroups(string window, Func<IList<AirGuardGroup>> groups)
        {
            _providers.Add(new KeyValuePair<string, Func<IList<AirGuardGroup>>>(window, groups));
        }

        public void Reset()
        {
            _mem.Clear(); _trackRaid.Clear(); _units.Clear(); _groups.Clear(); _own.Clear(); _uidGroup.Clear(); _defs.Clear();
            _alertLines.Clear(); _missilesSeen.Clear(); _ungroupedReported.Clear(); _ungroupedStage.Clear(); _raids = new List<AirRaid>(); _samsAt = -1e9; _lastSig = "";
            _revision++;
        }

        private static string Gk(string window, string group) { return window + "/" + group; }
        private static string F(double v) { return double.IsInfinity(v) ? "-1" : v.ToString("0.#", CultureInfo.InvariantCulture); }
        private void Log(string s) { try { _g.Log("[AirGuard] " + s); } catch { } }

        // ---- queries ----

        public IList<AirTrack> Tracks()
        {
            var res = new List<AirTrack>();
            foreach (var m in _mem.Values) res.Add(Current(m, _g.Now));
            return res;
        }

        private AirStage Cap(AirStage s) { return _p.Mode == "warn" && s > AirStage.Warn ? AirStage.Warn : s; }

        public bool Ignored(string window, string group)
        {
            GState gs;
            return _groups.TryGetValue(Gk(window, group), out gs) && gs.IgnoreUntil > _g.Now;
        }

        public void Ignore(string window, string group, double now)
        {
            string k = Gk(window, group);
            GState gs;
            if (!_groups.TryGetValue(k, out gs)) { gs = new GState { Window = window, Name = group }; _groups[k] = gs; }
            gs.IgnoreUntil = now + _p.IgnoreS; gs.IgnoreAt = now;
            Log("ignore grp=" + group);
            _revision++;
        }

        public AirStage GroupStage(string window, string group)
        {
            if (_p.Mode == "off") return AirStage.None;
            GState gs;
            if (!_groups.TryGetValue(Gk(window, group), out gs) || gs.IgnoreUntil > _g.Now) return AirStage.None;
            return Cap(gs.Eff);
        }

        public AirUnitThreat ForUnit(int uid)
        {
            if (_p.Mode == "off") return null;
            UState us; string gk;
            if (!_units.TryGetValue(uid, out us) || us.Stage == AirStage.None || us.Threat == null) return null;
            GState gs;
            if (_uidGroup.TryGetValue(uid, out gk) && _groups.TryGetValue(gk, out gs) && gs.IgnoreUntil > _g.Now) return null;
            var t = us.Threat;
            var c = new AirUnitThreat { Uid = t.Uid, TrackId = t.TrackId, Stage = Cap(us.Stage), Nm = t.Nm, EtaS = t.EtaS, RNm = t.RNm, RSrc = t.RSrc, Raid = t.Raid, Why = t.Why, Receding = us.Receding };
            if (!_uidGroup.ContainsKey(uid)) c.Stage = c.Stage > AirStage.Warn ? AirStage.Warn : c.Stage;   // outside the groups: warning only
            return c.Stage == AirStage.None ? null : c;
        }

        public AirUnitThreat AtPoint(GeoPt pt, bool helo)
        {
            if (_p.Mode == "off") return null;
            // T-180: for a helicopter a point is blocked only by an approaching track (or inside its radius + margin): evaluated as if it were already in the retreat
            var e = Evaluate(pt.Lat, pt.Lon, 0, 0, helo, helo ? AirStage.Retreat : AirStage.None, _g.Now, helo ? new HashSet<int>() : null);
            var s = Cap(e.Raw);
            if (s == AirStage.None || e.Worst == null) return null;
            return new AirUnitThreat { Uid = 0, TrackId = e.Worst.Id, Stage = s, Nm = e.Nm, EtaS = e.EtaS, RNm = e.Worst.RNm, RSrc = e.Worst.RSrc, Raid = RaidOf(e.Worst.Id), Why = e.Why };
        }

        public AirCover CoverFor(int uid)
        {
            if (_p.Mode != "retreat") return null;
            UState us; AirOwn o;
            if (!_units.TryGetValue(uid, out us) || us.Stage != AirStage.Retreat || !_own.TryGetValue(uid, out o)) return null;
            string gk; GState gs;
            if (!_uidGroup.TryGetValue(uid, out gk)) return null;   // outside the groups: the mod does not move it
            if (_groups.TryGetValue(gk, out gs) && gs.IgnoreUntil > _g.Now) return null;
            var thr = us.Worst;
            double now = _g.Now;
            if (thr == null) return us.Cover;
            // T-192: keep the cover track while a new "worst" one is not clearly closer (the worst track alternates between near keys)
            AirTrack cur = null;
            TrackMem keepMem;
            if (us.Cover != null && us.CoverTrackId != 0 && thr.Id != us.CoverTrackId && _mem.TryGetValue(us.CoverTrackId, out keepMem))
            {
                var old = Current(keepMem, now);
                var nw = Current(new TrackMem { T = thr, Lat0 = thr.Lat, Lon0 = thr.Lon, SeenAt = now }, now);
                double dOld = AswMath.DistNm(new GeoPt(o.Lat, o.Lon), new GeoPt(old.Lat, old.Lon)), dNew = AswMath.DistNm(new GeoPt(o.Lat, o.Lon), new GeoPt(nw.Lat, nw.Lon));
                bool oldWarn;
                if (AirGuardMath.ThreatEligible(old, _p, out oldWarn) && !oldWarn && AirGuardMath.KeepCoverTrack(dOld, dNew, _p)) cur = old;   // the held track must still be a retreat threat
            }
            if (cur == null) cur = Current(new TrackMem { T = thr, Lat0 = thr.Lat, Lon0 = thr.Lon, SeenAt = now }, now);
            double brg = AswMath.BearingDeg(new GeoPt(o.Lat, o.Lon), new GeoPt(cur.Lat, cur.Lon));
            if (us.Cover != null && now - us.CoverAt < _p.CoverRecalcS && AswMath.AngleDiff(brg, us.CoverBrg) <= _p.CoverBearingDeg) return us.Cover;
            double kn = o.SpeedKn > 50 ? o.SpeedKn : (o.Helicopter ? 100 : 250);
            int aboard = 0; try { aboard = _g.Aboard(uid); } catch { }
            AirCover c = AirGuardMath.LandOrCover(o, cur, _sams, kn, aboard, _p, us.Cover != null ? us.Cover.ShipUid : 0);
            if (us.Cover == null || us.Cover.Mode != c.Mode || us.Cover.ShipUid != c.ShipUid)
                Log("cover uid=" + uid + " mode=" + c.Mode + " ship=" + (c.ShipName ?? "-") + " why=" + (c.Note ?? "ok"));
            us.Cover = c; us.CoverAt = now; us.CoverBrg = brg; us.CoverTrackId = cur.Id;
            return c;
        }

        // ---- tracks ----

        private static AirTrack Current(TrackMem m, double now)
        {
            var t = m.T;
            var c = new AirTrack { Id = t.Id, Cls = t.Cls, Lat = m.Lat0, Lon = m.Lon0, CourseDeg = t.CourseDeg, SpeedKn = t.SpeedKn, LastSeenS = t.LastSeenS, Hostile = t.Hostile, Unknown = t.Unknown, Ghost = t.Ghost, Destroyed = t.Destroyed, Helicopter = t.Helicopter, Globe = t.Globe, RNm = t.RNm, RSrc = t.RSrc };
            if (t.Ghost)
            {
                double dt = Math.Max(0, now - m.SeenAt);
                double rad = Math.PI / 180.0;
                if (t.Globe)
                {
                    var g = AswMath.OffsetNm(new GeoPt(m.Lat0, m.Lon0), t.SpeedKn * Math.Cos(t.CourseDeg * rad) * dt / 3600.0, t.SpeedKn * Math.Sin(t.CourseDeg * rad) * dt / 3600.0, true);
                    c.Lat = g.Lat; c.Lon = g.Lon;
                }
                else
                {
                    c.Lat = m.Lat0 + t.SpeedKn * Math.Cos(t.CourseDeg * rad) * dt / 3600.0 / 60.0;
                    c.Lon = m.Lon0 + t.SpeedKn * Math.Sin(t.CourseDeg * rad) * dt / 3600.0 / 60.0;
                }
            }
            return c;
        }

        // T-179 / ADR-0079 section 7: the lines of the CAP alert groups in the raid card, (raid | group) -> line; they go with the raid
        private readonly Dictionary<string, AirThreatCardLine> _alertLines = new Dictionary<string, AirThreatCardLine>();

        /// <summary>The CAP alert group scrambled (or had nobody ready) for a raid: a line of the card, one x1 offer per raid and group.</summary>
        public void AlertLine(string group, IList<int> trackIds, string action, int count, string typeLabel)
        {
            if (_p.Mode == "off" || trackIds == null || group == null) return;
            string raid = null;
            foreach (int id in trackIds) { raid = RaidOf(id); if (raid != null) break; }
            if (raid == null) return;
            var line = new AirThreatCardLine { Group = group, Action = action == "scramble" || action == "preparing" ? action : "not_ready", Count = count, TypeLabel = typeLabel };
            string key = raid + "|" + group;
            AirThreatCardLine prev;
            if (_alertLines.TryGetValue(key, out prev) && (prev.Action == "scramble" && line.Action != "scramble" || prev.Action == "preparing" && line.Action == "not_ready")) return;   // a scramble is not overwritten by a later "nobody" / "preparing" (T-204), "preparing" not by "nobody"
            _alertLines[key] = line;
            Log("alert group=" + group + " raid=" + raid + " action=" + line.Action + " n=" + count);
            Offer("alert_scramble", key, UiText.Fmt("airguard.tc.alert", group, Math.Max(1, count)));
            FireCard(raid, false);
        }

        public bool RaidKeysOn { get { return _p.Mode != "off"; } }
        public string RaidKeyOf(int trackId) { return _p.Mode == "off" ? null : RaidOf(trackId); }
        private string RaidOf(int trackId) { string r; return _trackRaid.TryGetValue(trackId, out r) ? r : null; }

        private bool UpdateTracks(double now)
        {
            bool changed = false;
            var live = new HashSet<int>();
            IList<AirTrack> src = null;
            try { src = _g.AirTracks(); } catch { }
            if (src != null)
                foreach (var t in src)
                {
                    if (t == null) continue;
                    if (t.Destroyed || _g.TrackDestroyed(t.Id)) { if (_mem.Remove(t.Id)) changed = true; continue; }
                    live.Add(t.Id);
                    TrackMem m;
                    if (!_mem.TryGetValue(t.Id, out m)) { m = new TrackMem { FirstSeen = now }; _mem[t.Id] = m; changed = true; }
                    var c = Current(new TrackMem { T = t, Lat0 = t.Lat, Lon0 = t.Lon, SeenAt = now }, now);
                    c.Ghost = false; c.LastSeenS = now;
                    m.T = c; m.Lat0 = t.Lat; m.Lon0 = t.Lon; m.SeenAt = now;
                }
            foreach (int id in new List<int>(_mem.Keys))
            {
                if (live.Contains(id)) continue;
                var m = _mem[id];
                if (_g.TrackDestroyed(id) || now - m.SeenAt > _p.GhostS) { _mem.Remove(id); _trackRaid.Remove(id); changed = true; continue; }
                if (!m.T.Ghost)
                {
                    var c = Current(m, now); c.Ghost = true; m.T = c; changed = true;
                    Log("ghost track=" + id + " cls=" + (m.T.Cls ?? "-"));
                }
            }
            return changed;
        }

        private void UpdateRaids()
        {
            var el = new List<AirTrack>();
            double now = _g.Now;
            foreach (var m in _mem.Values) { bool w; var c = Current(m, now); if (AirGuardMath.ThreatEligible(c, _p, out w)) el.Add(c); }
            var raids = AirGuardMath.Raids(el, _p);
            foreach (var r in raids)
            {
                string key = null;
                foreach (int id in r.TrackIds) { string k; if (_trackRaid.TryGetValue(id, out k)) { key = k; break; } }
                if (key != null) r.Key = key;
                foreach (int id in r.TrackIds) _trackRaid[id] = r.Key;
            }
            _raids = raids;
        }

        // ---- evaluation ----

        private Eval Evaluate(double lat, double lon, double course, double kn, bool helo, AirStage prev, double now, ISet<int> released, IDictionary<int, double> closeSince = null, double softAt = double.NegativeInfinity, int ckBase = 0)
        {
            var e = new Eval();
            var o = new AirOwn { Lat = lat, Lon = lon, CourseDeg = course, SpeedKn = kn, Helicopter = helo };
            double wn = helo ? _p.WarnNmHelo : _p.WarnNm, rn = helo ? _p.RetreatNmHelo : _p.RetreatNm, re = helo ? _p.RetreatEtaSHelo : _p.RetreatEtaS;
            AirStage bestRaw = AirStage.None; double bestKey = double.MaxValue;
            foreach (var m in _mem.Values)
            {
                bool warnOnly; var t = Current(m, now);
                if (!AirGuardMath.ThreatEligible(t, _p, out warnOnly)) continue;
                double d = AswMath.DistNm(new GeoPt(t.Lat, t.Lon), new GeoPt(lat, lon), t.Globe);
                double eta = AirGuardMath.EtaS(d, t.RNm, AirGuardMath.ClosingKn(t, o));
                string why;
                AirStage raw = AirGuardMath.RawStage(d, eta, helo, _p, out why);
                if ((warnOnly || t.RSrc == "no_aaw") && raw > AirStage.Warn) raw = AirStage.Warn;
                if (raw == AirStage.Warn && why == "eta") why = "dist";
                if (warnOnly && raw != AirStage.None) why = "unknown";
                // T-180: a helicopter is let go by a track that does not approach (and the helicopter is outside its radius): no hold, no new retreat from it
                if (helo && released != null && (prev == AirStage.Retreat || released.Contains(t.Id)))
                {
                    double closing = AirGuardMath.TrackClosingKn(t, lat, lon);
                    bool holds;
                    if (prev != AirStage.Retreat && closeSince != null)   // T-207: a released track holds again only after a pause and a steady approach (no flapping)
                    {
                        int ck = ckBase + t.Id;   // zone: one timer per (point, track) - the closing speed differs by point
                        double cs; if (!closeSince.TryGetValue(ck, out cs)) cs = -1;
                        holds = AirGuardMath.HeloReholds(d, t.RNm, closing, now, softAt, ref cs, _p);
                        closeSince[ck] = cs;
                    }
                    else holds = AirGuardMath.HeloHolds(t, d, closing, _p);
                    if (holds) released.Remove(t.Id);
                    else
                    {
                        e.Soft.Add(t.Id);
                        if (raw > AirStage.Warn) raw = AirStage.Warn;
                        if (raw == AirStage.Warn && why == "eta") why = "dist";
                        double skey = (raw == AirStage.None ? 1e6 : 0) + Math.Min(eta, 1e5) * 0.01 + d;
                        if (raw > bestRaw || (raw == bestRaw && skey < bestKey)) { bestRaw = raw; bestKey = skey; e.Worst = t; e.Nm = d; e.EtaS = eta; e.Why = why; }
                        continue;
                    }
                }
                // clearing conditions for the previous stage
                if (prev == AirStage.Retreat && !(d > rn + _p.ClearMarginNm && eta > re + _p.ClearEtaMarginS)) e.ClearOk = false;
                else if (prev == AirStage.Warn && !(d > wn + _p.ClearMarginNm)) e.ClearOk = false;
                double key = (raw == AirStage.None ? 1e6 : 0) + (raw == AirStage.Retreat ? -1e5 : 0) + Math.Min(eta, 1e5) * 0.01 + d;
                if (raw > bestRaw || (raw == bestRaw && key < bestKey)) { bestRaw = raw; bestKey = key; e.Worst = t; e.Nm = d; e.EtaS = eta; e.Why = why; }
            }
            e.Raw = bestRaw;
            return e;
        }

        private const int CkStep = 10000000;
        private void PruneClose(Dictionary<int, double> close)
        {
            foreach (int id in new List<int>(close.Keys)) if (!_mem.ContainsKey(id % CkStep)) close.Remove(id);
        }

        // ---- the tick ----

        public void Tick(double now)
        {
            if (_p.Mode == "off")
            {
                if (_mem.Count > 0 || _units.Count > 0 || _groups.Count > 0) Reset();
                return;
            }
            bool changed = UpdateTracks(now);
            UpdateRaids();
            if (now - _samsAt >= 5) { try { _sams = _g.OwnSams() ?? new List<AirSam>(); } catch { } _samsAt = now; }
            // groups of the owners
            _defs.Clear(); _uidGroup.Clear();
            foreach (var pr in _providers)
            {
                IList<AirGuardGroup> gl = null;
                try { gl = pr.Value(); } catch { }
                if (gl == null) continue;
                foreach (var gr in gl)
                {
                    if (gr == null || gr.Name == null) continue;
                    string k = Gk(gr.Window ?? pr.Key, gr.Name);
                    _defs[k] = gr;
                    foreach (int u in gr.Uids) _uidGroup[u] = k;
                    if (!_groups.ContainsKey(k)) _groups[k] = new GState { Window = gr.Window ?? pr.Key, Name = gr.Name };
                }
            }
            foreach (string k in new List<string>(_groups.Keys)) if (!_defs.ContainsKey(k) && _groups[k].IgnoreUntil <= now) _groups.Remove(k);
            // a new track cancels "Continue tasks"
            foreach (var gs in _groups.Values)
                if (gs.IgnoreUntil > now)
                    foreach (var m in _mem.Values) if (m.FirstSeen > gs.IgnoreAt) { gs.IgnoreUntil = 0; Log("ignore_end grp=" + gs.Name + " why=new_track"); break; }
            // own birds
            IList<AirOwn> owns = null;
            try { owns = _g.OwnAirborne(); } catch { }
            _own.Clear();
            var seen = new HashSet<int>();
            if (owns != null)
                foreach (var o in owns)
                {
                    _own[o.Uid] = o; seen.Add(o.Uid);
                    UState us; if (!_units.TryGetValue(o.Uid, out us)) { us = new UState(); _units[o.Uid] = us; }
                    var e = Evaluate(o.Lat, o.Lon, o.CourseDeg, o.SpeedKn, o.Helicopter, us.Stage, now, us.Released, us.CloseSince, us.SoftAt);
                    AirStage raw = e.Raw; string why = e.Why; AirTrack worst = e.Worst; double nm = e.Nm, eta = e.EtaS;
                    bool clearOk = e.ClearOk;
                    if (o.MissileId != 0)
                    {
                        raw = AirStage.Retreat; why = "missile"; clearOk = false;
                        if (_missilesSeen.Add(o.MissileId))
                        {
                            Log("missile id=" + o.MissileId + " uid=" + o.Uid + " name=" + o.Name);
                            _g.Event("air_threat", "{\"uid\":" + o.Uid + ",\"stage\":2,\"why\":\"missile\",\"missile\":" + o.MissileId + "}");
                            Offer("missile", "missile:" + o.MissileId, UiText.Fmt("airguard.tc.missile", o.Name ?? ("#" + o.Uid)));
                        }
                    }
                    AirStage prev = us.Stage;
                    us.Stage = AirGuardMath.Hysteresis(prev, raw, clearOk, ref us.ClearSince, now, _p);
                    if (us.Stage == AirStage.Retreat && prev != AirStage.Retreat) { us.RetreatSince = now; us.Cover = null; us.CoverTrackId = 0; us.Receding = false; us.CloseSince.Clear(); }
                    if (prev == AirStage.Retreat && us.Stage != AirStage.Retreat && e.Soft.Count > 0) { us.Released.UnionWith(e.Soft); us.Receding = true; us.SoftAt = now; us.CloseSince.Clear(); }
                    if (us.Stage == AirStage.None) { us.Released.IntersectWith(_mem.Keys); PruneClose(us.CloseSince); }
                    if (us.Stage != AirStage.Retreat) { us.RetreatSince = -1; us.Cover = null; us.CoverTrackId = 0; }
                    if (us.Stage != prev) changed = true;
                    if (worst != null || o.MissileId != 0 || us.Stage == AirStage.None)
                    {
                        us.Worst = worst;
                        us.Threat = worst == null && o.MissileId == 0 ? null : new AirUnitThreat { Uid = o.Uid, TrackId = worst == null ? 0 : worst.Id, Stage = us.Stage, Nm = nm, EtaS = eta, RNm = worst == null ? 0 : worst.RNm, RSrc = worst == null ? null : worst.RSrc, Raid = worst == null ? null : RaidOf(worst.Id), Why = why };
                    }
                    if (us.Stage != AirStage.None && us.Threat != null) us.Threat.Stage = us.Stage;
                }
            foreach (int u in new List<int>(_units.Keys)) if (!seen.Contains(u)) { _units.Remove(u); _ungroupedStage.Remove(u); }
            // areas and effective stages of groups
            foreach (var kv in _groups)
            {
                var gs = kv.Value; AirGuardGroup def;
                bool has = _defs.TryGetValue(kv.Key, out def);
                AirStage areaRaw = AirStage.None; bool clearOk = true; AirTrack aw = null; double anm = 0, aeta = 0;
                var areaSoft = new HashSet<int>();
                if (has)
                {
                    var pts = new List<GeoPt>(def.AreaPts);
                    foreach (int du in def.DeckUids) { GeoPt? p = null; try { p = _g.UnitPos(du); } catch { } if (p.HasValue) pts.Add(p.Value); }
                    int pi = 0;
                    foreach (var pt in pts)
                    {
                        var e = Evaluate(pt.Lat, pt.Lon, 0, 0, def.Helicopter, gs.Area, now, gs.Released, gs.CloseSince, gs.SoftAt, (pi++) * CkStep);
                        areaSoft.UnionWith(e.Soft);
                        if (e.Raw > areaRaw || (e.Raw == areaRaw && e.Worst != null && (aw == null || e.Nm < anm))) { areaRaw = e.Raw; aw = e.Worst; anm = e.Nm; aeta = e.EtaS; }
                        if (!e.ClearOk) clearOk = false;
                    }
                }
                AirStage areaPrev = gs.Area;
                gs.Area = AirGuardMath.Hysteresis(gs.Area, areaRaw, clearOk, ref gs.ClearSince, now, _p);
                if (areaPrev == AirStage.Retreat && gs.Area != AirStage.Retreat && areaSoft.Count > 0) { gs.Released.UnionWith(areaSoft); gs.Receding = true; gs.SoftAt = now; gs.CloseSince.Clear(); }
                if (gs.Area == AirStage.Retreat && areaPrev != AirStage.Retreat) { gs.Receding = false; gs.CloseSince.Clear(); }
                if (gs.Area == AirStage.None) { gs.Released.IntersectWith(_mem.Keys); PruneClose(gs.CloseSince); }
                AirStage eff = gs.Area; AirTrack worst = aw; double nm = anm, eta = aeta; int wuid = 0;
                if (has)
                    foreach (int u in def.Uids)
                    {
                        UState us;
                        if (!_units.TryGetValue(u, out us) || us.Stage == AirStage.None || us.Threat == null) continue;
                        if (us.Stage > eff || (us.Stage == eff && us.Threat != null && (worst == null || us.Threat.Nm < nm))) { eff = us.Stage; worst = us.Worst; nm = us.Threat.Nm; eta = us.Threat.EtaS; wuid = u; }
                    }
                gs.Eff = eff; gs.Worst = worst; gs.Nm = nm; gs.EtaS = eta; gs.WorstUid = wuid;
            }
            var watch = TimeX1 as AirTimeX1;   // T-207: a change of the time compression the mod did not make is logged
            if (watch != null) watch.Watch();
            Report(now);
            if (changed) _revision++;
        }

        // ---- reports: events, journal, cards, x1 ----

        private void Offer(string kind, string key, string reason)
        {
            var x = TimeX1;
            if (x == null) return;
            try { x.Offer(kind, key, reason); } catch (Exception e) { Log("x1 offer failed: " + e.GetType().Name); }
        }

        private string ClsText(AirTrack t) { return t == null || t.Cls == null ? UiText.Get("airguard.cls.unknown") : t.Cls; }

        private void Report(double now)
        {
            foreach (var kv in _groups)
            {
                var gs = kv.Value;
                AirStage ms = gs.IgnoreUntil > now ? AirStage.None : Cap(gs.Eff);
                if (ms == gs.Reported) continue;
                AirGuardGroup adef;
                if (_defs.TryGetValue(kv.Key, out adef) && adef.Kind == "cap_alert") { gs.Reported = ms; continue; }   // T-179: the CAP alert group has no warn / retreat of its own - only the scramble line of the card
                AirStage prev = gs.Reported; gs.Reported = ms;
                if (ms > prev)
                {
                    if (ms == AirStage.Retreat) gs.RetreatSince = now;
                    Raised(gs.Window, gs.Name, 0, ms, gs.Worst, gs.Nm, gs.EtaS, now, prev);
                }
                else if (prev == AirStage.Retreat) { Resumed(gs.Window, gs.Name, now - Math.Max(0, gs.RetreatSince), GroupReceding(gs, adef)); gs.RetreatSince = -1; }
            }
            if (_p.WarnUngrouped)
                foreach (var kv in _units)
                {
                    if (_uidGroup.ContainsKey(kv.Key)) continue;
                    var us = kv.Value; AirOwn o;
                    AirStage ms = us.Stage > AirStage.Warn ? AirStage.Warn : us.Stage;
                    AirStage prev; _ungroupedStage.TryGetValue(kv.Key, out prev);
                    if (ms == prev) continue;
                    _ungroupedStage[kv.Key] = ms;
                    _own.TryGetValue(kv.Key, out o);
                    if (ms > prev && us.Threat != null) Raised(null, o != null ? (o.Name ?? ("#" + kv.Key)) : ("#" + kv.Key), kv.Key, ms, us.Worst, us.Threat.Nm, us.Threat.EtaS, now, prev);
                }
        }

        private void Raised(string window, string group, int uid, AirStage stage, AirTrack worst, double nm, double eta, double now, AirStage prev)
        {
            string raid = worst == null ? null : RaidOf(worst.Id);
            Log("threat track=" + (worst == null ? 0 : worst.Id) + " cls=" + (worst == null || worst.Cls == null ? "-" : worst.Cls) + " r_nm=" + (worst == null ? "-" : F(worst.RNm)) + " src=" + (worst == null ? "-" : worst.RSrc ?? "-")
                + " stage=" + (int)stage + " grp=" + group + " nm=" + F(nm) + " eta_s=" + F(eta));
            _g.Event("air_threat", "{\"raid\":" + Json.Str(raid ?? "") + ",\"tracks\":[" + (worst == null ? "" : worst.Id.ToString(CultureInfo.InvariantCulture)) + "],\"stage\":" + (int)stage
                + (uid != 0 ? ",\"uid\":" + uid : ",\"group\":" + Json.Str(group)) + ",\"nm\":" + F(nm) + ",\"eta_s\":" + F(eta) + ",\"r_nm\":" + (worst == null ? "0" : F(worst.RNm)) + ",\"r_src\":" + Json.Str(worst == null ? "default" : (worst.RSrc == "class" ? "class" : "default")) + "}");
            string rk = raid ?? ("t" + (worst == null ? 0 : worst.Id));
            if (stage >= AirStage.Warn && prev == AirStage.None) Offer("raid1", "raid1:" + rk, UiText.Fmt("airguard.tc.raid", ClsText(worst), RaidCount(raid), (int)Math.Round(nm)));
            if (stage == AirStage.Retreat) Offer("stage2", "stage2:" + rk + ":" + group, UiText.Fmt("airguard.tc.stage2", group, (int)Math.Round(nm)));
            if (raid != null) FireCard(raid, false);
        }

        /// <summary>T-180: the group went back to its tasks because the targets stopped approaching (a helicopter), not because they were far.</summary>
        private bool GroupReceding(GState gs, AirGuardGroup def)
        {
            if (gs.Receding) return true;
            if (def != null)
                foreach (int u in def.Uids) { UState us; if (_units.TryGetValue(u, out us) && us.Receding) return true; }
            return false;
        }

        private void Resumed(string window, string group, double afterS, bool receding = false)
        {
            Log("resume grp=" + group + " after_s=" + F(afterS) + (receding ? " why=receding" : ""));
            _g.Event("air_resume", "{\"group\":" + Json.Str(group) + ",\"after_s\":" + F(afterS) + "}");
            // the card of the last raid of this group is re-shown as "resumed"
            var cb = CardChanged;
            if (cb == null) return;
            try { cb(new AirThreatCard { RaidKey = "resume:" + group, Cls = null, Side = null, Resumed = true, Groups = { new AirThreatCardLine { Group = group, Action = "resume" } } }); } catch { }
        }

        private int RaidCount(string key)
        {
            foreach (var r in _raids) if (r.Key == key) return r.Count;
            return 1;
        }

        private void FireCard(string raidKey, bool resumed)
        {
            var cb = CardChanged;
            if (cb == null) return;
            try { var c = BuildCard(raidKey); if (c != null) { c.Resumed = resumed; cb(c); } } catch (Exception e) { Log("card failed: " + e.GetType().Name); }
        }

        internal AirThreatCard BuildCard(string raidKey)
        {
            AirRaid raid = null;
            foreach (var r in _raids) if (r.Key == raidKey) { raid = r; break; }
            if (raid == null) return null;
            var card = new AirThreatCard { RaidKey = raid.Key, Cls = raid.Cls, Count = raid.Count, CourseDeg = raid.CourseDeg, SpeedKn = raid.SpeedKn, CenterLat = raid.CenterLat, CenterLon = raid.CenterLon, Side = "hostile" };
            foreach (int id in raid.TrackIds) { TrackMem m; if (_mem.TryGetValue(id, out m) && !m.T.Hostile) card.Side = "unknown"; }
            double now = _g.Now;
            foreach (var kv in _groups)
            {
                var gs = kv.Value;
                AirGuardGroup adef;
                if (_defs.TryGetValue(kv.Key, out adef) && adef.Kind == "cap_alert") continue;   // T-179: its line is the scramble / not_ready one (AlertLine)
                if (gs.Worst == null || RaidOf(gs.Worst.Id) != raidKey) continue;
                AirStage ms = gs.IgnoreUntil > now ? AirStage.None : Cap(gs.Eff);
                if (ms == AirStage.None) continue;
                var line = new AirThreatCardLine { Group = gs.Name, Window = gs.Window, Nm = gs.Nm, EtaS = gs.EtaS, Action = "warn" };
                if (ms == AirStage.Retreat && gs.WorstUid != 0)
                {
                    var c = CoverFor(gs.WorstUid);
                    if (c != null)
                    {
                        line.Action = c.Mode == "land" ? "land" : (c.Mode == "cover" ? "cover" : "no_cover");
                        line.ShipName = c.ShipName;
                        if (c.Mode == "land") { try { line.Aboard = _g.Aboard(gs.WorstUid); } catch { } }
                    }
                }
                card.Groups.Add(line);
            }
            foreach (var kv in _alertLines)
                if (kv.Key.StartsWith(raidKey + "|", StringComparison.Ordinal)) card.Groups.Add(kv.Value);
            return card;
        }

        public string StateJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"mode\":").Append(Json.Str(_p.Mode)).Append(",\"revision\":").Append(_revision).Append(",\"tracks\":[");
            bool first = true;
            foreach (var t in Tracks())
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(t.Id).Append(",\"cls\":").Append(Json.Str(t.Cls ?? "")).Append(",\"hostile\":").Append(t.Hostile ? "true" : "false")
                  .Append(",\"unknown\":").Append(t.Unknown ? "true" : "false").Append(",\"ghost\":").Append(t.Ghost ? "true" : "false")
                  .Append(",\"r_nm\":").Append(F(t.RNm)).Append(",\"r_src\":").Append(Json.Str(t.RSrc ?? "")).Append(",\"raid\":").Append(Json.Str(RaidOf(t.Id) ?? "")).Append('}');
            }
            sb.Append("],\"groups\":[");
            first = true;
            foreach (var gs in _groups.Values)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"window\":").Append(Json.Str(gs.Window)).Append(",\"group\":").Append(Json.Str(gs.Name)).Append(",\"stage\":").Append((int)Cap(gs.Eff))
                  .Append(",\"ignored\":").Append(gs.IgnoreUntil > _g.Now ? "true" : "false").Append('}');
            }
            sb.Append("],\"units\":[");
            first = true;
            foreach (var kv in _units)
            {
                if (kv.Value.Stage == AirStage.None) continue;
                if (!first) sb.Append(','); first = false;
                var t = kv.Value.Threat;
                sb.Append("{\"uid\":").Append(kv.Key).Append(",\"stage\":").Append((int)kv.Value.Stage).Append(",\"nm\":").Append(t == null ? "0" : F(t.Nm)).Append(",\"eta_s\":").Append(t == null ? "0" : F(t.EtaS)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
