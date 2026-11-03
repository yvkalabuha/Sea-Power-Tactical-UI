using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;


namespace VoiceBotBridge
{
    // T-121: part of the ASW controller (see AswController.cs).

    internal sealed partial class AswController
    {
        // ---- save and load -----------------------------------------------------------------------------------------

        private bool AnyActive()
        {
            foreach (var g in _groups) if (g.Order != "none") return true;
            if (_sarAir.ShipsActive()) return true;   // ships on a rescue run are saved too
            return false;
        }

        /// <summary>T-130: a mark without groups clears its slot only after this many seconds of game time. Loading a save sets last_save while the
        /// old mission may still run; a mission change inside the grace drops the pending delete (the slot is the one being loaded).</summary>
        internal const double SlotDeleteGraceS = 3.0;
        private string _pendingDelete;
        private double _pendingDeleteAt;

        /// <summary>T-198 (ADR-0086): true while the Harmony hook on the game save works; then the file is written by <see cref="OnGameSaved"/> and the mark observer only follows the mark.</summary>
        public bool SaveHookActive;

        /// <summary>T-198: the game saved <paramref name="savPath"/> right now (hook on SaveLoadManager.SaveGame, main thread). Writes at once (no tick needed: the pause menu stops it)
        /// or drops the slot when there is nothing to keep.</summary>
        public void OnGameSaved(string savPath, SavStamp stamp)
        {
            if (string.IsNullOrEmpty(savPath)) return;
            _lastSave = savPath; _lastSaveSeen = true; _pendingDelete = null;
            if (AnyActive() || _deferred != null) Save(savPath, stamp);
            else
            {
                try { if (Store != null && Store.Delete(savPath)) _game.Log("asw save slot cleared " + SaveSlotStore.Key(savPath)); }
                catch (Exception e) { _game.Log("asw save failed: " + e.Message); }
            }
        }

        private void CheckSave()
        {
            string sp = _game.LastSavePath;
            if (!_lastSaveSeen) { _lastSaveSeen = true; _lastSave = sp; return; }
            if (SaveHookActive) { _lastSave = sp; _pendingDelete = null; return; }
            if (sp != _lastSave)
            {
                _lastSave = sp;
                if (sp == null) { _pendingDelete = null; return; }
                if (AnyActive() || _deferred != null) { _pendingDelete = null; Save(sp); }
                else { _pendingDelete = sp; _pendingDeleteAt = _now + SlotDeleteGraceS; }   // T-122 / R8: nothing to keep for this slot
                return;
            }
            if (_pendingDelete != null && _now >= _pendingDeleteAt)
            {
                string p = _pendingDelete; _pendingDelete = null;
                if (AnyActive() || _deferred != null) Save(p);
                else if (Store != null && Store.Delete(p)) _game.Log("asw save slot cleared " + SaveSlotStore.Key(p));
            }
        }

        private SaveSlotStore _store;
        private string _storeDir;

        /// <summary>The slot store; created lazily because SaveDir can arrive later. Null while there is no directory.</summary>
        private SaveSlotStore Store
        {
            get
            {
                string dir = _game.SaveDir;
                if (string.IsNullOrEmpty(dir)) return null;
                if (_store == null || _storeDir != dir) { _store = new SaveSlotStore(dir, _game.Log); _storeDir = dir; }
                return _store;
            }
        }

