using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-179 / ADR-0079: the CAP "Alert" group kind (a group of its own with its own settings). Scenarios of `captest t179 <name>`:
    // kind-state, alert-group, alert-hangar, alert-fill, kind-change, scramble-now, migrate, undo; alert-lend and alert-lend-off are in CapT179LendTests.
    internal static class CapT179Tests
    {
        internal const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";   // 20 x 40 nm, 75 nm from the deck

        internal sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        internal sealed class FakeNotes : IAirAlertNotes
        {
            public readonly List<string> Lines = new List<string>();
            public readonly Dictionary<int, string> Keys = new Dictionary<int, string>();   // T-197: track id -> AirGuard raid key
            public bool KeysOn = true;    // T-201: false = AirGuard Mode=off
            public bool AutoKey = true;   // like the real service: a track without a shared key is its own raid "r<id>"
            public bool RaidKeysOn { get { return KeysOn; } }
            public string RaidKeyOf(int trackId) { if (!KeysOn) return null; string k; return Keys.TryGetValue(trackId, out k) ? k : AutoKey ? "r" + trackId : null; }
            public void AlertLine(string group, IList<int> trackIds, string action, int count, string typeLabel)
            {
                Lines.Add(group + "|" + string.Join(",", trackIds) + "|" + action + "|" + count + "|" + typeLabel);
            }
        }

        internal sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P;
            public CapController C;
            public FakeNotes Notes = new FakeNotes();
            public static readonly List<Rig> All = new List<Rig>();
            public Rig(Action<CapParams> cfg = null, string mode = "Realistic")
            {
                All.Add(this);
                P = new CapParams();
                if (cfg != null) cfg(P);
                C = new CapController(G, P, Own);
                C.AlertNotes = Notes;
                G.Mode = mode;
                G.ExtraLoadouts["F-14A"] = F14(30, 6);
            }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public string Create(string uids) { return C.Group(J("{\"op\":\"create\",\"uids\":[" + uids + "]}")); }
            public string Patrol(string group, int n, int k, string extra = "") { return C.Order(J("{\"group\":\"" + group + "\"," + ZA + ",\"on_station\":" + n + ",\"ready\":" + k + extra + "}")); }
            public string Alert(string group, string extra = "") { return C.Order(J("{\"group\":\"" + group + "\",\"kind\":\"alert\",\"ref_uid\":" + CapFakeGame.ShipUid + extra + "}")); }
            public int Jn(string prefix) { int n = 0; foreach (string j in G.Journal) if (j.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
            public int Airborne(params int[] uids) { int n = 0; foreach (int u in uids) { var b = G.Bird(u); if (b != null && (b.State == "Air" || b.State == "Taxi")) n++; } return n; }
            public CapGroupView View(string group) { foreach (var g in C.View().Groups) if (g.Line.Group == group) return g; return null; }
            public CapMemberView Member(string group, int uid) { var g = View(group); if (g != null) foreach (var m in g.Members) if (m.Uid == uid) return m; return null; }
            public string Events(string name) { string s = ""; foreach (var e in G.Events) if (e.Key == name) s += e.Value; return s; }
        }

        internal static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        internal static List<LoadoutOption> F14(double longMin, double interceptMin)
        {
            Func<string, double, int, LoadoutOption> lo = (n, m, c) => new LoadoutOption { Name = n, Display = n, Capacity = 8, Available = true, ReadyUpMin = m, Weapons = new Dictionary<string, int> { { n == "AirToAirLongRange" ? "AIM-54" : "AIM-7", c }, { "AIM-9", 2 } } };
            return new List<LoadoutOption> { lo("AirToAirLongRange", longMin, 4), lo("AirToAirIntercept", interceptMin, 4) };
        }

        internal static CapContact Raid(int id, double northOfShipNm, double eastNm = 0)
        {
            return new CapContact { Id = id, Lat = 10.0 + northOfShipNm / 60.0, Lon = 11.0 + eastNm / 60.0, Hostile = true, CourseDeg = 180, SpeedKn = 500 };
        }

        /// <summary>CAPT179_DUMP=1: the log of the last rig of a failed scenario (debugging aid).</summary>
        public static string Run(string only = null)
        {
            string res = RunInner(only);
            if (Environment.GetEnvironmentVariable("CAPT179_DUMP") == "1" && Rig.All.Count > 0)
            {
                var last = Rig.All[Rig.All.Count - 1];
                foreach (string l in last.G.Logs) Console.WriteLine(l);
            }
            return res;
        }

        private static string RunInner(string only)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("kind-state") && (e = KindState()) != null) return "kind-state: " + e;
            if (want("alert-group") && (e = AlertGroup()) != null) return "alert-group: " + e;
            if (want("alert-hangar") && (e = AlertHangar()) != null) return "alert-hangar: " + e;
            if (want("alert-fill") && (e = AlertFill()) != null) return "alert-fill: " + e;
            if (want("kind-change") && (e = KindChange()) != null) return "kind-change: " + e;
            if (want("scramble-now") && (e = ScrambleNow()) != null) return "scramble-now: " + e;
            if (want("migrate") && (e = Migrate()) != null) return "migrate: " + e;
            if (want("undo") && (e = Undo()) != null) return "undo: " + e;
            if (only == null || only.StartsWith("alert-lend", StringComparison.Ordinal)) return CapT179LendTests.Run(only);
            return Array.IndexOf(new[] { "kind-state", "alert-group", "alert-hangar", "alert-fill", "kind-change", "scramble-now", "migrate", "undo" }, only) >= 0 ? null : "unknown scenario";
        }

        // an alert order: no zone, the settings in cap_state and in the view
        private static string KindState()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("1,2,3");
            string a = r.Alert("ПВО-1", ",\"ready\":3,\"per_raid\":1,\"radius_nm\":100,\"targets\":[\"air\",\"helo\"],\"after\":\"alert\"");
            k.Is("order ok " + a, a != null && a.Contains("\"ok\":true") && a.Contains("\"kind\":\"alert\""));
            var v = r.View("ПВО-1");
            k.Is("view: alert kind", v != null && v.Kind == "alert" && v.AlertSpec != null && v.Ordered && v.Rect == null);
            k.Is("view: settings", v.AlertSpec.Ready == 3 && v.AlertSpec.PerRaid == 1 && v.AlertSpec.RadiusNm == 100 && v.AlertSpec.After == "alert" && v.AlertSpec.Targets == (AlertTargets.Air | AlertTargets.Helo) && v.AlertSpec.RefUid == CapFakeGame.ShipUid);
            k.Is("view: all members are alert machines", v.Members.Count == 3 && v.Members[0].Alert && v.Members[2].Alert);
            string js = r.C.StateJson();
            k.Is("cap_state: kind + alert block", js.Contains("\"kind\":\"alert\"") && js.Contains("\"alert\":{\"ref\":200,\"ready\":3,\"per_raid\":1,\"level\":\"deck\",\"radius_nm\":100,\"targets\":[\"air\",\"helo\"]") && js.Contains("\"ready_now\":") && js.Contains("\"lent\":[]"));
            // a second order keeps what it does not name
            r.Alert("ПВО-1", ",\"radius_nm\":60");
            v = r.View("ПВО-1");
            k.Is("partial order keeps the rest", v.AlertSpec.RadiusNm == 60 && v.AlertSpec.Ready == 3 && v.AlertSpec.After == "alert");
            // a patrol group: kind patrol, accept_alert
            var q = new Rig();
            q.Create("5,6"); q.Patrol("ПВО-1", 1, 1, ",\"accept_alert\":false");
            var pv = q.View("ПВО-1");
            k.Is("patrol view", pv.Kind == "patrol" && !pv.AcceptAlert && pv.AlertSpec == null);
            k.Is("cap_state patrol: accept_alert", q.C.StateJson().Contains("\"accept_alert\":false") && q.C.StateJson().Contains("\"kind\":\"patrol\""));
            // no alert group at all: a raid sends nobody but the patrol (D8)
            var none = new Rig(null, "Cheater");
            none.Create("1,2,3"); none.Patrol("ПВО-1", 1, 0);
            none.G.Contacts.Add(Raid(50, 60));
            none.Step(2);
            k.Is("no alert group: no alert scramble", none.Notes.Lines.Count == 0 && !none.Events("cap_alert").Contains("scramble"));
            k.Is("the group of the service: only alert groups", q.C.AirGuardGroups().Count == 0 && r.C.AirGuardGroups().Count == 1 && r.C.AirGuardGroups()[0].Kind == "cap_alert" && r.C.AirGuardGroups()[0].Window == "cap");
            return k.Err;
        }

        // ПВО-2 "Alert" (2 of 3 machines ready, launch not allowed) next to the patrol ПВО-1: the radius, the second raid with nobody ready, the patrol untouched
        private static string AlertGroup()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("5,6"); r.Patrol("ПВО-1", 1, 1);
            r.Create("1,2,3");
            string a = r.Alert("ПВО-2", ",\"pursuit_nm\":100");
            k.Is("order ok " + a, a != null && a.Contains("\"ok\":true"));
            r.Step(5);
            int alertOrders = 0; foreach (string j in r.G.Journal) if (j.StartsWith("order 100 F-14A", StringComparison.Ordinal) && j.EndsWith(" hold", StringComparison.Ordinal)) alertOrders++;
            k.Is("two machines prepared with the fastest loadout, launch not allowed " + alertOrders, alertOrders == 2);
            r.Step(2400);
            k.Is("they stand without a raid", r.Airborne(1, 2, 3) == 0);
            var v = r.View("ПВО-2");
            k.Is("ready_now 2", v.ReadyNow == 2 && v.ScrambleBlock == null);
            k.Is("patrol on station meanwhile", r.G.Bird(5).State == "Air" && r.View("ПВО-1").Kind == "patrol");
            // a raid at 85 nm: nothing; at 78: two go
            r.G.Contacts.Add(Raid(50, 85));
            r.Step(5);
            k.Is("raid beyond the radius: nobody goes", r.Airborne(1, 2, 3) == 0);
            r.G.Contacts.Clear(); r.G.Contacts.Add(Raid(50, 78));
            r.Step(2);
            k.Is("scramble event", r.Events("cap_alert").Contains("\"op\":\"scramble\""));
            k.Is("card line scramble 2", r.Notes.Lines.Count == 1 && r.Notes.Lines[0].StartsWith("ПВО-2|50|scramble|2|") );
            k.Is("deck tier on while they wait", r.G.LogHas("deck priority on group=ПВО-2"));
            // the second raid right away: the third machine is being prepared (it counts, it goes when its preparation is over)
            r.G.Contacts.Add(Raid(60, 60, 40));
            r.Step(1);
            k.Is("second raid: the next machine goes", r.Notes.Lines.Count == 2 && r.Notes.Lines[1].StartsWith("ПВО-2|60|scramble|1|"));
            r.Step(40);
            k.Is("deck tier off after the take-off", r.G.LogHas("deck priority off group=ПВО-2"));
            k.Is("guided to the raid", r.G.Patrols >= 2);
            r.Step(600);
            k.Is("all three are airborne", r.Airborne(1, 2, 3) == 3);
            // a third raid: nobody is left
            r.G.Contacts.Add(Raid(70, 50, -40));
            r.Step(2);
            k.Is("third raid: nobody ready -> not_ready + card line", r.Events("cap_warn").Contains("alert_not_ready") && r.Notes.Lines.Count == 3 && r.Notes.Lines[2].Contains("|not_ready|"));
            // one warning per raid
            r.Step(5);
            k.Is("once per raid", r.Notes.Lines.Count == 3);
            k.Is("the patrol group keeps its own bird", r.View("ПВО-1").Members.Exists(m => m.Uid == 5) && !r.View("ПВО-1").Members.Exists(m => m.Alert));
            return k.Err;
        }

        // level hangar: nothing is prepared on the deck; a raid orders the launch (allow) at once
        private static string AlertHangar()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("1,2,3");
            r.Alert("ПВО-1", ",\"level\":\"hangar\"");
            r.Step(30);
            int orders = 0; foreach (string j in r.G.Journal) if (j.StartsWith("order 100 F-14A", StringComparison.Ordinal)) orders++;
            k.Is("no request before the raid", orders == 0);
            k.Is("ready_now 2 from the hangar", r.View("ПВО-1").ReadyNow == 2);
            r.G.Contacts.Add(Raid(50, 70));
            r.Step(2);
            int allowOrders = 0; foreach (string j in r.G.Journal) if (j.StartsWith("order 100 F-14A", StringComparison.Ordinal) && j.EndsWith(" allow", StringComparison.Ordinal)) allowOrders++;
            k.Is("raid: two orders with the launch allowed " + allowOrders, allowOrders == 2);
            k.Is("scramble event + log", r.Events("cap_alert").Contains("\"op\":\"scramble\"") && r.G.LogHas("(level hangar)"));
            r.Step(30);
            k.Is("not yet airborne: the hangar readiness takes time", r.Airborne(1, 2, 3) == 0);
            r.Step(400);
            k.Is("then they take off", r.Airborne(1, 2, 3) == 2);
            // a deck level group that is switched to hangar drops its prepared tasks
            var q = new Rig();
            q.Create("1,2,3"); q.Alert("ПВО-1");
            q.Step(5);
            int prepared = 0; foreach (string j in q.G.Journal) if (j.StartsWith("order 100 F-14A", StringComparison.Ordinal)) prepared++;
            q.Alert("ПВО-1", ",\"level\":\"hangar\"");
            q.Step(3);
            k.Is("deck -> hangar: the prepared tasks go " + prepared + "/" + q.Jn("abort "), prepared == 2 && q.Jn("abort ") == 2);
            return k.Err;
        }

        // "Fill from deck": one standing of two -> a free machine of the deck is taken; the stop releases it
        private static string AlertFill()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("1");
            r.Alert("ПВО-1", ",\"ready\":2,\"fill\":true");
            r.Step(3);
            var v = r.View("ПВО-1");
            k.Is("a free machine is taken", v.Members.Count == 2 && r.G.LogHas("[Cap] alert fill uid="));
            bool auto = false; foreach (var m in v.Members) if (m.AutoFill) auto = true;
            k.Is("marked auto", auto);
            // the taken machine is owned by the group
            string w; var taken = v.Members.Find(m => m.AutoFill);
            k.Is("owner is the group", taken != null && r.Own.Owner(taken.Uid, out w) == "ПВО-1");
            r.C.Stop(J("{\"group\":\"ПВО-1\"}"));
            r.Step(2);
            v = r.View("ПВО-1");
            k.Is("stop releases it", v.Members.Count == 1 && r.G.LogHas("alert unfill uid="));
            // fill off: nothing is taken
            var q = new Rig();
            q.Create("1"); q.Alert("ПВО-1", ",\"ready\":2");
            q.Step(3);
            k.Is("no fill by default", q.View("ПВО-1").Members.Count == 1);
            // the flag switched off releases the taken one
            var f = new Rig();
            f.Create("1"); f.Alert("ПВО-1", ",\"ready\":2,\"fill\":true");
            f.Step(3);
            k.Is("taken", f.View("ПВО-1").Members.Count == 2);
            f.Alert("ПВО-1", ",\"fill\":false");
            f.Step(3);
            k.Is("fill off: released", f.View("ПВО-1").Members.Count == 1);
            // a machine of another group or a foreign owner is never taken
            var o = new Rig();
            o.Create("1"); o.Create("2,3,4");
            o.Alert("ПВО-1", ",\"ready\":2,\"fill\":true");
            o.Step(3);
            k.Is("owned machines stay where they are", o.View("ПВО-1").Members.Count == 1 || o.View("ПВО-1").Members.TrueForAll(m => m.Uid == 1 || m.Uid >= 5));
            return k.Err;
        }

        // patrol -> alert: airborne birds fly home on hold, then become alert machines; alert -> patrol: a guided bird finishes the raid and takes a slot
        private static string KindChange()
        {
            var k = new Chk();
            var r = new Rig(null, "Cheater");
            r.Create("1,2,3,4");
            r.Patrol("ПВО-1", 2, 0);
            r.Step(900);
            k.Is("two on station", r.Airborne(1, 2, 3, 4) == 2);
            string a = r.Alert("ПВО-1", ",\"pursuit_nm\":100");
            k.Is("one command changes the kind " + a, a != null && a.Contains("\"kind\":\"alert\""));
            k.Is("journal kind", r.G.LogHas("[Cap] kind group=ПВО-1 patrol->alert"));
            k.Is("leave kind_change", r.G.LogHas("reason=kind_change") && r.G.EventCount("cap_rtb") >= 2);
            bool hold = true; foreach (var b in r.G.All) if (b.State == "Air" && b.Roe != "hold") hold = false;
            k.Is("weapons hold on the way home", hold);
            var v = r.View("ПВО-1");
            k.Is("alert kind without a zone", v.Kind == "alert" && v.Rect == null);
            r.Step(900);
            k.Is("all landed and prepared as alert machines", r.Airborne(1, 2, 3, 4) == 0 && r.View("ПВО-1").ReadyNow == 2);
            // alert -> patrol with a bird in a raid guidance
            r.G.Contacts.Add(Raid(50, 70));
            r.Step(4);
            k.Is("scramble", r.Jn("allow ") >= 2);
            r.Step(40);
            int air = r.Airborne(1, 2, 3, 4);
            k.Is("two airborne", air == 2);
            r.Patrol("ПВО-1", 4, 0);
            k.Is("journal kind back", r.G.LogHas("[Cap] kind group=ПВО-1 alert->patrol"));
            k.Is("guided birds are not recalled", r.G.EventCount("cap_rtb") == 2 + 0 || r.Events("cap_rtb").Split(new[] { "kind_change" }, StringSplitOptions.None).Length - 1 == 2);
            r.G.Contacts.Clear();
            r.Step(125);
            k.Is("the raid is over: the birds join the patrol", r.G.LogHas("-> patrol slot=2 (kind change)") && r.G.LogHas("-> patrol slot=3 (kind change)"));
            r.Step(1200);
            var pv = r.View("ПВО-1");
            int zone = 0; foreach (var m in pv.Members) if (m.Where == "zone" && !m.Alert) zone++;
            k.Is("a patrol group again, four on station " + zone, pv.Kind == "patrol" && zone == 4);
            return k.Err;
        }

        // cap_scramble: the ready machines take off without a raid, circle over the ship and go home after ReturnFarS
        private static string ScrambleNow()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("1,2,3"); r.Create("5,6");
            k.Is("not an alert group", r.C.ScrambleNow(J("{\"group\":\"ПВО-2\"}")) == "!not_alert");
            k.Is("unknown group", r.C.ScrambleNow(J("{\"group\":\"X\"}")) == "!unknown_group");
            k.Is("missing group", r.C.ScrambleNow(J("{}")) == "!missing_group");
            r.Alert("ПВО-1", ",\"after\":\"alert\"");
            k.Is("nothing ready yet", r.C.ScrambleNow(J("{\"group\":\"ПВО-1\"}")) == "!no_ready" && r.View("ПВО-1").ScrambleBlock == "no_ready");
            r.Step(900);
            k.Is("button open", r.View("ПВО-1").ScrambleBlock == null);
            string a = r.C.ScrambleNow(J("{\"group\":\"ПВО-1\"}"));
            k.Is("ok " + a, a != null && a.Contains("\"ok\":true") && a.Contains("\"n\":2"));
            r.Step(60);
            k.Is("two airborne over the ship", r.Airborne(1, 2, 3) == 2 && r.G.Patrols >= 2);
            double d = 99; foreach (int u in new[] { 1, 2, 3 }) { var b = r.G.Bird(u); if (b != null && b.State == "Air") d = Math.Min(d, AswMath.DistNm(new GeoPt(b.Lat, b.Lon), r.G.Ship.Value)); }
            r.Step(300);
            k.Is("not sent home before ReturnFarS on the circle", r.G.EventCount("cap_rtb") == 0 || r.G.LogHas("alert clear"));
            r.Step(200);
            k.Is("cleared: rtb_alert after the barrage", r.G.LogHas("-> rtb_alert why=after_alert") && r.G.EventCount("cap_rtb") >= 2);
            r.Step(900);
            k.Is("stand ready again", r.View("ПВО-1").ReadyNow == 2 && r.Airborne(1, 2, 3) == 0);
            // a raid appears while they circle: the barrage birds take it
            var q = new Rig();
            q.Create("1,2,3"); q.Alert("ПВО-1", ",\"per_raid\":1");
            q.Step(900);
            q.C.ScrambleNow(J("{\"group\":\"ПВО-1\"}"));
            q.Step(60);
            q.G.Contacts.Add(Raid(50, 60));
            q.Step(3);
            k.Is("a barrage bird takes the raid", q.G.LogHas("(from the barrage)") && q.Notes.Lines.Count == 1);
            return k.Err;
        }

        private static string Migrate()
        {
            var k = new Chk();
            string err;
            string f1 = "{\"format\":1,\"mission_time_s\":1000,\"groups\":["
                + "{\"group\":\"ПВО-1\",\"seq\":1,\"on_station\":2,\"ready\":0,\"roe\":\"tight\",\"alert\":2,\"alert_ref\":200,\"rect\":[],\"anchor\":\"geo\",\"members\":[{\"uid\":1,\"type\":\"F-14A\",\"slot\":-1},{\"uid\":2,\"type\":\"F-14A\",\"slot\":-1},{\"uid\":3,\"type\":\"F-14A\",\"slot\":-1}]},"
                + "{\"group\":\"ПВО-2\",\"seq\":2,\"on_station\":1,\"ready\":0,\"roe\":\"tight\",\"alert\":2,\"alert_ref\":200,\"rect\":[10.5,11,10.83333,11.66667],\"anchor\":\"geo\",\"members\":[{\"uid\":5,\"type\":\"F-15C\",\"slot\":-1},{\"uid\":6,\"type\":\"F-15C\",\"slot\":-1}]}],\"memory\":[]}";
            var d = CapSave.Parse(f1, out err);
            k.Is("format 1 reads " + err, d != null && err == null && d.Groups.Count == 2);
            var r = new Rig();
            r.C.Load(d, r.G.Now);
            var a = r.View("ПВО-1"); var b = r.View("ПВО-2");
            k.Is("no zone + alert -> alert group", a != null && a.Kind == "alert" && a.AlertSpec.Ready == 2 && a.AlertSpec.RefUid == 200 && a.Ordered && a.Members.Count == 3 && a.Members[0].Alert);
            k.Is("with a zone -> patrol without the alert", b != null && b.Kind == "patrol" && b.Ordered && b.Line.Warns.Contains("alert_migrated"));
            k.Is("journal", r.G.LogHas("load migrate group=ПВО-1 alert=2 -> alert_group") && r.G.LogHas("load migrate group=ПВО-2 -> dropped"));
            k.Is("warning event", r.Events("cap_warn").Contains("alert_migrated"));
            // any new order of the group clears the warning
            r.Patrol("ПВО-2", 1, 0);
            k.Is("warning cleared by an order", !r.View("ПВО-2").Line.Warns.Contains("alert_migrated"));
            // the migrated alert group works
            r.Step(5);
            k.Is("migrated group prepares its machines", r.View("ПВО-1").Members.Count == 3 && r.Jn("order 100 F-14A") == 2);
            // format round trip (T-186: format 3)
            var s = r.C.Save();
            string json = CapSave.ToJson(s);
            var back = CapSave.Parse(json, out err);
            k.Is("format round trip " + err, back != null && back.Format == CapSave.CurrentFormat && back.Groups[0].Kind == "alert" && back.Groups[0].AlertSpec.RefUid == 200 && back.Groups[1].Kind == "patrol");
            var r2 = new Rig();
            r2.C.Load(back, r2.G.Now);
            var a2 = r2.View("ПВО-1");
            k.Is("loaded: alert group with its settings", a2.Kind == "alert" && a2.AlertSpec.Same(r.View("ПВО-1").AlertSpec) && a2.Members.Count == 3);
            // an alert group that is stopped is not saved as an alert group
            r.C.Stop(J("{\"group\":\"ПВО-1\"}"));
            k.Is("stopped group is saved as patrol", r.C.Save().Groups[0].Kind == "patrol");
            return k.Err;
        }

        // "Stop all" and the undo: both groups come back with their kind and settings (the commands the window builds from the snapshot)
        private static string Undo()
        {
            var k = new Chk();
            var r = new Rig();
            r.Create("5,6"); r.Patrol("ПВО-1", 1, 1, ",\"accept_alert\":false");
            r.Create("1,2,3"); r.Alert("ПВО-2", ",\"ready\":3,\"per_raid\":1,\"level\":\"hangar\",\"radius_nm\":120,\"targets\":[\"air\",\"unknown\"],\"pursuit_nm\":80,\"speed\":\"cruise\",\"roe\":\"hold\",\"after\":\"alert\",\"fill\":true");
            r.Step(5);
            var snap = new List<CapGroupView>(r.C.View().Groups);
            var specBefore = r.View("ПВО-2").AlertSpec.Clone();
            r.C.Stop(J("{\"all\":true}"));
            r.Step(3);
            k.Is("both stopped", !r.View("ПВО-1").Ordered && !r.View("ПВО-2").Ordered);
            foreach (var g in snap)
            {
                if (g.Kind == "alert")
                {
                    var s = g.AlertSpec;
                    string cmd = "{\"group\":\"" + g.Line.Group + "\",\"kind\":\"alert\",\"ref_uid\":" + s.RefUid + ",\"ready\":" + s.Ready + ",\"per_raid\":" + s.PerRaid + ",\"level\":\"" + s.Level + "\",\"radius_nm\":" + s.RadiusNm
                        + ",\"targets\":\"" + CapAlertMath.TargetsCsv(s.Targets) + "\",\"pursuit_nm\":" + s.PursuitNm + ",\"speed\":\"" + s.Speed + "\",\"roe\":\"" + s.Roe + "\",\"after\":\"" + s.After + "\",\"fill\":" + (s.Fill ? "true" : "false") + "}";
                    k.Is("undo alert " + r.C.Order(J(cmd)), r.View(g.Line.Group).Kind == "alert");
                }
                else
                {
                    string cmd = "{\"group\":\"" + g.Line.Group + "\",\"kind\":\"patrol\",\"rect\":[" + g.Rect[0].ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + g.Rect[1].ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                        + g.Rect[2].ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + g.Rect[3].ToString(System.Globalization.CultureInfo.InvariantCulture) + "],\"on_station\":" + g.N + ",\"ready\":" + g.K + ",\"accept_alert\":" + (g.AcceptAlert ? "true" : "false") + "}";
                    k.Is("undo patrol " + r.C.Order(J(cmd)), r.View(g.Line.Group).Kind == "patrol" && r.View(g.Line.Group).Ordered);
                }
            }
            k.Is("alert settings restored", r.View("ПВО-2").AlertSpec.Same(specBefore));
            k.Is("patrol keeps accept_alert=false", !r.View("ПВО-1").AcceptAlert);
            return k.Err;
        }
    }
}
