// T-179 / ADR-0079: the "Alert" mode of the CAP window on the RecordPainter: the mode row, the fields, the folded "More", the scramble button, the ship pick,
// one command for a change of the mode, ru/en x scales x widths, no overflow, every control has a tip, no text field.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static partial class CapWindowTests
    {
        private static CapGroupView AlertGroup(string name, int readyNow, int air, bool lent)
        {
            var g = Group(name, true, 3, new[] { "F-14A" }, false);
            g.Kind = "alert"; g.Rect = null; g.N = 2; g.Roe = "tight";
            g.AlertSpec = new CapAlertSpec { RefUid = 7, Ready = 2, PerRaid = 2, Level = "deck", RadiusNm = 80, PursuitNm = 60, Speed = "max", Roe = "tight", After = "patrol", Targets = AlertTargets.Air };
            g.ReadyNow = readyNow; g.Airborne = air;
            g.ScrambleBlock = readyNow == 0 ? "no_ready" : null;
            foreach (var m in g.Members) { m.Alert = true; m.Slot = -1; m.TypeLabel = "F-14A"; }
            if (lent) g.Lent.Add(new KeyValuePair<int, string>(99, "ПВО-1"));
            return g;
        }

        private static CapView AlertView(int readyNow, int air, bool lent)
        {
            var v = new CapView();
            v.Groups.Add(Group("ПВО-1", true, 2, new[] { "F-14A" }, true));
            v.Groups.Add(AlertGroup("ПВО-2", readyNow, air, lent));
            v.Groups.Add(Group("ПВО-3", false, 2, new[] { "F-14A" }, false));
            return v;
        }

        public static string Alert179()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string e = Alert179In();
                    if (e != null) return lang + ": " + e;
                }
            return null;
        }

        private static Rig AlertRig(double s, float w, CapView v, bool more = false)
        {
            var r = new Rig(s, w, v);
            if (more) r.S.Open["cap.alert_more"] = true;
            r.Frame(); r.Click("cap.grp.1"); r.Frame(); r.Frame();
            return r;
        }

        private static string Alert179In()
        {
            string e;
            // ---- layout: the alert mode and the patrol mode at every scale and width, "More" shut and open, with and without ready machines
            foreach (double s in new[] { 0.85, 1.0, 1.33 })
                foreach (float bw in new[] { 420f, 500f, 760f })
                    foreach (bool more in new[] { false, true })
                        foreach (int ready in new[] { 2, 0 })
                        {
                            float w = (float)(bw * s);
                            var r = AlertRig(s, w, AlertView(ready, ready == 0 ? 0 : 1, ready == 0), more);
                            string tag = "layout s=" + s + " w=" + bw + (more ? " more" : "") + " ready=" + ready;
                            if (r.P.Find("cap.mode.1") == null || !r.P.Find("cap.mode.1").Selected || r.P.Find("cap.mode.0") == null || r.P.Find("cap.mode.0").Selected) return tag + ": the mode row";
                            string bad = r.P.CheckLayout(w, 100000f);
                            if (bad != null) return tag + ": " + bad;
                            if (r.K.Overflows.Count > 0) return tag + ": overflow " + r.K.Overflows[0];
                            var lint = r.P.Lint(UiTheme.For(s, UiDensity.Compact), true, new string[0]);
                            lint.RemoveAll(x => x.StartsWith("height") && (x.Contains(".chk.") || x.Contains(".row.") || x.Contains(".link.")));
                            if (lint.Count > 0) return tag + ": lint " + string.Join("; ", lint.GetRange(0, Math.Min(10, lint.Count)).ToArray());
                            if (r.P.Of("Field").Count != 0) return tag + ": a text field (Number) in the window";
                            if (r.P.Find("cap.protect") == null || r.P.Find("cap.alert.ready") == null || r.P.Find("cap.alert.per_raid.0") == null || r.P.Find("cap.alert.radius") == null || r.P.Find("cap.alert.after.0") == null) return tag + ": a visible field is missing";
                            if ((r.P.Find("cap.alert.level.0") != null) != more || (r.P.Find("cap.alert.targets.0") != null) != more) return tag + ": the folded 'More' shows its fields only when open";
                            if (r.P.Find("cap.n") != null || r.P.Find("cap.zsrc.0") != null) return tag + ": patrol fields in the alert mode";
                        }
            // the patrol mode: the same group list, the accept_alert check, no alert fields
            var rp = new Rig(1.0, 580, AlertView(2, 0, false)); rp.Frame(); rp.Frame();
            if (rp.P.Find("cap.mode.0") == null || !rp.P.Find("cap.mode.0").Selected) return "patrol is the first mode";
            if (rp.P.Find("cap.accept_alert") == null || rp.P.Find("cap.protect") != null) return "patrol: the accept_alert check and no alert fields";

            // ---- the chip of the group carries the mode, the status line says ready / airborne / lent
            var rc = AlertRig(1.0, 580, AlertView(2, 1, true));
            if (rc.P.FindText(UiText.Fmt("cap.lbl.chip", "ПВО-2", 3) + " " + UiText.Get("cap.chip.mode_alert")) == null) return "the group chip has no mode";
            if (rc.P.FindText(CapWindowModel.AlertLine(rc.V.Groups[1])) == null) return "no alert status line: " + CapWindowModel.AlertLine(rc.V.Groups[1]);

            // ---- "More": folds and unfolds; the summary shows in the bar when it is shut
            var rm = AlertRig(1.0, 580, AlertView(2, 0, false));
            if (rm.P.FindText(CapWindowBody.AlertSummary(rm.V.Groups[1].AlertSpec)) == null) return "no summary of the shut More";
            rm.Click("sec.cap.alert_more"); rm.Frame();
            if (rm.P.Find("cap.alert.targets.0") == null) return "More does not open";
            rm.Click("sec.cap.alert_more"); rm.Frame();
            if (rm.P.Find("cap.alert.targets.0") != null) return "More does not shut";

            // ---- targets are never empty; Apply sends them
            var rt = AlertRig(1.0, 580, AlertView(2, 0, false), true);
            rt.Click("cap.alert.targets.0"); rt.Frame();
            if (!rt.P.Find("cap.alert.targets.0").Selected) return "the last target chip went off";
            rt.Click("cap.alert.targets.1"); rt.Frame();
            rt.Click("apply"); rt.Frame();
            if ((e = Expect(rt, "apply targets", "cap_order")) != null) return e;
            var po = CapCommands.ParseOrder(rt.Out[0].Args, out e);
            if (po == null || po.Kind != "alert" || po.AlertSpec.Targets != (AlertTargets.Air | AlertTargets.Helo)) return "targets in the order " + e;

            // ---- "Changed" and the grey apply after a field edit
            var rd = AlertRig(1.0, 580, AlertView(2, 0, false));
            if (rd.P.Find("apply") == null || rd.P.Find("apply").Enabled) return "apply is grey while nothing changed";
            rd.Pick("cap.alert.radius", 0); rd.Frame();
            if (!rd.P.Find("apply").Enabled) return "apply after a change";
            if (rd.P.FindText("● " + UiText.Get("ui.footer.changed")) == null) return "no 'Changed' mark";
            rd.Click("apply");
            if ((e = Expect(rd, "apply radius", "cap_order")) != null) return e;
            po = CapCommands.ParseOrder(rd.Out[0].Args, out e);
            if (po == null || po.AlertSpec.RadiusNm != 40 || po.Kind != "alert") return "radius in the order " + e;

            // ---- per raid: 1 / 2 / all
            var rr = AlertRig(1.0, 580, AlertView(2, 0, false));
            rr.Click("cap.alert.per_raid.2"); rr.Frame(); rr.Click("apply");
            po = rr.Out.Count == 1 ? CapCommands.ParseOrder(rr.Out[0].Args, out e) : null;
            if (po == null || po.AlertSpec.PerRaid != 0) return "per raid = all " + e;

            // ---- "Scramble now": grey with the reason without ready machines; a press sends cap_scramble
            var rg = AlertRig(1.0, 580, AlertView(0, 0, false));
            var sc = rg.P.Find("scramble");
            if (sc == null || sc.Enabled || sc.Tip != UiText.Get("cap.hint.scramble_no_ready")) return "scramble must be grey with the reason: " + (sc == null ? "absent" : sc.Tip);
            var ro = AlertRig(1.0, 580, AlertView(2, 0, false));
            sc = ro.P.Find("scramble");
            if (sc == null || !sc.Enabled || sc.Text != UiText.Get("cap.btn.scramble")) return "scramble must be live";
            ro.Click("scramble");
            if ((e = Expect(ro, "scramble", "cap_scramble")) != null) return e;
            if ((string)ro.Out[0].Args["group"] != "ПВО-2") return "scramble group";
            var rpat = new Rig(1.0, 580, AlertView(2, 0, false)); rpat.Frame(); rpat.Frame();
            if (rpat.P.Find("scramble") != null) return "the patrol has no scramble button";

            // ---- the ship to protect: the list and the map pick
            var rs = AlertRig(1.0, 580, AlertView(2, 0, false));
            if (rs.P.Find("cap.protect.map") == null) return "no 'On the map' button";
            rs.Click("cap.protect.map"); rs.Frame();
            if (!rs.B.WantShipPick || rs.P.Find("cap.protect.cancel") == null) return "map pick does not start";
            rs.B.OnShipPicked(9); rs.B.MapPick.Cancel("test"); rs.Frame();
            rs.Click("apply"); rs.Frame();
            if ((e = Expect(rs, "protect", "cap_order")) != null) return e;
            po = CapCommands.ParseOrder(rs.Out[0].Args, out e);
            if (po == null || po.AlertSpec.RefUid != 9) return "ref_uid of the pick " + e;
            var rx = AlertRig(1.0, 580, AlertView(2, 0, false));
            rx.Click("cap.protect.map"); rx.Frame(); rx.B.CancelShipPick("rmb"); rx.Frame();
            if (rx.B.WantShipPick) return "the pick is not cancelled";

            // ---- a change of the mode is ONE cap_order kind=...: patrol -> alert, alert -> patrol (a zone by bearing is prefilled)
            var rk = new Rig(1.0, 580, AlertView(2, 0, false)); rk.Frame(); rk.Frame();
            rk.Click("cap.mode.1"); rk.Frame(); rk.Frame();
            if (rk.P.Find("cap.protect") == null) return "the alert fields do not show after the mode click";
            if (rk.P.FindText("● " + UiText.Get("ui.footer.changed")) == null) return "mode change: no 'Changed'";
            rk.Click("apply");
            if ((e = Expect(rk, "mode -> alert", "cap_order")) != null) return e;
            po = CapCommands.ParseOrder(rk.Out[0].Args, out e);
            if (po == null || po.Kind != "alert" || po.Zone.Rect != null) return "patrol -> alert order " + e;
            var rb = AlertRig(1.0, 580, AlertView(2, 0, false));
            rb.Click("cap.mode.0"); rb.Frame(); rb.Frame();
            if (rb.P.Find("cap.n") == null) return "patrol fields do not show";
            rb.Click("apply");
            if ((e = Expect(rb, "mode -> patrol", "cap_order")) != null) return e;
            po = CapCommands.ParseOrder(rb.Out[0].Args, out e);
            if (po == null || po.Kind != "patrol" || !po.Polar) return "alert -> patrol order " + e;

            // ---- accept_alert of the patrol
            var ra = new Rig(1.0, 580, AlertView(2, 0, false)); ra.Frame(); ra.Frame();
            ra.Click("cap.accept_alert"); ra.Frame(); ra.Click("apply");
            if ((e = Expect(ra, "accept_alert", "cap_order")) != null) return e;
            po = CapCommands.ParseOrder(ra.Out[0].Args, out e);
            if (po == null || po.Kind != "patrol" || po.AcceptAlert != 0) return "accept_alert in the order " + e;

            // ---- the loadout "Auto" of an alert group is the fastest one
            var rl = new Rig(1.0, 580, AlertView(2, 0, false)); rl.S.Open["cap.loadout"] = true; rl.Frame(); rl.Click("cap.grp.1"); rl.Frame(); rl.Frame();
            if (!rl.P.Items.Exists(it => it.Text != null && it.Text.Contains("≈"))) return "no 'Auto (fastest ...)' text: " + string.Join(" | ", rl.P.Items.FindAll(it => it.Id != null && it.Id.StartsWith("cap.lo")).ConvertAll(it => it.ToString()).ToArray());

            // ---- the undo of 'Stop all' brings the alert group back with its kind
            var ru = AlertRig(1.0, 580, AlertView(2, 0, false));
            ru.Click("stop_all"); ru.Frame();
            if (ru.P.Find("undo.btn") == null) return "no undo strip";
            ru.Out.Clear();
            ru.Click("undo.btn");
            bool back = false;
            foreach (var s in ru.Out) if (s.Name == "cap_order" && (string)s.Args["group"] == "ПВО-2" && (string)s.Args["kind"] == "alert") back = true;
            if (!back) return "the undo does not restore the alert kind: " + string.Join(", ", ru.Out.ConvertAll(x => x.Name + ":" + x.Args["group"]).ToArray());
            return null;
        }
    }
}
