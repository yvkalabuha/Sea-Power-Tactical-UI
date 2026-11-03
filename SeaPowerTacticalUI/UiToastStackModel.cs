using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>The service of the shared card stack (T-175, ADR-0075 item 7; T-173 uses it too): a card is a key, a kind and a body drawn with the kit.
    /// Generic: no ASW or air types here.</summary>
    internal interface IUiToasts
    {
        /// <summary>A new key is shown at the top; a known key gets the new body (and, with reshow, goes to the top again and is shown even if the player closed it).
        /// hideAfterS = 0: until closed. now = real time, s.</summary>
        void Show(string key, string kind, Action<UiKit> body, double now, double hideAfterS, bool reshow);
        void Update(string key, Action<UiKit> body);
        void Close(string key);
    }

    internal sealed class UiToast
    {
        public string Key, Kind;
        public Action<UiKit> Body;
        public double ShownAt, HideAfterS;
        public bool Closed;
    }

    /// <summary>Pure model of the stack: at most 3 visible cards (the newest first), the older ones fold into "+N", a card closed by the player stays closed until it is shown again with reshow.</summary>
    internal sealed class UiToastStackModel : IUiToasts
    {
        public const int MaxVisible = 3;
        private readonly List<UiToast> _all = new List<UiToast>();   // newest first
        private double _now;

        private UiToast Find(string key) { foreach (var t in _all) if (t.Key == key) return t; return null; }

        public void Show(string key, string kind, Action<UiKit> body, double now, double hideAfterS, bool reshow)
        {
            if (key == null) return;
            _now = now;
            var t = Find(key);
            if (t == null)
            {
                t = new UiToast { Key = key, Kind = kind, Body = body, ShownAt = now, HideAfterS = hideAfterS };
                _all.Insert(0, t);
                Trim();
                return;
            }
            t.Body = body; t.Kind = kind; t.HideAfterS = hideAfterS;
            if (reshow)
            {
                t.Closed = false; t.ShownAt = now;
                _all.Remove(t); _all.Insert(0, t);
            }
        }

        public void Update(string key, Action<UiKit> body)
        {
            var t = Find(key);
            if (t != null && body != null) t.Body = body;
        }

        public void Close(string key)
        {
            var t = Find(key);
            if (t != null) t.Closed = true;
        }

        public void CloseAll() { foreach (var t in _all) t.Closed = true; }

        /// <summary>Expiry by time; closed and expired cards are forgotten after a while (the key may come back).</summary>
        public void Tick(double now)
        {
            _now = now;
            foreach (var t in _all) if (!t.Closed && t.HideAfterS > 0 && now - t.ShownAt >= t.HideAfterS) t.Closed = true;
            Trim();
        }

        private void Trim()
        {
            // keep the closed cards (their key stays "seen") but not without a limit
            int closed = 0;
            for (int i = _all.Count - 1; i >= 0; i--)
                if (_all[i].Closed && ++closed > 40) _all.RemoveAt(i);
        }

        public IList<UiToast> Visible()
        {
            var res = new List<UiToast>();
            foreach (var t in _all) { if (t.Closed) continue; if (res.Count < MaxVisible) res.Add(t); }
            return res;
        }

        /// <summary>Open cards beyond the visible ones ("+N").</summary>
        public int Folded
        {
            get { int open = 0; foreach (var t in _all) if (!t.Closed) open++; return Math.Max(0, open - MaxVisible); }
        }

        public bool Any { get { foreach (var t in _all) if (!t.Closed) return true; return false; } }

        public bool Has(string key) { var t = Find(key); return t != null && !t.Closed; }

        public static string SelfTest()
        {
            var m = new UiToastStackModel();
            Action<UiKit> b = k => { };
            m.Show("a", "x", b, 0, 20, false); m.Show("b", "x", b, 1, 20, false); m.Show("c", "x", b, 2, 20, false);
            var v = m.Visible();
            if (v.Count != 3 || v[0].Key != "c" || v[2].Key != "a" || m.Folded != 0) return "three visible, newest first";
            m.Show("d", "x", b, 3, 20, false);
            v = m.Visible();
            if (v.Count != 3 || v[0].Key != "d" || v[2].Key != "b" || m.Folded != 1) return "the oldest folds into +1: " + v.Count + "/" + m.Folded;
            m.Close("d");
            v = m.Visible();
            if (v.Count != 3 || v[0].Key != "c" || m.Folded != 0) return "close brings the folded one back";
            m.Show("d", "x", b, 4, 20, false);
            if (m.Has("d")) return "a closed key stays closed without reshow";
            m.Show("d", "x", b, 5, 20, true);
            v = m.Visible();
            if (!m.Has("d") || v[0].Key != "d") return "reshow raises the card to the top";
            // a known open key: body replaced, position kept, no reshow
            int calls = 0; Action<UiKit> nb = k => calls++;
            m.Show("c", "x", nb, 6, 20, false);
            if (m.Visible()[0].Key != "d" || m.Visible()[1].Key != "c" || m.Visible()[1].Body != nb) return "same key: body updated, order kept";
            m.Update("c", b);
            if (m.Visible()[1].Body != b) return "Update replaces the body";
            // expiry: 20 s after the show (the reshow of d was at 5 s)
            m.Tick(22.5);
            if (m.Has("c") || m.Has("b") || m.Has("a")) return "the older ones expire";
            if (!m.Has("d")) return "d (reshown at 5 s) is still there at 22.5 s";
            m.Tick(25.5);
            if (m.Has("d")) return "d expires 20 s after the reshow";
            // hideAfter 0: until closed
            var n = new UiToastStackModel();
            n.Show("p", "x", b, 0, 0, false);
            n.Tick(10000);
            if (!n.Has("p")) return "hideAfterS 0 stays";
            n.Close("p");
            if (n.Any || n.Visible().Count != 0) return "closed";
            n.Close("nope"); n.Update("nope", b);
            // many closed cards do not grow without a limit
            for (int i = 0; i < 100; i++) { n.Show("k" + i, "x", b, i, 0, false); n.Close("k" + i); }
            n.Tick(200);
            if (n.Any) return "all closed";
            return null;
        }
    }
}
