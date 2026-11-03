// T-173 / ADR-0074 item 2: the "Strike" mode of the ASW window: the order draft (guarded ship, zone radius, targets, airborne), the table of targets with
// "Do not attack" / "Attack". Pure C# on the UiKit (the stand aswwintest drives it). Part of AswWindowBody.cs.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal sealed partial class AswWindowBody
    {
        private AswStrikeDraft _strike;
        private readonly HashSet<int> _pickedTargets = new HashSet<int>();

        /// <summary>The draft of the strike order (stand access).</summary>
        internal AswStrikeDraft StrikeDraft { get { return _strike; } }

        private void DrawStrikeOrder(UiKit k, AswView v, AswGroupView sel)
        {
            var d = _strike;
            if (!k.Section("order", Tx("asw.sec.order"), null, true, AswWindowModel.ModeSummary(_mode))) return;
            k.LabelColumn(Tx("asw.lbl.mode"), Tx("asw.lbl.ref_ship"), Tx("asw.lbl.radius"), Tx("asw.lbl.targets"), Tx("asw.lbl.airborne"));
            if (ModeRow(k)) return;
            // T-178 A6 (ADR-0077 s.4): the guarded ship is a ship pick (own ships by distance, "On the map"), the radius a list, the airborne count segments; nothing is typed
            var geo = new List<double[]>();
            var ships = ShipItems(v, sel, false, geo);
            int refUid = d.RefUid != 0 ? d.RefUid : sel != null ? sel.RefUid : 0;
            if (refUid == 0 && ships.Count > 0)
            {
                int baseUid = 0;
                if (sel != null) foreach (var r in sel.MemberRows) if (r.ShipUid != 0) { baseUid = r.ShipUid; break; }
                var sorted = ShipPickModel.Sorted(ships);
                int di = ShipPickModel.DefaultIndex(sorted, 0, v.SelectedShipUid, baseUid);
                if (di >= 0) { refUid = sorted[di].Uid; d.RefUid = refUid; }   // what is shown is what is sent
            }
            if (refUid != 0 && !ships.Exists(x => x.Uid == refUid) && sel != null && !string.IsNullOrEmpty(sel.RefName) && refUid == sel.RefUid)
            {
                ships.Add(new ShipItem { Uid = refUid, Name = sel.RefName, Icon = "ntds:Surface:Own", DistNm = double.MaxValue, Own = true }); geo.Add(null);
            }
            k.Row(Tx("asw.lbl.ref_ship"));
            int pickedRef = ShipRow(k, "ref", "ref", ships, geo, refUid, Tx("asw.tip.ref_pick"));
            if (pickedRef != refUid && pickedRef != 0) d.RefUid = pickedRef;
            k.Row(Tx("asw.lbl.radius"));
            double rad = AswStrikePanel.RadiusValue(d.RadiusText);
            int rcur = Array.IndexOf(AswStrikePanel.RadiusList, AswPatternMath.Snap(rad, AswStrikePanel.RadiusList));
            var rl = new List<string>();
            foreach (double x in AswStrikePanel.RadiusList) rl.Add(x <= 0 ? Tx("asw.strike.radius_none") : x.ToString("0", CultureInfo.InvariantCulture));
            int rnow = k.Dropdown("radius", rl, rcur, Tx("asw.tip.radius_list"), 1, null, null, 0);
            if (rnow != rcur && rnow >= 0 && rnow < rl.Count) d.RadiusText = AswStrikePanel.RadiusList[rnow].ToString("0", CultureInfo.InvariantCulture);
            var tl = AswStrikePanel.TargetLabels();
            int cur = Array.IndexOf(AswStrikePanel.TargetKinds, d.Targets);
            k.Row(Tx("asw.lbl.targets"));
            var ttips = new List<string>(); foreach (string tlx in tl) ttips.Add(Tx("asw.tip.targets"));
            int nt = k.Radio("targets", tl, cur, ttips);
            if (nt != cur && nt >= 0) d.Targets = AswStrikePanel.TargetKinds[nt];
            k.Row(Tx("asw.lbl.airborne"));
            var al = new List<string>(); var at = new List<string>();
            for (int i = 0; i <= 4; i++) { al.Add(i.ToString(CultureInfo.InvariantCulture)); at.Add(Tx("asw.tip.airborne")); }
            int acur = (int)Math.Max(0, Math.Min(4, Math.Round(AswStrikePanel.AirborneValue(d.AirborneText))));
            int anow = k.Segments("airborne", al, acur, at, null, 0);
            if (anow != acur && anow >= 0) d.AirborneText = anow.ToString(CultureInfo.InvariantCulture);

            var st = AswStrikePanel.Action(sel, d);
            var stop = AswOrderPanel.Stop(sel);
            bool dirty = sel != null && sel.Order == "strike" && st.Enabled;
            string pressed = k.Footer(new UiFooter
            {
                Stop = new UiAction("stop", Tx("asw.btn.stop_icon", stop.Label), stop.Tip ?? Tx("asw.tip.stop_group"), stop.Enabled),
                Primary = new UiAction(st.Id, Tx("asw.btn.start_icon", st.Label), st.Tip ?? Tx(st.Id == "apply" ? "asw.tip.apply_order" : "asw.tip.start_order"), st.Enabled),
                Changed = dirty,
            });
            if (pressed == "stop" && stop.Enabled) { if (Send("asw_stop", new Dictionary<string, object> { { "group", sel.Name } })) Say(Tx("asw.say.stopped", sel.Name), UiTone.Ok); }
            else if (pressed != null && pressed == st.Id && st.Enabled) RunStrikeOrder(sel, d);
            else if (pressed != null && pressed == st.Id) _env.Log("asw window press " + pressed + " -> grey: " + (st.Tip ?? ""));
            if (dirty) k.HintSlot(Tx("asw.hint.dirty", Tx("asw.fld.extra")), UiTone.Warn);
            else if (sel != null && sel.Order != "strike" && sel.Order != "none") k.HintSlot(Tx("asw.hint.replace"), UiTone.Warn);
            else k.HintSlot(Tx("asw.strike.hint"), UiTone.Dim);
        }

        private void RunStrikeOrder(AswGroupView sel, AswStrikeDraft d)
        {
            string err;
            var cmd = AswStrikePanel.Command(sel, d, out err);
            if (err != null) { _env.Log("asw window press strike -> err " + err); Say(err == "bad_number" ? Tx("asw.err.bad_number") : err, UiTone.Error); return; }   // i18n-allow: error code compared, the text is a key
            _env.Log("asw window press " + (sel.Order == "strike" ? "apply" : "start_strike") + " -> strike");
            bool apply = sel.Order == "strike";
            if (!Send("asw_order", cmd)) return;
            d.Reload = true;
            Say(Tx(apply ? "asw.say.changed" : "asw.say.ordered"), UiTone.Ok);
        }

        /// <summary>The targets of the strike group: "Target | Status | Aircraft | Torp. | Hits" and the buttons of the ticked rows.</summary>
        private void StrikeContacts(UiKit k, AswGroupView sel)
        {
            if (sel == null || sel.Order != "strike") return;
            var rows = sel.StrikeTargets;
            if (!k.Section("contacts", Tx("asw.sec.contacts"), rows.Count.ToString(CultureInfo.InvariantCulture), true)) return;
            var spec = new UiTable
            {
                Id = "strike_targets",
                Columns = new[] { new UiColumn(Tx("asw.col.target"), 5f), new UiColumn(Tx("asw.col.status"), 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("asw.col.vehicle"), 6f) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.tor"), 3f, false, true), new UiColumn(Tx("asw.col.hits"), 3f, false, true) }
            };
            Func<int, int, string> cell = (r, c) => c == 0 ? rows[r].Track : c == 1 ? AswStrikePanel.StatusText(rows[r]) : c == 2 ? (rows[r].Shooter ?? "—") : c == 3 ? rows[r].Torpedoes.ToString(CultureInfo.InvariantCulture)
                : rows[r].Hits < 0 ? "—" : rows[r].Hits.ToString(CultureInfo.InvariantCulture);
            Func<int, UiTone> tone = r => rows[r].Skipped ? UiTone.Dim : rows[r].Status.EndsWith(".hold", StringComparison.Ordinal) || rows[r].Status.EndsWith(".need_fix", StringComparison.Ordinal) ? UiTone.Warn : rows[r].Status.EndsWith(".killed", StringComparison.Ordinal) ? UiTone.Ok : UiTone.Normal;
            k.Table(spec, rows.Count, cell, tone, _pickedTargets, r => rows[r].ContactId, 6, Tx("asw.strike.none"), r => RowClick(_pickedTargets, rows[r].ContactId), null);
            bool can = _pickedTargets.Count > 0;
            string pressed = k.Actions(new UiAction("skip", Tx("asw.strike.skip"), Tx(can ? "asw.strike.skip" : "asw.hint.pick_first"), can), new UiAction("resume", Tx("asw.strike.resume"), Tx(can ? "asw.strike.resume" : "asw.hint.pick_first"), can));
            if (pressed == "skip" || pressed == "resume")
            {
                string name = pressed == "skip" ? "asw_strike_skip" : "asw_strike_resume";
                foreach (int id in new List<int>(_pickedTargets)) Send(name, new Dictionary<string, object> { { "contact", (double)id } });
                _pickedTargets.Clear();
            }
        }
    }
}