        private AswSaveData Snapshot(string sav, SavStamp stamp = null)
        {
            if (_deferred != null && _groups.Count == 0)
            {
                // T-211: a save while the groups wait for the world after a load keeps the file; its times move to the running clock
                ShiftTimes(_deferred, _now); _deferred.SavePath = sav; _deferred.Sav = stamp;
                return _deferred;
            }
            var d = new AswSaveData { MissionTimeS = _now, SavePath = sav, Sav = stamp };
            foreach (var g in _groups)
            {
                var dto = new AswGroupDto { Name = g.Name, Order = g.Order, TargetName = g.TargetName, TargetUid = g.TargetUid, TargetKind = g.TargetKind, Loadouts = new Dictionary<string, string>(g.Loadouts), Seq = Ownership != null ? Seq(g) : 0, AutoName = g.AutoName, Depth = g.Depth,
                    Tactics = new List<string>(g.Tactics), Rect = g.Rect, Params = g.P };
                if (g.Order == "course") { dto.Course = g.Plan.Course; if (g.Plan.Band.Set) dto.Barrier = BandDto(g.Plan.Band);  if (g.FrameSet) dto.FrameAxisDeg = SavedFrameAxis(g); }   // T-238 / T-245: the axis of the band (not the evasion course) survives a load
                if (g.Order == "area") dto.AreaPattern = g.Plan.AreaPattern;
                if (g.Order == "strike") { dto.RefUid = g.St.RefUid; dto.RadiusNm = g.St.RadiusNm; dto.Targets = g.St.Targets; dto.Airborne = g.St.Airborne; dto.Skip = new List<int>(g.St.Skip); }
                foreach (int u in g.Members) dto.Members.Add(g.Refs[u]);
                foreach (var w in _restoreWait) if (w.G == g) dto.Members.Add(w.R);   // T-211: members not found yet stay in the file
                d.Groups.Add(dto);
                foreach (var b in g.Buoys) d.Buoys.Add(new AswBuoyDto { Lat = b.Lat, Lon = b.Lon, DroppedS = b.DroppedS, LifeS = b.LifeS, Ammo = b.Ammo, Group = g.Name });
                foreach (var c in g.Contacts) d.Contacts.Add(new AswContactDto { Id = c.Id, Name = c.Name, Group = g.Name, Lat = c.Lat, Lon = c.Lon, SeenS = c.LastSeenS });
            }
            var written = new HashSet<int>();   // T-252: one record per uid, the key wins over a stale Uid field
            foreach (var kv in _mem)
            {
                var m = kv.Value; m.Uid = kv.Key;
                if (!written.Add(m.Uid)) continue;
                m.Radar = _radar != null ? _radar.Export(m.Uid) : null; d.Memory.Add(m);
            }
            if (_radar != null)   // T-241: radar memory of an aircraft that has no ROE / depth memory
                foreach (var g in _groups)
                    foreach (int u in g.Members)
                    {
                        var rd = _radar.Export(u); AswMemberRef rf;
                        if (rd != null && !written.Contains(u) && g.Refs.TryGetValue(u, out rf)) d.Memory.Add(new AswMemDto { Uid = u, Name = rf.Name, Type = rf.Type, Radar = rd });
                    }
            if (Sar != null)
            {
                d.Sar = Sar.Save();
                d.Sar.Groups.Clear();
                foreach (var g in _groups) if (g.Order == "sar") d.Sar.Groups.Add(SarAirOrder.OrderDto(g));
            }
            return d;
        }

        private void Save(string sav, SavStamp stamp = null)
        {
            try
            {
                var st = Store;
                if (st == null) return;
                if (st.Write(sav, AswSave.ToJson(Snapshot(sav, stamp)), jtxt => { string perr; return AswSave.Parse(jtxt, out perr) == null ? (perr ?? "bad file") : null; })) _game.Log("asw saved " + st.PathFor(sav));
            }
            catch (Exception e) { _game.Log("asw save failed: " + e.Message); }
        }

        /// <summary>Everything that belongs to the objects of a mission; no game calls.</summary>
        private void ForgetMission()
        {
            _groups.Clear(); _adopts.Clear(); _relief.Reset(); _mem.Clear(); _warnAt.Clear(); _warnings.Clear();
            if (_radar != null) _radar.ReleaseAll("asw");   // T-241: the memory goes, the radars stay as they are
            _targets.Clear(); _alerts.Clear(); _claims.Clear(); _killedEmitted.Clear(); _killedIds.Clear(); _hitAt.Clear(); _liveSince.Clear(); _badUntil.Clear(); _waitAt.Clear(); _gotoFail.Clear(); _gameRtbNoted.Clear(); _subsStamp = -1;
            _prevLive.Clear(); _lastKnown.Clear(); _gone.Clear(); _prepSeen.Clear();   // T-213
            _pendingRestore = null; _deferred = null; _restoreWait.Clear(); _memTaken.Clear(); _memFileUids.Clear(); _liveTaken.Clear(); _lastSaveSeen = false; _pendingDelete = null;
            _noDipUids.Clear(); _noDipTypes.Clear();   // T-188
            _skipLog.Clear();   // T-195: the "asw skip" throttle of the old mission must not hide the first line of the next one
            Book.Forget("asw");   // T-181/T-186
            _sarAir.Reset();
        }

