using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>A child of the panel grid: Id = index in Children.</summary>
    internal struct ToolbarCell { public int Id, Column, Span; public bool Ours; }
    internal struct ToolbarCellEdit { public int Id, OldColumn, NewColumn, OldSpan, NewSpan; }
    internal enum ToolbarAction { None, Insert, Check, Remove }

    /// <summary>T-174 (ADR-0073 section 3), pure: where the two buttons go on the game panel grid and what happens to the other children.</summary>
    internal static class ToolbarMath
    {
        public const string TagAsw = "VB_TB_ASW", TagCap = "VB_TB_CAP";
        public const int Added = 2;
        public const double CheckS = 1.0;
        public static readonly string[] AnchorNames = { "UnitStatusWindow", "CameraControlsWindow", "MapWindow", "EventLogWindow", "FormationManagerWindow" };

        /// <summary>max + 1; -1 for an empty list.</summary>
        public static int InsertColumn(IList<int> anchorColumns)
        {
            if (anchorColumns == null || anchorColumns.Count == 0) return -1;
            int m = int.MinValue;
            foreach (int c in anchorColumns) if (c > m) m = c;
            return m + 1;
        }

        /// <summary>The anchor stands in the last row of the grid.</summary>
        public static bool AnchorRowOk(int anchorRow, int rowCount) { return rowCount > 0 && anchorRow == rowCount - 1; }

        /// <summary>
        /// Other children: Column >= n -> Column += 2 (foreign buttons to the right of the insertion point move);
        /// otherwise a child spanning several columns up to the edge (Span >= 2, Column + Span >= n) -> Span += 2. A single-cell child that ends at the edge
        /// (a button in the last column) is not stretched. Our own children (Ours) are not touched.
        /// </summary>
        public static List<ToolbarCellEdit> Edits(IList<ToolbarCell> cells, int n)
        {
            var res = new List<ToolbarCellEdit>();
            if (cells == null) return res;
            foreach (var c in cells)
            {
                if (c.Ours) continue;
                if (c.Column >= n) res.Add(new ToolbarCellEdit { Id = c.Id, OldColumn = c.Column, NewColumn = c.Column + Added, OldSpan = c.Span, NewSpan = c.Span });
                else if (c.Span >= 2 && c.Column + c.Span >= n) res.Add(new ToolbarCellEdit { Id = c.Id, OldColumn = c.Column, NewColumn = c.Column, OldSpan = c.Span, NewSpan = c.Span + Added });
            }
            return res;
        }

        /// <summary>Inverse edits in reverse order.</summary>
        public static List<ToolbarCellEdit> Revert(IList<ToolbarCellEdit> done)
        {
            var res = new List<ToolbarCellEdit>();
            if (done == null) return res;
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var e = done[i];
                res.Add(new ToolbarCellEdit { Id = e.Id, OldColumn = e.NewColumn, NewColumn = e.OldColumn, OldSpan = e.NewSpan, NewSpan = e.OldSpan });
            }
            return res;
        }

        /// <summary>Format with " ({0})"; an empty hotkey drops the brackets.</summary>
        public static string Tip(string format, string hotkey)
        {
            if (format == null) return "";
            if (string.IsNullOrEmpty(hotkey)) return format.Replace(" ({0})", "").Replace("{0}", "").Trim();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, hotkey);
        }

        /// <summary>Hidden (not Collapsed): the Auto columns keep their width, the game panel does not jump.</summary>
        public static string VisibilityName(bool hidden) { return hidden ? "Hidden" : "Visible"; }

        /// <summary>
        /// Setting off and something inserted -> Remove. off (gave up) or not in a mission -> None. Not inserted (or not alive) -> Insert at nextTry.
        /// Inserted -> Check every CheckS (the caller passes alive = true between checks).
        /// </summary>
        public static ToolbarAction Next(bool enabled, bool inMission, bool off, bool inserted, bool alive, double now, double nextTry, double nextCheck)
        {
            if (!enabled) return inserted ? ToolbarAction.Remove : ToolbarAction.None;
            if (off || !inMission) return ToolbarAction.None;
            if (!inserted || !alive) return now >= nextTry ? ToolbarAction.Insert : ToolbarAction.None;
            return now >= nextCheck ? ToolbarAction.Check : ToolbarAction.None;
        }

        public const double ReinsertLogS = 60.0;

        /// <summary>null = alive; otherwise the first failed check, in the order of the Alive check.</summary>
        public static string DeadReason(bool hasEntries, bool gridKnown, bool viewSame, bool anchorInGrid, bool buttonsKnown, bool buttonsInGrid, bool buttonsLoaded)
        {
            if (!hasEntries) return "no_entries";
            if (!gridKnown) return "grid_gone";
            if (!viewSame) return "view_changed";
            if (!anchorInGrid) return "anchor_moved";
            if (!buttonsKnown) return "button_gone";
            if (!buttonsInGrid) return "button_parent";
            if (!buttonsLoaded) return "button_unloaded";
            return null;
        }

        /// <summary>true = the old grid is still the game's one: revert our edits there before a fresh insertion; false = dead tree, only forget.</summary>
        public static bool RevertBeforeReinsert(string reason)
        {
            return reason == "anchor_moved" || reason == "button_gone" || reason == "button_parent" || reason == "button_unloaded";
        }

        /// <summary>The first reinsert of a mission is logged at once, the next ones at most every ReinsertLogS.</summary>
        public static bool ReinsertLogDue(int sinceLast, double now, double lastLogAt)
        {
            return sinceLast > 0 && now - lastLogAt >= ReinsertLogS;
        }

        private static string _fail;
        private static void Eq(string what, bool ok) { if (!ok && _fail == null) _fail = what; }

        public static string SelfTest()
        {
            _fail = null;
            Eq("InsertColumn", InsertColumn(new[] { 1, 2, 3, 4, 5 }) == 6 && InsertColumn(new int[0]) == -1 && InsertColumn(null) == -1);
            var cells = new List<ToolbarCell>
            {
                new ToolbarCell { Id = 0, Column = 0, Span = 6 },   // background
                new ToolbarCell { Id = 1, Column = 0, Span = 6 },   // compass
                new ToolbarCell { Id = 2, Column = 0, Span = 1 },   // unit panel
                new ToolbarCell { Id = 3, Column = 1, Span = 1 }, new ToolbarCell { Id = 4, Column = 2, Span = 1 }, new ToolbarCell { Id = 5, Column = 3, Span = 1 },
                new ToolbarCell { Id = 6, Column = 4, Span = 1 }, new ToolbarCell { Id = 7, Column = 5, Span = 1 },
            };
            var ed = Edits(cells, 6);
            Eq("edits count", ed.Count == 2 && ed[0].Id == 0 && ed[1].Id == 1);
            Eq("edits span", ed.Count == 2 && ed[0].NewSpan == 8 && ed[0].OldSpan == 6 && ed[1].NewSpan == 8 && ed[0].NewColumn == 0);
            cells.Add(new ToolbarCell { Id = 8, Column = 6, Span = 1 });
            ed = Edits(cells, 6);
            var foreign = ed.Find(e => e.Id == 8);
            Eq("foreign button moves", ed.Count == 3 && foreign.NewColumn == 8 && foreign.NewSpan == 1);
            cells.Add(new ToolbarCell { Id = 9, Column = 6, Span = 1, Ours = true });
            cells.Add(new ToolbarCell { Id = 10, Column = 0, Span = 8, Ours = true });
            Eq("ours untouched", Edits(cells, 6).Count == 3);
            var rev = Revert(ed);
            Eq("revert reversed", rev.Count == 3 && rev[0].Id == 8 && rev[2].Id == 0);
            Eq("revert values", rev[0].NewColumn == 6 && rev[2].NewSpan == 6 && rev[2].OldSpan == 8);
            Eq("tip ru", Tip("Окно ПВО ({0})", "Alt+K") == "Окно ПВО (Alt+K)" && Tip("Окно ПВО ({0})", "") == "Окно ПВО");
            Eq("tip en", Tip("CAP window ({0})", "Alt+K") == "CAP window (Alt+K)" && Tip("CAP window ({0})", null) == "CAP window");
            Eq("visibility", VisibilityName(true) == "Hidden" && VisibilityName(false) == "Visible");
            Eq("anchor row", AnchorRowOk(2, 3) && !AnchorRowOk(1, 3) && !AnchorRowOk(0, 0));
            Eq("next off", Next(false, true, false, true, true, 0, 0, 0) == ToolbarAction.Remove && Next(false, true, false, false, true, 0, 0, 0) == ToolbarAction.None);
            Eq("next out of mission", Next(true, false, false, true, true, 10, 0, 0) == ToolbarAction.None);
            Eq("next gave up", Next(true, true, true, false, true, 10, 0, 0) == ToolbarAction.None);
            Eq("next insert", Next(true, true, false, false, false, 5, 5, 0) == ToolbarAction.Insert && Next(true, true, false, false, false, 4, 5, 0) == ToolbarAction.None);
            Eq("next dead -> insert at nextTry", Next(true, true, false, true, false, 6, 5, 0) == ToolbarAction.Insert);
            Eq("next check", Next(true, true, false, true, true, 2, 0, 1) == ToolbarAction.Check && Next(true, true, false, true, true, 0.5, 0, 1) == ToolbarAction.None);

            int opened = 0, setCalls = 0;
            var t = new ToolbarToggle(() => opened == 1, v => { setCalls++; opened = v ? 1 : 0; });
            Eq("toggle initial", !t.Shown && t.Pull() == null);
            opened = 1;
            Eq("pull follows window", t.Pull() == true && t.Shown && t.Pull() == null && setCalls == 0);
            Eq("click closes once", t.Click() == false && setCalls == 1 && !t.Shown);
            Eq("click opens once", t.Click() == true && setCalls == 2 && t.Shown);
            return _fail;
        }
    }

    /// <summary>Without echo: a programmatic IsChecked never calls SetOpen (the handler is on Click only).</summary>
    internal sealed class ToolbarToggle
    {
        private readonly Func<bool> _isOpen;
        private readonly Action<bool> _setOpen;
        private bool _shown;

        public ToolbarToggle(Func<bool> isOpen, Action<bool> setOpen) { _isOpen = isOpen; _setOpen = setOpen; _shown = isOpen != null && isOpen(); }

        public bool Shown { get { return _shown; } }

        /// <summary>Every frame: the new value for IsChecked / lit or null.</summary>
        public bool? Pull()
        {
            bool open = _isOpen();
            if (open == _shown) return null;
            _shown = open;
            return open;
        }

        /// <summary>User input: SetOpen(!IsOpen()); returns IsOpen() for IsChecked.</summary>
        public bool Click()
        {
            _setOpen(!_isOpen());
            _shown = _isOpen();
            return _shown;
        }
    }
}
