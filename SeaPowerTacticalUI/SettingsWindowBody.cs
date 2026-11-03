// T-144 (ADR-0050 sections 4, 5, 7): the body of the settings window. Pure C#: only UiKit, UiText and the registry; no Unity.
// The rows come from UiSettingsRegistry (nothing here names a module or a key); the texts are settings.<cfgsection>.<key>[.hint|.opt.<v>].
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge.Ui
{
    internal sealed class SettingsView
    {
        public UiSettingsRegistry Reg;
        public SettingsDraft Draft;
        public IList<HotkeyDraftResult> Keys;   // dry run of Draft.HotkeyRows() (same order); null = mod-only check is made here
        public bool ShowAdvanced;
        public HotkeyRecorder Rec;
        public string Status;                   // the line of the last Apply ("Applied: ...")
        public SettingsViewCache Cache = new SettingsViewCache();   // T-239
    }

    /// <summary>T-239: one row of the window with everything that does not change from pass to pass (texts, ids, widget, formatter, option lists).</summary>
    internal sealed class SettingsRow
    {
        public UiSettingEntry E;
        public string Label, KeyLabel, Id, BaseTip;
        public string[] Opts, BaseTips;                 // Choice / Multi: option labels and one tip per option
        public string ReqText;                          // Multi: the always-on options as a line
        public string[][] PartLabels, PartTips; public string[] PartIds;   // Parts: one array per part
        public UiSettingMeta M; public SettingWidget W;  // Number / Choice: resolved meta and widget
        public Func<double, string> Fmt, AutoFn;         // Number: value formatter; AutoSlider: formatter that reads AutoFlag
        public bool AutoFlag; public string AutoText;
        public double[] Grid; public List<UiItem> GridItems; public double Def;   // Number drop-down: the grid and its items
        public double OffCur = double.NaN; public double[] OffVals; public List<UiItem> OffItems;   // the same with the current value added (off the grid)

        public string AutoFormat(double x) { return AutoFlag ? AutoText : Fmt(x); }
    }

    /// <summary>T-239: everything of the body that depends only on (registry Revision, UiText.Revision, ShowAdvanced) and on (draft Revision).</summary>
    internal sealed class SettingsViewCache
    {
        internal sealed class Section
        {
            public string Id, Title, ResetId, Counter;
            public UiSettingEntry[] Rows;               // visible with ShowAdvanced
            public string[] Labels;                     // Label(e) per row (kit.LabelColumn input)
            public SettingsRow[] Cells;                 // the same rows with their cached texts / widgets
            public int Changed;                         // by draft Revision
        }

        public List<Section> Sections = new List<Section>();
        public IList<UiSettingEntry> KeyRows;           // HotkeyRows()
        public Dictionary<UiSettingEntry, int> KeyIndex = new Dictionary<UiSettingEntry, int>();   // O(1) position of a key row (was a linear search per row)
        public int RegRev = -1, TextRev = -1, DraftRev = -1; public bool Adv;
        internal int Rebuilds;
        public string AdvLabel, AdvTip, ResetText, ResetTip, RestartMark, ModMark, KeyOff, KeyFree, KeyAllTaken, ApplyNone;
        private string[] _counters = new string[0], _changedLines = new string[0];
        private int _linesRev = -1;
        private UiFooter _footer; private bool _fHasEdits, _fCan; private string _fReason; private int _fRev = -1;

        /// <summary>The footer description, rebuilt only when what it shows changes.</summary>
        public UiFooter FooterFor(bool hasEdits, bool canApply, string reason)
        {
            if (_footer != null && _fRev == UiText.Revision && _fHasEdits == hasEdits && _fCan == canApply && string.Equals(_fReason, reason, StringComparison.Ordinal)) return _footer;
            _footer = new UiFooter
            {
                Aux = new[] { new UiAction("revert", UiText.Get("settings.ui.revert"), UiText.Get("settings.ui.revert.tip"), hasEdits) },
                Primary = new UiAction("apply", UiText.Get("settings.ui.apply"), reason ?? UiText.Get("settings.ui.apply.tip"), canApply),
                Reason = reason,
            };
            _fRev = UiText.Revision; _fHasEdits = hasEdits; _fCan = canApply; _fReason = reason;
            return _footer;
        }

        /// <summary>Rebuilds what is stale; returns true if the layout was rebuilt (counted by Rebuilds for the stand).</summary>
        public bool Refresh(SettingsView v)
        {
            bool rebuilt = false;
            if (RegRev != v.Reg.Revision || TextRev != UiText.Revision || Adv != v.ShowAdvanced || KeyRows == null)
            {
                Rebuild(v);
                rebuilt = true; Rebuilds++;
            }
            if (rebuilt || DraftRev != v.Draft.Revision)
            {
                var d = v.Draft;
                foreach (var sec in Sections)
                {
                    int n = 0;
                    if (d.ChangedCount > 0) foreach (var e in sec.Rows) if (d.Changed(e)) n++;
                    sec.Changed = n;
                    sec.Counter = n > 0 ? Counter(n) : null;
                }
                DraftRev = v.Draft.Revision;
            }
            return rebuilt;
        }

        private string Counter(int n)
        {
            if (n >= _counters.Length) Array.Resize(ref _counters, n + 8);
            return _counters[n] ?? (_counters[n] = "\u25C2 " + n.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>"settings.ui.changed_n" cached by n and UiText.Revision.</summary>
        public string ChangedLine(int n)
        {
            if (_linesRev != UiText.Revision) { _changedLines = new string[0]; _linesRev = UiText.Revision; }
            if (n >= _changedLines.Length) Array.Resize(ref _changedLines, n + 8);
            return _changedLines[n] ?? (_changedLines[n] = UiText.Fmt("settings.ui.changed_n", n));
        }

        private void Rebuild(SettingsView v)
        {
            Sections = new List<Section>();
            KeyRows = v.Draft.HotkeyRows();
            KeyIndex.Clear();
            for (int i = 0; i < KeyRows.Count; i++) KeyIndex[KeyRows[i]] = i;
            AdvLabel = UiText.Get("settings.ui.advanced"); AdvTip = UiText.Get("settings.ui.advanced.tip");
            ResetText = UiText.Get("settings.ui.reset"); ResetTip = UiText.Get("settings.ui.reset.tip");
            RestartMark = "\u27F3 " + UiText.Get("settings.ui.restart_mark"); ModMark = "\u25C2 " + UiText.Get("settings.ui.modified_mark");
            KeyOff = UiText.Get("settings.ui.key_off"); KeyFree = "\u2713 " + UiText.Get("settings.ui.key_free"); KeyAllTaken = "! " + UiText.Get("settings.ui.key_all_taken");
            ApplyNone = UiText.Get("settings.ui.apply_none");
            foreach (string id in v.Reg.Sections())
            {
                var rows = SettingsWindowBody.Rows(v.Reg, id, v.ShowAdvanced);
                if (rows.Count == 0) continue;
                var sec = new Section { Id = id, Title = SettingsWindowBody.SectionTitle(id), ResetId = "reset." + id, Rows = new UiSettingEntry[rows.Count], Labels = new string[rows.Count], Cells = new SettingsRow[rows.Count] };
                for (int i = 0; i < rows.Count; i++)
                {
                    sec.Rows[i] = rows[i];
                    sec.Labels[i] = SettingsWindowBody.Label(rows[i]);
                    sec.Cells[i] = SettingsWindowBody.BuildRow(rows[i], id == UiSettingsRegistry.Hotkeys);
                }
                Sections.Add(sec);
            }
            RegRev = v.Reg.Revision; TextRev = UiText.Revision; Adv = v.ShowAdvanced;
        }
    }

    internal static class SettingsWindowBody
    {
        

        private static string T(string key) { return UiText.Get(key); }

        private static string Tx(string key, string fallback) { return UiText.Has(UiText.DefaultSection, key) ? UiText.Get(key) : fallback; }

        public static string Label(UiSettingEntry e) { return Tx(e.TextKey, e.Key); }

        public static string Hint(UiSettingEntry e) { return Tx(e.TextKey + ".hint", e.Description); }

        private static string OptLabel(UiSettingEntry e, string v)
        {
            string key = e.TextKey + ".opt." + v;
            if (UiText.Has(UiText.DefaultSection, key)) return UiText.Get(key);
            return OptFallback(e.TextKey, v);
        }

        /// <summary>T-183: the label of an option without its own text: a language name only for settings.ui.language, the stored value otherwise.</summary>
        internal static string OptFallback(string textKey, string v)
        {
            return textKey == "settings.ui.language" ? LangName(v) : v;
        }

        /// <summary>A language code (ru, en, pt-br) without its own text is shown by the language's own name; anything else as it is.</summary>
        private static string LangName(string code)
        {
            try
            {
                if (code.Length >= 2 && code.Length <= 5 && code == code.ToLowerInvariant() && char.IsLetter(code[0]))
                {
                    string n = new System.Globalization.CultureInfo(code).NativeName;
                    if (!string.IsNullOrEmpty(n) && n != code)
                    {
                        int p = n.IndexOf(" (", StringComparison.Ordinal);
                        n = p > 0 ? n.Substring(0, p) : n;
                        return char.ToUpperInvariant(n[0]) + n.Substring(1);
                    }
                }
            }
            catch (Exception) { }
            return code;
        }

        internal static string SectionTitle(string id) { return Tx("settings.section." + id, id); }

        /// <summary>Rows of a section as the window shows them: advanced ones only with the flag.</summary>
        public static IList<UiSettingEntry> Rows(UiSettingsRegistry reg, string sectionId, bool showAdvanced)
        {
            var rows = new List<UiSettingEntry>();
            foreach (var e in reg.InSection(sectionId))
                if (e.Class != UiKeyClass.Advanced || showAdvanced) rows.Add(e);
            return rows;
        }

        /// <summary>T-239: once per IMGUI pass before Draw / DrawFooter: drops the edits the mod caught up with and refreshes the layout cache.</summary>
        public static void BeginPass(SettingsView v)
        {
            v.Draft.Prune();
            v.Cache.Refresh(v);
        }

        /// <summary>T-164: the hint plus the note of a closed gate (T-239: the base text is cached, a string is built only while a gate is closed).</summary>
        private static string RowTip(SettingsView v, SettingsRow r)
        {
            string note = v.Reg.GateNote(r.E);
            return note == null ? r.BaseTip : r.BaseTip + "\n" + T(note);
        }

        private static string[] Repeat(string tip, int n)
        {
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = tip;
            return a;
        }

        /// <summary>The cached texts and widget parts of one row (rebuilt by SettingsViewCache.Refresh on a language / registry / advanced change).</summary>
        internal static SettingsRow BuildRow(UiSettingEntry e, bool keyRow)
        {
            var r = new SettingsRow { E = e, Label = Label(e), Id = (keyRow ? "k." : "f.") + e.FullKey, BaseTip = RowTip(e) };
            r.KeyLabel = r.Label;
            if (e.Meta != null && e.Meta.HoldKey) r.KeyLabel += " " + T("settings.ui.hold_mark");
            UiSettingMeta meta = e.Meta;
            if (e.Control == UiControl.Choice && meta != null && meta.Options != null && meta.Options.Length > 0)
            {
                r.Opts = new string[meta.Options.Length];
                for (int i = 0; i < r.Opts.Length; i++) r.Opts[i] = OptLabel(e, meta.Options[i]);
                r.BaseTips = Repeat(r.BaseTip, r.Opts.Length);
                r.M = SettingWidgetRule.Resolve(e);
                r.W = SettingWidgetRule.Pick(r.M, FitSegments);
            }
            else if (e.Control == UiControl.Multi && meta != null)
            {
                var opts = meta.Options ?? new string[0];
                r.Opts = new string[opts.Length];
                for (int i = 0; i < opts.Length; i++) r.Opts[i] = OptLabel(e, opts[i]);
                r.BaseTips = Repeat(r.BaseTip, opts.Length);
                if (meta.Required != null && meta.Required.Length > 0)
                {
                    var req = new List<string>();
                    foreach (var q in meta.Required) req.Add(OptLabel(e, q));
                    r.ReqText = string.Join(", ", req.ToArray());
                }
            }
            else if (e.Control == UiControl.Number)
            {
                r.M = SettingWidgetRule.Resolve(e);
                r.W = SettingWidgetRule.Pick(r.M, FitSegments);
                if (r.W != SettingWidget.Text)
                {
                    r.Fmt = NumFormat(r.M);
                    r.Def = Dbl(e.Box.Default);
                    if (r.W == SettingWidget.Dropdown)
                    {
                        int n = SettingWidgetRule.ValueCount(r.M);
                        r.Grid = new double[n];
                        r.GridItems = new List<UiItem>();
                        for (int i = 0; i < n; i++)
                        {
                            r.Grid[i] = Math.Round(r.M.Min + i * r.M.Step, 6);
                            r.GridItems.Add(UiItem.Of(r.Fmt(r.Grid[i]), null, null, null, Math.Abs(r.Grid[i] - r.Def) < 1e-6));
                        }
                    }
                    else if (r.W == SettingWidget.AutoSlider)
                    {
                        r.AutoText = UiText.Get("ui.auto");
                        r.AutoFn = r.AutoFormat;
                    }
                }
            }
            if (meta != null && meta.Parts != null && meta.Parts.Length > 0 && e.Control != UiControl.Number && e.Control != UiControl.Choice && e.Control != UiControl.Multi
                && e.Control != UiControl.Check && e.Control != UiControl.Hotkey)
            {
                int pc = meta.Parts.Length;
                r.PartLabels = new string[pc][]; r.PartTips = new string[pc][]; r.PartIds = new string[pc];
                for (int p = 0; p < pc; p++)
                {
                    r.PartLabels[p] = new string[meta.Parts[p].Length];
                    for (int i = 0; i < meta.Parts[p].Length; i++) r.PartLabels[p][i] = OptLabel(e, meta.Parts[p][i]);
                    r.PartTips[p] = Repeat(r.BaseTip, meta.Parts[p].Length);
                    r.PartIds[p] = r.Id + ".p" + p;
                }
            }
            return r;
        }

        private static string RowTip(UiSettingEntry e)
        {
            string def = e.Box.Format(e.Box.Default);
            if (e.Meta != null && e.Meta.Control == UiControl.Choice && e.Meta.Options != null) def = OptLabel(e, def);
            return Hint(e) + "\n" + UiText.Fmt("settings.ui.default_is", def);
        }

        /// <summary>Draws the body (the footer is DrawFooter, T-190). Returns the action pressed in this event: "reset.&lt;section&gt;" | "rec.&lt;Section.Key&gt;" | "adv" | null.
        /// The draft is edited here: controls, "x" of a key row, "adv" (ShowAdvanced is toggled by Draw), "revert" and "reset.*" are already done when Draw returns
        /// (the id is only reported). The caller still does "apply" (Draft.Apply(host), then Status) and "rec.*" (Rec.Start).</summary>
        public static string Draw(UiKit kit, SettingsView v)
        {
            string act = null;
            SettingsDraft d = v.Draft;
            var c = v.Cache;
            c.Refresh(v);

            bool adv = kit.Check("adv", v.ShowAdvanced, c.AdvLabel, c.AdvTip);
            if (adv != v.ShowAdvanced) { v.ShowAdvanced = adv; act = "adv"; }

            var keyRows = c.KeyRows;
            IList<HotkeyDraftResult> keys = v.Keys != null && v.Keys.Count == keyRows.Count ? v.Keys : d.ModKeyCheck();

            bool first = true;
            var secs = c.Sections;
            for (int si = 0; si < secs.Count; si++)
            {
                var sec = secs[si];
                bool open = kit.Section(sec.Id, sec.Title, sec.Counter, first);
                first = false;
                if (kit.SectionButton(sec.ResetId, c.ResetText, c.ResetTip))
                {
                    d.ResetSection(sec.Id, v.ShowAdvanced);
                    act = "reset." + sec.Id;
                }
                if (!open) continue;
                kit.LabelColumn(sec.Labels);
                bool keySec = sec.Id == UiSettingsRegistry.Hotkeys;
                for (int i = 0; i < sec.Cells.Length; i++)
                {
                    string a = keySec ? KeyRow(kit, v, sec.Cells[i], keyRows, keys) : ValueRow(kit, v, sec.Cells[i]);
                    if (a != null) act = a;
                }
            }

            return act;
        }

        /// <summary>T-190: the sticky footer (outside the scrolling body, ADR-0077 section 2.5): "Changed: N" / the last Apply result, the error row and
        /// [Revert changes] ... [Apply]. Returns "apply" | "apply.refused" | "revert" | null. "revert" is already done on the draft when it returns.</summary>
        public static string DrawFooter(UiKit kit, SettingsView v)
        {
            string act = null;
            SettingsDraft d = v.Draft;
            UiSettingsRegistry reg = v.Reg;
            var c = v.Cache;
            c.Refresh(v);
            var keyRows = c.KeyRows;
            IList<HotkeyDraftResult> keys = v.Keys != null && v.Keys.Count == keyRows.Count ? v.Keys : d.ModKeyCheck();
            kit.Separator();
            int n = d.ChangedCount;
            // One reserved row for "Changed: N" / the result of the last Apply: the buttons below must not jump when the first checkbox is ticked
            // (a click on Apply at the position seen before the tick would miss it, live check 0.8.7).
            if (n > 0) kit.Text(c.ChangedLine(n), UiTone.Accent);
            else if (!string.IsNullOrEmpty(v.Status)) kit.Text(v.Status, UiTone.Dim, true);
            else kit.Text("\u00A0", UiTone.Dim);
            string errTip = null;
            bool hasErr = d.HasErrors, canApply = n > 0 && !hasErr;
            if (hasErr)
            {
                UiSettingEntry bad = FirstError(reg, d, keyRows, keys);
                errTip = UiText.Fmt("settings.ui.error_in", bad == null ? "?" : Label(bad));
                kit.Text(errTip, UiTone.Error, true);
            }
            // T-178 S5: the footer of ADR-0077 ([Revert changes] ... [Apply]); a grey Apply says why under the row
            string reason = canApply ? null : (errTip ?? c.ApplyNone);
            string pressed = kit.Footer(c.FooterFor(n > 0, canApply, reason));
            if (pressed == "revert") { d.Revert(); act = "revert"; }
            else if (pressed == "apply") act = "apply";
            else if (kit.DisabledId == "apply") act = "apply.refused";   // a click on the grey Apply: the caller logs why
            return act;
        }

        /// <summary>T-190: the line for the window's hint row while the draft has unapplied edits (the footer may be scrolled out of sight in other layouts).</summary>
        public static IList<string> Warnings(SettingsView v)
        {
            int n = v.Draft.ChangedCount;
            return n > 0 ? new List<string> { UiText.Fmt("settings.ui.unapplied_n", n) } : null;
        }

        /// <summary>Why Apply is not possible now ("nothing changed", "error in Section.Key"), null = it is.</summary>
        public static string RefuseReason(SettingsView v)
        {
            SettingsDraft d = v.Draft;
            if (d.ChangedCount == 0) return "nothing changed";
            if (d.HasErrors)
            {
                var keyRows = d.HotkeyRows();
                UiSettingEntry bad = FirstError(v.Reg, d, keyRows, d.ModKeyCheck());
                return "error in " + (bad == null ? "?" : bad.FullKey);
            }
            return null;
        }

        /// <summary>Applies the draft (one Batch, one Save) and sets v.Status. Logs "settings: apply N keys (...)" or "settings: apply refused: reason".
        /// Returns the report or null when refused. Must be called from the body callback of the window (see SettingsWindowGui.OnGUI).</summary>
        public static UiApplyReport Apply(SettingsView v, IUiHost host, Action<string> log)
        {
            log = log ?? (m => { });
            string why = RefuseReason(v);
            if (why != null) { log("settings: apply refused: " + why); return null; }
            int n = v.Draft.ChangedCount;
            var rep = v.Draft.Apply(host);
            v.Status = UiText.Fmt("settings.ui.applied_report", rep.Live, rep.NextOrder, rep.Restart);
            log("settings: apply " + n + " keys (" + string.Join(",", rep.Keys.ToArray()) + ") live=" + rep.Live + " next_order=" + rep.NextOrder
                + " restart=" + rep.Restart + " failed=" + rep.Failed.Count + (rep.Failed.Count > 0 ? " [" + string.Join(",", rep.Failed.ToArray()) + "]" : ""));
            return rep;
        }

        private static UiSettingEntry FirstError(UiSettingsRegistry reg, SettingsDraft d, IList<UiSettingEntry> keyRows, IList<HotkeyDraftResult> keys)
        {
            foreach (var e in reg.All) if (d.Error(e) != null) return e;
            for (int i = 0; i < keyRows.Count && i < keys.Count; i++)
                if (keys[i].State == HotkeyDraftState.BadFormat || keys[i].State == HotkeyDraftState.ModConflict) return keyRows[i];
            return null;
        }

        // ---------------------------------------------------------------- rows

        private static void Marks(UiKit kit, SettingsView v, UiSettingEntry e)
        {
            var d = v.Draft;
            string err = d.Error(e);
            if (err != null) kit.Text("\u2717 " + ErrorText(e, err), UiTone.Error);
            if (d.Changed(e)) kit.Text(v.Cache.ModMark, UiTone.Accent);
            if (v.Reg.PendingRestart(e)) kit.Text(v.Cache.RestartMark, UiTone.Warn);
            else if (v.Reg.Effective(e) == UiApply.Restart) kit.Text(v.Cache.RestartMark, UiTone.Dim);
        }

        private static string ErrorText(UiSettingEntry e, string code)
        {
            if (code == "range" && e.Meta != null)
            {
                string lo = double.IsNaN(e.Meta.Min) ? "" : e.Box.Format(Cast(e, e.Meta.Min));
                string hi = double.IsNaN(e.Meta.Max) ? "" : e.Box.Format(Cast(e, e.Meta.Max));
                return lo + ".." + hi;
            }
            return code;
        }

        private static object Cast(UiSettingEntry e, double d)
        {
            if (e.Box.Type == typeof(int)) return (int)Math.Round(d);
            if (e.Box.Type == typeof(float)) return (float)d;
            return d;
        }

        private static string ValueRow(UiKit kit, SettingsView v, SettingsRow r)
        {
            SettingsDraft d = v.Draft;
            UiSettingEntry e = r.E;
            string id = r.Id;
            string tip = RowTip(v, r);
            kit.Row(r.Label, tip);
            if (!v.Reg.Available(e)) { kit.Text(T("settings.ui.unavailable"), UiTone.Dim); return null; }
            if (v.Reg.Gated(e)) { kit.Text(T(v.Reg.GateNote(e)), UiTone.Dim); return null; }
            switch (e.Control)
            {
                case UiControl.Check:
                {
                    object shown = d.Shown(e);
                    bool on = shown is bool && (bool)shown;
                    bool now = kit.Check(id, on, "", tip);
                    if (now != on) d.Edit(e, now);
                    break;
                }
                case UiControl.Choice:
                {
                    if (r.Opts == null) goto default;
                    ChoiceRow(kit, v, r, id, tip);
                    break;
                }
                case UiControl.Multi:
                {
                    UiSettingMeta m = e.Meta;
                    string cur = d.Shown(e) as string ?? "";
                    var on = new List<string>(cur.Split(','));
                    for (int i = 0; i < on.Count; i++) on[i] = on[i].Trim();
                    if (r.ReqText != null)
                    {
                        kit.Text(r.ReqText, UiTone.Dim);
                        kit.Wrap();
                    }
                    var opts = m.Options ?? new string[0];
                    var tones = new List<UiTone>();
                    foreach (var o in opts) tones.Add(on.Contains(o) ? UiTone.Accent : UiTone.Normal);
                    int hit = kit.Chips(id, r.Opts, -1, tones, ReferenceEquals(tip, r.BaseTip) ? r.BaseTips : Repeat(tip, opts.Length));
                    if (hit >= 0)
                    {
                        string o = m.Options[hit];
                        if (on.Contains(o)) on.Remove(o); else on.Add(o);
                        List<string> unknown;
                        d.Edit(e, UiSettingsRegistry.NormMulti(m, string.Join(",", on.ToArray()), out unknown));
                    }
                    break;
                }
                case UiControl.Number:
                {
                    if (!NumberRow(kit, v, r, id, tip)) goto default;
                    break;
                }
                default:
                {
                    if (r.PartLabels != null) { PartsRow(kit, v, r, tip); break; }
                    string text = d.TextOf(e);
                    string nt = kit.Number(id, text, 14);   // a Text without Parts is the one field left
                    if (nt != text) d.SetText(e, nt);
                    break;
                }
            }
            Marks(kit, v, e);
            return null;
        }

        // ---------------------------------------------------------------- T-178 S3/S4: the widget of a row (SettingWidgetRule)

        private const int FitSegments = 5;

        private static double Dbl(object o) { try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch (Exception) { return 0; } }

        private static string Plain(double x, int dec) { return Math.Round(x, dec).ToString(dec > 0 ? "0." + new string('#', dec) : "0", CultureInfo.InvariantCulture); }

        /// <summary>The value as the player reads it: stored x DisplayFactor, the decimals of the step, the unit.</summary>
        private static Func<double, string> NumFormat(UiSettingMeta m)
        {
            double f = m.DisplayFactor == 0 ? 1 : m.DisplayFactor;
            double st = m.Step * f;
            int dec = st >= 1 - 1e-9 ? 0 : (int)Math.Min(3, Math.Ceiling(-Math.Log10(st) - 1e-9));
            string unit = string.IsNullOrEmpty(m.UnitKey) ? null : T("settings.unit." + m.UnitKey);
            var memo = new Dictionary<double, string>();   // T-239: a slider asks for the same few values every pass
            return x =>
            {
                string r;
                if (memo.TryGetValue(x, out r)) return r;
                if (memo.Count >= 256) memo.Clear();
                r = Plain(x * f, dec) + (unit == null ? "" : " " + unit);
                memo[x] = r;
                return r;
            };
        }

        private static object CastTo(UiSettingEntry e, double x)
        {
            if (e.Box.Type == typeof(int)) return (int)Math.Round(x);
            if (e.Box.Type == typeof(float)) return (float)x;
            return x;
        }

        /// <summary>A number as a drop-down (up to 12 values), a slider or a slider with "Auto" (0 = auto). false = the control is a text (no usable range).</summary>
        private static bool NumberRow(UiKit kit, SettingsView v, SettingsRow r, string id, string tip)
        {
            SettingsDraft d = v.Draft;
            UiSettingEntry e = r.E;
            var m = r.M;
            var w = r.W;
            if (w == SettingWidget.Text) return false;
            double cur = Dbl(d.Shown(e)), def = r.Def;
            var fmt = r.Fmt;
            if (w == SettingWidget.Dropdown)
            {
                double[] vals = r.Grid; List<UiItem> items = r.GridItems;
                int sel = IndexOf(vals, cur);
                if (sel < 0)
                {
                    if (r.OffVals == null || r.OffCur != cur)
                    {
                        var l = new List<double>(r.Grid); l.Add(cur); l.Sort();
                        r.OffVals = l.ToArray(); r.OffCur = cur;
                        r.OffItems = new List<UiItem>();
                        foreach (double x in r.OffVals) r.OffItems.Add(UiItem.Of(fmt(x), null, null, null, Math.Abs(x - def) < 1e-6));
                    }
                    vals = r.OffVals; items = r.OffItems;
                    sel = IndexOf(vals, cur);
                }
                int res = kit.Dropdown(id, items, sel, tip, vals.Length > 8 ? 2 : 1);
                if (res != sel && res >= 0 && res < vals.Length) d.Edit(e, CastTo(e, vals[res]));
                return true;
            }
            if (w == SettingWidget.Slider)
            {
                double nv = kit.Slider(id, m.Min, m.Max, m.Step, cur, fmt, tip, def);
                if (Math.Abs(nv - UiKit.SnapValue(cur, m.Min, m.Max, 0)) > 1e-9) d.Edit(e, CastTo(e, nv));
                return true;
            }
            // AutoSlider: 0 = auto; the manual range starts above 0
            double mmin = m.Min > 0 ? m.Min : m.Step;
            bool auto = cur <= 0, wasAuto = auto;
            double fallback = def > 0 ? def : (1.0 >= mmin && 1.0 <= m.Max ? 1.0 : UiKit.SnapValue(m.Max / 4, mmin, m.Max, m.Step));
            double manual = cur > 0 ? cur : fallback;
            r.AutoFlag = auto;
            double nv2 = kit.AutoSlider(id, ref r.AutoFlag, 0, mmin, m.Max, m.Step, manual, r.AutoFn, tip, def > 0 ? (double?)def : null);
            auto = r.AutoFlag;
            if (auto != wasAuto) d.Edit(e, CastTo(e, auto ? 0 : nv2));
            else if (!auto && Math.Abs(nv2 - UiKit.SnapValue(manual, mmin, m.Max, 0)) > 1e-9) d.Edit(e, CastTo(e, nv2));
            return true;
        }

        private static int IndexOf(double[] vals, double cur)
        {
            for (int i = 0; i < vals.Length; i++) if (Math.Abs(vals[i] - cur) < 1e-6) return i;
            return -1;
        }

        private static void ChoiceRow(UiKit kit, SettingsView v, SettingsRow r, string id, string tip)
        {
            SettingsDraft d = v.Draft;
            UiSettingEntry e = r.E;
            string[] opts = e.Meta.Options;
            int idx = -1, dflt = -1;
            string cur = d.Shown(e) as string ?? "", def = e.Box.Default as string ?? "";
            for (int i = 0; i < opts.Length; i++)
            {
                if (string.Equals(opts[i], cur, StringComparison.OrdinalIgnoreCase)) idx = i;
                if (string.Equals(opts[i], def, StringComparison.OrdinalIgnoreCase)) dflt = i;
            }
            string[] tips = ReferenceEquals(tip, r.BaseTip) ? r.BaseTips : Repeat(tip, opts.Length);
            int ni = r.W == SettingWidget.Dropdown
                ? kit.Dropdown(id, r.Opts, idx, tip, 1, null, null, dflt)
                : kit.Segments(id, r.Opts, idx, tips, null, dflt);
            if (ni != idx && ni >= 0 && ni < opts.Length) d.Edit(e, opts[ni]);
        }

        /// <summary>A text "a,b,c" whose parts are choices of their own (Label units: distance, speed, height): a row of Segments per part; the cfg keeps the same string.</summary>
        private static void PartsRow(UiKit kit, SettingsView v, SettingsRow r, string tip)
        {
            SettingsDraft d = v.Draft;
            UiSettingEntry e = r.E;
            var parts = e.Meta.Parts;
            string cur = d.Shown(e) as string ?? "", def = e.Box.Default as string ?? "";
            var have = new List<string>(cur.Split(','));
            var dfl = new List<string>(def.Split(','));
            for (int i = 0; i < have.Count; i++) have[i] = have[i].Trim();
            while (have.Count < parts.Length) have.Add(dfl.Count > have.Count ? dfl[have.Count].Trim() : parts[have.Count][0]);
            bool changed = false;
            bool basic = ReferenceEquals(tip, r.BaseTip);
            for (int p = 0; p < parts.Length; p++)
            {
                int idx = -1, dflt = -1;
                for (int i = 0; i < parts[p].Length; i++)
                {
                    if (string.Equals(parts[p][i], have[p], StringComparison.OrdinalIgnoreCase)) idx = i;
                    if (p < dfl.Count && string.Equals(parts[p][i], dfl[p].Trim(), StringComparison.OrdinalIgnoreCase)) dflt = i;
                }
                if (p > 0) kit.Wrap();
                int ni = kit.Segments(r.PartIds[p], r.PartLabels[p], idx, basic ? r.PartTips[p] : Repeat(tip, parts[p].Length), null, dflt);
                if (ni != idx && ni >= 0) { have[p] = parts[p][ni]; changed = true; }
            }
            if (changed) d.Edit(e, string.Join(",", have.GetRange(0, parts.Length).ToArray()));
        }

        private static string KeyRow(UiKit kit, SettingsView v, SettingsRow r, IList<UiSettingEntry> keyRows, IList<HotkeyDraftResult> keys)
        {
            SettingsDraft d = v.Draft;
            UiSettingEntry e = r.E;
            kit.Row(r.KeyLabel, RowTip(v, r));
            if (v.Reg.Gated(e)) { kit.Text(T(v.Reg.GateNote(e)), UiTone.Dim); return null; }
            bool recording = v.Rec != null && v.Rec.Active && v.Rec.RowId == e.FullKey;
            string status; UiTone tone;
            if (recording) { status = T("settings.ui.key_record_hint"); tone = UiTone.Accent; }
            else StatusOf(v, e, keyRows, keys, out status, out tone);
            string spec = d.TextOf(e);
            bool rec, clr;
            string nt = kit.Hotkey(r.Id, spec, status, tone, recording, out rec, out clr);
            string act = null;
            if (clr) d.SetText(e, "");
            else if (nt != spec) d.SetText(e, nt);
            if (rec) act = "rec." + e.FullKey;
            Marks(kit, v, e);
            return act;
        }

        private static void StatusOf(SettingsView v, UiSettingEntry e, IList<UiSettingEntry> keyRows, IList<HotkeyDraftResult> keys, out string status, out UiTone tone)
        {
            status = null; tone = UiTone.Dim;
            int i;
            if (!v.Cache.KeyIndex.TryGetValue(e, out i) || i >= keys.Count) return;
            var c = v.Cache;
            HotkeyDraftResult r = keys[i];
            switch (r.State)
            {
                case HotkeyDraftState.Off: status = c.KeyOff; break;
                case HotkeyDraftState.Ok: status = c.KeyFree; tone = UiTone.Ok; break;
                case HotkeyDraftState.GameNext: status = "! " + UiText.Fmt("settings.ui.key_game_next", r.Picked); tone = UiTone.Warn; break;
                case HotkeyDraftState.AllTaken: status = c.KeyAllTaken; tone = UiTone.Warn; break;
                case HotkeyDraftState.BadFormat: status = "\u2717 " + UiText.Fmt("settings.ui.key_unknown", r.Error); tone = UiTone.Error; break;
                case HotkeyDraftState.ModConflict:
                {
                    string other = r.ConflictWith;
                    foreach (var k in keyRows) if (k.FullKey == r.ConflictWith) other = Label(k);
                    status = "\u2717 " + UiText.Fmt("settings.ui.key_taken_mod", other); tone = UiTone.Error;
                    break;
                }
            }
        }
    }
}
