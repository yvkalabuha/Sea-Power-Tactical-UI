using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    internal sealed partial class StrikeWindowBody
    {
        /// <summary>The ship picked on the map: it is the target at once (one target). Called by the shell.</summary>
        public void OnShipPicked(int id, StrikeView v)
        {
            var g = v == null ? null : Sel(v);
            if (g == null) { Say(Tx("strike.hint.no_group_selected"), UiTone.Warn); return; }
            _pickedShips.Clear(); _pickedShips.Add(id);
            _modeIdx = 0;
            SetTarget(g, new List<int> { id }, "single");
        }

        private void SetTarget(StrikeGroupView g, List<int> ids, string mode)
        {
            var cmd = new Dictionary<string, object> { { "group", g.Name }, { "mode", mode }, { "target_id", (double)ids[0] } };
            if (mode == "spread") cmd["track_ids"] = Uids(ids);
            string r = Send("strike_target", cmd);
            if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(Tx("strike.say.target"), UiTone.Ok);
        }

        private void DrawTarget(UiKit k, StrikeView v, StrikeGroupView g)
        {
            var alive = new HashSet<int>(v.Ships.Select(s => s.Id));
            _pickedShips.RemoveWhere(id => !alive.Contains(id));
            int valid = v.Ships.Count(s => s.Excl == null);
            k.Section("target", Tx("strike.sec.target"), I(valid) + "/" + I(v.Ships.Count), true);
            _modeIdx = k.Radio("mode", new[] { Tx("strike.mode.single"), Tx("strike.mode.spread") }, Math.Max(0, _modeIdx), new[] { Tx("strike.tip.mode"), Tx("strike.tip.mode") });
            var rows = v.Ships;
            var spec = new UiTable
            {
                Id = "ships",
                Columns = new[] { new UiColumn(Tx("strike.col.prio"), 1.5f, false, true), new UiColumn(Tx("strike.col.ship"), 6f) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("strike.col.ad"), 7f) { MaxEm = 14f }, new UiColumn(Tx("strike.col.tons"), 3.5f, false, true), new UiColumn(Tx("strike.col.sam"), 4.5f, false, true), new UiColumn(Tx("strike.col.status"), 0f, true) { MaxEm = 28f } },
            };
            Func<int, int, string> cell = (r, c) =>
            {
                var s = rows[r];
                switch (c)
                {
                    case 0: return s.Excl == null ? I(s.Prio) : "—";
                    case 1: return (string.IsNullOrEmpty(s.ClassName) ? "" : s.ClassName + " ") + "#" + I(s.Id);
                    case 2: return StrikeWindow.AdCell(s);
                    case 3: return StrikeWindow.TonsText(s.Tons);
                    case 4: return s.SamNm > 0 ? s.SamNm.ToString("0", CultureInfo.InvariantCulture) : "—";
                    default:
                        if (s.Excl != null) return StrikeWindow.ExclText(s.Excl);
                        if (g != null && g.MainId == s.Id) return Tx("strike.status.main") + "; " + StrikeWindow.PrioText(s);
                        if (g != null && g.Targets.Contains(s.Id)) return Tx("strike.status.in_set") + "; " + StrikeWindow.PrioText(s);
                        return StrikeWindow.PrioText(s);
                }
            };
            k.Table(spec, rows.Count, cell, r => rows[r].Excl != null ? UiTone.Dim : (g != null && g.MainId == rows[r].Id ? UiTone.Accent : UiTone.Normal), _pickedShips, r => rows[r].Id, 8, Tx("strike.hint.no_ships"));
            if (_env.PickActive) k.Hint(Tx("strike.hint.pick_active"), UiTone.Accent);
            string pressed = k.Actions(new UiAction("pick", Tx("strike.btn.pick"), Tx("strike.tip.pick"), g != null && !_env.PickActive), new UiAction("assign", Tx("strike.btn.assign"), Tx("strike.tip.assign"), g != null && _pickedShips.Count > 0));
            if (pressed == "pick") _env.StartPick();
            else if (pressed == "assign" && g != null)
            {
                var ids = rows.Where(s => _pickedShips.Contains(s.Id) && s.Excl == null).OrderBy(s => s.Prio).Select(s => s.Id).ToList();
                if (ids.Count == 0)
                {
                    var first = rows.FirstOrDefault(s => _pickedShips.Contains(s.Id));
                    Say(first != null && first.Excl != null ? Tx("strike.err.target_excluded", StrikeWindow.ExclText(first.Excl)) : Tx("strike.hint.pick_something"), UiTone.Warn);
                }
                else SetTarget(g, ids, _modeIdx == 1 ? "spread" : "single");
            }
        }
    }
}
