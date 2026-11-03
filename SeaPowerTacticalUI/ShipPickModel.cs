// T-178 Kf1 (ADR-0077 §3): the pure model of "pick your own ship": the sorted list (own ships only, nearest first), the default entry and the ship nearest to a click on the map.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal struct ShipItem { public int Uid; public string Name, Icon; public double DistNm; public bool Own; }

    internal static class ShipPickModel
    {
        public const float PickRadiusPx = 24f;

        /// <summary>Own ships only, by distance, then by name.</summary>
        public static List<ShipItem> Sorted(IEnumerable<ShipItem> items)
        {
            var l = new List<ShipItem>();
            if (items != null) foreach (var it in items) if (it.Own) l.Add(it);
            l.Sort((a, b) =>
            {
                int c = a.DistNm.CompareTo(b.DistNm);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return l;
        }

        /// <summary>The entry that is shown when nothing is chosen yet: the ship of the order, else the ship chosen in the window, else the ship the group is based on,
        /// else the nearest one; -1 for an empty list.</summary>
        public static int DefaultIndex(IList<ShipItem> sorted, int orderUid, int selectedUid, int baseUid)
        {
            if (sorted == null || sorted.Count == 0) return -1;
            foreach (int uid in new[] { orderUid, selectedUid, baseUid })
            {
                if (uid == 0) continue;
                for (int i = 0; i < sorted.Count; i++) if (sorted[i].Uid == uid) return i;
            }
            return 0;
        }

        /// <summary>The uid of the own ship nearest to (mx, my) in screen pixels within radiusPx; 0 = none (a foreign ship never counts). screenXY[i] = {x, y} of items[i].</summary>
        public static int Nearest(IList<ShipItem> items, IList<float[]> screenXY, float mx, float my, float radiusPx)
        {
            if (items == null || screenXY == null) return 0;
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < items.Count && i < screenXY.Count; i++)
            {
                if (!items[i].Own || screenXY[i] == null || screenXY[i].Length < 2) continue;
                double dx = screenXY[i][0] - mx, dy = screenXY[i][1] - my, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= radiusPx && d < bd) { bd = d; best = items[i].Uid; }
            }
            return best;
        }

        public static string SelfTest()
        {
            var all = new List<ShipItem>
            {
                new ShipItem { Uid = 3, Name = "Zeta", DistNm = 12, Own = true },
                new ShipItem { Uid = 1, Name = "Yorktown", DistNm = 4.3, Own = true },
                new ShipItem { Uid = 9, Name = "Allied", DistNm = 1, Own = false },
                new ShipItem { Uid = 2, Name = "Alpha", DistNm = 12, Own = true },
            };
            var s = Sorted(all);
            if (s.Count != 3) return "own ships only: " + s.Count;
            if (s[0].Uid != 1 || s[1].Uid != 2 || s[2].Uid != 3) return "by distance, then by name: " + s[0].Uid + s[1].Uid + s[2].Uid;
            if (Sorted(null).Count != 0) return "null list";
            if (DefaultIndex(s, 3, 1, 2) != 2) return "the ship of the order first";
            if (DefaultIndex(s, 0, 2, 3) != 1) return "then the chosen one";
            if (DefaultIndex(s, 0, 0, 3) != 2) return "then the base ship";
            if (DefaultIndex(s, 99, 0, 0) != 0 || DefaultIndex(s, 0, 0, 0) != 0) return "unknown uids: the nearest";
            if (DefaultIndex(new List<ShipItem>(), 1, 1, 1) != -1) return "empty list";
            var xy = new List<float[]> { new[] { 100f, 100f }, new[] { 140f, 100f }, new[] { 110f, 105f }, new[] { 400f, 400f } };
            var items = new List<ShipItem>
            {
                new ShipItem { Uid = 1, Own = true }, new ShipItem { Uid = 2, Own = true },
                new ShipItem { Uid = 7, Own = false }, new ShipItem { Uid = 4, Own = true },
            };
            if (Nearest(items, xy, 102, 101, 24) != 1) return "nearest within 24 px";
            if (Nearest(items, xy, 125, 100, 24) != 1 && Nearest(items, xy, 125, 100, 24) != 2) return "between two";
            if (Nearest(items, xy, 112, 105, 24) != 1) return "a foreign ship nearer than an own one does not win: " + Nearest(items, xy, 112, 105, 24);
            if (Nearest(items, xy, 110, 106, 4) != 0) return "a foreign ship alone: not selectable";
            if (Nearest(items, xy, 250, 250, 24) != 0) return "nothing in range";
            if (Nearest(items, xy, 140, 124f, 24) != 2) return "radius is inclusive";
            if (Nearest(items, xy, 140, 125f, 24) != 0) return "outside by a pixel";
            if (Nearest(null, xy, 0, 0, 24) != 0) return "null";
            return null;
        }
    }
}
