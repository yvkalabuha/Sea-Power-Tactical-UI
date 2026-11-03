using System;
using System.IO;

namespace VoiceBotBridge
{
    // T-198 (ADR-0086): restore of CAP groups after a load. Scenarios of `captest t198 <name> [dataDir]`.
    internal static class CapT198Tests
    {
        private static readonly string[] Names = { "persist-stamp", "stamp-match", "stamp-mismatch", "no-stamp-old-rule", "customer" };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private static readonly DateTime Utc = new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc);

        public static string Run(string only = null, string dataDir = null)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                if (n == "customer" && only == null) continue;   // needs the data folder
                switch (n)
                {
                    case "persist-stamp": e = PersistStamp(); break;
                    case "stamp-match": e = StampMatch(); break;
                    case "stamp-mismatch": e = StampMismatch(); break;
                    case "no-stamp-old-rule": e = NoStampOldRule(); break;
                    default: e = Customer(dataDir); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        private static string TmpDir() { return Path.Combine(Path.GetTempPath(), "captest198-" + Guid.NewGuid().ToString("N")); }

        private static CapT179Tests.Rig GroupRig(string dir, string last)
        {
            var r = new CapT179Tests.Rig();
            r.G.SavePath = last; r.G.SaveDirectory = dir;
            r.Create("1,2");
            return r;
        }

        private static CapT179Tests.Rig Fresh(string dir, string last)
        {
            var r = new CapT179Tests.Rig();
            r.G.SavePath = last; r.G.SaveDirectory = dir;
            return r;
        }

        private static string Slot(string dir, string sav) { return Path.Combine(dir, "cap", SaveSlotStore.Key(sav) + ".json"); }

        // the path is an argument (not LastSavePath), "sav" is written only with a stamp
        private static string PersistStamp()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                var r = GroupRig(dir, "/x/last.sav");
                var st = SavStamp.Now(2425.6, Utc, "hook");
                k.Is("persist with stamp", r.C.PersistNow("/x/new.sav", st));
                k.Is("file of the argument path", File.Exists(Slot(dir, "/x/new.sav")) && !File.Exists(Slot(dir, "/x/last.sav")));
                string json = File.ReadAllText(Slot(dir, "/x/new.sav"));
                string err; var d = CapSave.Parse(json, out err);
                k.Is("sav written and read", err == null && d.Sav != null && Math.Abs(d.Sav.ElapsedS - 2425.6) < 1e-9 && d.Sav.Src == "hook" && json.Contains("\"format\":3"));
                k.Is("old call has no stamp", r.C.PersistNow() && !File.ReadAllText(Slot(dir, "/x/last.sav")).Contains("\"sav\""));
                k.Is("no path", !r.C.PersistNow("", st));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return k.Err;
        }

        private static string StampMatch()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                var r = GroupRig(dir, "/x/a.sav");
                r.Step(5);
                k.Is("persist", r.C.PersistNow("/x/a.sav", SavStamp.Now(2425.6, Utc, "hook")));
                var o = Fresh(dir, "/x/a.sav");
                // the game restarts the clock: the mission runs for 2 s, the file says ~1000 s
                o.C.OnMissionChanged("/x/a.sav", 2.0, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:38Z", Src = "sav" });
                k.Is("restored", o.C.View().Groups.Count == 1);
                k.Is("event", o.Events("cap_restored").Contains("\"groups\":1"));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return k.Err;
        }

        private static string StampMismatch()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                var r = GroupRig(dir, "/x/a.sav");
                k.Is("persist", r.C.PersistNow("/x/a.sav", SavStamp.Now(2425.6, Utc, "hook")));
                var o = Fresh(dir, "/x/a.sav");
                o.C.OnMissionChanged("/x/a.sav", 2.0, new SavStamp { ElapsedS = 3100.2, Src = "sav" });
                k.Is("A loaded as B: nothing", o.C.View().Groups.Count == 0);
                k.Is("log", o.G.LogHas("restore skipped: stale file (stamp_mismatch)"));
                var p = Fresh(dir, "/x/a.sav");
                p.C.OnMissionChanged("/x/a.sav", 2.0, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:57Z", Src = "sav" });
                k.Is("other file creation time", p.C.View().Groups.Count == 0 && p.G.LogHas("(created_mismatch)"));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return k.Err;
        }

        // a file without a stamp and a load without a header: the rule of T-122 (file time within 5 s of the mission)
        private static string NoStampOldRule()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                var r = GroupRig(dir, "/x/a.sav");
                r.Step(3);
                double t0 = r.C.Save().MissionTimeS;
                k.Is("persist", r.C.PersistNow());
                var o = Fresh(dir, "/x/a.sav"); o.C.OnMissionChanged("/x/a.sav", t0);
                k.Is("near: restored", o.C.View().Groups.Count == 1);
                var p = Fresh(dir, "/x/a.sav"); p.C.OnMissionChanged("/x/a.sav", t0 + 100);
                k.Is("far: skipped", p.C.View().Groups.Count == 0 && p.G.LogHas("restore skipped: stale file (stale_time)"));
                var q = Fresh(dir, "/x/a.sav"); q.C.OnMissionChanged("/x/a.sav", 2.0, new SavStamp { ElapsedS = 5000 });
                k.Is("file without a stamp, header says 5000 s: legacy_time", q.C.View().Groups.Count == 0 && q.G.LogHas("(legacy_time)"));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return k.Err;
        }

        // the files of the customer's 0.30.5 run: header of the save + the CAP file of the mod (no stamp, mission_time_s 2426.2)
        private static string Customer(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) return "data dir argument is missing";
            var k = new Chk(); string dir = TmpDir();
            try
            {
                string header = Path.Combine(dataDir, "qa0305a_header.sav"), json = Path.Combine(dataDir, "cap_qa0305a.json");
                var loaded = SavStamp.ReadSav(header);
                k.Is("header read", loaded != null && Math.Abs(loaded.ElapsedS - 2425.65602779947) < 1e-9 && loaded.Created == "2026-10-23T02:07:37Z");
                string err; var d = CapSave.Parse(File.ReadAllText(json), out err);
                k.Is("cap file read", err == null && d.Groups.Count == 3 && d.Sav == null);
                k.Is("fresh by the legacy rule", SaveFreshness.Check(d.Sav, d.MissionTimeS, loaded, 3.0) == null);
                // the slot sits under the key of the path the game picker produced (back slashes)
                const string sav = "C:\\users\\steamuser\\AppData\\LocalLow\\Triassic Games\\Sea Power\\saves\\missions\\New Fileqa0305a.sav";
                Directory.CreateDirectory(Path.Combine(dir, "cap"));
                File.Copy(json, Path.Combine(dir, "cap", SaveSlotStore.LegacyKey(sav) + ".json"));
                var o = Fresh(dir, "/x/other.sav");
                o.C.OnMissionChanged(sav, 3.0, loaded);
                k.Is("groups restored", o.C.View().Groups.Count == 3);
                k.Is("event cap_restored groups=3", o.Events("cap_restored").Contains("\"groups\":3"));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return k.Err;
        }
    }
}
