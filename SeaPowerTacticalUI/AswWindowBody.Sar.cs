// T-148 A5 (T-147 G2): the SAR tab of the ASW window: the order section by SarPanel (draft + one action button). Pure C#.
// Drawing / pointing on the map fill the draft; nothing is sent until "Start rescue" / "Apply" (the only place that sends the SAR order is StartSar).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal sealed partial class AswWindowBody
    {
        private const string KStarted = "started", KApplied = "applied", KStopped = "stopped";   // kinds of SarPanel.SayText, not texts

        // ---------------------------------------------------------------- order: SAR

        private static SarPanelGroup PanelGroup(AswGroupView g)
        {
            if (g == null) return null;
            return new SarPanelGroup
            {
                Name = g.Name, Order = g.Order, Area = g.SarArea, OnComplete = g.SarOnComplete, Rect = g.SarRect,
                Lat = g.SarLat, Lon = g.SarLon, RadiusNm = g.SarRadiusNm, Members = g.Members.Count,
                OrderText = g.Order == "course" || g.Order == "area" ? AswWindowModel.OrderText(g) : g.Order
            };
        }

        /// <summary>Reloads the SAR draft when the group or its running order changed (the controller's data are the truth), keeps the edits otherwise.</summary>
        private void SyncSarDraft(AswGroupView sel)
        {
            string key = sel == null ? "" : sel.Name + "|" + sel.Order + "|" + sel.SarArea + "|" + sel.SarOnComplete + "|" + sel.SarLat.ToString("0.#####", CultureInfo.InvariantCulture)
                + "|" + sel.SarLon.ToString("0.#####", CultureInfo.InvariantCulture) + "|" + sel.SarRadiusNm.ToString("0.##", CultureInfo.InvariantCulture)
                + "|" + (sel.SarRect == null ? "" : string.Join(",", sel.SarRect));
            if (_sar != null && key == _sarKey) return;
            _sarKey = key;
            _sar = SarPanel.DraftFrom(PanelGroup(sel), _sar);
        }

        private void DrawSarOrder(UiKit k, AswView v, AswGroupView sel)
        {
            if (!k.Section("order", Tx("asw.sec.order"), null, true, AswWindowModel.ModeSummary(_mode))) return;
            k.LabelColumn(Tx("asw.lbl.mode"), SarPanel.WhereLabel(), SarPanel.AfterLabel(), Tx("sar.ui.radius"));
            if (ModeRow(k)) return;
            if (v != null && v.RaftsInWater >= 0) k.Hint(Tx("sar.ui.in_water", v.RaftsInWater, v.PeopleInWater));   // 0.27.10: what there is to rescue, before an order too
            var pg = PanelGroup(sel);
            var d = _sar;
            bool drawing = WantInput == "sar_rect", pointing = WantInput == "sar_point";
            var ab = SarPanel.AreaButtons(d, drawing, pointing);
            var al = new List<string> { ab[0].Label, drawing ? Tx("sar.area.drawing") : ab[1].Label, pointing ? Tx("sar.area.pointing") : ab[2].Label };
            int acur = drawing ? 1 : pointing ? 2 : d.Area == "rect" ? 1 : d.Area == "circle" ? 2 : 0;
            k.Row(SarPanel.WhereLabel());
            // T-178 A3 (ADR-0077 s.1): a single choice = segments; a click on "rectangle" / "circle" starts the map input, RMB / Esc / "Cancel" bring the previous area back
            var atips = new List<string> { ab[0].Tip, ab[1].Tip, ab[2].Tip };
            int hit = k.Segments("swhere", al, acur, atips, null, 0);
            if (hit != acur && hit >= 0)
            {
                if (hit == 0) { _sarPrev = null; WantInput = null; d.Area = "all"; }
                else
                {
                    _sarPrev = new SarDraft { Area = d.Area, Rect = d.Rect, HasPoint = d.HasPoint, Lat = d.Lat, Lon = d.Lon };
                    d.Area = hit == 1 ? "rect" : "circle"; WantInput = hit == 1 ? "sar_rect" : "sar_point";
                    Say(Tx(hit == 1 ? "sar.ui.draw_hint" : "sar.ui.point_hint"), UiTone.Accent);
                }
            }
            if (WantInput == "sar_rect" || WantInput == "sar_point")
                if (k.Button("swhere.cancel", Tx("ui.map.cancel"), Tx("ui.map.cancel.tip"))) CancelInput(WantInput == "sar_point" ? "sar.ui.cancelled" : "asw.say.draw_cancelled");
            k.Row(Tx("sar.ui.radius"));
            // T-176 W4 (ADR-0077): the radius of a circle is chosen from a list (SarRadiusList), the current value sticks to the nearest item; nothing is typed
            double rad = ParseRadius(d.RadiusText);
            if (rad <= 0) rad = 20;
            int rcur = IndexOfValue(AswPatternMath.SarRadiusList, rad);
            int rnow = k.Dropdown("sradius", Labels(AswPatternMath.SarRadiusList), rcur, Tx("sar.ui.radius"), 1, null, null, IndexOfValue(AswPatternMath.SarRadiusList, 20));
            if (rnow != rcur && rnow >= 0 && rnow < AswPatternMath.SarRadiusList.Length) d.RadiusText = AswPatternMath.SarRadiusList[rnow].ToString("0", CultureInfo.InvariantCulture);
            k.Unit(SarPanel.MilesLabel());
            var fb = SarPanel.AfterButtons(d);
            var fl = new List<string> { fb[0].Label, fb[1].Label }; var ft = new List<string> { fb[0].Tip, fb[1].Tip };
            k.Row(SarPanel.AfterLabel());
            int fcur = d.OnComplete == "loiter" ? 1 : 0;
            int nf = k.Segments("safter", fl, fcur, ft, null, 0);
            if (nf != fcur && nf >= 0) d.OnComplete = nf == 1 ? "loiter" : "rtb";

            var action = SarPanel.Action(pg, d, _pickedAvail.Count);
            var stop = SarPanel.Stop(pg);
            string hint = SarPanel.Hint(pg, d, _pickedAvail.Count);
            bool dirty = pg != null && pg.Order == "sar" && action.Enabled;
            string actionText = Tx(action.Id == "apply" ? "asw.btn.sar_apply" : "asw.btn.sar_start");   // T-155: "> Apply" / "> Start rescue"; the state logic stays in SarPanel
            string pressed = k.Footer(new UiFooter   // T-178 A3: [Stop] gap [Start rescue / Apply], "Changed" on the left
            {
                Stop = new UiAction("sar_stop", Tx("asw.btn.sar_stop"), stop.Tip, stop.Enabled),
                Primary = new UiAction(action.Id, actionText, action.Tip, action.Enabled),
                Changed = dirty,
            });
            if (pressed == "sar_stop" && stop.Enabled) { if (Send("asw_stop", new Dictionary<string, object> { { "group", sel.Name } })) Say(SarPanel.SayText(KStopped, sel.Name, ""), UiTone.Ok); }
            else if (pressed != null && pressed == action.Id && action.Enabled) StartSar(v, sel, d, action.Id == "apply");
            k.HintSlot(hint, UiTone.Warn);   // reserved room: the window never grows/jumps when the hint appears
        }

        private SarDraft _sarPrev;   // the area before "rectangle" / "circle" was pressed: RMB / Esc / "Cancel" bring it back

        private void StartSar(AswView v, AswGroupView sel, SarDraft d, bool apply)
        {
            string group = sel == null ? null : sel.Name;
            if (group == null)
            {
                // no group yet: the ticked helicopters become SAR-N first, the order follows when that worked
                if (Refused(v.ExcludedSar, _pickedAvail)) return;
                group = SarPanel.NextGroupName(GroupNames(v));
                if (!Send("asw_group", GroupCmd("create", group, _pickedAvail, "sar"))) return;
                _pickedAvail.Clear();
                _group = group;
            }
            string err;
            var cmd = SarPanel.OrderCommand(group, d, out err);
            if (cmd == null) { Say(SarPanel.ErrText(err), UiTone.Error); return; }
            if (!Send("sar_order", cmd)) return;
            WantInput = null;
            var g = new AswGroupView { SarArea = d.Area, SarRadiusNm = ParseRadius(d.RadiusText) };
            Say(SarPanel.SayText(apply ? KApplied : KStarted, group, AswWindowModel.SarAreaText(g) + " · " + AswWindowModel.SarOnCompleteText(d.OnComplete)), UiTone.Ok);
        }

        private static double ParseRadius(string s)
        {
            double r;
            return double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : 0;
        }
    }
}
