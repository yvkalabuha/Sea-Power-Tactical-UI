using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// Screen rectangles (pixels, y from the top) of the mod's own windows (Overview, ...), so the radial menu does not open under them
    /// and the coverage overlay does not draw its fog over them. Main thread only. T-070.
    /// </summary>
    internal static class ModUiRects
    {
        private static readonly Dictionary<string, double[]> Rects = new Dictionary<string, double[]>();

        public static void Set(string owner, double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0) { Rects.Remove(owner); return; }
            Rects[owner] = new[] { x, y, w, h };
        }

        // T-240: passive obstacles (the radar legend): other windows avoid them when they appear, but they are not mod windows (no mouse block, no radial-menu veto)
        private static readonly Dictionary<string, double[]> PassiveRects = new Dictionary<string, double[]>();

        public static void SetPassive(string owner, double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0) { PassiveRects.Remove(owner); return; }
            PassiveRects[owner] = new[] { x, y, w, h };
        }

        public static void ClearPassive(string owner) { PassiveRects.Remove(owner); }

        /// <summary>Copy of the passive rectangles by owner name.</summary>
        public static Dictionary<string, double[]> Passive()
        {
            var d = new Dictionary<string, double[]>();
            foreach (var kv in PassiveRects) d[kv.Key] = (double[])kv.Value.Clone();
            return d;
        }

        public static void Clear(string owner) { Rects.Remove(owner); Z.Remove(owner); }

        // Stacking order of the mod windows (last pressed = top). A press in an overlap must focus only the visually top window: the OnGUI call order is not the stacking order.
        private static readonly Dictionary<string, int> Z = new Dictionary<string, int>();
        private static int _zCounter;

        public static void Raise(string owner) { Z[owner] = ++_zCounter; }

        /// <summary>False when another mod window that covers the point is stacked above this one.</summary>
        public static bool IsTopAt(string owner, double x, double y)
        {
            int mine; Z.TryGetValue(owner, out mine);
            foreach (var kv in Rects)
            {
                if (kv.Key == owner) continue;
                var r = kv.Value;
                if (!(x >= r[0] && x < r[0] + r[2] && y >= r[1] && y < r[1] + r[3])) continue;
                int z; Z.TryGetValue(kv.Key, out z);
                if (z > mine) return false;
            }
            return true;
        }

        public static bool Contains(double x, double y, string exceptOwner = null)
        {
            foreach (var kv in Rects)
            {
                if (exceptOwner != null && kv.Key == exceptOwner) continue;
                var r = kv.Value;
                if (x >= r[0] && x < r[0] + r[2] && y >= r[1] && y < r[1] + r[3]) return true;
            }
            return false;
        }

        public static List<double[]> All()
        {
            var l = new List<double[]>();
            foreach (var kv in Rects) l.Add((double[])kv.Value.Clone());
            return l;
        }

        /// <summary>Copy of the rectangles by owner name (dev diag, ADR-0046).</summary>
        public static Dictionary<string, double[]> Named()
        {
            var d = new Dictionary<string, double[]>();
            foreach (var kv in Rects) d[kv.Key] = (double[])kv.Value.Clone();
            return d;
        }

        public static string SelfTest()
        {
            string fail = null;
            Action<string, bool> eq = (n, ok) => { if (!ok && fail == null) fail = n; };
            Rects.Clear();
            Set("a", 100, 50, 200, 100);
            eq("inside", Contains(150, 80));
            eq("outside", !Contains(50, 80) && !Contains(150, 200));
            eq("edge: right/bottom exclusive", Contains(100, 50) && !Contains(300, 80) && !Contains(150, 150));
            eq("except owner", !Contains(150, 80, "a") && Contains(150, 80, "b"));
            Set("b", 0, 0, 10, 10);
            eq("two owners", All().Count == 2 && Contains(5, 5));
            Set("a", 0, 0, 0, 0);
            eq("Set with zero size removes", All().Count == 1 && !Contains(150, 80));
            Clear("b");
            Set("n", 1, 2, 3, 4);
            var named = Named();
            named["n"][0] = 99;
            eq("Named: copy by owner", named.Count == 1 && Named()["n"][0] == 1 && Named()["n"][3] == 4);
            Clear("n");
            eq("Clear", All().Count == 0);
            // stacking: a press in an overlap belongs to the window raised last, not to the one whose OnGUI runs later
            Set("cap", 0, 0, 100, 100); Set("settings", 50, 50, 100, 100);
            eq("z: tie, both are top", IsTopAt("cap", 60, 60) && IsTopAt("settings", 60, 60));
            Raise("cap");
            eq("z: raised window is top", IsTopAt("cap", 60, 60) && !IsTopAt("settings", 60, 60));
            eq("z: outside the overlap the lower one is top", IsTopAt("settings", 120, 120));
            Raise("settings");
            eq("z: raise swaps", !IsTopAt("cap", 60, 60) && IsTopAt("settings", 60, 60));
            Clear("cap"); Clear("settings");
            // T-240: passive obstacles are invisible to the mouse logic
            PassiveRects.Clear();
            SetPassive("map_legend", 100, 100, 50, 50);
            eq("passive: not a window", !Contains(120, 120) && All().Count == 0 && Named().Count == 0 && IsTopAt("x", 120, 120));
            var pas = Passive();
            pas["map_legend"][0] = 999;
            eq("passive: copy", pas.Count == 1 && Passive()["map_legend"][0] == 100 && Passive()["map_legend"][3] == 50);
            SetPassive("map_legend", 0, 0, 0, 10);
            eq("passive: zero size removes", Passive().Count == 0);
            SetPassive("map_legend", 1, 2, 3, 4); ClearPassive("map_legend");
            eq("passive: clear", Passive().Count == 0);
            return fail;
        }
    }
}
