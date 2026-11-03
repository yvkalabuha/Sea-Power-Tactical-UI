using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-241 (ADR-0099): the search radar of a CAP aircraft is on while it flies its task and comes back as it was. Run("name") / Run() = all.
    internal static class CapT241Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";

        private sealed class Rig
        {
            public CapFakeGame G = new CapFakeGame();
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams();
            public CapController C;
            public AirRadarFakeIo Io = new AirRadarFakeIo();
            public AirRadarLedger L;
            public Rig(bool withRadar = true, bool hasAir = true, bool airOn = false)
            {
                for (int u = 1; u <= 9; u++) Io.Add(u, hasAir, false, airOn);
                C = new CapController(G, P, Own);
                if (withRadar) { L = new AirRadarLedger(Io); C.Radar = L; }
            }
            public void Step(int n, CapController c = null)
            {
                for (int i = 0; i < n; i++) { G.Advance(1); Io.NowS = G.Now; G.Tick(c ?? C); }
            }
            public string Create(string uids) { return C.Group(J("{\"op\":\"create\",\"uids\":[" + uids + "]}")); }
            public string Ord(string rect, int n, int k) { return C.Order(J("{\"group\":\"ПВО-1\"," + rect + ",\"on_station\":" + n + ",\"ready\":" + k + "}")); }
            public int On(int uid) { return Io.SetCount(uid, " on"); }
            public int Off(int uid) { return Io.SetCount(uid, " off"); }
            public int AllOn() { int n = 0; foreach (var c in Io.Calls) if (c.EndsWith(" on")) n++; return n; }
            public int AllOff() { int n = 0; foreach (var c in Io.Calls) if (c.EndsWith(" off")) n++; return n; }
        }

        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static Rig OnStation(string uids, int n, int k, int steps = 900, bool hasAir = true, bool airOn = false, bool withRadar = true)
        {
            var r = new Rig(withRadar, hasAir, airOn);
            r.Create(uids); r.Ord(ZA, n, k);
            r.Step(steps);
            return r;
        }

        private static int Flying(Rig r) { int n = 0; foreach (var b in r.G.All) if (b.State == "Air") n++; return n; }

        public static string Run(string only = null)
        {
            var all = new List<KeyValuePair<string, Func<string>>>
            {
                new KeyValuePair<string, Func<string>>("patrol_on", PatrolOn),
                new KeyValuePair<string, Func<string>>("already_on", AlreadyOn),
                new KeyValuePair<string, Func<string>>("leave_restore", LeaveRestore),
                new KeyValuePair<string, Func<string>>("stop_restore", StopRestore),
                new KeyValuePair<string, Func<string>>("disband_restore", DisbandRestore),
                new KeyValuePair<string, Func<string>>("release_restore", ReleaseRestore),
                new KeyValuePair<string, Func<string>>("alert_scramble", AlertScramble),
                new KeyValuePair<string, Func<string>>("lost_no_calls", LostNoCalls),
                new KeyValuePair<string, Func<string>>("no_radar", NoRadar),
                new KeyValuePair<string, Func<string>>("autoradar_off_live", AutoRadarOff),
                new KeyValuePair<string, Func<string>>("radar_null", RadarNull),
                new KeyValuePair<string, Func<string>>("player_off", PlayerOff),
                new KeyValuePair<string, Func<string>>("save_load", SaveLoad),
                new KeyValuePair<string, Func<string>>("old_file", OldFile),
                new KeyValuePair<string, Func<string>>("player_saved", PlayerSaved),
            };
            bool known = false;
            foreach (var kv in all)
            {
                if (only != null && only != kv.Key) continue;
                known = true;
                string e = kv.Value();
                if (e != null) return kv.Key + ": " + e;
            }
            return only != null && !known ? "unknown scenario " + only : null;
        }

        private static string PatrolOn()
        {
            var r = OnStation("1,2,3,4", 2, 1);
            int fl = Flying(r);
            if (fl < 2) return "birds not flying " + fl;
            foreach (var b in r.G.All) if (b.State == "Air" && (r.On(b.Uid) != 1 || r.Off(b.Uid) != 0 || r.L.StateOf(b.Uid) != "on")) return "bird " + b.Uid + " on=" + r.On(b.Uid) + " off=" + r.Off(b.Uid) + " state=" + r.L.StateOf(b.Uid);
            foreach (var b in r.G.All) if (b.State != "Air" && r.On(b.Uid) != 0) return "a bird that did not fly got a radar command " + b.Uid;
            if (!r.G.LogHas("[Cap] radar on uid=")) return "no journal line";
            r.Step(60);
            if (r.AllOn() != fl) return "repeated set on";
            var st = (Dictionary<string, object>)Json.Parse(r.C.StateJson());
            if (!r.C.StateJson().Contains("\"radar\":\"on\"")) return "state json has no radar";
            return null;
        }

        private static string AlreadyOn()
        {
            var r = OnStation("1,2", 1, 1, 900, true, true);
            if (Flying(r) < 1) return "no bird flies";
            if (r.Io.Calls.Count != 0) return "calls on a radar that was on: " + string.Join("|", r.Io.Calls);
            if (r.L.StateOf(1) != "untouched" && r.L.StateOf(2) != "untouched") return "state";
            return null;
        }

        private static string LeaveRestore()
        {
            var r = OnStation("1,2", 1, 1);
            if (r.On(1) != 1) return "radar not on for bird 1";
            r.G.Bird(1).AamNow = 1; r.Step(3);
            if (r.Off(1) != 1 || r.Io.Units[1].AirOn) return "no switch-off on leaving off=" + r.Off(1);
            for (int i = 0; i < 900 && r.G.Bird(1).State == "Air"; i++) r.Step(1);
            if (r.G.Bird(1).State == "Air") return "did not land";
            r.Step(5);
            if (r.Off(1) != 1 || r.On(1) != 1) return "calls after landing on=" + r.On(1) + " off=" + r.Off(1);
            if (r.L.Has(1)) return "entry kept after landing";
            return null;
        }

        private static string StopRestore()
        {
            var r = OnStation("1,2", 1, 1);
            int fl = Flying(r);
            r.C.Stop(J("{\"group\":\"ПВО-1\"}")); r.Step(3);
            if (r.AllOff() != fl) return "off after cap_stop " + r.AllOff() + " of " + fl;
            return null;
        }

        private static string DisbandRestore()
        {
            var r = OnStation("1,2", 1, 1);
            int fl = Flying(r);
            r.C.Group(J("{\"op\":\"disband\",\"group\":\"ПВО-1\"}")); r.Step(2);
            if (r.AllOff() != fl) return "off after disband " + r.AllOff() + " of " + fl;
            if (r.L.Count != 0) return "ledger not empty";
            return null;
        }

        private static string ReleaseRestore()
        {
            var r = OnStation("1,2", 1, 1);
            var air = new List<int>(); foreach (var b in r.G.All) if (b.State == "Air") air.Add(b.Uid);
            if (air.Count == 0) return "nobody flies";
            r.C.ReleaseUid(air[0]);
            if (r.Off(air[0]) != 1) return "off after Return control";
            return null;
        }

        private static string AlertScramble()
        {
            var r = new CapT179Tests.Rig();
            var io = new AirRadarFakeIo(); for (int u = 1; u <= 9; u++) io.Add(u);
            var l = new AirRadarLedger(io); r.C.Radar = l;
            Action<int> step = n => { for (int i = 0; i < n; i++) { r.G.Advance(1); io.NowS = r.G.Now; r.G.Tick(r.C); } };
            r.Create("1,2,3"); r.Alert("ПВО-1", ",\"after\":\"alert\"");
            step(900);
            if (io.Calls.Count != 0) return "calls for machines on the deck: " + string.Join("|", io.Calls);
            r.C.ScrambleNow(CapT179Tests.J("{\"group\":\"ПВО-1\"}"));
            step(60);
            int on = 0; foreach (var c in io.Calls) if (c.EndsWith(" on")) on++;
            if (on < 2) return "scrambled pair radar on=" + on;
            step(1400);
            int off = 0, on2 = 0; foreach (var c in io.Calls) { if (c.EndsWith(" off")) off++; if (c.EndsWith(" on")) on2++; }
            if (off != on2 || l.Count != 0) return "not given back after the return: on=" + on2 + " off=" + off + " entries=" + l.Count;
            return null;
        }

        private static string LostNoCalls()
        {
            var r = OnStation("1,2", 1, 1);
            if (r.On(1) != 1) return "not on";
            r.G.All.Remove(r.G.Bird(1));
            r.Step(60);
            if (r.Off(1) != 0) return "a game call for a lost bird";
            if (r.L.Has(1)) return "entry kept for a lost bird";
            return null;
        }

        private static string NoRadar()
        {
            var r = OnStation("1,2", 1, 1, 900, false);
            if (Flying(r) < 1) return "no bird flies";
            if (r.Io.Calls.Count != 0) return "calls without a radar";
            if (!r.G.LogHas("[Cap] radar none uid=") || !r.G.LogHas("reason=no_radar")) return "no journal line";
            return null;
        }

        private static string AutoRadarOff()
        {
            var r = OnStation("1,2", 1, 1);
            int fl = Flying(r);
            if (r.AllOn() != fl) return "not on";
            r.C.AutoRadar = false; r.Step(3);
            if (r.AllOff() != fl) return "off after AutoRadar=false " + r.AllOff();
            r.C.AutoRadar = true; r.Step(3);
            if (r.AllOn() != 2 * fl) return "on again after AutoRadar=true " + r.AllOn();
            return null;
        }

        private static string RadarNull()
        {
            var r = OnStation("1,2", 1, 1, 900, true, false, false);
            if (Flying(r) < 1) return "no bird flies";
            if (r.Io.Calls.Count != 0) return "calls with no ledger";
            var d = r.C.Save(); foreach (var m in d.Memory) if (m.Radar != null) return "radar memory without a ledger";
            return null;
        }

        private static string PlayerOff()
        {
            var r = OnStation("1,2", 1, 1);
            if (r.On(1) != 1) return "not on";
            r.Io.PlayerOff(1); r.Step(3);
            if (r.L.StateOf(1) != "player" || !r.G.LogHas("radar player_off uid=1")) return "player_off not seen";
            r.Step(60);
            if (r.On(1) != 1) return "switched on again";
            r.G.Bird(1).AamNow = 1; r.Step(3);
            if (r.Off(1) != 0) return "the player's radar was touched";
            return null;
        }

        private static CapSaveData RoundTrip(CapSaveData d, bool dropRadar)
        {
            string json = CapSave.ToJson(d);
            if (dropRadar) { int i = json.IndexOf("\"radar\":{"); while (i >= 0) { int j = json.IndexOf('}', i); json = json.Remove(i - 1, j - i + 2); i = json.IndexOf("\"radar\":{"); } }
            string err; var back = CapSave.Parse(json, out err);
            return err == null ? back : null;
        }

        private static Rig Reload(Rig r, CapSaveData d, out CapController c2)
        {
            c2 = new CapController(r.G, r.P, new AirOwnership());
            r.L = new AirRadarLedger(r.Io); c2.Radar = r.L;
            c2.Load(d, r.G.Now);
            r.Step(5, c2);
            return r;
        }

        private static string SaveLoad()
        {
            var r = OnStation("1,2", 1, 1);
            int fl = Flying(r);
            var d = r.C.Save();
            string json = CapSave.ToJson(d);
            if (!json.Contains("\"radar\":{") || !json.Contains("\"saved_on\":true")) return "json has no radar: " + json;
            var back = RoundTrip(d, false);
            if (back == null) return "parse";
            CapController c2; Reload(r, back, out c2);
            if (r.L.StateOf(1) == null && r.L.StateOf(2) == null) return "memory not imported";
            c2.Stop(J("{\"group\":\"ПВО-1\"}")); r.Step(3, c2);
            if (r.AllOff() != fl) return "off after load+stop " + r.AllOff() + " of " + fl;
            return null;
        }

        private static string OldFile()
        {
            var r = OnStation("1,2", 1, 1);
            var back = RoundTrip(r.C.Save(), true);
            if (back == null) return "parse of a file without radar";
            foreach (var m in back.Memory) if (m.Radar != null) return "radar in an old file";
            int before = r.Io.Calls.Count;
            CapController c2; Reload(r, back, out c2);
            // the radar is on (the game kept it): the loaded group finds it on and treats it as the player's own
            c2.Stop(J("{\"group\":\"ПВО-1\"}")); r.Step(3, c2);
            if (r.Io.Calls.Count != before) return "calls after loading an old file: " + string.Join("|", r.Io.Calls);
            return null;
        }

        private static string PlayerSaved()
        {
            var r = OnStation("1,2", 1, 1);
            int uid = -1; foreach (var b in r.G.All) if (b.State == "Air") { uid = b.Uid; break; }
            r.Io.PlayerOff(uid); r.Step(3);
            if (r.L.StateOf(uid) != "player") return "player_off not seen";
            var d = r.C.Save();
            if (!CapSave.ToJson(d).Contains("\"player\":true")) return "player flag not saved";
            var back = RoundTrip(d, false);
            int on0 = r.On(uid);
            CapController c2; Reload(r, back, out c2);
            r.Step(30, c2);
            if (r.On(uid) != on0) return "switched on after the load";
            if (r.L.StateOf(uid) != "player") return "player flag lost " + r.L.StateOf(uid);
            return null;
        }
    }
}
