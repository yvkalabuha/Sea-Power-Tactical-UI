using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VoiceBotBridge
{
    // T-141 (ADR-0048) + T-145 (ADR-0053): anti-torpedo salvos (RBU, Bofors) at the torpedo tracks within 4 nm, by time to impact, one free mount
    // per track. Role "evade": the ship under its own evasion fires before the turn; role "cover": any other ship fires at torpedoes that threaten
    // its own/allied neighbours (a salvo only: no course, no speed, no decoys). Honest mode: the target is a track of the plotting table; the only true
    // object touched is the one behind that track, through Common/Weapons.cs (the same way Attack does). Fires also under ROE hold (self-defence).
    internal sealed partial class GameApi
    {
        internal bool EvasionAtwOn = true, EvasionAtwDebug;
        internal double EvasionAtwSafetyNm = AntiTorpedoMath.DefaultSafetyNm, EvasionAtwReactionS = AntiTorpedoMath.DefaultReactionS;
        // T-145: [AutoEvade] AntiTorpedoMaxTracks / AntiTorpedoCover / AntiTorpedoCoverNm (bound by the settings, defaults here)
        internal int EvasionAtwMaxTracks = 4;
        internal bool EvasionAtwCover = true;
        internal double EvasionAtwCoverNm = 4;
        // T-193 (ADR-0083): [AutoEvade] AntiTorpedoFireNm - a salvo only at a track this close to the shooter
        internal double EvasionAtwFireNm = AntiTorpedoMath.DefaultFireNm;
        internal int AtwFired, AtwSkipped, AtwGone, AtwCoverFired;
        // T-230 (ADR-0095): the order is followed to the launch; a torpedo outside every arc is turned onto the salvo (customer answer Q-T230-1)
        internal int AtwLaunched, AtwFireFailed, AtwTurns;
        internal bool EvasionAtwTurnOn = true;

        private sealed class AtwPending { public int TrackId, Uid, Before; public double THitS; public string File; public AtwOrder Order; }

        private sealed class AtwTorpRef { public int Id; public object Veh, Obj; public double Lat, Lon; public bool Fresh = true; public double AgeS = double.NaN; }
        private sealed class AtwOwnRef { public object U; public int Uid; public double Lat, Lon; }
        private sealed class AtwShipRef { public int Uid; public string Side; public double Lat, Lon; }

        /// <summary>Once per mission second, shared by all ships: torpedo tracks, own ships (truth), friendly/neutral ship tracks.</summary>
        private sealed class AtwSnap
        {
            public double T;
            public readonly List<AtwTorpRef> Torps = new List<AtwTorpRef>();
            public readonly List<AtwOwnRef> Own = new List<AtwOwnRef>();
            public readonly List<AtwShipRef> Tracks = new List<AtwShipRef>();
        }

        private readonly AtwWindows _atwWindows = new AtwWindows();
        private readonly AtwClosing _atwClosing = new AtwClosing();
        private readonly AtwMounts _atwMounts = new AtwMounts();
        private readonly Dictionary<long, string> _atwLastReason = new Dictionary<long, string>();
        private readonly Dictionary<int, int> _atwSalvos = new Dictionary<int, int>();
        private readonly Dictionary<object, double> _atwCoverNext = new Dictionary<object, double>(new RefEq());
        private readonly List<AtwPending> _atwPending = new List<AtwPending>();
        private readonly List<AtwOrder> _atwOrders = new List<AtwOrder>();                              // T-230: orders not yet launched / failed
        private readonly Dictionary<AtwOrder, object> _atwOrderTask = new Dictionary<AtwOrder, object>();   // order -> the game's EngageTask
        private readonly HashSet<object> _atwOwnTasks = new HashSet<object>(new RefEq());               // tasks the mod added (own_order vs game_task)
        private readonly AtwTurnState _atwTurn = new AtwTurnState();                                     // T-230: turn onto the salvo, per ship
        private readonly AtwYawRate _atwYaw = new AtwYawRate();
        private readonly HashSet<int> _atwYawJumpLogged = new HashSet<int>();   // T-253: one jump line per ship per mission

        private void AtwYawJumpLog(int uid, double hdg)
        {
            if (!_atwYawJumpLogged.Add(uid)) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Log("[Evasion] atw yaw jump uid=" + uid + " d_hdg=" + Math.Round(_atwYaw.LastJumpDeg).ToString(ci) + " dt_s=" + _atwYaw.LastJumpS.ToString("0.0", ci) + " hdg=" + Math.Round(hdg).ToString(ci));
        }
        private readonly HashSet<string> _atwFailKinds = new HashSet<string>();
        private readonly TrackSpeed _atwSpeed = new TrackSpeed();                  // T-203: robust speed of the plotted track (log only)
        private readonly TrackFreshness _atwFresh = new TrackFreshness();          // T-203: position of the track frozen for TrackFreshness.StaleS
        private readonly Dictionary<int, double> _atwLastFreshS = new Dictionary<int, double>();   // track id -> last mission time the track was fresh (age fallback)
        private AtwSnap _atwSnap;
        private bool _atwMountsFallbackLogged;

        /// <summary>Called by EvasionTick at the start of the "known" branch, before the turn. Never throws.</summary>
        private void EvasionAtwStep(object u, double lat0, double lon0, EvTorp tp, double[] tpos)
        {
            try
            {
                if (!Capabilities.Contains("evasion_atw") || !EvasionAtwOn) return;
                double? nowN = Frame.MissionTimeS;
                if (!nowN.HasValue) return;
                try { double? hd = D(RxGet(u, EvasionSpecs.ObjHeading)); if (hd.HasValue) { int yu = UidOf(u); if (_atwYaw.Update(yu, hd.Value, nowN.Value)) AtwYawJumpLog(yu, hd.Value); } } catch (Exception) { }
                if (!AtwRound(u, "evade", nowN.Value))
                    AtwSkip(UidOf(u), tp.TrackId, tp.DistNm > AntiTorpedoMath.PrefilterNm ? "out_of_range" : "no_track");
            }
            catch (Exception e) { AtwFailed("step", e); }
        }

        /// <summary>T-145 cover hook (AutoEvadeHook, after EvasionTick): a ship that is not evading shoots at torpedoes that threaten its neighbours. Never throws.</summary>
        internal void AtwCoverTick(object u, double realNow)
        {
            try
            {
                if (AntiTorpedoMath.CoverGate(Capabilities.Contains("evasion_atw"), EvasionAtwOn, EvasionAtwCover, true, EvasionActive(u)) != null) return;
                double? nowN = Frame.MissionTimeS;
                if (!nowN.HasValue) return;
                double now = nowN.Value, next;
                if (_atwCoverNext.TryGetValue(u, out next) && now >= next - AntiTorpedoMath.CoverPeriodS && now < next) return;
                _atwCoverNext[u] = now + AntiTorpedoMath.CoverPeriodS;
                AtwEnsureSnapshot(now);
                if (!AtwTorpedoNear(u)) return;
                if (AntiTorpedoMath.CoverGate(true, true, true, ResolveFlag(u, "auto_evade"), EvasionActive(u)) != null) return;
                AtwRound(u, "cover", now);
            }
            catch (Exception e) { AtwFailed("cover", e); }
        }

        /// <summary>Mission left / restarted: forget everything that holds the old mission's units and tracks.</summary>
        internal void AtwLeaveMission()
        {
            _atwWindows.Clear();
            _atwClosing.Clear();
            _atwMounts.Clear();
            _atwLastReason.Clear();
            _atwSalvos.Clear();
            _atwCoverNext.Clear();
            _atwPending.Clear();
            _atwOrders.Clear(); _atwOrderTask.Clear(); _atwOwnTasks.Clear(); _atwTurn.Clear(); _atwYaw.Clear(); _atwYawJumpLogged.Clear(); _atwTurnStarted.Clear();
            _atwSpeed.Clear(); _atwFresh.Clear(); _atwLastFreshS.Clear();
            _atwSnap = null;
        }

        private void AtwFailed(string where, Exception e)
        {
            string kind = e.GetType().Name;
            if (_atwFailKinds.Add(kind)) { try { Log("[Evasion] atw " + where + " failed: " + kind + ": " + e.Message); } catch { } }
        }

        private void AtwEnsureSnapshot(double nowS)
        {
            if (_atwSnap != null && nowS >= _atwSnap.T && nowS - _atwSnap.T < 1.0) return;
            if (_atwSnap != null && nowS < _atwSnap.T) { _atwWindows.Clear(); _atwClosing.Clear(); _atwMounts.Clear(); _atwSalvos.Clear(); _atwSpeed.Clear(); _atwFresh.Clear(); _atwLastFreshS.Clear(); _atwOrders.Clear(); _atwOrderTask.Clear(); _atwOwnTasks.Clear(); _atwTurn.Clear(); _atwYaw.Clear(); _atwYawJumpLogged.Clear(); }   // the clock went back
            var snap = new AtwSnap { T = nowS };
            var live = new List<int>();
            // T-193 B4: own ships first (truth); a friendly track whose true object is one of them is the ship itself, not a second protected ship
            var ownObjs = new HashSet<object>(new RefEq());
            var tf = GetStatic(CoreSpecs.PlayerTaskforce);
            foreach (var listSpec in CoreSpecs.OwnShipLists)
            {
                var list = Get(tf, listSpec) as IEnumerable;
                if (list == null) continue;
                foreach (var o in list)
                {
                    double la, lo, h;
                    if (o == null || !OwnPos(o, out la, out lo, out h)) continue;
                    snap.Own.Add(new AtwOwnRef { U = o, Uid = UidOf(o), Lat = la, Lon = lo });
                }
            }
            foreach (var o in snap.Own) ownObjs.Add(o.U);
            var vehicles = Vehicles();
            if (vehicles != null)
                foreach (var c in vehicles)
                {
                    string kind = Kind(c);
                    if (IsGone(c)) continue;
                    var id = D(Get(c, CoreSpecs.VehId));
                    string rel = Relation(c);
                    if (kind == "Torpedo")
                    {
                        if (!id.HasValue) continue;
                        int tid = (int)id.Value;
                        live.Add(tid);
                        var obj = Get(c, CoreSpecs.VehObject);
                        var p = TrackPos(c);
                        if (!TorpedoTrack.Live(false, rel, p != null) || obj == null) continue;   // T-203: the one live-track rule, shared with the evasion
                        // T-203 (ADR-0087): fresh = the game's detection is live and the plotted position moved within StaleS
                        _atwFresh.Globe = _atwSpeed.Globe = AswMath.WorldGlobe;   // T-352: flat on stable
                        _atwFresh.Update(tid, p[0], p[1], nowS);
                        var hl = Get(c, EvasionSpecs.VehHasLiveTrack);
                        bool fresh = !(hl is bool && !(bool)hl) && !_atwFresh.Stale(tid, nowS);
                        if (fresh) _atwLastFreshS[tid] = nowS;
                        double age = double.NaN;
                        try { var row = ContactInfo == null ? null : ContactInfo.Get(tid); if (row != null) age = row.AgeS; } catch (Exception) { }
                        double lf;
                        if (double.IsNaN(age) && _atwLastFreshS.TryGetValue(tid, out lf)) age = Math.Max(0, nowS - lf);
                        if (fresh) age = 0;
                        snap.Torps.Add(new AtwTorpRef { Id = tid, Veh = c, Obj = obj, Lat = p[0], Lon = p[1], Fresh = fresh, AgeS = age });
                        _atwSpeed.Update(tid, p[0], p[1], nowS);
                    }
                    else if ((rel == "friendly" || rel == "neutral") && (kind == "Surface" || kind == "Submarine"))
                    {
                        var p = TrackPos(c);
                        if (p == null) continue;
                        var truth = Get(c, CoreSpecs.VehObject);
                        if (truth != null && ownObjs.Contains(truth)) continue;
                        snap.Tracks.Add(new AtwShipRef { Uid = id.HasValue ? -2 - (int)id.Value : -2, Side = rel, Lat = p[0], Lon = p[1] });
                    }
                }
            double histNm = AntiTorpedoMath.PrefilterNm + 10;
            foreach (var q in snap.Torps)
            {
                foreach (var o in snap.Own)
                {
                    double d = WorldDistanceNm(o.Lat, o.Lon, q.Lat, q.Lon);
                    if (d <= histNm) _atwClosing.Update(o.Uid, q.Id, d, nowS);
                }
                foreach (var a in snap.Tracks)
                {
                    if (a.Side != "friendly") continue;
                    double d = WorldDistanceNm(a.Lat, a.Lon, q.Lat, q.Lon);
                    if (d <= histNm) _atwClosing.Update(a.Uid, q.Id, d, nowS);
                }
            }
            _atwWindows.ResetIfTimeWentBack(nowS);
            _atwWindows.Prune(live);
            _atwClosing.Prune(live);
            _atwSpeed.Prune(live); _atwFresh.Prune(live);
            List<int> deadFresh = null;
            foreach (int k in _atwLastFreshS.Keys) if (!live.Contains(k)) (deadFresh ?? (deadFresh = new List<int>())).Add(k);
            if (deadFresh != null) foreach (int k in deadFresh) _atwLastFreshS.Remove(k);
            _atwMounts.Prune(nowS);
            var liveSet = new HashSet<int>(live);
            List<int> gone = null;
            foreach (int id in _atwSalvos.Keys) if (!liveSet.Contains(id)) (gone ?? (gone = new List<int>())).Add(id);
            if (gone != null) foreach (int id in gone) _atwSalvos.Remove(id);
            _atwSnap = snap;
        }

        // any hostile/unknown torpedo track within the prefilter distance of the unit
        private bool AtwTorpedoNear(object u)
        {
            double lat0, lon0, h;
            if (_atwSnap == null || _atwSnap.Torps.Count == 0 || !OwnPos(u, out lat0, out lon0, out h)) return false;
            foreach (var q in _atwSnap.Torps)
                if (WorldDistanceNm(lat0, lon0, q.Lat, q.Lon) <= AntiTorpedoMath.PrefilterNm) return true;
            return false;
        }

        /// <summary>One round of one shooter. false = no torpedo track within the prefilter distance (nothing touched).</summary>
        private bool AtwRound(object u, string role, double nowS)
        {
            AtwEnsureSnapshot(nowS);
            AtwFollowOrders(nowS);
            AtwCheckResults(nowS);
            double lat0, lon0, h;
            if (!OwnPos(u, out lat0, out lon0, out h)) return false;
            int uid = UidOf(u);
            if (role == "evade") AtwTurnPoll(u, uid, nowS);
            var near = new List<AtwTorpRef>();
            var dists = new Dictionary<int, double>();
            // T-287 (ADR-0114): frozen tracks whose pursuit point is within FrozenFireNm are not ranked here: AtwFrozenRound fires at the point
            List<AtwFrozenCand> frozen = role == "evade" ? AtwFrozenCands(uid, lat0, lon0, nowS) : new List<AtwFrozenCand>();
            var frozenIds = new HashSet<int>();
            foreach (var fc in frozen) frozenIds.Add(fc.Q.Id);
            var usedKeys = new HashSet<int>();
            foreach (var q in _atwSnap.Torps)
            {
                if (frozenIds.Contains(q.Id)) continue;
                double d = WorldDistanceNm(lat0, lon0, q.Lat, q.Lon);
                if (d > AntiTorpedoMath.PrefilterNm) continue;
                near.Add(q); dists[q.Id] = d;
            }
            if (near.Count == 0)
            {
                if (frozen.Count == 0) return false;
                AtwFrozenRound(u, uid, frozen, usedKeys, nowS, lat0, lon0);
                return true;
            }

            // protected ships: the shooter, its own/allied neighbours within CoverNm
            var prot = new List<AtwShipRef>();
            if (EvasionAtwCover)
            {
                foreach (var o in _atwSnap.Own)
                {
                    if (ReferenceEquals(o.U, u)) continue;
                    if (WorldDistanceNm(lat0, lon0, o.Lat, o.Lon) <= EvasionAtwCoverNm) prot.Add(new AtwShipRef { Uid = o.Uid, Side = "own", Lat = o.Lat, Lon = o.Lon });
                }
                foreach (var a in _atwSnap.Tracks)
                    if (a.Side == "friendly" && WorldDistanceNm(lat0, lon0, a.Lat, a.Lon) <= EvasionAtwCoverNm) prot.Add(a);
            }
            var round = new AtwRoundIn { ShooterUid = uid, Role = role, NowS = nowS, ReactionS = EvasionAtwReactionS, SafetyNm = EvasionAtwSafetyNm, MaxTracks = EvasionAtwMaxTracks, FireNm = EvasionAtwFireNm };
            var byId = new Dictionary<int, AtwTorpRef>();
            foreach (var q in near)
            {
                var t = new AtwTrackIn { TrackId = q.Id, DistNm = dists[q.Id] };
                t.Fresh = q.Fresh; t.AgeS = q.AgeS;
                int salvos; _atwSalvos.TryGetValue(q.Id, out salvos); t.SalvosSoFar = salvos;
                t.Cands.Add(new AtwThreatCand { Uid = uid, IsShooter = true, DistNm = t.DistNm, ClosingKn = _atwClosing.ClosingKn(uid, q.Id), HeldTtiS = _atwClosing.HeldTtiS(uid, q.Id, nowS, t.DistNm) });
                foreach (var p in prot)
                    t.Cands.Add(new AtwThreatCand { Uid = p.Uid, DistNm = WorldDistanceNm(p.Lat, p.Lon, q.Lat, q.Lon), ClosingKn = _atwClosing.ClosingKn(p.Uid, q.Id), HeldTtiS = _atwClosing.HeldTtiS(p.Uid, q.Id, nowS, WorldDistanceNm(p.Lat, p.Lon, q.Lat, q.Lon)) });
                round.Tracks.Add(t); byId[q.Id] = q;
            }
            // only the tracks that make the rank touch the weapons (options, mounts, game tasks)
            List<AtwTrackIn> ranked = AntiTorpedoMath.Rank(round.Tracks, EvasionAtwMaxTracks);
            round.Tracks = ranked;
            var ready = new HashSet<int>();
            bool keysRead = false;
            foreach (var t in ranked)
            {
                var q = byId[t.TrackId];
                foreach (var o in AtwOptions(u, q.Veh))
                {
                    t.Options.Add(o);
                    if (!o.MountReady || o.Count <= 0 || t.DistNm > o.MaxNm || t.DistNm < o.MinNm) continue;
                    keysRead = true;
                    bool arcKnown;
                    foreach (int k in AtwReadyMountKeys(u, q.Obj, o.File, out arcKnown)) { o.MountKeys.Add(k); ready.Add(k); }
                    if (arcKnown && o.MountKeys.Count == 0) { o.MountReady = false; o.ArcBlocked = true; }   // T-230: no mount has the torpedo in its arc
                }
                AtwNeighbours(u, q, t, lat0, lon0);
            }
            if (keysRead) _atwMounts.ReleaseMissing(uid, ready);

            var tById = new Dictionary<int, AtwTrackIn>();
            foreach (var t in ranked) tById[t.TrackId] = t;
            var assigns = AntiTorpedoMath.Allocate(round, _atwWindows, _atwMounts);
            bool firedNow = false;
            double hdgBack, courseBack; AtwBackCourse(u, out hdgBack, out courseBack);
            bool backBlock = _atwTurn.FireBlocked(uid, nowS, hdgBack, courseBack, AtwTurnState.BackArcCapS);   // T-265: until back on the evasion course (cap 120 s); T-261: by heading   // T-248: the ship is turning back after a salvo: another order would fail out of the arcs
            var receding = new Dictionary<int, string>();   // T-265: track id -> log fields of a salvo not fired at a receding torpedo
            var held = new Dictionary<int, string>();   // T-258: track id -> log fields of a salvo held back in the pause; decided once per round
            foreach (var a in assigns)
            {
                if (!a.Fire) continue;
                string tag;
                double? clNow = _atwClosing.ClosingKn(uid, a.TrackId);
                if (AtwTurnMath.Receding(a.TtiS, clNow ?? double.NaN, AtwTorpKn(a.TrackId))) { receding[a.TrackId] = " tti=inf closing=" + AtwClosingTag(uid, a.TrackId) + " trk_kn=" + AtwTrkKnTag(a.TrackId) + " torp_kn=" + Math.Round(AtwTorpKn(a.TrackId)).ToString() + " role=" + role; continue; }   // T-265: nobody is threatened and the torpedo goes away
                if (AtwBackHolds(backBlock, a, u, uid, byId[a.TrackId], lat0, lon0, nowS, out tag)) held[a.TrackId] = tag; else firedNow = true;
            }
            foreach (var a in assigns)
            {
                var q = byId[a.TrackId];
                if (a.Fire && receding.ContainsKey(a.TrackId))
                {
                    AtwSkip(uid, a.TrackId, "receding", receding[a.TrackId], nowS);
                    continue;
                }
                if (a.Fire && held.ContainsKey(a.TrackId))
                {
                    AtwSkip(uid, a.TrackId, "turn_back", " tti=" + (double.IsInfinity(a.TtiS) || double.IsNaN(a.TtiS) ? "inf" : Math.Round(a.TtiS).ToString()) + held[a.TrackId], nowS);
                    continue;
                }
                if (a.Fire)
                {
                    AtwShot shot; string err;
                    if (EngageAntiTorpedo(u, q.Veh, a.Ammo.File, AntiTorpedoMath.Shots, out shot, out err))
                    {
                        AtwNoteOrder(uid, a, shot, nowS, role);
                        _atwWindows.Note(a.TrackId, a.THitS);
                        _atwMounts.Take(uid, a.MountKey, a.Ammo.File, nowS + EvasionAtwReactionS + AntiTorpedoMath.BusyExtraS);
                        int n; _atwSalvos.TryGetValue(a.TrackId, out n); _atwSalvos[a.TrackId] = n + 1;
                        _atwPending.Add(new AtwPending { TrackId = a.TrackId, Uid = uid, THitS = a.THitS, File = shot.File, Before = shot.Before, Order = _atwLastOrder });
                        AtwFired++;
                        usedKeys.Add(a.MountKey);
                        if (role == "cover") AtwCoverFired++;
                        _atwLastReason.Remove(AntiTorpedoMath.AtwKey(uid, a.TrackId));
                        string tti = double.IsInfinity(a.TtiS) ? "inf" : Math.Round(a.TtiS).ToString();
                        Log("[Evasion] atw fire uid=" + uid + " role=" + role + " torp=" + a.TrackId + " threat=" + a.ThreatUid + " rank=" + a.Rank + "/" + a.Of + " tti=" + tti +
                            " closing=" + AtwClosingTag(uid, a.TrackId) + " trk_kn=" + AtwTrkKnTag(a.TrackId) + " left=" + shot.Before +
                            " live=" + (q.Fresh ? "1" : "0") + " age=" + (double.IsNaN(q.AgeS) ? "na" : Math.Round(q.AgeS).ToString(System.Globalization.CultureInfo.InvariantCulture)) + " trk_err_nm=" + AtwTrackErrTag(q) +
                            " mount=" + a.MountKey + " ammo=" + shot.File + " mount_ready=" + a.Ammo.MountReady +
                            " dist=" + Math.Round(shot.DistNm, 2) + " brg=" + Math.Round(shot.BrgDeg) + " salvo=" + shot.Shots + " t_hit=" + Math.Round(a.THitS, 1));
                        EvLine("auto_evade", "{\"uid\":" + uid + ",\"name\":" + Json.Str(NameOf(u)) + ",\"phase\":\"atw_fire\",\"role\":" + Json.Str(role) +
                            ",\"torpedo_track_id\":" + a.TrackId + ",\"threat_uid\":" + a.ThreatUid + ",\"rank\":" + a.Rank + ",\"of\":" + a.Of +
                            ",\"tti_s\":" + (double.IsInfinity(a.TtiS) ? "null" : Json.Num(Math.Round(a.TtiS, 1))) +
                            ",\"ammo\":" + Json.Str(shot.File) + ",\"dist_nm\":" + Json.Num(Math.Round(shot.DistNm, 2)) + ",\"t_hit_s\":" + Json.Num(Math.Round(a.THitS, 1)) + "}");
                    }
                    else AtwSkip(uid, a.TrackId, "order_" + err);
                    continue;
                }
                if (a.Reason == null) continue;
                if (a.Reason == "game_task" && _atwWindows.Allowed(a.TrackId, a.THitS)) _atwWindows.Note(a.TrackId, a.THitS);
                string reason = a.Reason;
                string extra = null;
                if (reason == "no_arc" && role == "evade" && !firedNow) reason += AtwTurnTry(u, uid, q, tById[a.TrackId], nowS, lat0, lon0, out extra);   // T-230: " why=..."; T-237: fields
                if (reason == "arc_or_reload") extra = AtwRangeTag(u, q, tById[a.TrackId], lat0, lon0);   // T-248: dist / brg / rel / eta_s as in no_arc
                AtwSkip(uid, a.Reason == "no_ammo" ? -1 : a.TrackId, reason, extra, nowS);
            }
            if (frozen.Count > 0) AtwFrozenRound(u, uid, frozen, usedKeys, nowS, lat0, lon0);
            return true;
        }

        private string AtwClosingTag(int uid, int trackId)
        {
            double? c = _atwClosing.ClosingKn(uid, trackId);
            return c.HasValue ? Math.Round(c.Value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "na";
        }

        // T-265: assumed torpedo speed as in AtwTurnTry: the plotted track speed when above the assumed one
        private double AtwTorpKn(int trackId)
        {
            double? v = _atwSpeed.Kn(trackId);
            return v.HasValue && v.Value > AntiTorpedoMath.TorpedoAssumedKn ? v.Value : AntiTorpedoMath.TorpedoAssumedKn;
        }

        private string AtwTrkKnTag(int trackId)
        {
            double? v = _atwSpeed.Kn(trackId);
            return v.HasValue ? Math.Round(v.Value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "na";
        }

        // T-203 (log only, Q1): distance between the plotted track position and the true position of the torpedo object; never read by the logic
        private string AtwTrackErrTag(AtwTorpRef q)
        {
            try
            {
                double la, lo, h;
                if (q.Obj == null || !OwnPos(q.Obj, out la, out lo, out h)) return "na";
                return Math.Round(WorldDistanceNm(q.Lat, q.Lon, la, lo), 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "na"; }
        }

        // ammunition of the file left on the unit now (T-203: confirms that the order launched); -1 = unknown
        private int AtwAmmoLeft(int uid, string file)
        {
            try
            {
                if (_atwSnap == null || file == null) return -1;
                foreach (var o in _atwSnap.Own)
                    if (o.Uid == uid)
                        foreach (var r in WeaponRows(o.U)) if (r[0] == file) return int.Parse(r[5]);
            }
            catch (Exception) { }
            return -1;
        }

        private readonly Dictionary<long, double> _atwSkipLogAt = new Dictionary<long, double>();   // T-248: Debug repeats of arc_or_reload / turn_back: once per 10 s per (ship, torpedo)

        /// <summary>T-261: heading now and evasion course of the ship (NaN when unknown). Never throws.</summary>
        private void AtwBackCourse(object u, out double hdgNow, out double course)
        {
            hdgNow = double.NaN; course = double.NaN;
            try
            {
                hdgNow = D(RxGet(u, EvasionSpecs.ObjHeading)) ?? double.NaN;
                EvRun run;
                if (_evRuns.TryGetValue(u, out run) && run.S.CourseDeg.HasValue) course = run.S.CourseDeg.Value;
            }
            catch (Exception) { }
        }

        private string AtwRangeTag(object u, AtwTorpRef q, AtwTrackIn t, double lat0, double lon0)
        {
            try
            {
                double hdg = D(RxGet(u, EvasionSpecs.ObjHeading)) ?? 0;
                return AtwTurnMath.RangeFields(t.DistNm, WorldBearing(lat0, lon0, q.Lat, q.Lon), hdg, t.TtiS);
            }
            catch (Exception) { return ""; }
        }

        // T-248 (coordinator): the turn-back block holds only torpedoes that need a turn; one already in the arc of a ready mount, or a close one, is fired at.
        // T-258: in the arc of a ready mount on the whole swing back to the evasion course (run 0.30.15: fire at tti=111 on the salvo heading, fire_failed NotInFiringArcs).
        // Unknown arcs or evasion course hold the salvo.
        private bool AtwBackHolds(bool backBlock, AtwAssign a, object u, int uid, AtwTorpRef q, double lat0, double lon0, double nowS, out string tag)
        {
            tag = "";
            bool inArc = a.Ammo != null && a.Ammo.MountReady && !a.Ammo.ArcBlocked && a.Ammo.MountKeys.Count > 0;
            if (backBlock && inArc && !(a.TtiS < AtwTurnState.BackUrgentTtiS))
            {
                double hdgNow = double.NaN, back = double.NaN;
                try
                {
                    double courseNaN;
                    AtwBackCourse(u, out hdgNow, out courseNaN);
                    back = !double.IsNaN(courseNaN) ? courseNaN : _atwTurn.BackHdgDeg(uid, nowS, hdgNow, courseNaN, AtwTurnState.BackArcCapS);
                    inArc = AtwTurnMath.StaysInArc(WorldBearing(lat0, lon0, q.Lat, q.Lon), hdgNow, back, AtwTurnArcs(u, q.Obj, a.Ammo.File), AtwTurnMath.BackArcMarginDeg);
                }
                catch (Exception) { inArc = false; }
                double relBack = double.IsNaN(hdgNow) || double.IsNaN(back) ? double.NaN : AtwTurnMath.Norm180(WorldBearing(lat0, lon0, q.Lat, q.Lon) - back);
                tag = " back_hdg=" + (double.IsNaN(back) ? "na" : Math.Round(back).ToString()) + " rel_back=" + (double.IsNaN(relBack) ? "na" : Math.Round(relBack).ToString()) + " stay=" + (inArc ? "1" : "0");
            }
            return AtwTurnState.BackBlocks(backBlock, inArc, a.TtiS);
        }

        private void AtwSkip(int uid, int trackId, string reason, string extra = null, double nowS = double.NaN)
        {
            AtwSkipped++;
            bool repeat = EvasionAtwDebug && (reason == "arc_or_reload" || reason == "turn_back");
            if (repeat ? AtwTurnMath.LogDue(_atwSkipLogAt, AntiTorpedoMath.AtwKey(uid, trackId), nowS, 10) : EvasionAtwDebug || AntiTorpedoMath.ReasonChanged(_atwLastReason, uid, trackId, reason))
                Log("[Evasion] atw skip uid=" + uid + " torp=" + trackId + " reason=" + reason + (extra ?? ""));
        }

        private void AtwCheckResults(double now)
        {
            for (int i = _atwPending.Count - 1; i >= 0; i--)
            {
                var p = _atwPending[i];
                if (now < p.THitS + AntiTorpedoMath.ResultAfterS) continue;
                if (p.Order != null && p.Order.Phase == "failed") { _atwPending.RemoveAt(i); continue; }   // T-230: no launch, no result (fire_failed was logged)
                _atwPending.RemoveAt(i);
                var c = FindContact(p.TrackId);
                bool gone = c == null || IsGone(c);
                if (gone) AtwGone++;
                string dist = "na";
                if (gone) dist = "gone";
                else if (_atwSnap != null)
                {
                    AtwOwnRef sh = null; AtwTorpRef tq = null;
                    foreach (var o in _atwSnap.Own) if (o.Uid == p.Uid) { sh = o; break; }
                    foreach (var q in _atwSnap.Torps) if (q.Id == p.TrackId) { tq = q; break; }
                    if (sh != null && tq != null) dist = Math.Round(WorldDistanceNm(sh.Lat, sh.Lon, tq.Lat, tq.Lon), 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                int left = AtwAmmoLeft(p.Uid, p.File);
                Log("[Evasion] atw result=" + (gone ? "gone" : "alive") + " uid=" + p.Uid + " torp=" + p.TrackId + " d=" + dist +
                    " left=" + (left < 0 ? "na" : left.ToString()) + " used=" + (p.Order != null && p.Order.Phase == "launched" ? p.Order.Used.ToString() : left < 0 ? "na" : (p.Before - left).ToString()) +
                    " launched=" + (p.Order != null && p.Order.Phase == "launched" ? "1" : "0"));
            }
        }

        private List<AtwOption> AtwOptions(object u, object torpVehicle)
        {
            var res = new List<AtwOption>();
            foreach (var a in AntiTorpedoOptions(u, torpVehicle))
                res.Add(new AtwOption { File = a.File, Display = a.Display, Kill = a.Kill, MinNm = a.MinNm, MaxNm = a.MaxNm, SpeedKn = a.SpeedKn, Count = a.Count, MountReady = a.MountReady });
            return res;
        }

        // safety + game task for one track: own ships (truth) and friendly/neutral ship tracks measured to the torpedo; an own unit within
        // NeighbourTaskNm of the torpedo that already has an EngageTask on it (or the shooter itself) counts as a game task
        private void AtwNeighbours(object shooter, AtwTorpRef q, AtwTrackIn t, double shooterLat, double shooterLon)
        {
            string eng = AtwEngagedBy(shooter, q.Obj);   // T-230: own_order | game_task | game_firing
            if (eng != null) { t.EngagedBy = eng; t.GameTask = true; }
            foreach (var o in _atwSnap.Own)
            {
                if (ReferenceEquals(o.U, shooter)) continue;
                double dist = WorldDistanceNm(o.Lat, o.Lon, q.Lat, q.Lon);
                t.Near.Add(new AtwNeighbour { Uid = o.Uid, Side = "own", DistToTorpNm = dist });
                if (dist <= AntiTorpedoMath.NeighbourTaskNm && !t.GameTask) { string ne = AtwEngagedBy(o.U, q.Obj); if (ne != null) { t.EngagedBy = ne; t.GameTask = true; } }
            }
            foreach (var a in _atwSnap.Tracks)
                t.Near.Add(new AtwNeighbour { Uid = a.Uid, Side = a.Side, DistToTorpNm = WorldDistanceNm(a.Lat, a.Lon, q.Lat, q.Lon) });
        }

        /// <summary>
        /// Identities (RuntimeHelpers.GetHashCode) of the mounts that can fire this ammo at the torpedo now. T-230: only mounts that have the torpedo in their
        /// firing arc and do not reload (GetWeaponSystemsForAmmunition returns the mount with the NEAREST arc when none has it). Empty = unknown (synthetic mount)
        /// when arcKnown=false, else no mount can fire now.
        /// </summary>
        private List<int> AtwReadyMountKeys(object u, object torpObj, string file, out bool arcKnown)
        {
            object pos = null;
            try { if (Present(EvasionSpecs.ObjUnityPosition)) pos = Call(torpObj, EvasionSpecs.ObjUnityPosition); } catch (Exception) { }
            return AtwReadyMountKeysAt(u, pos, torpObj, file, out arcKnown);
        }

        /// <summary>T-287: the same for a position (boxed Vector3); torpObjOrNull = null for a salvo at a point.</summary>
        private List<int> AtwReadyMountKeysAt(object u, object pos, object torpObj, string file, out bool arcKnown)
        {
            var res = new List<int>();
            arcKnown = false;
            try
            {
                if (!Present(EvasionSpecs.WeaponSystemsForAmmo) || !Present(EvasionSpecs.ObjUnityPosition) || pos == null)
                {
                    if (!_atwMountsFallbackLogged) { _atwMountsFallbackLogged = true; Log("[Evasion] atw mounts: fallback (one synthetic mount per ammo)"); }
                    return res;
                }
                var mounts = CallStatic(EvasionSpecs.WeaponSystemsForAmmo, u, pos, file, torpObj) as IEnumerable;
                if (mounts == null) return res;
                bool arcs = Present(EvasionSpecs.WsInFiringArc);
                arcKnown = arcs;
                foreach (var m in mounts)
                {
                    if (m == null) continue;
                    if (arcs)
                    {
                        var inArc = Call(m, EvasionSpecs.WsInFiringArc, pos);
                        if (inArc is bool && !(bool)inArc) continue;
                        var rl = Present(EvasionSpecs.WsReloading) ? Get(m, EvasionSpecs.WsReloading) : null;
                        if (rl is bool && (bool)rl) continue;
                    }
                    res.Add(RuntimeHelpers.GetHashCode(m));
                }
            }
            catch (Exception e) { res.Clear(); arcKnown = false; AtwFailed("mounts", e); }
            return res;
        }

        // ---- T-287 (ADR-0114): salvo at the pursuit point of a frozen torpedo track ----

        private sealed class AtwFrozenCand { public AtwTorpRef Q; public double PursuitNm, Kn; public double PLat, PLon; public bool Expired; }

        /// <summary>Frozen tracks (not fresh, last detection older than AtwStaleFireMaxS or unknown) of the ship whose pursuit point is within FrozenFireNm and not expired. Never throws.</summary>
        private List<AtwFrozenCand> AtwFrozenCands(int uid, double lat0, double lon0, double nowS)
        {
            var res = new List<AtwFrozenCand>();
            try
            {
                if (_atwSnap == null) return res;
                foreach (var q in _atwSnap.Torps)
                {
                    if (q.Fresh || q.AgeS <= AntiTorpedoMath.AtwStaleFireMaxS) continue;
                    double la, lo;
                    if (!_evPursuit.Point(uid, q.Id, out la, out lo)) continue;
                    double nm = WorldDistanceNm(lat0, lon0, la, lo);
                    bool expired = _evPursuit.Expired(uid, q.Id, nowS);
                    if (nm > AtwTurnMath.FrozenFireNm || expired) continue;
                    res.Add(new AtwFrozenCand { Q = q, PursuitNm = nm, Kn = _evPursuit.SpeedKn(uid, q.Id) ?? TrackPursuit.DefaultKn, PLat = la, PLon = lo, Expired = expired });
                }
            }
            catch (Exception e) { AtwFailed("frozen_cands", e); res.Clear(); }
            return res;
        }

        /// <summary>Unity position of the shooter moved by (dxNm east, dyNm north), y = 0; null = unknown. Never throws.</summary>
        private object AtwAimUnity(object u, double dxNm, double dyNm)
        {
            try
            {
                if (!Present(EvasionSpecs.ObjUnityPosition) || double.IsNaN(dxNm) || double.IsNaN(dyNm)) return null;
                var p = Call(u, EvasionSpecs.ObjUnityPosition);
                if (!(p is UnityEngine.Vector3)) return null;
                var v = (UnityEngine.Vector3)p;
                return new UnityEngine.Vector3(v.x + (float)(dxNm * SaUnityPerNm), 0f, v.z + (float)(dyNm * SaUnityPerNm));
            }
            catch (Exception) { return null; }
        }

        /// <summary>Order of the salvo at a position: EngageTask(ammo, position, shooter, shots) + AddEngageTask. error: no_ctor | no_ammo | failed.</summary>
        private bool EngageAntiTorpedoAt(object u, string ammoFile, object aimUnity, int shots, double aimNm, double aimBrgDeg, out AtwShot shot, out string error)
        {
            shot = null; error = null;
            try
            {
                if (u == null || aimUnity == null) { error = "failed"; return false; }
                if (!Present(EvasionSpecs.EngagePosCtor) || !Present(EvasionSpecs.ObjAddEngageTask))
                {
                    if (_atwFailKinds.Add("no_pos_ctor")) Log("[Evasion] atw frozen: no position ctor");
                    error = "no_ctor"; return false;
                }
                int before = 0;
                foreach (var r in WeaponRows(u)) { if (r[0] == ammoFile) before = int.Parse(r[5]); }
                if (before <= 0) { error = "no_ammo"; return false; }
                if (shots < 1) shots = 1;
                if (shots > before) shots = before;
                var task = New(EvasionSpecs.EngagePosCtor, ammoFile, aimUnity, u, shots, 1000, false, false);
                Call(u, EvasionSpecs.ObjAddEngageTask, task);
                string disp = null;
                try { disp = Get(Get(Call(u, EvasionSpecs.ObjAmmoByName, ammoFile), EvasionSpecs.AmmoParamsOf), EvasionSpecs.ApDisplayedName) as string; } catch { }
                shot = new AtwShot { File = ammoFile, Display = string.IsNullOrEmpty(disp) ? ammoFile : disp, Shots = shots, Before = before, DistNm = aimNm, BrgDeg = aimBrgDeg, Task = task };
                return true;
            }
            catch (Exception) { error = "failed"; return false; }
        }

        private static string AtwNum(double v, string f) { return double.IsNaN(v) ? "na" : v.ToString(f, System.Globalization.CultureInfo.InvariantCulture); }

        /// <summary>The salvos of the round at the pursuit points of frozen tracks (one per track). Never throws.</summary>
        private void AtwFrozenRound(object u, int uid, List<AtwFrozenCand> cands, HashSet<int> usedKeys, double nowS, double lat0, double lon0)
        {
            try
            {
                double hdg = D(RxGet(u, EvasionSpecs.ObjHeading)) ?? double.NaN;
                double ownKn = 0;
                try { ownKn = Math.Abs(D(Call(u, EvasionSpecs.ObjVelocityKn)) ?? 0); } catch (Exception) { }
                double hdgBack, courseBack; AtwBackCourse(u, out hdgBack, out courseBack);
                bool backBlock = _atwTurn.FireBlocked(uid, nowS, hdgBack, courseBack, AtwTurnState.BackArcCapS);
                double backHdg = !double.IsNaN(courseBack) ? courseBack : _atwTurn.BackHdgDeg(uid, nowS, hdgBack, courseBack, AtwTurnState.BackArcCapS);
                double cosLat = AswMath.WorldGlobe ? Math.Cos(lat0 * Math.PI / 180.0) : 1.0;   // T-352: flat on stable
                var order = new List<KeyValuePair<double, AtwFrozenCand>>();
                foreach (var c in cands)
                {
                    var a0 = AtwTurnMath.FrozenAim(AtwFrozenInOf(c, hdg, ownKn, lat0, lon0, cosLat, 0));
                    order.Add(new KeyValuePair<double, AtwFrozenCand>(a0.TtiS, c));
                }
                order.Sort((x, y) => x.Key.CompareTo(y.Key));
                foreach (var kv in order)
                {
                    var c = kv.Value; var q = c.Q;
                    string why = null; AtwFrozenAim shown = null;
                    int salvos; _atwSalvos.TryGetValue(q.Id, out salvos);
                    string eng = AtwEngagedBy(u, q.Obj);
                    bool open = false;
                    foreach (var o in _atwOrders) if (o.Uid == uid && o.TrackId == q.Id) { open = true; break; }
                    if (open) why = "own_order";
                    else if (eng != null) why = eng;
                    var opts = new List<AtwOption>();
                    if (why == null)
                    {
                        foreach (var o in AtwOptions(u, q.Veh)) if (o.Count > 0) opts.Add(o);
                        opts.Sort((x, y) => y.Kill.CompareTo(x.Kill));
                        if (opts.Count == 0) why = "no_ammo";
                    }
                    bool fired = false;
                    foreach (var opt in opts)
                    {
                        var aim = AtwTurnMath.FrozenAim(AtwFrozenInOf(c, hdg, ownKn, lat0, lon0, cosLat, opt.SpeedKn));
                        if (shown == null) shown = aim;
                        string w;
                        object aimU = AtwAimUnity(u, aim.AimX, aim.AimY);
                        bool arcKnown = false;
                        List<int> keys = null; List<AtwArc> arcs = null;
                        if (aimU == null) w = "no_aim";
                        else
                        {
                            keys = AtwReadyMountKeysAt(u, aimU, null, opt.File, out arcKnown);
                            arcs = AtwTurnArcsAt(u, aimU, null, opt.File);
                            w = AtwTurnMath.FrozenGate(aim, c.Expired, salvos, opt.MinNm, opt.MaxNm, EvasionAtwSafetyNm, hdg, arcs, backBlock, backHdg);
                        }
                        int key = 0;
                        double tHit = AntiTorpedoMath.ExpectedHitS(nowS, EvasionAtwReactionS, aim.AimNm, opt.SpeedKn);
                        if (w == null)
                        {
                            bool have = false;
                            foreach (int k in keys) if (!usedKeys.Contains(k) && !_atwMounts.Busy(uid, k, nowS)) { key = k; have = true; break; }
                            if (!have) w = "mount_busy";
                        }
                        double aimLat = lat0 + aim.AimY / 60.0, aimLon = lon0 + aim.AimX / (60.0 * (cosLat == 0 ? 1 : cosLat));
                        if (w == null && !_atwWindows.Allowed(q.Id, tHit)) w = "window";
                        if (w == null && AtwFriendlyAt(u, aimLat, aimLon)) w = "friendly_near";
                        if (w != null) { if (why == null) why = w; continue; }
                        AtwShot shot; string err;
                        if (!EngageAntiTorpedoAt(u, opt.File, aimU, AntiTorpedoMath.Shots, aim.AimNm, aim.AimBrgDeg, out shot, out err)) { if (why == null) why = "order_" + err; continue; }
                        var asg = new AtwAssign { TrackId = q.Id, MountKey = key, THitS = tHit, TtiS = aim.TtiS, Ammo = opt, Fire = true };
                        AtwNoteOrder(uid, asg, shot, nowS, "evade");
                        _atwWindows.Note(q.Id, tHit);
                        _atwMounts.Take(uid, key, opt.File, nowS + EvasionAtwReactionS + AntiTorpedoMath.BusyExtraS);
                        _atwSalvos[q.Id] = salvos + 1;
                        _atwPending.Add(new AtwPending { TrackId = q.Id, Uid = uid, THitS = tHit, File = shot.File, Before = shot.Before, Order = _atwLastOrder });
                        AtwFired++;
                        usedKeys.Add(key);
                        _atwLastReason.Remove(AntiTorpedoMath.AtwKey(uid, q.Id));
                        string fields = AtwTurnMath.FrozenFields(aim, hdg);
                        Log("[Evasion] atw salvo frozen uid=" + uid + " torp=" + q.Id + " age=" + AtwNum(q.AgeS, "F0") + " plot_nm=" + AtwNum(WorldDistanceNm(lat0, lon0, q.Lat, q.Lon), "F2") +
                            " pur_kn=" + AtwNum(c.Kn, "F0") + fields + " mount=" + key + " ammo=" + shot.File + " left=" + shot.Before + " salvo=" + shot.Shots + " t_hit=" + AtwNum(tHit, "F1") +
                            " trk_err_nm=" + AtwTrackErrTag(q) + " aim_err_nm=" + AtwAimErrTag(q, aimLat, aimLon));
                        EvLine("auto_evade", "{\"uid\":" + uid + ",\"name\":" + Json.Str(NameOf(u)) + ",\"phase\":\"atw_fire\",\"role\":\"evade\",\"frozen\":true" +
                            ",\"torpedo_track_id\":" + q.Id + ",\"tti_s\":" + (double.IsInfinity(aim.TtiS) ? "null" : Json.Num(Math.Round(aim.TtiS, 1))) +
                            ",\"ammo\":" + Json.Str(shot.File) + ",\"dist_nm\":" + Json.Num(Math.Round(aim.AimNm, 2)) + ",\"t_hit_s\":" + Json.Num(Math.Round(tHit, 1)) + "}");
                        fired = true;
                        break;
                    }
                    if (!fired)
                    {
                        if (shown == null) shown = AtwTurnMath.FrozenAim(AtwFrozenInOf(c, hdg, ownKn, lat0, lon0, cosLat, 0));
                        AtwSkip(uid, q.Id, "track_stale", " frozen=" + (why ?? "none") + AtwTurnMath.FrozenFields(shown, hdg), nowS);
                    }
                }
            }
            catch (Exception e) { AtwFailed("frozen", e); }
        }

        private AtwFrozenIn AtwFrozenInOf(AtwFrozenCand c, double hdg, double ownKn, double lat0, double lon0, double cosLat, double salvoKn)
        {
            return new AtwFrozenIn
            {
                PX = (c.PLon - lon0) * 60.0 * cosLat, PY = (c.PLat - lat0) * 60.0, PursuitKn = c.Kn,
                OwnHdgDeg = double.IsNaN(hdg) ? 0 : hdg, OwnKn = ownKn, ReactionS = EvasionAtwReactionS, SalvoKn = salvoKn
            };
        }

        // an own ship (truth) or a friendly / neutral track within the safety distance of the aim point
        private bool AtwFriendlyAt(object shooter, double lat, double lon)
        {
            if (_atwSnap == null) return false;
            foreach (var o in _atwSnap.Own)
                if (!ReferenceEquals(o.U, shooter) && WorldDistanceNm(o.Lat, o.Lon, lat, lon) <= EvasionAtwSafetyNm) return true;
            foreach (var a in _atwSnap.Tracks)
                if (WorldDistanceNm(a.Lat, a.Lon, lat, lon) <= EvasionAtwSafetyNm) return true;
            return false;
        }

        // log only: distance between the aim point and the true position of the torpedo
        private string AtwAimErrTag(AtwTorpRef q, double lat, double lon)
        {
            try
            {
                double la, lo, h;
                if (q.Obj == null || !OwnPos(q.Obj, out la, out lo, out h)) return "na";
                return Math.Round(WorldDistanceNm(lat, lon, la, lo), 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "na"; }
        }

        // ---- T-230 (ADR-0095): own order vs the game's task, the order followed to the launch ----

        private AtwOrder _atwLastOrder;

        private IEnumerable AtwMountsOf(object unit)
        {
            if (!Present(EvasionSpecs.ObjObp) || !Present(EvasionSpecs.ObpWeaponSystems)) return null;
            return Get(Get(unit, EvasionSpecs.ObjObp), EvasionSpecs.ObpWeaponSystems) as IEnumerable;
        }

        /// <summary>null | own_order | game_task | game_firing: who already works on this torpedo for this unit (queue and mounts).</summary>
        private string AtwEngagedBy(object unit, object torpObj)
        {
            try
            {
                if (unit == null || torpObj == null) return null;
                string res = null;
                var tasks = Get(unit, EvasionSpecs.ObjEngageTasks) as IEnumerable;
                if (tasks != null)
                    foreach (var t in tasks)
                        if (t != null && ReferenceEquals(Get(t, EvasionSpecs.TaskTargetObject), torpObj))
                        {
                            if (!_atwOwnTasks.Contains(t)) return "game_task";
                            res = "own_order";
                        }
                if (Present(EvasionSpecs.WsTakenTask))
                {
                    var mounts = AtwMountsOf(unit);
                    if (mounts != null)
                        foreach (var m in mounts)
                        {
                            if (m == null) continue;
                            var taken = Get(m, EvasionSpecs.WsTakenTask);
                            if (taken != null && ReferenceEquals(Get(taken, EvasionSpecs.TaskTargetObject), torpObj))
                            {
                                if (_atwOwnTasks.Contains(taken)) { res = "own_order"; continue; }
                                var ex = Get(m, EvasionSpecs.WsExecuting);
                                return ex is bool && (bool)ex ? "game_firing" : "game_task";
                            }
                            var ex2 = Get(m, EvasionSpecs.WsExecuting);
                            if (ex2 is bool && (bool)ex2 && taken == null && ReferenceEquals(Get(m, EvasionSpecs.WsTargetObject), torpObj)) return "game_firing";
                        }
                }
                return res;
            }
            catch (Exception e) { AtwFailed("engaged", e); return null; }
        }

        private void AtwNoteOrder(int uid, AtwAssign a, AtwShot shot, double nowS, string role)
        {
            _atwLastOrder = null;
            if (shot.Task == null) return;
            var o = new AtwOrder { Uid = uid, TrackId = a.TrackId, MountKey = a.MountKey, File = shot.File, OrderedS = nowS, THitS = a.THitS, ShipBefore = shot.Before };
            _atwOrders.Add(o); _atwOrderTask[o] = shot.Task; _atwOwnTasks.Add(shot.Task);
            _atwLastOrder = o;
            if (_atwTurn.NoteFire(uid, a.TrackId, nowS))
                Log("[Evasion] atw turn fire uid=" + uid + " torp=" + a.TrackId + " after_s=" + Math.Round(nowS - AtwTurnStartS(uid), 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private double AtwTurnStartS(int uid) { double s; return _atwTurnStarted.TryGetValue(uid, out s) ? s : double.NaN; }
        private readonly Dictionary<int, double> _atwTurnStarted = new Dictionary<int, double>();

        private object AtwUnitOf(int uid)
        {
            if (_atwSnap != null) foreach (var o in _atwSnap.Own) if (o.Uid == uid) return o.U;
            return null;
        }

        private object AtwMountByKey(object unit, int key)
        {
            var mounts = AtwMountsOf(unit);
            if (mounts != null) foreach (var m in mounts) if (m != null && RuntimeHelpers.GetHashCode(m) == key) return m;
            return null;
        }

        /// <summary>Reads what the game shows about the order this round; never throws.</summary>
        private AtwOrderObs AtwObserve(AtwOrder o, object unit, object task)
        {
            var ob = new AtwOrderObs();
            try
            {
                var tasks = Get(unit, EvasionSpecs.ObjEngageTasks) as IEnumerable;
                if (tasks != null) foreach (var t in tasks) if (ReferenceEquals(t, task)) { ob.InQueue = true; break; }
                if (Present(EvasionSpecs.WsTakenTask))
                {
                    object mount = null;
                    var mounts = AtwMountsOf(unit);
                    if (mounts != null)
                        foreach (var m in mounts)
                            if (m != null && ReferenceEquals(Get(m, EvasionSpecs.WsTakenTask), task)) { mount = m; ob.Taken = true; break; }
                    if (mount == null && o.WasTaken) mount = AtwMountByKey(unit, o.MountKey);   // the task left the mount: the same mount shows the spent rounds
                    if (mount != null)
                    {
                        ob.MountKey = RuntimeHelpers.GetHashCode(mount);
                        var st = Get(mount, EvasionSpecs.WsEngageState);
                        ob.EngageState = st == null ? null : st.ToString();
                        if (Present(EvasionSpecs.WsLoadedCount)) { try { ob.MountLoaded = Convert.ToInt32(Call(mount, EvasionSpecs.WsLoadedCount, o.File)); } catch (Exception) { } }
                        var rl = Get(mount, EvasionSpecs.WsReloading);
                        ob.MountReloading = rl is bool && (bool)rl;
                    }
                }
                else if (!ob.InQueue) o.WasTaken = true;   // no member to see the mount: a task that left the queue was taken
                ob.ShipLeft = AtwAmmoLeft(o.Uid, o.File);
            }
            catch (Exception e) { AtwFailed("observe", e); }
            return ob;
        }

        /// <summary>Every round: Step each order; log launched / fire_failed; cancel on timeout; free the mount and the window.</summary>
        private void AtwFollowOrders(double nowS)
        {
            for (int i = _atwOrders.Count - 1; i >= 0; i--)
            {
                var o = _atwOrders[i];
                try
                {
                    object task; _atwOrderTask.TryGetValue(o, out task);
                    object unit = AtwUnitOf(o.Uid);
                    string r;
                    if (unit == null || task == null) r = "failed:unit_gone";
                    else r = AtwOrderMath.Step(o, AtwObserve(o, unit, task), nowS);
                    if (r == null) continue;
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    double after = Math.Round(nowS - o.OrderedS, 1);
                    _atwOrders.RemoveAt(i);
                    if (r == "launched")
                    {
                        AtwLaunched++;
                        _atwMounts.Release(o.Uid, o.MountKey);
                        Log("[Evasion] atw launched uid=" + o.Uid + " torp=" + o.TrackId + " used=" + o.Used + " attrib=" + o.Attrib + " after_s=" + after.ToString(inv) + " mount=" + o.MountKey);
                    }
                    else
                    {
                        o.Phase = "failed";
                        AtwFireFailed++;
                        bool cancelled = unit != null && task != null && r.StartsWith("failed:timeout") && AtwCancel(o, unit, task);
                        _atwWindows.Forget(o.TrackId);
                        _atwMounts.Release(o.Uid, o.MountKey);
                        Log("[Evasion] atw fire_failed uid=" + o.Uid + " torp=" + o.TrackId + " reason=" + r.Substring(7) + " state=" + (o.LastState ?? "na") + " after_s=" + after.ToString(inv) +
                            " cancelled=" + (cancelled ? "1" : "0"));
                    }
                    if (task != null) _atwOwnTasks.Remove(task);
                    _atwOrderTask.Remove(o);
                    string back = _atwTurn.Result(o.Uid, r == "launched", nowS);
                    if (back != null) Log("[Evasion] atw turn back uid=" + o.Uid + " torp=" + o.TrackId + " reason=" + back);
                }
                catch (Exception e) { AtwFailed("follow", e); _atwOrders.RemoveAt(i); }
            }
        }

        /// <summary>Removes the order from the queue or ends it on the mount that took it. true = something was cancelled.</summary>
        private bool AtwCancel(AtwOrder o, object unit, object task)
        {
            try
            {
                if (o.WasTaken && Present(EvasionSpecs.WsEndEngageTask))
                {
                    var m = AtwMountByKey(unit, o.MountKey);
                    if (m != null && ReferenceEquals(Get(m, EvasionSpecs.WsTakenTask), task)) { Call(m, EvasionSpecs.WsEndEngageTask, true); return true; }
                }
                var tasks = Get(unit, EvasionSpecs.ObjEngageTasks) as IEnumerable;
                bool inQueue = false;
                if (tasks != null) foreach (var t in tasks) if (ReferenceEquals(t, task)) { inQueue = true; break; }
                if (inQueue && Present(EvasionSpecs.ObjRemoveEngageTask)) { Call(unit, EvasionSpecs.ObjRemoveEngageTask, task); return true; }
            }
            catch (Exception e) { AtwFailed("cancel", e); }
            return false;
        }

        // ---- T-230 (ADR-0095): turn onto the salvo ----

        private bool AtwOpenOrder(int uid)
        {
            foreach (var o in _atwOrders) if (o.Uid == uid) return true;
            return false;
        }

        /// <summary>Heading that replaces the evasion course while the ship turns onto the salvo; null = none. Also ends a turn that waits too long. Never throws.</summary>
        internal double? AtwTurnOverride(object u)
        {
            try
            {
                if (!EvasionAtwTurnOn || _atwTurn.Count == 0) return null;
                int uid = UidOf(u);
                double? now = Frame.MissionTimeS;
                if (now.HasValue)
                {
                    double hdgNow, courseNaN; AtwBackCourse(u, out hdgNow, out courseNaN);
                    double left = _atwTurn.LeftDeg(uid, hdgNow); bool arrived = _atwTurn.Arrived(uid);
                    string why = _atwTurn.Poll(uid, now.Value, true, true, hdgNow);
                    if (why != null) Log("[Evasion] atw turn back uid=" + uid + " reason=" + why + (why == "timeout" ? AtwTurnBackFields(uid, hdgNow, left, arrived) : ""));
                }
                return _atwTurn.Override(uid);
            }
            catch (Exception) { return null; }
        }

        /// <summary>The evasion of the ship ended, was released or the mission was left: the turn override goes with it.</summary>
        internal void AtwTurnEnd(int uid, string why)
        {
            try
            {
                if (_atwTurn.PhaseOf(uid) != "idle") Log("[Evasion] atw turn back uid=" + uid + " reason=" + why);
                _atwTurn.End(uid, why);
            }
            catch (Exception) { }
        }

        // T-261: fields of a turn back that ended without a salvo; the state is already idle then, so the values come from the call before it
        private string AtwTurnBackFields(int uid, double hdgNow, double left = double.NaN, bool arrived = false)
        {
            return " left_deg=" + (double.IsNaN(left) ? "na" : Math.Round(left).ToString(System.Globalization.CultureInfo.InvariantCulture)) + " arrived=" + (arrived ? "1" : "0");
        }

        private void AtwTurnPoll(object u, int uid, double nowS)
        {
            int trk = _atwTurn.TrackOf(uid);
            if (trk < 0) return;
            bool alive = false, fresh = true;
            if (_atwSnap != null) foreach (var q in _atwSnap.Torps) if (q.Id == trk) { alive = true; fresh = q.Fresh || q.AgeS <= AntiTorpedoMath.AtwStaleFireMaxS; break; }   // T-261: the Gate rule (Q1)
            double hdgNow = double.NaN, courseNaN = double.NaN;
            AtwBackCourse(u, out hdgNow, out courseNaN);
            double left = _atwTurn.LeftDeg(uid, hdgNow); bool arrived = _atwTurn.Arrived(uid);
            string why = _atwTurn.Poll(uid, nowS, alive, fresh, hdgNow);
            if (why != null) Log("[Evasion] atw turn back uid=" + uid + " torp=" + trk + " reason=" + why + (why == "timeout" || why == "frozen" ? AtwTurnBackFields(uid, hdgNow, left, arrived) : ""));
        }

        /// <summary>The arcs (game Vector2, degrees) of the mounts that could fire this ammo at the torpedo and do not reload.</summary>
        private List<AtwArc> AtwTurnArcs(object u, object torpObj, string file)
        {
            object pos = null;
            try { if (Present(EvasionSpecs.ObjUnityPosition)) pos = Call(torpObj, EvasionSpecs.ObjUnityPosition); } catch (Exception) { }
            return AtwTurnArcsAt(u, pos, torpObj, file);
        }

        /// <summary>T-287: the same for a position (boxed Vector3); torpObj = null for a salvo at a point.</summary>
        private List<AtwArc> AtwTurnArcsAt(object u, object pos, object torpObj, string file)
        {
            var res = new List<AtwArc>();
            try
            {
                if (pos == null || !Present(EvasionSpecs.WeaponSystemsForAmmo) || !Present(EvasionSpecs.ObjUnityPosition) || !Present(EvasionSpecs.WsVwp) || !Present(EvasionSpecs.WpFiringArcs)) return res;
                var mounts = CallStatic(EvasionSpecs.WeaponSystemsForAmmo, u, pos, file, torpObj) as IEnumerable;
                if (mounts == null) return res;
                foreach (var m in mounts)
                {
                    if (m == null) continue;
                    var rl = Present(EvasionSpecs.WsReloading) ? Get(m, EvasionSpecs.WsReloading) : null;
                    if (rl is bool && (bool)rl) continue;
                    var arcs = Get(Get(m, EvasionSpecs.WsVwp), EvasionSpecs.WpFiringArcs) as IEnumerable;
                    if (arcs == null) continue;
                    foreach (var v in arcs)
                    {
                        double? x = D(Get(v, EvasionSpecs.ArcX)), y = D(Get(v, EvasionSpecs.ArcY));
                        if (x.HasValue && y.HasValue && !(x.Value == 0 && y.Value == 0)) res.Add(new AtwArc(x.Value, y.Value));
                    }
                }
            }
            catch (Exception e) { res.Clear(); AtwFailed("arcs", e); }
            return res;
        }

        /// <summary>
        /// A torpedo in range but outside every arc (reason no_arc): plan a turn onto the salvo. Returns " why=..." for the skip line:
        /// turn (started) | turn_back | early | late | arcs | turning | wait_result | cooldown | the gate that holds the salvo. Never throws.
        /// </summary>
        private string AtwTurnTry(object u, int uid, AtwTorpRef q, AtwTrackIn t, double nowS, double lat0, double lon0, out string fields)
        {
            fields = "";
            try
            {
                if (!EvasionAtwTurnOn || !Present(EvasionSpecs.WsVwp)) return "";
                string ph = _atwTurn.PhaseOf(uid);
                if (ph != "idle") return " why=turning";
                if (AtwOpenOrder(uid)) return " why=wait_result";
                if (!_atwTurn.CanStart(uid, nowS)) return " why=cooldown";
                AtwOption opt = null;
                foreach (var o in t.Options) if (o.Count > 0 && o.ArcBlocked && t.DistNm <= o.MaxNm && t.DistNm >= o.MinNm) { opt = o; break; }
                if (opt == null) return "";
                double hdg = D(RxGet(u, EvasionSpecs.ObjHeading)) ?? 0;
                double brg = WorldBearing(lat0, lon0, q.Lat, q.Lon);
                bool slowed = false; EvRun run;
                if (_evRuns.TryGetValue(u, out run)) slowed = run.Slowed;
                double? trkKn = _atwSpeed.Kn(q.Id);
                double peak = _atwYaw.PeakDegS(uid);
                var inp = new AtwTurnIn
                {
                    HdgDeg = hdg, BrgDeg = brg, DistNm = t.DistNm, ClosingKn = _atwClosing.ClosingKn(uid, q.Id) ?? double.NaN, TtiS = t.TtiS,
                    TurnRateDegS = AtwTurnMath.RateFor(peak, slowed), MinNm = opt.MinNm, SpeedKn = opt.SpeedKn,
                    ReactionS = EvasionAtwReactionS, FireNm = EvasionAtwFireNm
                };
                if (trkKn.HasValue && trkKn.Value > AntiTorpedoMath.TorpedoAssumedKn) inp.TorpKn = trkKn.Value;
                inp.ShipKn = AtwTurnMath.ShipKnFromClosing(inp.ClosingKn, inp.TorpKn);
                string obs = " rate_obs=" + (double.IsNaN(peak) ? "na" : Math.Round(peak, 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
                fields = AtwTurnMath.Fields(inp, null) + obs;
                if (AtwTurnMath.Receding(t.TtiS, inp.ClosingKn, inp.TorpKn)) return " why=receding";   // T-265: no turn onto a salvo at a receding torpedo
                // T-253: the turn-back block holds a new turn too (run 0.30.14: turn start in the same round as turn back)
                double hdgBack, courseBack; AtwBackCourse(u, out hdgBack, out courseBack);
                if (_atwTurn.TurnHeld(uid, nowS, t.TtiS, hdgBack, courseBack)) return " why=turn_back";
                if (AtwTurnMath.PreEarly(t.DistNm, EvasionAtwFireNm)) return " why=early";   // cannot start yet whatever the ship does
                double tHit = AntiTorpedoMath.ExpectedHitS(nowS, EvasionAtwReactionS, t.DistNm, opt.SpeedKn);
                string gate = AtwTurnMath.Gate(t, _atwWindows, EvasionAtwSafetyNm, tHit);
                if (gate != null) return " why=" + gate;
                var arcs = AtwTurnArcs(u, q.Obj, opt.File);
                if (arcs.Count == 0) return " why=arcs";
                inp.Arcs = arcs;
                var plan = AtwTurnMath.Plan(inp);
                fields = AtwTurnMath.Fields(inp, plan) + obs;
                if (!plan.Ok) return " why=" + plan.Why;
                _atwTurn.Start(uid, q.Id, plan, nowS);
                _atwTurnStarted[uid] = nowS;
                AtwTurns++;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                Log("[Evasion] atw turn start uid=" + uid + " torp=" + q.Id + " brg=" + Math.Round(brg) + " hdg=" + Math.Round(hdg) + " to=" + Math.Round(plan.HeadingDeg) + " turn=" + Math.Round(plan.TurnDeg) +
                    " eta_s=" + Math.Round(plan.TurnS, 1).ToString(inv) + " dist=" + Math.Round(t.DistNm, 2).ToString(inv) + " fire_nm=" + Math.Round(plan.FireDistNm, 2).ToString(inv) +
                    " arc=" + Math.Round(plan.ArcFrom) + ".." + Math.Round(plan.ArcTo) + " rate=" + Math.Round(inp.TurnRateDegS, 1).ToString(inv) + " slow=" + (slowed ? "1" : "0") + " tti=" + (double.IsInfinity(t.TtiS) ? "inf" : Math.Round(t.TtiS).ToString(inv)) + obs);
                return " why=turn";
            }
            catch (Exception e) { AtwFailed("turn", e); return ""; }
        }

        /// <summary>The frozen-track branch of the evasion sends no course of its own: send the turn heading, or the evasion course back after the turn.</summary>
        private void AtwTurnSendFrozen(object u, EvRun run, double lat0, double lon0)
        {
            try
            {
                double? ov = AtwTurnOverride(u);
                if (run.LastCourseSent <= -900) return;
                double target = ov ?? run.S.CourseDeg ?? run.LastCourseSent;
                if (Math.Abs(EvasionMath.Diff(run.LastCourseSent, target)) < 3) return;
                double lat, lon;
                WorldDestination(lat0, lon0, target, EvasionLegNm, out lat, out lon);
                string wp = PlaceWaypoint("0", u, lat, lon, false, "");
                if (wp.Contains("\"ok\":true")) { run.LastCourseSent = target; _ctlMem.NoteRoutePlaced(u, lat, lon); }
            }
            catch (Exception e) { AtwFailed("turn_send", e); }
        }

        // self-check of the anti-torpedo salvo: EvasionSpecs (evasion_atw needs evasion_own and attack)

        internal string AtwDiagJson()
        {
            return "{\"on\":" + Json.Bool(EvasionAtwOn && Capabilities.Contains("evasion_atw")) + ",\"fired\":" + AtwFired + ",\"skipped\":" + AtwSkipped +
                   ",\"gone\":" + AtwGone + ",\"windows\":" + _atwWindows.Count + ",\"pending\":" + _atwPending.Count +
                   ",\"tracks\":" + (_atwSnap == null ? 0 : _atwSnap.Torps.Count) + ",\"busy\":" + _atwMounts.Count + ",\"cover_fired\":" + AtwCoverFired +
                   ",\"launched\":" + AtwLaunched + ",\"fire_failed\":" + AtwFireFailed + ",\"orders\":" + _atwOrders.Count + ",\"turns\":" + AtwTurns + ",\"turning\":" + _atwTurn.Count + "}";
        }
    }
}
