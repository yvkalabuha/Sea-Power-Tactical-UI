using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    // T-178 A (ADR-0077): the ASW / SAR / Strike window on the widgets: ship pick (list by distance, "On the map", RMB / Cancel), status chips, footer, the "Undo" card of "Stop all" / "Disband",
    // handles of the area (rectangle, SAR circle, 100 miles at most), the strike inputs (no fields), ru / en x scale 1 / 0.85 / 1.33: no field, lint clean, nothing past the width.
    internal static class AswT178Tests
    {
        private static string T(string key) { return UiText.Get(key); }

        public static string Run()
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string r = ShipPick() ?? Chips() ?? Undo() ?? Handles() ?? Strike() ?? SarParts() ?? CompassMemory();
                    if (r != null) sb.Append(lang + ": " + r + "\n");
                    foreach (double s in new[] { 1.0, 0.85, 1.33 })
                    {
                        r = Layout(s);
                        if (r != null) sb.Append(lang + " s=" + s + ": " + r + "\n");
                    }
                }
            return sb.Length == 0 ? null : sb.ToString();
        }

        // a course group with a board in the air at (35, 20); two own ships: "Zulu" 3 miles away, "Alpha" 12 miles away (by distance Zulu comes first, by name Alpha would)
        internal static AswView View()
        {
            var v = AswWindowTests.Normal();
            v.RefLat = 35; v.RefLon = 20;
            var g = v.Groups[0];
            g.MemberRows[0].HasPos = true; g.MemberRows[0].Lat = 35; g.MemberRows[0].Lon = 20;
            g.CourseTargets.Clear();
            g.CourseTargets.Add(new AswTargetOption { Uid = 500, Name = "Alpha", HasPos = true, Lat = 35.2, Lon = 20 });
            g.CourseTargets.Add(new AswTargetOption { Uid = 501, Name = "Zulu", HasPos = true, Lat = 35.05, Lon = 20 });
            g.CourseTargets.Add(new AswTargetOption { Uid = 600, Name = "Convoy", Kind = "formation", WidthNm = 9, HasPos = true, Lat = 35.1, Lon = 20 });
            g.TargetUid = 500; g.TargetName = "Alpha";
            v.CourseTargets.Clear(); v.CourseTargets.AddRange(g.CourseTargets);
            v.SelectedShipUid = 500;
            return v;
        }

        private static Win Make(double s, AswView v)
        {
            var w = new Win(s, 520f * (float)s, v);
            w.Frame(); w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            return w;
        }

        private static string ShipPick()
        {
            var w = Make(1.0, View());
            if (!w.Has("tgt") || !w.Has("tgt.map")) return "no ship pick (list + On the map) for the target";
            if (w.Has("tgt.next") || w.Has("tgt.prev")) return "the old arrows of the target are back";
            w.Click("tgt");
            string d0 = UnitFmt.Nm(3.0), d1 = UnitFmt.Nm(12.0);
            // the labels of the list: names and distances (nearest first: Zulu, then Alpha)
            int iz = -1, ia = -1, n = 0;
            foreach (var it in w.P.Items) if (it.Kind == "Label" && it.Text != null) { n++; if (it.Text == "Zulu") iz = n; if (it.Text == "Alpha") ia = n; }
            if (iz < 0 || ia < 0 || iz > ia) return "the ships are not sorted by distance: Zulu " + iz + " Alpha " + ia;
            if (w.P.FindText(UnitFmt.Nm(Math.Round(3.0, 1))) == null && !w.P.Items.Exists(x => x.Kind == "Label" && x.Text != null && x.Text.Contains(UnitFmt.Nm(3.0).Split(' ')[0]))) return "no distance in the list: " + d0 + " / " + d1;
            w.Click("tgt.i.0"); w.Frame();   // Zulu
            if (w.B.Draft.TargetUid != 501 || w.B.Draft.TargetKind != "ship") return "pick from the list: " + w.B.Draft.TargetUid;
            if (w.Sent.Count != 0) return "the pick sent something: " + w.Cmds();
            // On the map: the pick phase starts, a click on a ship assigns it, RMB cancels
            w.Click("tgt.map");
            if (w.B.WantInput != "ship_pick" || !w.B.MapPick.Active) return "On the map did not start the pick: " + w.B.WantInput;
            if (!w.Has("tgt.cancel")) return "no Cancel button during the pick";
            var xy = new List<float[]>();
            foreach (var it in w.B.PickItems) xy.Add(it.Uid == 500 ? new[] { 100f, 100f } : it.Uid == 501 ? new[] { 300f, 100f } : null);
            if (w.B.MapPick.OnClick(w.B.PickItems, xy, 600f, 600f) != 0 || !w.B.MapPick.Active) return "a click on the empty sea ended the pick";
            int uid = w.B.MapPick.OnClick(w.B.PickItems, xy, 105f, 98f);
            if (uid != 500) return "a click near Alpha gave " + uid;
            w.B.OnShipPicked(uid); w.Frame();
            if (w.B.Draft.TargetUid != 500 || w.B.WantInput != null || w.B.MapPick.Active) return "the picked ship is not the target / the mode did not end";
            w.Click("tgt.map");
            w.B.CancelInput("asw.pick.cancelled"); w.Frame();   // RMB / Esc
            if (w.B.MapPick.Active || w.B.WantInput != null || w.B.Draft.TargetUid != 500) return "RMB did not end the pick without a change";
            w.Click("tgt.map"); w.Click("tgt.cancel");
            if (w.B.MapPick.Active || w.B.WantInput != null) return "the Cancel button did not end the pick";
            // another group ends the pick, a mode change too
            w.Click("tgt.map"); w.B.SelectedGroup = "ПЛО-2"; w.Frame(); w.Frame();
            if (w.B.MapPick.Active) return "a group change did not end the pick";
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame(); w.Click("tgt.map"); w.Mode(1);
            if (w.B.MapPick.Active) return "a mode change did not end the pick";
            // a formation (-leader) is a target of kind formation
            w.Mode(0); w.Frame();
            return null;
        }

        private static string Chips()
        {
            var v = View();
            v.Groups[0].Contacts.Add(new AswContactView { Id = 7, Name = "Sub #7", Held = true });
            v.Groups[0].DeckWaitS = 75;
            var w = Make(1.0, v);
            string contact = T("asw.status.contact").Split('{')[0].Trim();
            if (!w.P.Items.Exists(x => x.Kind == "Label" && x.Text != null && x.Text.Contains(contact))) return "no contact chip";
            if (w.P.Items.FindAll(x => x.Kind == "Fill" && x.Id == UiColorRole.Warn.ToString()).Count < 2) return "the contact and the deck wait are not warning chips";
            // the footer: [Route] ... [Stop] gap [Apply]; the primary is the right-most button
            var apply = w.P.Find("apply"); var stop = w.P.Find("stop"); var route = w.P.Find("route");
            if (apply == null || stop == null || route == null) return "no footer buttons";
            if (apply.R.X < stop.R.X + stop.R.W + 12f - 0.5f) return "the gap between Stop and the primary is under 12 px";
            if (route.R.X > stop.R.X) return "the auxiliary button is not left of Stop";
            if (w.P.Items.Exists(x => x.Kind == "Button" && x.R.X + x.R.W > apply.R.X + apply.R.W + 0.5f && x.R.Y >= apply.R.Y - 0.5f && x.R.Y <= apply.R.Y + 0.5f)) return "the primary is not the last on its row";
            return null;
        }

        private static string Undo()
        {
            var w = Make(1.0, View());
            w.Click("stop_all");
            if (w.Sent.Count != 1 || w.Sent[0] != "asw_stop" || !w.Args[0].ContainsKey("all")) return "Stop all sent " + w.Cmds();
            // T-199 (ADR-0084): the strip is in the window body under "Groups", "Stop all" stays reachable
            if (w.P.Find("undo.btn") == null) return "no Undo strip in the window";
            if (w.P.Find("stop_all") == null) return "the strip hides Stop all";
            // the groups with an order are in the snapshot (ПЛО-1 course, ПЛО-2 area, SAR-1 sar)
            w.Sent.Clear(); w.Args.Clear();
            w.Env.Time += 4;
            w.Click("undo.btn"); w.Frame();
            bool course = false, area = false, sar = false;
            for (int i = 0; i < w.Sent.Count; i++)
            {
                if (w.Sent[i] == "asw_search_course" && (string)w.Args[i]["group"] == "ПЛО-1" && w.Args[i].ContainsKey("tactics") && w.Args[i].ContainsKey("depth")) course = true;
                if (w.Sent[i] == "asw_search_area" && (string)w.Args[i]["group"] == "ПЛО-2" && w.Args[i].ContainsKey("rect")) area = true;
                if (w.Sent[i] == "sar_order" && (string)w.Args[i]["group"] == "SAR-1") sar = true;
            }
            if (!course || !area || !sar) return "Undo did not replay the orders (course " + course + " area " + area + " sar " + sar + "): " + w.Cmds();
            if (w.P.Find("undo.btn") != null) return "the Undo button stays after the press";
            string done = T("ui.undo.done").Split('{')[0].Trim();
            if (!w.P.Items.Exists(x => x.Kind == "Label" && x.Text != null && x.Text.StartsWith(done, StringComparison.Ordinal))) return "no result line after Undo";
            w.Env.Time += 5; w.Frame();
            if (!w.P.Items.Exists(x => x.Kind == "Label" && x.Text != null && x.Text.StartsWith(done, StringComparison.Ordinal))) return "the result must stay 6 s";
            w.Env.Time += 2; w.Frame();
            if (w.P.Items.Exists(x => x.Kind == "Label" && x.Text != null && x.Text.StartsWith(done, StringComparison.Ordinal))) return "the result must hide after 6 s";
            // a late press (after 10 s): the strip is gone, nothing is restored
            var w2 = Make(1.0, View());
            w2.Click("stop_all"); w2.Sent.Clear();
            w2.Env.Time += 10.5; w2.Frame();
            if (w2.P.Find("undo.btn") != null) return "the strip must hide after 10 s";
            if (w2.B.PressUndo() != null || w2.Sent.Count != 0) return "a late Undo sent " + w2.Cmds();
            // the mission change drops it
            var w3 = Make(1.0, View());
            w3.Click("stop_all"); w3.B.ResetUndo(); w3.Frame();
            if (w3.P.Find("undo.btn") != null) return "ResetUndo did not hide the strip";
            // Disband: the group goes at once, the strip brings it back with its members and its order
            var v4 = View();
            var w4 = Make(1.0, v4);
            w4.Click("grp_disband");
            if (w4.Sent.Count != 1 || w4.Sent[0] != "asw_group" || (string)w4.Args[0]["op"] != "disband") return "Disband sent " + w4.Cmds();
            w4.Frame();   // the strip is drawn above the Disband button: it shows in the next frame
            if (w4.P.Find("undo.btn") == null) return "no Undo strip for Disband";
            // the game: the group is gone, its boards are free again (one board is taken meanwhile)
            var gone = v4.Groups[0]; v4.Groups.RemoveAt(0);
            foreach (var r in gone.MemberRows) if (r.Uid != gone.MemberRows[1].Uid) v4.Available.Add(r);
            w4.Frame();
            w4.Sent.Clear(); w4.Args.Clear();
            w4.Click("undo.btn"); w4.Frame();
            if (w4.Sent.Count < 2 || w4.Sent[0] != "asw_group" || (string)w4.Args[0]["op"] != "create" || (string)w4.Args[0]["group"] != "ПЛО-1") return "Disband undo: no create with the old name: " + w4.Cmds();
            var uids = w4.Args[0]["uids"] as List<object>;
            if (uids == null || uids.Count != gone.MemberRows.Count - 1) return "the taken board must be skipped: " + (uids == null ? "null" : uids.Count.ToString());
            if (!w4.Sent.Contains("asw_search_course") || w4.Sent.IndexOf("asw_search_course") < w4.Sent.IndexOf("asw_loadout")) return "Disband undo: the order is not replayed: " + w4.Cmds();
            if (!w4.P.Items.Exists(x => x.Kind == "Label" && x.Text != null && x.Text.Contains(gone.MemberRows[1].Name))) return "the skipped board is not named in the result line";
            return null;
        }

        private static string Handles()
        {
            // ASW area: a drawn rectangle has handles, 100 miles at most, a drag is undone by cancel
            var w = Make(1.0, View());
            w.Mode(1); w.Frame();
            if (w.B.EditableRect != null) return "handles before the area is drawn";
            w.Click("asrc.0");
            if (w.B.WantInput != "asw_rect") return "Draw did not start the input";
            w.B.CancelInput("asw.say.draw_cancelled"); w.Frame();
            if (w.B.WantInput != null || w.B.Draft.AreaSource != null) return "RMB left a half-drawn area";
            w.Click("asrc.0");
            w.B.OnRect(new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 }); w.Frame();
            var z = w.B.EditableRect;
            if (z == null || Math.Abs(z[0] - 35.0) > 1e-9 || Math.Abs(z[2] - 35.1) > 1e-9) return "no handles on the drawn area";
            double heightBefore = Math.Abs(z[2] - z[0]) * 60;
            w.B.BeginEdit();
            if (!w.B.Editing) return "BeginEdit";
            w.B.EditRect(ZoneHandle.N, 0, 20);   // the north edge 20 miles up
            var z2 = w.B.EditableRect;
            if (z2 == null || Math.Abs((z2[2] - z2[0]) * 60 - (heightBefore + 20)) > 1.01) return "the north handle did not stretch the area: " + (z2 == null ? "null" : ((z2[2] - z2[0]) * 60).ToString());
            if (!w.B.Draft.GeoDirty || w.B.Draft.AreaSource != "rect" || w.B.Draft.Rect.Length != 8) return "the edit did not reach the draft";
            w.B.EditRect(ZoneHandle.N, 0, 500);   // far past the limit
            var z3 = w.B.EditableRect;
            if ((z3[2] - z3[0]) * 60 > 100.5) return "the area is over 100 miles: " + (z3[2] - z3[0]) * 60;
            w.B.EndEdit(false);
            var z4 = w.B.EditableRect;
            if (z4 == null || Math.Abs((z4[2] - z4[0]) * 60 - heightBefore) > 0.01) return "RMB during the drag did not bring the area back";
            // the square by bearing has no handles
            w.Click("asrc.1");
            if (w.B.EditableRect != null) return "the square by bearing has handles";
            // SAR: rectangle and circle
            var s = Make(1.0, View());
            s.Mode(2); s.Frame();
            s.Click("swhere.1");
            if (s.B.WantInput != "sar_rect") return "SAR rectangle did not start the input";
            s.B.CancelInput("asw.say.draw_cancelled"); s.Frame();
            if (s.B.WantInput != null || s.B.EditableRect != null) return "SAR cancel";
            s.Click("swhere.1");
            s.B.OnRect(new[] { 35.0, 20.0, 35.0, 20.4, 35.2, 20.4, 35.2, 20.0 }); s.Frame();
            var sz = s.B.EditableRect;
            if (sz == null || Math.Abs(sz[2] - 35.2) > 1e-9) return "no handles on the SAR rectangle";
            s.B.BeginEdit(); s.B.EditRect(ZoneHandle.E, 10, 0); s.B.EndEdit(true);
            if (s.B.EditableRect[3] <= sz[3]) return "the east handle did not widen the SAR rectangle";
            s.Click("swhere.2");
            if (s.B.WantInput != "sar_point") return "SAR circle did not start the pointing";
            s.B.CancelInput("sar.ui.cancelled"); s.Frame();
            if (s.B.EditableCircle != null || s.B.Mode != "sar") return "RMB must bring the previous SAR area back";
            s.Click("swhere.2"); s.B.OnPoint(35.0, 20.0); s.Frame();
            var c = s.B.EditableCircle;
            if (c == null || c[2] <= 0) return "no circle after the point";
            s.B.BeginEdit(); s.B.EditCircle(ZoneHandle.Radius, 12, 0);
            var c2 = s.B.EditableCircle;
            if (c2[2] <= c[2] || Array.IndexOf(AswPatternMath.SarRadiusList, c2[2]) < 0) return "the radius handle: " + c2[2];
            s.B.EditCircle(ZoneHandle.Center, 5, 5);
            if (s.B.EditableCircle[0] <= c[0]) return "the centre handle";
            s.B.EndEdit(false);
            var c3 = s.B.EditableCircle;
            if (Math.Abs(c3[0] - c[0]) > 1e-9 || Math.Abs(c3[2] - c[2]) > 1e-9) return "cancel did not bring the circle back";
            return null;
        }

        /// <summary>T-352 S4: the zone editing follows the world flag (flat on stable, cos(lat) on the beta globe).</summary>
        public static string ZoneWorldFlat()
        {
            foreach (bool globe in new[] { false, true })
                using (AswMath.WorldScope(globe))
                {
                    double k = globe ? Math.Cos(60.0 * Math.PI / 180.0) : 1.0;
                    var s = Make(1.0, View());
                    s.Mode(2); s.Frame();
                    s.Click("swhere.1");
                    s.B.OnRect(new[] { 59.5, 10.0, 59.5, 10.5, 60.5, 10.5, 60.5, 10.0 }); s.Frame();
                    s.B.BeginEdit(); s.B.EditRect(ZoneHandle.E, 10, 0);
                    double dLon = s.B.EditableRect[3] - 10.5, want = 10.0 / (60.0 * k);
                    if (Math.Abs(dLon - want) > 1e-9) return "rect globe=" + globe + ": dLon " + dLon + " want " + want;
                    s.B.EndEdit(false);
                    string lab = AswWindowBody.SizeLabel(new[] { 59.5, 10.0, 60.5, 11.0 });
                    string wantLab = globe ? "30 × 60" : "60 × 60";
                    if (lab == null || !lab.StartsWith(wantLab)) return "label globe=" + globe + ": " + lab;
                    s.Click("swhere.2"); s.B.OnPoint(60.0, 10.0); s.Frame();
                    var c = s.B.EditableCircle;
                    s.B.BeginEdit(); s.B.EditCircle(ZoneHandle.Center, 10, 0);
                    double dc = s.B.EditableCircle[1] - c[1];
                    if (Math.Abs(dc - want) > 1e-9) return "circle globe=" + globe + ": dLon " + dc + " want " + want;
                    s.B.EndEdit(false);
                }
            return null;
        }

        private static string Strike()
        {
            var v = View();
            var w = Make(1.0, v);
            w.Mode(3); w.Frame();
            if (w.B.Mode != "strike") return "no strike mode";
            if (w.P.Of("Field").Count > 0) return "a text field in the strike mode";
            if (!w.Has("ref") || !w.Has("ref.map") || !w.Has("radius") || !w.Has("airborne.2")) return "strike inputs: ref " + w.Has("ref") + " map " + w.Has("ref.map") + " radius " + w.Has("radius") + " airborne " + w.Has("airborne.2");
            if (w.B.StrikeDraft.RefUid != 500) return "the guarded ship does not start with the selected ship: " + w.B.StrikeDraft.RefUid;
            w.Pick("ref", 0);   // the nearest = Zulu
            if (w.B.StrikeDraft.RefUid != 501) return "ref pick: " + w.B.StrikeDraft.RefUid;
            w.Pick("radius", 3); w.Click("airborne.2");
            if (w.Sent.Count != 0) return "the draft sent something: " + w.Cmds();
            w.Click("start_strike");
            if (w.Sent.Count != 1 || w.Sent[0] != "asw_order") return "start sent " + w.Cmds();
            var a = w.Args[0];
            if ((double)a["ref_uid"] != 501 || (double)a["radius_nm"] != 30 || (double)a["airborne"] != 2) return "order args: " + a["ref_uid"] + "/" + a["radius_nm"] + "/" + a["airborne"];
            w.Click("ref.map");
            if (w.B.WantInput != "ship_pick") return "strike: On the map";
            var xy = new List<float[]>();
            foreach (var it in w.B.PickItems) xy.Add(it.Uid == 500 ? new[] { 10f, 10f } : new[] { 500f, 500f });
            int uid = w.B.MapPick.OnClick(w.B.PickItems, xy, 12f, 12f);
            w.B.OnShipPicked(uid); w.Frame();
            if (w.B.StrikeDraft.RefUid != 500) return "strike map pick: " + w.B.StrikeDraft.RefUid;
            return null;
        }

        private static string SarParts()
        {
            var w = Make(1.0, View());
            w.Mode(2); w.Frame();
            if (w.Has("swhere.0") == false || w.Has("safter.0") == false) return "SAR segments missing";
            var ab = w.P.Find("sar_stop");
            if (ab == null || !ab.Text.Contains(T("asw.btn.stop"))) return "SAR stop button: " + (ab == null ? "none" : ab.Text);
            return null;
        }

        /// <summary>I1: the compass of the bearing remembers its mode (absolute / from the course) in the window state (UiWindowStateStore : IUiWindowValues).</summary>
        private static string CompassMemory()
        {
            var old = UiWindowPrefs.State;
            try
            {
                string saved = null;
                UiWindowPrefs.State = new UiWindowStateStore("", x => saved = x);
                var w = Make(1.0, View());
                w.Mode(1); w.Frame(); w.Click("asrc.1");
                if (!w.Has("brg.mode.1")) return "no mode switch of the compass";
                w.Click("brg.mode.1"); w.Frame();
                if (saved == null || !saved.Contains("compass.brg")) return "the compass mode is not saved: " + saved;
                UiWindowPrefs.State = new UiWindowStateStore(saved, x => saved = x);   // "restart"
                var w2 = Make(1.0, View());
                w2.Mode(1); w2.Frame(); w2.Click("asrc.1"); w2.Frame();
                var rel = w2.P.Find("brg.mode.1");
                if (rel == null || !rel.Selected) return "the compass mode is not restored after a restart";
            }
            finally { UiWindowPrefs.State = old; }
            return null;
        }

        private static string Layout(double s)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            float width = 520f * (float)s;
            foreach (string group in new[] { "ПЛО-1", "ПЛО-2", "SAR-1" })
                foreach (int mode in new[] { 0, 1, 2, 3 })
                {
                    var w = Make(s, View());
                    w.B.SelectedGroup = group; w.Frame(); w.Frame(); w.Mode(mode); w.Frame();
                    string tag = group + " mode " + mode;
                    var lint = w.P.Lint(UiTheme.For(s), true, new[] { "rename" });
                    foreach (string l in lint)
                    {
                        // a table row is 14 px at the floor scale 0.85 (UiKit.Table, not the window): the rule of 15 px is for the controls of the window itself
                        if (l.StartsWith("height", StringComparison.Ordinal) && (l.Contains(": members.") || l.Contains(": avail.") || l.Contains(": sar_ships.") || l.Contains(": strike_targets."))) continue;
                        if (seen.Add(l)) sb.Append(tag + ": lint " + l + "\n");
                    }
                    if (w.K.Overflows.Count > 0) sb.Append(tag + ": overflow " + string.Join("|", w.K.Overflows) + "\n");
                    string r = w.P.CheckLayout(width, (float)(0.85 * 1080 * s));
                    if (r != null) sb.Append(tag + ": " + r + "\n");
                    if (w.P.Of("Field").Count > 0) sb.Append(tag + ": a text field\n");
                    if (mode == 0 || mode == 3)
                    {   // an open list does not break the layout
                        string id = mode == 0 ? "tgt" : "ref";
                        w.Click(id); w.Frame();
                        r = w.P.CheckLayout(width, (float)(0.9 * 1080 * s));
                        if (r != null) sb.Append(tag + " list open: " + r + "\n");
                    }
                }
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}
