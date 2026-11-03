// T-148 A4: the body of the ASW / SAR window on the shared UiKit (ADR-0049, ui-mod-window-layout.md section 6). Pure C#: no Unity, no game.
// The frame (UiWindowFrame), the mouse input on the map and the attack dialog stay in AswWindowGui.cs; this class lays the sections out,
// keeps the drafts (nothing is sent until "Start" / "Apply", customer decision Q3) and turns clicks into commands.
// Partial methods without a body are the sockets of other tasks (T-142 K5, T-136 E3, T-119 I1): AswWindowBody.Course.cs / .Contacts.cs / .Deck.cs.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal interface IAswWindowEnv
    {
        double Now();
        void Log(string m);
        /// <summary>Centre the tactical map on the unit (a click on a list row). null = done, else the text of the reason.</summary>
        string Focus(int uid);
    }

    internal sealed partial class AswWindowBody
    {
        private readonly Func<string, Dictionary<string, object>, string> _send;
        private readonly IAswWindowEnv _env;
        private string _mode = "course", _group, _seenGroup, _seenOrder, _renameText = "", _renameFor;
        internal Func<bool> AreaRouteOn = AswDrawPlan.AreaRouteOn;   // T-174 W4: the remembered "Route" flag of the area order (the stand replaces them)
        internal Action<bool> SetAreaRoute = AswDrawPlan.SetAreaRoute;
        internal Func<bool> CourseRouteOn = AswDrawPlan.CourseRouteOn;   // T-176 W6 (ADR-0076 s.8): the remembered "Route" flag of the course order, like the area one
        internal Action<bool> SetCourseRoute = AswDrawPlan.SetCourseRoute;
        private IUiWindowValues _values;                                // the value memory of the window (the last order of a mode); set at the start of every Draw
        private string _previewSig;                                       // the preview the window holds (the route flag is on): resent when the draft changes
        private AswOrderDraft _draft;
        private SarDraft _sar;
        private string _sarKey;
        private readonly HashSet<int> _picked = new HashSet<int>(), _pickedAvail = new HashSet<int>();
        private bool _previewShown, _renaming;
        private int _noteSerial;

        private static string Tx(string key, params object[] a) { return a == null || a.Length == 0 ? UiText.Get(key) : UiText.Fmt(key, a); }

        public AswWindowBody(Func<string, Dictionary<string, object>, string> send, IAswWindowEnv env)
        {
            _send = send; _env = env;
        }

        /// <summary>course | area | sar (the tab).</summary>
        public string Mode { get { return _mode; } }
        public string SelectedGroup { get { return _group; } set { _group = value; } }
        /// <summary>The order draft of the course / area tabs (hooks of other tasks read and write it).</summary>
        internal AswOrderDraft Draft { get { return _draft; } }

        public string Note { get; private set; }
        public UiTone NoteTone { get; private set; }
        public double NoteUntil { get; private set; }
        /// <summary>Grows with every new note (the frame copies a note into its hint line once).</summary>
        public int NoteSerial { get { return _noteSerial; } }

        /// <summary>null | "asw_rect" | "sar_rect" | "sar_point": what the next mouse input on the map is for.</summary>
        public string WantInput { get; private set; }

        private void Say(string text, UiTone tone)
        {
            Note = text; NoteTone = tone; NoteUntil = _env.Now() + 6.0; _noteSerial++;
        }

        // ---------------------------------------------------------------- selection

        private AswGroupView Selected(AswView v)
        {
            AswGroupView first = null;
            foreach (var g in v.Groups) { if (first == null) first = g; if (g.Name == _group) return g; }
            _group = first == null ? null : first.Name;
            return first;
        }

        /// <summary>Ring / menu "ASW for this ship's group": preselect the first group whose target carries this name (no match = the choice stays).</summary>
        public bool SelectByTarget(AswView v, string targetName)
        {
            if (string.IsNullOrEmpty(targetName) || v == null) return false;
            foreach (var g in v.Groups)
                if (string.Equals(g.TargetName, targetName, StringComparison.OrdinalIgnoreCase)) { _group = g.Name; return true; }
            return false;
        }

        // ---------------------------------------------------------------- input from the map

        /// <summary>The rectangle drawn on the map (8 numbers lat/lon of the corners).</summary>
        public void OnRect(double[] geo8)
        {
            string want = WantInput;
            WantInput = null; _sarPrev = null;
            if (geo8 == null || geo8.Length < 8) return;
            if (want == "sar_rect" && _sar != null)
            {
                _sar.Area = "rect"; _sar.Rect = new[] { geo8[0], geo8[1], geo8[4], geo8[5] };
                Say(Tx("asw.hint.area_set"), UiTone.Ok);
            }
            else if (want == "asw_rect" && _draft != null)
            {
                _draft.AreaSource = "rect"; _draft.Rect = geo8; _draft.GeoDirty = true;
                Say(Tx("asw.hint.area_set"), UiTone.Ok);
            }
        }

        /// <summary>The point clicked on the map (centre of a SAR circle).</summary>
        public void OnPoint(double lat, double lon)
        {
            bool sar = WantInput == "sar_point";
            WantInput = null; _sarPrev = null;
            if (!sar || _sar == null) return;
            _sar.Area = "circle"; _sar.HasPoint = true; _sar.Lat = lat; _sar.Lon = lon;
            Say(Tx("asw.hint.point_set"), UiTone.Ok);
        }

        /// <summary>Drawing / pointing was cancelled (Esc, no map): sayKey = the text key of the reason, null = silently.</summary>
        public void CancelInput(string sayKey)
        {
            if ((WantInput == "sar_rect" || WantInput == "sar_point") && _sarPrev != null && _sar != null)   // T-178 A5: the previous area comes back
            {
                _sar.Area = _sarPrev.Area; _sar.Rect = _sarPrev.Rect; _sar.HasPoint = _sarPrev.HasPoint; _sar.Lat = _sarPrev.Lat; _sar.Lon = _sarPrev.Lon;
            }
            _sarPrev = null;
            WantInput = null;
            _pick.Cancel("cancel"); _pickFor = null;   // T-178 A2: a map pick ends too (RMB / Esc / "Cancel" / window closed / group or mode changed)
            if (sayKey != null) Say(Tx(sayKey), UiTone.Dim);
        }

        /// <summary>The window closes: the preview of the route this window asked for must go (the Gui sends nothing itself).</summary>
        public bool TakePreviewShown()
        {
            bool was = _previewShown;
            _previewShown = false;
            return was;
        }

        // ---------------------------------------------------------------- summary and warnings

        public string Summary(AswView v)
        {
            if (v == null) return "";
            return AswWindowModel.TaskLine(Selected(v));
        }

        public IList<string> Warnings(AswView v)
        {
            var l = new List<string>();
            if (v == null) return l;
            for (int i = v.Warnings.Count - 1; i >= 0; i--) l.Add(AswWindowModel.WarningText(v.Warnings[i]));
            return l;
        }

        // ---------------------------------------------------------------- sending

        private string LastAck;

        private bool Send(string name, Dictionary<string, object> cmd)
        {
            _env.Log("asw window cmd " + name);
            string ack = null;
            try { ack = _send(name, cmd); }
            catch (Exception e) { _env.Log("asw window cmd " + name + " failed: " + e.GetType().Name); }
            LastAck = ack;
            string err = SarPanel.AckText(ack);
            if (ack != null && ack.Contains("restore_waiting")) { Say(Tx("asw.err.restore_waiting"), UiTone.Warn); return false; }   // T-211
            if (err != null) { Say(err, UiTone.Error); return false; }
            if (ack != null && ack.Contains("\"hangar_taken\"")) Say(Tx("asw.say.hangar_taken"), UiTone.Warn);   // T-181: a hangar machine of that type was not free
            return true;
        }

        private static List<object> Uids(ICollection<int> set)
        {
            var l = new List<object>();
            foreach (int u in set) l.Add((double)u);
            return l;
        }

        private static Dictionary<string, object> GroupCmd(string op, string group, ICollection<int> uids, string kind = null)
        {
            var c = new Dictionary<string, object> { { "op", op } };
            if (kind != null) c["kind"] = kind;   // T-346 D4: the controller refuses the aircraft the mode does not take (defence in depth after Refused)
            if (group != null) c["group"] = group;
            if (uids != null) c["uids"] = Uids(uids);
            return c;
        }

        /// <summary>T-155: a click on a row (not on a link) only toggles its tick; the map is centred by the links of the "Aircraft" / "Ship" cells.</summary>
        private static void RowClick(ISet<int> picked, int uid)
        {
            if (picked != null) { if (!picked.Remove(uid)) picked.Add(uid); }
        }

        /// <summary>A click on a link cell: column 0 = the aircraft (a machine in the air, or the ship of its deck), column 1 = the ship.</summary>
        private void LinkClick(IList<AswRow> rows, int r, int c)
        {
            if (r < 0 || r >= rows.Count) return;
            int uid = c == 0 ? AswWindowModel.AircraftFocusUid(rows[r]) : AswWindowModel.ShipFocusUid(rows[r]);
            if (uid != 0) FocusRow(uid);
        }

        private static string LinkTip(IList<AswRow> rows, int r, int c)
        {
            if (r < 0 || r >= rows.Count) return null;
            string name = c == 0 ? rows[r].Name : rows[r].Base;
            string hn = c == 0 ? HangarTip(rows[r]) : null;   // T-181
            if (hn != null) return hn;
            return string.IsNullOrEmpty(name) ? null : Tx("asw.tip.link", name);
        }

        /// <summary>Picked aircraft that the controller does not use any more (the "Excluded" rows): nothing is sent, the line says why. They are dropped from the ticks.</summary>
        private bool Refused(IList<AswExcludedRow> excluded, ISet<int> picked)
        {
            AswExcludedRow bad = null;
            foreach (var e in excluded) if (e.Row != null && picked.Contains(e.Row.Uid)) { bad = e; break; }
            if (bad == null) return false;
            string ship = string.IsNullOrEmpty(bad.Row.Base) ? "" : " (" + bad.Row.Base + ")";
            Say(Tx("asw.say.refused", bad.Row.Name + ship, AswWindowModel.ExcludedText(bad.Reason)), UiTone.Warn);
            _env.Log("asw window refused " + bad.Row.Uid + " reason=" + bad.Reason);
            foreach (var e in excluded) if (e.Row != null) picked.Remove(e.Row.Uid);
            return true;
        }

        private void FocusRow(int uid)
        {
            string e = _env.Focus(uid);
            if (e != null) Say(e, UiTone.Warn);
        }

        // ---------------------------------------------------------------- drawing

        public void Draw(UiKit k, AswView v)
        {
            if (v == null) v = new AswView();
            _values = k.Values;
            var sel = Selected(v);
            _lastView = v; _lastSel = sel;
            if (_pick.Active && (WantInput != "ship_pick" || (sel == null ? null : sel.Name) != _seenGroup)) CancelInput("asw.pick.cancelled");   // another group or mode: the pick is over
            if (_editBase != null && (sel == null ? null : sel.Name) != _seenGroup) EndEdit(false);
            if (sel != null && (sel.Name != _seenGroup || sel.Order != _seenOrder)) { _seenGroup = sel.Name; _seenOrder = sel.Order; _mode = AswWindowModel.ModeOf(sel, _mode); }
            if (sel == null) _seenGroup = null;
            if (v.RestoreWaiting) k.Text(Tx("asw.restore_waiting"), UiTone.Warn, true);   // T-211
            if (v.RestorePrompt != null && k.Button("restore", v.RestorePrompt)) Send("asw_restore", new Dictionary<string, object>());
            k.Break();
            bool sar = _mode == "sar";   // T-174: the mode row sits inside "Order" (ModeRow); a change shows from the next frame
            bool strike = _mode == "strike";   // T-173: the strike group (ADR-0074)
            if (sar) SyncSarDraft(sel);
            else if (strike) _strike = AswStrikePanel.DraftFrom(sel, _strike);
            else _draft = AswOrderPanel.DraftFrom(sel, _mode, _draft, v.Defaults, k.Values, v.AreaSizeNm, v.SelectedShipUid);
            if (_datumRect != null && !sar && !strike) ApplyDatumRect();   // T-288
            if (sel != null && _renameFor != sel.Name) { _renameFor = sel.Name; _renameText = sel.Name; _renaming = false; }

            DrawGroups(k, v, sel);
            DrawMembers(k, sel, sel != null && sel.Order == "sar");   // the columns follow the order of the group, not the mode being composed
            if (sar) DrawSarOrder(k, v, sel);
            else if (strike) { DrawStrikeOrder(k, v, sel); StrikeContacts(k, sel); }
            else
            {
                DrawOrder(k, v, sel);
                LoadoutSection(k, sel, _draft);
                ContactsSection(k, sel);
            }
            DrawAvailable(k, v, sel, sar, strike);
            if (sar) DrawSarShips(k);
            if (k.DisabledClick != null) Say(k.DisabledClick, UiTone.Warn);   // a grey control explains itself on a click
        }

        /// <summary>T-174 W1 (ADR-0073 section 1): the first row of the "Order" section: course | area | sar. A change sets the mode, drops the map input and returns true:
        /// the caller leaves the section for this frame (the new draft is built at the top of the next one). Sends nothing.</summary>
        private bool ModeRow(UiKit k)
        {
            var labels = AswWindowModel.OrderButtonTexts();
            var tips = new List<string> { Tx("asw.tip.mode_course"), Tx("asw.tip.mode_area"), Tx("asw.tab.sar_tip", SarPanel.TabTip()), Tx("asw.strike.hint") };
            int cur = Array.IndexOf(AswWindowModel.OrderModes, _mode);
            k.Row(Tx("asw.lbl.mode"));
            int now = k.Radio("mode", labels, cur, tips);
            if (now == cur || now < 0) return false;
            _mode = AswWindowModel.OrderModes[now]; WantInput = null;
            _env.Log("asw window press mode." + now + " -> " + _mode);
            return true;
        }

        private void DrawGroups(UiKit k, AswView v, AswGroupView sel)
        {
            bool anyOrder = false;
            foreach (var g in v.Groups) if (g.Order != "none") anyOrder = true;
            bool open = k.Section("groups", Tx("asw.sec.groups"), v.Groups.Count > 0 ? v.Groups.Count.ToString(CultureInfo.InvariantCulture) : null, true);
            if (k.SectionButton("stop_all", "\u25a0 " + Tx("asw.btn.stop_all"), SarPanel.StopAllTip(), anyOrder))
            {
                var snaps = SnapGroups(v, null);   // T-178 A4: the action is at once, the card "Undo" lives 10 s
                if (Send("asw_stop", new Dictionary<string, object> { { "all", true } })) { Say(Tx("asw.say.stopped_all"), UiTone.Ok); TakeUndo(snaps, false, Tx("asw.undo.stop_all", snaps.Count)); }
            }
            _banner.Draw(k, _env.Now());   // T-199: visible also when the section is folded
            if (open)
            {
                if (v.Groups.Count > 0)
                {
                    var labels = new List<string>(); var tones = new List<UiTone>(); var gtips = new List<string>();
                    int cur = -1;
                    for (int i = 0; i < v.Groups.Count; i++)
                    {
                        var g = v.Groups[i];
                        labels.Add(g.Name + " \u00b7 " + g.Members.Count.ToString(CultureInfo.InvariantCulture) + " \u00b7 " + SarPanel.Badge(g.Order));
                        tones.Add(g.Blink ? UiTone.Warn : UiTone.Normal);
                        gtips.Add(Tx("asw.tip.group", g.Name));
                        if (g.Name == _group) cur = i;
                    }
                    int hit = k.Chips("grp", labels, cur, tones, gtips);
                    if (hit >= 0) _group = v.Groups[hit].Name;
                }
                // icon buttons: + new group from the ticked, - disband the selected, ... rename
                if (k.IconButton("grp_new", "+", Tx(_pickedAvail.Count > 0 ? "asw.tip.group_new_icon" : "asw.hint.pick_first"), _pickedAvail.Count > 0) && _pickedAvail.Count > 0)
                {
                    if (!Refused(_mode == "sar" ? v.ExcludedSar : _mode == "strike" ? v.ExcludedStrike : v.Excluded, _pickedAvail))
                    {
                        var cmd = GroupCmd("create", _mode == "sar" ? SarPanel.NextGroupName(GroupNames(v)) : null, _pickedAvail, _mode == "sar" ? "sar" : _mode == "strike" ? null : "asw");
                        if (Send("asw_group", cmd)) _pickedAvail.Clear();
                    }
                }
                k.Space(5);   // ADR-0077: [+] and [-] are at least 8 px x s apart
                if (k.IconButton("grp_disband", "\u2212", Tx("asw.tip.group_disband_icon"), sel != null) && sel != null)
                {
                    var snaps = SnapGroups(v, sel.Name);
                    if (Send("asw_group", AswWindowModel.DisbandCommand(sel.Name))) { _group = null; _renaming = false; TakeUndo(snaps, true, Tx("asw.undo.disband", snaps.Count > 0 ? snaps[0].Name : sel.Name)); }
                }
                k.Space(5);
                if (k.IconButton("grp_rename", "\u2026", Tx("asw.tip.rename_icon"), sel != null) && sel != null)
                {
                    _renaming = !_renaming; _renameText = sel.Name;
                }
                if (_renaming && sel != null)
                {
                    k.LabelColumn(Tx("asw.lbl.rename"));
                    k.Row(Tx("asw.lbl.rename"));
                    _renameText = k.Number("rename", _renameText, 14);
                    string typed = (_renameText ?? "").Trim();
                    bool canRename = typed.Length > 0 && typed != sel.Name;
                    if (k.Button("rename_ok", Tx("asw.btn.rename"), Tx("asw.tip.rename"), canRename) && canRename)
                    {
                        if (Send("asw_group", new Dictionary<string, object> { { "op", "rename" }, { "group", sel.Name }, { "name", typed } }))
                        {
                            string named = RenamedTo(LastAck) ?? typed;   // the controller may adjust the name (prefix of a rescue group)
                            _group = named; _renameFor = named; _renameText = named; _renaming = false;
                            Say(Tx("asw.say.renamed", named), UiTone.Ok);
                        }
                        else RenameError(LastAck);
                    }
                }
            }
            k.Break();
            if (sel == null) { k.Text(Tx("asw.hint.no_groups"), UiTone.Dim, true, 2); return; }
            // T-178 A3 (ADR-0077 s.5): the status of the group is chips: state, warnings (never folded into "+N"), facts; the full text is in the tips
            k.StatusChips("asw.status", GroupChips(sel), 2);
        }

        private static string RenamedTo(string ack)
        {
            try
            {
                var d = Json.Parse(ack) as Dictionary<string, object>;
                object data;
                if (d == null) return null;
                var src = d.TryGetValue("data", out data) && data is Dictionary<string, object> ? (Dictionary<string, object>)data : d;
                object g;
                return src.TryGetValue("group", out g) && g is string && ((string)g).Length > 0 ? (string)g : null;
            }
            catch (FormatException) { return null; }
        }

        /// <summary>The refusals of a rename that have their own text (T-150): the generic "Not done: code" is replaced.</summary>
        private void RenameError(string ack)
        {
            if (ack == null) return;
            if (ack.Contains("name_taken")) Say(Tx("asw.err.name_taken"), UiTone.Error);
            else if (ack.Contains("bad_name")) Say(Tx("asw.err.bad_name"), UiTone.Error);
        }

        private static List<string> GroupNames(AswView v)
        {
            var l = new List<string>();
            foreach (var g in v.Groups) l.Add(g.Name);
            return l;
        }

        // ---------------------------------------------------------------- members

        private void DrawMembers(UiKit k, AswGroupView sel, bool sar)
        {
            var rows = sel == null ? new List<AswRow>() : sel.MemberRows;
            PruneVanished(_picked, rows);   // T-181
            string title = sel == null ? Tx("asw.sec.members") : Tx("asw.sec.members_of", sel.Name);
            bool open = k.Section("members", title, rows.Count.ToString(CultureInfo.InvariantCulture), true);
            bool can = sel != null && _picked.Count > 0;
            if (sel != null && k.SectionButton("remove", Tx("asw.btn.remove_icon"), Tx(can ? "asw.tip.remove" : "asw.hint.pick_first"), can) && can)
            {
                if (Send("asw_group", GroupCmd("remove", sel.Name, _picked))) _picked.Clear();
            }
            if (!open) return;
            UiTable spec;
            Func<int, int, string> cell;
            if (sel != null && sel.Order == "strike")
            {
                spec = new UiTable
                {
                    Id = "members",
                    Columns = new[] { new UiColumn(Tx("asw.col.vehicle"), 6.5f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.ship"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.weapon"), 3.5f, false, true), new UiColumn(Tx("asw.col.fuel"), 8f), new UiColumn(Tx("asw.col.task"), 0f, true) { MaxEm = UiColumn.StateMaxEm } }
                };
                cell = (r, c) => c == 0 ? rows[r].Name : c == 1 ? AswWindowModel.ShipText(rows[r]) : c == 2 ? "\u00d7" + rows[r].Torpedoes.ToString(CultureInfo.InvariantCulture) : c == 3 ? AswWindowModel.FuelText(rows[r]) : AswStrikePanel.TaskText(rows[r]);
            }
            else if (sar)
            {
                spec = new UiTable
                {
                    Id = "members",
                    Columns = new[] { new UiColumn(Tx("asw.col.vehicle"), 6.5f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.ship"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("sar.col.onboard"), 3.5f), new UiColumn(Tx("sar.col.target"), 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("asw.col.state"), 9f) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("asw.col.fuel"), 8f) }
                };
                cell = (r, c) => c == 0 ? rows[r].Name : c == 1 ? AswWindowModel.ShipText(rows[r]) : c == 2 ? AswWindowModel.SarOnboardText(rows[r]) : c == 3 ? AswWindowModel.SarTargetText(rows[r]) : c == 4 ? AswWindowModel.StateText(rows[r]) : AswWindowModel.FuelText(rows[r]);
            }
            else
            {
                spec = new UiTable
                {
                    Id = "members",
                    Columns = new[] { new UiColumn(Tx("asw.col.vehicle"), 6.5f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.ship"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.tactic"), 4.5f), new UiColumn(Tx("asw.col.state"), 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("asw.col.buoys"), 4f, false, true), new UiColumn(Tx("asw.col.fuel"), 8f, false, true) }
                };
                cell = (r, c) => c == 0 ? rows[r].Name : c == 1 ? AswWindowModel.ShipText(rows[r]) : c == 2 ? AswWindowModel.MemberTacticCell(rows[r]) : c == 3 ? AswWindowModel.StateText(rows[r]) : c == 4 ? AswWindowModel.BuoysText(rows[r]) : AswWindowModel.FuelText(rows[r]);
            }
            k.Table(spec, rows.Count, cell, null, _picked, r => rows[r].Uid, 6, Tx("asw.hint.no_members"), r => RowClick(_picked, rows[r].Uid), Tx("asw.tip.row_focus"),
                (r, c) => LinkClick(rows, r, c), (r, c) => LinkTip(rows, r, c));
        }

        // ---------------------------------------------------------------- available / excluded

        private void DrawAvailable(UiKit k, AswView v, AswGroupView sel, bool sar, bool strike = false)
        {
            var list = sar ? v.AvailableSar : strike ? v.AvailableStrike : v.Available;
            {   // T-181: a ticked hangar row that is gone is unticked; the rows the controller refuses (excluded) stay ticked so that the click can say why
                var alive = new List<AswRow>(list);
                foreach (var ex in (sar ? v.ExcludedSar : strike ? v.ExcludedStrike : v.Excluded)) if (ex.Row != null) alive.Add(ex.Row);
                PruneVanished(_pickedAvail, alive);
            }
            var excluded = AswWindowModel.ExcludedSeenBy(sar ? v.ExcludedSar : strike ? v.ExcludedStrike : v.Excluded, sel == null ? null : sel.Name);   // 2026-10-22: members of the other groups say where they are
            bool open = k.Section("available", Tx("asw.sec.available"), list.Count.ToString(CultureInfo.InvariantCulture), true);
            var rows = new List<AswRow>(list);
            AswWindowModel.SortRows(rows, "name", true);
            bool can = _pickedAvail.Count > 0;
            if (rows.Count > 0)
            {
                string label = sel == null ? Tx("asw.btn.add_new_icon") : Tx("asw.btn.add_to_icon", sel.Name);
                if (k.SectionButton("add", label, Tx(can ? "asw.tip.add" : "asw.hint.pick_first"), can) && can && !Refused(excluded, _pickedAvail))
                {
                    string kind = sar ? "sar" : strike ? null : "asw";
                    var cmd = sel != null ? GroupCmd("add", sel.Name, _pickedAvail, kind) : GroupCmd("create", sar ? SarPanel.NextGroupName(GroupNames(v)) : null, _pickedAvail, kind);
                    if (Send("asw_group", cmd)) _pickedAvail.Clear();
                }
            }
            if (!open) return;
            if (rows.Count == 0)
            {
                // nothing free: why (the Excluded section is gone, ADR-0052); the whole list is in the tip
                k.Break();
                string why = AswWindowModel.RefusalSummary(excluded);
                if (why != null) k.Text(why, UiTone.Dim, true, 1, AswWindowModel.RefusalList(excluded, 8));
                else k.Hint(Tx("asw.hint.no_available"));
                return;
            }
            UiTable spec;
            Func<int, int, string> cell;
            if (sar)
            {
                spec = new UiTable
                {
                    Id = "avail",
                    Columns = new[] { new UiColumn(Tx("asw.col.vehicle"), 6.5f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.ship"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.type"), 4f), new UiColumn(Tx("asw.col.state"), 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("asw.col.loadout"), 5f), new UiColumn(SarPanel.SeatsColumn(), 2.5f, false, true) }
                };
                cell = (r, c) => c == 0 ? rows[r].Name : c == 1 ? AswWindowModel.ShipText(rows[r]) : c == 2 ? rows[r].Type : c == 3 ? AswWindowModel.StateText(rows[r]) : c == 4 ? rows[r].Loadout : rows[r].Capacity.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                spec = new UiTable
                {
                    Id = "avail",
                    Columns = new[] { new UiColumn(Tx("asw.col.vehicle"), 6.5f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.ship"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("asw.col.type"), 4f), new UiColumn(Tx("asw.col.state"), 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("asw.col.loadout"), 4f), new UiColumn(Tx("asw.col.buoys"), 4f, false, true), new UiColumn(Tx("asw.col.torp"), 1.5f, false, true), new UiColumn(Tx("asw.col.sensors"), 5f) }
                };
                cell = (r, c) => c == 0 ? rows[r].Name : c == 1 ? AswWindowModel.ShipText(rows[r]) : c == 2 ? rows[r].Type : c == 3 ? AswWindowModel.StateText(rows[r]) : c == 4 ? rows[r].Loadout : c == 5 ? AswWindowModel.BuoysText(rows[r]) : c == 6 ? rows[r].Torpedoes.ToString(CultureInfo.InvariantCulture) : AswWindowModel.SensorsCell(rows[r]);
            }
            k.Table(spec, rows.Count, cell, null, _pickedAvail, r => rows[r].Uid, 6, Tx("asw.hint.no_available"), r => RowClick(_pickedAvail, rows[r].Uid), Tx("asw.tip.row_focus"),
                (r, c) => LinkClick(rows, r, c), (r, c) => LinkTip(rows, r, c));
        }

        // ---------------------------------------------------------------- sockets of other tasks (no body = the call disappears at compile time)

        // T-142 K5 (AswWindowBody.Course.cs)
        partial void CourseTargetRow(UiKit k, AswGroupView sel, AswOrderDraft d);
        partial void AddCourseArgs(AswGroupView sel, AswOrderDraft d, Dictionary<string, object> cmd);
        partial void LoadoutSection(UiKit k, AswGroupView sel, AswOrderDraft d);
        partial void AddApplyCommands(AswGroupView sel, AswOrderDraft d, List<UiCmd> cmds);
        partial void DraftDirtyExtra(AswGroupView sel, AswOrderDraft d, ref bool dirty);
        // T-136 E3 (AswWindowBody.Contacts.cs)
        partial void ContactsSection(UiKit k, AswGroupView sel);
    }
}
