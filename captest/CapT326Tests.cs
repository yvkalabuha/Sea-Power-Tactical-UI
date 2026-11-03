using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-326: rename of a CAP group (cap_group op=rename): claims, creation number, loans of an alert group, automatic names, save, errors.
    internal static class CapT326Tests
    {
        private static CapT179Tests.Chk NewChk() { return new CapT179Tests.Chk(); }
        private static Dictionary<string, object> J(string json) { return CapT179Tests.J(json); }
        private static string Ren(CapT179Tests.Rig r, string group, string name) { return r.C.Group(J("{\"op\":\"rename\",\"group\":\"" + group + "\",\"name\":\"" + name + "\"}")); }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("rename") && (e = Rename()) != null) return "rename: " + e;
            if (want("claims") && (e = Claims()) != null) return "claims: " + e;
            if (want("taken") && (e = Taken()) != null) return "taken: " + e;
            if (want("auto") && (e = Auto()) != null) return "auto: " + e;
            if (want("lend") && (e = Lend()) != null) return "lend: " + e;
            if (want("save") && (e = Save()) != null) return "save: " + e;
            if (want("waiting") && (e = Waiting()) != null) return "waiting: " + e;
            return null;
        }

        private static CapT179Tests.Rig Two()
        {
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.Create("5,6"); r.Create("1,2");
            return r;
        }

        private static string Rename()
        {
            var k = NewChk();
            var r = Two();
            int seq = r.Own.GroupSeq("cap", "ПВО-1");
            string ack = Ren(r, "ПВО-1", "Альфа");
            k.Is("ack " + ack, ack != null && ack.Contains("\"group\":\"Альфа\"") && ack.Contains("\"old\":\"ПВО-1\""));
            var v = r.View("Альфа");
            k.Is("view by the new name", v != null && !v.AutoName && r.View("ПВО-1") == null);
            k.Is("log", r.G.LogHas("group rename ПВО-1 -> Альфа"));
            k.Is("event", r.Events("cap_group_renamed").Contains("\"new\":\"Альфа\"") && r.Events("cap_group_renamed").Contains("\"old\":\"ПВО-1\""));
            k.Is("seq kept", r.Own.GroupSeq("cap", "Альфа") == seq);
            k.Is("state json", r.C.StateJson().Contains("\"auto_name\":false") && r.C.StateJson().Contains("\"auto_name\":true"));
            k.Is("same name: ok, no event", Ren(r, "Альфа", "Альфа").Contains("\"ok\":true") && r.G.EventCount("cap_group_renamed") == 1);
            k.Is("unknown group", Ren(r, "ПВО-1", "Бета") == "!unknown_group");
            k.Is("missing name", r.C.Group(J("{\"op\":\"rename\",\"group\":\"Альфа\"}")) == "!missing_name");
            return k.Err;
        }

        private static string Claims()
        {
            var k = NewChk();
            var r = Two();
            r.Patrol("ПВО-1", 2, 0);
            Ren(r, "ПВО-1", "Альфа");
            string w;
            k.Is("owner is the new name", r.Own.Owner(5, out w) == "Альфа" && w == "cap");
            string add = r.C.Group(J("{\"op\":\"add\",\"group\":\"Альфа\",\"uids\":[3]}"));
            k.Is("add to the renamed group " + add, add != null && add.Contains("\"added\":[3]"));
            string o = r.Patrol("Альфа", 2, 0);
            k.Is("order by the new name " + o, o != null && !o.StartsWith("!"));
            k.Is("order by the old name", r.Patrol("ПВО-1", 2, 0) == "!unknown_group");
            k.Is("stop by the old name", r.C.Stop(J("{\"group\":\"ПВО-1\"}")) == "!unknown_group");
            string st = r.C.Stop(J("{\"group\":\"Альфа\"}"));
            k.Is("stop by the new name " + st, st != null && !st.StartsWith("!"));
            return k.Err;
        }

        private static string Taken()
        {
            var k = NewChk();
            var r = Two();
            k.Is("taken", Ren(r, "ПВО-1", "ПВО-2") == "!name_taken");
            k.Is("too long", Ren(r, "ПВО-1", new string('x', 25)) == "!bad_name");
            k.Is("blank is not bad: it means auto", Ren(r, "ПВО-1", "   ").Contains("\"ok\":true"));
            int ev = r.G.EventCount("cap_group_renamed");
            k.Is("own name: ok", Ren(r, "ПВО-1", "ПВО-1").Contains("\"ok\":true") && r.G.EventCount("cap_group_renamed") == ev);
            return k.Err;
        }

        private static string Auto()
        {
            var k = NewChk();
            var r = Two();
            Ren(r, "ПВО-1", "Альфа");
            string a = Ren(r, "Альфа", "");
            k.Is("auto name " + a, a != null && a.Contains("\"group\":\"ПВО-3\"") && a.Contains("\"old\":\"Альфа\""));
            k.Is("auto flag", r.View("ПВО-3") != null && r.View("ПВО-3").AutoName && r.View("Альфа") == null);
            int ev = r.G.EventCount("cap_group_renamed");
            string b = Ren(r, "ПВО-3", "");
            k.Is("auto group: nothing changes", b != null && b.Contains("\"group\":\"ПВО-3\"") && r.G.EventCount("cap_group_renamed") == ev);
            return k.Err;
        }

        private static string Lend()
        {
            var k = NewChk();
            for (int pass = 0; pass < 2; pass++)
            {
                var r = new CapT179Tests.Rig(null, "Cheater");
                r.Create("5,6"); r.Patrol("ПВО-1", 2, 0);
                r.Create("1,2,3"); r.Alert("ПВО-2", ",\"pursuit_nm\":100");
                r.Step(900);
                r.G.Contacts.Add(CapT179Tests.Raid(50, 70));
                r.Step(60);
                r.G.Bird(5).ReserveNm = 10;
                r.Step(5);
                r.G.Contacts.Clear();
                r.Step(125);
                int lent = r.G.LogHas("alert clear uid=1 group=ПВО-2 -> lend") ? 1 : 2;
                if (!r.G.LogHas("-> lend to=ПВО-1")) return "setup: no loan";
                if (pass == 0)
                {
                    Ren(r, "ПВО-2", "Дежурная");
                    var m = r.Member("ПВО-1", lent);
                    k.Is("LentFrom follows the alert group", m != null && m.LentFrom == "Дежурная");
                    r.G.Bird(lent).ReserveNm = 10;
                    r.Step(900);
                    k.Is("the bird goes back to the renamed alert group", r.G.LogHas("alert back uid=" + lent + " from=ПВО-1 to=Дежурная"));
                }
                else
                {
                    Ren(r, "ПВО-1", "Патруль");
                    var av = r.View("ПВО-2");
                    k.Is("shadow shows the new patrol name", av != null && av.Lent.Count == 1 && av.Lent[0].Value == "Патруль");
                    r.G.Bird(lent).ReserveNm = 10;
                    r.Step(900);
                    k.Is("back from the renamed patrol", r.G.LogHas("alert back uid=" + lent + " from=Патруль to=ПВО-2"));
                }
                if (k.Err != null) return k.Err + " pass " + pass;
            }
            return k.Err;
        }

        private static string Save()
        {
            var k = NewChk();
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.Create("5"); r.Create("6"); r.Create("1");
            Ren(r, "ПВО-1", "Конвой-9");
            Ren(r, "ПВО-3", "Тяжёлая");
            var d = r.C.Save();
            string err; var back = CapSave.Parse(CapSave.ToJson(d), out err);
            var r2 = new CapT179Tests.Rig(null, "Cheater");
            r2.C.Load(back, r2.G.Now);
            k.Is("names and flags " + err, r2.View("Конвой-9") != null && !r2.View("Конвой-9").AutoName && r2.View("ПВО-2") != null && r2.View("ПВО-2").AutoName && r2.View("Тяжёлая") != null);
            k.Is("counter ignores manual names", r2.Create("4") != null && r2.View("ПВО-3") != null);
            // an old file: no auto_name, the flag comes from the name
            var old = CapSave.Parse("{\"format\":3,\"groups\":[{\"group\":\"ПВО-4\",\"seq\":1,\"members\":[]},{\"group\":\"Свои\",\"seq\":2,\"members\":[]}]}", out err);
            var r3 = new CapT179Tests.Rig(null, "Cheater");
            r3.C.Load(old, r3.G.Now);
            k.Is("old file: derived flag", r3.View("ПВО-4") != null && r3.View("ПВО-4").AutoName && r3.View("Свои") != null && !r3.View("Свои").AutoName);
            return k.Err;
        }

        private static string Waiting()
        {
            var k = NewChk();
            var r = Two();
            typeof(CapController).GetField("_deferred", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(r.C, new CapSaveData());
            k.Is("restore_waiting", Ren(r, "ПВО-1", "Альфа") == "!restore_waiting");
            return k.Err;
        }
    }
}
