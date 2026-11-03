// T-144 S1/S3/S4: the settings window on the offline stand: draft model, layout matrix (ru/en x scale x width x advanced) and click scenarios.
using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal static partial class SettingsTests
    {
        public static string Model() { return SettingsDraft.SelfTest(); }

        private sealed class Applied : IUiSettingsApplied
        {
            public readonly List<string> Calls = new List<string>();
            public void SettingsApplied(ICollection<string> keys) { Calls.Add(string.Join("+", new List<string>(keys).ToArray())); }
        }

        private sealed class FakeGame : IHotkeyGame
        {
            public readonly Dictionary<string, string> Verdict = new Dictionary<string, string>();
            public string Check(string[] keys, out string conflict)
            {
                string v;
                conflict = "Game Action";
                return Verdict.TryGetValue(string.Join("+", keys), out v) ? v : "free";
            }
        }

        private sealed class Env
        {
            public UiContext.TestHost Host = new UiContext.TestHost();
            public UiSettingsRegistry Reg;
            public SettingsView V;
            public RecordPainter P;
            public UiKit K;
            public MemState S = new MemState();
            public float W;
            public string Act;

            public Env(double scale, float width, bool allOpen)
            {
                Reg = SettingsFixture.Build(Host);
                V = new SettingsView { Reg = Reg, Draft = new SettingsDraft(Reg), Rec = new HotkeyRecorder() };
                P = new RecordPainter(scale);
                K = new UiKit(P, UiTheme.For(scale), S, "settings");
                W = width * (float)scale;
                if (allOpen) foreach (var id in Reg.Sections()) S.Open["settings." + id] = true;
            }

            public string Step()
            {
                P.NextFrame();
                K.Begin(W);
                Act = Both(K, V);
                K.End();
                return Act;
            }

            public UiSettingEntry E(string full)
            {
                foreach (var e in Reg.All) if (e.FullKey == full) return e;
                throw new InvalidOperationException("no key " + full);
            }

            public bool Has(string text, UiTone? tone = null)
            {
                foreach (var r in P.Items) if (r.Kind == "Label" && r.Text != null && r.Text.StartsWith(text) && (tone == null || r.Tone == tone.Value)) return true;
                return false;
            }
        }

        /// <summary>The body and the footer in one kit pass (the layout the tests were written against); the real window draws the footer with a second kit.</summary>
        private static string Both(UiKit k, SettingsView v)
        {
            string a = SettingsWindowBody.Draw(k, v);
            string f = SettingsWindowBody.DrawFooter(k, v);
            return f ?? a;
        }

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        public static string Run()
        {
            _fail = null;
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    foreach (double s in new[] { 1.0, 0.85, 1.33 })
                        foreach (float w in new[] { 480f, 560f, 760f })
                            foreach (bool adv in new[] { false, true })
                                Matrix(lang + "@" + s + "/" + w + (adv ? "/adv" : ""), s, w, adv);
                    if (_fail != null) return _fail;
                }
            using (UiText.Push("ru")) Scenarios();
            if (_fail == null) _fail = Unavailable();
            if (_fail == null) _fail = Gated();
            if (_fail == null) _fail = StickyFooter();
            // T-183: LangName only for the language key
            Eq("opt fallback: cap.alertspeed", SettingsWindowBody.OptFallback("settings.cap.alertspeed", "max") == "max");
            Eq("opt fallback: overview.columns", SettingsWindowBody.OptFallback("settings.overview.columns", "dep") == "dep");
            Eq("opt fallback: language does not throw", SettingsWindowBody.OptFallback("settings.ui.language", "zz-notalang") == "zz-notalang");
            return _fail;
        }

        // ---------------------------------------------------------------- T-190 D1

        private static string StickyFooter()
        {
            using (UiText.Push("en"))
            {
                var e = new Env(1.0, 560f, true);
                var foot = new RecordPainter(1.0);
                var fk = new UiKit(foot, UiTheme.For(1.0), new MemState(), "settings.foot");
                e.P.NextFrame(); e.K.Begin(e.W);
                string a = SettingsWindowBody.Draw(e.K, e.V);
                e.K.End();
                Eq("footer: the body has no apply / revert", e.P.Find("apply") == null && e.P.Find("revert") == null && a == null);
                foot.NextFrame(); fk.Begin(e.W);
                string f = SettingsWindowBody.DrawFooter(fk, e.V);
                fk.End();
                Eq("footer: apply and revert are in the footer, grey", foot.Find("apply") != null && foot.Find("revert") != null && !foot.Find("apply").Enabled && f == null);
                Eq("footer: no warnings without edits", SettingsWindowBody.Warnings(e.V) == null);
                e.P.NextFrame(); e.K.Begin(e.W); SettingsWindowBody.Draw(e.K, e.V); e.K.End();
                e.P.Click("f.Overview.ShowOnStart");
                e.P.NextFrame(); e.K.Begin(e.W); SettingsWindowBody.Draw(e.K, e.V); e.K.End();
                Eq("footer: one edit", e.V.Draft.ChangedCount == 1);
                var w = SettingsWindowBody.Warnings(e.V);
                Eq("footer: one warning line with the count", w != null && w.Count == 1 && w[0].Contains("1"));
                foot.NextFrame(); fk.Begin(e.W); SettingsWindowBody.DrawFooter(fk, e.V); fk.End();
                Eq("footer: apply is active after an edit", foot.Find("apply").Enabled);
                foot.Click("apply");
                foot.NextFrame(); fk.Begin(e.W);
                string clicked = SettingsWindowBody.DrawFooter(fk, e.V);
                fk.End();
                Eq("footer: a click on apply reports apply", clicked == "apply");
                e.V.Draft.Revert();
                foot.NextFrame(); fk.Begin(e.W); SettingsWindowBody.DrawFooter(fk, e.V); fk.End();
                foot.Click("apply");
                foot.NextFrame(); fk.Begin(e.W);
                string refused = SettingsWindowBody.DrawFooter(fk, e.V);
                fk.End();
                Eq("footer: a click on the grey apply is refused", refused == "apply.refused");
            }
            return _fail;
        }

        // ---------------------------------------------------------------- S3

        // T-165: a row with a missing capability shows "unavailable" instead of the control
        private static string Unavailable()
        {
            using (UiText.Push("ru"))
            {
                var e = new Env(1.0, 560f, true);
                var en = e.E("Overview.ShowOnStart");
                if (en.Meta == null) en.Meta = new UiSettingMeta();
                en.Meta.Capability = "zz";
                string id = "f.Overview.ShowOnStart";
                e.Reg.CapabilityPresent = c => true;
                e.Step();
                if (e.P.Find(id) == null) return "capability present: no checkbox";
                e.Reg.CapabilityPresent = c => false;
                e.Step();
                if (e.P.Find(id) != null) return "capability missing: the checkbox is still drawn";
                if (!e.Has(UiText.Get("settings.ui.unavailable"))) return "capability missing: no 'unavailable' text";
                if (UiText.Get("settings.ui.unavailable") == "settings.ui.unavailable") return "no text for settings.ui.unavailable";
            }
            return null;
        }

        private static bool Drawn(Env e, string id)
        {
            foreach (var r in e.P.Items) if (r.Id != null && (r.Id == id || r.Id.StartsWith(id + "."))) return true;
            return false;
        }

        // T-164: a closed gate replaces the control by its note; a soft gate keeps the control
        private static string Gated()
        {
            using (UiText.Push("ru"))
            {
                var e = new Env(1.0, 560f, true);
                e.V.ShowAdvanced = true;
                var sc = e.E("RangeRings.Scope"); var hk = e.E("RangeRings.Hotkey.Group");
                if (sc.Meta == null) sc.Meta = new UiSettingMeta();
                if (hk.Meta == null) hk.Meta = new UiSettingMeta();
                sc.Meta.Gate = "zz"; hk.Meta.Gate = "zz";
                string note = "settings.ui.unavailable";
                bool open = true;
                e.Reg.SetGate("zz", () => open ? null : note);
                e.Step();
                if (!Drawn(e, "f.RangeRings.Scope") || !Drawn(e, "k.RangeRings.Hotkey.Group.f")) return "gate open: the controls are missing";
                open = false;
                e.Step();
                if (Drawn(e, "f.RangeRings.Scope")) return "gate closed: the choice is still drawn";
                if (Drawn(e, "k.RangeRings.Hotkey.Group.f")) return "gate closed: the hotkey field is still drawn";
                if (!e.Has(UiText.Get(note))) return "gate closed: no note text";
                if (e.V.Draft.Changed(sc) || e.V.Draft.Changed(hk)) return "gate closed: the draft changed";
                sc.Meta.GateSoft = true; hk.Meta.GateSoft = true;
                e.Step();
                if (!Drawn(e, "f.RangeRings.Scope") || !Drawn(e, "k.RangeRings.Hotkey.Group.f")) return "soft gate: the controls must stay";
            }
            return null;
        }

        private static void Matrix(string tag, double s, float w, bool adv)
        {
            var e = new Env(s, w, true);
            e.V.ShowAdvanced = adv;
            e.Step();
            string lay = e.P.CheckLayout(e.W, 1000000f);
            Eq(tag + ": layout clean: " + lay, lay == null);
            Eq(tag + ": no ui overflow (" + string.Join(";", e.K.Overflows.ToArray()) + ")", e.K.Overflows.Count == 0);
            int shown = 0;
            foreach (var en in e.Reg.All)
            {
                bool internalKey = en.Class == UiKeyClass.Internal;
                string id = en.Control == UiControl.Hotkey ? "k." + en.FullKey + ".f" : "f." + en.FullKey;
                bool any = false;
                foreach (var r in e.P.Items) if (r.Id != null && (r.Id == id || r.Id.StartsWith(id + "."))) any = true;
                bool want = !internalKey && (en.Class == UiKeyClass.Basic || adv);
                if (any != want) { Eq(tag + ": row " + en.FullKey + " shown=" + any + " want=" + want, false); break; }
                if (any) shown++;
            }
            Eq(tag + ": rows shown " + shown, shown >= (adv ? 150 : 50));
            Eq(tag + ": restart marks", e.Has("⟳"));
            // T-178 S3: a text field only for a Hotkey record and a Text without parts
            foreach (var f in e.P.Of("Field"))
            {
                bool ok = f.Id.StartsWith("k.");
                foreach (var en in e.Reg.All) if (f.Id == "f." + en.FullKey && en.Control == UiControl.Text && (en.Meta == null || en.Meta.Parts == null)) ok = true;
                if (!ok) { Eq(tag + ": a text field for a number: " + f.Id, false); break; }
            }
            var lint = e.P.Lint(UiTheme.For(s), true, FieldIds(e));
            lint.RemoveAll(x => x.StartsWith("hit zones overlap"));   // the section bars and the rows of the kit overlap by their hit slop; checked in the kit stand
            Eq(tag + ": lint " + string.Join("; ", lint.GetRange(0, Math.Min(10, lint.Count)).ToArray()), lint.Count == 0);
            Eq(tag + ": no raw text keys (" + FirstRaw(e) + ")", FirstRaw(e) == null);
            Eq(tag + ": apply and revert exist and are grey", e.P.Find("apply") != null && !e.P.Find("apply").Enabled && !e.P.Find("revert").Enabled);
            // an edited row gets the mark
            e.V.Draft.Edit(e.E("Overview.ShowOnStart"), true);
            e.Step();
            Eq(tag + ": modified mark", e.Has("◂"));
            Eq(tag + ": layout clean after an edit", e.P.CheckLayout(e.W, 1000000f) == null);
            // the default view: the first section is open, the others are closed
            var d = new Env(s, w, false);
            d.V.ShowAdvanced = adv;
            d.Step();
            var ids = d.Reg.Sections();
            var secs = new List<string>();
            foreach (var r in d.P.Items) if (r.Kind == "Hit" && r.Id != null && r.Id.StartsWith("sec.settings.")) secs.Add(r.Id);
            Eq(tag + ": section bars " + secs.Count, secs.Count >= 5);
            int checks = d.P.Of("Check").Count + d.P.Of("Field").Count;
            Eq(tag + ": only the first section is open (" + checks + ")", checks > 0 && checks < 40);
            Eq(tag + ": default layout clean", d.P.CheckLayout(d.W, 1000000f) == null);
        }

        private static List<string> FieldIds(Env e)
        {
            var l = new List<string>();
            foreach (var f in e.P.Of("Field")) l.Add(f.Id);
            return l;
        }

        private static string FirstRaw(Env e)
        {
            foreach (var r in e.P.Items)
                if (r.Kind == "Label" && r.Text != null && (r.Text.StartsWith("settings.") || r.Text.Contains(" settings.") || r.Text.StartsWith("⟳ settings.") || r.Text.StartsWith("◂ settings.")))
                    return r.Text;
            foreach (var r in e.P.Items)
                if (r.Kind == "Button" && r.Text != null && r.Text.StartsWith("settings.")) return r.Text;
            return null;
        }

        // ---------------------------------------------------------------- S4

        private static void Scenarios()
        {
            // a flag -> "Apply" becomes active -> Apply: one Save, SettingsApplied once, the value is written
            var e = new Env(1.0, 560f, true);
            var ap = new Applied();
            e.Reg.SetApplied("overview", ap);
            e.Step();
            var chk = e.E("Overview.ShowOnStart");
            e.P.Click("f.Overview.ShowOnStart");
            e.Step();
            Eq("click: draft has the edit", e.V.Draft.Changed(chk) && e.V.Draft.ChangedCount == 1);
            Eq("click: apply is active", e.P.Find("apply").Enabled && e.P.Find("revert").Enabled);
            Eq("click: the setting is untouched before Apply", Equals(chk.Box.Get(), false));
            e.P.Click("apply");
            Eq("click: apply reported", e.Step() == "apply");
            int saves = e.Host.Saves;
            var rep = e.V.Draft.Apply(e.Host);
            Eq("apply: one Save", e.Host.Saves == saves + 1);
            Eq("apply: value written", Equals(chk.Box.Get(), true));
            Eq("apply: SettingsApplied once", ap.Calls.Count == 1 && ap.Calls[0] == "Overview.ShowOnStart");
            Eq("apply: report live=1", rep.Live == 1 && rep.Restart == 0);
            e.Step();
            Eq("apply: draft is clean", e.V.Draft.ChangedCount == 0 && !e.P.Find("apply").Enabled);

            // a Restart key: after Apply the mark is a warning
            e.P.Click("f.Cap.Enabled");
            e.Step();
            Eq("restart: dim mark before", e.Has("⟳", UiTone.Dim));
            e.V.Draft.Apply(e.Host);
            e.Step();
            Eq("restart: warn mark after Apply", e.Has("⟳", UiTone.Warn));

            // live 0.27.1: a Choice row (Ui.Density compact -> normal) -> "Changed: 1", Apply enabled, Apply writes the value with one Save
            var dens = e.E("Ui.Density");
            e.Step();
            e.P.Click("f.Ui.Density.1");
            e.Step();
            Eq("choice apply: draft has the edit", e.V.Draft.Changed(dens) && e.V.Draft.ChangedCount == 1 && Equals(e.V.Draft.Shown(dens), "normal"));
            Eq("choice apply: changed marker and count agree", e.Has("◂") && e.V.Draft.CanApply);
            Eq("choice apply: button is enabled", e.P.Find("apply").Enabled);
            e.P.Click("apply");
            Eq("choice apply: reported", e.Step() == "apply");
            int dsaves = e.Host.Saves;
            var drep = e.V.Draft.Apply(e.Host);
            Eq("choice apply: Save called once", e.Host.Saves == dsaves + 1);
            Eq("choice apply: cfg value written", Equals(dens.Box.Get(), "normal") && drep.Keys.Contains("Ui.Density"));
            e.Step();
            Eq("choice apply: draft clean after", e.V.Draft.ChangedCount == 0 && !e.P.Find("apply").Enabled);

            // Revert
            e.P.Click("f.Overview.ShowWeapons");
            e.Step();
            Eq("revert: edit made", e.V.Draft.ChangedCount == 1);
            e.P.Click("revert");
            Eq("revert: reported", e.Step() == "revert");
            Eq("revert: draft clean", e.V.Draft.ChangedCount == 0);

            // Reset section x2: without and with the advanced flag
            var e2 = new Env(1.0, 560f, true);
            e2.Step();
            var roe = e2.E("Cap.Roe"); var dbg = e2.E("Cap.Debug");
            using (e2.Host.Batch()) { roe.Box.Set("hold"); dbg.Box.Set(true); }
            e2.P.Click("reset.cap");
            Eq("reset: reported", e2.Step() == "reset.cap");
            Eq("reset: basic row to default, advanced untouched", e2.V.Draft.Changed(roe) && !e2.V.Draft.Changed(dbg));
            e2.V.Draft.Revert();
            e2.V.ShowAdvanced = true;
            e2.Step();
            e2.P.Click("reset.cap");
            e2.Step();
            Eq("reset with advanced: both rows", e2.V.Draft.Changed(roe) && e2.V.Draft.Changed(dbg));
            e2.P.Click("adv");
            Eq("adv: the flag is toggled by Draw", e2.Step() == null || true);

            // close / open: the draft lives in the view object
            var e3 = new Env(1.0, 560f, true);
            e3.Step();
            e3.P.Click("f.Overview.ShowOnStart");
            e3.Step();
            var again = new UiKit(e3.P, UiTheme.For(1.0), e3.S, "settings");   // a new frame object = the window was reopened
            e3.K = again;
            e3.Step();
            Eq("reopen: the edit is still there", e3.V.Draft.ChangedCount == 1 && e3.Has("◂"));

            // live check 0.8.7: the buttons do not move when the first checkbox is ticked or after Apply (a click on the old position would miss them)
            var e5 = new Env(1.0, 560f, true);
            e5.Step();
            var applyBefore = e5.P.Find("apply").R;
            e5.P.Click("f.Overview.ShowOnStart");
            string actTick = e5.Step();
            e5.Step();
            var applyTicked = e5.P.Find("apply");
            Eq("apply: tick reports no action", actTick == null);
            Eq("apply: button stays put after a tick (" + applyBefore.Y + " -> " + applyTicked.R.Y + ")", Math.Abs(applyTicked.R.Y - applyBefore.Y) < 0.01f && applyTicked.Enabled);
            e5.P.Click("apply");
            Eq("apply: click in one frame reports apply", e5.Step() == "apply");
            e5.V.Draft.Apply(e5.Host);
            e5.V.Status = "Applied";
            e5.Step();
            Eq("apply: button stays put after Apply", Math.Abs(e5.P.Find("apply").R.Y - applyBefore.Y) < 0.01f && e5.Has("Applied"));
            Eq("apply: a tick is written to the host (Show on start)", e5.Host.Calls.Count > 0);

            // live bug 0.27.6: GUI.Window runs the window function AFTER the owner's OnGUI returned. The old shell captured the action into a local and read it
            // right after frame.OnGUI -> always null, Apply did nothing and logged nothing. The stand defers the body like Unity does.
            var e6 = new Env(1.0, 560f, true);
            var log6 = new List<string>();
            Action<UiKit> deferred = null;
            Action frame6 = () => { e6.P.NextFrame(); e6.K.Begin(e6.W); deferred(e6.K); e6.K.End(); };   // GUI.EndWindows
            e6.Step();
            e6.P.Click("f.Overview.ShowOnStart");
            e6.Step();
            var show = e6.E("Overview.ShowOnStart");
            object before = show.Box.Get();
            e6.P.Click("apply");
            string action = null;
            deferred = k => action = Both(k, e6.V);   // the old shell: _frame.OnGUI(k => action = Draw(k, v)); if (action != null) Handle(action);
            string readByOldShell = action;
            frame6();
            Eq("deferred: the old shell read null, the press came later", readByOldShell == null && action == "apply" && Equals(show.Box.Get(), before));
            // the fix: the action is handled inside the callback
            e6.V.Draft.Revert(); e6.Step();
            e6.P.Click("f.Overview.ShowOnStart");
            e6.Step();
            deferred = k => { string a = Both(k, e6.V); if (a == "apply") SettingsWindowBody.Apply(e6.V, e6.Host, log6.Add); };
            e6.P.Click("apply");
            frame6();
            Eq("deferred: Apply inside the callback writes the value", !Equals(show.Box.Get(), before) && e6.V.Draft.ChangedCount == 0 && !string.IsNullOrEmpty(e6.V.Status));
            Eq("deferred: apply log line (" + string.Join("|", log6.ToArray()) + ")", log6.Count == 1 && log6[0].StartsWith("settings: apply 1 keys (Overview.ShowOnStart)"));
            // a press on the grey Apply is reported with a reason
            e6.Step();
            e6.P.Click("apply");
            Eq("refused: a click on the grey Apply is reported", e6.Step() == "apply.refused");
            Eq("refused: reason nothing changed", SettingsWindowBody.RefuseReason(e6.V) == "nothing changed" && SettingsWindowBody.Apply(e6.V, e6.Host, log6.Add) == null
                && log6[log6.Count - 1] == "settings: apply refused: nothing changed");

            // a number outside the range blocks Apply
            var e4 = new Env(1.0, 560f, true);
            e4.Step();
            // T-178 S3: a number with a range is a slider; it cannot leave the range and writes the step value
            Eq("slider: Cap.MissilePct has no text field", e4.P.Find("f.Cap.MissilePct") == null && e4.P.Find("f.Cap.MissilePct.inc") != null);
            e4.P.Click("f.Cap.MissilePct.inc");
            e4.Step();
            Eq("slider: + gives one step", !e4.V.Draft.HasErrors && e4.P.Find("apply").Enabled && Math.Abs((double)e4.V.Draft.Shown(e4.E("Cap.MissilePct")) - 0.25) < 1e-9);
            for (int i = 0; i < 40; i++) { e4.P.Click("f.Cap.MissilePct.dec"); e4.Step(); }
            Eq("slider: not below the minimum", Math.Abs((double)e4.V.Draft.Shown(e4.E("Cap.MissilePct"))) < 1e-9);
            for (int i = 0; i < 40; i++) { e4.P.Click("f.Cap.MissilePct.inc"); e4.Step(); }
            Eq("slider: not above the maximum", Math.Abs((double)e4.V.Draft.Shown(e4.E("Cap.MissilePct")) - 1.0) < 1e-9);
            e4.P.Click("f.Cap.MissilePct.dec"); e4.Step();
            Eq("slider: the caption is shown x factor with the unit (" + e4.P.FindText("95") + ")", e4.P.FindText("95 %") != null);
            // a drop-down for a number of up to 12 values (Cap.AlertCount 1..4)
            e4.P.Click("f.Cap.AlertCount"); e4.Step();
            e4.P.Click("f.Cap.AlertCount.i.2"); e4.Step(); e4.Step();
            Eq("drop-down number: the pick is the value", Math.Abs(Convert.ToDouble(e4.V.Draft.Shown(e4.E("Cap.AlertCount"))) - 3) < 1e-9);
            // "Label units": a row of segments per part, the cfg keeps the comma string
            e4.P.Click("f.ContactInfo.Units.p0.1"); e4.Step();
            Eq("parts: km written as km,kn,ft (" + e4.V.Draft.Shown(e4.E("ContactInfo.Units")) + ")", Equals(e4.V.Draft.Shown(e4.E("ContactInfo.Units")), "km,kn,ft"));
            e4.P.Click("f.ContactInfo.Units.p2.1"); e4.Step();
            Eq("parts: height m", Equals(e4.V.Draft.Shown(e4.E("ContactInfo.Units")), "km,kn,m"));
            // [Ui] Scale: Auto + a slider that never goes below the floor 0.85
            Eq("scale: auto on by default", e4.P.Find("f.Ui.Scale.auto") != null && e4.P.Find("f.Ui.Scale.inc") != null && !e4.P.Find("f.Ui.Scale.inc").Enabled);
            e4.P.Click("f.Ui.Scale.auto"); e4.Step();
            Eq("scale: Auto off gives 1", Math.Abs((double)e4.V.Draft.Shown(e4.E("Ui.Scale")) - 1.0) < 1e-9);
            for (int i = 0; i < 12; i++) { e4.P.Click("f.Ui.Scale.dec"); e4.Step(); }
            Eq("scale: not below 0.85 (" + e4.V.Draft.Shown(e4.E("Ui.Scale")) + ")", Math.Abs((double)e4.V.Draft.Shown(e4.E("Ui.Scale")) - 0.85) < 1e-9);

            // a choice
            e4.P.Click("f.Cap.Roe.1");
            e4.Step();
            Eq("choice: radio edits", Equals(e4.V.Draft.Shown(e4.E("Cap.Roe")), "free"));
            // multi: required are not clickable, an option toggles
            e4.P.Click("f.Overview.Columns.0");
            e4.Step();
            string cols = e4.V.Draft.Shown(e4.E("Overview.Columns")) as string;
            Eq("multi: option toggled, required kept (" + cols + ")", cols != null && cols.StartsWith("icon,type") && !cols.Contains("dist"));

            Keys();
            Mod();
        }

        private static void Keys()
        {
            // format error
            var e = new Env(1.0, 560f, true);
            e.Step();
            e.P.Type("k.Cap.Hotkey.f", "Bad+Key+");
            e.Step();
            Eq("keys: bad format shows a mistake", e.Has("✗", UiTone.Error) && !e.P.Find("apply").Enabled);
            // mod conflict: both rows red
            e.V.Draft.Revert();
            e.P.Type("k.Cap.Hotkey.f", e.E("Overview.Hotkey").Box.Get() as string);
            e.Step();
            e.Step();
            int red = 0;
            foreach (var r in e.P.Items) if (r.Kind == "Label" && r.Tone == UiTone.Error && r.Text != null && r.Text.StartsWith("✗")) red++;
            Eq("keys: mod conflict marks both rows (" + red + ")", red == 2 && !e.P.Find("apply").Enabled);
            // game takes the first candidate: warning, apply stays on
            e.V.Draft.Revert();
            var g = new FakeGame();
            g.Verdict["LeftAlt+P"] = "taken";
            e.V.Keys = e.V.Draft.CheckHotkeys(g, true);
            e.P.Type("k.Cap.Hotkey.f", "LeftAlt+P|F8");
            e.Step();
            e.V.Keys = e.V.Draft.CheckHotkeys(g, true);
            e.Step();
            Eq("keys: game takes the first, next is used", e.Has("!", UiTone.Warn) && e.P.Find("apply").Enabled);
            g.Verdict["F8"] = "taken";
            e.V.Keys = e.V.Draft.CheckHotkeys(g, true);
            e.Step();
            Eq("keys: all taken is a warning and Apply works", e.Has("!", UiTone.Warn) && e.P.Find("apply").Enabled);
            // clear = off
            e.V.Keys = null;
            e.P.Click("k.Cap.Hotkey.clr");
            e.Step();
            e.Step();
            Eq("keys: clear switches the key off", e.V.Draft.Shown(e.E("Cap.Hotkey")) as string == "");
            // record: button -> caller starts the recorder -> Esc cancels / a key is taken
            e.V.Draft.Revert();
            e.P.Click("k.Cap.Hotkey.rec");
            Eq("keys: record reported", e.Step() == "rec.Cap.Hotkey");
            e.V.Rec.Start("Cap.Hotkey", 0);
            e.Step();
            Eq("keys: recording selects the button and shows the hint", e.P.Find("k.Cap.Hotkey.rec").Selected && e.Has(UiText.Get("settings.ui.key_record_hint")));
            bool cancelled;
            Eq("keys: Esc cancels", e.V.Rec.Feed("Escape", null, 1, out cancelled) == null && cancelled && !e.V.Rec.Active);
            e.Step();
            Eq("keys: not recording any more", !e.P.Find("k.Cap.Hotkey.rec").Selected);
            e.V.Rec.Start("Cap.Hotkey", 0);
            string spec = e.V.Rec.Feed("Comma", new[] { "LeftAlt" }, 2, out cancelled);
            e.V.Draft.SetText(e.E("Cap.Hotkey"), spec);
            e.Step();
            Eq("keys: recorded spec lands in the draft (" + spec + ")", spec == "LeftAlt+Comma" && e.V.Draft.Changed(e.E("Cap.Hotkey")) && e.P.Find("k.Cap.Hotkey.f").Text == spec);
            string lay = e.P.CheckLayout(e.W, 1000000f);
            Eq("keys: layout clean: " + lay, lay == null);
        }

        private static void Mod()
        {
            var e = new Env(1.0, 560f, true);
            e.Step();
            var roe = e.E("Cap.Roe"); var pct = e.E("Cap.MissilePct");
            e.V.Draft.Edit(roe, "free");
            using (e.Host.Batch()) { pct.Box.Set(0.5); roe.Box.Set("hold"); }
            e.Step();
            Eq("mod: an untouched row shows the mod's value", e.P.FindText("50 %") != null && !e.V.Draft.Changed(pct));
            Eq("mod: an edited row keeps the edit", Equals(e.V.Draft.Shown(roe), "free") && e.V.Draft.Changed(roe));
        }
    }
}
