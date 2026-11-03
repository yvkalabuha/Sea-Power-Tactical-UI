// T-176 W2-W7 (ADR-0076 s.7, ADR-0077): the order section of the ASW window on the widgets: chips of the tactics, segments of the width, drop-down lists of the standoff / distance /
// size, the compass of the bearing. Nothing is typed. Pure C#: no Unity, no game.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed partial class AswWindowBody
    {
        private static string DepthLabel(string d) { return Tx(d == "above" ? "asw.depth.above" : d == "below" ? "asw.depth.below" : "asw.depth.alternate"); }

        private static readonly string[] Depths = { "auto", "above", "below", "alternate" };

        private static List<string> Labels(double[] list)
        {
            var l = new List<string>();
            foreach (double v in list) l.Add(v.ToString("0", CultureInfo.InvariantCulture));
            return l;
        }

        private static int IndexOfValue(double[] list, double v) { return Array.IndexOf(list, AswPatternMath.Snap(v, list)); }

        /// <summary>A value of a list: segments (few values) or a drop-down; true = the value changed (the draft is marked).</summary>
        private bool PickList(UiKit k, string id, double[] list, ref double value, string tip, bool segments, double dflt)
        {
            int cur = IndexOfValue(list, value);
            var labels = Labels(list);
            int now;
            if (segments) { var tl = new List<string>(); foreach (string x in labels) tl.Add(tip); now = k.Segments(id, labels, cur, tl, null, IndexOfValue(list, dflt)); }
            else now = k.Dropdown(id, labels, cur, tip, 1, null, null, IndexOfValue(list, dflt));
            if (now == cur || now < 0 || now >= list.Length) return false;
            value = list[now];
            return true;
        }

        /// <summary>The "Depth" row: auto (with what it comes to), above, below, alternate.</summary>
        private void DepthRow(UiKit k, AswOrderDraft d)
        {
            string resolved = AswPatternMath.DepthAuto(d.Mode == "area" ? new List<string> { d.AreaPattern } : d.Effective(), d.Mode, 0);
            var dl = new List<string> { Tx("asw.depth.auto", DepthLabel(resolved)), DepthLabel("above"), DepthLabel("below"), DepthLabel("alternate") };
            int dcur = Array.IndexOf(Depths, d.Depth);
            k.Row(Tx("asw.lbl.depth"));
            var dtips = new List<string>(); foreach (string dname in Depths) dtips.Add(DepthTip(dname));
            int nd = k.Segments("depth", dl, dcur, dtips, null, 0);
            if (nd != dcur && nd >= 0) d.Depth = Depths[nd];
        }

        private static readonly double[] RowsListD = { 1, 2, 3, 4, 5 };   // T-229: AswPatternMath.BarrierRowsList as the doubles PickList takes

        private void CourseRows(UiKit k, AswGroupView sel, AswOrderDraft d)
        {
            var opts = AswWindowModel.TacticsFor("course", sel);
            var labels = new List<string>(); var tips = new List<string>(); var en = new List<bool>();
            foreach (var o in opts) { labels.Add(o.Label); tips.Add(o.Enabled ? TacticTip(o.Tactic) : o.DisabledReason); en.Add(o.Enabled); }
            k.Row(Tx("asw.lbl.tactic"));
            var wasOn = (bool[])d.On.Clone();
            if (k.Toggles("tactic", labels, d.On, tips, en))
            {
                d.ApplyExclusive(wasOn);   // T-334 / ADR-0130: Corridor and Barrier exclude each other, the pressed one stays
                _env.Log("asw window press tactic -> " + d.Combo + " (course, exclusive)");
            }
            else if (k.DisabledClick != null) _env.Log("asw window press tactic (grey) -> refused: " + k.DisabledClick);
            if (d.Chosen().Count == 0) k.Unit(Tx("asw.tactic.auto"));
            var eff = d.Effective();
            var def = new AswPatternParams();
            if (eff.Contains("dip"))
            {
                k.Row(Tx("asw.lbl.dip"));
                k.Unit(Tx("asw.lbl.standoff"));
                double l = d.Course.DipLNm;
                if (PickList(k, "dip_l", AswPatternMath.StandoffList, ref l, Tx("asw.lbl.standoff"), false, def.DipStandoffNm)) { d.Course.DipLNm = l; d.GeoDirty = true; }
            }
            if (eff.Contains("corridor"))
            {
                k.Row(Tx("asw.lbl.corridor"));
                double x = d.Course.CorridorXNm;
                if (PickList(k, "cor_x", AswPatternMath.WidthList, ref x, Tx("asw.lbl.width_nm"), true, def.CorridorWidthNm)) { d.Course.CorridorXNm = x; d.GeoDirty = true; }
                k.Wrap();
                k.Unit(Tx("asw.lbl.standoff"));
                double l = d.Course.CorridorLNm;
                if (PickList(k, "cor_l", AswPatternMath.StandoffList, ref l, Tx("asw.lbl.standoff"), false, def.CorridorStandoffNm)) { d.Course.CorridorLNm = l; d.GeoDirty = true; }
            }
            if (eff.Contains("barrier"))
            {
                k.Row(Tx("asw.lbl.barrier"));
                double x = d.Course.BarrierXNm;
                if (PickList(k, "bar_x", AswPatternMath.WidthList, ref x, Tx("asw.lbl.width_nm"), true, def.BarrierWidthNm)) { d.Course.BarrierXNm = x; d.GeoDirty = true; }
                // T-229: the sliding barrier: how many rows and how far apart (the standoff is not used by it)
                k.Wrap();
                k.Unit(Tx("asw.lbl.rows"));
                double n = d.Course.BarrierRows;
                if (PickList(k, "bar_n", RowsListD, ref n, Tx("asw.lbl.rows"), true, def.BarrierRows)) { d.Course.BarrierRows = (int)n; d.GeoDirty = true; }
                k.Wrap();
                k.Unit(Tx("asw.lbl.gap"));
                double gp = d.Course.BarrierGapNm;
                if (PickList(k, "bar_gap", AswPatternMath.BarrierGapList, ref gp, Tx("asw.lbl.gap"), false, def.BarrierGapNm)) { d.Course.BarrierGapNm = gp; d.GeoDirty = true; }
            }
        }

        private void AreaRows(UiKit k, AswGroupView sel, AswOrderDraft d)
        {
            var opts = AswWindowModel.TacticsFor("area", sel);
            var labels = new List<string> { Tx("asw.area.auto") }; var tips = new List<string> { Tx("asw.tip.pattern.auto") }; var en = new List<bool> { true };
            int cur = d.AreaPattern == "auto" ? 0 : -1;
            for (int i = 0; i < opts.Count; i++)
            {
                labels.Add(opts[i].Tactic == "grid" ? Tx("asw.area.grid") : opts[i].Tactic == "dip" ? Tx("asw.area.dip") : Tx("asw.area.mad"));
                tips.Add(opts[i].Enabled ? PatternTip(opts[i].Tactic) : opts[i].DisabledReason); en.Add(opts[i].Enabled);
                if (opts[i].Tactic == d.AreaPattern) cur = i + 1;
            }
            k.Row(Tx("asw.lbl.tactic"));
            int np = k.Segments("pattern", labels, cur, tips, en, 0);
            if (np != cur && np >= 0) { d.AreaPattern = np == 0 ? "auto" : opts[np - 1].Tactic; _env.Log("asw window press pattern." + np + " -> " + d.AreaPattern + " (area)"); }
            else if (k.DisabledClick != null) _env.Log("asw window press pattern (grey) -> refused: " + k.DisabledClick);

            bool drawing = WantInput == "asw_rect";
            var sl = new List<string> { Tx(drawing ? "asw.btn.area_drawing" : "asw.btn.area_rect"), Tx("asw.btn.by_bearing") };
            int scur = drawing ? 0 : d.AreaSource == "rect" ? 0 : d.AreaSource == "bearing" ? 1 : -1;
            k.Row(Tx("asw.lbl.area"));
            int hit = k.Chips("asrc", sl, scur, null, new List<string> { Tx("asw.tip.area_rect"), Tx("asw.tip.area_bearing") });
            if (hit == 0)
            {
                if (drawing) CancelInput("asw.say.draw_cancelled");
                else { WantInput = "asw_rect"; Say(Tx("sar.ui.draw_hint"), UiTone.Accent); }
            }
            else if (hit == 1) { d.AreaSource = "bearing"; d.GeoDirty = true; WantInput = null; }
            if (d.AreaSource == "bearing")
            {
                k.Row(Tx("asw.lbl.bearing"));
                double? course = sel != null && sel.AxisDeg >= 0 ? (double?)sel.AxisDeg : null;
                double b = k.Compass("brg", d.BearingDeg, course, Tx("asw.lbl.bearing"));
                if (Math.Abs(b - d.BearingDeg) > 1e-9) { d.BearingDeg = b; d.GeoDirty = true; }
                k.Row(Tx("asw.lbl.dist"));
                double dist = d.DistNm;
                if (PickList(k, "dist", AswPatternMath.AreaDistList, ref dist, Tx("asw.lbl.dist"), false, 20)) { d.DistNm = dist; d.GeoDirty = true; }
                k.Unit(Tx("asw.lbl.miles"));
                k.Row(Tx("asw.lbl.size"));
                double size = d.SizeNm;
                if (PickList(k, "size", AswPatternMath.AreaSizeList, ref size, Tx("asw.lbl.size"), false, 10)) { d.SizeNm = size; d.GeoDirty = true; }
                k.Unit(Tx("asw.lbl.miles"));
            }
            else if (d.AreaSource == "rect") { k.Row(null); k.Text(Tx("asw.hint.area_set"), UiTone.Ok); }
        }

        /// <summary>The summary line of the draft (ADR-0076 s.7, AswPatternMath.Summary): what the order will lay with the numbers of the draft.</summary>
        private string SummaryText(AswGroupView sel, AswOrderDraft d)
        {
            double r = sel != null && sel.RNm > 0 ? sel.RNm : 2.7;
            int members = sel == null ? 1 : Math.Max(1, sel.Members.Count);
            if (d.Mode == "course")
            {
                var eff = d.Effective();
                if (eff.Contains("corridor"))
                {
                    var s = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "corridor", R = r, XNm = d.Course.CorridorXNm, LNm = d.Course.CorridorLNm, VKn = 15, Buoys = 40 * members, Members = members, MaxRows = 6 });
                    return Tx("asw.sum.corridor", N0(d.Course.CorridorXNm), s.RowsSide.ToString(CultureInfo.InvariantCulture), N0(s.Buoys), N0(s.BuoysPerHour));
                }
                if (eff.Contains("barrier"))
                {
                    var s = AswPatternMath.Summary(new AswSummaryIn { Order = "course", Tactic = "barrier", R = r, XNm = d.Course.BarrierXNm, GapNm = d.Course.BarrierGapNm, Rows = d.Course.BarrierRows, VKn = 15, Buoys = 40 * members, Members = members });
                    string text = Tx("asw.sum.barrier", N0(d.Course.BarrierXNm), s.RowsPlan.ToString(CultureInfo.InvariantCulture), N0(d.Course.BarrierGapNm), N0(s.Buoys), N0(s.BuoysPerHour));
                    if (s.BuoysPerHour > 0)
                    {
                        int mins = (int)Math.Round(40.0 * members / s.BuoysPerHour * 60.0);   // the buoys of the members (40 each) at the speed of the estimate
                        text += " " + Tx("asw.sum.barrier_left", (mins / 60).ToString(CultureInfo.InvariantCulture), (mins % 60).ToString("00", CultureInfo.InvariantCulture), members.ToString(CultureInfo.InvariantCulture));
                    }
                    return text;
                }
                return Tx("asw.sum.dip", AswPatternMath.DipStepV3(r).ToString("0", CultureInfo.InvariantCulture), N0(d.Course.DipLNm));
            }
            var area = AswPatternMath.Summary(new AswSummaryIn { Order = "area", Tactic = "grid", R = r, AreaNorthNm = d.SizeNm, AreaEastNm = d.SizeNm });
            return Tx("asw.sum.area", N0(d.SizeNm), N0(d.SizeNm), area.Nx.ToString(CultureInfo.InvariantCulture), area.Ny.ToString(CultureInfo.InvariantCulture), (Math.Round(area.StepNm * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture), area.Nodes.ToString(CultureInfo.InvariantCulture));
        }

        private static string N0(double v) { return Math.Round(v).ToString("0", CultureInfo.InvariantCulture); }

        private void DrawOrder(UiKit k, AswView v, AswGroupView sel)
        {
            var d = _draft;
            bool course = d.Mode == "course";
            if (!k.Section("order", Tx("asw.sec.order"), null, true, AswWindowModel.ModeSummary(_mode))) return;
            k.LabelColumn(Tx("asw.lbl.mode"), Tx("asw.lbl.target"), Tx("asw.lbl.tactic"), Tx("asw.lbl.corridor"), Tx("asw.lbl.barrier"), Tx("asw.lbl.dip"), Tx("asw.lbl.depth"), Tx("asw.lbl.area"), Tx("asw.lbl.bearing"), Tx("asw.lbl.dist"), Tx("asw.lbl.size"));
            if (ModeRow(k)) return;
            if (course) CourseTargetRow(k, sel, d);
            if (course) CourseRows(k, sel, d); else AreaRows(k, sel, d);
            DepthRow(k, d);
            k.Text(SummaryText(sel, d), UiTone.Dim, true, 2);
            string warn = sel == null || sel.Plan == null ? null : sel.Plan.Warn;
            if (warn != null && sel.Order == d.Mode) k.Text(AswWindowModel.WarningText(warn), UiTone.Warn, true, 2);   // buoys_short | corridor_too_wide | life_short of the running order

            // actions
            var st = ActionState(sel, d);
            var stop = AswOrderPanel.Stop(sel);
            bool previewOn = v.Preview.Count > 0;
            bool dirty = sel != null && sel.Order == d.Mode && st.Enabled && st.Id == "apply";
            bool routeOn = course ? CourseRouteOn() : AreaRouteOn();   // T-176 W6: the course order has its own persistent flag too
            bool routeGrey = !course && AswOrderPanel.NeedAreaFor(sel, d) && !(sel != null && sel.Order == "area");
            // T-178 A3 (ADR-0077 s.5): one footer row: [Route] ... [Stop] gap [Start / Apply]; "Changed" on the left
            var route = new UiAction("route", Tx(routeOn ? "asw.btn.route_hide_short" : "asw.btn.route_show_short"), routeGrey ? Tx("asw.hint.need_area") : Tx(course ? "asw.tip.route" : "asw.tip.route_area"), !routeGrey);
            string pressed = k.Footer(new UiFooter
            {
                Aux = new[] { route },
                Stop = new UiAction("stop", Tx("asw.btn.stop_icon", stop.Label), stop.Tip ?? Tx("asw.tip.stop_group"), stop.Enabled),
                Primary = new UiAction(st.Id, Tx("asw.btn.start_icon", st.Label), st.Tip ?? Tx(st.Id == "apply" ? "asw.tip.apply_order" : "asw.tip.start_order"), st.Enabled),
                Changed = dirty,
            });
            if (pressed == "route") ToggleRoute(sel, d, routeOn, previewOn);
            else if (pressed == "stop" && stop.Enabled) { if (Send("asw_stop", new Dictionary<string, object> { { "group", sel.Name } })) Say(Tx("asw.say.stopped", sel.Name), UiTone.Ok); }
            else if (pressed != null && pressed == st.Id && st.Enabled) { _env.Log("asw window press " + pressed + " -> " + d.Mode + " tactic=" + d.TacticKey); RunOrder(sel, d); }
            else if (pressed != null && pressed == st.Id) _env.Log("asw window press " + pressed + " -> grey: " + (st.Tip ?? ""));
            if (course ? CourseRouteOn() : AreaRouteOn()) HoldPreview(sel, d);

            // reserved room (0.28.1): a tall window clamped at the screen bottom moved up, buttons included, when this line appeared
            string hint = AswOrderPanel.Hint(sel, d);
            if (hint == "dirty") k.HintSlot(Tx("asw.hint.dirty", AswOrderPanel.DirtyFields(sel, d)), UiTone.Warn);
            else if (hint == "replace") k.HintSlot(Tx("asw.hint.replace"), UiTone.Warn);
            else if (hint == "need_area") k.HintSlot(Tx("asw.hint.need_area"), UiTone.Dim);
            else if (dirty) k.HintSlot(Tx("asw.hint.dirty", AswOrderPanel.DirtyFields(sel, d)), UiTone.Warn);
            else k.HintSlot(null);
        }

        /// <summary>The state of "Start" / "Apply" with the extra edits of the sockets (loadout, target) counted as changes.</summary>
        private UiButtonState ActionState(AswGroupView sel, AswOrderDraft d)
        {
            var st = AswOrderPanel.Action(sel, d);
            if (!st.Enabled && st.Id == "apply" && AswOrderPanel.SameAsOrder(sel, d))
            {
                bool extra = false;
                DraftDirtyExtra(sel, d, ref extra);
                if (extra) { st.Enabled = true; st.Tip = Tx("asw.hint.dirty", Tx("asw.fld.extra")); }
            }
            return st;
        }

        private static void Sig(StringBuilder sb, object o)
        {
            var dict = o as Dictionary<string, object>;
            var list = o as List<object>;
            if (dict != null) { var keys = new List<string>(dict.Keys); keys.Sort(StringComparer.Ordinal); sb.Append('{'); foreach (string key in keys) { sb.Append(key).Append('='); Sig(sb, dict[key]); sb.Append(';'); } sb.Append('}'); }
            else if (list != null) { sb.Append('['); foreach (object x in list) { Sig(sb, x); sb.Append(','); } sb.Append(']'); }
            else if (o is double) sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture));
            else sb.Append(o == null ? "null" : o.ToString());
        }

        private static string PreviewSig(Dictionary<string, object> cmd) { if (cmd == null) return null; var sb = new StringBuilder(); Sig(sb, cmd); return sb.ToString(); }

        /// <summary>
        /// "Route" of the course and the area order (ADR-0076 s.8): a persistent flag. On: the overlay draws the plan of an active order and the window holds the dry run of the draft
        /// (resent when the draft changes, HoldPreview). Off: the flag and the dry run go.
        /// </summary>
        private void ToggleRoute(AswGroupView sel, AswOrderDraft d, bool on, bool previewOn)
        {
            bool course = d.Mode == "course";
            if (on)
            {
                if (course) SetCourseRoute(false); else SetAreaRoute(false);
                if (previewOn && Send("asw_preview_clear", new Dictionary<string, object>())) _previewShown = false;
                _previewSig = null;
                _env.Log("asw window press route (" + d.Mode + ") -> off");
                return;
            }
            if (course) SetCourseRoute(true); else SetAreaRoute(true);
            _env.Log("asw window press route (" + d.Mode + ") -> on");
            _previewSig = null;
            HoldPreview(sel, d);   // the dry run of the draft goes at once (when the draft says something the order does not)
        }

        private void HoldPreview(AswGroupView sel, AswOrderDraft d)
        {
            bool course = d.Mode == "course";
            // an active order of this mode that the draft does not change needs no dry run: the overlay draws its plan
            bool needRun = sel == null || sel.Order != d.Mode || d.GeoDirty || (!course && d.AreaSource != null) || d.TacticKey != (course ? AswPatternMath.ComboOf(sel.Course.Tactics) : sel.AreaPattern);
            if (!needRun)
            {
                if (_previewShown && Send("asw_preview_clear", new Dictionary<string, object>())) _previewShown = false;
                _previewSig = null;
                return;
            }
            var cmd = AswOrderPanel.PreviewCommand(sel, d);
            if (cmd == null) return;
            AddCourseArgs(sel, d, cmd);
            string sig = PreviewSig(cmd);
            if (sig == _previewSig) return;
            if (Send("asw_preview", cmd)) { _previewShown = true; _previewSig = sig; }
        }

        private void RunOrder(AswGroupView sel, AswOrderDraft d)
        {
            string err;
            var cmds = AswOrderPanel.Commands(sel, d, out err);
            if (err != null) { _env.Log("asw window press order -> err " + err); Say(OrderErrText(sel, d, err), UiTone.Error); return; }
            var all = new List<UiCmd>();
            AddApplyCommands(sel, d, all);
            all.AddRange(cmds);
            if (all.Count == 0) return;
            foreach (var c in all)
            {
                if (c.Name == "asw_search_course") AddCourseArgs(sel, d, c.Args);
                if (!Send(c.Name, c.Args)) return;
            }
            bool apply = sel != null && sel.Order == d.Mode;
            d.Reload = true;   // the group now carries what was ordered: the draft starts again from it
            if (_values != null) _values.SetValue(d.Mode == "area" ? AswOrderPanel.LastAreaId : AswOrderPanel.LastCourseId, AswOrderPanel.FormatLast(d));   // the last order of the mode is the start of the next one
            Say(Tx(apply ? "asw.say.changed" : "asw.say.ordered"), UiTone.Ok);
            if (!apply) _previewShown = false;   // a real order removes the dry run
            _previewSig = null;
        }

        private static string OrderErrText(AswGroupView sel, AswOrderDraft d, string err)
        {
            switch (err)
            {
                case "need_area": return Tx("asw.hint.need_area");
                case "tactic_unavailable":
                    foreach (var o in AswWindowModel.TacticsFor(d.Mode, sel)) if (!o.Enabled && !string.IsNullOrEmpty(o.DisabledReason) && (d.Mode == "area" ? o.Tactic == d.AreaPattern : d.Chosen().Contains(o.Tactic))) return o.DisabledReason;
                    return Tx("asw.err.tactic");
                default: return err;
            }
        }
    }
}
