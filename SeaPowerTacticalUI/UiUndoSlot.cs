// T-178 U1 (ADR-0077 §8): the pure core of "Undo". One snapshot per window: a dangerous action ("Stop all", "Disband") takes a snapshot BEFORE it runs and the card offers
// "Undo" for 10 s of real time. A new action of the same window replaces the old snapshot; a mission change (Reset) and the end of the time drop it. The module gives the closures
// (take the snapshot, restore through the same commands); the restore reports what came back and what was skipped.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class UiUndoResult { public int Groups, Restored; public List<string> Skipped = new List<string>(); }

    internal sealed class UiUndoSlot
    {
        public const double LifeS = 10;
        public Action<string> Log;

        private sealed class Slot { public string Label; public int Groups; public Func<UiUndoResult> Restore; public double At; }
        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>();

        private void Say(string s) { if (Log != null) { try { Log(s); } catch { } } }

        /// <summary>Keeps the snapshot of `window` (replacing the previous one). `now` = real time in seconds.</summary>
        public void Take(string window, string label, int groups, Func<UiUndoResult> restore, double now)
        {
            if (string.IsNullOrEmpty(window) || restore == null) return;
            _slots[window] = new Slot { Label = label, Groups = groups, Restore = restore, At = now };
            Say("undo take window=" + window + " groups=" + groups);   // i18n-allow: journal line
        }

        /// <summary>Runs the restore of `window` and drops the snapshot; null = none or expired.</summary>
        public UiUndoResult Restore(string window, double now)
        {
            Slot s;
            if (window == null || !_slots.TryGetValue(window, out s)) return null;
            _slots.Remove(window);
            if (now - s.At > LifeS) { Say("undo expire window=" + window + " groups=" + s.Groups); return null; }   // i18n-allow: journal line
            UiUndoResult r;
            try { r = s.Restore(); } catch (Exception e) { Say("undo restore failed window=" + window + ": " + e.GetType().Name + ": " + e.Message); return null; }   // i18n-allow: journal line
            if (r == null) return null;
            Say("undo restore window=" + window + " groups=" + r.Groups + " restored=" + r.Restored + " skipped=" + r.Skipped.Count);   // i18n-allow: journal line
            return r;
        }

        /// <summary>Seconds left to press Undo (0 = none).</summary>
        public double Left(string window, double now)
        {
            Slot s;
            if (window == null || !_slots.TryGetValue(window, out s)) return 0;
            return Math.Max(0, LifeS - (now - s.At));
        }

        /// <summary>Drops the expired snapshots; returns their windows.</summary>
        public IList<string> Tick(double now)
        {
            var gone = new List<string>();
            foreach (var kv in _slots) if (now - kv.Value.At > LifeS) gone.Add(kv.Key);
            foreach (string w in gone) { Say("undo expire window=" + w + " groups=" + _slots[w].Groups); _slots.Remove(w); }   // i18n-allow: journal line
            return gone;
        }

        /// <summary>Mission change: no snapshot survives.</summary>
        public void Reset() { _slots.Clear(); }

        public static string SelfTest()
        {
            var u = new UiUndoSlot(); var log = new List<string>(); u.Log = log.Add;
            int calls = 0;
            Func<UiUndoResult> ok = () => { calls++; var r = new UiUndoResult { Groups = 3, Restored = 2 }; r.Skipped.Add("Hawk 21 — in CAP-2"); return r; };
            if (u.Restore("asw", 0) != null) return "nothing to restore";
            u.Take("asw", "Stopped 3 groups", 3, ok, 100);
            if (Math.Abs(u.Left("asw", 103) - 7) > 1e-9) return "left 7 s: " + u.Left("asw", 103);
            var res = u.Restore("asw", 109.9);
            if (res == null || res.Groups != 3 || res.Restored != 2 || res.Skipped.Count != 1 || calls != 1) return "restore within 10 s with the skipped list";
            if (u.Restore("asw", 109.95) != null || calls != 1) return "a snapshot restores once";
            // 10.1 s: gone
            u.Take("asw", "x", 3, ok, 200);
            if (u.Restore("asw", 210.1) != null || calls != 1) return "after 10.1 s there is no snapshot";
            // exactly at the limit still works
            u.Take("asw", "x", 3, ok, 300);
            if (u.Restore("asw", 310.0) == null) return "10.0 s is still in time";
            // a second action replaces the first one
            int a = 0, b = 0;
            u.Take("cap", "first", 1, () => { a++; return new UiUndoResult { Groups = 1, Restored = 1 }; }, 400);
            u.Take("cap", "second", 2, () => { b++; return new UiUndoResult { Groups = 2, Restored = 2 }; }, 405);
            var r2 = u.Restore("cap", 406);
            if (a != 0 || b != 1 || r2.Groups != 2) return "the second action replaces the first";
            if (Math.Abs(u.Left("cap", 406)) > 0) return "left after restore";
            // windows are independent
            u.Take("asw", "x", 1, ok, 500); u.Take("cap", "y", 1, ok, 505);
            var gone = u.Tick(511);
            if (gone.Count != 1 || gone[0] != "asw" || u.Left("cap", 511) <= 0) return "Tick drops only the expired window";
            // mission change
            u.Reset();
            if (u.Restore("cap", 506) != null || u.Left("cap", 506) != 0) return "Reset drops everything";
            // a throwing restore does not break the slot
            u.Take("asw", "bad", 1, () => { throw new InvalidOperationException("x"); }, 600);
            if (u.Restore("asw", 601) != null) return "a failing restore gives null";
            // log lines
            string all = string.Join("|", log);
            if (!all.Contains("undo take window=asw groups=3") || !all.Contains("undo restore window=asw groups=3 restored=2 skipped=1") || !all.Contains("undo expire window=asw groups=3")) return "journal: " + all;
            if (!all.Contains("undo expire window=asw groups=1")) return "journal expire by Tick: " + all;
            u.Take(null, "x", 1, ok, 0); u.Take("w", "x", 1, null, 0);
            if (u.Left("w", 0) != 0) return "bad arguments are ignored";
            return null;
        }
    }
}
