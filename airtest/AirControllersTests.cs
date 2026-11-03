using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    /// <summary>D1: the ASW/SAR controller and the CAP controller on their fakes, one broker, one deck (ADR-0042 "Проверка").</summary>
    internal static class AirControllersTests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";

        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private sealed class Rig
        {
            public readonly AswFakeGame A = new AswFakeGame();
            public readonly CapFakeGame C = new CapFakeGame();
            public readonly MuxAirOps Mux;
            public AirOwnership Own = new AirOwnership();
            public readonly List<string> Logs = new List<string>();
            public AirDeckBroker B;
            public AswController AC;
            public CapController CC;
            public double T0;

            public Rig(double yieldMax = 120)
            {
                Mux = new MuxAirOps(A, C);
                A.Now = C.Now;
                T0 = C.Now;
                B = new AirDeckBroker(Mux, Own, new AirDeckParams { YieldMaxS = yieldMax }, Logs.Add, null);
                AC = new AswController(A, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }) { Ownership = Own, Deck = B };
                CC = new CapController(C, new CapParams { Debug = true }, Own) { Deck = B };
                Own.Register("asw", AC.GroupNameOf);
            }

            /// <summary>Ground deck member of the ASW fake (a prepared task "task:g1" or a hangar machine "hangar:SH-3H").</summary>
            public AswMember Deck(int uid, string key, int deckUid = 100)
            {
                var m = AswFakeGame.Helo(uid, "deck_ready");
                m.Uid = -uid; m.DeckKey = key; m.DeckUid = deckUid; m.Dipping = true; m.Type = MuxAirOps.AswType;
                A.Mem.Add(m);
                return m;
            }

            public string AswGroup(string name, params int[] uids)
            {
                A.Decks[100] = new AswTarget { Uid = 100, Lat = 35, Lon = 20 };
                string r = AC.Group(J("{\"op\":\"create\",\"group\":\"" + name + "\",\"uids\":[" + string.Join(",", uids) + "]}"));
                if (AswController.IsErr(r)) return r;
                r = AC.SearchCourse(J("{\"group\":\"" + name + "\",\"tactics\":[\"dip\"]}"));
                return AswController.IsErr(r) ? r : null;
            }

            public string CapGroup(int n, int k = 1, string uids = "1,2,3")
            {
                string r = CC.Group(J("{\"op\":\"create\",\"uids\":[" + uids + "]}"));
                if (r.StartsWith("!")) return r;
                r = CC.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":" + n + ",\"ready\":" + k + "}"));
                return r.StartsWith("!") ? r : null;
            }

            public void Threat() { C.Contacts.Add(new CapContact { Id = 77, Lat = 10.83333 + 70.0 / 60, Lon = 11.3, Hostile = true }); }

            public void Step(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    A.Advance(1); C.Advance(1);
                    Mux.GameTick(); Mux.TickNo++;
                    double t = C.Now;
                    B.BeginTick(t); AC.Tick(t); CC.Tick(t); B.EndTick(t);
                }
            }

            public double Elapsed { get { return C.Now - T0; } }
            public bool Log(string part) { foreach (var l in Logs) if (l.Contains(part)) return true; return false; }
            public string LogLine(string part) { foreach (var l in Logs) if (l.Contains(part)) return l; return null; }
        }

        public static string Run()
        {
            string e;
            if ((e = Order()) != null) return "order: " + e;
            if ((e = AllowYield()) != null) return "yield: " + e;
            if ((e = Timeout()) != null) return "timeout: " + e;
            if ((e = OtherDeck()) != null) return "other deck: " + e;
            if ((e = BySeq()) != null) return "seq: " + e;
            if ((e = SaveLoad()) != null) return "save/load: " + e;
            if ((e = Diag()) != null) return "diag: " + e;
            return null;
        }

        // the CAP group with a gap under an air threat gets the deck first; the ASW order follows and is held (allow=false)
        private static string Order()
        {
            var r = new Rig();
            r.CapGroup(1);
            r.Deck(1, "hangar:SH-3H");
            string err = r.AswGroup("ASW-1", -1);
            if (err != null) return err;
            r.Threat();
            r.Step(6);
            int capAt = r.Mux.IndexOf("order", "cap"), aswAt = r.Mux.IndexOf("order", "asw");
            if (capAt < 0) return "no CAP order: " + string.Join("|", r.Mux.Calls);
            if (aswAt < 0) return "no ASW order: " + string.Join("|", r.Mux.Calls);
            if (capAt > aswAt) return "CAP order is not first: " + string.Join("|", r.Mux.Calls);
            if (r.A.Calls.Exists(x => x.StartsWith("launch:", StringComparison.Ordinal))) return "ASW launched under the flag";
            if (!r.A.Calls.Exists(x => x.StartsWith("prepare:", StringComparison.Ordinal))) return "ASW machine not prepared (order with allow=false)";
            if (r.B.HeldS("asw", "ASW-1") < 0) return "ASW not held: " + string.Join("|", r.Mux.Calls) + " logs " + string.Join(" | ", r.Logs.ToArray()) + " C.logs " + string.Join(" | ", r.C.Logs.ToArray());
            return null;
        }

        // an allow of ASW waits while the flag stands; the threat goes away -> the allow is executed (reason flag_off)
        private static string AllowYield()
        {
            var r = new Rig();
            r.CapGroup(1);
            r.Deck(1, "task:g1");
            string err = r.AswGroup("ASW-1", -1);
            if (err != null) return err;
            r.Threat();
            r.Step(30);
            if (r.A.Allows.Count != 0) return "allow executed under the flag: " + string.Join("|", r.Mux.Calls);
            if (r.B.HeldS("asw", "ASW-1") < 20) return "held seconds " + r.B.HeldS("asw", "ASW-1");
            r.C.Contacts.Clear();
            r.Step(120);
            if (r.A.Allows.Count != 1) return "allow count after the threat left: " + r.A.Allows.Count + " logs " + string.Join(" | ", r.Logs.ToArray());
            if (!r.Log("yield ASW-1 held=") || !r.Log("reason=flag_off")) return "yield line: " + string.Join(" | ", r.Logs.ToArray());
            if (r.B.HeldS("asw", "ASW-1") != -1) return "still held";
            return null;
        }

        // the threat stays: the allow goes at 120 s of waiting
        private static string Timeout()
        {
            var r = new Rig();
            r.CapGroup(1);
            r.Deck(1, "task:g1");
            string err = r.AswGroup("ASW-1", -1);
            if (err != null) return err;
            r.Threat();
            r.Step(100);
            if (r.A.Allows.Count != 0) return "allowed before the time: " + r.Elapsed;
            r.Step(30);
            if (r.A.Allows.Count != 1) return "no allow at ~120 s, elapsed " + r.Elapsed + " logs " + string.Join(" | ", r.Logs.ToArray());
            if (!r.Log("reason=timeout")) return "no timeout reason: " + string.Join(" | ", r.Logs.ToArray());
            // the first hold starts with the first request, a second or two after the start
            string line = r.LogLine("reason=timeout");
            if (!line.Contains("held=12") && !line.Contains("held=11")) return "held seconds in the line: " + line;
            return null;
        }

        // the ASW machine of another deck does not wait
        private static string OtherDeck()
        {
            var r = new Rig();
            r.CapGroup(1);
            r.Deck(1, "task:g1", 101);
            string err = r.AswGroup("ASW-1", -1);
            if (err != null) return err;
            r.Threat();
            r.Step(5);
            if (r.A.Allows.Count != 1) return "other deck: allow count " + r.A.Allows.Count;
            if (r.B.HeldS("asw", "ASW-1") != -1) return "other deck: held";
            return null;
        }

        // no flag, no contact: the order between the windows follows GroupSeq (the older group first)
        private static string BySeq()
        {
            foreach (bool capFirst in new[] { true, false })
            {
                var r = new Rig();
                r.Deck(1, "hangar:SH-3H");
                string err;
                if (capFirst) { if ((err = r.CapGroup(1)) != null) return err; if ((err = r.AswGroup("ASW-1", -1)) != null) return err; }
                else { if ((err = r.AswGroup("ASW-1", -1)) != null) return err; if ((err = r.CapGroup(1)) != null) return err; }
                r.Step(8);
                int capAt = r.Mux.IndexOf("order", "cap"), aswAt = r.Mux.IndexOf("order", "asw");
                if (capAt < 0 || aswAt < 0) return "capFirst=" + capFirst + " missing orders: " + string.Join("|", r.Mux.Calls);
                string[] c = r.Mux.Calls[capAt].Split(' '), a = r.Mux.Calls[aswAt].Split(' ');
                if (c[0] != a[0]) return "capFirst=" + capFirst + " the orders came in different ticks: " + string.Join("|", r.Mux.Calls);
                if ((capAt < aswAt) != capFirst) return "capFirst=" + capFirst + " wrong order: " + string.Join("|", r.Mux.Calls);
            }
            return null;
        }

        // both windows save, a fresh ownership reads them back: the numbers (and so the order) are the same
        private static string SaveLoad()
        {
            string dir = Path.Combine(Path.GetTempPath(), "airtest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var r = new Rig();
                r.A.SaveDir = dir; r.A.LastSavePath = "/s/a.sav";
                r.Deck(1, "task:g1"); r.Deck(2, "task:g2");
                string err;
                if ((err = r.AswGroup("ASW-1", -1)) != null) return err;
                if ((err = r.CapGroup(1)) != null) return err;
                if ((err = r.AswGroup("ASW-2", -2)) != null) return err;
                r.Step(3);
                int s1 = r.Own.GroupSeq("asw", "ASW-1"), s2 = r.Own.GroupSeq("cap", "ПВО-1"), s3 = r.Own.GroupSeq("asw", "ASW-2");
                if (s1 != 1 || s2 != 2 || s3 != 3) return "numbers before the save " + s1 + "/" + s2 + "/" + s3;
                var capData = CapSave.Parse(CapSave.ToJson(r.CC.Save()), out err);
                if (capData == null) return "cap save: " + err;
                r.A.LastSavePath = "/s/B.sav";
                r.Step(1);
                // a new mission: fresh ownership, fresh controllers; the same deck members (names), new uids
                var g = new Rig();
                g.A.SaveDir = dir; g.A.Now = r.A.Now;
                var x = AswFakeGame.Helo(11, "air"); x.Name = "Helo 1";
                var y = AswFakeGame.Helo(12, "air"); y.Name = "Helo 2";
                g.A.Mem.Add(x); g.A.Mem.Add(y);
                g.CC.Load(capData, g.C.Now);
                g.AC.OnMissionChanged("/s/b.sav", r.A.Now + 2);
                int t1 = g.Own.GroupSeq("asw", "ASW-1"), t2 = g.Own.GroupSeq("cap", "ПВО-1"), t3 = g.Own.GroupSeq("asw", "ASW-2");
                if (t1 != s1 || t2 != s2 || t3 != s3) return "numbers after the load " + t1 + "/" + t2 + "/" + t3;
                if (g.Own.GroupSeq("cap", "NEW") != 4) return "the next group does not take max + 1";
                return null;
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        // IUiDiag "deck": the snapshot shows the held key, the deadline and the order line
        private static string Diag()
        {
            var r = new Rig();
            r.CapGroup(1);
            r.Deck(1, "task:g1");
            string err = r.AswGroup("ASW-1", -1);
            if (err != null) return err;
            r.Threat();
            r.Step(40);
            string json = r.B.DiagJson(10);
            var root = J(json);
            var decks = (List<object>)root["decks"];
            if (decks.Count != 1) return "decks " + json;
            var d = (Dictionary<string, object>)decks[0];
            if ((double)d["ship_uid"] != 100) return "ship_uid " + json;
            double until = (double)d["yield_until_s"];
            if (until < 70 || until > 90) return "yield_until_s " + until + " " + json;
            var q = (List<object>)d["queue"];
            bool held = false;
            foreach (object o in q) { var qi = (Dictionary<string, object>)o; if ((string)qi["key"] == "task:g1" && (double)qi["hold_s"] >= 30) held = true; }
            if (!held) return "held key missing: " + json;
            return null;
        }
    }
}
