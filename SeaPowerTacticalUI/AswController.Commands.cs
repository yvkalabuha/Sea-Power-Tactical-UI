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
        // ---- commands (data JSON, or "!code|detail" on error) ------------------------------------------------------

        public static bool IsErr(string s) { return s != null && s.Length > 0 && s[0] == '!'; }
        private static string Fail(string code, string detail = null) { return "!" + code + (detail == null ? "" : "|" + detail); }

        /// <summary>Null = added (or already a member); "hangar_taken" = a hangar machine of the pair is free no more (ADR-0080 s.6); anything else is refused as before.</summary>
        private string AddMember(Grp g, AswMember m) { return AddMember(g, m, null); }

        /// <summary>T-346 D4: the exclusion of an aircraft for a group order (sar | strike | else the ASW search): the one source of the ring's dry run (AdmitWhy) and of the add command. null = fit.</summary>
        private string OrderExclusion(string order, AswMember m)
        {
            if (order == "sar") return AswMath.SarExclusionReason(m);
            if (order == "strike") return AswStrikeMath.StrikeExclusion(m, TorpedoLoadoutOn(m));
            return AswMath.ExclusionReason(m);
        }

        /// <summary>kind (T-346): null = the group order decides as before; asw | sar = the requester says which crew the group is for, an unfit aircraft is refused with the reason of the ring (the group order, if set, still wins).</summary>
        private string AddMember(Grp g, AswMember m, string kind)
        {
            if (g.Order == "sar" && AswMath.SarExclusionReason(m) != null) return "unfit";   // not a SAR machine (T-109)
            if (g.Order == "strike" && AswStrikeMath.StrikeExclusion(m, TorpedoLoadoutOn(m)) != null) return "unfit";   // no torpedo (T-173)
            if (kind != null && g.Order != "sar" && g.Order != "strike")
            {
                string why = OrderExclusion(kind == "sar" ? "sar" : null, m);
                if (why != null) return why;
            }
            var old = GroupOf(m.Uid);
            if (IsSlot(m))
            {
                // T-181: the machines of a type on a deck are interchangeable; a slot held by someone else is replaced by the lowest free one (no taking over)
                if (old == g) return null;
                if (old != null || ForeignOwner(m.Uid) != null)
                {
                    var free = FreeSlot(m.DeckUid, m.Type, null);
                    if (free == null) return "hangar_taken";
                    m = free;
                }
                g.Slots[m.Uid] = new SlotRef { DeckUid = m.DeckUid, Type = m.Type, K = m.Slot };
                g.Joined[m.Uid] = Book.NextJoin();
            }
            else
            {
                if (ForeignOwner(m.Uid) != null) return "foreign";   // held by a CAP group (T-112)
                if (old != null && old != g) RemoveMember(old, m.Uid, true);
            }
            if (g.Members.Contains(m.Uid)) return null;
            g.Members.Add(m.Uid);
            g.Refs[m.Uid] = new AswMemberRef { Uid = m.Uid, Name = m.Name, Type = m.Type, Deck = IsSlot(m) ? m.DeckUid : 0 };
            g.Dirty = true;
            return null;
        }

        private void RemoveMember(Grp g, int uid, bool restore)
        {
            g.Members.Remove(uid); g.Refs.Remove(uid); g.Assign.Remove(uid); g.Jobs.Remove(uid); g.Prepared.Remove(uid);
            g.Slots.Remove(uid); g.Joined.Remove(uid); g.Allowed.Remove(uid);   // T-181
            g.Dirty = true;
            ReleaseRuns(g.Name, uid, "member_removed"); _waitAt.Remove(uid); _gameRtbNoted.Remove(uid);   // T-214
            RadarRelease(uid, restore);   // T-241
            if (restore) RestoreMemory(uid);
        }

        private readonly HashSet<int> _gameRtbNoted = new HashSet<int>();   // T-214: one log line per sortie

        /// <summary>T-214: the takeover check of one airborne member; true = the member was removed (the loop continues).
        /// The game's own return home (Winchester / BingoFuel / ReturnToBase) is not a player takeover: the member stays and lands (see AswDipTasks.Takeover).</summary>
        private bool CheckTakeover(Grp g, int u, AswMember m)
        {
            bool changed = m.Where != "player" && g.Order != "none" && g.Order != "sar" && m.Where == "air" && !m.Evading && _game.TasksChangedByOther(u);
            bool ownRtb = g.St != null && g.St.Rtb.Contains(u);
            string why = AswDipTasks.Takeover(m.Where, m.AiState, m.Evading, changed, ownRtb);
            if (why == null) { if (!AswMath.IsLeaving(m)) _gameRtbNoted.Remove(u); return false; }
            if (why == "game_rtb")
            {
                if (_gameRtbNoted.Add(u))
                {
                    _game.Log("asw member game_rtb " + g.Name + " uid=" + I(u) + " " + m.Name + " state=" + m.AiState);
                    Warn(g, "game_rtb", m.Name, 600);
                }
                return false;
            }
            _game.Log("asw member player_took " + g.Name + " uid=" + I(u) + " " + m.Name + " ai=" + (m.AiState ?? "-") + " where=" + m.Where);
            RemoveMember(g, u, true); Warn(g, "player_took", m.Name, 30);
            return true;
        }

        private void RestoreMemory(int uid)
        {
            AswMemDto m;
            if (!_mem.TryGetValue(uid, out m)) return;
            if (m.Roe != null) _game.RestoreRoe(uid, m.Roe);
            string prev;
            if (m.Depth != null) _game.SetDepth(uid, m.Depth, out prev);
            _mem.Remove(uid);
        }

        public string Group(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            AswCommandsParse.GroupCmd cmd = AswCommandsParse.ParseGroup(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            RefreshLive();
            Grp g;
            var rejected = new List<KeyValuePair<int, string>>();
            switch (cmd.Op)
            {
                case "create":
                    g = NewGroup(cmd.Group);
                    AddUids(g, cmd.Uids, rejected, cmd.Kind);
                    break;
                case "add":
                    g = Find(cmd.Group); if (g == null) return Fail("no_group");
                    AddUids(g, cmd.Uids, rejected, cmd.Kind);
                    break;
                case "remove":
                    g = Find(cmd.Group); if (g == null) return Fail("no_group");
                    foreach (int u in cmd.Uids) if (g.Members.Contains(u)) RemoveMember(g, u, true);
                    break;
                case "rename":
                {
                    g = Find(cmd.Group); if (g == null) return Fail("no_group");
                    var self = g;
                    string verr = AswGroupNames.Validate(cmd.NewName, x => { var o = Find(x); return o != null && o != self; });
                    if (verr != null) return Fail(verr);
                    string old = g.Name;
                    if (cmd.NewName != old) Rename(g, cmd.NewName);
                    g.AutoName = false;
                    RebuildView();
                    return Obj("group", g.Name, "old", old);
                }
                default:   // disband
                    g = Find(cmd.Group); if (g == null) return Fail("no_group");
                    StopStrikeTargets(g.Name);
                    foreach (int u in new List<int>(g.Members)) RemoveMember(g, u, true);
                    _groups.Remove(g);
                    ClearWarnings(g);
                    RebuildView();
                    return Obj("group", g.Name, "disbanded", true);
            }
            ReassignNow(g);
            RebuildView();
            if (rejected.Count == 0) return Obj("group", g.Name, "members", g.Members.Count);
            var rj = new StringBuilder("[");
            for (int i = 0; i < rejected.Count; i++) { if (i > 0) rj.Append(','); rj.Append(Obj("uid", rejected[i].Key, "reason", rejected[i].Value)); }
            return Obj("group", g.Name, "members", g.Members.Count, "rejected", new Raw(rj.Append(']').ToString()));
        }

        /// <summary>create|add: a live uid is added; an old stack uid (HangarUid) takes every free slot of its pair (ADR-0080 s.6/s.7); "hangar_taken" uids are listed in <paramref name="rejected"/> (uid, reason); T-346: with kind the unfit aircraft are listed too, with the reason of the ring.</summary>
        private void AddUids(Grp g, IList<int> uids, List<KeyValuePair<int, string>> rejected, string kind)
        {
            var before = new HashSet<int>(g.Members);
            foreach (int u in uids)
            {
                AswMember m;
                if (_live.TryGetValue(u, out m))
                {
                    // review T-181: a slot the command itself gave to this group (as the free replacement of a taken one) must not swallow the next uid of the list: it asks for one more machine
                    if (IsSlot(m) && g.Members.Contains(m.Uid) && !before.Contains(m.Uid))
                    {
                        var more = FreeSlot(m.DeckUid, m.Type, null);
                        if (more == null) { rejected.Add(new KeyValuePair<int, string>(u, "hangar_taken")); continue; }
                        m = more;
                    }
                    string res = AddMember(g, m, kind);
                    if (res == "hangar_taken" || (kind != null && res != null)) rejected.Add(new KeyValuePair<int, string>(u, res));
                    continue;
                }
                foreach (var s in StackSlots(u))
                {
                    string sr = AddMember(g, s, kind);
                    if (kind != null && sr != null) rejected.Add(new KeyValuePair<int, string>(s.Uid, sr));   // T-346 D4: the stack slots report the reason too
                }
            }
        }

        private Grp Resolve(string name, int targetUid, bool create)
        {
            if (name != null) return Find(name);
            if (targetUid != 0) foreach (var g in _groups) if (g.TargetUid == targetUid) return g;
            if (_groups.Count == 1) return _groups[0];
            if (!create) return null;
            string autoName = UiText.Get("asw.group.prefix");
            var auto = Find(autoName) ?? NewGroup(autoName);
            RefreshLive();
            foreach (var m in _live.Values) if (Capable(m) && AswMath.Usable(m)) AddMember(auto, m);
            return auto;
        }

        private void ApplyDepth(Grp g, string depth)
        {
            if (depth == null) return;
            g.Depth = depth;
            g.Prepared.Clear();
        }

        public string SearchCourse(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            var cmd = AswCommandsParse.ParseCourse(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            if (AswPatternMath.TacticsConflict(NormTactics("course", cmd.Tactics))) return Fail("tactics_conflict");   // T-334 / ADR-0130
            AswTarget t = cmd.TargetUid != 0 ? _game.Target(cmd.TargetUid) : _game.SelectedShip();
            if (t == null) return Fail("no_target");
            var g = Resolve(cmd.Group, t.Uid, true);
            if (g == null) return Fail("no_group");
            if (g.Members.Count == 0) return Fail("no_aircraft");
            ClearPreview();
            if (g.Order == "sar") _sarAir.Leave(g, false);
            g.Order = "course"; g.TargetUid = t.Uid; g.TargetName = t.Name; g.Rect = null;
            g.TargetKind = cmd.TargetKind; g.FormWidthNm = 0;
            bool madRemoved;
            g.Plan.Course = CourseSpecOf(cmd, g, out madRemoved);   // T-176 C1: the order spec (v3 fields, old ids migrated)
            g.Tactics = CourseTactics(g.Plan.Course);
            if (madRemoved) Warn(g, "tactic_removed", "mad", 0);
            if (cmd.LeadNm.HasValue) { g.P.LeadDipNm = cmd.LeadNm.Value; g.P.LeadMadNm = cmd.LeadNm.Value; }
            if (cmd.WidthNm.HasValue) g.P.WidthNm = cmd.WidthNm.Value;
            if (g.TargetKind == "formation")
            {
                g.FormWidthNm = FormationWidth(t.Uid);
                if (!cmd.WidthNm.HasValue && g.FormWidthNm > 0) g.P.WidthNm = g.FormWidthNm;   // the player did not give a width: the formation's own
            }
            ApplyDepth(g, cmd.Depth ?? _defaults.Pat.BuoyDepth);
            g.Plan.Course.Depth = g.Depth;
            ResetPlan(g);
            ReassignNow(g);
            AutoRename(g);
            RebuildView();
            return Obj("group", g.Name, "target", t.Name, "tactics", new Raw(TacticsJson(g)));
        }

        public string SearchArea(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            var cmd = AswCommandsParse.ParseArea(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            string tname, rerr; int tuid;
            double[] rect = AreaRectOf(cmd, out tname, out tuid, out rerr);
            if (rerr != null) return Fail(rerr);
            var g = Resolve(cmd.Group, 0, true);
            if (g == null) return Fail("no_group");
            if (g.Members.Count == 0) return Fail("no_aircraft");
            ClearPreview();
            if (g.Order == "sar") _sarAir.Leave(g, false);
            g.Order = "area"; g.Rect = rect; g.TargetName = tname; g.TargetUid = tuid; g.TargetKind = "ship"; g.FormWidthNm = 0;
            g.Tactics = cmd.Tactics.Count > 0 ? new List<string>(cmd.Tactics) : new List<string>(AutoArea);
            g.Plan.AreaPattern = g.Tactics.Count == 1 ? g.Tactics[0] : "auto";
            ApplyDepth(g, cmd.Depth ?? _defaults.Pat.BuoyDepth);
            ResetPlan(g);
            ReassignNow(g);
            AutoRename(g);
            RebuildView();
            return Obj("group", g.Name, "rect", new Raw(RectJson(rect)), "tactics", new Raw(TacticsJson(g)));
        }

        /// <summary>The search rectangle of an area command: given corners, or bearing + distance (+ size) from the target.</summary>
        private double[] AreaRectOf(AswCommandsParse.AreaCmd cmd, out string tname, out int tuid, out string err)
        {
            tname = null; tuid = 0; err = null;
            if (cmd.Rect != null) return cmd.Rect;
            AswTarget t = cmd.TargetUid != 0 ? _game.Target(cmd.TargetUid) : _game.SelectedShip();
            if (t == null) { err = "no_target"; return null; }
            var ctr = AswMath.Offset(Pos(t), cmd.Bearing, cmd.DistNm);
            double half = (cmd.SizeNm > 0 ? cmd.SizeNm : _defaults.AreaSizeNm) / 2;
            tname = t.Name; tuid = t.Uid;
            return RectAround(ctr, half * 2, half * 2);
        }

        /// <summary>The vehicle types of the group (members) with the chosen loadout and the ASW loadouts of the deck that holds the type.</summary>
        private void LoadoutRowsOf(Grp g, AswGroupView gv)
        {
            foreach (int u in g.Members)
            {
                AswMember m;
                if (!_live.TryGetValue(u, out m) || string.IsNullOrEmpty(m.Type) || gv.LoadoutRows.Exists(r => r.Type == m.Type)) continue;
                var row = new AswLoadoutView { Type = m.Type, Label = m.TypeLabel ?? m.Type };
                string ch;
                if (g.Loadouts.TryGetValue(m.Type, out ch) && !string.IsNullOrEmpty(ch)) row.Chosen = ch;
                int deck = 0;
                foreach (var l in _live.Values) if (l.Type == m.Type && l.DeckUid != 0 && l.Where.StartsWith("deck", StringComparison.Ordinal)) { deck = l.DeckUid; break; }
                if (deck == 0) deck = m.DeckUid;
                if (deck != 0)
                    try { foreach (var lo in _game.SarLoadouts(deck, m.Type)) if (AswMath.IsAswLoadout(lo, g.P.Loadouts) && !row.Names.Contains(lo.Name)) row.Names.Add(lo.Name); }
                    catch (Exception e) { _game.Log("asw loadouts error: " + e.Message); }
                gv.LoadoutRows.Add(row);
            }
        }

        /// <summary>asw_loadout {group?, type, loadout|"auto"}: the loadout the group orders for a vehicle type from the next launch on (T-142 K).</summary>
        public string Loadout(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            var cmd = AswCommandsParse.ParseLoadout(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            var g = Resolve(cmd.Group, 0, false);
            if (g == null) return Fail("no_group");
            bool auto = string.Equals(cmd.Loadout, "auto", StringComparison.OrdinalIgnoreCase);
            if (auto) g.Loadouts.Remove(cmd.Type); else g.Loadouts[cmd.Type] = cmd.Loadout;
            string key = g.Name + "|loadout_fallback";
            _warnAt.Remove(key);
            _warnings.RemoveAll(w => w.Group == g.Name && w.Text.StartsWith("loadout_fallback", StringComparison.Ordinal));
            _game.Log("asw loadout " + g.Name + " " + cmd.Type + "=" + (auto ? "auto" : cmd.Loadout));
            RebuildView();
            return Obj("group", g.Name, "type", cmd.Type, "loadout", auto ? "auto" : cmd.Loadout);
        }

        /// <summary>asw_set_tactics: the group's tactic set changes at once, existing assignments are re-made, no new search order is needed.</summary>
        public string SetTactics(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            var cmd = AswCommandsParse.ParseSetTactics(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            var g = Resolve(cmd.Group, 0, false);
            if (g == null) return Fail("no_group");
            if (g.Order == "course" && AswPatternMath.TacticsConflict(NormTactics("course", cmd.Tactics))) return Fail("tactics_conflict");   // T-334 / ADR-0130
            g.Tactics = NormTactics(g.Order, cmd.Tactics);
            if (g.Order == "course") g.Plan.Course.Tactics = g.Tactics.Count > 0 && AswPatternMath.ComboOf(g.Tactics) != "auto" ? new List<string>(g.Tactics) : new List<string>();
            // tactics that are gone lose their holders now; Reassign hands the freed members the tactics nobody holds
            foreach (int u in new List<int>(g.Assign.Keys))
            {
                string t = g.Assign[u];
                if (t != "reserve" && !g.Tactics.Contains(t))
                {
                    g.Assign.Remove(u);
                    AswMember am;
                    if (_live.TryGetValue(u, out am) && am.Where == "air") { CancelRoute(g, u); g.Jobs.Remove(u); }   // T-150 R2: the old tactic's route goes
                }
            }
            g.Lines.Clear(); g.CursorSet = false; g.NextCrossAt = 0; g.NextRingAt = 0;
            g.Dirty = true;
            _game.Log("asw tactics " + g.Name + " -> " + string.Join(",", g.Tactics.ToArray()));
            ReassignNow(g);
            RebuildView();
            return Obj("group", g.Name, "tactics", new Raw(TacticsJson(g)));
        }

        internal static double[] RectAround(GeoPt c, double wNm, double hNm)
        {
            var a = AswMath.ToGeo(c, 0, new NmPt(-hNm / 2, -wNm / 2));
            var b = AswMath.ToGeo(c, 0, new NmPt(-hNm / 2, wNm / 2));
            var d = AswMath.ToGeo(c, 0, new NmPt(hNm / 2, wNm / 2));
            var e = AswMath.ToGeo(c, 0, new NmPt(hNm / 2, -wNm / 2));
            return new[] { a.Lat, a.Lon, b.Lat, b.Lon, d.Lat, d.Lon, e.Lat, e.Lon };
        }

        private static string RectJson(double[] r)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < r.Length; i++) { if (i > 0) sb.Append(','); sb.Append(N(r[i])); }
            return sb.Append(']').ToString();
        }

        private static string TacticsJson(Grp g)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < g.Tactics.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Str(g.Tactics[i])); }
            return sb.Append(']').ToString();
        }

        private void ResetPlan(Grp g)
        {
            g.FrameSet = false; g.CursorSet = false; g.AreaBuilt = false; g.AreaDone = false;
            g.NextCrossAt = 0; g.NextRingAt = 0; g.Jobs.Clear(); g.Dirty = true; g.Lines.Clear();
            g.Assign.Clear(); g.PreScanned = false; g.ReliefIn = -1; g.Status = "";
            g.AreaIdx = g.AreaDipIdx = g.AreaMadIdx = 0;
            ResetPlanState(g);
            // a new order replaces the old route: the drop/dip tasks of the previous order must not stay queued in the game
            foreach (int u in g.Members)
            {
                AswMember m;
                if (_live.TryGetValue(u, out m) && m.Where == "air") CancelRoute(g, u);
            }
        }

        private void StopGroup(Grp g)
        {
            if (g.Order == "sar") _sarAir.Leave(g, true);
            StopStrikeTargets(g.Name);
            g.Order = "none"; g.Jobs.Clear(); g.Contacts.Clear(); g.Lines.Clear(); g.Prepared.Clear();
            // live 0.29.0: the zone / route of the stopped order stayed on the map; only the buoys that are really in the water remain drawn
            ResetPlanState(g);
            g.Rect = null; g.FrameSet = false; g.CursorSet = false; g.AreaBuilt = false; g.AreaDone = false; g.AreaAll = g.AreaDip = g.AreaMad = null;
            g.AreaIdx = g.AreaDipIdx = g.AreaDipIdx = g.AreaMadIdx = 0; g.Covered = 1; g.NextCrossAt = 0; g.NextRingAt = 0;
            foreach (int u in new List<int>(g.Members)) RestoreMemory(u);
        }

        public string Stop(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            var cmd = AswCommandsParse.ParseStop(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            ClearPreview();
            int n = 0;
            if (cmd.All) { foreach (var g in _groups) { if (g.Order != "none") n++; StopGroup(g); } }
            else
            {
                var g = Resolve(cmd.Group, 0, false);
                if (g == null) return Fail("no_group");
                if (g.Order != "none") n++;
                StopGroup(g);
            }
            RebuildView();
            return Obj("stopped", n);
        }

        public string Depth(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            var cmd = AswCommandsParse.ParseDepth(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            var g = Resolve(cmd.Group, 0, false);
            if (g == null) return Fail("no_group");
            ApplyDepth(g, cmd.Mode);
            RebuildView();
            return Obj("group", g.Name, "depth", g.Depth);
        }

        public string Restore(Dictionary<string, object> c)
        {
            if (_pendingRestore == null) return Fail("nothing_to_restore");
            var d = _pendingRestore; _pendingRestore = null;
            ApplyRestore(d);
            RebuildView();
            return Obj("groups", _groups.Count);
        }

        // ---- attack (ADR-0074: no confirmation any more) ------------------------------------------------------------

        /// <summary>asw_attack: there is no question to answer; the strike group attacks by itself (old clients get this code).</summary>
        public string Attack(Dictionary<string, object> c) { return Fail("no_confirm_needed"); }

        /// <summary>asw_attack_cancel: use asw_strike_skip.</summary>
        public string AttackCancel(Dictionary<string, object> c) { return Fail("use_asw_strike_skip"); }
    }
}
