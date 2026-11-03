// T-178 C: the CAP window on the ADR-0077 widgets: lists, sliders, compass, status chips, footer, memory of the last order, "Undo", layout and lint (ru/en x scale).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal static partial class CapWindowTests
    {
        private static readonly double[] WidgetScales = { 1.0, 0.85, 1.33 };

        public static string Widgets()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string e = WidgetsIn();
                    if (e != null) return lang + ": " + e;
                }
            return null;
        }

        private static string WidgetsIn()
        {
            string e;
            // C1: N and K are lists
            var r = new Rig(1.0, 580, Data(1)); r.Frame();
            foreach (string id in new[] { "cap.n", "cap.k" })
                if (r.P.Find(id) == null || r.P.Find(id).Kind == "Field") return "C1: " + id + " is not a drop-down";
            r.Pick("cap.n", 0);
            if (!r.P.Find("cap.n").Text.StartsWith("1")) return "C1: the pick does not reach the draft: " + r.P.Find("cap.n").Text;
            if (!r.P.Find("cap.k").Text.StartsWith(UiText.Fmt("ui.auto.value", 1).Replace("1)", "")) || !r.P.Find("cap.k").Text.Contains("(1)")) return "C1: 'Auto (N)' does not show the calculated value: " + r.P.Find("cap.k").Text;
            r.Click("apply");
            if ((e = Expect(r, "C1 apply", "cap_order")) != null) return e;
            if ((double)r.Out[0].Args["on_station"] != 1) return "C1: on_station " + r.Out[0].Args["on_station"];

            // C2: the altitude is a slider with "Auto" (= 0)
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            if (r.P.Find("cap.alt.auto") == null || r.P.Find("cap.alt.inc") == null || r.P.Find("cap.alt.inc").Enabled) return "C2: Auto on: the slider must be grey";
            r.Click("cap.alt.auto");
            if (!r.P.Find("cap.alt.inc").Enabled) return "C2: Auto off: the slider must be live";
            r.Click("apply");
            if ((e = Expect(r, "C2 apply", "cap_order")) != null) return e;
            if ((double)r.Out[0].Args["alt_ft"] != 15000) return "C2: Auto off gives the default value, got " + r.Out[0].Args["alt_ft"];

            // C3: zone by bearing: Segments + Compass; the order carries the absolute bearing
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("cap.zsrc.1");
            if (r.P.Find("cap.bearing.p.2") == null) return "C3: no compass";
            r.Click("cap.bearing.p.2");
            r.Click("apply");
            if ((e = Expect(r, "C3 east", "cap_order")) != null) return e;
            if ((double)r.Out[0].Args["bearing"] != 90) return "C3: preset East gives " + r.Out[0].Args["bearing"];
            r = new Rig(1.0, 580, Data(1)); r.E.Ship = 7; r.E.Course = 145; r.Frame();
            r.Click("cap.zsrc.1"); r.Click("cap.bearing.mode.1"); r.Frame();
            r.Click("cap.bearing.p.2"); r.Frame();   // +90 from the course
            if (r.P.FindText(UiText.Fmt("ui.compass.value.rel", "+90", 235)) == null) return "C3: no double caption '+90 from course (235)': " + string.Join(" | ", r.P.Items.FindAll(it => it.Id != null && it.Id.StartsWith("cap.bearing") || it.Text != null && it.Text.Contains("°")).ConvertAll(it => it.ToString()).ToArray());
            r.Click("apply");
            if ((e = Expect(r, "C3 relative", "cap_order")) != null) return e;
            if ((double)r.Out[0].Args["bearing"] != 235) return "C3: from course +90 at 145 gives " + r.Out[0].Args["bearing"];
            if (r.S.Value("compass.cap.bearing", "") != "rel") return "C3: the compass mode is not remembered";
            r = new Rig(1.0, 580, Data(1)); r.Frame(); r.Click("cap.zsrc.1");
            if (r.P.Find("cap.bearing.mode.1") == null || r.P.Find("cap.bearing.mode.1").Enabled) return "C3: 'from course' must be grey without a course";

            // C4: thresholds are sliders
            r = new Rig(1.0, 580, Data(1)); r.S.Open["cap.thresholds"] = true; r.Frame(); r.Frame();
            if (r.P.Of("Field").Count != 0) return "C4: a text field is left in the window: " + r.P.Of("Field")[0].Id;
            foreach (string id in new[] { "cap.thr.missile", "cap.thr.margin", "cap.thr.floor" })
                if (r.P.Find(id + ".inc") == null) return "C4: no slider " + id;

            // C5: the fields are filled; the last order is remembered
            r = new Rig(1.0, 580, Data(1)); r.Frame(); r.Click("cap.grp.2"); r.Frame();
            if (r.P.Find("start") == null || !r.P.Find("start").Enabled) return "C5: the first 'Start' is not available";
            r.Pick("cap.n", 3); r.Pick("cap.dist", 3);
            r.Click("start");
            if ((e = Expect(r, "C5 start", "cap_order")) != null) return e;
            if (string.IsNullOrEmpty(r.S.Value("cap.last", null))) return "C5: the last order is not remembered";
            var r2 = new Rig(1.0, 580, Data(1));
            foreach (var kv in r.S.Vals) r2.S.Vals[kv.Key] = kv.Value;   // a restart: the store comes back from Format()
            r2.Frame(); r2.Click("cap.grp.2"); r2.Frame();
            if (!r2.P.Find("cap.n").Text.StartsWith("4")) return "C5: N after a restart: " + r2.P.Find("cap.n").Text;
            if (!r2.P.Find("cap.dist").Text.StartsWith("40")) return "C5: distance after a restart: " + r2.P.Find("cap.dist").Text;
            if (!r2.P.Find("start").Enabled) return "C5: Start after a restart";

            if ((e = MapModes()) != null) return e;
            if ((e = Undo()) != null) return e;
            if ((e = WidgetLayout()) != null) return e;
            return null;
        }

        private static double[] Sz(double[] z)
        {
            return new[] { Math.Abs(z[3] - z[1]) * 60.0 * Math.Cos((z[0] + z[2]) / 2.0 * Math.PI / 180.0), Math.Abs(z[2] - z[0]) * 60.0 };
        }

        // C8: RMB / "Cancel" keep the previous zone; the handles change the draft; "Changed"; the size label
        private static string MapModes()
        {
            using (AswMath.WorldScope(true)) return MapModesBody();   // T-352: the old expectation is the beta (cos lat); the flat world has its own case (zone_worldflat)
        }

        private static string MapModesBody()
        {
            var zone = new[] { 36.0, 21.0, 36.5, 21.5 };
            var r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("cap.draw");
            if (!r.B.WantRect) return "C8: draw mode not on";
            r.Frame();
            if (r.P.Find("cap.draw.cancel") == null) return "C8: no Cancel in the mode row";
            r.Click("cap.draw.cancel");
            if (r.B.WantRect) return "C8: Cancel did not leave the mode";
            r.B.OnRect(zone); r.Frame();
            r.Click("cap.draw");
            r.B.CancelInput();   // what the right mouse button does (UiMapDrawInput.RightClick -> CancelInput)
            r.Frame();
            if (r.B.WantRect || r.B.DraftZone == null || Math.Abs(r.B.DraftZone[0] - 36.0) > 1e-9) return "C8: RMB must keep the previous draft zone";
            // handles on the draft zone
            double[] before = Sz(r.B.DraftZone);
            r.B.BeginEdit();
            if (!r.B.Editing) return "C8: no edit on a draft zone";
            r.B.EditZone(ZoneHandle.NE, 10.2, 5.4);
            double[] after = Sz(r.B.DraftZone);
            if (Math.Abs(after[0] - (before[0] + 10)) > 1.5 || Math.Abs(after[1] - (before[1] + 5)) > 1.5) return "C8: NE handle: " + before[0] + "x" + before[1] + " -> " + after[0] + "x" + after[1];
            r.B.EditZone(ZoneHandle.E, 5000, 0);
            if (Sz(r.B.DraftZone)[0] > r.B.MaxZoneNm + 1.5) return "C8: the zone is longer than MaxZone: " + Sz(r.B.DraftZone)[0];
            r.B.EndEdit(false);
            if (Math.Abs(r.B.DraftZone[3] - 21.5) > 1e-9 || Math.Abs(r.B.DraftZone[2] - 36.5) > 1e-9) return "C8: RMB during the drag must bring the zone back";
            // the zone of an order: a handle makes a draft and "Changed"
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            if (r.B.DraftZone != null || r.B.EditableZone == null) return "C8: the order zone must be editable without a draft";
            r.B.BeginEdit(); r.B.EditZone(ZoneHandle.N, 0, 8); r.B.EndEdit(true); r.Frame();
            if (r.B.DraftZone == null) return "C8: a handle changed nothing";
            if (r.P.FindText("● " + UiText.Get("ui.footer.changed")) == null) return "C8: no 'Changed' mark after a handle";
            r.Click("apply");
            if (Expect(r, "C8 apply of the handle", "cap_order") != null) return "C8: apply after a handle";
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.B.BeginEdit(); r.B.EditZone(ZoneHandle.N, 0, 8); r.B.EndEdit(false);
            if (r.B.DraftZone != null) return "C8: cancelling the drag of the order zone must leave no draft";
            if (CapWindowBody.SizeLabel(zone) == null || !CapWindowBody.SizeLabel(zone).Contains("×")) return "C8: size label " + CapWindowBody.SizeLabel(zone);
            return null;
        }

        private static CapView WithOnlyCandidates(CapView v, string group)
        {
            // the stand's "after the disband": the members of the group are free candidates
            var g = v.Groups.Find(x => x.Line.Group == group);
            v.Groups.Remove(g);
            foreach (var m in g.Members) v.Candidates.Add(new CapCandidateView { Uid = m.Uid, Name = m.Name, Type = m.Type, TypeLabel = m.TypeLabel, ShipUid = m.ShipUid, ShipName = m.ShipName, Where = "hangar" });
            return v;
        }

        // C7 (T-199, ADR-0084): "Stop all" and "Disband" take a snapshot, the strip in the window body offers "Undo" (10 s), the result stays 6 s
        private static bool Has(Rig r, string part) { return r.P.Items.Exists(it => it.Text != null && it.Text.Contains(part)); }

        private static string Undo()
        {
            string e;
            var r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("stop_all");
            if ((e = Expect(r, "C7 stop all", "cap_stop")) != null) return e;
            if (r.P.Find("undo.btn") == null) return "C7: no undo strip in the window after 'Stop all'";
            if (r.P.Find("stop_all") == null) return "C7: the strip must not hide 'Stop all'";
            if (!Has(r, UiText.Fmt("ui.undo.left", 10))) return "C7: 10 s expected";
            r.E.Time = 4; r.Frame();
            if (!Has(r, UiText.Fmt("ui.undo.left", 6))) return "C7: 6 s left expected";
            r.Click("undo.btn");
            if ((e = Expect(r, "C7 undo of stop all", "cap_stop", "cap_order", "cap_order")) != null) return e;
            if (r.Out[1].Args["group"].ToString() != "ПВО-1" || r.Out[2].Args["group"].ToString() != "ПВО-2") return "C7: the orders of the same groups";
            r.Frame();
            if (!Has(r, UiText.Fmt("ui.undo.done", 2, 2))) return "C7: result line";
            if (r.P.Find("undo.btn") != null) return "C7: the button stays after the press";
            r.E.Time = 9; r.Frame();
            if (!Has(r, UiText.Fmt("ui.undo.done", 2, 2))) return "C7: result must stay 6 s";
            r.E.Time = 11; r.Frame();
            if (Has(r, UiText.Fmt("ui.undo.done", 2, 2))) return "C7: result must hide after 6 s";
            // too late
            r = new Rig(1.0, 580, Data(1)); r.Frame(); r.Click("stop_all");
            r.E.Time = 10.5; r.Out.Clear(); r.Frame();
            if (r.P.Find("undo.btn") != null) return "C7: undo strip after 10 s";
            if (r.B.PressUndo() != null || r.Out.Count != 0) return "C7: undo after 10 s sent commands";
            // a mission change drops the snapshot
            r = new Rig(1.0, 580, Data(1)); r.Frame(); r.Click("stop_all");
            r.B.ResetUndo(); r.Out.Clear(); r.Frame();
            if (r.P.Find("undo.btn") != null) return "C7: Reset left the strip";
            if (r.B.PressUndo() != null || r.Out.Count != 0) return "C7: Reset did not drop the snapshot";
            // disband: a busy machine is in the skipped list
            var v = Data(1);
            r = new Rig(1.0, 580, v); r.Frame();
            r.Click("disband");
            if ((e = Expect(r, "C7 disband", "cap_group")) != null) return e;
            int taken = v.Groups[0].Members[0].Uid;
            WithOnlyCandidates(v, "ПВО-1");
            v.Candidates.Find(c => c.Uid == taken).Reason = "player";
            r.Frame();
            r.Click("undo.btn");
            if (r.Out.Count != 3 || r.Out[1].Name != "cap_group" || r.Out[2].Name != "cap_order") return "C7: undo of disband: " + string.Join(",", r.Out.ConvertAll(o => o.Name).ToArray());
            if ((string)r.Out[1].Args["op"] != "create" || (string)r.Out[1].Args["group"] != "ПВО-1" || ((List<object>)r.Out[1].Args["uids"]).Count != 1) return "C7: create with the old name and the free machine only";
            r.Frame();
            if (!Has(r, UiText.Fmt("ui.undo.done", 1, 1))) return "C7: result with the skipped machine";
            return null;
        }

        // C6/C9: the layout of the whole window: no overflow, nothing past the width, lint clean, no text field, the gap of [+] [-]
        private static string WidgetLayout()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                    foreach (double s in WidgetScales)
                        foreach (int kind in new[] { 1, 3 })
                            foreach (bool all in new[] { false, true })
                                foreach (bool polar in new[] { false, true })
                                {
                                    float w = (float)(500 * s);
                                    var r = new Rig(s, w, Data(kind));
                                    if (all) { r.S.Open["cap.thresholds"] = true; r.S.Open["cap.loadout"] = true; }
                                    string tag = "C9 " + lang + " s=" + s + " data=" + kind + (all ? " all" : "") + (polar ? " polar" : "");
                                    r.Frame();
                                    if (kind == 3) r.Click("cap.grp.2");
                                    if (polar) r.Click("cap.zsrc.1");
                                    r.Frame(); r.Frame();
                                    string bad = r.P.CheckLayout(w, 100000f);
                                    if (bad != null) return tag + ": " + bad;
                                    if (r.K.Overflows.Count > 0) return tag + ": overflow " + r.K.Overflows[0];
                                    var lint = r.P.Lint(UiTheme.For(s, UiDensity.Compact), true, new string[0]);
                                    // table rows are 16 px x s high (13.6 px at the floor 0.85): a limit of UiKit.Table (kit), not of this window
                                    lint.RemoveAll(x => x.StartsWith("height") && (x.Contains(".chk.") || x.Contains(".row.") || x.Contains(".link.")));
                                    if (lint.Count > 0) return tag + ": lint " + string.Join("; ", lint.GetRange(0, Math.Min(12, lint.Count)).ToArray());
                                    var plus = r.P.Find("new"); var minus = r.P.Find("disband");
                                    if (plus == null || minus == null || minus.R.X - (plus.R.X + plus.R.W) < 8f * UiScale.Windows(s) - 0.01f) return tag + ": the gap of [+] [-] is under 8 px x s";
                                    if (r.P.Of("Field").Count != 0) return tag + ": text field";
                                }
            return null;
        }
    }
}
