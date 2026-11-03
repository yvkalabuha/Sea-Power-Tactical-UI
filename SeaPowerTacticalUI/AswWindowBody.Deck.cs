// T-119 I1: the "waiting for the deck (CAP)" label of the ASW / SAR window (a segment of the one-line group status in AswWindowBody.cs). Pure C#.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class AswWindowBody
    {
        /// <summary>null = not waiting.</summary>
        internal static string DeckWaitText(AswGroupView g)
        {
            if (g == null || g.DeckWaitS < 0) return null;
            return Tx("asw.status.deck_wait", AswWindowModel.MmSs(g.DeckWaitS));
        }

        /// <summary>T-181 (ADR-0080 s.8): hangar rows are positional, a ticked uid may be gone after a refresh: the tick is dropped. Returns how many were dropped.</summary>
        internal static int PruneVanished(ISet<int> picked, IList<AswRow> rows)
        {
            if (picked == null || picked.Count == 0) return 0;
            var alive = new HashSet<int>();
            if (rows != null) foreach (var r in rows) alive.Add(r.Uid);
            var gone = new List<int>();
            foreach (int u in picked) if (!alive.Contains(u)) gone.Add(u);
            foreach (int u in gone) picked.Remove(u);
            return gone.Count;
        }

        /// <summary>T-181: the tip of the name cell of a hangar row ("in the hangar: N of this type"); null = not a hangar row.</summary>
        internal static string HangarTip(AswRow r)
        {
            return r == null || r.HangarN <= 0 ? null : Tx("asw.tip.hangar_n", r.HangarN);
        }

        /// <summary>T-181 selftest of the pure helpers (aswwintest t181).</summary>
        internal static string T181SelfTest()
        {
            var picked = new HashSet<int> { 1, 2, 3 };
            var rows = new List<AswRow> { new AswRow { Uid = 1, Name = "SH-3H" }, new AswRow { Uid = 3, Name = "SH-3H" } };
            if (PruneVanished(picked, rows) != 1 || picked.Count != 2 || picked.Contains(2)) return "prune";
            if (PruneVanished(new HashSet<int>(), rows) != 0 || PruneVanished(null, rows) != 0) return "prune empty";
            if (HangarTip(new AswRow { HangarN = 5 }) != "в ангаре: 5 машин этого типа") return "hangar tip: " + HangarTip(new AswRow { HangarN = 5 });
            if (HangarTip(new AswRow()) != null || HangarTip(null) != null) return "hangar tip none";
            var three = new List<AswRow>();
            for (int k = 1; k <= 3; k++) three.Add(new AswRow { Uid = 100 + k, Name = "SH-3H", Slot = k, HangarN = 3 });
            AswWindowModel.SortRows(three, "name", true);
            for (int k = 0; k < 3; k++) if (three[k].Slot != k + 1) return "slot order after the name sort";
            return null;
        }
    }
}