        /// <summary>T-130: the mission was left (menu or another mission): drop groups, snapshots and memory without touching the game.</summary>
        public void OnMissionLeft()
        {
            ForgetMission();
            _live = new Dictionary<int, AswMember>(); _airBefore = new HashSet<int>(); _hangarBefore.Clear(); _liveStale = true;
            _preview.Clear(); _previewUntil = 0;
            RebuildView();
        }

        /// <summary>A mission was entered: <paramref name="savPathOrNull"/> is the .sav it came from (null = new mission).</summary>
        public void OnMissionChanged(string savPathOrNull, double missionTimeS) { OnMissionChanged(savPathOrNull, missionTimeS, null); }

        /// <summary>T-198 (ADR-0086): <paramref name="loaded"/> = header stamp of the loaded .sav; null = unknown (the T-122 rule: file time within 5 s of the mission).</summary>
        public void OnMissionChanged(string savPathOrNull, double missionTimeS, SavStamp loaded)
        {
            ForgetMission();
            _now = missionTimeS;
            RefreshLive();
            if (savPathOrNull != null && _cfg.RestoreOnLoad != "off")
            {
                try
                {
                    var st = Store;
                    if (st != null)
                    {
                        AswSaveData d = null; string err = null;
                        string src;
                        string text = st.Read(savPathOrNull, t => { d = AswSave.Parse(t, out err); return d == null ? (err ?? "bad file") : null; }, out src);
                        string why = text == null ? null : SaveFreshness.Check(d.Sav, d.MissionTimeS, loaded, missionTimeS);
                        if (text == null) { if (err != null) _game.Log("asw restore: " + err); }
                        else if (why != null) _game.Log("asw restore: file is stale (" + why + ")");
                        else
                        {
                            if (DeferRestore) { _deferred = d; _game.Log("asw restore deferred until the world is placed groups=" + I(d.Groups.Count)); }   // T-211: the shift is made at the apply
                            else
                            {
                                ShiftTimes(d, missionTimeS);
                                if (_cfg.RestoreOnLoad == "ask") _pendingRestore = d; else ApplyRestore(d);
                            }
                        }
                    }
                }
                catch (Exception e) { _game.Log("asw restore failed: " + e.Message); }
            }
            RebuildView();
        }

        /// <summary>T-198: the game does not restore the mission clock, so every time kept in the file moves to the clock of the running mission (buoy life, contact age).</summary>
        private static void ShiftTimes(AswSaveData d, double nowS)
        {
            double dt = SaveFreshness.Shift(d.MissionTimeS, nowS);
            foreach (var b in d.Buoys) b.DroppedS += dt;
            foreach (var c in d.Contacts) c.SeenS += dt;
            foreach (var gd in d.Groups) if (gd.Barrier != null) { gd.Barrier.T0 += dt; foreach (var r in gd.Barrier.Rows) r.LaidS += dt; }   // T-229
            d.MissionTimeS = nowS;
        }

        /// <summary>T-229: the band of the sliding barrier as it goes to the file (Taken is not saved: after a load every aircraft asks again).</summary>
        /// <summary>T-245: the barrier saves the axis of its band (held / route), the frame axis of a ship on an evasion course is not the line the rows lie on.</summary>
        private static double SavedFrameAxis(Grp g)
        {
            if (g.Tactics.Contains("barrier") && g.Plan.Band.Set) return g.BandAxisSet ? g.BandAxis : g.Plan.Band.AxisDeg;
            return g.PlanAxis;
        }

        private static AswBarrierBandDto BandDto(BarrierBand b)
        {
            var d = new AswBarrierBandDto { Lat = b.O.Lat, Lon = b.O.Lon, AxisDeg = b.AxisDeg, T0 = b.T0, NextSeq = b.NextSeq };
            foreach (var r in b.Rows)
            {
                var rd = new AswBarrierRowDto { Seq = r.Seq, Lat = r.Center.Lat, Lon = r.Center.Lon, AxisDeg = r.AxisDeg, HalfNm = r.HalfNm, XNm = r.XNm, LaidS = r.LaidS, Buoys = r.Buoys, Passed = r.Passed };
                foreach (var p in r.Open) rd.Open.Add(new[] { p.Lat, p.Lon });
                d.Rows.Add(rd);
            }
            return d;
        }

