using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-234 (stream A): the pure core of the "Orders:" label of a unit. Controllers of the mod report what they drive (a label per unit),
    /// the registry keeps the strongest label per unit, the text is built from ini keys and the writer plans the writes into the game's
    /// OrderFeedbackText (only on change, never over a foreign text). No Unity, no game types: tested by the stand `uatest`.
    /// The label has no group name (T-234-Q2): "ASW "Barrier": to line 2".
    /// </summary>
    internal static class UaPrio
    {
        public const int Evasion = 100, Retreat = 90, SmartAttack = 80, Intercept = 70, AswAttack = 60, Strike = 60, Sar = 50, Asw = 40, Cap = 40;
    }

    internal sealed class UnitActionLabel
    {
        public int Uid, Prio;
        public string Source;                 // evasion|sub_evasion|air_evasion|air_guard|smart_attack|intercept|asw_attack|asw_strike|sar|asw|cap
        public string CmdKey; public string[] CmdArgs;     // ua.cmd.*  ({0} = tactic name / track; no group name)
        public string PhaseKey; public string[] PhaseArgs; // ua.ph.*, null = no phase
    }

    internal interface IUnitActionSource
    {
        string Id { get; }
        /// <summary>Main thread. Adds one label per unit the source drives now; must not touch the game.</summary>
        void CollectActions(double nowS, IList<UnitActionLabel> into);
    }

    internal sealed class UnitActionRegistry
    {
        private readonly List<IUnitActionSource> _sources = new List<IUnitActionSource>();
        private readonly List<string> _ids = new List<string>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private readonly List<UnitActionLabel> _tmp = new List<UnitActionLabel>();
        private readonly Dictionary<int, UnitActionLabel> _best = new Dictionary<int, UnitActionLabel>();

        /// <summary>Order of Add = tie-break.</summary>
        public void Add(IUnitActionSource s) { if (s == null) return; _sources.Add(s); _ids.Add(s.Id); }

        public IList<string> Ids { get { return _ids; } }

        /// <summary>Highest Prio per uid; equal Prio -> earlier source. A throwing source is skipped, logWarn once per source id until it succeeds again.
        /// The returned dictionary is reused by the next call.</summary>
        public IDictionary<int, UnitActionLabel> Resolve(double nowS, Action<string> logWarn)
        {
            _best.Clear();
            for (int i = 0; i < _sources.Count; i++)
            {
                var s = _sources[i];
                _tmp.Clear();
                try { s.CollectActions(nowS, _tmp); }
                catch (Exception e)
                {
                    if (_warned.Add(_ids[i]) && logWarn != null)
                        try { logWarn("[UnitAction] source " + _ids[i] + " failed: " + e.GetType().Name + ": " + e.Message); } catch (Exception) { }
                    continue;
                }
                _warned.Remove(_ids[i]);
                for (int k = 0; k < _tmp.Count; k++)
                {
                    var l = _tmp[k];
                    if (l == null) continue;
                    UnitActionLabel cur;
                    if (!_best.TryGetValue(l.Uid, out cur) || l.Prio > cur.Prio) _best[l.Uid] = l;
                }
            }
            _tmp.Clear();
            return _best;
        }
    }

    internal static class UnitActionText
    {
        public const int MaxLen = 80;
        public const string Sep = " · ";

        /// <summary>"cmd: phase" through UiText.Fmt; no phase -> "cmd". Empty CmdKey -> "".</summary>
        public static string Format(UnitActionLabel l)
        {
            if (l == null || string.IsNullOrEmpty(l.CmdKey)) return "";
            string cmd = UiText.Fmt(l.CmdKey, l.CmdArgs ?? new string[0]);
            if (string.IsNullOrEmpty(l.PhaseKey)) return cmd;
            return cmd + ": " + UiText.Fmt(l.PhaseKey, l.PhaseArgs ?? new string[0]);
        }

        /// <summary>mode "replace": modText. mode "smart": modText + Sep + gameText when gameText is not empty, not in plain and not equal to modText. Clipped to MaxLen.</summary>
        public static string Compose(string modText, string gameText, ICollection<string> plain, string mode)
        {
            modText = modText ?? "";
            string g = gameText == null ? "" : gameText.Trim();
            if (mode == "replace" || g.Length == 0 || g == modText || (plain != null && plain.Contains(g))) return Clip(modText, MaxLen);
            return Clip(modText + Sep + g, MaxLen);
        }

        public static string Clip(string s, int max)
        {
            if (s == null) return "";
            if (max < 1 || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }
    }

    internal sealed class UnitActionWriter
    {
        public struct Write { public int Uid; public string Text; }

        private readonly Dictionary<int, string> _ours = new Dictionary<int, string>();
        private readonly List<int> _tmp = new List<int>();

        public int Owned { get { return _ours.Count; } }

        /// <summary>wanted uid -> text. feedback(uid): the current OrderFeedbackText, null = no unit.</summary>
        public IList<Write> Plan(IDictionary<int, string> wanted, Func<int, string> feedback)
        {
            var res = new List<Write>();
            if (wanted != null)
            {
                _tmp.Clear();
                foreach (var kv in wanted) _tmp.Add(kv.Key);
                _tmp.Sort();
                foreach (var uid in _tmp)
                {
                    string text = wanted[uid];
                    if (string.IsNullOrEmpty(text)) continue;
                    string cur = feedback(uid);
                    if (cur == null) { _ours.Remove(uid); continue; }                 // deck row / gone: no write
                    string mine;
                    bool isOurs = _ours.TryGetValue(uid, out mine) && cur == mine;
                    if (cur.Length == 0 || isOurs)
                    {
                        if (cur != text) res.Add(new Write { Uid = uid, Text = text });
                        _ours[uid] = text;
                    }
                    else _ours.Remove(uid);                                              // a foreign text: wait until the game clears it
                }
            }
            _tmp.Clear();
            foreach (var kv in _ours) if (wanted == null || !wanted.ContainsKey(kv.Key) || string.IsNullOrEmpty(wanted[kv.Key])) _tmp.Add(kv.Key);
            _tmp.Sort();
            foreach (var uid in _tmp)
            {
                string mine = _ours[uid];
                _ours.Remove(uid);
                string cur = feedback(uid);
                if (cur != null && cur == mine) res.Add(new Write { Uid = uid, Text = "" });
            }
            return res;
        }

        /// <summary>The caller reports a failed write: the uid is not "ours" (the next Plan retries).</summary>
        public void Failed(int uid) { _ours.Remove(uid); }

        /// <summary>Key off / Dispose: "" for every unit that still shows our text.</summary>
        public IList<Write> ReleaseAll(Func<int, string> feedback)
        {
            var res = new List<Write>();
            _tmp.Clear();
            foreach (var kv in _ours) _tmp.Add(kv.Key);
            _tmp.Sort();
            foreach (var uid in _tmp)
            {
                string cur = feedback(uid);
                if (cur != null && cur == _ours[uid]) res.Add(new Write { Uid = uid, Text = "" });
            }
            _ours.Clear();
            return res;
        }

        /// <summary>Mission leave: the objects are gone, no writes.</summary>
        public void Forget() { _ours.Clear(); }
    }

    internal static class UnitActionSelfTest
    {
        private sealed class Src : IUnitActionSource
        {
            public string Id { get; set; }
            public UnitActionLabel[] Labels = new UnitActionLabel[0];
            public bool Throw;
            public void CollectActions(double nowS, IList<UnitActionLabel> into)
            {
                foreach (var l in Labels) into.Add(l);
                if (Throw) throw new InvalidOperationException("boom");
            }
        }

        private static UnitActionLabel L(int uid, int prio, string src) { return new UnitActionLabel { Uid = uid, Prio = prio, Source = src, CmdKey = "ua.cmd.retreat" }; }

        public static string Registry()
        {
            var a = new Src { Id = "a", Labels = new[] { L(1, UaPrio.Asw, "a"), L(2, UaPrio.Cap, "a") } };
            var b = new Src { Id = "b", Labels = new[] { L(1, UaPrio.Evasion, "b"), L(2, UaPrio.Cap, "b"), L(3, UaPrio.Sar, "b") } };
            var bad = new Src { Id = "bad", Throw = true, Labels = new[] { L(9, UaPrio.Evasion, "bad") } };
            var reg = new UnitActionRegistry();
            reg.Add(a); reg.Add(bad); reg.Add(b);
            if (string.Join(",", new List<string>(reg.Ids).ToArray()) != "a,bad,b") return "Ids order = " + string.Join(",", new List<string>(reg.Ids).ToArray());
            var warns = new List<string>();
            var r = reg.Resolve(0, warns.Add);
            if (r.Count != 3) return "resolved " + r.Count + " units, want 3 (a throwing source must add nothing)";
            if (r[1].Source != "b") return "uid 1: higher Prio (100 over 40) must win, got " + r[1].Source;
            if (r[2].Source != "a") return "uid 2: equal Prio must keep the earlier source, got " + r[2].Source;
            if (r[3].Source != "b") return "uid 3 source";
            if (warns.Count != 1) return "one warning per failing source, got " + warns.Count;
            reg.Resolve(1, warns.Add);
            if (warns.Count != 1) return "a repeat of the same failure must not log again, got " + warns.Count;
            bad.Throw = false;
            reg.Resolve(2, warns.Add);
            bad.Throw = true;
            reg.Resolve(3, warns.Add);
            if (warns.Count != 2) return "after a success the failure is logged again, got " + warns.Count;
            return null;
        }

        public static string Text()
        {
            using (UiText.Push("en"))
            {
                var none = new UnitActionLabel { CmdKey = "ua.cmd.retreat" };
                if (UnitActionText.Format(none) != "Retreat") return "Format without phase = " + UnitActionText.Format(none);
                var ph = new UnitActionLabel { CmdKey = "ua.cmd.intercept", CmdArgs = new[] { "T5" }, PhaseKey = "ua.ph.ic.held" };
                if (UnitActionText.Format(ph) != "Intercept T5: holding") return "Format with phase = " + UnitActionText.Format(ph);
                if (UnitActionText.Format(new UnitActionLabel()) != "") return "empty CmdKey must give an empty text";
                if (UnitActionText.Format(null) != "") return "null label must give an empty text";
            }
            string mod = "Retreat: to cover of Perry";
            var plain = new HashSet<string> { "Going to destination", "Loitering" };
            if (UnitActionText.Compose(mod, "Going to destination", plain, "smart") != mod) return "smart + plain game text must give the mod text only";
            if (UnitActionText.Compose(mod, "", plain, "smart") != mod) return "smart + empty game text";
            if (UnitActionText.Compose(mod, null, plain, "smart") != mod) return "smart + null game text";
            if (UnitActionText.Compose(mod, "Attacking T7", plain, "smart") != mod + " · Attacking T7") return "smart + important game text = " + UnitActionText.Compose(mod, "Attacking T7", plain, "smart");
            if (UnitActionText.Compose(mod, mod, plain, "smart") != mod) return "smart + game text equal to the mod text";
            if (UnitActionText.Compose(mod, "Attacking T7", plain, "replace") != mod) return "replace must always give the mod text";
            string longMod = new string('x', 70);
            string c = UnitActionText.Compose(longMod, "Attacking something far away", plain, "smart");
            if (c.Length != UnitActionText.MaxLen || !c.EndsWith("…")) return "long text must be clipped to MaxLen with an ellipsis, got " + c.Length;
            if (UnitActionText.Clip("abc", 3) != "abc" || UnitActionText.Clip("abcd", 3) != "ab…") return "Clip";
            return null;
        }

        private static string Join(IList<UnitActionWriter.Write> w)
        {
            var p = new List<string>();
            foreach (var x in w) p.Add(x.Uid + "=" + x.Text);
            return string.Join(";", p.ToArray());
        }

        public static string Writer()
        {
            var game = new Dictionary<int, string> { { 1, "" }, { 2, "" }, { 3, "" } };
            Func<int, string> fb = u => { string v; return game.TryGetValue(u, out v) ? v : null; };
            var w = new UnitActionWriter();
            var want = new Dictionary<int, string> { { 1, "A" }, { 2, "B" } };
            var p = w.Plan(want, fb);
            if (Join(p) != "1=A;2=B" || w.Owned != 2) return "tick 1: " + Join(p) + " owned " + w.Owned;
            foreach (var x in p) game[x.Uid] = x.Text;
            if (w.Plan(want, fb).Count != 0) return "an unchanged text must not be written again";
            want[1] = "A2";
            p = w.Plan(want, fb);
            if (Join(p) != "1=A2") return "a changed text: " + Join(p);
            game[1] = "A2";
            // not wanted, cur == ours -> "" and forget
            want.Remove(2);
            p = w.Plan(want, fb);
            if (Join(p) != "2=" || w.Owned != 1) return "release: " + Join(p) + " owned " + w.Owned;
            game[2] = "";
            // not wanted, cur is foreign -> forget, no write
            game[1] = "Game text";
            p = w.Plan(new Dictionary<int, string>(), fb);
            if (p.Count != 0 || w.Owned != 0) return "foreign text must be left alone: " + Join(p) + " owned " + w.Owned;
            // wanted, cur foreign non-empty -> wait; after the game clears it -> write
            var w2 = new UnitActionWriter();
            game[3] = "Refuel hint";
            var want3 = new Dictionary<int, string> { { 3, "C" } };
            if (w2.Plan(want3, fb).Count != 0 || w2.Owned != 0) return "foreign hint: tick 1 must not write";
            game[3] = "";
            p = w2.Plan(want3, fb);
            if (Join(p) != "3=C" || w2.Owned != 1) return "foreign hint: tick 2 = " + Join(p);
            // the game overwrote our text with a hint, then cleared it: wait, then write again
            game[3] = "Hint";
            if (w2.Plan(want3, fb).Count != 0) return "our text replaced by a hint: no write";
            game[3] = "";
            if (Join(w2.Plan(want3, fb)) != "3=C") return "after the hint went away the label must come back";
            // unit missing (deck row / gone)
            var w3 = new UnitActionWriter();
            if (w3.Plan(new Dictionary<int, string> { { 77, "Z" } }, fb).Count != 0 || w3.Owned != 0) return "a unit without an object must not be written";
            // Failed -> retry
            var w4 = new UnitActionWriter();
            var want1 = new Dictionary<int, string> { { 1, "F" } };
            game[1] = "";
            if (Join(w4.Plan(want1, fb)) != "1=F") return "failed: first plan";
            w4.Failed(1);
            if (w4.Owned != 0 || Join(w4.Plan(want1, fb)) != "1=F") return "Failed must make the next Plan retry";
            // ReleaseAll writes "" only to our own; Forget writes nothing
            game[1] = ""; game[2] = "foreign"; game[3] = "";
            var w5 = new UnitActionWriter();
            foreach (var x in w5.Plan(new Dictionary<int, string> { { 1, "F" }, { 3, "G" } }, fb)) game[x.Uid] = x.Text;
            if (w5.Owned != 2) return "ReleaseAll setup";
            game[1] = "changed by the game";
            var rel = w5.ReleaseAll(fb);
            if (Join(rel) != "3=" || w5.Owned != 0) return "ReleaseAll: " + Join(rel) + " owned " + w5.Owned;
            var w6 = new UnitActionWriter();
            game[1] = "";
            w6.Plan(want1, fb);
            w6.Forget();
            if (w6.Owned != 0 || w6.ReleaseAll(fb).Count != 0) return "Forget must drop ownership without writes";
            return null;
        }

        public static string Run()
        {
            string e = Registry() ?? Text() ?? Writer();
            return e;
        }
    }
}
