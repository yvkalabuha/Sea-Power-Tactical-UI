// T-178 A (ADR-0077): the interface rules in the ASW / SAR / Strike window: the ship pick (list + "On the map"), the status chips, the "Undo" strip of "Stop all" / "Disband",
// the handles of the area being composed. Pure C#: no Unity, no game. The mouse on the map (UiMapPick phases, handle drag) is AswWindowGui.cs; this class holds the state and the maths.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal sealed partial class AswWindowBody
    {
        // ---------------------------------------------------------------- services

        private readonly UiUndoBanner _banner = new UiUndoBanner();   // T-199 (ADR-0084): the strip under "Groups" instead of a card of the shared stack
        private UiUndoSlot _undo = new UiUndoSlot();
        /// <summary>The "Undo" slot of the window (the feature may share one).</summary>
        internal UiUndoSlot Undo { get { return _undo; } set { if (value != null) _undo = value; } }
        private string _undoResult;
        private AswView _lastView;
        private AswGroupView _lastSel;
        private const string UndoWindow = "asw";

        // ---------------------------------------------------------------- ship pick (target "by course", guarded ship of the strike)

        private readonly UiMapPick _pick = new UiMapPick();
        private string _pickFor;
        /// <summary>The phases of picking an own ship on the map (the Gui drives them).</summary>
        internal UiMapPick MapPick { get { return _pick; } }
        /// <summary>The ships of the running map pick and their lat/lon (parallel lists), for the Gui.</summary>
        internal List<ShipItem> PickItems = new List<ShipItem>();
        internal List<double[]> PickGeo = new List<double[]>();

        /// <summary>Own ships (and, for the course target, the formations as -leaderUid) with the distance to the group; geo gets {lat, lon} per item (null = unknown).</summary>
        private List<ShipItem> ShipItems(AswView v, AswGroupView sel, bool formations, List<double[]> geo)
        {
            var items = new List<ShipItem>();
            if (v == null) return items;
            var targets = sel != null && sel.CourseTargets.Count > 0 ? sel.CourseTargets : v.CourseTargets;   // the same list for every group
            double rlat = v.RefLat, rlon = v.RefLon;
            bool have = rlat != 0 || rlon != 0;
            if (sel != null)
            {
                foreach (var r in sel.MemberRows) if (r.HasPos) { rlat = r.Lat; rlon = r.Lon; have = true; break; }
                if (!have || (sel.MemberRows.Count > 0 && !sel.MemberRows[0].HasPos))
                    foreach (var r in sel.MemberRows)
                    {
                        if (r.HasPos || r.ShipUid == 0) continue;
                        foreach (var o in targets) if (o.Uid == r.ShipUid && o.HasPos) { rlat = o.Lat; rlon = o.Lon; have = true; break; }
                        if (have) break;
                    }
            }
            foreach (var o in targets)
            {
                bool formation = (o.Kind ?? "ship") == "formation";
                if (formation && !formations) continue;
                double dist = have && o.HasPos ? AswMath.DistNm(new GeoPt(rlat, rlon), new GeoPt(o.Lat, o.Lon)) : double.MaxValue;
                items.Add(new ShipItem { Uid = formation ? -o.Uid : o.Uid, Name = formation ? Tx("asw.tgt.formation", o.Name) : o.Name, Icon = "ntds:Surface:Own", DistNm = dist, Own = true });
                geo.Add(o.HasPos && !formation ? new[] { o.Lat, o.Lon } : null);
            }
            return items;
        }

        /// <summary>The ship row: "[icon Name · 4,3 мили v] [⌖ На карте]" (+ "Отмена" while the map pick runs). Returns the chosen uid (selectedUid while nothing changes).</summary>
        private int ShipRow(UiKit k, string id, string who, List<ShipItem> items, List<double[]> geo, int selectedUid, string tip)
        {
            bool map;
            int uid = k.ShipPick(id, items, selectedUid, tip, out map);
            if (map)
            {
                PickItems = items; PickGeo = geo; _pickFor = who;
                _pick.Start("asw"); WantInput = "ship_pick";
                Say(Tx("ui.ship.hint"), UiTone.Accent);
                _env.Log("asw window press " + id + ".map -> pick on the map (" + who + ")");
            }
            if (_pick.Active && _pickFor == who)
            {
                PickItems = items; PickGeo = geo;
                if (k.Button(id + ".cancel", Tx("ui.map.cancel"), Tx("ui.map.cancel.tip"))) CancelInput("asw.pick.cancelled");   // i18n-allow: control id
            }
            return uid;
        }

        /// <summary>The left click of the map pick landed on an own ship (the Gui got the uid from UiMapPick.OnClick).</summary>
        public void OnShipPicked(int uid)
        {
            string who = _pickFor;
            _pickFor = null;
            if (WantInput == "ship_pick") WantInput = null;
            if (uid == 0) return;
            string name = null;
            foreach (var it in PickItems) if (it.Uid == uid) name = it.Name;
            if (who == "tgt" && _draft != null) { _draft.TargetUid = uid; _draft.TargetKind = "ship"; }
            else if (who == "ref" && _strike != null) _strike.RefUid = uid;
            else return;
            Say(Tx("asw.pick.set", name ?? uid.ToString(CultureInfo.InvariantCulture)), UiTone.Ok);
            _env.Log("asw window pick " + who + " uid=" + uid);
        }

        // ---------------------------------------------------------------- tips of the order controls (literal keys: the key check finds them)

        private static string TacticTip(string t)
        {
            switch (t) { case "dip": return Tx("asw.tip.tactic.dip"); case "corridor": return Tx("asw.tip.tactic.corridor"); case "barrier": return Tx("asw.tip.tactic.barrier"); default: return null; }
        }

        private static string PatternTip(string t)
        {
            switch (t) { case "grid": return Tx("asw.tip.pattern.grid"); case "dip": return Tx("asw.tip.pattern.dip"); case "mad": return Tx("asw.tip.pattern.mad"); default: return null; }
        }

        private static string DepthTip(string d)
        {
            switch (d) { case "above": return Tx("asw.tip.depth.above"); case "below": return Tx("asw.tip.depth.below"); case "alternate": return Tx("asw.tip.depth.alternate"); default: return Tx("asw.tip.depth.auto"); }
        }

        // ---------------------------------------------------------------- status chips of the selected group

        private static string TacticsText(AswGroupView g)
        {
            var parts = new List<string>();
            foreach (string t in g.Tactics) parts.Add(AswWindowModel.TacticLabelFor(g.Order, t));
            return string.Join("+", parts.ToArray());
        }

        /// <summary>state -> warnings -> facts (ADR-0077 s.5): "✔ by course Kirov" / "● no order", "! CONTACT 1", "! waits for the deck", "R 2.7 ...", "buoys in water 6".</summary>
        internal static List<UiChip> GroupChips(AswGroupView g)
        {
            var l = new List<UiChip>();
            if (g == null) return l;
            string full = AswWindowModel.StatusLine(g);
            bool has = g.Order != "none";
            string what = AswWindowModel.OrderText(g);
            if (g.Order == "course" && !string.IsNullOrEmpty(g.TargetName)) what += " " + g.TargetName;
            else if (g.Order == "area" && !string.IsNullOrEmpty(g.TargetText)) what += " " + g.TargetText;
            else if (g.Order == "strike" && !string.IsNullOrEmpty(g.RefName)) what += " " + g.RefName;
            l.Add(has ? new UiChip { Glyph = "✔", Text = what, Tone = UiTone.Ok, Tip = Tx("asw.chip.active.tip") + ": " + full }
                      : new UiChip { Glyph = "●", Text = Tx("ui.status.none"), Tone = UiTone.Dim, Tip = Tx("asw.chip.none.tip") });
            // warnings
            if (g.Retreat != null) l.Add(new UiChip { Glyph = "!", Text = AswWindowModel.RetreatChipText(g.Retreat), Tone = UiTone.Warn });   // T-180: first among the warnings
            if (g.Contacts.Count > 0) l.Add(new UiChip { Glyph = "!", Text = Tx("asw.status.contact", g.Contacts.Count), Tone = UiTone.Warn });
            else if (g.Blink) l.Add(new UiChip { Glyph = "!", Text = Tx("asw.status.contact", 1), Tone = UiTone.Warn });
            string wait = DeckWaitText(g);
            if (wait != null) l.Add(new UiChip { Glyph = "!", Text = wait, Tone = UiTone.Warn });
            if (g.ReliefInS > 0 && (g.Order == "course" || g.Order == "area")) l.Add(new UiChip { Glyph = "!", Text = Tx("asw.status.gap", Math.Round(g.ReliefInS).ToString(CultureInfo.InvariantCulture)), Tone = UiTone.Warn });
            string warn = g.Plan == null ? null : g.Plan.Warn;
            if (warn != null && (g.Order == "course" || g.Order == "area")) l.Add(new UiChip { Glyph = "!", Text = AswWindowModel.WarningText(warn), Tone = UiTone.Warn });
            // facts
            if (g.Order == "course" || g.Order == "area")
            {
                if (g.Tactics.Count > 0) l.Add(new UiChip { Text = TacticsText(g), Tone = UiTone.Normal });
                if (g.RNm > 0) l.Add(new UiChip { Text = Tx("asw.status.radius", g.RNm.ToString("0.0", CultureInfo.InvariantCulture), AswWindowModel.RSourceText(g.RSource)), Tone = UiTone.Normal });
                l.Add(new UiChip { Text = Tx("asw.status.buoys", g.BuoysInWater), Tone = UiTone.Normal });
                if (g.Order == "area" && g.CoveredFrac < 1) l.Add(new UiChip { Text = Tx("asw.status.covered", Math.Round(g.CoveredFrac * 100).ToString(CultureInfo.InvariantCulture)), Tone = UiTone.Normal });
                if (g.NextBarrierInS >= 0) l.Add(new UiChip { Text = Tx("asw.status.barrier", AswWindowModel.MmSs(g.NextBarrierInS)), Tone = UiTone.Normal });
                string axis = g.Order == "course" ? AxisText(g) : null;
                if (axis != null) l.Add(new UiChip { Text = axis, Tone = UiTone.Normal });
            }
            else if (g.Order == "sar")
            {
                l.Add(new UiChip { Text = AswWindowModel.SarAreaText(g), Tone = UiTone.Normal });
                l.Add(new UiChip { Text = AswWindowModel.SarOnCompleteText(g.SarOnComplete), Tone = UiTone.Normal });
                if (!string.IsNullOrEmpty(g.Status) && g.Status != "none") l.Add(new UiChip { Text = AswWindowModel.SarStatusText(g.Status), Tone = UiTone.Normal });
                l.Add(new UiChip { Text = AswWindowModel.SarTaskLine(g), Tone = UiTone.Normal });
            }
            else if (g.Order == "strike")
            {
                l.Add(new UiChip { Text = Tx(g.Targets == "hostile" ? "asw.targets.hostile" : "asw.targets.hostile_unknown"), Tone = UiTone.Normal });
                int n = 0;
                foreach (var t in g.StrikeTargets) if (!t.Skipped && !t.Status.EndsWith(".killed", StringComparison.Ordinal)) n++;
                if (g.StrikeTargets.Count > 0) l.Add(new UiChip { Text = Tx("asw.sec.contacts") + " " + n.ToString(CultureInfo.InvariantCulture), Tone = UiTone.Normal });
            }
            return l;
        }

        // ---------------------------------------------------------------- "Undo" for "Stop all" and "Disband"

        private sealed class GroupSnap
        {
            public string Name, Order; public List<int> Uids = new List<int>(); public List<string> UidNames = new List<string>(); public List<UiCmd> Cmds = new List<UiCmd>();
        }

        /// <summary>The orders and members of the groups BEFORE the action (only the ones with an order for "Stop all"; one group, with or without an order, for "Disband").</summary>
        private List<GroupSnap> SnapGroups(AswView v, string only)
        {
            var l = new List<GroupSnap>();
            if (v == null) return l;
            foreach (var g in v.Groups)
            {
                if (only != null ? g.Name != only : g.Order == "none") continue;
                var s = new GroupSnap { Name = g.Name, Order = g.Order };
                foreach (var r in g.MemberRows) { s.Uids.Add(r.Uid); s.UidNames.Add(r.Name); }
                foreach (int u in g.Members) if (!s.Uids.Contains(u)) { s.Uids.Add(u); s.UidNames.Add("#" + u.ToString(CultureInfo.InvariantCulture)); }
                OrderCommandsOf(v, g, s.Cmds);
                l.Add(s);
            }
            return l;
        }

        /// <summary>The commands that give the group the order it has now again (empty = no order or not restorable).</summary>
        private void OrderCommandsOf(AswView v, AswGroupView g, List<UiCmd> cmds)
        {
            string err;
            if (g.Order == "course")
            {
                foreach (var r in g.LoadoutRows)
                    if (!string.IsNullOrEmpty(r.Chosen) && !string.Equals(r.Chosen, "auto", StringComparison.OrdinalIgnoreCase))
                        cmds.Add(new UiCmd { Name = "asw_loadout", Args = new Dictionary<string, object> { { "group", g.Name }, { "type", r.Type }, { "loadout", r.Chosen } } });
                var d = AswOrderPanel.DraftFrom(g, "course", null, v.Defaults, null, v.AreaSizeNm, v.SelectedShipUid);
                d.TargetUid = g.TargetUid; d.TargetKind = g.TargetKind ?? "ship";
                var a = AswOrderPanel.CourseOrderArgs(g, d);
                AddCourseArgs(g, d, a);
                cmds.Add(new UiCmd { Name = "asw_search_course", Args = a });
            }
            else if (g.Order == "area" && g.Rect != null && g.Rect.Length >= 8)
            {
                var d = AswOrderPanel.DraftFrom(g, "area", null, v.Defaults, null, v.AreaSizeNm, v.SelectedShipUid);
                d.AreaSource = "rect"; d.Rect = g.Rect;
                cmds.Add(new UiCmd { Name = "asw_search_area", Args = AswOrderPanel.AreaOrderArgs(g, d) });
            }
            else if (g.Order == "sar")
            {
                var c = SarPanel.OrderCommand(g.Name, SarPanel.DraftFrom(PanelGroup(g), null), out err);
                if (c != null) cmds.Add(new UiCmd { Name = "sar_order", Args = c });
            }
            else if (g.Order == "strike")
            {
                var c = AswStrikePanel.Command(g, AswStrikePanel.DraftFrom(g, null), out err);
                if (c != null) cmds.Add(new UiCmd { Name = "asw_order", Args = c });
            }
        }

        private void TakeUndo(List<GroupSnap> snaps, bool disbanded, string title)
        {
            if (snaps == null || snaps.Count == 0) return;
            _undo.Log = _env.Log;
            _undoResult = null;
            _undo.Take(UndoWindow, title, snaps.Count, () => Restore(snaps, disbanded), _env.Now());
            _banner.Show(title, () => _undo.Left(UndoWindow, _env.Now()), DoUndo, () => _undoResult, _env.Now());
        }

        private void DoUndo()
        {
            var r = _undo.Restore(UndoWindow, _env.Now());
            if (r == null) { _undoResult = Tx("asw.undo.gone"); return; }
            string s = Tx("ui.undo.done", r.Restored, r.Groups);
            if (r.Skipped.Count > 0) s += "; " + string.Join("; ", r.Skipped.ToArray());
            _undoResult = s;
        }

        /// <summary>T-178 I3: presses "Undo" without the card (dev command ui_undo); the result line, null = no snapshot left.</summary>
        public string PressUndo()
        {
            if (_undo.Left(UndoWindow, _env.Now()) <= 0) return null;
            DoUndo();
            return _undoResult;
        }

        /// <summary>Drops the snapshot and its card (a new mission, or the window is closed for good).</summary>
        public void ResetUndo()
        {
            _undo.Reset(); _undoResult = null; _banner.Hide();
        }

        private static List<AswRow> FreeFor(AswView v, string order)
        {
            return order == "sar" ? v.AvailableSar : order == "strike" ? v.AvailableStrike : v.Available;
        }

        private static List<AswExcludedRow> ExcludedFor(AswView v, string order)
        {
            return order == "sar" ? v.ExcludedSar : order == "strike" ? v.ExcludedStrike : v.Excluded;
        }

        private UiUndoResult Restore(List<GroupSnap> snaps, bool disbanded)
        {
            var res = new UiUndoResult { Groups = snaps.Count };
            var v = _lastView ?? new AswView();
            foreach (var s in snaps)
            {
                string name = s.Name;
                if (disbanded)
                {
                    var take = new List<int>();
                    var free = FreeFor(v, s.Order); var excl = ExcludedFor(v, s.Order);
                    for (int i = 0; i < s.Uids.Count; i++)
                    {
                        bool ok = false;
                        foreach (var r in free) if (r.Uid == s.Uids[i]) ok = true;
                        if (ok) { take.Add(s.Uids[i]); continue; }
                        string why = Tx("asw.undo.no_member");
                        foreach (var e in excl) if (e.Row != null && e.Row.Uid == s.Uids[i]) why = AswWindowModel.ExcludedText(e.Reason);
                        res.Skipped.Add(Tx("ui.undo.skipped", s.UidNames[i], why));
                    }
                    if (take.Count == 0) continue;
                    if (!Send("asw_group", GroupCmd("create", s.Name, take, s.Order == "sar" ? "sar" : s.Order == "strike" ? null : "asw"))) { res.Skipped.Add(Tx("ui.undo.skipped", s.Name, Tx("asw.undo.no_group"))); continue; }
                    name = RenamedTo(LastAck) ?? s.Name;
                }
                else
                {
                    bool there = false;
                    foreach (var g in v.Groups) if (g.Name == s.Name) there = true;
                    if (!there) { res.Skipped.Add(Tx("ui.undo.skipped", s.Name, Tx("asw.undo.no_group"))); continue; }
                }
                bool fine = true;
                foreach (var c in s.Cmds)
                {
                    c.Args["group"] = name;
                    if (!Send(c.Name, c.Args)) { res.Skipped.Add(Tx("ui.undo.skipped", name, Tx("asw.undo.no_order"))); fine = false; break; }
                }
                if (fine) res.Restored++;
            }
            return res;
        }

        // ---------------------------------------------------------------- handles of the area being composed (ZoneEditMath)

        /// <summary>The longest side of an area the handles allow, miles.</summary>
        public double MaxZoneNm = 100;
        private double[] _editBase; private string _editKind; private bool _editFromDraft;

        private static double[] BoxOf(double[] c8)
        {
            double la0 = double.MaxValue, la1 = double.MinValue, lo0 = double.MaxValue, lo1 = double.MinValue;
            for (int i = 0; i + 1 < c8.Length; i += 2) { la0 = Math.Min(la0, c8[i]); la1 = Math.Max(la1, c8[i]); lo0 = Math.Min(lo0, c8[i + 1]); lo1 = Math.Max(lo1, c8[i + 1]); }
            return new[] { la0, lo0, la1, lo1 };
        }

        private static double[] Corners(double[] z) { return new[] { z[0], z[1], z[0], z[3], z[2], z[3], z[2], z[1] }; }

        /// <summary>The rectangle the handles stand on, {lat0, lon0, lat1, lon1}: the drawn area of the ASW draft (or the running area order's), the SAR rectangle; null = none.
        /// The square "by bearing" has no handles.</summary>
        public double[] EditableRect
        {
            get
            {
                if (WantInput != null) return null;
                if (_mode == "area" && _draft != null && _draft.Mode == "area")
                {
                    if (_draft.AreaSource == "rect" && _draft.Rect != null && _draft.Rect.Length >= 8) return BoxOf(_draft.Rect);
                    var sel = _lastSel;
                    if (_draft.AreaSource == null && sel != null && sel.Order == "area" && sel.Rect != null && sel.Rect.Length >= 8) return BoxOf(sel.Rect);
                }
                if (_mode == "sar" && _sar != null && _sar.Area == "rect" && _sar.Rect != null && _sar.Rect.Length >= 4) return new[] { _sar.Rect[0], _sar.Rect[1], _sar.Rect[2], _sar.Rect[3] };
                return null;
            }
        }

        /// <summary>The SAR circle the handles stand on, {lat, lon, radiusNm}; null = none.</summary>
        public double[] EditableCircle
        {
            get
            {
                if (WantInput != null || _mode != "sar" || _sar == null || _sar.Area != "circle" || !_sar.HasPoint) return null;
                double r = ParseRadius(_sar.RadiusText);
                return new[] { _sar.Lat, _sar.Lon, r > 0 ? r : 20 };
            }
        }

        public bool Editing { get { return _editBase != null; } }

        public void BeginEdit()
        {
            var z = EditableRect; var c = z == null ? EditableCircle : null;
            if (z == null && c == null) return;
            _editKind = z != null ? "rect" : "circle";
            _editBase = z != null ? new[] { z[0], z[1], z[2], z[3] } : new[] { c[0], c[1], c[2] };
            _editFromDraft = _mode == "sar" || (_draft != null && _draft.AreaSource == "rect");
        }

        /// <summary>Moves a handle of the rectangle by (dx east, dy north) miles from where BeginEdit found it: snapped to 1 mile, 5 x 5 at least, MaxZone at most; the result is the draft.</summary>
        public void EditRect(ZoneHandle h, double dxNm, double dyNm)
        {
            if (_editBase == null || _editKind != "rect") return;
            var b = _editBase;
            double la0 = Math.Min(b[0], b[2]), la1 = Math.Max(b[0], b[2]), lo0 = Math.Min(b[1], b[3]), lo1 = Math.Max(b[1], b[3]);
            double k = 60.0 * ZoneEditMath.LonK((la0 + la1) / 2.0, AswMath.WorldGlobe);   // T-352: flat on stable
            if (k < 1e-6) return;
            var r = ZoneEditMath.Drag(new[] { lo0 * k, la0 * 60.0, lo1 * k, la1 * 60.0 }, h, dxNm, dyNm, MaxZoneNm);
            var z = new[] { r[1] / 60.0, r[0] / k, r[3] / 60.0, r[2] / k };
            if (_mode == "sar") { if (_sar != null) _sar.Rect = z; }
            else if (_draft != null) { _draft.AreaSource = "rect"; _draft.Rect = Corners(z); _draft.GeoDirty = true; }
        }

        /// <summary>The circle: Center moves it, Radius: dxNm = the change of the radius (sticks to the list of radii).</summary>
        public void EditCircle(ZoneHandle h, double dxNm, double dyNm)
        {
            if (_editBase == null || _editKind != "circle" || _sar == null) return;
            var b = _editBase;
            double k = 60.0 * ZoneEditMath.LonK(b[0], AswMath.WorldGlobe);   // T-352
            if (k < 1e-6) return;
            var radii = new List<double>(AswPatternMath.SarRadiusList);
            var r = ZoneEditMath.DragCircle(new[] { b[1] * k, b[0] * 60.0, b[2] }, h, dxNm, dyNm, radii);
            _sar.Lat = r[1] / 60.0; _sar.Lon = r[0] / k;
            _sar.RadiusText = r[2].ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>keep = false (RMB / Esc during the drag): the area from before BeginEdit comes back.</summary>
        public void EndEdit(bool keep)
        {
            if (_editBase == null) return;
            if (!keep)
            {
                if (_editKind == "circle" && _sar != null) { _sar.Lat = _editBase[0]; _sar.Lon = _editBase[1]; _sar.RadiusText = _editBase[2].ToString("0", CultureInfo.InvariantCulture); }
                else if (_mode == "sar" && _sar != null) _sar.Rect = _editBase;
                else if (_draft != null)
                {
                    if (_editFromDraft) { _draft.Rect = Corners(_editBase); }
                    else { _draft.AreaSource = null; _draft.Rect = null; _draft.GeoDirty = false; }
                }
            }
            else Say(Tx("asw.say.area_edited"), UiTone.Ok);
            _editBase = null; _editKind = null;
        }

        /// <summary>"40 × 20 miles" of a zone {lat0, lon0, lat1, lon1} (the ghost of the drawing and the handles).</summary>
        public static string SizeLabel(double[] z)
        {
            if (z == null || z.Length < 4) return null;
            double h = Math.Abs(z[2] - z[0]) * 60.0, w = Math.Abs(z[3] - z[1]) * 60.0 * ZoneEditMath.LonK((z[0] + z[2]) / 2.0, AswMath.WorldGlobe);
            return ZoneEditMath.SizeLabel(w, h);
        }
    }
}
