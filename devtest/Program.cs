using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using VoiceBotBridge;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge.Dev
{
    internal static class Program
    {
        private static int Main(string[] argv)
        {
            string mode = argv.Length > 0 ? argv[0] : "selftest";
            if (mode == "table") { Console.WriteLine(DevCommandTable.TableJson()); return 0; }
            if (mode == "serve") return Serve(argv);
            if (mode != "selftest") { Console.Error.WriteLine("usage: devtest selftest|table|serve --dir D [--max-clients N] [--fire off] [--force off] [--fail cmd=code] [--no-slots] [--no-diag] [--stall] [--idle-s N] [--timeout-ms N]"); return 2; }
            int bad = 0;
            bad += Run("DevProtocol", ProtocolTest());
            bad += Run("DevCommands", DevCommandTable.SelfTest() ?? CommandsTest());
            bad += Run("DevLogRing", DevLogRing.SelfTest());
            bad += Run("DevExecutor", ExecutorTest());
            bad += Run("DevFire", FireTest());
            bad += Run("DevPump", PumpTest());
            bad += Run("DevCmdLog", DevCmdLog.SelfTest() ?? PumpLogTest());
            bad += Run("DevAirReady", AirReadyTest());
            bad += Run("DevGfx", GfxTest());
            bad += Run("DevGfx lod", LodTest());
            bad += Run("DevForce", DevForce.SelfTest() ?? ForceTest() ?? ForceExecTest());   // T-361
            bad += Run("DevGc", GcTest());   // T-212
            bad += Run("dev stop selftest", StopTest());   // T-220
            bad += Run("dev stop by poll", StopPollTest());   // T-282
            bad += Run("dev client threads reused", ClientReuseTest());   // T-282
            bad += Run("DiagHost", DiagHost.SelfTest());
            bad += Run("DevStartGate", DevStartGate.SelfTest());   // T-282
            FakeGame.Uninstall();
            return bad == 0 ? 0 : 1;
        }

        /// <summary>T-220/T-227: Stop closes the sockets and joins the accept and ALL client threads (two clients open); a third one gets "busy".</summary>
        private static string StopTest()
        {
            var srv = new DevServer(new[] { 0 }, "tok", r => "{}", () => "{\"hello\":1}", TimeSpan.FromMinutes(1));
            if (!srv.Start()) return "start";
            using (var cl = new System.Net.Sockets.TcpClient())
            using (var cl2 = new System.Net.Sockets.TcpClient())
            using (var cl3 = new System.Net.Sockets.TcpClient())
            {
                cl.Connect("127.0.0.1", srv.Port);
                var rd = new StreamReader(cl.GetStream());
                cl.ReceiveTimeout = 3000;
                if (rd.ReadLine() == null) return "no hello";
                cl2.Connect("127.0.0.1", srv.Port);
                var rd2 = new StreamReader(cl2.GetStream());
                cl2.ReceiveTimeout = 3000;
                if (rd2.ReadLine() == null) return "no hello for the second client";
                cl3.Connect("127.0.0.1", srv.Port);
                cl3.ReceiveTimeout = 3000;
                string third = new StreamReader(cl3.GetStream()).ReadLine();
                if (third == null || !third.Contains("busy")) return "third client must get busy: " + third;
                int live = 0;
                foreach (var n in ModThreads.AliveDedicated()) if (n == "sptui-dev-client") live++;
                if (live != 2) return "two client threads expected before Stop, got " + live;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!srv.Stop(1000)) return "Stop returned false";
                if (sw.ElapsedMilliseconds > 1000) return "Stop took " + sw.ElapsedMilliseconds + " ms";
                foreach (var n in ModThreads.AliveDedicated()) if (n == "sptui-dev-accept" || n == "sptui-dev-client") return "thread still alive: " + n;
                if (!srv.Stop(1000)) return "second Stop returned false";
            }
            return null;
        }

        /// <summary>T-282: with no client at all Stop returns fast and the accept loop left through its poll timeout.</summary>
        private static string StopPollTest()
        {
            var srv = new DevServer(new[] { 0 }, "tok", r => "{}", () => "{\"hello\":1}", TimeSpan.FromMinutes(1));
            if (!srv.Start()) return "start";
            Thread.Sleep(100);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!srv.Stop(1000)) return "Stop returned false";
            if (sw.ElapsedMilliseconds > 600) return "Stop took " + sw.ElapsedMilliseconds + " ms";
            if (srv.AcceptExit != "poll") return "AcceptExit=" + srv.AcceptExit;
            return null;
        }

        /// <summary>T-282: 40 short sequential connections are served by the 2 pooled client threads; no thread per connection.</summary>
        private static string ClientReuseTest()
        {
            var srv = new DevServer(new[] { 0 }, "tok", r => "{\"id\":" + (r.Id == null ? "null" : "\"" + r.Id + "\"") + ",\"ok\":true}", () => "{\"hello\":1}", TimeSpan.FromMinutes(1), 65536, null, 2);
            if (!srv.Start()) return "start";
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    using (var cl = new System.Net.Sockets.TcpClient())
                    {
                        cl.Connect("127.0.0.1", srv.Port);
                        cl.ReceiveTimeout = 3000;
                        var rd = new StreamReader(cl.GetStream());
                        string hello = rd.ReadLine();
                        if (hello == null || hello.Contains("busy")) return "busy at #" + i + ": " + hello;
                        var b = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"p\",\"cmd\":\"ping\",\"token\":\"tok\"}\n");
                        cl.GetStream().Write(b, 0, b.Length);
                        string reply = rd.ReadLine();
                        if (reply == null || !reply.Contains("\"ok\":true")) return "bad reply #" + i + ": " + reply;
                    }
                }
                if (srv.ClientThreadsCreated != 2) return "ClientThreadsCreated=" + srv.ClientThreadsCreated;
                int n = 0;
                foreach (var t in ModThreads.Snapshot()) if (t.Name == "sptui-dev-client" && t.Alive) n++;
                if (n > 2) return "client thread infos: " + n;
            }
            finally { srv.Stop(1000); }
            return null;
        }

        private static int Run(string name, string err)
        {
            Console.WriteLine(name + " " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }

        private static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static string Tr(string cmd, Dictionary<string, object> a)
        {
            List<Dictionary<string, object>> st; string err;
            if (!DevCommandTable.Translate(cmd, a, out st, out err)) return "ERR " + err;
            return DevJson.Write(st);
        }

        private static string ProtocolTest()
        {
            var h = (Dictionary<string, object>)Json.Parse(DevProtocol.Hello("0.1.0", "0.15.0", true, true, false));
            if ((string)h["hello"] != "sptui-dev/1" || ((List<object>)h["groups"]).Contains("fire")) return "hello without fire";
            h = (Dictionary<string, object>)Json.Parse(DevProtocol.Hello("0.1.0", null, false, true, true));
            if (!((List<object>)h["groups"]).Contains("fire") || h["ui"] != null) return "hello with fire";
            var ok = (Dictionary<string, object>)Json.Parse(DevProtocol.Ok(7.0, new DevRaw("{\"a\":1}"), "[Dev][FIRE] x"));
            if (!(bool)ok["ok"] || (double)ok["id"] != 7 || (string)ok["fire_log"] != "[Dev][FIRE] x" || ((Dictionary<string, object>)ok["data"])["a"] as double? != 1) return "ok ack";
            var er = (Dictionary<string, object>)Json.Parse(DevProtocol.Err("q", "forbidden", "d", null));
            if ((bool)er["ok"] || (string)er["error"] != "forbidden" || er.ContainsKey("fire_log")) return "err ack";
            DevRequest r; string e;
            if (DevProtocol.TryParse("{nope", out r, out e) || e != "bad_json") return "bad_json";
            if (DevProtocol.TryParse("[1]", out r, out e) || e != "bad_json") return "array";
            if (DevProtocol.TryParse("{\"id\":1}", out r, out e) || e != "bad_request") return "no cmd";
            if (!DevProtocol.TryParse("{\"id\":3,\"token\":\"t\",\"cmd\":\"ping\",\"args\":{\"x\":1}}", out r, out e) || r.Cmd != "ping" || r.Token != "t" || r.Args.Count != 1) return "parse";
            // T-198: blank lines are not requests; the log head is escaped and never shows the token
            foreach (string blank in new[] { "", "\r", "   ", "\uFEFF", " \t\r" }) if (!DevServer.IsBlank(blank)) return "blank: " + blank.Length;
            if (DevServer.IsBlank("{}") || DevServer.IsBlank("x")) return "not blank";
            string head = DevServer.MaskHead("{\"id\": 1, \"token\": \"0123abcd\", \"cmd\": \"state\"\r\n\u0001");
            if (head.Contains("0123abcd") || !head.Contains("\"token\": \"***\"") || head.Contains("\r") || !head.Contains("\\r\\n") || !head.Contains("\\x01")) return "mask head: " + head;
            if (DevServer.MaskHead(new string('a', 200)).Length != 63) return "head length " + DevServer.MaskHead(new string('a', 200)).Length;
            return null;
        }

        private static string CommandsTest()
        {
            // commands that the dev channel must never know
            foreach (string c in new[] { "foo", "action", "set_flags", "asw_restore", "restore_control", "retarget", "pick", "link", "confirm_neutral" })
                if (Tr(c, null) != "ERR forbidden") return "forbidden " + c;
            foreach (string k in new[] { "confirm_neutral", "pick", "confirm" })
                if (!Tr("attack", O("shooter", 1.0, "target", 101.0, k, true)).StartsWith("ERR bad_args")) return "extra key " + k;
            if (!Tr("attack", O("shooter", 1.0, "target", 101.0, "scope", "all")).StartsWith("ERR bad_args")) return "scope all";
            if (!Tr("attack", O("shooter", "1", "target", 101.0)).StartsWith("ERR bad_args")) return "attack type";
            if (Tr("attack", O("shooter", 1.0, "target", 101.0)) != "[{\"cmd\":\"attack\",\"uid\":1,\"target_id\":101,\"confirm\":true}]") return "attack steps";
            if (Tr("window", O("name", "asw", "state", "open")) != "[{\"cmd\":\"ui_window\",\"name\":\"asw\",\"on\":true}]") return "window asw";
            if (Tr("window", O("name", "overview")) != "[{\"cmd\":\"overview\"}]") return "window overview";
            // T-178 I3: ui_undo {window} -> one ui_undo step; the window is asw | cap
            if (Tr("ui_undo", O("window", "cap")) != "[{\"cmd\":\"ui_undo\",\"window\":\"cap\"}]") return "ui_undo cap";
            if (!Tr("ui_undo", O("window", "overview")).StartsWith("ERR bad_args") || !Tr("ui_undo", O()).StartsWith("ERR bad_args")) return "ui_undo bad window";
            if (DevCommandTable.GroupOf("ui_undo") != "control") return "ui_undo group";
            if (Tr("layer", O("id", "labels")) != "ERR unknown_layer") return "layer";
            var uids = new List<object> { 5.0, 6.0 };
            if (Tr("group_create", O("kind", "asw", "group", "A", "uids", uids, "target_uid", 9.0, "depth", "deep"))
                != "[{\"cmd\":\"asw_group\",\"op\":\"create\",\"group\":\"A\",\"uids\":[5,6],\"kind\":\"asw\"},{\"cmd\":\"asw_search_course\",\"group\":\"A\",\"target_uid\":9,\"depth\":\"deep\"}]") return "asw course chain";
            if (Tr("group_create", O("kind", "sar", "group", "S", "uids", uids, "on_complete", "rtb"))
                != "[{\"cmd\":\"asw_group\",\"op\":\"create\",\"group\":\"S\",\"uids\":[5,6],\"kind\":\"sar\"},{\"cmd\":\"sar_order\",\"group\":\"S\",\"on_complete\":\"rtb\"}]") return "sar chain";
            if (Tr("group_create", O("kind", "cap", "group", "C", "uids", uids, "bearing", 90.0, "dist_nm", 50.0, "size_nm", 20.0, "origin_uid", 1.0))
                != "[{\"cmd\":\"cap_group\",\"op\":\"create\",\"group\":\"C\",\"uids\":[5,6]},{\"cmd\":\"cap_order\",\"group\":\"C\",\"bearing\":90,\"dist_nm\":50,\"size_nm\":20,\"origin_uid\":1}]") return "cap chain";
            if (!Tr("group_create", O("kind", "cap", "group", "C", "uids", uids, "roe", "free")).StartsWith("ERR bad_args")) return "cap roe (patrol mode)";
            // T-179 (ADR-0079): mode alert -> cap_group create + ONE cap_order kind=alert; the alert settings are only for the alert mode; cap_scramble
            if (Tr("group_create", O("kind", "cap", "mode", "alert", "group", "A", "uids", uids, "ref_uid", 200.0, "ready", 3.0, "per_raid", 1.0, "level", "hangar", "radius_nm", 100.0, "roe", "hold", "fill", true))
                != "[{\"cmd\":\"cap_group\",\"op\":\"create\",\"group\":\"A\",\"uids\":[5,6]},{\"cmd\":\"cap_order\",\"group\":\"A\",\"kind\":\"alert\",\"ref_uid\":200,\"ready\":3,\"per_raid\":1,\"level\":\"hangar\",\"radius_nm\":100,\"roe\":\"hold\",\"fill\":true}]") return "cap alert chain";
            if (!Tr("group_create", O("kind", "cap", "mode", "alert", "group", "A", "uids", uids, "bearing", 90.0)).StartsWith("ERR bad_args")) return "alert mode takes no zone";
            if (!Tr("group_create", O("kind", "cap", "mode", "zap", "group", "A", "uids", uids)).StartsWith("ERR bad_args")) return "bad mode";
            if (!Tr("group_create", O("kind", "cap", "group", "C", "uids", uids, "bearing", 90.0, "dist_nm", 50.0, "size_nm", 20.0, "origin_uid", 1.0, "accept_alert", false)).Contains("\"cap_order\"")) return "patrol accept_alert";
            if (Tr("cap_scramble", O("group", "A")) != "[{\"cmd\":\"cap_scramble\",\"group\":\"A\"}]") return "cap_scramble";
            if (!Tr("cap_scramble", O()).StartsWith("ERR bad_args") || DevCommandTable.GroupOf("cap_scramble") != "control") return "cap_scramble args / group";
            if (!Tr("group_edit", O("kind", "cap", "group", "C", "roe", "free")).StartsWith("ERR bad_args")) return "edit cap roe";
            if (Tr("group_edit", O("kind", "cap", "group", "C", "missile_pct", 50.0)) != "[{\"cmd\":\"cap_params\",\"group\":\"C\",\"missile_pct\":50}]") return "cap params";
            if (Tr("group_edit", O("kind", "asw", "group", "A", "depth", "deep", "add", uids))
                != "[{\"cmd\":\"asw_depth\",\"group\":\"A\",\"mode\":\"deep\"},{\"cmd\":\"asw_group\",\"op\":\"add\",\"group\":\"A\",\"uids\":[5,6],\"kind\":\"asw\"}]") return "asw edit";
            if (Tr("group_delete", O("kind", "cap", "group", "C"))
                != "[{\"cmd\":\"cap_stop\",\"group\":\"C\"},{\"cmd\":\"cap_group\",\"op\":\"disband\",\"group\":\"C\"}]") return "cap delete";
            if (Tr("group_delete", O("kind", "sar", "group", "S"))
                != "[{\"cmd\":\"asw_stop\",\"group\":\"S\"},{\"cmd\":\"asw_group\",\"op\":\"disband\",\"group\":\"S\"}]") return "sar delete";
            if (Tr("time", O("mode", "speed", "value", 5.0)) != "[{\"cmd\":\"set_time_compression\",\"value\":5}]") return "time speed";
            if (Tr("time", O("mode", "pause")) != "[{\"cmd\":\"pause\"}]") return "time pause";
            if (Tr("select_unit", O("track_id", 101.0, "center", true)) != "[{\"cmd\":\"select_contact\",\"track_id\":101},{\"cmd\":\"center_map\",\"track_id\":101}]") return "select track";
            if (Tr("intercept", O("uid", 1.0, "target_id", 101.0)) != "[{\"cmd\":\"intercept\",\"uid\":1,\"target_id\":101}]") return "intercept";
            if (Tr("intercept", O("uid", 1.0, "cancel", true)) != "[{\"cmd\":\"intercept_cancel\",\"uid\":1}]") return "intercept cancel";
            if (Tr("intercept", O("uid", 1.0)) != "[{\"cmd\":\"intercept_status\",\"uid\":1}]") return "intercept status";
            if (!Tr("intercept", O("target_id", 101.0)).StartsWith("ERR bad_args")) return "intercept no uid";
            if (Tr("sub_datum_list", O()) != "[{\"cmd\":\"sub_datum_list\"}]") return "sub_datum_list";
            if (Tr("sub_datum_list", O("limit", 5.0)) != "[{\"cmd\":\"sub_datum_list\",\"limit\":5}]") return "sub_datum_list limit";
            if (Tr("sub_datum_search", O("datum", 2.0, "group", "ASW-1")) != "[{\"cmd\":\"sub_datum_search\",\"id\":2,\"group\":\"ASW-1\"}]") return "sub_datum_search";
            if (Tr("sub_datum_search", O("datum", 2.0)) != "[{\"cmd\":\"sub_datum_search\",\"id\":2}]") return "sub_datum_search no group";
            if (Tr("sub_datum_dismiss", O("datum", 2.0)) != "[{\"cmd\":\"sub_datum_dismiss\",\"id\":2}]") return "sub_datum_dismiss";
            if (!Tr("sub_datum_search", O("group", "ASW-1")).StartsWith("ERR bad_args")) return "sub_datum_search no datum";
            if (!Tr("sub_datum_dismiss", O("datum", "x")).StartsWith("ERR bad_args")) return "sub_datum_dismiss wrong type";
            if (Tr("smart_attack", O("shooter", 1.0, "target", 101.0)) != "[{\"cmd\":\"smart_attack\",\"uid\":1,\"target_id\":101}]") return "smart";
            if (Tr("asw_attack", O("proposal_id", "p1")) != "[{\"cmd\":\"asw_attack\",\"proposal_id\":\"p1\",\"confirm\":true,\"by\":\"window\"}]") return "asw_attack";
            if (Tr("asw_strike", O("group", "S", "airborne", 1.0)) != "[{\"cmd\":\"asw_order\",\"group\":\"S\",\"order\":\"strike\",\"airborne\":1}]") return "asw_strike";
            if (Tr("asw_strike_skip", O("contact", 7.0)) != "[{\"cmd\":\"asw_strike_skip\",\"contact\":7}]") return "asw_strike_skip";
            if (Tr("set_roe", O("uid", 1.0, "roe", "free")) != "[{\"cmd\":\"set_roe\",\"uid\":1,\"status\":\"free\",\"confirm\":true}]") return "roe free";
            if (Tr("set_roe", O("uid", 1.0, "roe", "tight")) != "[{\"cmd\":\"set_roe\",\"uid\":1,\"status\":\"tight\"}]") return "roe tight";
            if (Tr("set_roe", O("formation", 2.0, "roe", "hold")) != "[{\"cmd\":\"set_roe\",\"uid\":2,\"scope\":\"formation\",\"formation\":2,\"status\":\"hold\"}]") return "roe formation";
            return null;
        }

        // ------------------------------------------------------------------ D3 / D4 / D5

        private static readonly List<string> Warned = new List<string>();

        private static DevExecutor Exec(bool fireOn, DevLogRing ring = null)
        {
            Warned.Clear();
            return new DevExecutor(ring ?? new DevLogRing(), () => fireOn, Warned.Add);
        }

        private static Dictionary<string, object> Call(DevExecutor ex, string cmd, Dictionary<string, object> args, object id = null)
        {
            var r = new DevRequest { Id = id ?? 1.0, Cmd = cmd, Args = args ?? new Dictionary<string, object>() };
            return (Dictionary<string, object>)Json.Parse(ex.Handle(r));
        }

        private static bool Ok(Dictionary<string, object> a) { return a.ContainsKey("ok") && (bool)a["ok"]; }
        private static string Code(Dictionary<string, object> a) { return a.ContainsKey("error") ? a["error"] as string : null; }
        private static string FireLog(Dictionary<string, object> a) { return a.ContainsKey("fire_log") ? a["fire_log"] as string : null; }
        private static int Calls() { lock (FakeGame.Calls) return FakeGame.Calls.Count; }
        private static string Last() { lock (FakeGame.Calls) return FakeGame.Calls[FakeGame.Calls.Count - 1]; }

        private static string ExecutorTest()
        {
            var ex = Exec(true);
            FakeGame.Uninstall();
            if (Code(Call(ex, "state", null)) != "ui_mod_absent") return "no diag slot";
            if (Code(Call(ex, "ping", null)) != "ui_mod_absent") return "no link slot";
            if (Code(Call(ex, "foo", null)) != "forbidden") return "forbidden";
            if (Code(Call(ex, "attack", O("shooter", 1.0, "target", 101.0, "pick", true))) != "bad_args") return "bad_args";
            FakeGame.Install(true, true);
            var st = Call(ex, "state", O("sections", new List<object> { "mission", "deck" }, "limit", 5.0), "abc");
            if (!Ok(st) || (string)st["id"] != "abc") return "state id passthrough";
            var secs = (Dictionary<string, object>)((Dictionary<string, object>)st["data"])["sections"];
            if (!secs.ContainsKey("deck") || secs["deck"] == null || secs.ContainsKey("asw")) return "state sections";
            var all = Call(ex, "state", null);
            if (((Dictionary<string, object>)((Dictionary<string, object>)all["data"])["sections"]).Count != 22) return "state all 22";
            var un = Call(ex, "state", O("sections", new List<object> { "units" }));
            if (!Ok(un) || !((Dictionary<string, object>)((Dictionary<string, object>)un["data"])["sections"]).ContainsKey("units")) return "units section";
            if (Code(Call(ex, "state", O("sections", new List<object> { "nope" }))) != "diag_error") return "diag error";
            AppDomain.CurrentDomain.SetData(DevInfo.DiagSlotKey, new Dictionary<string, object> { { "diag", 2 }, { "version", "x" }, { "query", (Func<string, string>)(q => "{}") } });
            if (Code(Call(ex, "state", null)) != "unsupported") return "diag v2";
            FakeGame.Install(true, true);
            FakeGame.SetLink(2);
            if (Code(Call(ex, "ping", null)) != "unsupported") return "link v2";
            FakeGame.SetLink(1);
            // two-step chain: the second step is not called when the first fails
            var grp = O("kind", "asw", "group", "A", "uids", new List<object> { 5.0 }, "target_uid", 9.0);
            FakeGame.Calls.Clear(); FakeGame.Fail["asw_group"] = "no_such_group";
            var bad = Call(ex, "group_create", grp);
            if (Ok(bad) || Code(bad) != "no_such_group" || Calls() != 1) return "chain stops at first error";
            var steps = (List<object>)((Dictionary<string, object>)bad["data"])["steps"];
            if (steps.Count != 1) return "error carries data.steps";
            FakeGame.Fail.Clear(); FakeGame.Calls.Clear();
            var good = Call(ex, "group_create", grp);
            if (!Ok(good) || Calls() != 2 || ((List<object>)((Dictionary<string, object>)good["data"])["steps"]).Count != 2) return "chain of two";
            if (!Last().Contains("asw_search_course")) return "second step";
            FakeGame.Calls.Clear();
            if (!Ok(Call(ex, "window", O("name", "asw", "state", "open"))) || !Last().Contains("\"ui_window\"")) return "window";
            var lg = Exec(true);
            Call(lg, "attack", O("shooter", 1.0, "target", 101.0));
            var tail = Call(lg, "log", O("lines", 5.0, "level", "warning"));
            if (((List<object>)((Dictionary<string, object>)tail["data"])["lines"]).Count != 1) return "log command";
            var h = (Dictionary<string, object>)Json.Parse(ex.Hello());
            if ((bool)h["diag"] != true || (string)h["ui"] != "0.20.0") return "hello";
            return null;
        }

        // air_ready (QA cheat): validation, own decks only, ready-up and cool-down set to 0, journal line, no link call
        private static string AirReadyTest()
        {
            if (!Tr("air_ready", O()).StartsWith("ERR bad_args")) return "no selector accepted";
            if (!Tr("air_ready", O("all", true, "ship_uid", 5.0)).StartsWith("ERR bad_args")) return "all with ship_uid accepted";
            if (!Tr("air_ready", O("key", "x:1")).StartsWith("ERR bad_args")) return "bad key accepted";
            if (!Tr("air_ready", O("uid", 1.0)).StartsWith("ERR bad_args")) return "unknown arg accepted";
            if (Tr("air_ready", O("all", true)) != "[]") return "all";
            if (DevCommandTable.GroupOf("air_ready") != "control") return "group";

            var ex = Exec(false);   // fire off must not matter
            SeaPower.Globals.Set(null);
            if (Code(Call(ex, "air_ready", O("all", true))) != "no_mission") return "no mission";
            var tf = new SeaPower.Taskforce();
            var cv = new SeaPower.ObjectBase { UniqueID = 7 };
            cv._obp._flightDeck = new SeaPower.FlightDeck();
            var vt = new SeaPower.VehicleTypeOnBoard { _fileName = "f14a" };
            var p1 = new SeaPower.PendingLaunchTask { _duration = 1800, VehicleType = vt };
            p1._stateMachine.CurrentState = new SeaPower.HandleReadyUpTask();
            var p2 = new SeaPower.PendingLaunchTask { _duration = 900, VehicleType = vt };
            var cool = new SeaPower.CoolDownTask { _duration = 600, VehicleType = vt };
            var launch = new SeaPower.LaunchTask { _duration = 30 };
            cv._obp._flightDeck.FlightDeckTasks.AddRange(new SeaPower.FlightDeckTask[] { p1, p2, cool, launch });
            var ff = new SeaPower.ObjectBase { UniqueID = 8 };   // own ship without a deck
            tf._taskforceVessels.Add(cv); tf._taskforceVessels.Add(ff);
            SeaPower.Globals.Set(tf);

            if (Code(Call(ex, "air_ready", O("ship_uid", 99.0))) != "unit_not_found") return "foreign / unknown ship";
            if (Code(Call(ex, "air_ready", O("ship_uid", 8.0))) != "no_flight_deck") return "ship without deck";
            if (Code(Call(ex, "air_ready", O("key", "task:" + Guid.NewGuid()))) != "deck_task_not_found") return "unknown key";
            int before = Calls();
            var one = Call(ex, "air_ready", O("key", "task:" + p2._uid));
            if (!Ok(one) || p2._duration != 0 || p1._duration != 1800 || cool._duration != 600) return "one key";
            var all = Call(ex, "air_ready", O("all", true));
            var d = (Dictionary<string, object>)all["data"];
            if (!Ok(all) || (double)d["changed"] != 2 || p1._duration != 0 || cool._duration != 0 || launch._duration != 30) return "all decks";
            var rows = (List<object>)d["tasks"];
            if (rows.Count != 3) return "rows " + rows.Count;
            var r0 = (Dictionary<string, object>)rows[0];
            if ((string)r0["key"] != "task:" + p1._uid || (string)r0["type"] != "f14a" || (string)r0["state"] != "HandleReadyUpTask" || (double)r0["duration_was"] != 1800 || !(bool)r0["set"]) return "row shape";
            if ((string)((Dictionary<string, object>)rows[2])["key"] != "cool:" + cool._uid) return "cool key";
            if (Calls() != before) return "air_ready must not call link";
            if (Warned.Count != 6 || !Warned[5].StartsWith("[Dev][AIR] air_ready ship=- key=- all=true → ok changed=2")) return "journal: " + (Warned.Count > 0 ? Warned[Warned.Count - 1] : "none");
            SeaPower.Globals.Set(null);
            return null;
        }

        // force (T-361): translation into one qa_force step, dev-side argument checks
        private static string ForceTest()
        {
            if (Tr("force", O("op", "route_shift", "uid", 7.0)) != "[{\"cmd\":\"qa_force\",\"op\":\"route_shift\",\"uid\":7}]") return "route_shift: " + Tr("force", O("op", "route_shift", "uid", 7.0));
            if (Tr("force", O("op", "steer_hostile", "to_uid", 5.0, "away", true, "stop_nm", 4.0)) != "[{\"cmd\":\"qa_force\",\"op\":\"steer_hostile\",\"to_uid\":5,\"stop_nm\":4,\"away\":true}]") return "steer_hostile";
            if (!Tr("force", O("op", "route_shift")).StartsWith("ERR bad_args")) return "missing uid";
            if (!Tr("force", O("op", "nope")).StartsWith("ERR bad_args")) return "unknown op";
            if (!Tr("force", O("op", "divert", "uid", 7.0, "away", true)).StartsWith("ERR bad_args")) return "foreign arg";
            if (!Tr("force", O("op", "divert", "uid", "x")).StartsWith("ERR bad_args")) return "wrong type";
            if (!Tr("force", O("op", "divert", "uid", 7.0, "dist_nm", 500.0)).StartsWith("ERR bad_args")) return "range";
            if (DevCommandTable.GroupOf("force") != "control") return "group";
            return null;
        }

        // force through the executor: switch, journal, link errors, no link slot
        private static string ForceExecTest()
        {
            FakeGame.Install(true, true);
            var args = O("op", "route_shift", "uid", 7.0, "dist_nm", 2.0);
            var ex = Exec(true);
            FakeGame.Calls.Clear();
            var ok = Call(ex, "force", args);
            if (!Ok(ok) || Calls() != 1 || !Last().Contains("\"qa_force\"")) return "ok";
            if (Warned.Count != 1 || Warned[0] != "[Dev][FORCE] op=route_shift args=dist_nm=2 uid=7 → ok") return "journal ok: " + (Warned.Count > 0 ? Warned[0] : "none");
            FakeGame.Fail["qa_force"] = "not_in_ai_window";
            var bad = Call(ex, "force", args);
            if (Ok(bad) || Code(bad) != "not_in_ai_window") return "link error";
            if (Warned.Count != 2 || !Warned[1].EndsWith("→ not_in_ai_window")) return "journal error";
            FakeGame.Fail["qa_force"] = "forbidden";
            if (Code(Call(ex, "force", args)) != "forbidden") return "forbidden passthrough";
            FakeGame.Fail.Clear();
            var off = new DevExecutor(new DevLogRing(), () => true, Warned.Add, null, null, null, () => false);
            Warned.Clear(); FakeGame.Calls.Clear();
            if (Code(Call(off, "force", args)) != "force_disabled" || Calls() != 0) return "disabled";
            if (Warned.Count != 1 || !Warned[0].EndsWith("→ force_disabled")) return "journal disabled";
            if (Code(Call(off, "ping", null)) != null) return "disabled must not block ping";
            if (Code(Call(Exec(false), "force", args)) != null) return "fire switch must not block force";
            FakeGame.Uninstall();
            Warned.Clear();
            if (Code(Call(Exec(true), "force", args)) != "ui_mod_absent") return "no link";
            if (Warned.Count != 1 || !Warned[0].EndsWith("→ ui_mod_absent")) return "journal absent";
            if (Code(Call(Exec(true), "force", O("op", "nope"))) != "bad_args") return "bad_args via executor";
            return null;
        }

        // gc (T-212): in the table as a read command, forced collection answers live_mb / ms
        private static string GcTest()
        {
            if (DevCommandTable.GroupOf("gc") != "read") return "group " + DevCommandTable.GroupOf("gc");
            var ex = Exec(false);
            int before = Calls();
            var a = Call(ex, "gc", null);
            if (!Ok(a)) return "not ok: " + Code(a);
            var d = (Dictionary<string, object>)a["data"];
            if (!d.ContainsKey("live_mb") || !d.ContainsKey("ms") || (double)d["live_mb"] <= 0) return "shape";
            if (Calls() != before) return "gc must not call link";
            return null;
        }

        // gfx (T-165): validation, dump shape, set / restore of every lever kind, reset, unsupported, journal, no link call
        private static string GfxTest()
        {
            if (!Tr("gfx", O()).StartsWith("ERR bad_args")) return "no op accepted";
            if (!Tr("gfx", O("op", "set", "lever", "nope", "on", true)).StartsWith("ERR bad_args")) return "unknown lever accepted";
            if (!Tr("gfx", O("op", "set", "lever", "rcm")).StartsWith("ERR bad_args")) return "set without on accepted";
            if (!Tr("gfx", O("op", "dump", "lever", "rcm")).StartsWith("ERR bad_args")) return "dump with lever accepted";
            if (!Tr("gfx", O("op", "set", "lever", "rcm", "on", "yes")).StartsWith("ERR bad_args")) return "string on accepted";
            if (Tr("gfx", O("op", "dump")) != "[]" || Tr("gfx", O("op", "set", "lever", "ssao", "on", true)) != "[]") return "valid gfx";
            if (DevCommandTable.GroupOf("gfx") != "control") return "group";

            var ex = Exec(false);
            var gs = UnityEngine.GameObject.Scene;
            gs.Clear();
            if (Code(Call(ex, "gfx", O("op", "set", "lever", "rcm", "on", true))) != "unsupported") return "no camera: unsupported";
            var camGo = UnityEngine.GameObject.Make("Main Camera");
            var cam = camGo.Add<UnityEngine.Camera>();
            var rcm = camGo.Add<RenderColorMask>();
            var ssao = camGo.Add<AmplifyOcclusionEffect>();
            var bloom = camGo.Add<AmplifyBloom.AmplifyBloomEffect>();
            var shadow = camGo.Add<SeaPower.ScreenSpaceCloudShadow>();
            UnityEngine.Camera.main = cam;
            var mgr = new SeaPower.CameraManager();
            SeaPower.CameraManager.Instance = mgr;
            var reflGo = UnityEngine.GameObject.Make("Reflection Cam");
            reflGo.Add<UnityEngine.Camera>().targetTexture = new UnityEngine.RenderTexture { name = "refl_rt" };
            var oceanGo = UnityEngine.GameObject.Make("Ocean");
            var ocean = oceanGo.Add<Ceto.Ocean>();
            var refl = oceanGo.Add<Ceto.PlanarReflection>();
            refl.reflectionMask.value = 5;
            ocean.Reflection = refl; ocean.UnderWater = oceanGo.Add<Ceto.UnderWater>();
            Ceto.Ocean.Instance = ocean;
            var clouds = UnityEngine.GameObject.Make("Clouds");
            var smoke = UnityEngine.GameObject.Make("Smoke").Add<UnityEngine.ParticleSystem>();
            int before = Calls();

            var dump = Call(ex, "gfx", O("op", "dump"));
            if (!Ok(dump)) return "dump: " + Code(dump) + " " + (dump.ContainsKey("detail") ? dump["detail"] : "");
            var dd = (Dictionary<string, object>)dump["data"];
            var cams = (List<object>)dd["cameras"];
            if (!Ok(dump) || cams.Count != 2) return "dump cameras";
            var c1 = (Dictionary<string, object>)cams[1];
            if ((string)c1["target_texture"] != "refl_rt" || (bool)c1["main"] || !(bool)((Dictionary<string, object>)cams[0])["main"]) return "camera row";
            var beh = (List<object>)((Dictionary<string, object>)dd["main_camera"])["behaviours"];
            if (beh.Count != 4 || (string)((Dictionary<string, object>)beh[0])["type"] != "RenderColorMask") return "behaviours " + beh.Count;
            var rd = (Dictionary<string, object>)dd["reflection"];
            if ((double)rd["reflection_mask"] != 5 || (string)rd["resolution"] != "QUARTER") return "reflection dump";
            if ((string)((Dictionary<string, object>)dd["ocean"])["normal_overlay"] != "FULL") return "ocean dump";
            if ((string)((Dictionary<string, object>)dd["underwater"])["mode"] != "ABOVE_AND_BELOW") return "underwater dump";
            var qd = (Dictionary<string, object>)dd["quality"];
            if ((double)qd["lod_bias"] != 1 || (string)qd["anisotropic_filtering"] != "Enable" || (double)dd["target_frame_rate"] != 30) return "quality dump";
            if (!(bool)((Dictionary<string, object>)dd["clouds"])["active"]) return "clouds dump";
            if ((double)dd["particle_systems"] != 1) return "particle count";
            foreach (var k in dd.Keys) if (k.Contains("rate") && k != "target_frame_rate") return "no render-rate in dump";

            foreach (var l in DevGfx.Levers)
            {
                var r = Call(ex, "gfx", O("op", "set", "lever", l, "on", true));
                if (!Ok(r)) return "set " + l + ": " + Code(r);
            }
            if (rcm.enabled || ssao.enabled || bloom.ApplyLensGlare || bloom.ApplyLensFlare || bloom.BloomDownsampleCount != 3) return "camera levers";
            if (clouds.activeSelf || shadow.enabled || refl.reflectionMask.value != 0) return "cloud / refl";
            if (ocean.normalOverlaySize != Ceto.OVERLAY_MAP_SIZE.QUARTER || ocean.foamOverlaySize != Ceto.OVERLAY_MAP_SIZE.QUARTER || ocean.heightOverlaySize != Ceto.OVERLAY_MAP_SIZE.HALF) return "ovl";
            if (UnityEngine.QualitySettings.lodBias != 0.5f || UnityEngine.QualitySettings.pixelLightCount != 0 || UnityEngine.QualitySettings.softParticles) return "quality levers";
            if (UnityEngine.Application.targetFrameRate != 60 || UnityEngine.QualitySettings.vSyncCount != 0) return "fps60";
            var againR = Call(ex, "gfx", O("op", "set", "lever", "lod", "on", true));
            if (!Ok(againR)) return "again: " + Code(againR) + " " + (againR.ContainsKey("detail") ? againR["detail"] : "");
            var again = (Dictionary<string, object>)againR["data"];
            if ((string)again["note"] != "already applied") return "already applied";

            if (smoke.Max != 250 || !cam.enabled) return "particles / camera untouched: " + smoke.Max;
            if (Array.IndexOf(DevGfx.Levers, "rate2") >= 0) return "rate2 is out of scope";

            var off = Call(ex, "gfx", O("op", "set", "lever", "cloud", "on", false));
            if (!Ok(off) || !clouds.activeSelf || !shadow.enabled) return "cloud restore";
            var ad = (Dictionary<string, object>)((Dictionary<string, object>)Call(ex, "gfx", O("op", "dump"))["data"]);
            if (((List<object>)ad["applied"]).Contains("cloud") || ((List<object>)ad["applied"]).Count != DevGfx.Levers.Length - 1) return "applied list";
            if ((string)((Dictionary<string, object>)Call(ex, "gfx", O("op", "set", "lever", "cloud", "on", false))["data"])["note"] != "not applied") return "not applied";

            var reset = Call(ex, "gfx", O("op", "reset"));
            if (!Ok(reset) || ((List<object>)((Dictionary<string, object>)reset["data"])["restored"]).Count != DevGfx.Levers.Length - 1) return "reset";
            if (!rcm.enabled || !ssao.enabled || !bloom.ApplyLensGlare || !bloom.ApplyLensFlare || bloom.BloomDownsampleCount != 6 || refl.reflectionMask.value != 5) return "reset camera / refl";
            if (ocean.normalOverlaySize != Ceto.OVERLAY_MAP_SIZE.FULL || UnityEngine.QualitySettings.lodBias != 1f || UnityEngine.QualitySettings.pixelLightCount != 2 || !UnityEngine.QualitySettings.softParticles) return "reset quality";
            if (UnityEngine.Application.targetFrameRate != 30 || UnityEngine.QualitySettings.vSyncCount != 1 || smoke.Max != 1000 || smoke.RateT != 10f || smoke.Life != 1f) return "reset fps60 / particles";

            camGo.Comps.Remove(rcm);
            var miss = Call(ex, "gfx", O("op", "set", "lever", "rcm", "on", true));
            if (Code(miss) != "unsupported" || !((string)miss["detail"]).Contains("RenderColorMask")) return "missing component";
            if (Calls() != before) return "gfx must not call link";
            string last = Warned.Count > 0 ? Warned[Warned.Count - 1] : "none";
            if (!last.StartsWith("[Dev][GFX] gfx op=set lever=rcm on=true → unsupported")) return "journal: " + last;
            if (!Warned.Exists(w => w.StartsWith("[Dev][GFX] gfx op=reset lever=- on=- → ok restored=fps60,particles,softp"))) return "reset journal";
            gs.Clear(); UnityEngine.Camera.main = null; SeaPower.CameraManager.Instance = null; Ceto.Ocean.Instance = null;
            UnityEngine.QualitySettings.lodBias = 1f;
            return null;
        }

        // gfx (T-165): the removed `smoke` lever and the `k` argument are rejected; lod never raises
        private static string LodTest()
        {
            if (!Tr("gfx", O("op", "set", "lever", "smoke", "on", true)).StartsWith("ERR bad_args")) return "lever smoke still accepted";
            if (!Tr("gfx", O("op", "set", "lever", "ssao", "on", true, "k", 0.5)).StartsWith("ERR bad_args")) return "k accepted";
            if (!Tr("gfx", O("op", "dump", "k", 0.5)).StartsWith("ERR bad_args")) return "dump with k accepted";
            var gs = UnityEngine.GameObject.Scene;
            gs.Clear();
            var gfx = new DevGfx(null, () => 0);
            Warned.Clear();
            var ex = new DevExecutor(new DevLogRing(), () => false, Warned.Add, null, null, gfx);
            UnityEngine.QualitySettings.lodBias = 0.3f;   // lod never raises a lower bias, restores it on off
            if (!Ok(Call(ex, "gfx", O("op", "set", "lever", "lod", "on", true))) || UnityEngine.QualitySettings.lodBias != 0.3f) return "lod raised";
            Call(ex, "gfx", O("op", "set", "lever", "lod", "on", false));
            if (UnityEngine.QualitySettings.lodBias != 0.3f) return "lod restore";
            UnityEngine.QualitySettings.lodBias = 1f;
            gs.Clear();
            return null;
        }

        private static string FireTest()
        {
            FakeGame.Install(true, true);
            var ring = new DevLogRing();
            var ex = Exec(true, ring);
            var r = Call(ex, "attack", O("shooter", 1.0, "target", 101.0));
            if (!Ok(r) || Calls() != 1 || Last() != "{\"cmd\":\"attack\",\"uid\":1,\"target_id\":101,\"confirm\":true,\"id\":1}") return "attack hostile: " + (Calls() > 0 ? Last() : "no call");
            string line = FireLog(r);
            if (line == null || !line.StartsWith("[Dev][FIRE] attack shooter=1 Kirov target=101 Destroyer rel=hostile weapon=- salvo=- \u2192 ok")) return "fire line: " + line;
            if (Warned.Count != 1 || Warned[0] != line || ring.Tail(5, "[Dev][FIRE]", null).Count != 1) return "journal";
            foreach (double t in new[] { 102.0, 103.0, 104.0, 105.0 })
            {
                FakeGame.Calls.Clear(); Warned.Clear();
                var d = Call(ex, "attack", O("shooter", 1.0, "target", t));
                if (Code(d) != "target_not_hostile" || Calls() != 0 || Warned.Count != 1 || FireLog(d) == null) return "not hostile " + t;
            }
            FakeGame.Fail["attack"] = "neutral_near"; FakeGame.Calls.Clear();
            var nn = Call(ex, "attack", O("shooter", 1.0, "target", 101.0));
            if (Code(nn) != "neutral_near" || Calls() != 1 || Last().Contains("confirm_neutral")) return "neutral_near passthrough";
            FakeGame.Fail["attack"] = "attack_disabled";
            if (Code(Call(ex, "attack", O("shooter", 1.0, "target", 101.0))) != "attack_disabled") return "attack_disabled";
            FakeGame.Fail.Clear(); FakeGame.Calls.Clear();
            if (!Ok(Call(ex, "smart_attack", O("shooter", 1.0, "target", 101.0, "ammo", "Mk46"))) || Last().Contains("pick") || Last().Contains("confirm_neutral")) return "smart_attack";
            FakeGame.Calls.Clear();
            var a1 = Call(ex, "asw_attack", O("proposal_id", "p1"));
            if (!Ok(a1) || !Last().StartsWith("{\"cmd\":\"asw_attack\",\"proposal_id\":\"p1\",\"confirm\":true,\"by\":\"window\"")) return "asw_attack p1";
            FakeGame.Calls.Clear();
            if (Code(Call(ex, "asw_attack", O("proposal_id", "p2"))) != "target_not_hostile" || Calls() != 0) return "asw_attack neutral";
            if (Code(Call(ex, "asw_attack", O("proposal_id", "p9"))) != "no_proposal" || Calls() != 0) return "asw_attack no_proposal";
            // 0.27.10 (live): ASW contact side=unknown painted red -> proposal p3 -> the dev guard follows the map like the proposal
            Warned.Clear();
            var a3 = Call(ex, "asw_attack", O("proposal_id", "p3"));
            if (!Ok(a3) || Calls() != 1 || !Last().StartsWith("{\"cmd\":\"asw_attack\",\"proposal_id\":\"p3\"")) return "asw_attack map-hostile p3";
            if (FireLog(a3) == null || !FireLog(a3).StartsWith("[Dev][FIRE] asw_attack proposal=p3 target=106 rel=unknown map=hostile \u2192 ok")) return "fire line p3: " + FireLog(a3);
            FakeGame.Calls.Clear();
            if (!Ok(Call(ex, "attack", O("shooter", 1.0, "target", 106.0))) || Calls() != 1) return "attack map-hostile 106";
            FakeGame.Calls.Clear();
            if (Code(Call(ex, "attack", O("shooter", 1.0, "target", 107.0))) != "target_not_hostile" || Calls() != 0) return "neutral painted red attacked";
            Warned.Clear();
            var roe = Call(ex, "set_roe", O("uid", 1.0, "roe", "free"));
            if (!Ok(roe) || !Last().Contains("\"confirm\":true") || !FireLog(roe).Contains("set_roe uid=1 roe=free scope=unit")) return "roe free";
            Call(ex, "set_roe", O("uid", 1.0, "roe", "tight"));
            if (Last().Contains("confirm")) return "roe tight has no confirm";
            // T-341 C: the strike commands have no target argument: the gate lets them through while fire is on, the mod checks the ships
            FakeGame.Calls.Clear();
            var sl = Call(ex, "strike_launch", O("group", "A"));
            if (!Ok(sl) || Calls() != 1 || FireLog(sl) == null || !FireLog(sl).StartsWith("[Dev][FIRE] strike_launch args=A/- \u2192 ok")) return "strike_launch " + FireLog(sl);
            FakeGame.Calls.Clear();
            var spo = Call(ex, "strike_probe", O("op", "ew", "uid", 68.0, "target_id", 7002.0, "on", true, "obj", true));   // T-346 D1: obj is accepted
            if (!Ok(spo)) return "strike_probe obj rejected";
            FakeGame.Calls.Clear();
            var sp = Call(ex, "strike_probe", O("op", "fire", "uid", 68.0, "target_id", 7002.0, "ammo", "usn_agm-84d"));
            if (!Ok(sp) || Calls() != 1 || FireLog(sp) == null || !FireLog(sp).Contains("strike_probe args=fire/68/7002")) return "strike_probe " + FireLog(sp);
            // FireCommands off
            var off = Exec(false);
            FakeGame.Calls.Clear();
            foreach (var c in new[] { "attack", "smart_attack", "asw_attack", "set_roe", "strike_launch" })
            {
                var args = c == "asw_attack" ? O("proposal_id", "p1") : c == "set_roe" ? O("uid", 1.0, "roe", "hold") : c == "strike_launch" ? O("group", "A") : O("shooter", 1.0, "target", 101.0);
                Warned.Clear();
                var d = Call(off, c, args);
                if (Code(d) != "fire_disabled" || Calls() != 0 || Warned.Count != 1) return "fire_disabled " + c;
            }
            if (!Ok(Call(off, "ping", null))) return "control still works with fire off";
            // no diag, link present
            FakeGame.Install(false, true);
            var nd = Exec(true);
            if (Code(Call(nd, "attack", O("shooter", 1.0, "target", 101.0))) != "target_not_hostile") return "attack without diag";
            if (!Ok(Call(nd, "set_roe", O("uid", 1.0, "roe", "hold")))) return "set_roe without diag";
            FakeGame.Install(true, false);
            var nl = Exec(true);
            var nlr = Call(nl, "attack", O("shooter", 1.0, "target", 101.0));
            if (Code(nlr) != "ui_mod_absent" || FireLog(nlr) == null) return "attack without link";
            return null;
        }

        private static string PumpLogTest()
        {
            var lines = new List<string>();
            var pump = new DevPump(r => r.Cmd == "bad" ? DevProtocol.Err(r.Id, "bad_args", "x", null) : DevProtocol.Ok(r.Id, "x", null), null, null,
                                   (r, reply, ms, waitMs) => lines.Add(DevCmdLog.Line(DateTime.Now, r.Cmd, reply, ms, waitMs)));
            pump.Enqueue(new DevRequest { Id = 1.0, Cmd = "state" });
            pump.Enqueue(new DevRequest { Id = 2.0, Cmd = "bad" });
            pump.Pump(4, 5000); // wide budget: cold JIT under a parallel build can eat 100 ms on the first command (flake 2026-11-03)
            if (lines.Count != 2) return "pump log: " + lines.Count + " lines";
            if (!lines[0].Contains(" [Dev] cmd state -> ok exec_ms=") || !lines[1].Contains(" [Dev] cmd bad -> !bad_args exec_ms=")) return "pump log: " + string.Join(" | ", lines);
            var thrower = new DevPump(r => DevProtocol.Ok(r.Id, "x", null), null, null, (r, reply, ms, w) => { throw new InvalidOperationException("log"); });
            thrower.Enqueue(new DevRequest { Id = 3.0, Cmd = "state" });
            if (thrower.Pump(4, 100) != 1) return "pump log: a throwing logger must not break the pump";
            return null;
        }

        private static string PumpTest()
        {
            long clock = 0;
            var ran = new List<string>();
            var timeouts = new List<string>();
            var gate = new ManualResetEventSlim(false);
            Func<DevRequest, string> exec = r =>
            {
                if (r.Cmd == "slow") { gate.Wait(10000); }
                clock += 1; ran.Add(r.Cmd);
                return DevProtocol.Ok(r.Id, "x", null);
            };
            var pump = new DevPump(exec, (r, why) => timeouts.Add(r.Cmd + ":" + why), () => clock);
            for (int i = 0; i < 10; i++) pump.Enqueue(new DevRequest { Id = (double)i, Cmd = "c" + i });
            if (pump.Pump(4, 100) != 4 || ran.Count != 4) return "max 4 per pump";
            if (pump.Pump(4, 2) != 2) return "budget 2 ms";
            while (pump.Pump(4, 100) > 0) { }
            if (ran.Count != 10) return "drain";
            // timeout before start: never executed later
            ran.Clear();
            var p = pump.Enqueue(new DevRequest { Id = 99.0, Cmd = "attack" });
            string reply = pump.Await(p, 50);
            if (!reply.Contains("\"timeout\"") || !reply.Contains("not executed")) return "timeout not executed";
            pump.Pump();
            if (ran.Count != 0) return "cancelled request must not run";
            if (timeouts.Count != 1 || timeouts[0] != "attack:not executed") return "timeout callback";
            // timeout while running
            var slow = pump.Enqueue(new DevRequest { Id = 100.0, Cmd = "slow" });
            var th = new Thread(() => pump.Pump());
            th.Start();
            Thread.Sleep(50);
            string r2 = pump.Await(slow, 50);
            gate.Set();
            th.Join();
            if (!r2.Contains("outcome unknown")) return "timeout outcome unknown: " + r2;
            return null;
        }

        // ------------------------------------------------------------------ serve

        private static int Serve(string[] argv)
        {
            string dir = null; bool fireOn = true, forceOn = true, noSlots = false, noDiag = false; int idle = 60, timeoutMs = 5000, maxClients = 2; string failCmd = null, failCode = null;
            for (int i = 1; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "--dir": dir = argv[++i]; break;
                    case "--fire": fireOn = argv[++i] != "off"; break;
                    case "--force": forceOn = argv[++i] != "off"; break;   // T-361
                    case "--fail": { string[] kv = argv[++i].Split('='); failCmd = kv[0]; failCode = kv[1]; break; }   // T-361: the fake link answers this error for one cmd
                    case "--no-slots": noSlots = true; break;
                    case "--no-diag": noDiag = true; break;
                    case "--stall": FakeGame.Stall = true; break;
                    case "--idle-s": idle = int.Parse(argv[++i]); break;
                    case "--timeout-ms": timeoutMs = int.Parse(argv[++i]); break;
                    case "--max-clients": maxClients = int.Parse(argv[++i]); break;   // T-227
                }
            }
            if (dir == null) { Console.Error.WriteLine("--dir required"); return 2; }
            Directory.CreateDirectory(dir);
            bool stall = FakeGame.Stall;
            if (noSlots) FakeGame.Uninstall(); else FakeGame.Install(!noDiag, true);
            FakeGame.Stall = stall;
            if (failCmd != null) FakeGame.Fail[failCmd] = failCode;
            FakeGame.CallsFile = Path.Combine(dir, "calls.jsonl");
            string logPath = Path.Combine(dir, "log.txt");
            var ring = new DevLogRing();
            var exec = new DevExecutor(ring, () => fireOn, line => { lock (ring) File.AppendAllText(logPath, line + "\n"); }, null, null, null, () => forceOn);
            var pump = new DevPump(exec.Handle, exec.FireTimeout);
            string token = DevSession.NewToken();
            var server = new DevServer(new[] { 0 }, token, r => pump.Await(pump.Enqueue(r), timeoutMs), exec.Hello, TimeSpan.FromSeconds(idle), 65536, line => { lock (ring) File.AppendAllText(logPath, line + "\n"); }, maxClients);
            if (!server.Start()) return 3;
            DevSession.Write(dir, server.Port, token, Environment.ProcessId, DevInfo.Version, DateTime.UtcNow.ToString("o"), fireOn);
            Console.WriteLine("serving " + server.Port);
            Console.Out.Flush();
            bool closed = false;
            new Thread(() => { try { Console.In.ReadToEnd(); } catch (Exception) { } closed = true; }) { IsBackground = true }.Start();
            while (!closed) { pump.Pump(); Thread.Sleep(5); }
            server.Stop();
            DevSession.Delete(dir);
            return 0;
        }
    }
}