        private static BarrierBand BandFrom(AswBarrierBandDto d)
        {
            var b = new BarrierBand { Set = true, O = new GeoPt(d.Lat, d.Lon), AxisDeg = d.AxisDeg, T0 = d.T0, NextSeq = d.NextSeq };
            foreach (var rd in d.Rows)
            {
                var r = new BarrierRec { Seq = rd.Seq, Center = new GeoPt(rd.Lat, rd.Lon), AxisDeg = rd.AxisDeg, HalfNm = rd.HalfNm, XNm = rd.XNm, LaidS = rd.LaidS, Buoys = rd.Buoys, Passed = rd.Passed };
                foreach (var p in rd.Open) r.Open.Add(new GeoPt(p[0], p[1]));
                b.Rows.Add(r);
            }
            return b;
        }

        private void ApplyRestore(AswSaveData d)
        {
            RefreshLive();
            _restoreWait.Clear(); _memTaken.Clear(); _memFileUids.Clear(); _liveTaken.Clear();
            foreach (var fg in d.Groups) if (fg.Members != null) foreach (var fm in fg.Members) if (fm.Uid > 0) _memFileUids.Add(fm.Uid);
            var liveRefs = new List<AswMemberRef>();
            var liveUid = new List<int>();
            foreach (var m in _live.Values) { liveRefs.Add(new AswMemberRef { Uid = m.Uid, Name = m.Name, Type = m.Type, Label = m.TypeLabel }); liveUid.Add(m.Uid); }
            int missing = 0, stacks = 0;
            foreach (var dto in d.Groups)
            {
                var g = new Grp { Name = dto.Name, Order = dto.Order, TargetName = dto.TargetName, TargetUid = dto.TargetUid, TargetKind = dto.TargetKind ?? "ship", Depth = dto.Depth ?? "alternate",
                    Tactics = new List<string>(dto.Tactics), Rect = dto.Rect, P = dto.Params ?? Clone(_defaults),
                    AutoName = dto.AutoName ?? AswGroupNames.IsAuto(dto.Name, KnownPrefixes()) };
                if (dto.Order == "strike") { g.St = new StrikeState { RefUid = dto.RefUid, RadiusNm = dto.RadiusNm, Targets = dto.Targets ?? "hostile_unknown", Airborne = dto.Airborne }; foreach (int k in dto.Skip) g.St.Skip.Add(k); }
                _groups.Add(g);
                if (Ownership != null) { Ownership.RestoreSeq("asw", g.Name, dto.Seq); Seq(g); }   // old file (seq 0): numbers in the order of loading
                if (dto.Order == "sar")
                    foreach (var sg in d.Sar.Groups) if (sg.Group == dto.Name) { g.SarArea = SarAirOrder.AreaOf(sg); g.SarOnComplete = sg.OnComplete == "loiter" ? "loiter" : "rtb"; }
                { int n = GroupNo(dto.Name); if (n > _groupNo) _groupNo = n; }
                foreach (var r in dto.Members)
                {
                    // T-181 (ADR-0080 s.7): an old stack uid takes the free machines of its pair; a slot uid that is taken (or moved) falls back to a free slot of the pair
                    var stack = r.Deck == 0 ? StackSlots(r.Uid) : new List<AswMember>();
                    if (stack.Count > 0 && stack[0].Type != r.Type && r.Type != null && stack[0].TypeLabel != r.Type) stack.Clear();
                    if (stack.Count > 0)
                    {
                        if (AswSave.StackRestore != "all") stack.RemoveRange(1, stack.Count - 1);
                        foreach (var sm in stack) { RestoreSlot(g, sm); }
                        stacks++;
                        _game.Log("asw restore hangar stack " + g.Name + " type=" + stack[0].Type + " deck=" + I(stack[0].DeckUid) + " -> " + I(stack.Count) + " machines");
                        continue;
                    }
                    AswMember m = null;
                    AswMember direct;
                    if (_live.TryGetValue(r.Uid, out direct) && IsSlot(direct) && GroupOf(direct.Uid) == null) m = direct;
                    else if (r.Deck != 0 && !(_live.TryGetValue(r.Uid, out direct) && !IsSlot(direct))) m = FreeSlot(r.Deck, r.Type, null);
                    if (m == null)
                    {
                        int i = AswSave.Match(r, liveRefs, _liveTaken, _memFileUids);
                        if (i < 0) { _restoreWait.Add(new RestoreWait { G = g, R = r, Until = _now + RestoreGraceS }); continue; }   // T-211: the lists may still be filling
                        m = _live[liveUid[i]];
                    }
                    if (!JoinRestored(g, m, d, r)) missing++;
                }
                RestorePattern(g, dto);
                foreach (var b in d.Buoys) if (b.Group == g.Name) g.Buoys.Add(new BuoyRec { Lat = b.Lat, Lon = b.Lon, DroppedS = b.DroppedS, LifeS = b.LifeS, Ammo = b.Ammo });
                if (dto.Loadouts != null) foreach (var kv in dto.Loadouts) g.Loadouts[kv.Key] = kv.Value;
                g.Dirty = true;
            }
            Book.Rebind(_now, null, true);   // T-181/T-186: the slots follow the machines left in the hangars right after the load
            bool anyStrike = false; foreach (var sg in _groups) if (sg.Order == "strike") anyStrike = true;
            if (anyStrike) { _tickNo++; _subsStamp = -1; SummaryAlert(); }   // one summary card for the tracks that are live already (no dedupe state is saved)
            if (Sar != null) _sarAir.LoadNow(d.Sar);   // T-130: SAR was cleared before the ASW file is read (subscriber order), no pause needed
            _restoreData = d; _restoreMissing = missing; _restoreStacks = stacks;
            if (_restoreWait.Count == 0) FinishRestore();
        }

