// T-148 C3: the body of the CAP window on the shared control set (ADR-0049, ui-mod-window-layout.md §5). Pure: no Unity, no game.
// Sections: Groups, (status), Members, Order, Loadout (closed, summary in the bar), Thresholds (closed), Available. No "Excluded" section (ADR-0052):
// the reasons are a summary line in "Available", the answer "Cannot: ..." on [+] / "To group" and the journal line "cap window refused".
// Order edits (N, K, ROE, zone, anchor, loadout) change a draft (CapOrderDraft); commands go out only with "Start" / "Apply" (decision Q3).
// Texts are UiText keys (cap.* in cap_window.ini and sea_power_tactical_ui.ini); nothing is hardcoded.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal interface ICapWindowEnv
    {
        int SelectedShip();                         // uid of the ship selected in the game (0 = none)
        string ShipName(int uid);
        GeoPt? RefPoint();                          // map reference point (own ship / map centre)
        IList<string> LoadoutNames(string type);    // CAP loadouts of the game for a type (null = only "auto")
        string LoadoutLabel(string type, string name);   // the game's display name of a loadout (null / empty = show the name)
        double LoadoutReadyMin(string type, string name);   // T-175: minutes to prepare the loadout on the deck ("auto" = the usual pick); < 0 = unknown
        void ResolveRestore(bool yes);
        string Focus(int uid);                      // centres the map on a unit; null = done, else the text of the error (shown in the hint line)
        /// <summary>T-179: the own ships a group can protect (name, distance from the map reference); geo gets {lat, lon} per item (null = unknown).</summary>
        List<ShipItem> OwnShips(List<double[]> geo);
        double Now();
        void Log(string message);
        /// <summary>T-178 C3: the course of an own ship, degrees; null = unknown or slower than 3 kn (the compass then has no "from course" mode).</summary>
        double? ShipCourse(int uid);
    }

    internal sealed partial class CapWindowBody
    {
        private const double NoteSeconds = 6.0;
        private readonly Func<string, Dictionary<string, object>, string> _send;
        private readonly ICapWindowEnv _env;
        private string _group;
        private bool _renaming; private string _renameText, _renameFor, _lastAck;   // T-326: the name field of the selected group
        private CapOrderDraft _draft = new CapOrderDraft();
        private bool _resetDraft, _drawing, _needLr, _dash = true;
        private double _thrMissile = -1, _thrMargin = -1, _thrFloor = -1;   // T-178 C4: thresholds of the sliders (the [Cap] defaults until the player moves them)
        private IUiWindowValues _values;                                        // T-178 C5: the value memory of the window (null = none)
        private CapView _lastView = new CapView();
        private UiUndoSlot _undo = new UiUndoSlot();
        private string _undoResult;
        private readonly UiUndoBanner _banner = new UiUndoBanner();   // T-199 (ADR-0084): the strip under "Groups" instead of a card of the shared stack
        private readonly HashSet<int> _picked = new HashSet<int>(), _pickedAvail = new HashSet<int>();
        private readonly Dictionary<string, int> _anchorOf = new Dictionary<string, int>();   // group -> ship the zone was anchored to by this window
        private UiTable _tMembers, _tAvail;
        private Dictionary<int, string> _names = new Dictionary<int, string>();   // uid -> the shown name of the aircraft (CapWindowModel.DisplayNames)
        private int _tablesRev = -1;
        private CapGroupView _lastSel;   // the group selected in the last Draw (OnRect comes from Update, without a view)
        private string _note;
        private UiTone _noteTone;
        private double _noteUntil;

        public CapWindowBody(Func<string, Dictionary<string, object>, string> send, ICapWindowEnv env)
        {
            _send = send; _env = env;
        }

        public string Note { get { return _note; } }
        public UiTone NoteTone { get { return _noteTone; } }
        public double NoteUntil { get { return _noteUntil; } }
        /// <summary>The window waits for a rectangle drawn on the map.</summary>
        public bool WantRect { get { return _drawing; } }
        public string SelectedGroup { get { return _group; } }
        /// <summary>T-178 C7: the "Undo" slot of the window (the feature may share one with the other windows).</summary>
        public UiUndoSlot Undo { get { return _undo; } set { if (value != null) _undo = value; } }

        /// <summary>T-154 A7: the drawn zone of the selected group that is not an order yet (lat0, lon0, lat1, lon1), else null. CapOverlay draws it dashed.</summary>
        public double[] DraftZone
        {
            get
            {
                if (_resetDraft || _group == null || _draft == null || _draft.ForGroup != _group || _draft.Kind != "patrol") return null;
                if (_draft.ZoneSource != "rect" || _draft.Zone == null || _draft.Zone.Length < 4) return null;
                var sel = _lastSel;
                if (sel != null && sel.Ordered && CapOrderPanel.SameAsOrder(sel, _draft)) return null;
                return _draft.Zone;
            }
        }

        private static string Tx(string key, params object[] args) { return UiText.Fmt(key, args); }

        private void Say(string text, UiTone tone)
        {
            _note = text; _noteTone = tone; _noteUntil = _env.Now() + NoteSeconds;
        }

        // ---------------------------------------------------------------- input from the map

        /// <summary>The rectangle drawn on the map, as a zone {lat0, lon0, lat1, lon1}: goes to the draft, nothing is sent.</summary>
        public void OnRect(double[] zone4)
        {
            _drawing = false;
            if (zone4 == null || zone4.Length < 4) return;
            _draft.ZoneSource = "rect";
            _draft.Zone = new[] { zone4[0], zone4[1], zone4[2], zone4[3] };
            Say(Tx("cap.say.zone_drawn"), UiTone.Ok);
            // Q2 (customer, 2026-10-21): a group WITHOUT an order gets it at once (as before 0.20); a group with an order keeps the draft + "Apply"
            if (_lastSel != null && !_lastSel.Ordered && GroupName(_lastSel) == _group && _draft.ForGroup == _group)
            {
                _env.Log("cap window zone drawn for " + _group + " without an order -> sending it");
                ApplyDraft(_lastSel);
            }
        }

        // ---------------------------------------------------------------- T-178 C8: handles of the draft / current zone (ZoneEditMath)

        /// <summary>The longest side of a zone the handles allow, miles ([Cap] MaxZone).</summary>
        public double MaxZoneNm = 200;
        private double[] _editBase; private bool _editFromDraft; private string _editGroup;

        /// <summary>The zone the handles stand on: the draft rectangle, else the order's rectangle of the selected group (when the draft keeps it); null = none.</summary>
        public double[] EditableZone
        {
            get
            {
                var dz = DraftZone;
                if (dz != null) return dz;
                var sel = _lastSel;
                if (sel != null && sel.Ordered && sel.Kind == "patrol" && sel.Rect != null && sel.Rect.Length >= 4 && _draft != null && _draft.Kind == "patrol" && _draft.ZoneSource == null && !_drawing) return sel.Rect;
                return null;
            }
        }

        /// <summary>T-335: "Hawk 1 · 40 × 20 miles": the size label of the handles with the name of the group whose zone is edited (as renamed in T-326).</summary>
        public string EditableLabel(double[] z)
        {
            string size = SizeLabel(z), name = EditableGroup;
            return string.IsNullOrEmpty(name) || size == null ? size : name + " · " + size;
        }

        /// <summary>T-335 review: the group the handles belong to: the one caught by BeginEdit during a drag, else the draft's group, else the group of the order zone; null = none.</summary>
        public string EditableGroup
        {
            get
            {
                if (_editBase != null) return _editGroup;
                if (DraftZone != null) return _draft.ForGroup;
                return EditableZone != null ? GroupName(_lastSel) : null;
            }
        }

        public bool Editing { get { return _editBase != null; } }

        public void BeginEdit()
        {
            var z = EditableZone;
            if (z == null) return;
            _editFromDraft = DraftZone != null;
            _editGroup = EditableGroup;
            _editBase = new[] { z[0], z[1], z[2], z[3] };
        }

        /// <summary>Moves a handle by (dx east, dy north) miles from where BeginEdit found the zone: snapped to 1 mile, 5 x 5 at least, MaxZone at most. The result is the draft zone.</summary>
        public void EditZone(ZoneHandle h, double dxNm, double dyNm)
        {
            if (_editBase == null) return;
            var b = _editBase;
            double la0 = Math.Min(b[0], b[2]), la1 = Math.Max(b[0], b[2]), lo0 = Math.Min(b[1], b[3]), lo1 = Math.Max(b[1], b[3]);
            double k = 60.0 * ZoneEditMath.LonK((la0 + la1) / 2.0, AswMath.WorldGlobe);   // T-352: flat on stable
            if (k < 1e-6) return;
            var r = ZoneEditMath.Drag(new[] { lo0 * k, la0 * 60.0, lo1 * k, la1 * 60.0 }, h, dxNm, dyNm, MaxZoneNm);
            _draft.ZoneSource = "rect";
            _draft.Zone = new[] { r[1] / 60.0, r[0] / k, r[3] / 60.0, r[2] / k };
        }

        /// <summary>keep = false (RMB / Esc during the drag): the zone from before BeginEdit comes back.</summary>
        public void EndEdit(bool keep)
        {
            if (_editBase == null) return;
            if (!keep)
            {
                if (_editFromDraft) { _draft.ZoneSource = "rect"; _draft.Zone = _editBase; }
                else { _draft.ZoneSource = null; _draft.Zone = null; }
            }
            else Say(Tx("cap.say.zone_drawn"), UiTone.Ok);
            _editBase = null;
        }

        /// <summary>"40 × 20 miles" of a zone {lat0, lon0, lat1, lon1} (the ghost of the drawing and the handles).</summary>
        public static string SizeLabel(double[] z)
        {
            if (z == null || z.Length < 4) return null;
            double h = Math.Abs(z[2] - z[0]) * 60.0, w = Math.Abs(z[3] - z[1]) * 60.0 * ZoneEditMath.LonK((z[0] + z[2]) / 2.0, AswMath.WorldGlobe);
            return ZoneEditMath.SizeLabel(w, h);
        }

        public void CancelInput()
        {
            if (_pick.Active) { CancelShipPick("cancel"); return; }
            if (!_drawing) return;
            _drawing = false;
            Say(Tx("cap.ui.cancelled"), UiTone.Dim);
        }

        // ---------------------------------------------------------------- data helpers

        private CapGroupView Selected(CapView v)
        {
            CapGroupView first = null;
            foreach (var g in v.Groups)
            {
                if (g.Line == null) continue;
                if (first == null) first = g;
                if (g.Line.Group == _group) return g;
            }
            string was = _group;
            _group = first == null ? null : first.Line.Group;
            if (was != _group) { _picked.Clear(); _drawing = false; _pick.Cancel("group"); }
            return first;
        }

        private static string GroupName(CapGroupView g) { return g != null && g.Line != null ? g.Line.Group : null; }

        private static string ErrText(string code)
        {
            string key = "cap.ui.err." + code;
            return UiText.Has(UiText.DefaultSection, key) ? Tx(key) : code;
        }

        /// <summary>Sends a command; writes the journal line; says the error. Returns null on ok, otherwise the error code.</summary>
        private string Do(string name, Dictionary<string, object> args)
        {
            string ack;
            try { ack = _send(name, args); } catch (Exception e) { ack = "!" + e.GetType().Name; }
            _lastAck = ack;
            _env.Log(CapWindowModel.CmdLogLine(name, ack));
            string err = CapWindowModel.AckError(ack);
            if (err != null) Say(Tx("cap.ui.cmd_err", ErrText(err)), UiTone.Error);
            return err;
        }

        private string ApplyErrorText(string err, CapOrderDraft d)
        {
            switch (err)
            {
                case "need_group": return Tx("cap.ui.need_group");
                case "need_zone": return Tx("cap.ui.need_zone");
                case "bad_ready": return Tx("cap.hint.bad_ready");
                case "bad_targets": return Tx("cap.hint.bad_targets");
                case "no_origin": return Tx("cap.ui.err.no_origin");
                case "bad_number": return d.ZoneSource == "polar" && CapWindowModel.Int(d.NText).HasValue ? Tx("cap.ui.bad_number") : Tx("cap.hint.bad_number");
                default: return err;
            }
        }

        private void ApplyDraft(CapGroupView sel)
        {
            var d = _draft;
            string err;
            // the origin (and anchor) of a zone by bearing is the group's carrier, not the ship selected in the game (live 2026-10-21)
            var cmds = CapOrderPanel.Commands(sel, d, CapWindowModel.OriginShip(sel, _env.SelectedShip()), _env.RefPoint(), out err);
            if (err != null) { _env.Log("cap window apply " + GroupName(sel) + " -> err " + err); Say(ApplyErrorText(err, d), UiTone.Error); return; }
            if (cmds.Count == 0) { _env.Log("cap window apply " + GroupName(sel) + " -> nothing to send"); Say(Tx("cap.hint.same"), UiTone.Dim); return; }
            string group = GroupName(sel);
            foreach (var c in cmds)
            {
                if (Do(c.Name, c.Args) != null) return;
                if (c.Name == "cap_order")
                {
                    object au;
                    if (c.Args.TryGetValue("anchor_uid", out au) && au is double) _anchorOf[group] = (int)(double)au; else _anchorOf.Remove(group);
                }
            }
            RememberLast(d);
            if (d.Kind == "alert" && d.Alert != null) CapOrderPanel.LastAlertSpec = d.Alert.Clone();
            _resetDraft = true;
            _env.Log("cap window apply " + group + " -> sent " + cmds.Count + " command(s)");
            Say(Tx("cap.say.order", group), UiTone.Ok);
        }

        private void EnsureTables()
        {
            if (_tablesRev == UiText.Revision && _tMembers != null) return;
            _tablesRev = UiText.Revision;
            _tMembers = new UiTable
            {
                Id = "cap.members",
                Columns = new[]
                {
                    new UiColumn(Tx("cap.col.aircraft"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("cap.col.ship"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("cap.col.type"), 4f),
                    new UiColumn(Tx("cap.col.where"), 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("cap.col.slot"), 1.8f, false, true), new UiColumn(Tx("cap.col.aam"), 3f), new UiColumn(Tx("cap.col.fuel"), 6f),
                },
            };
            _tAvail = new UiTable
            {
                Id = "cap.avail",
                Columns = new[] { new UiColumn(Tx("cap.col.aircraft"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("cap.col.ship"), 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("cap.col.type"), 4f), new UiColumn(Tx("cap.col.where"), 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("cap.col.loadout"), 0f, true) { MaxEm = UiColumn.StateMaxEm } },   // T-325: + loadout
            };
        }

        // ---------------------------------------------------------------- links and refusals (T-155)

        private void FocusUnit(int uid)
        {
            if (uid == 0) { Say(Tx("cap.say.no_unit"), UiTone.Warn); return; }
            string e = _env.Focus(uid);
            if (e != null) Say(e, UiTone.Warn);
        }

        private string NameOf(int uid, string fallback)
        {
            string n;
            return _names.TryGetValue(uid, out n) && n != null ? n : (fallback ?? "#" + uid.ToString(CultureInfo.InvariantCulture));
        }

        private static void Toggle(ISet<int> set, int uid) { if (!set.Remove(uid)) set.Add(uid); }

        /// <summary>The picked machines (or, with nothing free, every excluded one) that cannot be taken: says why in the hint line (Warn), writes the journal, forgets stale picks.</summary>
        private bool Refuse(List<CapCandidateView> free, List<CapCandidateView> excl, HashSet<int> picked)
        {
            var bad = CapWindowModel.Refused(picked, free, excl);
            if (bad.Count == 0) return false;
            foreach (var c in bad) _env.Log("cap window refused " + c.Uid.ToString(CultureInfo.InvariantCulture) + " reason=" + c.Reason);
            Say(Tx("cap.say.refused", string.Join("; ", CapWindowModel.RefusalList(bad, 3, _names).ToArray())), UiTone.Warn);
            foreach (var c in bad) picked.Remove(c.Uid);
            return true;
        }

        /// <summary>State of [+] / "To group": enabled = the click does something (an order or a refusal with the reason); tip = what the button says.</summary>
        private string PickState(List<CapCandidateView> free, List<CapCandidateView> excl, string okTip, out bool enabled)
        {
            enabled = true;
            if (free.Count == 0 && excl.Count == 0) { enabled = false; return Tx("cap.ui.err.no_aircraft"); }
            if (free.Count == 0) return CapWindowModel.RefusalSummary(excl);
            if (CapWindowModel.Refused(_pickedAvail, free, excl).Count > 0) return okTip;
            if (_pickedAvail.Count == 0) { enabled = false; return Tx("cap.ui.err.missing_uids"); }
            return okTip;
        }

        // ---------------------------------------------------------------- the window

        public void Draw(UiKit k, CapView v)
        {
            v = v ?? new CapView();
            _lastView = v;
            _values = k.Values;
            EnsureTables();
            var sel = Selected(v);
            _lastSel = sel;
            if (sel != null && _renameFor != GroupName(sel)) { _renameFor = GroupName(sel); _renameText = _renameFor; _renaming = false; }   // T-326
            if (_resetDraft) { _draft.ForGroup = null; _resetDraft = false; }
            var before = _draft;
            _draft = CapOrderPanel.DraftFrom(sel, _draft);
            if (!ReferenceEquals(before, _draft) && sel != null) Prefill(sel, _draft);
            string group = GroupName(sel);
            int au;
            if (group != null && _anchorOf.TryGetValue(group, out au)) _draft.AnchorUid = au;
            var free = new List<CapCandidateView>(); var excl = new List<CapCandidateView>();
            CapWindowModel.SplitCandidates(v, free, excl);
            _names = CapWindowModel.DisplayNames(v);

            if (v.RestoreWaiting) k.Text(Tx("cap.ui.restore_waiting"), UiTone.Warn, true);   // T-211
            if (v.RestorePending)
            {
                k.Text(Tx("cap.ui.restore"), UiTone.Warn, true);
                string r = k.Actions(new UiAction("restore.no", Tx("cap.ui.restore_no")), new UiAction("restore.yes", Tx("cap.ui.restore_yes")));
                if (r == "restore.yes") _env.ResolveRestore(true); else if (r == "restore.no") _env.ResolveRestore(false);
            }

            GroupsSection(k, v, sel, free, excl);
            MembersSection(k, sel);
            OrderSection(k, sel);
            IcptSection(k, sel);   // T-333
            LoadoutSection(k, sel);
            ThresholdsSection(k, sel);
            AvailableSection(k, sel, free, excl, v);

            if (k.DisabledClick != null) Say(k.DisabledClick, UiTone.Warn);
        }

        // ---------------------------------------------------------------- T-178 C5: filled fields and the memory of the last order

        private const string LastKey = "cap.last";

        /// <summary>A group WITHOUT an order starts with every field filled (the last order of this window, else the defaults) and a zone by bearing: "Start" works at once.</summary>
        private void Prefill(CapGroupView g, CapOrderDraft d)
        {
            if (g.Rect != null && g.Rect.Length >= 4) return;   // a patrol group with a zone keeps its order; a group without a zone (also an alert one) starts the patrol fields filled
            string last = _values == null ? null : _values.Value(LastKey, null);
            if (!string.IsNullOrEmpty(last))
            {
                var p = last.Split(';');
                Func<int, string> at = i => i < p.Length ? p[i] : "";
                if (CapWindowModel.Int(at(0)).HasValue) d.NText = at(0);
                d.KText = at(1);
                if (Array.IndexOf(CapWindowModel.Roes, at(2)) >= 0) d.Roe = at(2);
                if (CapOrderPanel.AltOf(at(3)).HasValue) d.AltText = at(3);
                if (CapWindowModel.Num(at(4)).HasValue) d.BearingText = at(4);
                if (CapWindowModel.Num(at(5)).HasValue) d.DistText = at(5);
                if (CapWindowModel.Num(at(6)).HasValue) d.SizeText = at(6);
                if (CapWindowModel.Num(at(7)).HasValue) d.WidthText = at(7);
                d.Anchor = at(8) == "1";
            }
            if (d.ZoneSource == null && (g.Rect == null || g.Rect.Length < 4)) d.ZoneSource = "polar";
        }

        private void RememberLast(CapOrderDraft d)
        {
            if (_values == null) return;
            _values.SetValue(LastKey, string.Join(";", new[] { d.NText, d.KText, d.Roe, d.AltText, d.BearingText, d.DistText, d.SizeText, d.WidthText, d.Anchor ? "1" : "0" }));
        }

        // ---------------------------------------------------------------- T-178 C1: lists of numbers

        private static string Inv(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private static List<double> Merge(IEnumerable<double> baseVals, double cur)
        {
            var l = new List<double>(baseVals);
            bool has = false;
            foreach (double x in l) if (Math.Abs(x - cur) < 1e-6) has = true;
            if (!has) { l.Add(cur); l.Sort(); }
            return l;
        }

        /// <summary>A list of numbers with the current one always in it; the default is marked. Returns the chosen value.</summary>
        private static double PickNum(UiKit k, string id, IEnumerable<double> baseVals, double cur, Func<double, string> fmt, string tip, double dflt, int columns = 1)
        {
            var vals = Merge(baseVals, cur);
            var items = new List<UiItem>(); int sel = 0;
            for (int i = 0; i < vals.Count; i++)
            {
                items.Add(UiItem.Of(fmt(vals[i]), null, null, null, Math.Abs(vals[i] - dflt) < 1e-6));
                if (Math.Abs(vals[i] - cur) < 1e-6) sel = i;
            }
            int r = k.Dropdown(id, items, sel, tip, columns);
            return r >= 0 && r < vals.Count ? vals[r] : cur;
        }

        private static IEnumerable<double> Range(int from, int to) { for (int i = from; i <= to; i++) yield return i; }

        private static string NmText(double v) { return UnitFmt.Nm(v, Math.Abs(v - Math.Round(v)) < 1e-6 ? 0 : 1); }

        private static readonly double[] DistList = { 10, 20, 30, 40, 50, 60, 80, 100, 150, 200 };
        private static readonly double[] SizeList = { 10, 20, 30, 40, 50, 60, 80, 100 };

        /// <summary>"Ready" (K): "Auto (a)" then 0..8; empty text = auto.</summary>
        private string PickReady(UiKit k, string text, int autoK)
        {
            int? cur = string.IsNullOrWhiteSpace(text) ? (int?)null : CapWindowModel.Int(text);
            var vals = new List<int>();
            for (int i = 0; i <= 8; i++) vals.Add(i);
            if (cur.HasValue && cur.Value >= 0 && !vals.Contains(cur.Value)) { vals.Add(cur.Value); vals.Sort(); }
            var items = new List<UiItem> { UiItem.Of(Tx("ui.auto.value", autoK), null, null, Tx("cap.tip.ready_auto"), true) };
            foreach (int v in vals) items.Add(UiItem.Of(v.ToString(CultureInfo.InvariantCulture)));
            int sel = !cur.HasValue ? 0 : vals.IndexOf(cur.Value) + 1;
            if (cur.HasValue && sel == 0) return text;   // a bad text (cannot come from the list): kept, the panel reports it
            int r = k.Dropdown("cap.k", items, sel, Tx("cap.tip.ready"), 1);
            return r <= 0 ? "" : vals[r - 1].ToString(CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- T-178 C6: the status chips of a group

        private static List<UiChip> GroupChips(CapGroupView sel)
        {
            var l = new List<UiChip>();
            var g = sel.Line;
            bool has = sel.Ordered;
            bool alert = sel.Kind == "alert" && sel.Ordered;
            l.Add(has ? new UiChip { Glyph = "✔", Text = Tx("cap.chip.active"), Tone = UiTone.Ok, Tip = Tx("cap.chip.active.tip") }
                      : new UiChip { Glyph = "●", Text = Tx("ui.status.none"), Tone = UiTone.Dim, Tip = Tx("cap.hint.need_zone") });
            if (g.DeckPriority) l.Add(new UiChip { Glyph = "!", Text = Tx("cap.line.prio"), Tone = UiTone.Warn, Tip = Tx("cap.warn.deck_priority") });
            if (g.GapS >= 0) l.Add(new UiChip { Glyph = "!", Text = Tx("cap.line.gap", CapWindow.Mmss(g.GapS)), Tone = UiTone.Warn, Tip = Tx("cap.warn.continuity_gap") });
            if (g.Warns != null) foreach (string w in g.Warns) if (!string.IsNullOrEmpty(w)) l.Add(new UiChip { Glyph = "!", Text = CapWindow.WarnText(w), Tone = UiTone.Warn });
            if (alert)
            {
                l.Add(new UiChip { Text = CapWindowModel.AlertLine(sel), Tone = UiTone.Normal, Tip = Tx("cap.chip.alert.tip") });
                return l;
            }
            if (sel.Ordered) l.Add(new UiChip { Text = ZoneEditMath.SizeLabel(g.WNm, g.HNm), Tone = UiTone.Normal, Tip = Tx("cap.chip.zone.tip") });
            if (has) l.Add(new UiChip { Text = Tx("cap.line.onstation", g.OnStation, g.N), Tone = UiTone.Normal });
            if (g.Relieving > 0) l.Add(new UiChip { Text = Tx("cap.line.relieving", g.Relieving), Tone = UiTone.Normal });
            if (has && g.Ready > 0) l.Add(new UiChip { Text = Tx("cap.line.ready", g.Ready), Tone = UiTone.Normal });
            if (has && g.NextReliefS >= 0) l.Add(new UiChip { Text = Tx("cap.line.relief", CapWindow.Mmss(g.NextReliefS)), Tone = UiTone.Normal });
            string icpt = CapWindowModel.IcptLine(sel);   // T-333
            if (icpt != null) l.Add(new UiChip { Glyph = "\u25B6", Text = icpt, Tone = UiTone.Accent, Tip = Tx("cap.chip.icpt.tip") });
            return l;
        }

        private void GroupsSection(UiKit k, CapView v, CapGroupView sel, List<CapCandidateView> free, List<CapCandidateView> excl)
        {
            bool anyOrdered = false;
            foreach (var g in v.Groups) if (g.Line != null && g.Ordered) anyOrdered = true;   // T-179: an alert group is ordered too
            bool open = k.Section("groups", Tx("cap.ui.groups"), v.Groups.Count > 0 ? v.Groups.Count.ToString(CultureInfo.InvariantCulture) : null);
            if (k.SectionButton("stop_all", Tx("cap.btn.stop_all"), anyOrdered ? Tx("cap.tip.stop_all") : Tx("cap.hint.nothing_to_stop"), anyOrdered))
            {
                var snaps = SnapGroups(v, null);
                if (Do("cap_stop", CapWindowModel.StopCmd(null)) == null) { Say(Tx("cap.say.stopped_all"), UiTone.Ok); TakeUndo(snaps, false, Tx("cap.undo.stop_all", snaps.Count)); }
            }
            _banner.Draw(k, _env.Now());   // T-199: visible also when the section is folded
            if (!open) return;
            var labels = new List<string>(); var tones = new List<UiTone>(); var names = new List<string>(); var tips = new List<string>();
            int idx = -1;
            foreach (var g in v.Groups)
            {
                if (g.Line == null) continue;
                if (g.Line.Group == _group) idx = labels.Count;
                string chipMode = CapWindowModel.ModeChip(g);
                labels.Add(Tx("cap.lbl.chip", g.Line.Group, g.Members.Count) + (chipMode == null ? "" : " " + chipMode));
                names.Add(g.Line.Group);
                tips.Add(Tx("cap.tip.group", g.Line.Group));
                tones.Add(g.Line.DeckPriority || g.Line.GapS >= 0 ? UiTone.Warn : UiTone.Normal);
            }
            if (labels.Count == 0) k.Hint(Tx("cap.hint.no_groups"));
            else
            {
                int hit = k.Chips("cap.grp", labels, idx, tones, tips);
                if (hit >= 0 && hit < names.Count && names[hit] != _group) { _group = names[hit]; _picked.Clear(); _drawing = false; _pick.Cancel("group"); }
            }
            bool newOn;
            string newTip = PickState(free, excl, Tx("cap.tip.new_icon"), out newOn);
            if (k.IconButton("new", "+", newTip, newOn))
            {
                if (!Refuse(free, excl, _pickedAvail) && Do("cap_group", CapWindowModel.GroupCreate(_pickedAvail)) == null) _pickedAvail.Clear();
            }
            k.Space(5);   // ADR-0077: [+] and [-] are at least 8 px x s apart
            if (k.IconButton("disband", "−", sel != null ? Tx("cap.tip.disband", GroupName(sel)) : Tx("cap.ui.need_group"), sel != null) && sel != null)
            {
                var snaps = SnapGroups(v, GroupName(sel));
                if (Do("cap_group", CapWindowModel.GroupDisband(GroupName(sel))) == null)
                {
                    _anchorOf.Remove(GroupName(sel)); _group = null;
                    TakeUndo(snaps, true, Tx("cap.undo.disband", snaps.Count > 0 ? snaps[0].Name : ""));
                }
            }
            // T-326: rename of the selected group, like the ASW window (T-150); an empty field returns the automatic name
            k.Space(5);
            if (k.IconButton("grp_rename", "\u2026", Tx("cap.tip.rename_icon"), sel != null) && sel != null)
            {
                _renaming = !_renaming; _renameText = GroupName(sel);
            }
            if (_renaming && sel != null)
            {
                k.LabelColumn(Tx("cap.lbl.rename"));
                k.Row(Tx("cap.lbl.rename"));
                _renameText = k.Number("rename", _renameText, 14);
                string typed = (_renameText ?? "").Trim();
                bool canRename = typed.Length > 0 ? typed != GroupName(sel) : !sel.AutoName;
                if (k.Button("rename_ok", Tx("cap.btn.rename"), Tx("cap.tip.rename"), canRename) && canRename)
                {
                    string old = GroupName(sel);
                    if (Do("cap_group", CapWindowModel.GroupRename(old, typed)) == null)
                    {
                        string named = CapWindowModel.RenamedTo(_lastAck) ?? (typed.Length > 0 ? typed : old);
                        MoveName(old, named);
                        _renameFor = named; _renameText = named; _renaming = false;
                        Say(Tx(typed.Length == 0 ? "cap.say.renamed_auto" : "cap.say.renamed", named), UiTone.Ok);
                    }
                }
            }
            if (sel != null && sel.Line != null) k.StatusChips("cap.status", GroupChips(sel), 2);
        }

        /// <summary>T-326: a renamed group keeps the selection, the draft and the anchor ship of its zone.</summary>
        private void MoveName(string old, string named)
        {
            if (old == named) return;
            if (_group == old) _group = named;
            if (_draft != null && _draft.ForGroup == old) _draft.ForGroup = named;
            if (_editGroup == old) _editGroup = named;
            int au;
            if (_anchorOf.TryGetValue(old, out au)) { _anchorOf.Remove(old); _anchorOf[named] = au; }
        }

        private void MembersSection(UiKit k, CapGroupView sel)
        {
            var rows = sel == null ? new List<CapMemberView>() : sel.Members;
            string head = sel == null ? Tx("cap.col.aircraft") : Tx("cap.ui.members", GroupName(sel));
            bool open = k.Section("members", head, rows.Count.ToString(CultureInfo.InvariantCulture));
            string tip = sel == null ? Tx("cap.ui.need_group") : _picked.Count == 0 ? Tx("cap.ui.err.missing_uids") : Tx("cap.tip.remove");
            bool canRemove = sel != null && _picked.Count > 0;
            if (rows.Count > 0 && k.SectionButton("remove", Tx("cap.btn.remove_short"), tip, canRemove))
                if (Do("cap_group", CapWindowModel.GroupRemove(GroupName(sel), _picked)) == null) _picked.Clear();
            if (!open) return;
            var cells = new Dictionary<int, string[]>();
            Func<int, string[]> row = r => { string[] c; if (!cells.TryGetValue(r, out c)) cells[r] = c = CapWindowModel.MemberCells(rows[r], NameOf(rows[r].Uid, rows[r].Name)); return c; };
            k.Table(_tMembers, rows.Count, (r, c) => row(r)[c], null, _picked, r => rows[r].Uid, 6, Tx("cap.hint.no_members"),
                r => Toggle(_picked, rows[r].Uid), Tx("cap.tip.row"),
                (r, c) => FocusUnit(c == CapWindowModel.CAircraft ? CapWindowModel.AircraftFocusUid(rows[r].Air, rows[r].Uid, rows[r].ShipUid) : rows[r].ShipUid),
                (r, c) => Tx("cap.tip.link", c == CapWindowModel.CAircraft ? row(r)[c] : rows[r].ShipName) + (c == CapWindowModel.CAircraft && rows[r].HangarN > 0 ? "\n" + CapWindowModel.HangarTip(rows[r].HangarN) : ""));
        }

        private string ZoneStatus(CapGroupView sel, CapOrderDraft d)
        {
            if (d.ZoneSource == "rect" && d.Zone != null && d.Zone.Length >= 4)
            {
                double dLat = Math.Abs(d.Zone[2] - d.Zone[0]) * 60.0;
                double dLon = Math.Abs(d.Zone[3] - d.Zone[1]) * 60.0 * ZoneEditMath.LonK((d.Zone[0] + d.Zone[2]) / 2.0, AswMath.WorldGlobe);
                return Tx("cap.zone.rect", Math.Round(dLon).ToString("0", CultureInfo.InvariantCulture), Math.Round(dLat).ToString("0", CultureInfo.InvariantCulture));
            }
            if (d.ZoneSource == "polar") return Tx("cap.zone.polar", d.BearingText, d.DistText);
            return sel != null && sel.Rect != null && sel.Rect.Length >= 4 ? Tx("cap.zone.group") : Tx("cap.zone.none");
        }

        private void OrderSection(UiKit k, CapGroupView sel)
        {
            if (!k.Section("task", Tx("cap.sec.task"))) return;
            var d = _draft;
            bool alertKind = d.Kind == "alert";
            if (alertKind) k.LabelColumn(Tx("cap.lbl.mode"), Tx("cap.lbl.protect"), Tx("cap.lbl.ready_n"), Tx("cap.lbl.radius"), Tx("cap.lbl.after"));
            else k.LabelColumn(Tx("cap.lbl.mode"), Tx("cap.ui.on_station"), Tx("cap.ui.roe"), Tx("cap.lbl.alt_row"), Tx("cap.lbl.zone"), Tx("cap.lbl.bearing_row"), Tx("cap.lbl.dist_row"), Tx("cap.lbl.size_row"));
            ModeRow(k, sel);
            alertKind = d.Kind == "alert";
            int ship = 0;
            if (alertKind) AlertRows(k, sel, d);
            else PatrolRows(k, sel, d, out ship);

            string code = CapOrderPanel.Hint(sel, d, ship);
            var st = CapOrderPanel.Stop(sel);
            var act = CapOrderPanel.Action(sel, d);
            // T-179: "Scramble now" of an alert group (grey with the reason in the tip)
            UiAction[] aux = null;
            if (sel != null && sel.Kind == "alert" && sel.Ordered)
                aux = new[] { new UiAction("scramble", Tx("cap.btn.scramble"), CapWindowModel.ScrambleTip(sel), sel.ScrambleBlock == null) };
            string pressed = k.Footer(new UiFooter
            {
                Aux = aux,
                Stop = new UiAction("stop", st.Label, st.Tip ?? Tx("cap.tip.stop"), st.Enabled),
                Primary = new UiAction(act.Id, act.Label, act.Tip ?? Tx(act.Id == "start" ? "cap.tip.start" : "cap.tip.apply"), act.Enabled),
                Changed = code == "dirty",
                Reason = act.Enabled ? null : act.Tip,
            });
            if (pressed == "stop")
            {
                _env.Log("cap window press stop " + GroupName(sel));
                if (Do("cap_stop", CapWindowModel.StopCmd(GroupName(sel))) == null) Say(Tx("cap.say.stopped", GroupName(sel)), UiTone.Ok);
            }
            else if (pressed == "scramble" && sel != null)
            {
                _env.Log("cap window press scramble " + GroupName(sel));
                if (Do("cap_scramble", new Dictionary<string, object> { { "group", GroupName(sel) } }) == null) Say(Tx("cap.say.scramble", GroupName(sel)), UiTone.Ok);
            }
            else if (pressed == "start" || pressed == "apply") { _env.Log("cap window press " + pressed + " " + GroupName(sel)); ApplyDraft(sel); }
        }

        private void PatrolRows(UiKit k, CapGroupView sel, CapOrderDraft d, out int ship)
        {
            // N and K: lists (T-178 C1)
            k.Row(Tx("cap.ui.on_station"));
            int nCur = CapWindowModel.Int(d.NText) ?? CapOrderPanel.DefaultN;
            int n = (int)PickNum(k, "cap.n", Range(1, 12), nCur, v => Inv(v), Tx("cap.tip.n"), CapOrderPanel.DefaultN, 2);
            if (n != nCur || CapWindowModel.Int(d.NText) == null) d.NText = n.ToString(CultureInfo.InvariantCulture);
            k.Unit(Tx("cap.ui.ready"));
            int members = sel == null ? 0 : sel.Members.Count;
            d.KText = PickReady(k, d.KText, Math.Max(0, Math.Min(Math.Max(1, n), members - n)));

            RoeRow(k, sel, d.Roe, r => d.Roe = r);
            // T-178 C2: the altitude, "Auto" = 0 (the game's default)
            k.Row(Tx("cap.lbl.alt_row"), Tx("cap.tip.alt"));
            int? altNow = CapOrderPanel.AltOf(d.AltText);
            bool auto = !altNow.HasValue || altNow.Value == 0;
            double dfltAlt = CapOrderPanel.DefaultAltFt > 0 ? CapOrderPanel.DefaultAltFt : 15000;
            double altVal = auto ? dfltAlt : altNow.Value;
            double altShown = UiKit.SnapValue(altVal, AltMin, AltMax, AltStep);
            bool autoWas = auto;
            double altRes = k.AutoSlider("cap.alt", ref auto, 0, AltMin, AltMax, AltStep, altVal, v => auto ? Tx("cap.ui.alt_auto") : UnitFmt.Ft(v), Tx("cap.tip.alt"), dfltAlt);
            if (auto) d.AltText = "0";
            else if (autoWas || Math.Abs(altRes - altShown) > 1e-9) d.AltText = Math.Round(altRes).ToString("0", CultureInfo.InvariantCulture);

            bool hasGroup = sel != null;
            ship = CapWindowModel.OriginShip(sel, _env.SelectedShip());
            string originName = ship != 0 ? (CapWindowModel.ShipNameOf(sel, ship) ?? _env.ShipName(ship)) : null;
            string fromTip = ship != 0 ? Tx("cap.ui.from_ship", originName) : Tx("cap.ui.from_view");

            // T-178 C3: the zone: [Draw] [By bearing]
            k.Row(Tx("cap.lbl.zone"));
            bool polarOn = d.ZoneSource == "polar" && !_drawing;
            int mode = polarOn ? 1 : 0;
            int m = k.Segments("cap.zsrc", new[] { Tx("cap.ui.zone_src.draw"), Tx("cap.ui.zone_src.polar") }, mode,
                               new[] { Tx("cap.ui.draw_hint"), hasGroup ? Tx("cap.tip.polar") + " (" + fromTip + ")" : Tx("cap.ui.need_group") }, new[] { hasGroup || _drawing, hasGroup });
            if (m != mode)
            {
                if (m == 1) { d.ZoneSource = "polar"; _drawing = false; Say(Tx("cap.say.zone_polar"), UiTone.Ok); }
                else d.ZoneSource = d.Zone != null && d.Zone.Length >= 4 ? "rect" : null;
                polarOn = m == 1;
            }
            if (!polarOn)
            {
                if (_drawing)
                {
                    if (k.Button("cap.draw.cancel", Tx("ui.map.cancel"), Tx("ui.map.cancel.tip"))) CancelInput();
                    k.Text(Tx("cap.ui.draw_hint2"), UiTone.Accent, true, 2);
                }
                else if (k.Button("cap.draw", Tx("cap.btn.draw"), Tx("cap.ui.draw_hint"), hasGroup, d.ZoneSource == "rect" ? UiTone.Accent : UiTone.Normal))   // i18n-allow: zone source code
                {
                    _drawing = true;
                    Say(Tx("cap.ui.draw_hint"), UiTone.Dim);
                }
            }
            k.Text(ZoneStatus(sel, d), d.ZoneSource != null ? UiTone.Accent : UiTone.Dim);

            if (polarOn)
            {
                k.Row(Tx("cap.lbl.bearing_row"), fromTip);
                double bCur = CapWindowModel.Num(d.BearingText) ?? 0;
                double? course = null;
                if (ship != 0) { try { course = _env.ShipCourse(ship); } catch { } }
                double b = k.Compass("cap.bearing", bCur, course, fromTip);
                if (Math.Abs(b - CompassMath.Norm(bCur)) > 1e-9) d.BearingText = CapWindowModel.Fmt(b);
                k.Row(Tx("cap.lbl.dist_row"));
                d.DistText = Inv(PickNum(k, "cap.dist", DistList, CapWindowModel.Num(d.DistText) ?? 40, NmText, Tx("cap.tip.dist"), 40, 2));
                k.Row(Tx("cap.lbl.size_row"));
                d.SizeText = Inv(PickNum(k, "cap.size", SizeList, CapWindowModel.Num(d.SizeText) ?? 40, NmText, Tx("cap.tip.size"), 40, 2));
                k.Unit("×");
                d.WidthText = Inv(PickNum(k, "cap.width", SizeList, CapWindowModel.Num(d.WidthText) ?? 20, NmText, Tx("cap.tip.width"), 20, 2));
            }
            k.Row(null);
            d.Anchor = k.Check("cap.anchor", d.Anchor, Tx("cap.ui.anchor_short"), Tx("cap.ui.anchor"));
            // the origin ship of the bearing, by name ("from: Dwight D. Eisenhower"); the map centre when the group has no deck
            if (hasGroup) k.Text(Tx("cap.ui.origin", originName ?? Tx("cap.ui.origin_view")), UiTone.Dim, true, 1, fromTip);
            // T-179: the patrol takes the alert aircraft that miss a bird (the lend after the raid)
            k.Row(null);
            d.AcceptAlert = k.Check("cap.accept_alert", d.AcceptAlert, Tx("cap.lbl.accept_alert"), Tx("cap.tip.accept_alert"));
            // T-333 (ADR-0129): intercept of a target in / near the zone: on / off and the margin
            k.Row(null);
            if (d.Icpt == null) d.Icpt = CapOrderPanel.DefaultIcptSpec.Clone();
            d.Icpt.On = k.Check("cap.icpt.on", d.Icpt.On, Tx("cap.lbl.icpt"), Tx("cap.tip.icpt"));
            if (d.Icpt.On)
            {
                k.Unit(Tx("cap.lbl.icpt_margin"));
                d.Icpt.MarginNm = PickNum(k, "cap.icpt.margin", IcptMargins, d.Icpt.MarginNm, NmText, Tx("cap.tip.icpt_margin"), CapOrderPanel.DefaultIcptSpec.MarginNm, 3);
            }
        }

        private static readonly double[] IcptMargins = { 0, 5, 10, 20, 30 };

        /// <summary>T-333: the details of the zone intercept (per target, keep on station, targets, speed); edits the draft, goes out with Apply.</summary>
        private void IcptSection(UiKit k, CapGroupView sel)
        {
            var d = _draft;
            if (d == null || d.Kind == "alert") return;
            if (d.Icpt == null) d.Icpt = CapOrderPanel.DefaultIcptSpec.Clone();
            var a = d.Icpt;
            string sum = a.On ? Tx("cap.sum.icpt", a.PerTarget, a.Keep) : Tx("cap.sum.icpt_off");
            if (!k.Section("icpt", Tx("cap.sec.icpt"), null, false, sum)) return;
            if (sel == null) { k.Hint(Tx("cap.ui.need_group")); return; }
            k.LabelColumn(Tx("cap.lbl.icpt_per"), Tx("cap.lbl.icpt_keep"), Tx("cap.lbl.icpt_targets"), Tx("cap.lbl.icpt_speed"));
            k.Row(Tx("cap.lbl.icpt_per"), Tx("cap.tip.icpt_per"));
            a.PerTarget = (int)PickNum(k, "cap.icpt.per", Range(1, 4), a.PerTarget, v => Inv(v), Tx("cap.tip.icpt_per"), CapOrderPanel.DefaultIcptSpec.PerTarget, 4);
            k.Row(Tx("cap.lbl.icpt_keep"), Tx("cap.tip.icpt_keep"));
            a.Keep = (int)PickNum(k, "cap.icpt.keep", Range(0, 6), a.Keep, v => Inv(v), Tx("cap.tip.icpt_keep"), CapOrderPanel.DefaultIcptSpec.Keep, 4);
            k.Row(Tx("cap.lbl.icpt_targets"), Tx("cap.tip.icpt_targets"));
            bool air = HasKind(a.Targets, "air"), helo = HasKind(a.Targets, "helo"), unk = HasKind(a.Targets, "unknown");
            air = k.Check("cap.icpt.t_air", air, Tx("cap.lbl.icpt_t_air"), Tx("cap.tip.icpt_targets"));
            helo = k.Check("cap.icpt.t_helo", helo, Tx("cap.lbl.icpt_t_helo"), Tx("cap.tip.icpt_targets"));
            unk = k.Check("cap.icpt.t_unknown", unk, Tx("cap.lbl.icpt_t_unknown"), Tx("cap.tip.icpt_unknown"));
            var parts = new List<string>(); if (air) parts.Add("air"); if (helo) parts.Add("helo"); if (unk) parts.Add("unknown");
            if (parts.Count > 0) a.Targets = string.Join(",", parts.ToArray());   // an empty choice is refused: the last kind stays
            k.Row(Tx("cap.lbl.icpt_speed"), Tx("cap.tip.icpt_speed"));
            int sp = k.Segments("cap.icpt.speed", new[] { Tx("cap.val.dash_max_short"), Tx("cap.val.dash_cruise_short") }, a.Speed == "cruise" ? 1 : 0,
                                new[] { Tx("cap.tip.dash_max"), Tx("cap.tip.dash_cruise") }, null, 0);
            a.Speed = sp == 1 ? "cruise" : "max";
        }

        private static bool HasKind(string csv, string kind)
        {
            foreach (string t in (csv ?? "").Split(',')) if (string.Equals(t.Trim(), kind, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The ROE row with its warnings; set receives a new value.</summary>
        private void RoeRow(UiKit k, CapGroupView sel, string roe, Action<string> set)
        {
            k.Row(Tx("cap.ui.roe"));
            var roes = CapWindowModel.Roes;
            var rl = new List<string>(); var rt = new List<string>();
            foreach (string r in roes) { rl.Add(Tx("cap.ui.roe." + r)); rt.Add(Tx("cap.ui.roe.tip_" + r)); }
            int ri = Math.Max(0, Array.IndexOf(roes, roe));
            string now = roes[k.Segments("cap.roe", rl, ri, rt, null, Array.IndexOf(roes, "tight"))];
            set(now);
            // T-175: ROE warnings and the air-only note
            if (now == "free") k.Text(Tx("cap.ui.roe_free_warn"), UiTone.Warn, true, 1);
            if (sel != null && !sel.AirOnly) k.Text(Tx("cap.ui.air_only_off"), UiTone.Warn, true, 1);
        }

        private const double AltMin = 1000, AltMax = 40000, AltStep = 500;

        private void LoadoutSection(UiKit k, CapGroupView sel)
        {
            var types = sel == null ? new List<string>() : CapWindowModel.TypesOf(sel);
            string sum = null;
            if (types.Count > 0)
            {
                var parts = new List<string>();
                foreach (string type in types)
                {
                    string c;
                    if (!_draft.Loadouts.TryGetValue(type, out c) || string.IsNullOrEmpty(c)) c = CapWindowModel.LoadoutOf(sel, type);
                    parts.Add(CapWindowModel.TypeText(CapWindowModel.TypeLabelOf(sel, type), type) + ": " + LoadoutShown(type, c));
                }
                sum = string.Join(", ", parts.ToArray());
            }
            if (!k.Section("loadout", Tx("cap.ui.loadouts"), null, false, sum)) return;
            if (sel == null) { k.Hint(Tx("cap.ui.need_group")); return; }
            if (types.Count == 0) { k.Hint(Tx("cap.hint.no_members")); return; }
            // 0.28.1: the row names the aircraft type as the game shows it (F-14A), not its file id (usn_f-14a)
            var typeTexts = new List<string>();
            foreach (string type in types) typeTexts.Add(CapWindowModel.TypeText(CapWindowModel.TypeLabelOf(sel, type), type));
            k.LabelColumn(typeTexts.ToArray());
            for (int ti = 0; ti < types.Count; ti++)
            {
                string type = types[ti];
                string cur;
                if (!_draft.Loadouts.TryGetValue(type, out cur) || string.IsNullOrEmpty(cur)) cur = CapWindowModel.LoadoutOf(sel, type);
                string shown = LoadoutShown(type, cur);
                k.Row(typeTexts[ti]);
                int dir = k.Cycle("cap.lo." + type, shown, Tx("cap.tip.loadout"));
                if (dir != 0) _draft.Loadouts[type] = CapWindowModel.CycleLoadout(cur, _env.LoadoutNames(type), dir);
                string ready = "";
                double rm = -1;
                try { rm = _env.LoadoutReadyMin(type, cur); } catch { }
                if (rm >= 0) ready = " · " + Tx("cap.ui.loadout_ready", Math.Round(rm).ToString("0", CultureInfo.InvariantCulture));
                k.Text(Tx("cap.ui.loadout_aam", CapWindowModel.AamOfType(sel, type)) + ready, UiTone.Dim);
            }
        }

        /// <summary>"auto" -> the localized word; a game loadout -> its display name (the internal name when the game has none).</summary>
        private string LoadoutShown(string type, string name)
        {
            if (string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase)) return _draft != null && _draft.Kind == "alert" ? AlertAutoText(type) : Tx("cap.ui.loadout_auto");   // T-179: Auto of an alert group = the fastest
            // the game lookup walks the decks: cached per (type, name) for 5 s, the body is drawn several times a frame
            string key = type + "|" + name;
            KeyValuePair<double, string> c;
            double now = _env.Now();
            if (!_loDisplay.TryGetValue(key, out c) || now - c.Key > 5.0 || now < c.Key)
            {
                string disp = null;
                try { disp = _env.LoadoutLabel(type, name); } catch { }
                c = new KeyValuePair<double, string>(now, disp);
                if (_loDisplay.Count > 64) _loDisplay.Clear();
                _loDisplay[key] = c;
            }
            return CapWindowModel.LoadoutText(name, c.Value);
        }

        private readonly Dictionary<string, KeyValuePair<double, string>> _loDisplay = new Dictionary<string, KeyValuePair<double, string>>();

        private void ThresholdsSection(UiKit k, CapGroupView sel)
        {
            if (!k.Section("thresholds", Tx("cap.ui.params"), null, false)) return;
            if (_thrMissile < 0) { _thrMissile = Math.Round(CapOrderPanel.DefaultMissilePct * 100); _thrMargin = CapOrderPanel.DefaultRingMarginNm; _thrFloor = Math.Round(CapOrderPanel.DefaultFuelFloorPct * 100); }
            k.LabelColumn(Tx("cap.ui.missile_pct"), Tx("cap.ui.fuel_margin"), Tx("cap.ui.fuel_floor"));
            k.Row(Tx("cap.ui.missile_pct"));
            _thrMissile = k.Slider("cap.thr.missile", 0, 100, 5, _thrMissile, v => Inv(v) + " %", Tx("cap.tip.thr_missile"), Math.Round(CapOrderPanel.DefaultMissilePct * 100));
            k.Row(Tx("cap.ui.fuel_margin"));
            _thrMargin = k.Slider("cap.thr.margin", 0, 100, 1, _thrMargin, v => UnitFmt.Nm(v, 0), Tx("cap.tip.thr_margin"), CapOrderPanel.DefaultRingMarginNm);
            k.Row(Tx("cap.ui.fuel_floor"));
            _thrFloor = k.Slider("cap.thr.floor", 0, 50, 1, _thrFloor, v => Inv(v) + " %", Tx("cap.tip.thr_floor"), Math.Round(CapOrderPanel.DefaultFuelFloorPct * 100));
            k.Row(null);
            _needLr = k.Check("cap.thr.lr", _needLr, Tx("cap.ui.need_lr"), Tx("cap.tip.need_lr"));
            k.Wrap();
            _dash = k.Check("cap.thr.dash", _dash, Tx("cap.ui.dash"), Tx("cap.tip.dash"));
            if (k.Footer(new UiFooter { Primary = new UiAction("apply_thr", Tx("cap.btn.apply_thresholds"), Tx("cap.tip.apply_thr")) }) == "apply_thr")
            {
                string err;
                var pc = CapWindowModel.ParamsCmd(GroupName(sel), Inv(_thrMissile), Inv(_thrMargin), Inv(_thrFloor), _needLr, _dash, out err);
                if (pc == null) Say(ErrText(err), UiTone.Error);
                else if (Do("cap_params", pc) == null) Say(Tx("cap.say.params"), UiTone.Ok);
            }
        }

        // ---------------------------------------------------------------- T-178 C7: "Undo" for "Stop all" and "Disband"

        private sealed class GroupSnap
        {
            public string Name, Roe, Anchor, Kind; public double[] Rect; public int N, K, AnchorUid; public bool Ordered, AcceptAlert; public double AltFt; public CapAlertSpec Alert; public CapInterceptSpec Icpt;
            public List<int> Uids = new List<int>(); public List<string> UidNames = new List<string>();
            public Dictionary<string, string> Loadouts = new Dictionary<string, string>();
        }

        /// <summary>The orders and members of the groups BEFORE the action (only: the ordered ones for "Stop all"; one group, ordered or not, for "Disband").</summary>
        private List<GroupSnap> SnapGroups(CapView v, string only)
        {
            var l = new List<GroupSnap>();
            foreach (var g in v.Groups)
            {
                if (g.Line == null) continue;
                if (only != null ? g.Line.Group != only : !g.Ordered) continue;
                var s = new GroupSnap { Name = g.Line.Group, Roe = g.Roe, Anchor = g.Anchor, Rect = g.Rect == null ? null : (double[])g.Rect.Clone(), N = g.N, K = g.K, Kind = g.Kind, Alert = g.AlertSpec == null ? null : g.AlertSpec.Clone(), AcceptAlert = g.AcceptAlert, Ordered = g.Ordered, AltFt = g.AltFt, Icpt = g.Icpt == null ? null : g.Icpt.Clone() };
                int au;
                if (_anchorOf.TryGetValue(s.Name, out au)) s.AnchorUid = au;
                foreach (var kv in g.Loadouts) s.Loadouts[kv.Key] = kv.Value;
                foreach (var m in g.Members) { s.Uids.Add(m.Uid); s.UidNames.Add(NameOf(m.Uid, m.Name)); }
                l.Add(s);
            }
            return l;
        }

        private Dictionary<string, object> OrderOf(GroupSnap s)
        {
            Dictionary<string, object> o;
            if (s.Kind == "alert" && s.Alert != null) o = CapOrderPanel.AlertOrder(s.Name, s.Alert);   // T-179: the alert kind comes back with every setting
            else
            {
                bool hasRect = s.Rect != null && s.Rect.Length >= 4;
                if (hasRect) o = CapWindowModel.OrderRect(s.Name, s.Rect, s.N, s.K, s.Roe, s.Anchor == "unit" ? s.AnchorUid : 0, (int)s.AltFt);
                else o = new Dictionary<string, object> { { "group", s.Name } };
                if (o == null) return null;
                if (!hasRect && !string.IsNullOrEmpty(s.Roe)) o["roe"] = s.Roe;
                o["kind"] = "patrol"; o["accept_alert"] = s.AcceptAlert;
                CapOrderPanel.AddIcpt(o, s.Icpt);   // T-333
            }
            if (o == null) return null;
            var lo = new Dictionary<string, object>();
            foreach (var kv in s.Loadouts) if (!string.IsNullOrEmpty(kv.Value) && !string.Equals(kv.Value, "auto", StringComparison.OrdinalIgnoreCase)) lo[kv.Key] = kv.Value;
            if (lo.Count > 0) o["loadouts"] = lo;
            return o;
        }

        private void TakeUndo(List<GroupSnap> snaps, bool disbanded, string title)
        {
            if (snaps == null || snaps.Count == 0) return;
            double now = _env.Now();
            _undo.Log = _env.Log;
            _undoResult = null;
            _undo.Take("cap", title, snaps.Count, () => Restore(snaps, disbanded), now);
            _banner.Show(title, () => _undo.Left("cap", _env.Now()), DoUndo, () => _undoResult, now);
        }

        private void DoUndo()
        {
            var r = _undo.Restore("cap", _env.Now());
            if (r == null) { _undoResult = Tx("cap.undo.gone"); return; }
            string s = Tx("ui.undo.done", r.Restored, r.Groups);
            if (r.Skipped.Count > 0) s += "; " + string.Join("; ", r.Skipped.ToArray());
            _undoResult = s;
        }

        /// <summary>T-178 I3: presses "Undo" without the card (dev command ui_undo); the result line, null = no snapshot left.</summary>
        public string PressUndo()
        {
            if (_undo.Left("cap", _env.Now()) <= 0) return null;
            DoUndo();
            return _undoResult;
        }

        /// <summary>Drops the snapshot (a new mission).</summary>
        public void ResetUndo() { _undo.Reset(); _undoResult = null; _banner.Hide(); }

        private UiUndoResult Restore(List<GroupSnap> snaps, bool disbanded)
        {
            var res = new UiUndoResult { Groups = snaps.Count };
            var v = _lastView ?? new CapView();
            foreach (var s in snaps)
            {
                if (disbanded)
                {
                    var take = new List<int>();
                    for (int i = 0; i < s.Uids.Count; i++)
                    {
                        CapCandidateView c = null;
                        foreach (var x in v.Candidates) if (x.Uid == s.Uids[i]) c = x;
                        if (c != null && string.IsNullOrEmpty(c.Reason)) take.Add(s.Uids[i]);
                        else res.Skipped.Add(Tx("ui.undo.skipped", s.UidNames[i], c != null && !string.IsNullOrEmpty(c.Reason) ? CapWindow.ReasonText(c.Reason) : Tx("cap.reason.gone")));
                    }
                    if (take.Count == 0) continue;
                    var cmd = CapWindowModel.GroupCreate(take); cmd["group"] = s.Name;
                    if (Do("cap_group", cmd) != null) { res.Skipped.Add(Tx("ui.undo.skipped", s.Name, Tx("cap.undo.no_group"))); continue; }
                }
                else
                {
                    CapGroupView cur = null;
                    foreach (var g in v.Groups) if (g.Line != null && g.Line.Group == s.Name) cur = g;
                    if (cur == null) { res.Skipped.Add(Tx("ui.undo.skipped", s.Name, Tx("cap.undo.no_group"))); continue; }
                }
                var order = s.Ordered ? OrderOf(s) : null;
                if (order != null && Do("cap_order", order) != null) { res.Skipped.Add(Tx("ui.undo.skipped", s.Name, Tx("cap.undo.no_order"))); continue; }
                res.Restored++;
            }
            return res;
        }

        private void AvailableSection(UiKit k, CapGroupView sel, List<CapCandidateView> free, List<CapCandidateView> excl, CapView view)
        {
            bool open = k.Section("available", Tx("cap.sec.available"), free.Count.ToString(CultureInfo.InvariantCulture));
            if (sel != null)
            {
                bool on;
                string tip = PickState(free, excl, Tx("cap.tip.add", GroupName(sel)), out on);
                if (k.SectionButton("add", Tx("cap.btn.add_to", GroupName(sel)), tip, on))
                    if (!Refuse(free, excl, _pickedAvail) && Do("cap_group", CapWindowModel.GroupAdd(GroupName(sel), _pickedAvail)) == null) _pickedAvail.Clear();
            }
            if (!open) return;
            string hint = CapWindowModel.AvailableHintKey(free.Count, excl.Count);
            if (hint != null) { k.Text(Tx(hint), UiTone.Dim, true, 1); return; }
            var rows = CapWindowModel.AvailableRows(view);
            if (free.Count == 0)
            {
                string tip = string.Join("\n", CapWindowModel.RefusalList(excl, 8, _names).ToArray());
                k.Text(CapWindowModel.RefusalSummary(excl), UiTone.Dim, false, 1, tip);
                if (rows.Count == 0) return;
            }
            var cells = new Dictionary<int, string[]>();
            Func<int, string[]> row = r => { string[] c; if (!cells.TryGetValue(r, out c)) cells[r] = c = CapWindowModel.ShownRefused(rows[r]) ? CapWindowModel.ForeignCells(rows[r], NameOf(rows[r].Uid, rows[r].Name)) : CapWindowModel.CandidateCells(rows[r], NameOf(rows[r].Uid, rows[r].Name)); return c; };
            k.Table(_tAvail, rows.Count, (r, c) => row(r)[c], r => CapWindowModel.ShownRefused(rows[r]) ? UiTone.Dim : UiTone.Normal, _pickedAvail, r => rows[r].Uid, 5, null,
                r =>
                {
                    if (CapWindowModel.ShownRefused(rows[r])) { _pickedAvail.Remove(rows[r].Uid); Say(Tx("cap.say.refused", CapWindowModel.RefusalLine(rows[r], _names)), UiTone.Warn); }
                    else Toggle(_pickedAvail, rows[r].Uid);
                }, Tx("cap.tip.row"),
                (r, c) => FocusUnit(c == CapWindowModel.CAircraft ? CapWindowModel.AircraftFocusUid(rows[r].Air, rows[r].Uid, rows[r].ShipUid) : rows[r].ShipUid),
                (r, c) => Tx("cap.tip.link", c == CapWindowModel.CAircraft ? row(r)[c] : rows[r].ShipName) + (c == CapWindowModel.CAircraft && rows[r].HangarN > 0 ? "\n" + CapWindowModel.HangarTip(rows[r].HangarN) : ""));
        }

        // ---------------------------------------------------------------- collapsed line and warnings

        /// <summary>One short line for the collapsed window.</summary>
        public string Summary(CapView v)
        {
            v = v ?? new CapView();
            CapGroupView g = null;
            foreach (var x in v.Groups) { if (x.Line == null) continue; if (g == null) g = x; if (x.Line.Group == _group) { g = x; break; } }
            if (g == null) return Tx("cap.sum.none");
            if (g.Kind == "alert" && g.Ordered) return Tx("cap.sum.alert", g.Line.Group, g.ReadyNow, g.AlertSpec == null ? 0 : g.AlertSpec.Ready);   // T-179
            return Tx("cap.sum.group", g.Line.Group, g.Line.OnStation, g.Line.N);
        }

        /// <summary>Warnings of the selected group for the hint line of the frame.</summary>
        public IList<string> Warnings(CapView v)
        {
            var l = new List<string>();
            if (v == null) return l;
            foreach (var g in v.Groups)
                if (g.Line != null && g.Line.Group == _group && g.Line.Warns != null)
                    foreach (string w in g.Line.Warns) l.Add(CapWindow.WarnText(w));
            return l;
        }
    }
}
