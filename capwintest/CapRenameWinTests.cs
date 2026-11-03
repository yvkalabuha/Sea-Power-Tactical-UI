// T-326: the rename field of the CAP window (the "..." button, the name row, OK), ru and en.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static partial class CapWindowTests
    {
        public static string Rename(string only)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            foreach (string lang in new[] { "ru", "en" })
            {
                if (only != null && only != "ru-en" && lang != "ru") continue;
                using (UiText.Push(lang))
                {
                    if (want("buttons") || only == "ru-en") if ((e = RenButtons()) != null) return lang + ": buttons: " + e;
                    if (want("send") || only == "ru-en") if ((e = RenSend()) != null) return lang + ": send: " + e;
                    if (want("taken") || only == "ru-en") if ((e = RenTaken()) != null) return lang + ": taken: " + e;
                    if (want("empty") || only == "ru-en") if ((e = RenEmpty()) != null) return lang + ": empty: " + e;
                    if (want("label") || only == "ru-en") if ((e = RenLabel()) != null) return lang + ": label: " + e;
                }
            }
            return null;
        }

        private static bool RenHas(Rig r, string id) { return r.P.Find(id) != null; }

        // T-335 review: the handle label names the group whose zone is edited, not the group selected later
        private static string RenLabel()
        {
            var v = Data(1);
            var r = new Rig(1.0, 580, v); r.Frame();
            var z = r.B.EditableZone;
            if (z == null) return "no editable order zone";
            if (r.B.EditableGroup != "ПВО-1") return "group of the order zone: " + r.B.EditableGroup;
            if (!r.B.EditableLabel(z).StartsWith("ПВО-1 · ")) return "label of the order zone: " + r.B.EditableLabel(z);
            r.B.BeginEdit();
            v.Groups.RemoveAt(0); r.Frame();   // the selection moves to another group during the drag
            if (r.B.SelectedGroup == "ПВО-1") return "the stand did not move the selection";
            if (!r.B.EditableLabel(z).StartsWith("ПВО-1 · ")) return "label during the drag: " + r.B.EditableLabel(z);
            r.B.EndEdit(false);
            if (r.B.EditableGroup == "ПВО-1") return "the dragged group is kept after the drag";
            // a drawn draft zone: the draft's group
            v = Data(1); r = new Rig(1.0, 580, v); r.Frame();
            r.B.OnRect(new[] { 36.0, 21.0, 36.5, 21.5 }); r.Frame();
            if (r.B.DraftZone == null) return "no draft zone";
            if (r.B.EditableGroup != r.B.SelectedGroup) return "draft group " + r.B.EditableGroup + " vs " + r.B.SelectedGroup;
            return null;
        }

        private static string RenButtons()
        {
            var r = new Rig(1.0, 580, new CapView()); r.Frame();
            if (RenHas(r, "rename") || RenHas(r, "rename_ok")) return "the row is shown without groups";
            if (!RenHas(r, "grp_rename") || r.P.Find("grp_rename").Enabled) return "the button must be grey without a group";
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            if (!RenHas(r, "grp_rename") || !r.P.Find("grp_rename").Enabled) return "the button must be live with a group";
            if (RenHas(r, "rename") || RenHas(r, "rename_ok")) return "the row is shown before the button";
            r.Click("grp_rename"); r.Frame();
            if (!RenHas(r, "rename") || !RenHas(r, "rename_ok")) return "the button does not open the row";
            if (r.P.FindText(UiText.Get("cap.lbl.rename")) == null) return "no label";
            r.Click("grp_rename"); r.Frame();
            if (RenHas(r, "rename")) return "the button does not close the row";
            return null;
        }

        private static string RenSend()
        {
            string e;
            var v = Data(1);
            var r = new Rig(1.0, 580, v); r.E.Ship = 7; r.Frame();
            string old = v.Groups[0].Line.Group;
            r.Click("cap.anchor");
            r.Click("apply");
            if ((e = Expect(r, "first order", "cap_order")) != null) return e;
            if (!r.Out[0].Args.ContainsKey("anchor_uid")) return "no anchor in the first order";
            r.Out.Clear();
            v.Groups[0].Anchor = "unit"; r.E.Ship = 0;   // the controller anchored the zone; no ship is selected any more
            r.Frame();
            r.Ack = n => n == "cap_group" ? "{\"ok\":true,\"group\":\"Альфа\",\"old\":\"" + old + "\"}" : "{\"ok\":true}";
            r.Click("grp_rename"); r.Frame();
            r.P.Type("rename", "Альфа"); r.Frame();
            if (!r.P.Find("rename_ok").Enabled) return "OK is grey after typing";
            r.Click("rename_ok");
            if ((e = Expect(r, "rename", "cap_group")) != null) return e;
            var a = r.Out[0].Args;
            if (a["op"] as string != "rename" || a["group"] as string != old || a["name"] as string != "Альфа") return "rename args";
            // the controller renames the group: the view follows
            v.Groups[0].Line.Group = "Альфа"; v.Groups[0].AutoName = false;
            r.Frame();
            if (r.B.SelectedGroup != "Альфа") return "selection lost: " + r.B.SelectedGroup;
            if (RenHas(r, "rename")) return "the row stays open";
            r.Out.Clear();
            r.Click("cap.roe.1");
            r.Click("apply");
            if ((e = Expect(r, "order after the rename", "cap_order")) != null) return e;
            if (r.Out[0].Args["group"] as string != "Альфа") return "order group " + r.Out[0].Args["group"];
            object au;
            if (!r.Out[0].Args.TryGetValue("anchor_uid", out au) || (double)au != 7) return "anchor lost after the rename";
            return null;
        }

        private static string RenTaken()
        {
            var v = Data(1);
            var r = new Rig(1.0, 580, v); r.Frame();
            r.Ack = n => "!name_taken";
            r.Click("grp_rename"); r.Frame();
            r.P.Type("rename", "ПВО-2"); r.Frame();
            r.Click("rename_ok");
            string want = UiText.Get("cap.ui.err.name_taken");
            if (r.B.Note == null || !r.B.Note.Contains(want)) return "note: " + r.B.Note;
            if (r.B.NoteTone != UiTone.Error) return "note tone " + r.B.NoteTone;
            if (r.B.SelectedGroup != "ПВО-1") return "selection changed to " + r.B.SelectedGroup;
            return null;
        }

        private static string RenEmpty()
        {
            // an automatic name: the empty field gives nothing to do
            var v = Data(1);
            var r = new Rig(1.0, 580, v); r.Frame();
            r.Click("grp_rename"); r.Frame();
            r.P.Type("rename", ""); r.Frame();
            if (r.P.Find("rename_ok").Enabled) return "OK is live for an automatic name";
            // a manual name: the empty field returns the automatic one
            var w = Data(1); w.Groups[0].AutoName = false; w.Groups[0].Line.Group = "Альфа";
            r = new Rig(1.0, 580, w);
            r.Ack = n => "{\"ok\":true,\"group\":\"ПВО-4\",\"old\":\"Альфа\"}";
            r.Frame();
            r.Click("grp_rename"); r.Frame();
            r.P.Type("rename", ""); r.Frame();
            if (!r.P.Find("rename_ok").Enabled) return "OK is grey for a manual name";
            r.Click("rename_ok");
            if (r.Out.Count != 1 || r.Out[0].Args["name"] as string != "") return "empty name not sent";
            return null;
        }
    }
}
