using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    // T-211 (ADR-0088): the ASW restore after a load waits for the world (DeferRestore / ApplyDeferred) and looks for late members. `aswtest t211 <name>`.
    internal static class AswT211Tests
    {
        private static readonly string[] Names = { "deferred-apply", "late-match", "shift-at-apply", "save-while-waiting", "commands-waiting", "deferred-member-uids" };
        private static readonly DateTime Utc = new DateTime(2026, 10, 23, 6, 0, 0, DateTimeKind.Utc);
        private const string Sav = "/s/b.sav";

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }
        private static string TmpDir() { return Path.Combine(Path.GetTempPath(), "asw211-" + Guid.NewGuid().ToString("N")); }
        private static SavStamp Loaded() { return new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T06:00:00Z", Src = "sav" }; }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "deferred-apply": e = DeferredApply(); break;
                    case "late-match": e = LateMatch(); break;
                    case "shift-at-apply": e = ShiftAtApply(); break;
                    case "save-while-waiting": e = SaveWhileWaiting(); break;
                    case "deferred-member-uids": e = DeferredMemberUids(); break;
                    default: e = CommandsWaiting(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        /// <summary>Writes a file with 2 groups (helos 1,2 | 3,4,5) as the slot of <see cref="Sav"/>; the buoy of the first group dropped at 2400 s.</summary>
        private static void WriteFile(string dir)
        {
            var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
            for (int i = 1; i <= 5; i++) f.Mem.Add(AswFakeGame.Helo(i, i % 2 == 1 ? "air" : "deck_ready"));
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
            c.Group(P("{\"op\":\"create\",\"group\":\"G1\",\"uids\":[1,2]}"));
            c.Group(P("{\"op\":\"create\",\"group\":\"G2\",\"uids\":[3,4,5]}"));
            c.SearchCourse(P("{\"group\":\"G1\",\"tactics\":[\"rows\"]}"));
            c.SearchCourse(P("{\"group\":\"G2\",\"tactics\":[\"rows\"]}"));
            Run(c, f, 5);
            c.OnGameSaved(Sav, SavStamp.Now(2425.6, Utc, "hook"));
            string err; string path = Path.Combine(dir, SaveSlotStore.Key(Sav) + ".json");
            var d = AswSave.Parse(File.ReadAllText(path), out err);
            if (d == null || d.Groups.Count != 2) throw new InvalidOperationException("setup: " + err);
            d.MissionTimeS = 2426; d.Buoys.Add(new AswBuoyDto { Lat = 1, Lon = 1, DroppedS = 2400, LifeS = 28800, Ammo = "x", Group = d.Groups[0].Name });
            File.WriteAllText(path, AswSave.ToJson(d));
        }

        private static AswFakeGame World(string dir, params int[] helos)
        {
            var g = new AswFakeGame { SaveDir = dir, LastSavePath = Sav, Now = 3 };
            foreach (int i in helos) g.Mem.Add(AswFakeGame.Helo(i, i % 2 == 1 ? "air" : "deck_ready"));
            return g;
        }

        private static AswController Reader(AswFakeGame g, string mode = "auto")
        {
            var c = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = mode }) { DeferRestore = true };
            c.OnMissionChanged(Sav, 3, Loaded());
            return c;
        }

        private static int Members(AswController c) { int n = 0; foreach (var g in c.View().Groups) n += g.Members.Count; return n; }
        private static bool Warned(AswController c, string s) { return c.View().Warnings.Exists(w => w.Contains(s)); }

        // T-217 (ADR-0089): the uids the post-load gate waits for: Uid > 0, Deck == 0, no task uid, no repeats; empty after the apply / without a file
        private static string DeferredMemberUids()
        {
            string dir = TmpDir();
            try
            {
                WriteFile(dir);
                string err; string path = Path.Combine(dir, SaveSlotStore.Key(Sav) + ".json");
                var d = AswSave.Parse(File.ReadAllText(path), out err);
                if (d == null) return "setup: " + err;
                d.Groups[0].Members.Add(new AswMemberRef { Uid = -2000101, Name = "Slot", Type = "SH-60", Deck = 77 });
                d.Groups[0].Members.Add(new AswMemberRef { Uid = AirOpsMath.TaskUid("task:abc"), Name = "Task", Type = "SH-60" });
                d.Groups[1].Members.Add(new AswMemberRef { Uid = 1, Name = "Repeat", Type = "SH-60" });
                d.Groups[1].Members.Add(new AswMemberRef { Uid = 99, Name = "Deck", Type = "SH-60", Deck = 77 });
                File.WriteAllText(path, AswSave.ToJson(d));
                var g = World(dir);
                var c = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }) { DeferRestore = true };
                if (c.DeferredMemberUids().Count != 0) return "not empty without a file";
                c.OnMissionChanged(Sav, 3, Loaded());
                var u = c.DeferredMemberUids();
                if (!c.RestoreWaiting) return "not waiting";
                if (u.Count != 5 || u.Contains(-2000101) || u.Contains(99) || u.Contains(AirOpsMath.TaskUid("task:abc"))) return "uids " + string.Join(",", u);
                for (int i = 1; i <= 5; i++) if (!u.Contains(i)) return "missing " + i + ": " + string.Join(",", u);
                c.ApplyDeferred(g.Now);
                if (c.DeferredMemberUids().Count != 0) return "not empty after the apply";
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        private static string DeferredApply()
        {
            string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var g = World(dir);   // the lists are empty
                var c = Reader(g);
                if (!c.RestoreWaiting || !c.View().RestoreWaiting) return "not waiting";
                Run(c, g, 6);
                if (c.View().Groups.Count != 0 || c.View().Warnings.Count != 0) return "something happened while waiting: " + c.View().Groups.Count + " groups, warnings " + string.Join("|", c.View().Warnings);
                for (int i = 1; i <= 5; i++) g.Mem.Add(AswFakeGame.Helo(i, i % 2 == 1 ? "air" : "deck_ready"));
                c.ApplyDeferred(g.Now);
                if (c.RestoreWaiting) return "still waiting after the apply";
                if (c.View().Groups.Count != 2 || Members(c) != 5) return "groups " + c.View().Groups.Count + " members " + Members(c) + " " + string.Join(" | ", g.Logs);
                if (!Warned(c, "restored: 2 groups") || Warned(c, "not found")) return "warning: " + string.Join("|", c.View().Warnings);
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        private static string LateMatch()
        {
            string dir = TmpDir();
            try
            {
                WriteFile(dir);
                // 2 of 5 show up 6 s after the apply
                var g = World(dir, 1, 3, 5);
                var c = Reader(g);
                c.ApplyDeferred(g.Now);
                if (Members(c) != 3) return "at the apply 3 of 5, got " + Members(c);
                if (Warned(c, "restored")) return "the summary came before the wait ended";
                Run(c, g, 6);
                g.Mem.Add(AswFakeGame.Helo(2, "deck_ready")); g.Mem.Add(AswFakeGame.Helo(4, "deck_ready"));
                Run(c, g, 3);
                if (Members(c) != 5) return "late members not joined: " + Members(c);
                if (!Warned(c, "restored: 2 groups") || Warned(c, "not found")) return "summary: " + string.Join("|", c.View().Warnings);
                // one never comes
                var h = World(dir, 1, 3, 5, 4);
                var d = Reader(h);
                d.ApplyDeferred(h.Now);
                Run(d, h, 10);
                if (Warned(d, "restored")) return "summary before 20 s";
                Run(d, h, 14);
                if (Members(d) != 4 || !Warned(d, "restored: 2 groups, 1 aircraft not found")) return "after 20 s: members " + Members(d) + " warnings " + string.Join("|", d.View().Warnings);
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        // the buoy dropped at 2400 s of the old clock, file 2426 s: shifted by the clock at the apply (100 s), not at the entry (3 s)
        private static string ShiftAtApply()
        {
            string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var g = World(dir, 1, 2, 3, 4, 5);
                var c = Reader(g);
                g.Now = 100;
                c.ApplyDeferred(100);
                AswBuoyView mine = null;
                foreach (var gv in c.View().Groups) foreach (var b in gv.Buoys) if (Math.Abs(b.Lat - 1) < 1e-9) mine = b;
                if (mine == null) return "buoy missing";
                double expect = 1 - 26.0 / 28800;   // the buoy is still 26 s old at the apply (shifted at the entry it would be 123 s old)
                if (Math.Abs(mine.LifeFrac - expect) > 1e-6) return "life fraction " + mine.LifeFrac + " expected " + expect;
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        private static string SaveWhileWaiting()
        {
            string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var g = World(dir);
                var c = Reader(g);
                Run(c, g, 3);
                c.OnGameSaved("/s/c.sav", SavStamp.Now(2430, Utc, "hook"));
                string path = Path.Combine(dir, SaveSlotStore.Key("/s/c.sav") + ".json");
                if (!File.Exists(path)) return "no file written while waiting";
                string err; var d = AswSave.Parse(File.ReadAllText(path), out err);
                if (d == null || d.Groups.Count != 2 || d.Groups[1].Members.Count != 3) return "content: " + err;
                if (d.Sav == null || Math.Abs(d.Sav.ElapsedS - 2430) > 1e-9) return "stamp";
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        private static string CommandsWaiting()
        {
            string dir = TmpDir();
            try
            {
                WriteFile(dir);
                var g = World(dir, 1, 2);
                var c = Reader(g);
                string[] r =
                {
                    c.Group(P("{\"op\":\"create\",\"uids\":[1]}")), c.SearchCourse(P("{\"tactics\":[\"rows\"]}")), c.SearchArea(P("{\"rect\":[1,2,3,4]}")),
                    c.Loadout(P("{\"type\":\"SH-3H\",\"loadout\":\"ASW\"}")), c.SetTactics(P("{\"tactics\":[\"rows\"]}")), c.Stop(P("{}")), c.Depth(P("{\"mode\":\"deep\"}")),
                    c.Order(P("{\"group\":\"G1\",\"order\":\"strike\"}")), c.SarOrder(P("{\"group\":\"G1\"}")), c.SarShip(P("{\"uid\":1,\"op\":\"start\"}")),
                };
                for (int i = 0; i < r.Length; i++) if (r[i] != "!restore_waiting") return "command " + i + " -> " + r[i];
                g.Mem.Add(AswFakeGame.Helo(3, "air")); g.Mem.Add(AswFakeGame.Helo(4, "deck_ready")); g.Mem.Add(AswFakeGame.Helo(5, "air"));
                c.ApplyDeferred(g.Now);
                if (c.Stop(P("{}")) == "!restore_waiting") return "still refused after the apply";
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }
    }
}