        // ---- T-211 (ADR-0088): the restore after a load waits for the world ----

        private const double RestoreGraceS = 20;   // a member not found in the game lists yet is searched this long (game time) after the apply

        private sealed class RestoreWait { public Grp G; public AswMemberRef R; public double Until; }
        private readonly List<RestoreWait> _restoreWait = new List<RestoreWait>();
        private AswSaveData _restoreData;
        private int _restoreMissing, _restoreStacks;

        /// <summary>Set by the game layer while the post-load gate is on: OnMissionChanged keeps the checked file until <see cref="ApplyDeferred"/>.</summary>
        public bool DeferRestore { get; set; }

        /// <summary>The checked file waits for the world after a load.</summary>
        public bool RestoreWaiting { get { return _deferred != null; } }

        /// <summary>T-217 (ADR-0089): uids of the waiting file members the post-load gate expects in the game lists (Uid &gt; 0, Deck == 0, not a task uid, no repeats).</summary>
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

        /// <summary>The post-load gate opened: the file is applied now (ask mode: the prompt goes up now). The times of the file move to <paramref name="now"/>.</summary>
        public void ApplyDeferred(double now)
        {
            var d = _deferred; _deferred = null;
            if (d == null) return;
            try
            {
                _now = now;
                RefreshLive();
                ShiftTimes(d, now);
                if (_cfg.RestoreOnLoad == "ask") _pendingRestore = d; else ApplyRestore(d);
                _game.Log("asw restore applied after load");
            }
            catch (Exception e) { _game.Log("asw restore failed: " + e.Message); }
            RebuildView();
        }

        /// <summary>A member joins a restored group; false = a hangar slot is not available (the member is lost).</summary>
        private readonly HashSet<int> _memTaken = new HashSet<int>();      // T-252: indices into _restoreData.Memory already given out
        private readonly HashSet<int> _liveTaken = new HashSet<int>();      // T-255: live uids already given to a member of _restoreData
        private readonly HashSet<int> _memFileUids = new HashSet<int>();   // T-252: uids of all members of the file

