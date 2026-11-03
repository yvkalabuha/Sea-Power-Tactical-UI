using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    // T-241 (ADR-0099): the search radar of an ASW aircraft (helicopter or plane) is on while it searches and comes back as it was. `aswtest t241 <name>`.
    internal static class AswT241Tests
    {
        public static readonly string[] Names =
        {
            "search_on", "surface_fallback", "plane_on", "evade_keep", "retreat_restore_resume", "player_off_no_resume", "leaving_restore",
            "no_order_none", "land_no_calls", "release_restore", "autoradar_off_live", "no_radar", "save_load", "old_file", "player_saved", "load_foreign_release",
        };

        private sealed class Rig
        {
            public AswFakeGame F; public AswController C; public AirRadarFakeIo Io = new AirRadarFakeIo(); public AirRadarLedger L; public AswConfig Cfg;
            public AswT180Retreat.FakeThreats Th; public AswT180Retreat.FakeSteer St;
            public Rig(int helos = 2, bool surface = true, bool helicopter = true, bool retreat = false, bool hasRadar = true, string restore = "off", AswFakeGame game = null)
            {
                F = game ?? AswT180Retreat.Helos(helos);
                for (int u = 1; u <= helos; u++) { Io.Add(u, !surface && hasRadar, surface && hasRadar); F.Mem[u - 1].Helicopter = helicopter; }
                Cfg = new AswConfig { RestoreOnLoad = restore };
                C = Make(F, Cfg, retreat);
                L = new AirRadarLedger(Io); C.Radar = L;
            }
            private AswController Make(AswFakeGame f, AswConfig cfg, bool retreat)
            {
                var c = new AswController(f, new AswParams(), cfg);
                if (retreat)
                {
                    St = new AswT180Retreat.FakeSteer(f); Th = new AswT180Retreat.FakeThreats();
                    c.AirGuard = Th; c.AirSteer = new AirRetreatSteer(St, s => f.Logs.Add("steer " + s));
                }
                return c;
            }
            public void Start(string order = "{\"tactics\":[\"rows\",\"dip\"]}")
            {
                C.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + string.Join(",", F.Mem.Select(m => m.Uid.ToString()).ToArray()) + "]}"));
                if (order != null) C.SearchCourse(P(order));
            }
            public void Run(int n, AswController c = null) { for (int i = 0; i < n; i++) { F.Advance(1); Io.NowS = F.Now; F.Tick(c ?? C); } }
            public int On(int uid) { return Io.SetCount(uid, " on"); }
            public int Off(int uid) { return Io.SetCount(uid, " off"); }
            public bool Logged(string s) { foreach (var l in F.Logs) if (l.Contains(s)) return true; return false; }
        }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        public static string Run(string only = null)
        {
            bool known = only == null;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                known = true;
                string e;
                switch (n)
                {
                    case "search_on": e = SearchOn(); break;
                    case "surface_fallback": e = SurfaceFallback(); break;
                    case "plane_on": e = PlaneOn(); break;
                    case "evade_keep": e = EvadeKeep(); break;
                    case "retreat_restore_resume": e = RetreatRestoreResume(); break;
                    case "player_off_no_resume": e = PlayerOffNoResume(); break;
                    case "leaving_restore": e = LeavingRestore(); break;
                    case "no_order_none": e = NoOrderNone(); break;
                    case "land_no_calls": e = LandNoCalls(); break;
                    case "release_restore": e = ReleaseRestore(); break;
                    case "autoradar_off_live": e = AutoRadarOffLive(); break;
                    case "no_radar": e = NoRadar(); break;
                    case "save_load": e = SaveLoad(); break;
                    case "old_file": e = OldFile(); break;
                    case "player_saved": e = PlayerSaved(); break;
                    case "load_foreign_release": e = LoadForeignRelease(); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            return known ? null : "unknown scenario " + only;
        }

        private static string SearchOn()
        {
            var r = new Rig(2); r.Start(); r.Run(60);
            if (r.On(1) != 1 || r.On(2) != 1 || !r.Io.Calls.Contains("set 1 surface on")) return "calls " + string.Join("|", r.Io.Calls);
            if (r.L.StateOf(1) != "on") return "state " + r.L.StateOf(1);
            if (!r.Logged("asw radar on uid=1 list=surface")) return "no journal line";
            r.Run(60);
            if (r.On(1) != 1) return "repeated set on";
            if (!r.C.StateJson().Contains("\"radar\":{\"1\":\"on\",\"2\":\"on\"}")) return "state json " + r.C.StateJson();
            return null;
        }

        private static string SurfaceFallback()
        {
            var r = new Rig(1, false); r.Start(); r.Run(60);
            if (!r.Io.Calls.Contains("set 1 air on") || r.Io.SetCount(1, "surface on") != 0) return "calls " + string.Join("|", r.Io.Calls);
            return null;
        }

        private static string PlaneOn()
        {
            var r = new Rig(1, true, false); r.Start(); r.Run(60);
            if (r.On(1) != 1) return "a plane got no radar";
            return null;
        }

        private static string EvadeKeep()
        {
            var r = new Rig(1); r.Start(); r.Run(60);
            r.F.Mem[0].Evading = true; r.Run(30);
            if (r.Off(1) != 0 || r.On(1) != 1) return "calls while evading " + string.Join("|", r.Io.Calls);
            return null;
        }

        private static string RetreatRestoreResume()
        {
            var r = new Rig(2, true, true, true); r.Start(); r.Run(60);
            if (r.On(1) != 1) return "not on";
            r.Th.Unit[1] = AirStage.Retreat; r.Th.Cover[1] = AswT180Retreat.Cov(8, 6); r.Th.Group["G"] = AirStage.Retreat;
            r.Run(10);
            if (r.Off(1) != 1 || r.Io.Units[1].SurfOn) return "no switch-off in the retreat off=" + r.Off(1);
            r.Th.Unit.Clear(); r.Th.Group.Clear();
            r.Run(30);
            if (r.On(1) != 2 || !r.Io.Units[1].SurfOn) return "not switched on after the resume on=" + r.On(1);
            return null;
        }

        private static string PlayerOffNoResume()
        {
            var r = new Rig(2, true, true, true); r.Start(); r.Run(60);
            r.Io.PlayerOff(1); r.Run(5);
            if (r.L.StateOf(1) != "player" || !r.Logged("asw radar player_off uid=1")) return "player_off not seen";
            r.Th.Unit[1] = AirStage.Retreat; r.Th.Cover[1] = AswT180Retreat.Cov(8, 6); r.Th.Group["G"] = AirStage.Retreat;
            r.Run(10);
            r.Th.Unit.Clear(); r.Th.Group.Clear();
            r.Run(40);
            if (r.On(1) != 1 || r.Off(1) != 0) return "the player's radar was touched on=" + r.On(1) + " off=" + r.Off(1);
            return null;
        }

        private static string LeavingRestore()
        {
            var r = new Rig(1); r.Start(); r.Run(60);
            r.F.Mem[0].AiState = "BingoFuel"; r.Run(5);
            if (r.Off(1) != 1) return "no switch-off when the game sends it home";
            return null;
        }

        private static string NoOrderNone()
        {
            var r = new Rig(1); r.Start(null); r.Run(60);
            if (r.Io.Calls.Count != 0) return "calls for a group without an order " + string.Join("|", r.Io.Calls);
            return null;
        }

        private static string LandNoCalls()
        {
            var r = new Rig(1); r.Start(); r.Run(60);
            if (r.On(1) != 1) return "not on";
            r.F.Mem.Clear(); r.Run(120);
            if (r.Off(1) != 0) return "a game call for an aircraft that is gone";
            return null;
        }

        private static string ReleaseRestore()
        {
            var r = new Rig(2); r.Start(); r.Run(60);
            r.C.Group(P("{\"op\":\"remove\",\"group\":\"G\",\"uids\":[1]}")); r.Run(2);
            if (r.Off(1) != 1 || r.Off(2) != 0) return "off 1=" + r.Off(1) + " 2=" + r.Off(2);
            return null;
        }

        private static string AutoRadarOffLive()
        {
            var r = new Rig(1); r.Start(); r.Run(60);
            r.Cfg.AutoRadar = false; r.Run(3);
            if (r.Off(1) != 1) return "not given back";
            r.Cfg.AutoRadar = true; r.Run(10);
            if (r.On(1) != 2) return "not on again " + r.On(1);
            return null;
        }

        private static string NoRadar()
        {
            var r = new Rig(1, true, true, false, false); r.Start(); r.Run(60);
            if (r.Io.Calls.Count != 0 || !r.Logged("asw radar none uid=1 reason=no_radar")) return "calls " + string.Join("|", r.Io.Calls);
            return null;
        }

        // ---- save / load ----
        private static string SavedFile(string dir) { foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) if (!f.EndsWith(".bak")) return f; return null; }

        private static string SaveLoadCore(bool strip, bool playerOff, out Rig r1, out Rig r2, out string dir, bool foreignRelease = false)
        {
            dir = Path.Combine(Path.GetTempPath(), "asw241-" + Guid.NewGuid().ToString("N"));
            r1 = null; r2 = null;
            var f = AswT180Retreat.Helos(1); f.SaveDir = dir; f.LastSavePath = "/s/a.sav";
            r1 = new Rig(1, true, true, false, true, "auto", f);
            r1.Start(); r1.Run(60);
            if (playerOff) { r1.Io.PlayerOff(1); r1.Run(5); }
            var stamp = new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" };
            r1.C.OnGameSaved("/s/b.sav", stamp);
            string file = SavedFile(dir);
            if (file == null) return "no file written";
            string txt = File.ReadAllText(file);
            if (!txt.Contains("\"radar\":{")) return "no radar in the file: " + txt;
            if (strip) { txt = Regex.Replace(txt, ",\"radar\":\\{[^}]*\\}", ""); File.WriteAllText(file, txt); if (txt.Contains("\"radar\"")) return "strip failed"; }
            var g = AswT180Retreat.Helos(1); g.SaveDir = dir; g.LastSavePath = "/s/b.sav"; g.Now = 2000;
            r2 = new Rig(1, true, true, false, true, "auto", g);
            r2.Io = r1.Io;   // the same radars survive the load (the game keeps them as they are)
            r2.L = new AirRadarLedger(r1.Io); r2.C.Radar = r2.L;
            r2.C.OnMissionChanged("/s/b.sav", g.Now, stamp);
            if (foreignRelease) r2.L.ReleaseAll("cap");   // T-246: the CAP controller loads its own file right after (CapController.Load -> ClearAll)
            for (int i = 0; i < 6; i++) { g.Advance(1); r1.Io.NowS = g.Now; g.Tick(r2.C); }
            return null;
        }

        private static string SaveLoad()
        {
            Rig r1, r2; string dir;
            try
            {
                string e = SaveLoadCore(false, false, out r1, out r2, out dir); if (e != null) return e;
                if (r2.L.StateOf(1) == null) return "memory not imported";
                r2.Cfg.AutoRadar = false; r2.Run(3);
                if (r1.Io.SetCount(1, " off") != 1 || r1.Io.Units[1].SurfOn) return "no switch-off after the load";
                return null;
            }
            finally { Cleanup(); }
        }

        private static string OldFile()
        {
            Rig r1, r2; string dir;
            try
            {
                string e = SaveLoadCore(true, false, out r1, out r2, out dir); if (e != null) return e;
                int before = r1.Io.Calls.Count;
                r2.Cfg.AutoRadar = false; r2.Run(3);
                if (r1.Io.Calls.Count != before) return "calls after loading an old file " + string.Join("|", r1.Io.Calls);
                return null;
            }
            finally { Cleanup(); }
        }

        private static string PlayerSaved()
        {
            Rig r1, r2; string dir;
            try
            {
                string e = SaveLoadCore(false, true, out r1, out r2, out dir); if (e != null) return e;
                if (r2.L.StateOf(1) != "player") return "player flag lost " + r2.L.StateOf(1);
                int on0 = r1.Io.SetCount(1, " on");
                r2.Run(30);
                if (r1.Io.SetCount(1, " on") != on0) return "switched on after the load";
                return null;
            }
            finally { Cleanup(); }
        }

        /// <summary>
        /// T-246 (N6): after a load another holder (CAP) clears its own memory (ReleaseAll "cap") - the ASW entry that came from the file must not lose the mod's switch-on:
        /// the ASW state after the next ticks must be "on" (not "untouched"), and the end of the task must switch the radar off.
        /// </summary>
        private static string LoadForeignRelease()
        {
            Rig r1, r2; string dir;
            try
            {
                string e = SaveLoadCore(false, false, out r1, out r2, out dir, true); if (e != null) return e;
                if (r2.L.StateOf(1) != "on") return "state after the foreign release " + r2.L.StateOf(1) + " (the switch-on of the mod is lost)";
                r2.Cfg.AutoRadar = false; r2.Run(3);
                if (r1.Io.SetCount(1, " off") != 1 || r1.Io.Units[1].SurfOn) return "no switch-off after the task: " + string.Join("|", r1.Io.Calls);
                return null;
            }
            finally { Cleanup(); }
        }

        private static void Cleanup()
        {
            try { foreach (string d in Directory.GetDirectories(Path.GetTempPath(), "asw241-*")) Directory.Delete(d, true); } catch (Exception) { }
        }
    }
}
