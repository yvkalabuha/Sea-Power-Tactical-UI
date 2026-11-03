using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    internal sealed partial class StrikeWindowBody
    {
        private static int PickId(StrikeAirRow r, int index) { return r.Uid != 0 ? r.Uid : -(index + 1); }

        private void DrawAir(UiKit k, StrikeView v, StrikeGroupView g)
        {
            // a ticked row that is gone is unticked
            var alive = new HashSet<int>();
            for (int i = 0; i < v.Air.Count; i++) alive.Add(PickId(v.Air[i], i));
            _pickedAir.RemoveWhere(id => !alive.Contains(id));
            int strikers = v.Air.Count(r => r.Why == null);
            k.Section("air", Tx("strike.sec.air"), I(strikers) + "/" + I(v.Air.Count), true);
            var rows = v.Air;
            var spec = new UiTable
            {
                Id = "air",
                Columns = new[] { new UiColumn(Tx("strike.col.name"), 6.5f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("strike.col.missiles"), 6f) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("strike.col.fuel"), 4.5f, false, true), new UiColumn(Tx("strike.col.where"), 0f, true) { MaxEm = UiColumn.StateMaxEm } },
            };
            Func<int, int, string> cell = (r, c) =>
            {
                var row = rows[r];
                switch (c)
                {
                    case 0: return row.Name ?? row.Type ?? "";
                    case 1: return row.Missiles.Count == 0 ? "—" : string.Join(", ", row.Missiles);
                    case 2: return row.FuelPct >= 0 ? row.FuelPct.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
                    default: return WhereText(row);
                }
            };
            k.Table(spec, rows.Count, cell, r => rows[r].Why != null ? UiTone.Dim : (rows[r].Group != null && g != null && rows[r].Group == g.Name ? UiTone.Ok : UiTone.Normal),
                _pickedAir, r => PickId(rows[r], r), 8, Tx("strike.hint.no_air"), null, Tx("strike.tip.row_focus"),
                (r, c) => { if (c == 0 && rows[r].Uid != 0) { string code = _env.FocusUnit(rows[r].Uid); if (code != null) Say(Tx("strike.say.focus_failed"), UiTone.Error); } }, null);
            string pressed = k.Actions(new UiAction("air.remove", Tx("strike.btn.remove"), Tx("strike.tip.remove"), g != null && _pickedAir.Count > 0), new UiAction("air.add", Tx("strike.btn.add"), Tx("strike.tip.add"), g != null && _pickedAir.Count > 0));
            if (pressed == null || g == null) return;
            var uids = new List<int>(); var keys = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (!_pickedAir.Contains(PickId(rows[i], i))) continue;
                bool mine = rows[i].Group == g.Name;
                if (pressed == "air.add" ? (rows[i].Group == null && rows[i].Why == null) : mine)
                {
                    if (rows[i].Uid != 0) uids.Add(rows[i].Uid); else if (rows[i].DeckKey != null) keys.Add(rows[i].DeckKey);
                }
            }
            if (uids.Count + keys.Count == 0)
            {
                // everything ticked is refused: say why of the first row
                var first = rows.Where((r, i) => _pickedAir.Contains(PickId(r, i))).FirstOrDefault();
                Say(first != null && first.Why != null ? Tx("strike.err.not_striker", StrikeWindow.WhyText(first.Why)) : Tx("strike.hint.pick_something"), UiTone.Warn);
                return;
            }
            var cmd = new Dictionary<string, object> { { "op", pressed == "air.add" ? "add" : "remove" }, { "group", g.Name } };
            if (uids.Count > 0) cmd["uids"] = Uids(uids);
            if (keys.Count > 0) cmd["deck_keys"] = keys.Select(x => (object)x).ToList();
            string r2 = Send("strike_group", cmd);
            if (Err(r2)) Say(StrikeWindow.ErrText(r2), UiTone.Error);
            else { Say(Tx(pressed == "air.add" ? "strike.say.added" : "strike.say.removed", I(uids.Count + keys.Count)), UiTone.Ok); _pickedAir.Clear(); }
        }

        private static string WhereText(StrikeAirRow r)
        {
            if (r.Group != null && r.Why == null) return r.Group + (r.Ew ? ", " + Tx("strike.where.ew") : "") + (r.Where == "deck" ? ", " + Tx("strike.where.deck") : "");
            if (r.Why != null) return StrikeWindow.WhyText(r.Why);
            if (r.Ew && r.Where != "deck") return Tx("strike.where.ew") + ", " + Tx("strike.where.air");
            if (r.Where == "deck") return r.ReadyInS > 1 ? Tx("strike.where.deck_ready", StrikeWindow.Mmss(r.ReadyInS)) : Tx("strike.where.deck_ready_now");
            return Tx("strike.where.air");
        }
    }
}