        private bool JoinRestored(Grp g, AswMember m, AswSaveData d, AswMemberRef saved)
        {
            if (IsSlot(m) && GroupOf(m.Uid) != null) { m = FreeSlot(m.DeckUid, m.Type, null); if (m == null) return false; }
            if (IsSlot(m)) { RestoreSlot(g, m); return true; }
            g.Members.Add(m.Uid); _liveTaken.Add(m.Uid); g.Refs[m.Uid] = new AswMemberRef { Uid = m.Uid, Name = m.Name, Type = m.Type, Label = m.TypeLabel };
            int mi = AswSave.MatchMemory(saved, d.Memory, _memTaken, _memFileUids);   // T-252: by the saved uid, each record once
            if (mi >= 0)
            {
                _memTaken.Add(mi);
                var src = d.Memory[mi];
                var mem = new AswMemDto { Uid = m.Uid, Name = src.Name, Type = src.Type, Roe = src.Roe, Depth = src.Depth, Radar = src.Radar };
                _mem[m.Uid] = mem;
                if (mem.Radar != null && _radar != null) _radar.Import(m.Uid, "asw", mem.Radar);   // T-241
                if (src.Uid != m.Uid) _game.Log("asw restore memory uid=" + I(m.Uid) + " from=" + I(src.Uid) + " by=" + (src.Uid == saved.Uid ? "uid" : "name"));
            }
            g.Prepared.Add(m.Uid);   // ROE and depth were already set before the save
            g.Dirty = true;
            return true;
        }

        /// <summary>Each tick after the refresh: the members that were not found at the apply are matched again; the ones still missing after RestoreGraceS are counted lost.</summary>
        private void RetryRestoreWait()
        {
            if (_restoreWait.Count == 0) return;
            var liveRefs = new List<AswMemberRef>();
            var liveUid = new List<int>();
            foreach (var lm in _live.Values) { liveRefs.Add(new AswMemberRef { Uid = lm.Uid, Name = lm.Name, Type = lm.Type, Label = lm.TypeLabel }); liveUid.Add(lm.Uid); }
            foreach (var w in new List<RestoreWait>(_restoreWait))
            {
                if (!_groups.Contains(w.G)) { _restoreWait.Remove(w); continue; }   // the group is gone meanwhile
                int i = AswSave.Match(w.R, liveRefs, _liveTaken, _memFileUids);
                if (i >= 0)
                {
                    var m = _live[liveUid[i]];
                    if (!IsSlot(m) && GroupOf(m.Uid) != null) continue;   // an aircraft of another group is not taken; the wait runs out
                    _restoreWait.Remove(w);
                    if (!JoinRestored(w.G, m, _restoreData, w.R)) _restoreMissing++;
                }
                else if (_now >= w.Until) { _restoreWait.Remove(w); _restoreMissing++; }
            }
            if (_restoreWait.Count == 0) FinishRestore();
        }

        private void FinishRestore()
        {
            _game.Event("asw_warn", Obj("code", "restored", "groups", _groups.Count, "missing", _restoreMissing, "stacks", _restoreStacks));
            _warnings.Add(new AswWarn { Text = "restored: " + I(_groups.Count) + " groups" + (_restoreMissing > 0 ? ", " + I(_restoreMissing) + " aircraft not found" : "") });
        }

        /// <summary>A hangar slot machine joins a restored group (no SAR / strike filter: it was a member when the file was written).</summary>
        private void RestoreSlot(Grp g, AswMember m)
        {
            g.Members.Add(m.Uid);
            g.Refs[m.Uid] = new AswMemberRef { Uid = m.Uid, Name = m.Name, Type = m.Type, Label = m.TypeLabel, Deck = m.DeckUid };
            g.Slots[m.Uid] = new SlotRef { DeckUid = m.DeckUid, Type = m.Type, K = m.Slot };
            g.Joined[m.Uid] = 0;   // T-186: "taken at" is not saved; ties after a load are decided by (GroupSeq, window, k)
        }

        /// <summary>"Return control" (T-091) for one aircraft: leave the group, ROE and depth come back from memory.</summary>
        public void ReleaseUid(int uid)
        {
            var g = GroupOf(uid);
            if (Sar != null) Sar.ReleaseUnit(uid, "released");   // a SAR claim goes; on a ship this also ends the run and restores the route
            if (g != null) RemoveMember(g, uid, true); else RestoreMemory(uid);
            RebuildView();
        }
    }
}
