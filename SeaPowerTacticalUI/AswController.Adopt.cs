using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-121: part of the ASW controller (see AswController.cs). 0.27.8: deck machines that launch or land are followed into the group (adoption).

    internal sealed partial class AswController
    {
        // A deck aircraft that was launched shows up in the game as a new air unit with a new uid: adopt it into the group.
        // T-150: GoneSince = -1 while the row (task / unit / cool-down) is still listed; the time limit counts from the first tick it is missing.
        // Kind "launch": a deck row turns into an air unit; "land": an air unit turns into a cool-down row and then back into a hangar row.
        // 0.27.8: GoneTicks counts the scans with nothing to follow (the time limit needs both, so time compression cannot end it within a few scans);
        // HangarSeen = machines of the type in the hangar of the deck while the task row was still listed (-1 = unknown): more of them after the
        // task is gone, with no LaunchTask spawned from it, means the game put the machine back (abortLaunchTask).
        private sealed class Adopt
        {
            public Grp G; public int Uid, DeckUid; public string Task, Type, Key, Serial, Base, Kind = "launch";
            public HashSet<int> Known = new HashSet<int>(); public double GoneSince = -1;
            public int GoneTicks, HangarSeen = -1;
            public string LastSeen;   // state of the followed row when it was last listed ("deck_prep 840s" ...), for the log
        }

        /// <summary>Scans with nothing to follow before an adoption may expire (on top of AdoptGapS of mission time).</summary>
        internal const int AdoptGapTicks = 30;
        /// <summary>Scans after the task row is gone before "back in the hangar" is concluded (the spawn frame may list neither the task nor the LaunchTask).</summary>
        private const int AbortTicks = 3;

        private bool HasAdopt(int uid)
        {
            foreach (var a in _adopts) if (a.Uid == uid) return true;
            return false;
        }

        /// <summary>One Adopt per row: a row that is already tracked (callback + handover) is not tracked twice.</summary>
        private Adopt StartAdopt(Grp g, int uid, AswMember row, string task, string kind)
        {
            foreach (var x in _adopts) if (x.G == g && x.Uid == uid && x.Kind == kind) return x;
            var a = new Adopt { G = g, Uid = uid, Task = task, Kind = kind, Known = new HashSet<int>(_airBefore) };
            if (row != null)
            {
                a.DeckUid = row.DeckUid; a.Type = row.Type; a.Key = row.DeckKey; a.Serial = row.Serial; a.Base = row.Base;
                a.LastSeen = row.Where + " " + I((int)Math.Round(Math.Max(0, row.ReadyInS))) + "s";
                if (kind == "launch" && KeyIs(row, "task:"))
                {
                    // the task row may already be gone (the member vanished): the hangar of the snapshot it was last listed in
                    int hb;
                    a.HangarSeen = _live.ContainsKey(uid) ? HangarCount(a.DeckUid, a.Type) : _hangarBefore.TryGetValue(a.DeckUid + "|" + a.Type, out hb) ? hb : 0;
                }
            }
            _adopts.Add(a);
            _game.Log("asw adopt start " + g.Name + " kind=" + kind + " uid=" + I(uid) + " key=" + (a.Key ?? "-") + " task=" + (task ?? "-"));
            return a;
        }

        private AswMember FindRow(Func<AswMember, bool> ok)
        {
            foreach (var c in _live.Values) if (GroupOf(c.Uid) == null && ok(c)) return c;
            return null;
        }

        private static bool KeyIs(AswMember m, string prefix) { return m.DeckKey != null && m.DeckKey.StartsWith(prefix, StringComparison.Ordinal); }

        /// <summary>The hangar row of a type on a deck (by uid, else by the base ship name) that is free: the slot with the lowest k (ADR-0080 s.1), else a legacy stack row (T-150 P6; the same rule serves SAR landings).</summary>
        private AswMember HangarRow(string type, int deckUid, string baseName)
        {
            return FreeSlot(deckUid, type, baseName) ?? FindRow(c => KeyIs(c, "hangar:") && c.Slot == 0 && c.Type == type && (deckUid != 0 ? c.DeckUid == deckUid : baseName == null || c.Base == null || c.Base == baseName));
        }

        // ---- T-181 (ADR-0080): hangar slots ---------------------------------------------------------------------------

        private sealed class SlotRef { public int DeckUid; public string Type; public int K; }

        /// <summary>The mod waits this long for the task row of an ordered machine to be listed (the game lists it a frame or two later).</summary>
        internal const double OwnRowWaitS = 30;

        private static bool IsSlot(AswMember m) { return m.Slot > 0 && KeyIs(m, "hangar:"); }

        /// <summary>T-186: the shortage counter lives in the shared book (dropped after AbortTicks ticks).</summary>
        private bool SlotPending(int uid) { return Book.Short("asw", uid) > 0; }

        /// <summary>A free (in no group, no foreign owner) slot row of the pair with the lowest k. deckUid 0 = any deck whose ship is called baseName (null = any).</summary>
        private AswMember FreeSlot(int deckUid, string type, string baseName)
        {
            AswMember best = null;
            foreach (var c in _live.Values)
            {
                if (!IsSlot(c) || c.Type != type || GroupOf(c.Uid) != null || ForeignOwner(c.Uid) != null) continue;
                if (deckUid != 0 ? c.DeckUid != deckUid : (baseName != null && c.Base != null && c.Base != baseName)) continue;
                if (best == null || (c.DeckUid == best.DeckUid ? c.Slot < best.Slot : c.DeckUid < best.DeckUid)) best = c;
            }
            return best;
        }

        /// <summary>An old stack uid (HangarUid(ship, type)) names every free slot of the pair (customer Q2, 2026-10-22); empty = not a stack uid.</summary>
        private List<AswMember> StackSlots(int uid)
        {
            var res = new List<AswMember>();
            if (_live.ContainsKey(uid)) return res;
            var seen = new HashSet<string>();
            foreach (var c in _live.Values)
            {
                if (!IsSlot(c) || !seen.Add(c.DeckUid + "|" + c.Type) || AswMath.HangarUid(c.DeckUid, c.Type) != uid) continue;
                foreach (var f in _live.Values) if (IsSlot(f) && f.DeckUid == c.DeckUid && f.Type == c.Type && GroupOf(f.Uid) == null && ForeignOwner(f.Uid) == null) res.Add(f);
            }
            res.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return res;
        }

        /// <summary>T-186 (ADR-0082 s.2): the claims of every group on hangar slots, for the shared book (window "asw"). An order of the slot in flight pins it.</summary>
        private IEnumerable<AirOpsMath.SlotClaim> SlotClaims()
        {
            var res = new List<AirOpsMath.SlotClaim>();
            foreach (var g in _groups)
                foreach (var kv in g.Slots)
                {
                    int u = kv.Key;
                    if (!g.Members.Contains(u)) continue;
                    long joined; g.Joined.TryGetValue(u, out joined);
                    res.Add(new AirOpsMath.SlotClaim
                    {
                        Uid = u, Group = g.Name, Window = "asw", Seq = Seq(g), JoinedAt = joined, K = kv.Value.K, DeckUid = kv.Value.DeckUid, Type = kv.Value.Type,
                        Pinned = _relief.InFlight("launch" + I(u)) || _relief.InFlight("prepare" + I(u))   // an order of this very slot is in flight (risk 3)
                    });
                }
            return res;
        }

        /// <summary>Machines of the pair in the hangar by the last scan; -1 = this window holds no slot of the pair (the book then asks the others).</summary>
        private int SlotNumbers(int deck, string type)
        {
            bool mine = false;
            foreach (var g in _groups)
            {
                foreach (var kv in g.Slots) if (kv.Value.DeckUid == deck && kv.Value.Type == type && g.Members.Contains(kv.Key)) { mine = true; break; }
                if (mine) break;
            }
            if (!mine) return -1;
            int numbers = 0;
            foreach (var c in _live.Values) if (IsSlot(c) && c.DeckUid == deck && c.Type == type) numbers = Math.Max(numbers, c.DeckCount);
            return numbers;
        }

        /// <summary>A move or a drop decided by the book (ADR-0082 s.2) for a member of this window.</summary>
        private void ApplySlot(AirOpsMath.SlotMove mv)
        {
            var g = Find(mv.Group);
            if (g == null) return;
            if (mv.Drop) { DropSlot(g, mv.Uid, mv.DeckUid, mv.Type); return; }
            _game.Log("asw slot " + g.Name + " uid=" + I(mv.Uid) + " -> " + I(mv.ToUid) + " k=" + I(mv.FromK) + "->" + I(mv.ToK) + " n=" + I(mv.N));
            MoveMember(g, mv.Uid, mv.ToUid, mv.ToK);
        }

        /// <summary>The book decided: the machine of the slot is gone for good (AbortTicks scans without it).</summary>
        private void DropSlot(Grp g, int uid, int deck, string type)
        {
            string name = g.Refs.ContainsKey(uid) ? g.Refs[uid].Name : I(uid);
            _game.Log("asw member hangar_taken " + g.Name + " uid=" + I(uid) + " type=" + type + " deck=" + I(deck));
            RemoveMember(g, uid, false); _mem.Remove(uid);
            Warn(g, "hangar_taken", name, 600);
        }

        /// <summary>
        /// The one place a member changes its uid in place (a slot rebind, a hand-over to a task): every per-uid record of the group goes along.
        /// toK 0 = the new uid is not a slot.
        /// </summary>
        private void MoveMember(Grp g, int fromUid, int toUid, int toK)
        {
            int ix = g.Members.IndexOf(fromUid);
            if (ix < 0 || fromUid == toUid) return;
            g.Members[ix] = toUid;
            AswMemberRef rf;
            if (g.Refs.TryGetValue(fromUid, out rf)) { g.Refs.Remove(fromUid); rf.Uid = toUid; if (toK == 0) rf.Deck = 0; g.Refs[toUid] = rf; }
            string tac;
            if (g.Assign.TryGetValue(fromUid, out tac)) { g.Assign.Remove(fromUid); g.Assign[toUid] = tac; }
            Job job;
            if (g.Jobs.TryGetValue(fromUid, out job)) { g.Jobs.Remove(fromUid); g.Jobs[toUid] = job; }
            if (g.Prepared.Remove(fromUid)) g.Prepared.Add(toUid);
            if (g.Released.Remove(fromUid)) g.Released.Add(toUid);
            if (g.Dumped.Remove(fromUid)) g.Dumped.Add(toUid);
            if (g.Allowed.Remove(fromUid)) g.Allowed.Add(toUid);
            double at;
            if (g.StandbyAt.TryGetValue(fromUid, out at)) { g.StandbyAt.Remove(fromUid); g.StandbyAt[toUid] = at; }
            long jn;
            if (g.Joined.TryGetValue(fromUid, out jn)) { g.Joined.Remove(fromUid); g.Joined[toUid] = jn; }
            SlotRef sr;
            if (g.Slots.TryGetValue(fromUid, out sr))
            {
                g.Slots.Remove(fromUid);
                if (toK > 0) { sr.K = toK; g.Slots[toUid] = sr; }
            }
            AswMemDto dto;
            if (_mem.TryGetValue(fromUid, out dto)) { _mem.Remove(fromUid); dto.Uid = toUid; _mem[toUid] = dto; }
            if (_radar != null) _radar.Remap(fromUid, toUid);   // T-241
        }

        /// <summary>ADR-0080 s.3: the order of a slot was taken by the deck: the member leaves the slot at once and becomes the uid its task row will have.</summary>
        private void HandOver(Grp g, int slotUid, string key, string task, bool allow)
        {
            SlotRef sr;
            g.Slots.TryGetValue(slotUid, out sr);
            int nu = AswMath.TaskUid(key);
            g.OwnKeys[key] = _now;
            var rec = new OwnRec { Task = task, Type = sr != null ? sr.Type : null, DeckUid = sr != null ? sr.DeckUid : 0, Uid = nu, SourceUid = slotUid, SourceK = sr != null ? sr.K : 0, Allow = allow };
            AswMember src;
            if (_live.TryGetValue(slotUid, out src)) { rec.Type = src.Type; rec.DeckUid = src.DeckUid; }
            rec.HangarSeen = Math.Max(0, HangarCount(rec.DeckUid, rec.Type) - 1);
            g.OwnInfo[key] = rec;
            MoveMember(g, slotUid, nu, 0);
            // the slot uid is positional: the next machine of that slot is a new one, the order pause of this one does not apply to it
            foreach (string pre in new[] { "launch", "prepare", "sarlaunch" }) _relief.Mark(pre + I(slotUid), -1e9);
            g.Assign[nu] = task;
            if (allow) g.Allowed.Add(nu);
            g.Dirty = false;
            _game.Log("asw handover " + g.Name + " slot=" + I(slotUid) + "#" + I(rec.SourceK) + " key=" + key + " -> uid=" + I(nu) + " task=" + task);
        }

        /// <summary>A hangar slot of the group on this deck for this type (the lowest k); 0 = none.</summary>
        private int GroupSlotOf(Grp g, int deckUid, string type)
        {
            int best = 0, bestK = int.MaxValue;
            foreach (var kv in g.Slots)
                if (kv.Value.DeckUid == deckUid && kv.Value.Type == type && g.Members.Contains(kv.Key) && kv.Value.K < bestK) { best = kv.Key; bestK = kv.Value.K; }
            return best;
        }

        /// <summary>The member is an order of the group whose task row has not been listed yet (key of the order).</summary>
        private bool PendingTask(Grp g, int uid, out string key)
        {
            foreach (var kv in g.OwnInfo)
                if (kv.Value.Uid == uid && g.OwnKeys.ContainsKey(kv.Key) && !g.OwnSeen.Contains(kv.Key)) { key = kv.Key; return true; }
            key = null;
            return false;
        }

        /// <summary>ADR-0080 s.4: an own task of the group on this deck still waits for a place on it ("await_deck"): no new order goes to the deck.</summary>
        private bool DeckAwaiting(Grp g, int deckUid)
        {
            foreach (var kv in g.OwnInfo)
            {
                if (kv.Value.DeckUid != deckUid) continue;
                foreach (var c in _live.Values) if (c.DeckKey == kv.Key && c.DeckPhase == "await_deck") return true;
            }
            return false;
        }

        /// <summary>The wait for the task row ran out: the machine is back in the hangar (a slot again) or the order vanished. True = the member is dealt with.</summary>
        private bool OrderRowGone(Grp g, int uid, string key)
        {
            OwnRec rec = g.OwnInfo[key];
            g.OwnKeys.Remove(key); g.OwnSeen.Remove(key); g.OwnInfo.Remove(key);
            if (HangarCount(rec.DeckUid, rec.Type) > rec.HangarSeen)
            {
                var hang = HangarRow(rec.Type, rec.DeckUid, null);
                if (hang != null)
                {
                    string task; g.Assign.TryGetValue(uid, out task);
                    SwapMember(g, uid, hang, "reserve");
                    if (g.Members.Contains(hang.Uid)) { _game.Log("asw adopt " + g.Name + " uid=" + I(uid) + " key=" + key + " aborted: back in the hangar -> slot " + I(hang.Uid)); return true; }
                }
            }
            string name = g.Refs.ContainsKey(uid) ? g.Refs[uid].Name : I(uid);
            _game.Log("asw member lost " + g.Name + " uid=" + I(uid) + " " + name + " reason=order_vanished");
            Warn(g, "member_lost", name + ": order_vanished", 30);
            RemoveMember(g, uid, false); _mem.Remove(uid);
            return true;
        }

        private void SwapMember(Grp g, int oldUid, AswMember c, string assign)
        {
            AddMember(g, c);
            if (!g.Members.Contains(c.Uid)) return;
            RemoveMember(g, oldUid, false); _mem.Remove(oldUid);
            g.Assign[c.Uid] = assign;
            g.StandbyAt.Remove(oldUid);
        }

        private void ProcessAdopts(Grp g)
        {
            foreach (var a in new List<Adopt>(_adopts))
            {
                if (a.G != g) continue;
                AswMember m;
                bool present = _live.TryGetValue(a.Uid, out m);
                if (a.Kind == "launch")
                {
                    // off the deck (air, or a game / SAR state of a flying machine): done; a lingering adoption would hijack a later launch of the type
                    if (present && !IsDeckRow(m)) { _adopts.Remove(a); continue; }
                    if (present)
                    {
                        // still on the deck list (or already swapped to its "unit:" row)
                        a.GoneSince = -1; a.GoneTicks = 0;
                        if (KeyIs(m, "task:")) a.HangarSeen = HangarCount(a.DeckUid, a.Type);
                        a.LastSeen = m.Where + " " + I((int)Math.Round(Math.Max(0, m.ReadyInS))) + "s";
                        continue;
                    }
                }
                else if (present) { a.GoneSince = -1; a.GoneTicks = 0; continue; }   // landing: the unit: / cool: row is still listed
                if (a.GoneSince < 0) a.GoneSince = _now;
                a.GoneTicks++;
                if (a.GoneTicks == 1) GoneLog(g, a);
                if (a.Kind == "launch")
                {
                    double lat = double.NaN, lon = double.NaN;
                    var dt = a.DeckUid != 0 ? _game.Target(a.DeckUid) : null;
                    if (dt != null) { lat = dt.Lat; lon = dt.Lon; }
                    var list = new List<AswMember>(_live.Values);
                    string from = a.Key != null && a.Key.StartsWith("task:", StringComparison.Ordinal) ? a.Key : null;
                    int pick = AswMath.PickAdoptee(new AswMember { Uid = a.Uid, DeckUid = a.DeckUid, Type = a.Type }, list, a.Known, u => GroupOf(u) != null, lat, lon, from);
                    AswMember c;
                    if (pick != 0 && _live.TryGetValue(pick, out c))
                    {
                        bool unit = c.Where != "air";
                        SwapMember(g, a.Uid, c, a.Task ?? "reserve");
                        if (g.Members.Contains(c.Uid))
                        {
                            string via = from != null && c.FromKey == from ? "task" : unit ? "unit" : "type";
                            _game.Log("asw adopt " + g.Name + " uid=" + I(a.Uid) + " -> " + I(c.Uid) + " via=" + via);
                            if (unit) { a.Uid = c.Uid; a.GoneSince = -1; a.GoneTicks = 0; } else _adopts.Remove(a);
                            continue;
                        }
                    }
                    if (from != null && a.HangarSeen >= 0 && a.GoneTicks >= AbortTicks && HangarCount(a.DeckUid, a.Type) > a.HangarSeen && BackToHangar(g, a)) continue;
                }
                else
                {
                    // landing: the cool-down row of the same machine, then the hangar row of its type
                    var cool = FindRow(c => KeyIs(c, "cool:") && (a.Serial != null ? c.Serial == a.Serial : c.Type == a.Type && (a.Base == null || c.Base == a.Base)));
                    if (cool != null)
                    {
                        SwapMember(g, a.Uid, cool, "reserve");
                        if (g.Members.Contains(cool.Uid))
                        {
                            _game.Log("asw adopt " + g.Name + " uid=" + I(a.Uid) + " -> " + I(cool.Uid) + " via=cool serial=" + (a.Serial ?? "-"));
                            a.Uid = cool.Uid; a.Key = cool.DeckKey; a.GoneSince = -1;
                            continue;
                        }
                    }
                    var hang = HangarRow(a.Type, a.DeckUid, a.Base);   // T-181: the landed machine takes a free slot of its type (no merging into a row of the group)
                    if (hang != null)
                    {
                        SwapMember(g, a.Uid, hang, "reserve");
                        if (g.Members.Contains(hang.Uid)) _game.Log("asw adopt " + g.Name + " uid=" + I(a.Uid) + " -> " + I(hang.Uid) + " via=hangar");
                        _adopts.Remove(a);
                        continue;
                    }
                }
                if (_now - a.GoneSince > _defaults.AdoptGapS && a.GoneTicks >= AdoptGapTicks)
                {
                    _adopts.Remove(a);
                    _game.Log("asw adopt expired " + g.Name + " uid=" + I(a.Uid) + " kind=" + a.Kind + " key=" + (a.Key ?? "-") + " last=" + (a.LastSeen ?? "-"));
                    if (g.Members.Contains(a.Uid))
                    {
                        string name = g.Refs.ContainsKey(a.Uid) ? g.Refs[a.Uid].Name : I(a.Uid);
                        string reason = a.Kind == "land" ? "left_air_landed_or_lost" : "left_deck";
                        _game.Log("asw member lost " + g.Name + " uid=" + I(a.Uid) + " " + name + " reason=" + reason);
                        Warn(g, "member_lost", name + ": " + reason, 30);
                        RemoveMember(g, a.Uid, false); _mem.Remove(a.Uid);
                    }
                }
            }
        }

        /// <summary>
        /// 0.27.8 (live: a held-then-allowed task vanished with no machine and no refund): the first scan without the followed row says what the deck
        /// shows instead, so the next log tells a spawn that was never listed from a task the game dropped.
        /// </summary>
        private void GoneLog(Grp g, Adopt a)
        {
            int launching = 0, newAir = 0;
            foreach (var c in _live.Values)
            {
                if (c.Type != a.Type) continue;
                if (c.Where == "deck_launching" && c.DeckUid == a.DeckUid) launching++;
                else if (c.Where == "air" && !a.Known.Contains(c.Uid) && GroupOf(c.Uid) == null) newAir++;
            }
            _game.Log("asw adopt " + g.Name + " uid=" + I(a.Uid) + " kind=" + a.Kind + " key=" + (a.Key ?? "-") + " row gone: last=" + (a.LastSeen ?? "-")
                + " launching=" + I(launching) + " new_air=" + I(newAir) + " hangar=" + I(HangarCount(a.DeckUid, a.Type)) + "/" + I(a.HangarSeen)
                + " " + (a.DeckUid != 0 ? _game.DeckState(a.DeckUid) : "deck=?"));
        }

        /// <summary>Machines of a type in the hangar of a deck (the "hangar:" row; 0 = no row).</summary>
        private int HangarCount(int deckUid, string type)
        {
            foreach (var c in _live.Values)
                if (KeyIs(c, "hangar:") && c.DeckUid == deckUid && c.Type == type) return Math.Max(1, c.DeckCount);
            return 0;
        }

        /// <summary>
        /// The task was taken back by the game (abortLaunchTask: no LaunchTask was spawned from it and the hangar has one more machine): the member
        /// becomes the hangar row (or merges into it when the row is already in the group). False = the hangar row is in another group.
        /// </summary>
        private bool BackToHangar(Grp g, Adopt a)
        {
            var hang = HangarRow(a.Type, a.DeckUid, a.Base);
            string how = null;
            if (hang != null)
            {
                SwapMember(g, a.Uid, hang, "reserve");
                if (g.Members.Contains(hang.Uid)) how = "slot " + I(hang.Uid);
            }
            if (how == null) return false;
            _adopts.Remove(a);
            _game.Log("asw adopt " + g.Name + " uid=" + I(a.Uid) + " key=" + (a.Key ?? "-") + " aborted: back in the hangar -> " + how);
            return true;
        }


        private static bool IsDeckRow(AswMember m) { return m.Where != null && m.Where.StartsWith("deck", StringComparison.Ordinal); }
        private static bool IsTask(AswMember m) { return m.DeckKey != null && m.DeckKey.StartsWith("task:", StringComparison.Ordinal); }

        /// <summary>Own deck orders (T-150: ASW and SAR) that now exist as rows join the group with the tactic they were ordered for; keys whose rows are gone are forgotten.</summary>
        private void AdoptOwnTasks(Grp g)
        {
            if (g.OwnKeys.Count == 0) return;
            foreach (var m in new List<AswMember>(_live.Values))
            {
                if (m.DeckKey == null || !g.OwnKeys.ContainsKey(m.DeckKey)) continue;
                OwnRec rec;
                g.OwnInfo.TryGetValue(m.DeckKey, out rec);
                if (rec != null && rec.Uid == m.Uid && g.Members.Contains(m.Uid))
                {
                    // T-181: the member was handed over to this row's uid at the order; the row is listed now
                    if (g.OwnSeen.Add(m.DeckKey))
                    {
                        _game.Log("asw handover " + g.Name + " key=" + m.DeckKey + " row seen uid=" + I(m.Uid));
                        if (KeyIs(m, "task:")) StartAdopt(g, m.Uid, m, g.Assign.ContainsKey(m.Uid) ? g.Assign[m.Uid] : (rec.Task ?? "reserve"), "launch");
                    }
                    continue;
                }
                if (GroupOf(m.Uid) != null) continue;
                AddMember(g, m);
                if (!g.Members.Contains(m.Uid)) continue;
                string task = rec != null && rec.Task != null ? rec.Task : g.Order == "sar" ? "sar" : "reserve";
                g.Assign[m.Uid] = task;
                g.OwnSeen.Add(m.DeckKey);
                _game.Log("asw handover " + g.Name + " key=" + m.DeckKey + " -> uid=" + I(m.Uid) + " task=" + task);
                if (KeyIs(m, "task:")) StartAdopt(g, m.Uid, m, task, "launch");
            }
            foreach (string k in new List<string>(g.OwnKeys.Keys))
            {
                bool live = false;
                foreach (var m in _live.Values) if (m.DeckKey == k) { live = true; break; }
                OwnRec orec; g.OwnInfo.TryGetValue(k, out orec);
                bool handed = orec != null && orec.Uid != 0 && g.Members.Contains(orec.Uid);   // T-181: a handed-over member waits OwnRowWaitS (TickGroup decides)
                if (!live && (g.OwnSeen.Contains(k) || (!handed && _now - g.OwnKeys[k] > 10))) { g.OwnKeys.Remove(k); g.OwnSeen.Remove(k); g.OwnInfo.Remove(k); }
            }
        }
    }
}
