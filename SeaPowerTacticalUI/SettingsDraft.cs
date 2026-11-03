// T-144 (ADR-0050 sections 4, 6, 7): the draft of the settings window. Pure C#: no Unity, no BepInEx.
// Every edit lives here until "Apply"; the registry (and so the config file) is touched only by Apply.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    internal sealed class SettingsDraft
    {
        private readonly UiSettingsRegistry _reg;
        private readonly Dictionary<string, object> _edits = new Dictionary<string, object>();   // FullKey -> value (type of the setting)
        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>();  // FullKey -> "format" | "range" | "option"
        private readonly Dictionary<string, string> _typed = new Dictionary<string, string>();   // FullKey -> text of a field that failed to parse

        public SettingsDraft(UiSettingsRegistry reg) { _reg = reg; }

        /// <summary>T-239: +1 on every change of _edits / _errors / _typed (Edit, SetText, Revert, ResetSection, Apply, a Prune that removed something).</summary>
        public int Revision { get; private set; }

        /// <summary>T-239: test counter, entries visited by Prune (stand only).</summary>
        internal static int PruneScans;

        /// <summary>Edit if any, else the current value: a value written by the mod itself flows into rows that are not edited.</summary>
        public object Shown(UiSettingEntry e)
        {
            object v;
            return _edits.TryGetValue(e.FullKey, out v) ? v : e.Box.Get();
        }

        /// <summary>Text of the row's field: what the player typed (while it is wrong or an edit), else the formatted shown value.</summary>
        public string TextOf(UiSettingEntry e)
        {
            string t;
            bool live = _errors.ContainsKey(e.FullKey) || _edits.ContainsKey(e.FullKey);
            return live && _typed.TryGetValue(e.FullKey, out t) ? t : e.Box.Format(Shown(e));
        }

        /// <summary>A value equal to the current one removes the edit.</summary>
        public void Edit(UiSettingEntry e, object v)
        {
            Revision++;
            _errors.Remove(e.FullKey); _typed.Remove(e.FullKey);
            if (Equals(v, e.Box.Get())) _edits.Remove(e.FullKey);
            else _edits[e.FullKey] = v;
        }

        /// <summary>Parses the field text; on a mistake the row keeps the text and an error (the draft value is left as it was).</summary>
        public bool SetText(UiSettingEntry e, string text)
        {
            object v; string err;
            Revision++;
            _typed[e.FullKey] = text ?? "";   // the field keeps what was typed ("2." must not turn into "2" under the cursor)
            if (e.Box.TryParse(text, out v, out err))
            {
                _errors.Remove(e.FullKey);
                if (Equals(v, e.Box.Get())) _edits.Remove(e.FullKey); else _edits[e.FullKey] = v;
                return true;
            }
            _errors[e.FullKey] = err ?? "format";
            return false;
        }

        public string Error(UiSettingEntry e)
        {
            string r;
            return _errors.TryGetValue(e.FullKey, out r) ? r : null;
        }

        private readonly List<string> _gone = new List<string>();

        /// <summary>An edit equal to the current value (the mod wrote the same) is dropped here. O(edits) via the registry's key table; no allocation when nothing is dropped.
        /// T-239: called explicitly once per pass by the window (SettingsWindowBody.BeginPass), not from Changed / ChangedCount.</summary>
        public void Prune()
        {
            if (_edits.Count == 0) return;
            _gone.Clear();
            foreach (var kv in _edits)
            {
                PruneScans++;
                var e = _reg.Find(kv.Key);
                if (e != null && Equals(kv.Value, e.Box.Get())) _gone.Add(kv.Key);
            }
            if (_gone.Count == 0) return;
            foreach (var k in _gone) _edits.Remove(k);
            _gone.Clear();
            Revision++;
        }

        public bool Changed(UiSettingEntry e) { return _edits.Count != 0 && _edits.ContainsKey(e.FullKey); }

        public int ChangedCount { get { return _edits.Count; } }
        /// <summary>T-190: the full keys of the unapplied edits (log line when the window is closed with a draft).</summary>
        public List<string> ChangedKeys() { return new List<string>(_edits.Keys); }

        private IList<HotkeyDraftResult> _modCheck;
        private int _modCheckRev = -1;

        /// <summary>T-239: the mod-only key check (CheckHotkeys(null,false)) cached by Revision; InvalidateChecks() drops it.</summary>
        public IList<HotkeyDraftResult> ModKeyCheck()
        {
            if (_modCheck == null || _modCheckRev != Revision) { _modCheck = CheckHotkeys(null, false); _modCheckRev = Revision; }
            return _modCheck;
        }

        public void InvalidateChecks() { _modCheck = null; }

        /// <summary>Parse errors and key-format / mod-conflict errors. A conflict with the game does not block.</summary>
        public bool HasErrors
        {
            get
            {
                if (_errors.Count > 0) return true;
                var res = ModKeyCheck();
                for (int i = 0; i < res.Count; i++)
                    if (res[i].State == HotkeyDraftState.BadFormat || res[i].State == HotkeyDraftState.ModConflict) return true;
                return false;
            }
        }

        public bool CanApply { get { return ChangedCount > 0 && !HasErrors; } }

        /// <summary>"Revert changes".</summary>
        public void Revert() { Revision++; _edits.Clear(); _errors.Clear(); _typed.Clear(); }

        /// <summary>"Reset section": the rows of the section that the window shows go to their Default. Advanced rows only with the flag.</summary>
        public void ResetSection(string sectionId, bool showAdvanced)
        {
            foreach (var e in _reg.InSection(sectionId))
            {
                if (e.Class == UiKeyClass.Advanced && !showAdvanced) continue;
                Edit(e, e.Box.Default);
            }
        }

        /// <summary>The key rows (every Hotkey entry, registry order) of the section "hotkeys".</summary>
        public IList<UiSettingEntry> HotkeyRows() { return _reg.InSection(UiSettingsRegistry.Hotkeys); }

        /// <summary>Dry run over the shown specs of every key row, in the order of HotkeyRows().</summary>
        public IList<HotkeyDraftResult> CheckHotkeys(IHotkeyGame game, bool inMission)
        {
            var rows = new List<HotkeyDraftRow>();
            foreach (var e in HotkeyRows()) rows.Add(new HotkeyDraftRow { Id = e.FullKey, Spec = Shown(e) as string ?? "" });
            return HotkeyBinding.DryRun(rows, game, inMission);
        }

        /// <summary>One Batch, one Save (ADR-0050 section 3). The applied edits are cleared.</summary>
        public UiApplyReport Apply(IUiHost host)
        {
            Prune();
            var rep = _reg.Apply(host, new Dictionary<string, object>(_edits));
            Revision++;
            _edits.Clear(); _errors.Clear(); _typed.Clear();
            return rep;
        }

        // ------------------------------------------------------------------ self-test

        private static UiSettingEntry Find(UiSettingsRegistry r, string full) { return r.Find(full); }

        private sealed class FakeGame : IHotkeyGame
        {
            public readonly Dictionary<string, string> Verdict = new Dictionary<string, string>();
            public string Check(string[] keys, out string conflict)
            {
                string k = string.Join("+", keys), v;
                conflict = "Game Action";
                return Verdict.TryGetValue(k, out v) ? v : "free";
            }
        }

        private static void Mk<T>(UiSettingsRegistry r, UiContext.TestHost h, string owner, string sec, string key, UiKeyClass cls, UiApply apply, UiControl ctl, T def, UiSettingMeta m)
        {
            r.CurrentOwner = owner;
            m.Control = ctl; m.Apply = apply; m.Section = owner;
            var s = r.Track(sec, key, "d", cls, m, h.Bind(sec, key, def, "d", cls, m));
            if (apply != UiApply.Restart) r.OnChanged(owner, s, v => { });
        }

        // the same shapes as the real keys, without the stand fixture (this file also builds into the mod)
        private static UiSettingsRegistry MiniRegistry(UiContext.TestHost h)
        {
            var r = new UiSettingsRegistry(h.LogInfo, h.LogWarning);
            Mk(r, h, "overview", "Overview", "ShowOnStart", UiKeyClass.Basic, UiApply.Live, UiControl.Check, false, new UiSettingMeta());
            Mk(r, h, "overview", "Overview", "Hotkey", UiKeyClass.Basic, UiApply.Live, UiControl.Hotkey, "LeftAlt+O", new UiSettingMeta());
            Mk(r, h, "asw", "Asw", "Hotkey", UiKeyClass.Basic, UiApply.Live, UiControl.Hotkey, "LeftAlt+U|F7", new UiSettingMeta());
            Mk(r, h, "asw", "Asw", "OwnSubSafeNm", UiKeyClass.Basic, UiApply.Live, UiControl.Number, 4.0, new UiSettingMeta { Min = 1, Max = 10 });
            Mk(r, h, "cap", "Cap", "Enabled", UiKeyClass.Basic, UiApply.Restart, UiControl.Check, true, new UiSettingMeta());
            Mk(r, h, "cap", "Cap", "Hotkey", UiKeyClass.Basic, UiApply.Live, UiControl.Hotkey, "LeftAlt+P|LeftAlt+K|F8", new UiSettingMeta());
            Mk(r, h, "cap", "Cap", "MissilePct", UiKeyClass.Basic, UiApply.NextOrder, UiControl.Number, 0.25, new UiSettingMeta { Min = 0, Max = 1, DisplayFactor = 100 });
            Mk(r, h, "cap", "Cap", "Roe", UiKeyClass.Basic, UiApply.NextOrder, UiControl.Choice, "tight", new UiSettingMeta { Options = new[] { "tight", "free", "hold" } });
            Mk(r, h, "cap", "Cap", "Debug", UiKeyClass.Advanced, UiApply.Live, UiControl.Auto, false, new UiSettingMeta());
            return r;
        }

        public static string SelfTest()
        {
            try
            {
                var host = new UiContext.TestHost();
                var reg = MiniRegistry(host);
                var d = new SettingsDraft(reg);
                var pct = Find(reg, "Cap.MissilePct"); var roe = Find(reg, "Cap.Roe"); var chk = Find(reg, "Overview.ShowOnStart");
                var capKey = Find(reg, "Cap.Hotkey"); var ovKey = Find(reg, "Overview.Hotkey"); var aswKey = Find(reg, "Asw.Hotkey");
                if (pct == null || roe == null || chk == null || capKey == null || ovKey == null || aswKey == null) return "settings draft: fixture keys missing";

                // edit -> Changed; equal to the current value takes it back
                if (d.ChangedCount != 0 || d.CanApply) return "settings draft: starts clean";
                d.Edit(chk, true);
                if (!d.Changed(chk) || d.ChangedCount != 1 || !d.CanApply) return "settings draft: an edit is Changed";
                if (!Equals(d.Shown(chk), true) || !Equals(chk.Box.Get(), false)) return "settings draft: Shown is the edit, the setting is untouched";
                d.Edit(chk, false);
                if (d.Changed(chk) || d.ChangedCount != 0 || d.CanApply) return "settings draft: the current value removes the edit";

                // T-239: Prune is explicit and O(edits); HasErrors does not repeat the dry run without a Revision change
                {
                    var chk0 = chk.Box.Get();
                    d.Edit(chk, !(bool)chk0);
                    int scans = SettingsDraft.PruneScans;
                    d.Prune(); d.Prune(); d.Prune();
                    if (SettingsDraft.PruneScans - scans != 3 || d.ChangedCount != 1) return "settings draft: Prune visits the edits only (" + (SettingsDraft.PruneScans - scans) + ")";
                    chk.Box.Set(!(bool)chk0);   // the mod writes the same value: the edit is dropped by Prune, not by Changed
                    int rev = d.Revision;
                    if (!d.Changed(chk) || d.ChangedCount != 1) return "settings draft: Changed does not prune";
                    d.Prune();
                    if (d.Changed(chk) || d.ChangedCount != 0 || d.Revision == rev) return "settings draft: Prune drops the edit and bumps Revision";
                    chk.Box.Set(chk0);
                    int calls = HotkeyBinding.DryRunCalls;
                    bool he = d.HasErrors; he |= d.HasErrors; he |= d.CanApply;
                    int used = HotkeyBinding.DryRunCalls - calls;
                    if (used > 1) return "settings draft: HasErrors repeats the dry run " + used;
                    calls = HotkeyBinding.DryRunCalls;
                    d.Edit(capKey, "F5");
                    he |= d.HasErrors; he |= d.HasErrors;
                    if (HotkeyBinding.DryRunCalls - calls != 1) return "settings draft: a Revision change re-runs the dry run once (" + (HotkeyBinding.DryRunCalls - calls) + ")";
                    d.InvalidateChecks();
                    he |= d.HasErrors;
                    if (HotkeyBinding.DryRunCalls - calls != 2) return "settings draft: InvalidateChecks re-runs the dry run";
                    d.Revert();
                }

                // text: a wrong value blocks Apply, the row keeps the typed text; DisplayFactor 100
                if (d.TextOf(pct) != "25") return "settings draft: factor text '" + d.TextOf(pct) + "'";
                d.Edit(chk, true);
                if (d.SetText(pct, "250") || d.Error(pct) != "range" || !d.HasErrors || d.CanApply || d.TextOf(pct) != "250") return "settings draft: range error blocks";
                if (d.SetText(pct, "abc") || d.Error(pct) != "format") return "settings draft: format error";
                if (!d.SetText(pct, "40") || d.Error(pct) != null || d.HasErrors || !d.Changed(pct) || !Equals(d.Shown(pct), 0.4)) return "settings draft: good text " + d.Shown(pct);
                d.Revert();
                if (d.ChangedCount != 0 || d.HasErrors || d.TextOf(pct) != "25") return "settings draft: Revert";

                // key rows: bad format and a mod conflict block, a game conflict does not
                d.Edit(capKey, "Bad+Key+");
                if (!d.HasErrors || d.CanApply) return "settings draft: BadFormat blocks";
                d.Edit(capKey, ovKey.Box.Get());
                var res = d.CheckHotkeys(null, false);
                var rows = d.HotkeyRows();
                int ci = rows.IndexOf(capKey), oi = rows.IndexOf(ovKey);
                if (res[ci].State != HotkeyDraftState.ModConflict || res[oi].State != HotkeyDraftState.ModConflict || !d.HasErrors || d.CanApply) return "settings draft: ModConflict blocks both";
                var gm = new FakeGame();
                gm.Verdict["LeftAlt+O"] = "taken";
                d.Edit(capKey, "LeftAlt+P|F8");
                gm.Verdict["LeftAlt+P"] = "taken";
                res = d.CheckHotkeys(gm, true);
                if (res[ci].State != HotkeyDraftState.GameNext || d.HasErrors || !d.CanApply) return "settings draft: GameNext does not block";
                gm.Verdict["F8"] = "taken";
                res = d.CheckHotkeys(gm, true);
                if (res[ci].State != HotkeyDraftState.AllTaken || d.HasErrors || !d.CanApply) return "settings draft: AllTaken does not block";
                d.Revert();

                // ResetSection: basic rows always, advanced with the flag, hotkeys = the key rows
                host.Calls.Clear();
                using (host.Batch())
                {
                    roe.Box.Set("hold"); pct.Box.Set(0.5);
                    var adv = Find(reg, "Cap.Debug"); adv.Box.Set(true);
                    capKey.Box.Set("F1");
                }
                d.ResetSection("cap", false);
                if (!Equals(d.Shown(roe), "tight") || !Equals(d.Shown(pct), 0.25) || d.Changed(Find(reg, "Cap.Debug"))) return "settings draft: ResetSection basic only";
                if (d.Changed(capKey)) return "settings draft: ResetSection leaves key rows to the key section";
                d.ResetSection("cap", true);
                if (!d.Changed(Find(reg, "Cap.Debug"))) return "settings draft: ResetSection with advanced";
                d.ResetSection(UiSettingsRegistry.Hotkeys, false);
                if (!d.Changed(capKey) || !Equals(d.Shown(capKey), capKey.Box.Default)) return "settings draft: ResetSection hotkeys";
                d.Revert();

                // a value written by the mod flows in; an edited row is not touched; the same value drops the edit
                d.Edit(roe, "free");
                chk.Box.Set(true);
                pct.Box.Set(0.3);
                if (!Equals(d.Shown(chk), true) || d.Changed(chk)) return "settings draft: a mod write shows in an untouched row";
                if (!Equals(d.Shown(roe), "free") || !d.Changed(roe)) return "settings draft: an edited row is kept";
                d.Edit(pct, 0.3);
                if (d.Changed(pct)) return "settings draft: edit equal to the mod write is gone";
                roe.Box.Set("free");
                d.Prune();   // T-239: the window prunes once per pass (BeginPass)
                if (d.Changed(roe)) return "settings draft: the mod wrote the same value, the edit is dropped";
                chk.Box.Set(false); pct.Box.Set(0.25); roe.Box.Set("tight");

                // Apply: one Save, the report, edits cleared; the draft object lives on ("close / open the window")
                var restart = Find(reg, "Cap.Enabled"); var live = Find(reg, "Asw.OwnSubSafeNm"); var next = Find(reg, "Cap.Roe");
                d.Edit(restart, false); d.Edit(live, 7.0); d.Edit(next, "free");
                if (d.ChangedCount != 3) return "settings draft: three edits";
                int saves = host.Saves;
                var rep = d.Apply(host);
                if (host.Saves != saves + 1) return "settings draft: Apply saves once (" + (host.Saves - saves) + ")";
                if (rep.Restart != 1 || rep.Live != 1 || rep.NextOrder != 1 || rep.Keys.Count != 3) return "settings draft: report " + rep.Live + "/" + rep.NextOrder + "/" + rep.Restart;
                if (d.ChangedCount != 0 || !Equals(restart.Box.Get(), false) || !Equals(live.Box.Get(), 7.0)) return "settings draft: Apply writes and clears";
                if (!reg.PendingRestart(restart) || reg.PendingRestart(live)) return "settings draft: restart mark after Apply";
                saves = host.Saves;
                d.Apply(host);
                if (host.Saves != saves + 1 && host.Saves != saves) return "settings draft: empty Apply";

                string wr = SettingWidgetRule.SelfTest();
                if (wr != null) return wr;
                // HotkeyRecorder
                string r = HotkeyRecorder.SelfTest();
                if (r != null) return r;
                return null;
            }
            catch (Exception ex) { return "settings draft: " + ex.GetType().Name + ": " + ex.Message; }
        }
    }

    internal enum SettingWidget { Check, Segments, Dropdown, Slider, AutoSlider, Parts, Hotkey, Text, Multi }

    /// <summary>T-178 S1 (ADR-0077 section 1): which control a settings row gets. A number with a range is a drop-down (up to 12 values) or a slider ("0 = auto" gets the Auto flag),
    /// a choice of up to `fitSegments` options is Segments (UiKit.Segments itself falls back to a drop-down when they do not fit the row), a text with Parts is a row of Segments per part.
    /// A text field remains only for a Text without Parts. A key without a range in its meta gets one derived from its default (Resolve).</summary>
    internal static class SettingWidgetRule
    {
        public const int DropdownMaxValues = 12;

        public static bool HasRange(UiSettingMeta m)
        {
            return m != null && !double.IsNaN(m.Min) && !double.IsNaN(m.Max) && m.Max > m.Min && m.Step > 0;
        }

        /// <summary>Number of values of a range, both ends included.</summary>
        public static int ValueCount(UiSettingMeta m) { return HasRange(m) ? (int)Math.Floor((m.Max - m.Min) / m.Step + 1e-6) + 1 : 0; }

        public static SettingWidget Pick(UiSettingMeta m, int fitSegments)
        {
            if (m == null) return SettingWidget.Text;
            switch (m.Control)
            {
                case UiControl.Check: return SettingWidget.Check;
                case UiControl.Hotkey: return SettingWidget.Hotkey;
                case UiControl.Multi: return SettingWidget.Multi;
                case UiControl.Choice:
                    if (m.Options == null || m.Options.Length == 0) return SettingWidget.Text;
                    return m.Options.Length <= fitSegments ? SettingWidget.Segments : SettingWidget.Dropdown;
                case UiControl.Number:
                    if (!HasRange(m)) return SettingWidget.Text;
                    if (m.ZeroIsAuto) return SettingWidget.AutoSlider;
                    return ValueCount(m) <= DropdownMaxValues ? SettingWidget.Dropdown : SettingWidget.Slider;
                default:
                    return m.Parts != null && m.Parts.Length > 0 ? SettingWidget.Parts : SettingWidget.Text;
            }
        }

        private static double NiceUp(double x)
        {
            if (!(x > 0)) return 1;
            double e = Math.Pow(10, Math.Floor(Math.Log10(x))), m = x / e;
            foreach (double c in new[] { 1, 1.5, 2, 3, 4, 5, 6, 8, 10 }) if (m <= c + 1e-9) return c * e;
            return 10 * e;
        }

        /// <summary>The coarsest step (5, 2, 1 x 10^k) that gives 10..200 values and lands on `def` (the default must be a value of the grid); integers step at least 1.</summary>
        public static double PickStep(double min, double max, double def, bool isInt)
        {
            double range = max - min;
            if (!(range > 0)) return 1;
            int k0 = (int)Math.Floor(Math.Log10(range));
            double fallback = 0;
            for (int k = k0; k >= k0 - 5; k--)
                foreach (double mul in new[] { 5.0, 2.0, 1.0 })
                {
                    double s = mul * Math.Pow(10, k);
                    if (isInt && s < 1 - 1e-9) continue;
                    double n = range / s;
                    if (n > 200 + 1e-9 || n < 10 - 1e-9) continue;
                    if (fallback == 0) fallback = s;
                    double q = (def - min) / s;
                    if (Math.Abs(q - Math.Round(q)) < 1e-6) return s;
                }
            if (fallback > 0) return fallback;
            return isInt ? 1 : range / 100.0;
        }

        /// <summary>The range of a number the window can offer: the meta's own Min/Max/Step; what is missing is derived from the default (0..4 x default); a step that does not hit the default is refined.</summary>
        public static void RangeOf(UiSettingMeta m, double def, bool isInt, out double min, out double max, out double step)
        {
            bool haveMin = m != null && !double.IsNaN(m.Min), haveMax = m != null && !double.IsNaN(m.Max);
            min = haveMin ? m.Min : (def < 0 ? def * 4 : 0);
            if (haveMax) max = m.Max;
            else max = def < 0 ? NiceUp(-def * 4) : min + NiceUp(def == 0 ? 1 : def * 4);
            if (!(max > min)) max = min + 1;
            double st = m != null && m.Step > 0 ? m.Step : 1;
            bool explicitStep = haveMin && haveMax && m != null && m.Step > 0 && m.Step != 1;
            double q = (def - min) / st;
            bool onGrid = Math.Abs(q - Math.Round(q)) < 1e-6 || def < min || def > max;
            if (!explicitStep && (!haveMin || !haveMax || !onGrid || (max - min) / st > 200)) st = PickStep(min, max, def, isInt);
            if (isInt && st < 1) st = 1;
            if (!haveMax) max = Math.Ceiling(max / st - 1e-9) * st;
            step = st;
        }

        /// <summary>A copy of the entry's meta with the control resolved and the range filled in: what Pick looks at.</summary>
        public static UiSettingMeta Resolve(UiSettingEntry e)
        {
            var src = e.Meta;
            var m = new UiSettingMeta
            {
                Control = e.Control, Apply = src != null ? src.Apply : UiApply.Restart, ZeroIsAuto = src != null && src.ZeroIsAuto, UnitKey = src != null ? src.UnitKey : null,
                DisplayFactor = src != null ? src.DisplayFactor : 1, Options = src != null ? src.Options : null, Required = src != null ? src.Required : null,
                Parts = src != null ? src.Parts : null, HoldKey = src != null && src.HoldKey,
            };
            if (m.Control == UiControl.Number)
            {
                double def = 0;
                try { def = Convert.ToDouble(e.Box.Default, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { }
                double mn, mx, st;
                RangeOf(src, def, e.Box.Type == typeof(int), out mn, out mx, out st);
                m.Min = mn; m.Max = mx; m.Step = st;
            }
            else if (src != null) { m.Min = src.Min; m.Max = src.Max; m.Step = src.Step; }
            return m;
        }

        public static string SelfTest()
        {
            var num = new UiSettingMeta { Control = UiControl.Number, Min = 0, Max = 100, Step = 5 };
            if (Pick(num, 5) != SettingWidget.Slider || ValueCount(num) != 21) return "rule: 21 values -> slider";
            num.Max = 55; if (Pick(num, 5) != SettingWidget.Dropdown || ValueCount(num) != 12) return "rule: 12 values -> drop-down";
            num.Max = 60; if (Pick(num, 5) != SettingWidget.Slider) return "rule: 13 values -> slider";
            if (Pick(new UiSettingMeta { Control = UiControl.Number, Min = 0.85, Max = 2, Step = 0.05, ZeroIsAuto = true }, 5) != SettingWidget.AutoSlider) return "rule: ZeroIsAuto -> auto slider";
            if (Pick(new UiSettingMeta { Control = UiControl.Number }, 5) != SettingWidget.Text) return "rule: a number without a range is a text (the window derives a range first)";
            var ch = new UiSettingMeta { Control = UiControl.Choice, Options = new[] { "a", "b", "c", "d", "e" } };
            if (Pick(ch, 5) != SettingWidget.Segments) return "rule: 5 options -> segments";
            ch.Options = new[] { "a", "b", "c", "d", "e", "f" };
            if (Pick(ch, 5) != SettingWidget.Dropdown) return "rule: 6 options -> drop-down";
            if (Pick(new UiSettingMeta { Control = UiControl.Text, Parts = new[] { new[] { "nm", "km" } } }, 5) != SettingWidget.Parts) return "rule: text with parts";
            if (Pick(new UiSettingMeta { Control = UiControl.Text }, 5) != SettingWidget.Text) return "rule: a text stays a text";
            if (Pick(new UiSettingMeta { Control = UiControl.Check }, 5) != SettingWidget.Check || Pick(new UiSettingMeta { Control = UiControl.Hotkey }, 5) != SettingWidget.Hotkey || Pick(new UiSettingMeta { Control = UiControl.Multi, Options = new[] { "a" } }, 5) != SettingWidget.Multi) return "rule: check / hotkey / multi";
            if (Pick(null, 5) != SettingWidget.Text) return "rule: no meta";
            // derived ranges: the default is always a value of the grid
            foreach (var c in new[] { new object[] { 300.0, false }, new object[] { 0.7, false }, new object[] { 0.15, false }, new object[] { 5400.0, false }, new object[] { 1.0, false }, new object[] { 6.0, true }, new object[] { 40000.0, true }, new object[] { 0.0, false }, new object[] { -1.0, true }, new object[] { 0.25, false } })
            {
                double def = (double)c[0]; double mn, mx, st;
                RangeOf(null, def, (bool)c[1], out mn, out mx, out st);
                double q = (def - mn) / st;
                if (!(mx > mn) || !(st > 0) || Math.Abs(q - Math.Round(q)) > 1e-6 || def > mx + 1e-9 || def < mn - 1e-9) return "derived range of " + def + ": " + mn + ".." + mx + " step " + st;
                if ((bool)c[1] && st < 1) return "integer step of " + def;
                if ((mx - mn) / st > 200.5) return "derived range of " + def + " has too many values";
            }
            // an explicit range whose step misses the default is refined (0.5..10 step 1, default 4: 4 is not on 0.5, 1.5, ...)
            double a, b, s2;
            RangeOf(new UiSettingMeta { Min = 0.5, Max = 10, Step = 1 }, 4.0, false, out a, out b, out s2);
            double q2 = (4.0 - a) / s2;
            if (a != 0.5 || b != 10 || Math.Abs(q2 - Math.Round(q2)) > 1e-6) return "refined step: " + s2;
            RangeOf(new UiSettingMeta { Min = 500, Max = 40000, Step = 500 }, 0, false, out a, out b, out s2);
            if (s2 != 500) return "an explicit step is kept: " + s2;
            return null;
        }
    }

    /// <summary>The state machine of "Record" (ADR-0050 section 7): Escape or 5 s cancel, the first non-modifier key ends it.</summary>
    internal sealed class HotkeyRecorder
    {
        public const double TimeoutS = 5.0;
        private double _until;

        public bool Active { get; private set; }
        public string RowId { get; private set; }

        public void Start(string rowId, double now) { Active = true; RowId = rowId; _until = now + TimeoutS; }

        public void Cancel() { Active = false; RowId = null; }

        public static bool IsModifier(string keyName)
        {
            switch (keyName)
            {
                case "LeftShift": case "RightShift": case "LeftControl": case "RightControl":
                case "LeftAlt": case "RightAlt": case "LeftCommand": case "RightCommand": case "LeftWindows": case "RightWindows":
                case "AltGr": case "LeftApple": case "RightApple":
                    return true;
            }
            return false;
        }

        /// <summary>Call every frame while Active with the key pressed in this frame (null = none) and the modifiers held.
        /// Returns the spec ("LeftAlt+X") when finished; null while waiting. cancelled = Escape or timeout (Active becomes false).</summary>
        public string Feed(string keyName, IList<string> heldModifiers, double now, out bool cancelled)
        {
            cancelled = false;
            if (!Active) return null;
            if (keyName == "Escape" || now >= _until) { cancelled = true; Cancel(); return null; }
            if (string.IsNullOrEmpty(keyName) || IsModifier(keyName)) return null;
            var parts = new List<string>();
            if (heldModifiers != null)
                foreach (var m in heldModifiers) if (!string.IsNullOrEmpty(m) && !parts.Contains(m)) parts.Add(m);
            parts.Add(keyName);
            Cancel();
            return string.Join("+", parts.ToArray());
        }

        public static string SelfTest()
        {
            var rec = new HotkeyRecorder();
            bool c;
            if (rec.Active || rec.Feed("A", null, 0, out c) != null) return "recorder: idle";
            rec.Start("Cap.Hotkey", 10);
            if (!rec.Active || rec.RowId != "Cap.Hotkey") return "recorder: Start";
            if (rec.Feed("LeftAlt", new[] { "LeftAlt" }, 10.5, out c) != null || c || !rec.Active) return "recorder: a modifier alone waits";
            if (rec.Feed(null, new[] { "LeftAlt" }, 11, out c) != null || c) return "recorder: no key waits";
            string s = rec.Feed("Comma", new[] { "LeftAlt" }, 11.5, out c);
            if (s != "LeftAlt+Comma" || c || rec.Active) return "recorder: modifier + key = " + s;
            rec.Start("A.K", 0);
            s = rec.Feed("F10", new string[0], 1, out c);
            if (s != "F10" || rec.Active) return "recorder: a bare key = " + s;
            rec.Start("A.K", 0);
            if (rec.Feed("Escape", new[] { "LeftAlt" }, 1, out c) != null || !c || rec.Active) return "recorder: Escape cancels";
            rec.Start("A.K", 100);
            if (rec.Feed(null, null, 104.9, out c) != null || c || !rec.Active) return "recorder: still waiting before 5 s";
            if (rec.Feed(null, null, 105.0, out c) != null || !c || rec.Active) return "recorder: 5 s cancel";
            rec.Start("A.K", 0);
            s = rec.Feed("X", new[] { "LeftControl", "LeftShift", "LeftControl" }, 1, out c);
            if (s != "LeftControl+LeftShift+X") return "recorder: modifiers once = " + s;
            return null;
        }
    }
}
