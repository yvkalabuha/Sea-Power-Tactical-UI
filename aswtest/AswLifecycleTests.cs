using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    // T-130: leaving a mission empties the controller without asking the game; a load does not lose its slot to a late save mark.
    internal static class AswLifecycleTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static void Run(AswController c, AswFakeGame f, int seconds)
        {
            for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); }
        }

        public static string Run()
        {
            string e;
            if ((e = Left()) != null) return "left: " + e;
            if ((e = LoadKeepsSlot()) != null) return "load keeps slot: " + e;
            return null;
        }

        private static string Left()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            c.Group(P("{\"op\":\"create\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 5);
            if (c.View().Groups.Count != 1 || c.View().Aircraft.Count == 0) return "no group before leaving";
            int calls = f.MembersCalls;
            c.OnMissionLeft();
            if (f.MembersCalls != calls) return "the game was asked for members while leaving";
            if (c.View().Groups.Count != 0 || c.View().Aircraft.Count != 0 || c.View().Available.Count != 0) return "view not empty";
            string js = c.StateJson();
            var o = (Dictionary<string, object>)Json.Parse(js);
            object gr; if (o.TryGetValue("groups", out gr) && ((List<object>)gr).Count != 0) return "state json still has groups";
            // a new mission starts from nothing
            c.OnMissionChanged(null, f.Now);
            if (c.View().Groups.Count != 0) return "groups after the next mission";
            return null;
        }

        // the old mission has no groups; the game sets last_save to the slot being loaded BEFORE the mission objects change
        private static string LoadKeepsSlot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "aswlife-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
                var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c.Group(P("{\"op\":\"create\",\"uids\":[1,2]}"));
                c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
                Run(c, f, 10);
                f.LastSavePath = "/s/B.sav"; Run(c, f, 1);   // the player saved: the slot file is written
                string file = Path.Combine(dir, AswSave.Key("/s/B.sav") + ".json");
                if (!File.Exists(file)) return "save missing";
                // a second controller = the old mission without groups; the load sets last_save first
                var g = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/z.sav", Now = f.Now };
                g.Mem.Add(AswFakeGame.Helo(1, "air")); g.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/z.sav", g.Now);
                Run(c2, g, 2);
                g.LastSavePath = "/s/B.sav";
                Run(c2, g, 2);   // inside the grace: the old mission is still running
                c2.OnMissionChanged("/s/B.sav", g.Now);   // the objects changed
                Run(c2, g, 6);
                if (!File.Exists(file)) return "the slot being loaded was deleted";
                if (c2.View().Groups.Count != 1) return "groups not restored: " + c2.View().Groups.Count;
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }
    }
}
