// T-112 (ADR-0040 §11): one machine = one automation. NOT under CAP_WIP: AswController references it (stream X, step X3).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed class AirOwnership
    {
        private sealed class Claimed { public string Window, Group; }

        private readonly Dictionary<int, Claimed> _claims = new Dictionary<int, Claimed>();
        private readonly List<KeyValuePair<string, Func<int, string>>> _resolvers = new List<KeyValuePair<string, Func<int, string>>>();
        private readonly Dictionary<string, int> _seq = new Dictionary<string, int>();
        private int _next = 1;
        private readonly HangarSlotBook _slots = new HangarSlotBook();

        /// <summary>T-186 (ADR-0082): the hangar slots of both windows, one rebinding.</summary>
        public HangarSlotBook Slots { get { return _slots; } }

        /// <summary>null = ok | "in_group:&lt;name&gt;" when another group/window owns the unit.</summary>
        public string Claim(int uid, string window, string group)
        {
            string ow;
            string owner = Owner(uid, out ow);
            if (owner != null && !(ow == window && owner == group)) return "in_group:" + owner;
            _claims[uid] = new Claimed { Window = window, Group = group };
            return null;
        }

        /// <summary>Group name or null; looks at claims, then at registered resolvers.</summary>
        public string Owner(int uid, out string window)
        {
            Claimed c;
            if (_claims.TryGetValue(uid, out c)) { window = c.Window; return c.Group; }
            foreach (var r in _resolvers)
            {
                string g = null;
                try { g = r.Value(uid); } catch (Exception) { }
                if (g != null) { window = r.Key; return g; }
            }
            window = null;
            return null;
        }

        public void Release(int uid) { _claims.Remove(uid); }

        public void ReleaseGroup(string window, string group)
        {
            var del = new List<int>();
            foreach (var kv in _claims) if (kv.Value.Window == window && kv.Value.Group == group) del.Add(kv.Key);
            foreach (int u in del) _claims.Remove(u);
        }

        public void Clear() { _claims.Clear(); }

        /// <summary>External owners that do not Claim (ASW/SAR groups live in AswController).</summary>
        public void Register(string window, Func<int, string> groupOf)
        {
            for (int i = 0; i < _resolvers.Count; i++)
                if (_resolvers[i].Key == window) { _resolvers.RemoveAt(i); break; }
            if (groupOf != null) _resolvers.Add(new KeyValuePair<string, Func<int, string>>(window, groupOf));
        }

        /// <summary>Global creation number of a group shared by both windows; first sight assigns the next number.</summary>
        public int GroupSeq(string window, string group)
        {
            string k = window + "\n" + group;
            int n;
            if (!_seq.TryGetValue(k, out n)) { n = _next++; _seq[k] = n; }
            return n;
        }

        /// <summary>T-150: a group was renamed; it keeps its creation number. Nothing to move when the old name was never numbered.</summary>
        public void RenameSeq(string window, string oldName, string newName)
        {
            string ok = window + "\n" + oldName, nk = window + "\n" + newName;
            int n;
            if (!_seq.TryGetValue(ok, out n)) return;
            _seq.Remove(ok);
            _seq[nk] = n;
        }

        /// <summary>T-326: claims of a renamed group move to the new name (CAP claims by group name).</summary>
        public void RenameClaims(string window, string oldName, string newName)
        {
            foreach (var kv in _claims)
                if (kv.Value.Window == window && kv.Value.Group == oldName) kv.Value.Group = newName;
        }

        /// <summary>Number from a save: seq &lt;= 0 is ignored (old save); otherwise it is set (overwrites) and the next number is max(_next, seq + 1).
        /// Duplicates are allowed: ties are decided by (seq, window, name).</summary>
        public void RestoreSeq(string window, string group, int seq)
        {
            if (seq <= 0) return;
            _seq[window + "\n" + group] = seq;
            if (seq >= _next) _next = seq + 1;
        }

        /// <summary>null = ok, otherwise error text.</summary>
        public static string SelfTest()
        {
            var o = new AirOwnership();
            string w;
            if (o.Claim(1, "cap", "ПВО-1") != null) return "claim free";
            if (o.Claim(1, "cap", "ПВО-1") != null) return "claim same again";
            if (o.Claim(1, "cap", "ПВО-2") != "in_group:ПВО-1") return "claim other group";
            if (o.Claim(1, "asw", "ПЛО-1") != "in_group:ПВО-1") return "claim other window";
            if (o.Owner(1, out w) != "ПВО-1" || w != "cap") return "owner";
            if (o.Owner(2, out w) != null || w != null) return "owner free";
            o.Register("asw", uid => uid == 7 ? "ПЛО-1" : null);
            if (o.Owner(7, out w) != "ПЛО-1" || w != "asw") return "resolver owner";
            if (o.Claim(7, "cap", "ПВО-1") != "in_group:ПЛО-1") return "claim vs resolver";
            if (o.Claim(7, "asw", "ПЛО-1") != null) return "claim own resolver group";
            o.Release(7);
            o.Register("asw", uid => uid == 8 ? "ПЛО-2" : null);
            if (o.Owner(7, out w) != null || o.Owner(8, out w) != "ПЛО-2") return "register replaces";
            o.Release(1);
            if (o.Owner(1, out w) != null) return "release";
            o.Claim(3, "cap", "ПВО-1"); o.Claim(4, "cap", "ПВО-1"); o.Claim(5, "cap", "ПВО-2");
            o.ReleaseGroup("cap", "ПВО-1");
            if (o.Owner(3, out w) != null || o.Owner(4, out w) != null || o.Owner(5, out w) != "ПВО-2") return "release group";
            o.Clear();
            if (o.Owner(5, out w) != null) return "clear";
            var s = new AirOwnership();
            int a = s.GroupSeq("cap", "ПВО-1"), b = s.GroupSeq("asw", "ПЛО-1"), c = s.GroupSeq("cap", "ПВО-2");
            if (a != 1 || b != 2 || c != 3) return "seq order " + a + b + c;
            if (s.GroupSeq("cap", "ПВО-1") != 1 || s.GroupSeq("asw", "ПЛО-1") != 2) return "seq stable";
            if (s.GroupSeq("cap", "ПЛО-1") != 4) return "seq window distinct";
            var r = new AirOwnership();
            r.RestoreSeq("cap", "ПВО-1", 5);
            if (r.GroupSeq("cap", "ПВО-1") != 5) return "restore seq";
            if (r.GroupSeq("asw", "ПЛО-1") != 6) return "restore next";
            r.RestoreSeq("cap", "ПВО-2", 0); r.RestoreSeq("cap", "ПВО-2", -3);
            if (r.GroupSeq("cap", "ПВО-2") != 7) return "restore ignores <=0";
            r.RestoreSeq("asw", "ПЛО-1", 2);
            if (r.GroupSeq("asw", "ПЛО-1") != 2) return "restore overwrites";
            if (r.GroupSeq("cap", "ПВО-3") != 8) return "restore keeps next";
            r.RestoreSeq("cap", "ПВО-4", 3);
            if (r.GroupSeq("cap", "ПВО-4") != 3 || r.GroupSeq("cap", "ПВО-5") != 9) return "restore duplicate ok";
            r.RenameSeq("asw", "ПЛО-1", "Конвой");
            if (r.GroupSeq("asw", "Конвой") != 2) return "rename keeps seq";
            r.RenameSeq("asw", "нет", "Другой");
            if (r.GroupSeq("asw", "Другой") != 10) return "rename of unnumbered";
            var q = new AirOwnership();
            q.Claim(1, "cap", "ПВО-1"); q.Claim(2, "asw", "ПВО-1");
            q.RenameClaims("cap", "ПВО-1", "Альфа");
            if (q.Owner(1, out w) != "Альфа") return "rename claims owner";
            if (q.Claim(1, "cap", "Альфа") != null) return "rename claims new";
            if (q.Claim(1, "cap", "ПВО-1") != "in_group:Альфа") return "rename claims old";
            if (q.Owner(2, out w) != "ПВО-1" || w != "asw") return "rename claims other window";
            return HangarSlotBook.SelfTest();
        }
    }

    /// <summary>
    /// T-186 (ADR-0082 §2): one book of hangar slot claims for both windows. Holders (cap, asw) register a claims provider and an apply callback;
    /// <see cref="Rebind"/> groups the claims by (deck, type) and rebinds them with AirOpsMath.RebindSlots, one rule for both windows.
    /// A drop happens after AbortTicks ticks of shortage in a row. No Unity, main thread only.
    /// </summary>
    internal sealed class HangarSlotBook
    {
        internal const int AbortTicks = 3;

        private sealed class Holder
        {
            public string Window;
            public Func<IEnumerable<AirOpsMath.SlotClaim>> Claims;
            public Action<AirOpsMath.SlotMove> Apply;
            public Func<int, string, int> Numbers;
        }

        private readonly List<Holder> _holders = new List<Holder>();
        private readonly Dictionary<string, int> _short = new Dictionary<string, int>();   // window + "\n" + uid -> ticks of shortage
        private readonly Dictionary<string, int> _lastN = new Dictionary<string, int>();   // deck + "|" + type -> n of the last rebind
        private long _join;
        private bool _hasT;
        private double _lastT;

        public Action<string> Log;

        public void Register(string window, Func<IEnumerable<AirOpsMath.SlotClaim>> claims, Action<AirOpsMath.SlotMove> apply, Func<int, string, int> numbers)
        {
            Unregister(window);
            _holders.Add(new Holder { Window = window, Claims = claims, Apply = apply, Numbers = numbers });
        }

        public void Unregister(string window)
        {
            for (int i = 0; i < _holders.Count; i++)
                if (_holders[i].Window == window) { _holders.RemoveAt(i); return; }
        }

        /// <summary>Next global "taken at" number: 1, 2, 3... (0 = restored from a save).</summary>
        public long NextJoin() { return ++_join; }

        private static string ShortKey(string window, int uid) { return window + "\n" + uid.ToString(CultureInfo.InvariantCulture); }

        private static string PairKey(int deck, string type) { return deck.ToString(CultureInfo.InvariantCulture) + "|" + type; }

        /// <summary>Ticks of shortage in a row of the claim (0 = none).</summary>
        public int Short(string window, int uid)
        {
            int n;
            return _short.TryGetValue(ShortKey(window, uid), out n) ? n : 0;
        }

        private List<AirOpsMath.SlotClaim> AllClaims()
        {
            var res = new List<AirOpsMath.SlotClaim>();
            foreach (var h in _holders.ToArray())
            {
                IEnumerable<AirOpsMath.SlotClaim> cl = null;
                try { cl = h.Claims == null ? null : h.Claims(); } catch (Exception) { }
                if (cl == null) continue;
                foreach (var c in cl) { if (c.Window == null) c.Window = h.Window; res.Add(c); }
            }
            return res;
        }

        /// <summary>A claim of any holder on slot k of (deck, type): the window and group.</summary>
        public bool Held(int deckUid, string type, int k, out string window, out string group)
        {
            window = null; group = null;
            foreach (var c in AllClaims())
                if (c.DeckUid == deckUid && c.Type == type && c.K == k) { window = c.Window; group = c.Group; return true; }
            return false;
        }

        private static int SafeNumbers(Func<int, string, int> f, int deck, string type)
        {
            try { return f(deck, type); } catch (Exception) { return -1; }
        }

        private static List<string> GroupByPair(List<AirOpsMath.SlotClaim> all, Dictionary<string, List<AirOpsMath.SlotClaim>> byPair)
        {
            var pairs = new List<string>();
            foreach (var c in all)
            {
                string pk = PairKey(c.DeckUid, c.Type);
                List<AirOpsMath.SlotClaim> l;
                if (!byPair.TryGetValue(pk, out l)) { byPair[pk] = l = new List<AirOpsMath.SlotClaim>(); pairs.Add(pk); }
                l.Add(c);
            }
            return pairs;
        }

        /// <summary>
        /// Rebind every (deck, type) pair. numbers (the game) wins over the answers of the holders (max of those &gt;= 0; -1 = "I do not see the pair");
        /// a pair nobody sees is skipped. Same t twice without force = nothing. force (after Load / remove) applies moves but does not count shortage or drop.
        /// A move into a slot still held by a pending drop waits for the next tick. Returns the number of applied moves and drops.
        /// </summary>
        public int Rebind(double t, Func<int, string, int> numbers = null, bool force = false)
        {
            if (!force)
            {
                if (_hasT && t == _lastT) return 0;
                _hasT = true; _lastT = t;
            }
            var all = AllClaims();
            if (all.Count == 0 && _short.Count == 0) return 0;
            var byPair = new Dictionary<string, List<AirOpsMath.SlotClaim>>();
            var pairs = GroupByPair(all, byPair);
            int applied = 0;
            var live = new HashSet<string>();
            foreach (var pk in pairs)
            {
                var claims = byPair[pk];
                int deck = claims[0].DeckUid; string type = claims[0].Type;
                int n = -1;
                if (numbers != null) n = SafeNumbers(numbers, deck, type);
                else
                    foreach (var h in _holders.ToArray())
                        if (h.Numbers != null) n = Math.Max(n, SafeNumbers(h.Numbers, deck, type));
                if (n < 0)
                {
                    foreach (var c in claims) live.Add(ShortKey(c.Window, c.Uid));   // pair not seen: nothing moves, counters kept
                    continue;
                }
                _lastN[pk] = n;
                var moves = AirOpsMath.RebindSlots(n, claims);
                foreach (var mv in moves)
                {
                    if (!mv.Drop) continue;
                    string sk = ShortKey(mv.Window, mv.Uid);
                    live.Add(sk);
                    if (force) continue;
                    int cnt; _short.TryGetValue(sk, out cnt);
                    _short[sk] = cnt + 1;
                }
                var occupied = new HashSet<int>();
                foreach (var c in claims) occupied.Add(c.K);
                var done = new List<AirOpsMath.SlotMove>();
                foreach (var mv in moves)   // drops first: they free the slots the moves may want
                    if (mv.Drop && !force && _short[ShortKey(mv.Window, mv.Uid)] >= AbortTicks) { occupied.Remove(mv.FromK); done.Add(mv); }
                foreach (var mv in moves)
                {
                    if (mv.Drop || occupied.Contains(mv.ToK)) continue;
                    occupied.Remove(mv.FromK); occupied.Add(mv.ToK);
                    done.Add(mv);
                }
                int drops = 0;
                foreach (var mv in done)
                {
                    if (mv.Drop) { _short.Remove(ShortKey(mv.Window, mv.Uid)); drops++; }
                    Holder h = null;
                    foreach (var x in _holders) if (x.Window == mv.Window) { h = x; break; }
                    if (h != null && h.Apply != null) { try { h.Apply(mv); } catch (Exception) { } }
                    applied++;
                }
                if (done.Count > 0 && Log != null)
                {
                    try { Log("air slots deck=" + deck + " type=" + type + " n=" + n + " moves=" + (done.Count - drops) + " drops=" + drops); } catch (Exception) { }
                }
            }
            if (!force)
            {
                var stale = new List<string>();
                foreach (var kv in _short) if (!live.Contains(kv.Key)) stale.Add(kv.Key);
                foreach (var k in stale) _short.Remove(k);
            }
            return applied;
        }

        /// <summary>Shortage counters of one window (Load / ClearAll / mission change).</summary>
        public void Forget(string window)
        {
            var del = new List<string>();
            string pre = window + "\n";
            foreach (var kv in _short) if (kv.Key.StartsWith(pre, StringComparison.Ordinal)) del.Add(kv.Key);
            foreach (var k in del) _short.Remove(k);
        }

        /// <summary>New mission: counters, join number, last tick and last n are cleared; holders stay registered.</summary>
        public void Reset()
        {
            _short.Clear(); _lastN.Clear(); _join = 0; _hasT = false; _lastT = 0;
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append(' ');
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>{"pairs":[{"deck","type","n","claims":[{"k","uid","window","group","joined","pinned"}]}]}; n of the last rebind (-1 = none yet), no game calls.</summary>
        public string DiagJson(int limit)
        {
            var byPair = new Dictionary<string, List<AirOpsMath.SlotClaim>>();
            var pairs = GroupByPair(AllClaims(), byPair);
            var sb = new StringBuilder("{\"pairs\":[");
            int shown = 0;
            foreach (var pk in pairs)
            {
                if (shown >= limit) break;
                var l = byPair[pk];
                l.Sort((a, b) => a.K.CompareTo(b.K));
                int n; if (!_lastN.TryGetValue(pk, out n)) n = -1;
                if (shown++ > 0) sb.Append(',');
                sb.Append("{\"deck\":").Append(l[0].DeckUid).Append(",\"type\":\"").Append(Esc(l[0].Type)).Append("\",\"n\":").Append(n).Append(",\"claims\":[");
                for (int i = 0; i < l.Count; i++)
                {
                    var c = l[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"k\":").Append(c.K).Append(",\"uid\":").Append(c.Uid).Append(",\"window\":\"").Append(Esc(c.Window))
                      .Append("\",\"group\":\"").Append(Esc(c.Group)).Append("\",\"joined\":").Append(c.JoinedAt)
                      .Append(",\"pinned\":").Append(c.Pinned ? "true" : "false").Append('}');
                }
                sb.Append("]}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>null = ok, otherwise error text.</summary>
        public static string SelfTest()
        {
            var book = new HangarSlotBook();
            var capC = new List<AirOpsMath.SlotClaim>(); var aswC = new List<AirOpsMath.SlotClaim>();
            var capMoves = new List<AirOpsMath.SlotMove>(); var aswMoves = new List<AirOpsMath.SlotMove>();
            int capN = 3, aswN = -1;
            book.Register("cap", () => capC, mv => capMoves.Add(mv), (d, t) => capN);
            book.Register("asw", () => aswC, mv => aswMoves.Add(mv), (d, t) => aswN);
            if (book.NextJoin() != 1 || book.NextJoin() != 2) return "next join";
            Func<int, string, int, long, int, AirOpsMath.SlotClaim> C = (u, w, seq, j, k) =>
                new AirOpsMath.SlotClaim { Uid = u, Window = w, Group = w + seq, Seq = seq, JoinedAt = j, K = k, DeckUid = 9, Type = "T" };
            capC.Add(C(1, "cap", 1, 1, 1)); aswC.Add(C(2, "asw", 2, 2, 2)); capC.Add(C(3, "cap", 1, 3, 3));
            string w, g;
            if (!book.Held(9, "T", 2, out w, out g) || w != "asw" || g != "asw2") return "held foreign";
            if (book.Held(9, "T", 4, out w, out g) || book.Held(8, "T", 2, out w, out g)) return "held free";
            if (book.Rebind(1) != 0) return "rebind nothing to do";
            capN = 2;   // shrink: cap k3 took last
            if (book.Rebind(2) != 0 || book.Short("cap", 3) != 1) return "shortage 1";
            if (book.Rebind(2) != 0 || book.Short("cap", 3) != 1) return "same t twice";
            if (book.Rebind(3, null, true) != 0 || book.Short("cap", 3) != 1) return "force keeps counter";
            if (book.Rebind(3) != 0 || book.Short("cap", 3) != 2) return "shortage 2";
            string log = null; book.Log = s => log = s;
            if (book.Rebind(4) != 1 || capMoves.Count != 1 || !capMoves[0].Drop || capMoves[0].Uid != 3 || aswMoves.Count != 0) return "drop on 3rd tick";
            if (log != "air slots deck=9 type=T n=2 moves=0 drops=1") return "log " + log;
            if (book.Short("cap", 3) != 0) return "counter cleared after drop";
            capC.RemoveAt(1);   // the window dropped the member
            log = null; book.Rebind(5);
            if (log != null) return "no log without moves";
            // numbers from the holders: max of the answers >= 0
            aswN = 0; capN = -1;
            capMoves.Clear(); aswMoves.Clear(); capC.Clear();
            aswC.Clear(); aswC.Add(C(20, "asw", 1, 1, 1));
            book.Rebind(6); book.Rebind(7);
            if (book.Rebind(8) != 1 || aswMoves.Count != 1 || !aswMoves[0].Drop) return "max of holder answers";
            // external numbers win; -1 from outside skips the pair
            aswC.Clear(); aswC.Add(C(21, "asw", 1, 1, 5)); aswMoves.Clear();
            if (book.Rebind(9, (d, t) => -1) != 0) return "unseen pair skipped";
            if (book.Rebind(10, (d, t) => 3) != 1 || aswMoves.Count != 1 || aswMoves[0].ToK != 1 || aswMoves[0].ToUid != AirOpsMath.HangarSlotUid(9, "T", 1) || aswMoves[0].N != 3) return "external numbers + ToUid";
            // a move into a slot still held by a pending drop waits
            capC.Clear(); aswC.Clear(); capMoves.Clear(); aswMoves.Clear();
            capC.Add(C(31, "cap", 1, 1, 1)); aswC.Add(C(32, "asw", 2, 3, 2)); capC.Add(C(33, "cap", 1, 2, 3));
            capN = 2; aswN = 2;
            if (book.Rebind(20) != 0 || capMoves.Count != 0) return "move waits for a pending drop";
            if (book.Short("asw", 32) != 1) return "pending drop counted";
            book.Forget("cap");
            if (book.Short("asw", 32) != 1) return "forget keeps other window";
            book.Forget("asw");
            if (book.Short("asw", 32) != 0) return "forget asw";
            string d = book.DiagJson(1);
            if (!d.StartsWith("{\"pairs\":[{\"deck\":9,\"type\":\"T\",\"n\":2,\"claims\":[{\"k\":1,\"uid\":31,\"window\":\"cap\",\"group\":\"cap1\",\"joined\":1,\"pinned\":false}")) return "diag " + d;
            book.Reset();
            if (book.NextJoin() != 1 || book.DiagJson(0) != "{\"pairs\":[]}") return "reset";
            book.Register("cap", () => new List<AirOpsMath.SlotClaim>(), null, null);
            if (book.Held(9, "T", 1, out w, out g)) return "register replaces";
            book.Unregister("cap"); book.Unregister("asw");
            if (book.Held(9, "T", 2, out w, out g)) return "unregister";
            return null;
        }
    }
}
