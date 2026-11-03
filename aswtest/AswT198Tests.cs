using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    // T-198 (ADR-0086): the ASW file is written by the game-save hook and read back against the stamp of the loaded save. `aswtest t198 <name>`.
    internal static class AswT198Tests
    {
        private static readonly string[] Names = { "hook-save-paused", "hook-save-empty", "hook-then-marker", "sar-ships", "restore-stamp", "buoy-shift" };
        private static readonly DateTime Utc = new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc);

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }
        private static string TmpDir() { return Path.Combine(Path.GetTempPath(), "asw198-" + Guid.NewGuid().ToString("N")); }
        private static string Slot(string dir, string sav) { return Path.Combine(dir, SaveSlotStore.Key(sav) + ".json"); }
        private static SavStamp Stamp(double el) { return SavStamp.Now(el, Utc, "hook"); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "hook-save-paused": e = HookSavePaused(); break;
                    case "hook-save-empty": e = HookSaveEmpty(); break;
                    case "hook-then-marker": e = HookThenMarker(); break;
                    case "sar-ships": e = AswSarTests.HookShipSave(); break;
                    case "restore-stamp": e = RestoreStamp(); break;
                    default: e = BuoyShift(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static AswController WithGroup(AswFakeGame f, string restore = "auto")
        {
            f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = restore });
            c.Group(P("{\"op\":\"create\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 5);
            return c;
        }

        // the pause menu stops the tick: the save must be written without a Tick
        private static string HookSavePaused()
        {
            string dir = TmpDir();
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                var c = WithGroup(f);
                int logs = f.Logs.Count;
                c.OnGameSaved("/s/pause.sav", Stamp(2425.6));
                if (!File.Exists(Slot(dir, "/s/pause.sav"))) return "file not written";
                if (!f.Logs.Exists(l => l.StartsWith("asw saved "))) return "no 'asw saved' line";
                string err; var d = AswSave.Parse(File.ReadAllText(Slot(dir, "/s/pause.sav")), out err);
                if (d == null || d.Sav == null || Math.Abs(d.Sav.ElapsedS - 2425.6) > 1e-9 || d.Groups.Count != 1) return "file content: " + err;
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        private static string HookSaveEmpty()
        {
            string dir = TmpDir();
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                Run(c, f, 2);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Slot(dir, "/s/old.sav"), "{\"format\":2}");
                c.OnGameSaved("/s/old.sav", Stamp(10));
                if (File.Exists(Slot(dir, "/s/old.sav"))) return "slot of an empty save not dropped at once";
                if (!f.Logs.Exists(l => l.StartsWith("asw save slot cleared "))) return "no 'cleared' line";
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        // after the hook the mark observer must not overwrite the stamped file (no hook flag: same mark; with the flag: the mark lags behind)
        private static string HookThenMarker()
        {
            string dir = TmpDir();
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                var c = WithGroup(f);
                c.OnGameSaved("/s/b.sav", Stamp(2425.6));
                f.LastSavePath = "/s/b.sav";
                string before = File.ReadAllText(Slot(dir, "/s/b.sav"));
                int saved = f.Logs.FindAll(l => l.StartsWith("asw saved ")).Count;
                Run(c, f, 5);
                if (f.Logs.FindAll(l => l.StartsWith("asw saved ")).Count != saved || File.ReadAllText(Slot(dir, "/s/b.sav")) != before) return "same mark rewrote the file";

                var g = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                var c2 = WithGroup(g);
                c2.SaveHookActive = true;
                c2.OnGameSaved("/s/c.sav", Stamp(77.7));
                // the game writes last_save a moment later; until then the mark is the old one, then the new one
                Run(c2, g, 3); g.LastSavePath = "/s/c.sav"; Run(c2, g, 3);
                string txt = File.ReadAllText(Slot(dir, "/s/c.sav"));
                if (!txt.Contains("\"sav\"") || !txt.Contains("77.7")) return "stamped file overwritten by the mark observer";
                g.LastSavePath = "/s/d.sav"; Run(c2, g, 5);
                if (File.Exists(Slot(dir, "/s/d.sav"))) return "mark observer wrote while the hook is active";
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        private static string RestoreStamp()
        {
            string dir = TmpDir();
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                var c = WithGroup(f);
                c.OnGameSaved("/s/b.sav", Stamp(2425.6));

                var g = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/b.sav", Now = 2 };
                g.Mem.Add(AswFakeGame.Helo(1, "air")); g.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", 2, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:38Z", Src = "sav" });
                if (c2.View().Groups.Count != 1) return "stamp matched, groups " + c2.View().Groups.Count;
                if (!c2.View().Warnings.Exists(w => w.Contains("restored: 1 groups"))) return "no 'restored: N groups' warning";

                var h = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/b.sav", Now = 2 };
                h.Mem.Add(AswFakeGame.Helo(1, "air")); h.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
                var c3 = new AswController(h, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c3.OnMissionChanged("/s/b.sav", 2, new SavStamp { ElapsedS = 3100.2, Src = "sav" });
                if (c3.View().Groups.Count != 0) return "other save loaded, groups " + c3.View().Groups.Count;
                if (!h.Logs.Contains("asw restore: file is stale (stamp_mismatch)")) return "no stale line: " + string.Join(" | ", h.Logs);
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        // the buoy dropped at 2400 s of the old clock; the file says 2426 s; the new clock says 3 s: the buoy is 26 s old, not "from the future"
        private static string BuoyShift()
        {
            string dir = TmpDir();
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                var c = WithGroup(f);
                c.OnGameSaved("/s/b.sav", Stamp(2425.6));
                string err; string path = Slot(dir, "/s/b.sav");
                var d = AswSave.Parse(File.ReadAllText(path), out err);
                string name = d.Groups[0].Name;
                d.MissionTimeS = 2426; d.Buoys.Add(new AswBuoyDto { Lat = 1, Lon = 1, DroppedS = 2400, LifeS = 28800, Ammo = "x", Group = name });
                File.WriteAllText(path, AswSave.ToJson(d));

                var g = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/b.sav", Now = 3 };
                g.Mem.Add(AswFakeGame.Helo(1, "air")); g.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", 3, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:37Z", Src = "sav" });
                if (c2.View().Groups.Count != 1) return "group not restored: " + string.Join(" | ", g.Logs);
                var bs = c2.View().Groups[0].Buoys;
                AswBuoyView mine = null;
                foreach (var b in bs) if (Math.Abs(b.Lat - 1) < 1e-9 && Math.Abs(b.Lon - 1) < 1e-9) mine = b;
                if (mine == null) return "the saved buoy is not in the view (buoys " + bs.Count + ")";
                double expect = 1 - 26.0 / 28800;
                if (Math.Abs(mine.LifeFrac - expect) > 1e-6) return "life fraction " + mine.LifeFrac + " expected " + expect;
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }
    }
}
