using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039: pure deck and relief math shared by SAR (T-109) and CAP (T-112). No Unity, no reflection.
    internal sealed class LoadoutOption
    {
        public string Name, Display;
        public int Capacity;
        public double ReadyUpMin = 30, CoolDownMin = 30;
        public bool Available;
        public string[] Roles;
        public Dictionary<string, int> Weapons;
    }

    internal sealed class AirDeckTask
    {
        public string Key;
        public int ShipUid;
        public string Type, Loadout, State;
        public double RemainS;
        public bool Ours;
    }

    /// <summary>Deck access shared by ASW/SAR (T-109) and CAP (T-112) through the deck broker (ADR-0042). Main thread only.</summary>
    internal interface IAirOps
    {
        string DeckTimingsMode();                                            // FlightDeckTimingsMode name
        List<LoadoutOption> Loadouts(int deckUid, string vehicleType);       // with Capacity/ReadyUp/CoolDown/Available
        List<AirDeckTask> DeckTasks(int deckUid);                            // order = order of FlightDeckTasks
        /// <summary>FlightDeck.createLaunchTask with ltp{_missionType, _launchCount=1}. null = ok (key, readyS out); "no_ammo" when the game returns null; "no_airframes" (no machine of the type left in the hangar), "no_callsign", "launch_failed"/"unknown_loadout" otherwise.</summary>
        string OrderLaunch(int deckUid, string vehicleType, string loadoutName, string missionType, bool allowLaunch, out string key, out double readyS);
        string AllowLaunch(int deckUid, string key);                         // PendingLaunchTask.launchAllowed = true
        string AbortLaunch(int deckUid, string key);                         // only keys the mod ordered
        /// <summary>ADR-0042 item 5: add the pending task to FlightDeck._flightDeckTasksToPrioritise (moved to the head on the next game tick).
        /// null = ok (also when already queued) | "not_ours" | "deck_task_not_found" | "bad_state" (not await_deck/await_crew/ready_up) | "unsupported" (member missing) | "launch_failed:&lt;E&gt;".</summary>
        string Prioritise(int deckUid, string key);
        /// <summary>The flight deck is knocked out (BaseSystem.Inoperable: the game cleared its tasks and runs none until it is repaired). false = operable or unknown.</summary>
        bool DeckInoperable(int deckUid);
        /// <summary>Diagnostic one-liner of every task the deck lists (kind:type:state:key, any task class), "-" = none / no deck. Log only.</summary>
        string DeckDump(int deckUid);
    }

    /// <summary>Group facts for the deck order (ADR-0041). WindowIndex = GroupSeq; Window = "asw" | "cap" (tie-break).</summary>
    internal sealed class AirGroupPrio { public string Name, Order; public bool HasContact; public int WindowIndex;
                                     public bool CapGapUnderThreat;
                                     public string Window; }

    /// <summary>Pure sort key of a deck request (no delegates), so FlushOrder is testable on its own. Kind: 0 abort, 1 allow, 2 order.</summary>
    internal struct DeckReqKey { public int Tier, Seq, Kind, Ticket; public string Window, Group; }

    /// <summary>T-231: what a launch order needs to know of one record of a vehicle type on a deck.</summary>
    internal sealed class VtRecFacts { public bool HasLoadout; public int[] SquadronNumbers; }

    internal static class AirOpsMath
    {
        /// <summary>FlightDeckTimingsMode multiplier: Realistic 1, Casual 5, Arcade 10, Cheater 1e6, unknown 1.</summary>
        public static double TimingsMultiplier(string mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "casual": return 5;
                case "arcade": return 10;
                case "cheater": return 1e6;
                default: return 1;
            }
        }

        /// <summary>
        /// Seconds until a pending launch task has finished its ready-up (0.8.7 HandleReadyUpTask: _duration -= dt * multiplier * crew, so the rest is
        /// _duration / multiplier / crew; before a crew is assigned at least one is assumed). _startTime is never set on a live task (only on load), so it cannot be used.
        /// </summary>
        public static double PendingRemainS(double duration, string mode, int crew)
        {
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0) return 0;
            return duration / TimingsMultiplier(mode) / Math.Max(1, crew);
        }

        /// <summary>T-204: the vehicle type of a deck task: VehicleType._fileName; a CoolDownTask has none - its iniRef (the file name of the landed type). First non-empty, else null.</summary>
        internal static string DeckTaskType(string vehicleFileName, string coolIniRef)
        {
            if (!string.IsNullOrEmpty(vehicleFileName)) return vehicleFileName;
            return string.IsNullOrEmpty(coolIniRef) ? null : coolIniRef;
        }

        public const double DefaultHangarPrepS = 900;

        /// <summary>T-196: seconds an order from the hangar prepares: _readyUpDuration (s, &lt;=0 -> 900) / the deck timing mode. Same formula as ReadyS(lo, mode, 0).</summary>
        public static double HangarPrepS(double readyUpS, string mode)
        {
            return PendingRemainS(readyUpS > 0 ? readyUpS : DefaultHangarPrepS, mode, 0);
        }

        public static double ReadyS(LoadoutOption lo, string mode, double waitS)
        {
            return (lo == null ? 30 : lo.ReadyUpMin) * 60.0 / TimingsMultiplier(mode) + waitS;
        }

        public static double CoolDownS(LoadoutOption flown, string mode)
        {
            return (flown == null ? 30 : flown.CoolDownMin) * 60.0 / TimingsMultiplier(mode);
        }

        /// <summary>Landing + CoolDown(flown) + ReadyUp(next) + launch + transit.</summary>
        public static double TurnaroundS(LoadoutOption flown, LoadoutOption next, string mode,
                                         double recoverS, double launchS, double transitS)
        {
            return recoverS + CoolDownS(flown, mode) + ReadyS(next, mode, 0) + launchS + transitS;
        }

        private static int TieRank(string name, string[] order)
        {
            for (int i = 0; i < order.Length; i++) if (string.Equals(order[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return order.Length;
        }

        /// <summary>Best loadout by score (score &lt;= 0 never taken); ties by tieBreakCsv order, then by name. -1 = none.</summary>
        public static int PickBest(IList<LoadoutOption> los, Func<LoadoutOption, double> score, string tieBreakCsv, ISet<string> skip)
        {
            var order = (tieBreakCsv ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < order.Length; i++) order[i] = order[i].Trim();
            int best = -1; double bestScore = 0;
            if (los == null) return -1;
            for (int i = 0; i < los.Count; i++)
            {
                var lo = los[i];
                if (lo == null || (skip != null && skip.Contains(lo.Name))) continue;
                double sc = score(lo);
                if (sc <= 0) continue;
                bool better = best < 0 || sc > bestScore;
                if (!better && sc == bestScore)
                {
                    int ra = TieRank(lo.Name, order), rb = TieRank(los[best].Name, order);
                    better = ra < rb || (ra == rb && string.CompareOrdinal(lo.Name ?? "", los[best].Name ?? "") < 0);
                }
                if (better) { best = i; bestScore = sc; }
            }
            return best;
        }

        /// <summary>Deck queue: CAP gap under threat, then ASW with a contact, then the rest; ties by WindowIndex, Window, Name, input index (ADR-0041).</summary>
        public static List<string> DeckOrder(IList<AirGroupPrio> groups)
        {
            var l = new List<AirGroupPrio>();
            if (groups != null) l.AddRange(groups);
            var idx = new List<int>();
            for (int i = 0; i < l.Count; i++) idx.Add(i);
            idx.Sort((a, b) =>
            {
                int ka = Tier(l[a]), kb = Tier(l[b]);
                if (ka != kb) return ka.CompareTo(kb);
                if (l[a].WindowIndex != l[b].WindowIndex) return l[a].WindowIndex.CompareTo(l[b].WindowIndex);
                int c = string.CompareOrdinal(l[a].Window ?? "", l[b].Window ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(l[a].Name ?? "", l[b].Name ?? "");
                if (c != 0) return c;
                return a.CompareTo(b);
            });
            var res = new List<string>();
            foreach (int i in idx) res.Add(l[i].Name);
            return res;
        }

        /// <summary>ADR-0041: 0 = CAP group with a gap under air threat; 1 = ASW group with a contact; 2 = others ("sar" is always 2).</summary>
        public static int Tier(AirGroupPrio g)
        {
            if (g == null) return 2;
            if (g.Order == "cap" && g.CapGapUnderThreat) return 0;
            return g.Order == "asw" && g.HasContact ? 1 : 2;
        }

        /// <summary>Execution order of the requests of one deck: indices of reqs by (tier, seq, window, group, kind, ticket).</summary>
        public static List<int> FlushOrder(IList<DeckReqKey> reqs)
        {
            var idx = new List<int>();
            if (reqs == null) return idx;
            for (int i = 0; i < reqs.Count; i++) idx.Add(i);
            idx.Sort((a, b) =>
            {
                var x = reqs[a]; var y = reqs[b];
                if (x.Tier != y.Tier) return x.Tier.CompareTo(y.Tier);
                if (x.Seq != y.Seq) return x.Seq.CompareTo(y.Seq);
                int c = string.CompareOrdinal(x.Window ?? "", y.Window ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(x.Group ?? "", y.Group ?? "");
                if (c != 0) return c;
                if (x.Kind != y.Kind) return x.Kind.CompareTo(y.Kind);
                if (x.Ticket != y.Ticket) return x.Ticket.CompareTo(y.Ticket);
                return a.CompareTo(b);
            });
            return idx;
        }

        private static bool Movable(string state) { return state == "await_deck" || state == "await_crew" || state == "ready_up"; }

        /// <summary>Which flagged CAP tasks to move to the head and in which order to call Prioritise (reverse of the target order, because each
        /// moved task lands first). list = DeckTasks in order; flaggedRank(key) = -1 when not under the flag, else the rank of its group (lower = first);
        /// yielding(key) = the task belongs to the "asw" window. Empty when no yielding task stands before a movable flagged one.</summary>
        public static List<string> PrioritiseCalls(IList<AirDeckTask> list, Func<string, int> flaggedRank, Func<string, bool> yielding)
        {
            var res = new List<string>();
            if (list == null || flaggedRank == null || yielding == null) return res;
            var flagged = new List<KeyValuePair<int, string>>();   // (rank, key) in list order
            bool yieldSeen = false, need = false;
            foreach (var t in list)
            {
                if (t == null || t.Key == null) continue;
                int r = flaggedRank(t.Key);
                if (r >= 0 && Movable(t.State)) { flagged.Add(new KeyValuePair<int, string>(r, t.Key)); if (yieldSeen) need = true; }
                else if (r < 0 && yielding(t.Key)) yieldSeen = true;
            }
            if (!need) return res;
            // stable sort by rank (list order for equal ranks) = target order
            var order = new List<int>();
            for (int i = 0; i < flagged.Count; i++) order.Add(i);
            order.Sort((a, b) => flagged[a].Key != flagged[b].Key ? flagged[a].Key.CompareTo(flagged[b].Key) : a.CompareTo(b));
            for (int i = order.Count - 1; i >= 0; i--) res.Add(flagged[order[i]].Value);
            return res;
        }

        // ---- selftest (null = ok) ----
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static void Eq(string n, double a, double b) { if (Math.Abs(a - b) > 1e-6 && _err == null) _err = n + ": " + a + " != " + b; }

        private static LoadoutOption Lo(string n, int cap, bool avail = true, double ready = 30, double cool = 30)
        {
            return new LoadoutOption { Name = n, Display = n, Capacity = cap, Available = avail, ReadyUpMin = ready, CoolDownMin = cool };
        }

        // ---- T-186 (ADR-0082): hangar slot uid, task uid and rebinding, shared by the ASW and CAP windows (moved from AswMath, ADR-0080) ----

        internal const int MaxSlots = 16;

        /// <summary>Q1 of ADR-0082: who loses a machine when the hangar shrinks. FirstTaken = whoever took it last (customer-confirmed).</summary>
        internal enum SlotOrder { FirstTaken, GroupSeq, CapFirst }
        internal const SlotOrder SlotPolicy = SlotOrder.FirstTaken;
        /// <summary>Q2: a taken slot is replaced by the lowest free one of the pair (false = reject with in_group).</summary>
        internal const bool TakeFreeWhenTaken = true;
        /// <summary>Q3: a slot held by the other window is shown as a row with reason in_group (false = not shown).</summary>
        internal const bool ShowForeignSlots = true;

        private static uint Fnv1a(string s)
        {
            uint h = 2166136261;
            foreach (char ch in s) { h ^= ch; h *= 16777619; }
            return h;
        }

        /// <summary>ADR-0080 §1: uid of the hangar slot k of a type on a ship, -(0x20000000 .. 0x3FFFFFFF); never meets a task uid (0x40000000).</summary>
        internal static int HangarSlotUid(int shipUid, string type, int k)
        {
            uint h = Fnv1a(shipUid.ToString(CultureInfo.InvariantCulture) + "|" + (type ?? "") + "#" + k.ToString(CultureInfo.InvariantCulture));
            return -(0x20000000 | (int)(h & 0x1FFFFFFF));
        }

        /// <summary>ADR-0080 §3: uid of a task / cool-down row, a pure function of its key ("task:&lt;guid&gt;" | "cool:&lt;guid&gt;"): -(0x40000000 | 30 bits).</summary>
        internal static int TaskUid(string key)
        {
            return -(0x40000000 | (int)(Fnv1a(key ?? "") & 0x3FFFFFFF));
        }

        /// <summary>T-218 / ADR-0080 §3: true for the uid of a task / cool-down row (<see cref="TaskUid"/>): negative with the bit 0x40000000 of the magnitude set.
        /// Hangar slot uids (<see cref="HangarSlotUid"/>, bit 0x20000000), legacy CAP hangar uids, unit uids (positive) and 0 are not task uids.
        /// Public contract: such a uid is re-created by the game with a new GUID on every save load, so it cannot be looked up by uid after a load
        /// (T-217: the post-load gate on pause does not wait for group members with a task uid; T-218: the CAP window re-adopts them by ship, type and state).</summary>
        public static bool IsTaskUid(int uid)
        {
            return uid < 0 && ((-(long)uid) & 0x40000000L) != 0;
        }

        internal static string SlotKey(string type, int k) { return "hangar:" + type + "#" + k.ToString(CultureInfo.InvariantCulture); }

        /// <summary>"hangar:&lt;type&gt;#k" -> type, k. False for a legacy "hangar:&lt;type&gt;" and for other keys.</summary>
        internal static bool ParseSlotKey(string key, out string type, out int k)
        {
            type = null; k = 0;
            if (key == null || !key.StartsWith("hangar:", StringComparison.Ordinal)) return false;
            int hash = key.LastIndexOf('#');
            if (hash < 8) return false;
            int n;
            if (!int.TryParse(key.Substring(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) return false;
            type = key.Substring(7, hash - 7); k = n;
            return true;
        }

        /// <summary>ADR-0082 §6: an old CAP positional hangar uid -(ship*10000 + idx*100 + k + 1) (v = -uid, 0 &lt; v &lt; 0x20000000) -> ship = v / 10000.</summary>
        internal static bool IsLegacyCapHangarUid(int uid, out int shipUid)
        {
            shipUid = 0;
            if (uid >= 0) return false;
            long v = -(long)uid;
            if (v <= 0 || v >= 0x20000000) return false;
            shipUid = (int)(v / 10000);
            return true;
        }

        internal sealed class SlotClaim
        {
            public int Uid, Seq, K, DeckUid;
            public string Group, Window, Type;
            public long JoinedAt;
            public bool Pinned;   // an order of this slot is in flight: the book neither moves nor drops it
        }

        internal sealed class SlotMove
        {
            public int Uid, FromK, ToK, ToUid, DeckUid, N;
            public string Group, Window, Type;
            public bool Drop;   // Drop: ToK = 0, ToUid = 0
        }

        private static int WindowOrdinal(string w) { return w == null ? 0 : w == "cap" ? 1 : w == "asw" ? 2 : 3; }

        private static int CompareClaims(SlotClaim a, SlotClaim b, SlotOrder order)
        {
            int c;
            switch (order)
            {
                case SlotOrder.GroupSeq:
                    if ((c = a.Seq.CompareTo(b.Seq)) != 0) return c;
                    if ((c = a.JoinedAt.CompareTo(b.JoinedAt)) != 0) return c;
                    break;
                case SlotOrder.CapFirst:
                    if ((c = (a.Window != "cap" ? 1 : 0).CompareTo(b.Window != "cap" ? 1 : 0)) != 0) return c;
                    if ((c = a.JoinedAt.CompareTo(b.JoinedAt)) != 0) return c;
                    if ((c = a.Seq.CompareTo(b.Seq)) != 0) return c;
                    return a.K.CompareTo(b.K);
                default:
                    if ((c = a.JoinedAt.CompareTo(b.JoinedAt)) != 0) return c;
                    if ((c = a.Seq.CompareTo(b.Seq)) != 0) return c;
                    break;
            }
            if ((c = WindowOrdinal(a.Window).CompareTo(WindowOrdinal(b.Window))) != 0) return c;
            return a.K.CompareTo(b.K);
        }

        internal static List<SlotMove> RebindSlots(int numbers, IList<SlotClaim> claims) { return RebindSlots(numbers, claims, SlotPolicy); }

        /// <summary>
        /// ADR-0082 §2 / ADR-0080 §2: claims of one (deck, type) pair from both windows, numbers = machines now in the hangar. A Pinned claim with 1 &lt;= K &lt;= n
        /// keeps its k first; a Pinned claim with K &gt; n gets no move and no drop. Then, in the order of the policy, a claim with 1 &lt;= K &lt;= n that nobody before it holds
        /// stays; the rest take the lowest free k; the rest drop. Only the claims that move or drop are returned (in order).
        /// </summary>
        internal static List<SlotMove> RebindSlots(int numbers, IList<SlotClaim> claims, SlotOrder order)
        {
            int n = Math.Max(0, Math.Min(numbers, MaxSlots));
            var sorted = new List<SlotClaim>(claims);
            sorted.Sort((a, b) => CompareClaims(a, b, order));
            var taken = new HashSet<int>();
            var stays = new HashSet<SlotClaim>();
            foreach (var c in sorted)
                if (c.Pinned && c.K >= 1 && c.K <= n && taken.Add(c.K)) stays.Add(c);
            // Q1: only as many claims survive as there are machines; the ones who took last are the ones who drop, wherever their k is.
            var survive = new HashSet<SlotClaim>(stays);
            int room = n - stays.Count;
            foreach (var c in sorted)
                if (!c.Pinned && room > 0) { survive.Add(c); room--; }
            foreach (var c in sorted)
                if (!c.Pinned && survive.Contains(c) && c.K >= 1 && c.K <= n && taken.Add(c.K)) stays.Add(c);
            var res = new List<SlotMove>();
            int next = 1;
            foreach (var c in sorted)
            {
                if (stays.Contains(c) || c.Pinned) continue;
                var mv = new SlotMove { Uid = c.Uid, Group = c.Group, Window = c.Window, Type = c.Type, DeckUid = c.DeckUid, N = n, FromK = c.K };
                if (survive.Contains(c))
                {
                    while (next <= n && taken.Contains(next)) next++;
                    taken.Add(next); mv.ToK = next; mv.ToUid = HangarSlotUid(c.DeckUid, c.Type, next);
                }
                else mv.Drop = true;
                res.Add(mv);
            }
            return res;
        }

        /// <summary>T-231: the record of the type a launch order takes: the first with the loadout and a squadron with Numbers > 0; -1 = none.</summary>
        internal static int PickLaunchRecord(IList<VtRecFacts> recs, out int squadron)
        {
            squadron = -1;
            if (recs == null) return -1;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (r == null || !r.HasLoadout || r.SquadronNumbers == null) continue;
                for (int k = 0; k < r.SquadronNumbers.Length; k++) if (r.SquadronNumbers[k] > 0) { squadron = k; return i; }
            }
            return -1;
        }

        internal static string SlotSelfTest()
        {
            _err = null;
            int a = HangarSlotUid(7, "usn_sh-3h", 1);
            True("slot uid literals", a == -577077302 && HangarSlotUid(7, "usn_sh-3h", 2) == -560299683 && HangarSlotUid(100, "F-14A", 1) == -836476755 && HangarSlotUid(100, "F-14A", 3) == -870031993);
            True("task uid literals", TaskUid("task:abc") == -1994244102 && TaskUid("cool:abc") == -1142562998 && TaskUid("k1") == -1480425665);
            True("task uid", (-TaskUid("task:abc") & 0x40000000) != 0 && TaskUid("task:a") != TaskUid("cool:a"));
            True("is task uid", IsTaskUid(TaskUid("task:abc")) && IsTaskUid(TaskUid("cool:a")) && IsTaskUid(-1236665748) && !IsTaskUid(a) && !IsTaskUid(HangarSlotUid(77, "usn_f-14a", 1)) && !IsTaskUid(1628) && !IsTaskUid(0) && !IsTaskUid(-(77 * 10000 + 101)) && !IsTaskUid(int.MinValue));
            string ty; int kk;
            True("slot key", SlotKey("usn_sh-3h", 3) == "hangar:usn_sh-3h#3" && ParseSlotKey("hangar:usn_sh-3h#3", out ty, out kk) && ty == "usn_sh-3h" && kk == 3);
            True("slot key legacy", !ParseSlotKey("hangar:usn_sh-3h", out ty, out kk) && !ParseSlotKey("task:abc", out ty, out kk));
            int ship;
            True("legacy cap uid", IsLegacyCapHangarUid(-(200 * 10000 + 1 * 100 + 1), out ship) && ship == 200);
            True("legacy cap uid not slot/task", !IsLegacyCapHangarUid(a, out ship) && !IsLegacyCapHangarUid(TaskUid("task:abc"), out ship) && !IsLegacyCapHangarUid(5, out ship));
            Func<int, string, int, long, int, SlotClaim> C = (u, g, seq, j, k) => new SlotClaim { Uid = u, Group = g, Seq = seq, JoinedAt = j, K = k };
            var r = RebindSlots(5, new[] { C(1, "A", 0, 1, 1), C(3, "A", 0, 2, 3), C(5, "A", 0, 3, 5) });
            True("rebind keeps", r.Count == 0);
            r = RebindSlots(4, new[] { C(1, "A", 0, 1, 1), C(3, "A", 0, 3, 3) });
            True("rebind order middle", r.Count == 0);
            r = RebindSlots(4, new[] { C(1, "A", 0, 1, 1), C(2, "A", 0, 2, 2), C(4, "A", 0, 4, 4), C(5, "A", 0, 5, 5) });
            True("rebind order middle full", r.Count == 1 && r[0].Uid == 5 && r[0].FromK == 5 && r[0].ToK == 3 && !r[0].Drop);
            r = RebindSlots(4, new[] { C(2, "A", 0, 1, 2), C(5, "A", 0, 2, 5) });
            True("rebind outside", r.Count == 1 && r[0].Uid == 5 && r[0].ToK == 1);
            r = RebindSlots(3, new[] { C(1, "A", 0, 1, 1), C(2, "A", 0, 2, 2), C(3, "A", 0, 3, 3), C(4, "B", 1, 4, 4), C(5, "B", 1, 5, 5) });
            True("rebind drop", r.Count == 2 && r[0].Uid == 4 && r[0].Drop && r[0].ToK == 0 && r[1].Uid == 5 && r[1].Drop);
            r = RebindSlots(4, new[] { C(1, "A", 0, 1, 1), C(2, "A", 0, 2, 2), C(3, "A", 0, 3, 3) });
            True("rebind growth", r.Count == 0);
            r = RebindSlots(3, new[] { C(20, "A", 0, 1, 2), C(21, "B", 1, 2, 2) });
            True("rebind duplicate", r.Count == 1 && r[0].Uid == 21 && r[0].FromK == 2 && r[0].ToK == 1);
            r = RebindSlots(0, new[] { C(1, "A", 0, 1, 1), C(2, "A", 0, 2, 2) });
            True("rebind none", r.Count == 2 && r[0].Drop && r[1].Drop);
            r = RebindSlots(40, new[] { C(1, "A", 0, 1, 17) });
            True("rebind max", r.Count == 1 && r[0].ToK == 1);
            // A3 (a): two windows on one pair, n 3 -> 2: the one who took last loses
            Func<int, string, int, long, int, SlotClaim> W = (u, w, seq, j, k) => new SlotClaim { Uid = u, Window = w, Group = w + seq, Seq = seq, JoinedAt = j, K = k, DeckUid = 9, Type = "T" };
            r = RebindSlots(2, new[] { W(1, "cap", 1, 1, 1), W(2, "asw", 2, 2, 2), W(3, "cap", 1, 3, 3) });
            True("order first taken a", r.Count == 1 && r[0].Uid == 3 && r[0].Drop && r[0].Window == "cap");
            r = RebindSlots(2, new[] { W(1, "cap", 1, 1, 1), W(2, "asw", 2, 3, 2), W(3, "cap", 1, 2, 3) });
            True("order first taken b", r.Count == 2 && r[0].Uid == 3 && r[0].ToK == 2 && !r[0].Drop && r[1].Uid == 2 && r[1].Drop && r[1].Window == "asw");
            r = RebindSlots(2, new[] { W(1, "cap", 2, 0, 1), W(2, "asw", 1, 0, 2), W(3, "cap", 1, 0, 3) });
            True("order joined zero", r.Count == 2 && r[0].Uid == 3 && r[0].ToK == 1 && r[1].Uid == 1 && r[1].Drop);
            // (g) Pinned
            var pin = W(2, "asw", 2, 9, 2); pin.Pinned = true;
            r = RebindSlots(2, new[] { W(1, "cap", 1, 1, 1), pin, W(3, "cap", 1, 2, 3) });
            True("pinned stays", r.Count == 1 && r[0].Uid == 3 && r[0].Drop);
            var pin3 = W(3, "asw", 2, 1, 3); pin3.Pinned = true;
            r = RebindSlots(2, new[] { W(1, "cap", 1, 1, 1), W(2, "cap", 1, 2, 2), pin3 });
            True("pinned outside untouched", r.Count == 0);
            // (d) 5 -> 4 with the middle slot ordered away: only k5 moves, the other window's k4 stays
            r = RebindSlots(4, new[] { W(1, "cap", 1, 1, 1), W(2, "cap", 1, 2, 2), W(4, "asw", 2, 4, 4), W(5, "cap", 1, 5, 5) });
            True("order middle other window", r.Count == 1 && r[0].Uid == 5 && r[0].ToK == 3 && r[0].ToUid == HangarSlotUid(9, "T", 3) && r[0].N == 4 && r[0].DeckUid == 9);
            // (e) other policies
            var set = new[] { W(1, "cap", 2, 1, 1), W(2, "asw", 1, 5, 2), W(3, "cap", 2, 3, 3) };
            r = RebindSlots(2, set, SlotOrder.GroupSeq);
            True("order group seq", r.Count == 1 && r[0].Uid == 3 && r[0].Drop);
            r = RebindSlots(2, new[] { W(1, "asw", 1, 1, 1), W(2, "cap", 2, 5, 2), W(3, "asw", 1, 3, 3) }, SlotOrder.CapFirst);
            True("order cap first", r.Count == 1 && r[0].Uid == 3 && r[0].Drop);
            return _err;
        }

        public static string SelfTest()
        {
            _err = null;
            Eq("m real", TimingsMultiplier("Realistic"), 1); Eq("m casual", TimingsMultiplier("Casual"), 5);
            Eq("m arcade", TimingsMultiplier("Arcade"), 10); Eq("m cheater", TimingsMultiplier("Cheater"), 1e6);
            Eq("m unknown", TimingsMultiplier("???"), 1); Eq("m null", TimingsMultiplier(null), 1);
            var l15 = Lo("A", 8, true, 15, 20);
            Eq("pending real", PendingRemainS(1800, "Realistic", 0), 1800); Eq("pending crew", PendingRemainS(1800, "Realistic", 2), 900);
            Eq("pending casual", PendingRemainS(1800, "Casual", 1), 360); Eq("pending done", PendingRemainS(-1, "Realistic", 1), 0); Eq("pending nan", PendingRemainS(double.NaN, "Arcade", 1), 0);
            Eq("t204 type vt", DeckTaskType("a", "b") == "a" ? 1 : 0, 1); Eq("t204 type ini", DeckTaskType(null, "b") == "b" ? 1 : 0, 1);
            Eq("t204 type none", DeckTaskType("", "") == null ? 1 : 0, 1);
            Eq("hprep real", HangarPrepS(900, "Realistic"), 900); Eq("hprep casual", HangarPrepS(900, "Casual"), 180);
            Eq("hprep zero", HangarPrepS(0, "Realistic"), 900); Eq("hprep neg arcade", HangarPrepS(-1, "Arcade"), 90);
            foreach (var hm in new[] { "Realistic", "Casual", "Arcade", "Cheater" }) Eq("hprep = ready " + hm, HangarPrepS(l15.ReadyUpMin * 60, hm), ReadyS(l15, hm, 0));
            Eq("ready 1", ReadyS(l15, "Realistic", 0), 900); Eq("ready 5", ReadyS(l15, "Casual", 0), 180);
            Eq("ready 10", ReadyS(l15, "Arcade", 0), 90); Eq("ready wait", ReadyS(l15, "Casual", 25), 205);
            Eq("cool 1", CoolDownS(l15, "Realistic"), 1200); Eq("cool 5", CoolDownS(l15, "Casual"), 240);
            var nxt = Lo("B", 8, true, 10, 5);
            Eq("turnaround", TurnaroundS(l15, nxt, "Realistic", 100, 50, 200), 100 + 1200 + 600 + 50 + 200);
            Eq("turnaround m5", TurnaroundS(l15, nxt, "Casual", 100, 50, 200), 100 + 240 + 120 + 50 + 200);

            Func<LoadoutOption, double> cap = x => x.Capacity;
            var los = new List<LoadoutOption> { Lo("ASW", 2), Lo("Transport", 8), Lo("Ferry", 8), Lo("Empty", 8), Lo("Alpha", 8) };
            True("best tie order", PickBest(los, cap, "Empty,Ferry,Transport,Default", null) == 3);
            True("best skip", PickBest(los, cap, "Empty,Ferry,Transport,Default", new HashSet<string> { "Empty" }) == 2);
            True("best tie name", PickBest(los, cap, "", null) == 4);
            True("best zero", PickBest(new List<LoadoutOption> { Lo("X", 0) }, cap, "", null) == -1);
            True("best empty", PickBest(new List<LoadoutOption>(), cap, "", null) == -1);
            True("best higher", PickBest(new List<LoadoutOption> { Lo("A", 2), Lo("B", 3) }, cap, "A", null) == 1);

            var gs = new List<AirGroupPrio>
            {
                new AirGroupPrio { Name = "G3", Order = "asw", HasContact = false, WindowIndex = 0 },
                new AirGroupPrio { Name = "G2", Order = "sar", HasContact = true, WindowIndex = 1 },
                new AirGroupPrio { Name = "G1", Order = "asw", HasContact = true, WindowIndex = 3 },
                new AirGroupPrio { Name = "G0", Order = "asw", HasContact = true, WindowIndex = 2 },
            };
            var ord = DeckOrder(gs);
            True("deckorder", string.Join(",", ord) == "G0,G1,G3,G2");

            // ADR-0041: CAP gap under threat first
            var capT = new AirGroupPrio { Name = "C1", Order = "cap", CapGapUnderThreat = true, WindowIndex = 5 };
            var capN = new AirGroupPrio { Name = "C2", Order = "cap", CapGapUnderThreat = false, WindowIndex = 0 };
            var aswC = new AirGroupPrio { Name = "A1", Order = "asw", HasContact = true, WindowIndex = 1 };
            True("cap>asw", string.Join(",", DeckOrder(new[] { aswC, capT })) == "C1,A1");
            True("nocap<asw", string.Join(",", DeckOrder(new[] { capN, aswC })) == "A1,C2");
            var capT2 = new AirGroupPrio { Name = "C3", Order = "cap", CapGapUnderThreat = true, WindowIndex = 2 };
            True("two caps", string.Join(",", DeckOrder(new[] { capT, capT2, aswC })) == "C3,C1,A1");

            // T-119: Tier / DeckOrder ties / FlushOrder / PrioritiseCalls
            True("tier cap flag", Tier(new AirGroupPrio { Order = "cap", CapGapUnderThreat = true }) == 0);
            True("tier asw contact", Tier(new AirGroupPrio { Order = "asw", HasContact = true }) == 1);
            True("tier sar contact", Tier(new AirGroupPrio { Order = "sar", HasContact = true }) == 2);
            True("tier cap noflag", Tier(new AirGroupPrio { Order = "cap" }) == 2);
            True("tier asw plain", Tier(new AirGroupPrio { Order = "asw" }) == 2);
            var tw1 = new AirGroupPrio { Name = "Z", Order = "asw", WindowIndex = 4, Window = "cap" };
            var tw2 = new AirGroupPrio { Name = "A", Order = "asw", WindowIndex = 4, Window = "asw" };
            var tw3 = new AirGroupPrio { Name = "B", Order = "asw", WindowIndex = 4, Window = "asw" };
            True("deckorder tie window/name", string.Join(",", DeckOrder(new[] { tw1, tw3, tw2 })) == "A,B,Z");
            Func<int, int, int, int, string, string, DeckReqKey> rk = (tier, seq, kind, tk, w, gr) => new DeckReqKey { Tier = tier, Seq = seq, Kind = kind, Ticket = tk, Window = w, Group = gr };
            var fl = new List<DeckReqKey> { rk(2, 1, 2, 1, "asw", "G"), rk(2, 1, 1, 2, "asw", "G"), rk(2, 1, 0, 3, "asw", "G") };
            True("flush kind", string.Join(",", FlushOrder(fl)) == "2,1,0");
            fl = new List<DeckReqKey> { rk(2, 1, 2, 1, "asw", "A"), rk(0, 9, 2, 2, "cap", "C") };
            True("flush tier>seq", string.Join(",", FlushOrder(fl)) == "1,0");
            fl = new List<DeckReqKey> { rk(2, 5, 2, 1, "asw", "A"), rk(2, 3, 2, 2, "cap", "C") };
            True("flush seq>window", string.Join(",", FlushOrder(fl)) == "1,0");
            fl = new List<DeckReqKey> { rk(2, 3, 2, 1, "cap", "C"), rk(2, 3, 2, 2, "asw", "A") };
            True("flush window", string.Join(",", FlushOrder(fl)) == "1,0");
            fl = new List<DeckReqKey> { rk(2, 3, 2, 7, "asw", "A"), rk(2, 3, 2, 4, "asw", "A") };
            True("flush ticket", string.Join(",", FlushOrder(fl)) == "1,0");
            // no flags = the old DeckOrder
            True("no flags = deckorder", string.Join(",", DeckOrder(gs)) == "G0,G1,G3,G2");
            Func<string, string, AirDeckTask> tk2 = (k, st) => new AirDeckTask { Key = k, State = st };
            Func<string, int> rankC = k => k.StartsWith("c") ? 0 : -1;
            Func<string, bool> yA = k => k.StartsWith("a");
            True("prio one", string.Join(",", PrioritiseCalls(new[] { tk2("a1", "ready_up"), tk2("c1", "await_deck") }, rankC, yA)) == "c1");
            True("prio two reverse", string.Join(",", PrioritiseCalls(new[] { tk2("a1", "ready_up"), tk2("c1", "await_deck"), tk2("c2", "ready_up") }, rankC, yA)) == "c2,c1");
            True("prio spawn skipped", PrioritiseCalls(new[] { tk2("a1", "ready_up"), tk2("c1", "await_spawn") }, rankC, yA).Count == 0);
            True("prio asw behind", PrioritiseCalls(new[] { tk2("c1", "ready_up"), tk2("a1", "ready_up") }, rankC, yA).Count == 0);
            True("prio foreign not returned", string.Join(",", PrioritiseCalls(new[] { tk2("x1", "ready_up"), tk2("a1", "ready_up"), tk2("c1", "await_crew") }, rankC, yA)) == "c1");
            True("prio only foreign before", PrioritiseCalls(new[] { tk2("x1", "ready_up"), tk2("c1", "await_crew") }, rankC, yA).Count == 0);
            Func<string, int> rank2 = k => k == "c1" ? 1 : k == "c2" ? 0 : -1;
            True("prio rank order", string.Join(",", PrioritiseCalls(new[] { tk2("a1", "ready_up"), tk2("c1", "ready_up"), tk2("c2", "ready_up") }, rank2, yA)) == "c1,c2");
            True("prio empty", PrioritiseCalls(null, rankC, yA).Count == 0);
            var memoErr = TickMemo<List<object>>.SelfTest();
            if (memoErr != null && _err == null) _err = "memo: " + memoErr;
            // T-231: the launch record of a type (a pair of records: the machines are in the second)
            int sq;
            Eq("t231 pick 2nd", PickLaunchRecord(new List<VtRecFacts> { new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 0 } }, new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 1 } } }, out sq), 1);
            Eq("t231 pick squadron", sq, 0);
            Eq("t231 pick none", PickLaunchRecord(new List<VtRecFacts> { new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 0 } }, new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 0, 0 } } }, out sq), -1);
            Eq("t231 pick no loadout first", PickLaunchRecord(new List<VtRecFacts> { new VtRecFacts { HasLoadout = false, SquadronNumbers = new[] { 2 } }, new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 0, 3 } } }, out sq), 1);
            Eq("t231 pick second squadron", sq, 1);
            Eq("t231 pick first wins", PickLaunchRecord(new List<VtRecFacts> { new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 1 } }, new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 5 } } }, out sq), 0);
            Eq("t231 pick loadout only in 2nd, machines only in 1st", PickLaunchRecord(new List<VtRecFacts> { new VtRecFacts { HasLoadout = false, SquadronNumbers = new[] { 2 } }, new VtRecFacts { HasLoadout = true, SquadronNumbers = new[] { 0 } } }, out sq), -1);
            Eq("t231 pick null", PickLaunchRecord(null, out sq), -1);
            if (_err == null) _err = SlotSelfTest();
            return _err;
        }
    }

    /// <summary>
    /// T-126: one cached scan per air tick. Inside Begin/End the first Get runs fresh() and the next ones return the same value;
    /// outside a tick every Get runs fresh() (commands and windows see the live game). End drops the value (no objects of the game are kept between ticks).
    /// </summary>
    internal sealed class TickMemo<T> where T : class
    {
        private bool _on;
        private T _value;

        public int Fresh { get; private set; }

        public void Begin() { _on = true; _value = null; }

        public void End() { _on = false; _value = null; }

        public T Get(Func<T> fresh)
        {
            if (!_on) { Fresh++; return fresh(); }
            if (_value == null) { Fresh++; _value = fresh(); }
            return _value;
        }

        public static string SelfTest()
        {
            var m = new TickMemo<List<object>>();
            Func<List<object>> f = () => new List<object>();
            var a = m.Get(f); var b = m.Get(f);
            if (ReferenceEquals(a, b) || m.Fresh != 2) return "outside a tick: every call fresh";
            m.Begin();
            var c = m.Get(f); var d = m.Get(f);
            if (!ReferenceEquals(c, d) || m.Fresh != 3) return "inside a tick: one fresh";
            m.End();
            var e = m.Get(f);
            if (ReferenceEquals(e, c) || m.Fresh != 4) return "after End: dropped";
            m.End();   // End without Begin is safe
            m.Begin(); m.Begin();
            var g = m.Get(f);
            if (ReferenceEquals(g, c) || m.Fresh != 5) return "second Begin: new value";
            m.End();
            return null;
        }
    }
}
