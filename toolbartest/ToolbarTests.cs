using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class ToolbarTests
    {
        /// <summary>Layout of game 0.8.7: the stand-level checks of ToolbarMath beyond its own SelfTest.</summary>
        public static string Math()
        {
            if (ToolbarMath.InsertColumn(new[] { 1, 2, 3, 4, 5 }) != 6) return "InsertColumn";
            var cells = new List<ToolbarCell>
            {
                new ToolbarCell { Id = 0, Column = 0, Span = 6 }, new ToolbarCell { Id = 1, Column = 0, Span = 6 }, new ToolbarCell { Id = 2, Column = 0, Span = 1 },
                new ToolbarCell { Id = 3, Column = 1, Span = 1 }, new ToolbarCell { Id = 4, Column = 5, Span = 1 }, new ToolbarCell { Id = 5, Column = 6, Span = 1 },
            };
            var e = ToolbarMath.Edits(cells, 6);
            if (e.Count != 3) return "edits " + e.Count;
            var back = ToolbarMath.Revert(e);
            foreach (var r in back)
            {
                var c = cells[r.Id];
                if (r.OldColumn == c.Column && r.OldSpan == c.Span) continue;
                // the revert of an edit starts from the edited values
                if (r.NewColumn != c.Column || r.NewSpan != c.Span) return "revert id " + r.Id;
            }
            if (ToolbarMath.Tip("ASW / SAR window ({0})", "Alt+U") != "ASW / SAR window (Alt+U)") return "tip";
            if (ToolbarMath.DeadReason(true, true, true, true, true, true, true) != null) return "dead alive";
            if (ToolbarMath.DeadReason(false, true, true, true, true, true, true) != "no_entries") return "dead no_entries";
            if (ToolbarMath.DeadReason(true, false, true, true, true, true, true) != "grid_gone") return "dead grid_gone";
            if (ToolbarMath.DeadReason(true, true, false, true, true, true, true) != "view_changed") return "dead view_changed";
            if (ToolbarMath.DeadReason(true, true, true, false, true, true, true) != "anchor_moved") return "dead anchor_moved";
            if (ToolbarMath.DeadReason(true, true, true, true, false, true, true) != "button_gone") return "dead button_gone";
            if (ToolbarMath.DeadReason(true, true, true, true, true, false, true) != "button_parent") return "dead button_parent";
            if (ToolbarMath.DeadReason(true, true, true, true, true, true, false) != "button_unloaded") return "dead button_unloaded";
            foreach (var w in new[] { "grid_gone", "view_changed", "no_entries" }) if (ToolbarMath.RevertBeforeReinsert(w)) return "revert " + w;
            foreach (var w in new[] { "anchor_moved", "button_gone", "button_parent", "button_unloaded" }) if (!ToolbarMath.RevertBeforeReinsert(w)) return "revert " + w;
            if (!ToolbarMath.ReinsertLogDue(1, 0, -1e9) || ToolbarMath.ReinsertLogDue(5, 30, 0) || !ToolbarMath.ReinsertLogDue(5, 60, 0)) return "log due";
            return null;
        }
    }
}
