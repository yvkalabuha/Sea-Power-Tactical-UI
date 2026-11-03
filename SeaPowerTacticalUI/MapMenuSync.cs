using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal enum MenuAction { None, Insert, Scan }

    /// <summary>T-113 B, pure: how the World View item of the map menu is recognised and how often a failed insertion is retried.</summary>
    internal static class MapMenuMath
    {
        internal const string WorldViewPath = "DataContext.WorldViewOpen";

        /// <summary>The item's IsChecked binding path says World View, or its Header equals the model's WorldViewButtonName (both non-empty).</summary>
        public static bool IsWorldViewItem(string header, string buttonName, string isCheckedPath)
        {
            if (isCheckedPath == WorldViewPath) return true;
            return !string.IsNullOrEmpty(header) && !string.IsNullOrEmpty(buttonName) && header == buttonName;
        }

        /// <summary>T-154 F3: World View among <paramref name="items"/> by template features (O(items), no tree walk). isItem filters menu items,
        /// headerOf/pathOf read Header and the IsChecked binding path. Returns the item and its index, or null.</summary>
        public static object FindWorldViewIn(IEnumerable items, string buttonName, Func<object, bool> isItem, Func<object, string> headerOf, Func<object, string> pathOf, out int index)
        {
            index = -1;
            if (items == null) return null;
            int i = 0;
            foreach (var it in items)
            {
                if (it != null && isItem(it) && IsWorldViewItem(headerOf(it), buttonName, pathOf(it))) { index = i; return it; }
                i++;
            }
            return null;
        }

        /// <summary>Delay before the next insertion try after <paramref name="failures"/> failures: 3, 6, 12, 24, 48, 60 s (cap).</summary>
        public static double RetryDelayS(int failures)
        {
            double d = 3;
            for (int i = 0; i < failures && d < 60; i++) d *= 2;
            return d > 60 ? 60 : d;
        }

        public const double MinRescanS = 5;

        /// <summary>What to do on a check. Order: not in mission -> None; cached menu loaded and all our items are members -> None;
        /// cached menu loaded -> Insert (into the cached menu, no tree walk); otherwise Scan when forced or now >= nextScan, else None.</summary>
        public static MenuAction NextAction(bool inMission, bool menuCached, bool menuLoaded, bool allMembers, bool forced, double now, double nextScan)
        {
            if (!inMission) return MenuAction.None;
            if (menuCached && menuLoaded) return allMembers ? MenuAction.None : MenuAction.Insert;
            return (forced || now >= nextScan) ? MenuAction.Scan : MenuAction.None;
        }

        /// <summary>Tag value equals tag: a string compared ordinally, any other non-null object by ToString(); null -> false.</summary>
        public static bool TagIs(object tagValue, string tag)
        {
            if (tagValue == null) return false;
            var s = tagValue as string;
            if (s != null) return string.Equals(s, tag, StringComparison.Ordinal);
            string t;
            try { t = tagValue.ToString(); } catch { return false; }
            return string.Equals(t, tag, StringComparison.Ordinal);
        }

        private static object TagOf(object item, Func<object, object> tagOf)
        {
            if (item == null || tagOf == null) return null;
            try { return tagOf(item); }
            catch { return null; }
        }

        /// <summary>Our item in <paramref name="items"/>: <paramref name="known"/> if it is a member (by reference), else the first item whose
        /// Tag (read by tagOf, the game side owns the member) satisfies TagIs; null when none.</summary>
        public static object FindOwn(IEnumerable items, string tag, object known, Func<object, object> tagOf)
        {
            if (items == null) return null;
            if (known != null) foreach (var it in items) if (ReferenceEquals(it, known)) return known;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (TagIs(TagOf(it, tagOf), tag)) return it;
            }
            return null;
        }

        /// <summary>true when every non-null element of <paramref name="ours"/> is a member of <paramref name="items"/> by reference; false for an empty or null list, or for a null element.</summary>
        public static bool AllMembers(IEnumerable items, IList<object> ours)
        {
            if (items == null || ours == null || ours.Count == 0) return false;
            foreach (var o in ours)
            {
                if (o == null) return false;
                bool found = false;
                foreach (var it in items) if (ReferenceEquals(it, o)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>Number of elements (null-safe, 0 for null).</summary>
        public static int Count(IEnumerable items)
        {
            if (items == null) return 0;
            int n = 0;
            foreach (var _ in items) n++;
            return n;
        }

        /// <summary>Type name of the item's Tag value for the diagnostic line: "null", "String", or the runtime type name.</summary>
        public static string TagTypeName(object item, Func<object, object> tagOf)
        {
            var v = TagOf(item, tagOf);
            return v == null ? "null" : v.GetType().Name;
        }

        public static bool GiveUp(int failures, int maxTries) { return maxTries > 0 && failures >= maxTries; }
    }

    /// <summary>T-143: one Info line per menu instance (root hash) + a Debug summary every 60 s.</summary>
    internal sealed class MapMenuLogGate
    {
        public const double SummarySeconds = 60;
        public const int MaxRoots = 64;
        private readonly HashSet<int> _roots = new HashSet<int>();
        private double _nextSummary = double.NaN;
        private int _scans, _reinserts, _newRoots;

        /// <summary>true the first time a root is seen (then false for the same root).</summary>
        public bool FirstFor(int root)
        {
            if (_roots.Count >= MaxRoots) _roots.Clear();
            if (!_roots.Add(root)) return false;
            _newRoots++;
            return true;
        }

        private bool _infoDone;

        /// <summary>T-154 F3: the per-root Info line is written for the first menu instance only; later roots only count into the 60 s summary.</summary>
        public bool ShouldLogRoot(int root)
        {
            bool first = FirstFor(root);
            if (!first || _infoDone) return false;
            _infoDone = true;
            return true;
        }

        public void NoteScan() { _scans++; }
        public void NoteReinsert() { _reinserts++; }

        /// <summary>First call arms the timer; then true once per SummarySeconds when any counter is non-zero; resets counters when it fires.</summary>
        public bool SummaryDue(double now, out int scans, out int reinserts, out int roots)
        {
            scans = reinserts = roots = 0;
            if (double.IsNaN(_nextSummary)) { _nextSummary = now + SummarySeconds; return false; }
            if (now < _nextSummary) return false;
            _nextSummary = now + SummarySeconds;
            if (_scans == 0 && _reinserts == 0 && _newRoots == 0) return false;
            scans = _scans; reinserts = _reinserts; roots = _newRoots;
            _scans = _reinserts = _newRoots = 0;
            return true;
        }

        public void Reset() { _roots.Clear(); _nextSummary = double.NaN; _scans = _reinserts = _newRoots = 0; _infoDone = false; }
    }

    /// <summary>
    /// T-104 / W2, pure: keeps the check mark of a map-menu item and the state of a mod window in step without echo.
    /// Window state is the source of truth. Item changes caused by our own Pull are not forwarded back to the window.
    /// </summary>
    internal sealed class MapMenuSync
    {
        private bool _syncing;
        private bool _item;

        public MapMenuSync(bool initialItemState) { _item = initialItemState; }

        public bool ItemState { get { return _item; } }
        public bool Syncing { get { return _syncing; } }

        /// <summary>Per frame: the window is open/closed. Returns the value to put on the item, or null when it already shows it.
        /// The caller wraps the item write in Begin()/End() so the resulting Checked/Unchecked event is recognised as an echo.</summary>
        public bool? Pull(bool windowOpen)
        {
            if (windowOpen == _item) return null;
            return windowOpen;
        }

        public void Begin(bool newItemState) { _syncing = true; _item = newItemState; }
        public void End() { _syncing = false; }

        /// <summary>The item reports a new check state (user click or our own write). Returns the value to apply to the window or null (echo / no change).</summary>
        public bool? OnItemChanged(bool isChecked, bool windowOpen)
        {
            if (_syncing) return null;
            _item = isChecked;
            if (isChecked == windowOpen) return null;
            return isChecked;
        }
    }

    /// <summary>T-107 W1, pure: the [Overview] Debug log writes a contact row only when its key changes, plus one summary line per interval.</summary>
    internal sealed class OverviewDebugGate
    {
        public const double SummarySeconds = 60;
        private readonly Dictionary<int, string> _key = new Dictionary<int, string>();
        private double _nextSummary = double.NaN;
        private int _changed;

        /// <summary>true when the row's key differs from the last logged one (or it is new). Unchanged rows are never repeated.</summary>
        public bool Due(int id, string key)
        {
            string old;
            if (_key.TryGetValue(id, out old) && old == key) return false;
            _key[id] = key;
            _changed++;
            return true;
        }

        /// <summary>One summary line per SummarySeconds (first call only arms the timer). Resets the changed counter when it fires.</summary>
        public bool SummaryDue(double now, out int changed)
        {
            changed = 0;
            if (double.IsNaN(_nextSummary)) { _nextSummary = now + SummarySeconds; return false; }
            if (now < _nextSummary) return false;
            _nextSummary = now + SummarySeconds;
            changed = _changed; _changed = 0;
            return true;
        }

        public void Forget(ICollection<int> alive)
        {
            if (_key.Count <= 4 * (alive.Count + 16)) return;
            var keep = new HashSet<int>(alive);
            foreach (int k in new List<int>(_key.Keys)) if (!keep.Contains(k)) _key.Remove(k);
        }

        public void Reset() { _key.Clear(); _nextSummary = double.NaN; _changed = 0; }

        public static string SelfTest()
        {
            var g = new OverviewDebugGate();
            if (!g.Due(1, "a")) return "new row";
            if (g.Due(1, "a")) return "same key repeated";
            if (!g.Due(1, "b")) return "changed key";
            int ch;
            if (g.SummaryDue(0, out ch)) return "first call arms";
            if (g.SummaryDue(59, out ch)) return "summary early";
            if (!g.SummaryDue(60, out ch) || ch != 2) return "summary at 60 s changed=" + ch;
            if (g.SummaryDue(61, out ch)) return "summary again";
            // 10 s of unchanged rows must stay silent (the old code repeated every 10 s)
            for (int t = 62; t < 120; t += 10) if (g.Due(1, "b")) return "repeat after " + t;
            var s = new MapMenuSync(false);
            if (s.Pull(false) != null) return "pull same";
            var v = s.Pull(true);
            if (v != true) return "pull open";
            s.Begin(true);
            if (s.OnItemChanged(true, true) != null) return "echo forwarded";   // event raised by our own write
            s.End();
            if (s.OnItemChanged(false, true) != false) return "click close";    // user unchecks while open
            if (s.OnItemChanged(false, false) != null) return "no change forwarded";
            if (s.OnItemChanged(true, false) != true) return "click open";
            if (s.Pull(true) != null) return "item already shows open";
            return null;
        }
    }
}
