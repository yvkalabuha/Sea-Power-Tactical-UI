using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    // T-211 (ADR-0088): CAP groups after a load wait for the world (DeferRestore / ApplyDeferred), the grace of a restored member,
    // the served raids in the file. Scenarios of `captest t211 <name>`.
    internal static class CapT211Tests
    {
        private static readonly string[] Names =
        {
            "deferred-empty-then-birds", "restore-grace", "save-while-waiting", "commands-waiting", "ask-waiting", "immediate-legacy",
            "served-roundtrip", "served-key-only", "deferred-member-uids",
        };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private static readonly DateTime Utc = new DateTime(2026, 10, 23, 6, 0, 0, DateTimeKind.Utc);
        private const string Sav = "/x/a.sav";
        private static SavStamp Loaded() { return new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T06:00:00Z", Src = "sav" }; }

        /// <summary>CAPT211_DUMP=1: the log of the last rig of a failed scenario (debugging aid).</summary>
        public static string Run(string only = null)
        {
            string res = RunInner(only);
            if (res != null && Environment.GetEnvironmentVariable("CAPT211_DUMP") == "1" && CapT179Tests.Rig.All.Count > 0)
                foreach (string l in CapT179Tests.Rig.All[CapT179Tests.Rig.All.Count - 1].G.Logs) Console.WriteLine(l);
            return res;
        }

        private static string RunInner(string only)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "deferred-empty-then-birds": e = DeferredEmptyThenBirds(); break;
                    case "restore-grace": e = RestoreGrace(); break;
                    case "save-while-waiting": e = SaveWhileWaiting(); break;
                    case "commands-waiting": e = CommandsWaiting(); break;
                    case "ask-waiting": e = AskWaiting(); break;
                    case "immediate-legacy": e = ImmediateLegacy(); break;
                    case "served-roundtrip": e = ServedRoundTrip(false); break;
                    case "deferred-member-uids": e = DeferredMemberUids(); break;
                    default: e = ServedRoundTrip(true); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        private static string TmpDir() { return Path.Combine(Path.GetTempPath(), "captest211-" + Guid.NewGuid().ToString("N")); }
        private static void Rm(string dir) { try { Directory.Delete(dir, true); } catch (Exception) { } }

        private static CapGroupDto Dto(string name, int seq, params CapMemberDto[] members)
        {
            return new CapGroupDto
            {
                Group = name, Seq = seq, OnStation = 2, Ready = 0, Roe = "free", Rect = new[] { 10.5, 11, 10.83333, 11.66667 }, Anchor = "geo",
                Loadouts = new Dictionary<string, string>(), P = new CapParams(), Members = new List<CapMemberDto>(members), Keys = new List<string>(),
            };
        }

        /// <summary>A file with two groups: 5 on station, 6 flying home (slot -2) | 2 on station, 1 on a deck. Written as the slot of <see cref="Sav"/>.</summary>
        private static void WriteFile(string dir)
        {
            var w = new CapT179Tests.Rig(null, "Cheater");
            w.G.SavePath = Sav; w.G.SaveDirectory = dir;
            var d = new CapSaveData { MissionTimeS = 2425.6 };
            d.Groups.Add(Dto("ПВО-1", 1,
                new CapMemberDto { Uid = 5, Name = "Eagle 5", Type = "F-15C", Slot = 0 },
                new CapMemberDto { Uid = 6, Name = "Eagle 6", Type = "F-15C", Slot = -2 }));
            d.Groups.Add(Dto("ПВО-2", 2,
                new CapMemberDto { Uid = 2, Name = "Tomcat 2", Type = "F-14A", Slot = 1 },
                new CapMemberDto { Uid = 1, Name = "Tomcat 1", Type = "F-14A", Slot = -1 }));
            w.C.Load(d, w.G.NowS);
            if (!w.C.PersistNow(Sav, SavStamp.Now(2425.6, Utc, "hook"))) throw new InvalidOperationException("cannot write the slot");
        }

        private static CapT179Tests.Rig Reader(string dir, Action<CapParams> cfg = null)
        {
            var o = new CapT179Tests.Rig(cfg, "Cheater");
            o.G.SavePath = Sav; o.G.SaveDirectory = dir;
            return o;
        }

        /// <summary>The state right after a load: the lists of the game are still empty (no airborne machines, the deck hangar row is not there).</summary>
        private static void Empty(CapT179Tests.Rig o)
        {
            o.G.Bird(5).State = "Gone"; o.G.Bird(6).State = "Gone"; o.G.Bird(2).State = "Gone"; o.G.HideUids.Add(1);
        }

        private static void Fill(CapT179Tests.Rig o)
        {
            o.G.Bird(5).State = "Air"; o.G.Bird(6).State = "Air"; o.G.Bird(2).State = "Air"; o.G.HideUids.Clear();
        }

        private static int Members(CapT179Tests.Rig o) { int n = 0; foreach (var g in o.C.View().Groups) n += g.Members.Count; return n; }
        private static bool MemberOut(CapT179Tests.Rig o) { return o.G.LogHas("member out"); }

        // T-217 (ADR-0089): the uids the post-load gate waits for: Uid > 0, Deck == 0, no task uid, no repeats; empty after the apply / without a file
        private static string DeferredMemberUids()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                var w = new CapT179Tests.Rig(null, "Cheater");
                w.G.SavePath = Sav; w.G.SaveDirectory = dir;
                var d = new CapSaveData { MissionTimeS = 2425.6 };
                d.Groups.Add(Dto("ПВО-1", 1,
                    new CapMemberDto { Uid = 201, Name = "Eagle 1", Type = "F-15C", Slot = 0 },
                    new CapMemberDto { Uid = 202, Name = "Eagle 2", Type = "F-15C", Slot = 1 },
                    new CapMemberDto { Uid = -2000101, Name = "Deck 1", Type = "F-14A", Slot = -1, Deck = 77 },
                    new CapMemberDto { Uid = AirOpsMath.TaskUid("task:abc"), Name = "Task 1", Type = "F-14A", Slot = -1, TaskDeck = 77 }));
                d.Groups.Add(Dto("ПВО-2", 2, new CapMemberDto { Uid = 201, Name = "Eagle 1", Type = "F-15C", Slot = -1, LentFrom = "ПВО-1" }));
                w.C.Load(d, w.G.NowS);
                if (!w.C.PersistNow(Sav, SavStamp.Now(2425.6, Utc, "hook"))) throw new InvalidOperationException("cannot write the slot");
                var o = Reader(dir);
                k.Is("empty without a file", o.C.DeferredMemberUids().Count == 0);
                o.C.DeferRestore = true;
                o.C.OnMissionChanged(Sav, 2.0, Loaded());
                var u = o.C.DeferredMemberUids();
                k.Is("waiting " + string.Join(",", u), o.C.RestoreWaiting);
                k.Is("201 and 202 only: " + string.Join(",", u), u.Count == 2 && u.Contains(201) && u.Contains(202));
                o.C.ApplyDeferred(o.G.NowS);
                k.Is("empty after the apply", o.C.DeferredMemberUids().Count == 0);
            }
            finally { Rm(dir); }
            return k.Err;
        }

        private static string DeferredEmptyThenBirds()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var o = Reader(dir); Empty(o);
                o.C.DeferRestore = true;
                o.C.OnMissionChanged(Sav, 2.0, Loaded());
                k.Is("waiting", o.C.RestoreWaiting && o.C.View().RestoreWaiting);
                o.Step(12);
                k.Is("no groups while the lists are empty", o.C.View().Groups.Count == 0 && o.C.RestoreWaiting);
                k.Is("no member out", !MemberOut(o));
                Fill(o);
                o.C.ApplyDeferred(o.G.NowS);
                k.Is("applied", !o.C.RestoreWaiting && o.C.View().Groups.Count == 2);
                k.Is("log", o.G.LogHas("restore applied after load"));
                o.Step(5);
                k.Is("all three on their places " + Members(o), Members(o) == 4 && o.C.View().Groups.Count == 2);
                k.Is("no member out after the apply", !MemberOut(o));
            }
            finally { Rm(dir); }
            return k.Err;
        }

        private static string RestoreGrace()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var o = Reader(dir); Empty(o);
                o.C.DeferRestore = true;
                o.C.OnMissionChanged(Sav, 2.0, Loaded());
                o.C.ApplyDeferred(o.G.NowS);   // applied while the lists are still empty: the second layer
                k.Is("loaded", o.C.View().Groups.Count == 2);
                o.Step(8);
                o.G.Bird(5).State = "Air";   // shows up on the 8th second
                o.Step(8);
                k.Is("nobody out before the grace ends (16 s)", !MemberOut(o));
                o.Step(8);   // 24 s: 6 and 2 never came
                k.Is("5 stays", o.C.View().Groups[0].Members.Exists(m => m.Uid == 5));
                k.Is("6 gone while leaving after the grace", o.G.LogHas("member out uid=6 group=ПВО-1 (gone while leaving)"));
                k.Is("2 lost after the grace", o.G.LogHas("member out uid=2 group=ПВО-2 (lost)"));
            }
            finally { Rm(dir); }
            return k.Err;
        }

        private static string SaveWhileWaiting()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var o = Reader(dir); Empty(o);
                o.C.DeferRestore = true;
                o.C.OnMissionChanged(Sav, 2.0, Loaded());
                o.Step(3);
                const string sav2 = "/x/b.sav";
                k.Is("persist while waiting", o.C.PersistNow(sav2, SavStamp.Now(2430, Utc, "hook")));
                string path = Path.Combine(dir, "cap", SaveSlotStore.Key(sav2) + ".json");
                k.Is("file exists", File.Exists(path));
                string err; var d = File.Exists(path) ? CapSave.Parse(File.ReadAllText(path), out err) : null;
                k.Is("the 2 groups are in the new file", d != null && d.Groups.Count == 2 && d.Groups[0].Members.Count == 2);
                k.Is("the slot is not cleared", File.Exists(Path.Combine(dir, "cap", SaveSlotStore.Key(Sav) + ".json")));
            }
            finally { Rm(dir); }
            return k.Err;
        }

        private static string CommandsWaiting()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var o = Reader(dir); Empty(o);
                o.C.DeferRestore = true;
                o.C.OnMissionChanged(Sav, 2.0, Loaded());
                string a = o.Create("3,4");
                k.Is("group: " + a, a == "!restore_waiting");
                k.Is("order", o.Patrol("ПВО-1", 1, 0) == "!restore_waiting");
                k.Is("loadout", o.C.Loadout(CapT179Tests.J("{\"group\":\"ПВО-1\",\"type\":\"F-14A\",\"loadout\":\"auto\"}")) == "!restore_waiting");
                k.Is("params", o.C.Params(CapT179Tests.J("{\"missile_pct\":0.3}")) == "!restore_waiting");
                k.Is("stop", o.C.Stop(CapT179Tests.J("{\"group\":\"all\"}")) == "!restore_waiting");
                k.Is("scramble", o.C.ScrambleNow(CapT179Tests.J("{\"group\":\"ПВО-1\"}")) == "!restore_waiting");
                k.Is("view does not change the state", o.C.View().RestoreWaiting && o.C.RestoreWaiting);
                Fill(o); o.C.ApplyDeferred(o.G.NowS);
                a = o.Create("3");
                k.Is("commands work after the apply " + a, a != null && !a.StartsWith("!restore_waiting", StringComparison.Ordinal));
            }
            finally { Rm(dir); }
            return k.Err;
        }

        private static string AskWaiting()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var o = Reader(dir, p => p.RestoreOnLoad = "ask"); Empty(o);
                o.C.DeferRestore = true;
                o.C.OnMissionChanged(Sav, 2.0, Loaded());
                k.Is("waiting, no prompt yet", o.C.RestoreWaiting && !o.C.RestorePending);
                o.C.ResolveRestore(true);
                k.Is("an answer before the gate loads nothing", o.C.View().Groups.Count == 0 && o.C.RestoreWaiting);
                Fill(o); o.C.ApplyDeferred(o.G.NowS);
                k.Is("the prompt goes up after the gate", !o.C.RestoreWaiting && o.C.RestorePending && o.C.View().Groups.Count == 0);
                o.C.ResolveRestore(true);
                k.Is("accepted: 2 groups", o.C.View().Groups.Count == 2);
            }
            finally { Rm(dir); }
            return k.Err;
        }

        private static string ImmediateLegacy()
        {
            var k = new Chk(); string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var o = Reader(dir);
                o.C.OnMissionChanged(Sav, 2.0, Loaded());
                k.Is("restored at once, not waiting", !o.C.RestoreWaiting && o.C.View().Groups.Count == 2 && !o.C.View().RestoreWaiting);
                k.Is("ApplyDeferred without data is a no-op", Do(() => o.C.ApplyDeferred(o.G.NowS)) && o.C.View().Groups.Count == 2);
            }
            finally { Rm(dir); }
            return k.Err;
        }

        private static bool Do(Action a) { try { a(); return true; } catch (Exception) { return false; } }

        // a group "Дежурство" served a raid, Save -> JSON -> Parse -> Load into a new controller over the same world: no second scramble to the same raid
        private static string ServedRoundTrip(bool keyOnly)
        {
            var k = new Chk();
            var r = new CapT179Tests.Rig(null, "Cheater");
            if (keyOnly) r.Notes.Keys[7027] = "r7026";
            r.Create("1,2,3,4"); r.Alert("ПВО-1", ",\"ready\":4,\"per_raid\":2");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(7027, 50));
            r.Step(10);
            Func<CapT179Tests.Rig, int> scrambles = x => { int n = 0; foreach (string l in x.G.Logs) if (l.Contains("alert scramble group=ПВО-1 raid=")) n++; return n; };
            k.Is("first scramble", scrambles(r) == 1);
            r.Step(60);
            var data = r.C.Save();
            k.Is("served in the dto", data.Groups[0].Served != null && data.Groups[0].Served.Contains(7027) && data.Groups[0].ServedRaids != null && data.Groups[0].ServedRaids.Count == 1);
            string err; var back = CapSave.Parse(CapSave.ToJson(data), out err);
            k.Is("file round trip", err == null && back.Groups[0].Served.Contains(7027) && back.Groups[0].ServedRaids.Count == 1);
            var c2 = new CapController(r.G, new CapParams(), new AirOwnership()) { AlertNotes = r.Notes };
            c2.Load(back, r.G.NowS);
            int before = scrambles(r);
            r.G.Contacts.Clear();
            if (keyOnly) { r.Notes.Keys[7050] = "r7026"; r.G.Contacts.Add(CapT179Tests.Raid(7050, 50)); }
            else { r.G.Contacts.Add(CapT179Tests.Raid(7027, 50)); r.G.Contacts.Add(CapT179Tests.Raid(7029, 45, 3)); }
            for (int i = 0; i < 20; i++) { r.G.Advance(1); r.G.Tick(c2); }
            k.Is((keyOnly ? "same key, other id" : "same ids") + ": no second scramble", scrambles(r) == before);
            // a new raid is served
            r.G.Contacts.Clear(); r.G.Contacts.Add(CapT179Tests.Raid(7090, 60, 30));
            for (int i = 0; i < 20; i++) { r.G.Advance(1); r.G.Tick(c2); }
            k.Is("a new raid still scrambles", scrambles(r) == before + 1);
            return k.Err;
        }
    }
}
