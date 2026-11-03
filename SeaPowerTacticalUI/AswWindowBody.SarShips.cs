// T-161: the "Ships and submarines" section of the SAR tab of the ASW window: own ships / subs with ticks, the radius draft and the two buttons.
// Pure C#. Rows come from the socket SarShips (once a second of real time, only while the section is open); nothing is sent before a button is pressed.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class AswWindowBody
    {
        /// <summary>T-161 socket: rows of own ships / subs (null = SAR is off: the section is not drawn). Wired by the asw module. Reads the game: called at most once a second.</summary>
        internal Func<IList<SarShipRow>> SarShips;

        private const double ShipRowsEveryS = 1.0;
        private List<SarShipRow> _shipRows;
        private double _shipRowsAt = double.NegativeInfinity;
        private readonly HashSet<int> _pickedShips = new HashSet<int>();
        private int _shipRadius = 3;   // index in SarShipPanel.Radii; default "All"

        private void InvalidateShipRows() { _shipRowsAt = double.NegativeInfinity; }

        private void RefreshShipRows()
        {
            double now = _env.Now();
            if (_shipRows != null && now >= _shipRowsAt && now - _shipRowsAt < ShipRowsEveryS) return;
            IList<SarShipRow> l = null;
            try { l = SarShips(); }
            catch (Exception e) { _env.Log("asw window sar ships failed: " + e.GetType().Name); }
            _shipRowsAt = now;
            _shipRows = l == null ? new List<SarShipRow>() : SarShipPanel.Sorted(l);
            var alive = new HashSet<int>();
            foreach (var r in _shipRows) alive.Add(r.Uid);
            _pickedShips.RemoveWhere(u => !alive.Contains(u));
        }

        private void DrawSarShips(UiKit k)
        {
            if (SarShips == null) return;
            bool open = k.Section("sar_ships", Tx("sar.ships.sec"), SarShipPanel.Counter(_shipRows), true);
            if (!open) return;
            RefreshShipRows();
            var rows = _shipRows;
            if (rows.Count == 0) { k.Hint(SarShipPanel.Hint(rows, _pickedShips)); return; }
            var spec = new UiTable
            {
                Id = "sar_ships",
                Columns = new[] { new UiColumn(Tx("sar.ships.col.ship"), 7f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("sar.ships.col.type"), 5f), new UiColumn(Tx("sar.ships.col.state"), 0f, true) { MaxEm = UiColumn.StateMaxEm } }
            };
            k.Table(spec, rows.Count, (r, c) => c == 0 ? rows[r].Name : c == 1 ? rows[r].Type : SarShipPanel.StateText(rows[r]), r => SarShipPanel.RowTone(rows[r]), _pickedShips, r => rows[r].Uid, 6,
                SarShipPanel.Hint(rows, _pickedShips), r => RowClick(_pickedShips, rows[r].Uid), Tx("sar.ships.tip.row"),
                (r, c) => { if (c == 0 && r >= 0 && r < rows.Count) FocusRow(rows[r].Uid); }, (r, c) => r >= 0 && r < rows.Count && c == 0 ? Tx("asw.tip.link", rows[r].Name) : null);
            k.LabelColumn(Tx("sar.ships.lbl.radius"));
            k.Row(Tx("sar.ships.lbl.radius"));
            var rtips = new List<string>(); foreach (var rl in SarShipPanel.RadiusLabels()) rtips.Add(Tx("sar.ships.tip.radius"));
            int rad = k.Segments("shiprad", SarShipPanel.RadiusLabels(), _shipRadius, rtips, null, SarShipPanel.RadiusLabels().Count - 1);
            if (rad >= 0) _shipRadius = rad;
            k.Unit(SarPanel.MilesLabel());
            var stop = SarShipPanel.Stop(rows, _pickedShips);
            var start = SarShipPanel.Start(rows, _pickedShips);
            string pressed = k.Footer(new UiFooter { Stop = new UiAction(stop.Id, stop.Label, stop.Tip, stop.Enabled), Primary = new UiAction(start.Id, start.Label, start.Tip, start.Enabled) });   // T-178 A3
            if (pressed == stop.Id && stop.Enabled) SendShips("stop", rows);
            else if (pressed == start.Id && start.Enabled) SendShips("start", rows);
            string hint = SarShipPanel.Hint(rows, _pickedShips);
            if (hint != null) k.Hint(hint);
        }

        /// <summary>One sar_ship per ticked ship that fits the op; the answers (and the busy ones "Start" skips) go into one line.</summary>
        private void SendShips(string op, IList<SarShipRow> rows)
        {
            var byUid = new Dictionary<int, SarShipRow>();
            foreach (var r in rows) byUid[r.Uid] = r;
            var results = new List<KeyValuePair<SarShipRow, string>>();
            foreach (var cmd in SarShipPanel.Commands(op, rows, _pickedShips, SarShipPanel.Radii[Math.Max(0, Math.Min(_shipRadius, SarShipPanel.Radii.Length - 1))]))
            {
                _env.Log("asw window cmd sar_ship " + op);
                string ack = null;
                try { ack = _send("sar_ship", cmd); }
                catch (Exception e) { _env.Log("asw window cmd sar_ship failed: " + e.GetType().Name); }
                LastAck = ack;
                results.Add(new KeyValuePair<SarShipRow, string>(byUid[(int)(double)cmd["uid"]], ack));
            }
            if (op == "start") results.AddRange(SarShipPanel.Skipped(rows, _pickedShips));
            InvalidateShipRows();
            UiTone tone;
            string say = SarShipPanel.Summary(op, results, out tone);
            Say(say, tone);
        }
    }
}
