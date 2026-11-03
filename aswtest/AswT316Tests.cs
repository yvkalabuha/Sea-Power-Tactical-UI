using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-316 (ADR-0123): the MAD pass is off by default; a live track goes straight to the drop; with the pass on it ends at MadVerifyS or is skipped while MAD is unreadable.
    internal static class AswT316Tests
    {
        public static readonly string[] Names = { "default-off", "active-fix", "pass-limit", "not-read", "ttw-no-mad" };

        private static void Run(AswController c, AswFakeGame f, int s) { AswT173AttackTests.Run(c, f, s); }
        private static bool Log(AswFakeGame f, string needle) { return AswT173AttackTests.Log(f, needle); }

        public static string Run(string only)
        {
            bool known = only == null;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                known = true;
                string e = n == "default-off" ? DefaultOff() : n == "active-fix" ? ActiveFix() : n == "pass-limit" ? PassLimit() : n == "not-read" ? NotRead() : TtwNoMad();
                if (e != null) return n + ": " + e;
            }
            return known ? null : "unknown scenario " + only;
        }

        private static string DefaultOff()
        {
            if (new AswLocParams().MadVerify) return "MadVerify defaults to true";
            return null;
        }

        private static string ActiveFix()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSubMad(f, -1);
            var m = AswT173AttackTests.Shooter(f, 1, 1.0, 3, sub); m.Mad = true;
            var c = AswT173AttackTests.Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            for (int i = 0; i < 6; i++)
            {
                Run(c, f, 1);
                if (c.RunState(sub.Id) == "verify") return "verify state at tick " + i;
            }
            if (f.Logs.Exists(l => l.StartsWith("asw mad", StringComparison.Ordinal))) return "asw mad line: " + AswT173AttackTests.Dump(f);
            if (f.AttackTrackCalls.Count != 1) return "attack calls " + f.AttackTrackCalls.Count;
            if (!Log(f, "state idle -> approach") || !Log(f, "approach -> engaged")) return "no approach/engaged: " + AswT173AttackTests.Dump(f);
            return null;
        }

        private static AswContact AddSubMad(AswFakeGame f, double madFix)
        {
            var sub = AswT173AttackTests.AddSub(f); sub.MadFixS = madFix;
            return sub;
        }

        private static string PassLimit()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSubMad(f, -1);
            var m = AswT173AttackTests.Shooter(f, 1, 1.0, 3, sub); m.Mad = true;
            var c = AswT173AttackTests.Group(f, cfg => { cfg.Loc.MadVerify = true; cfg.Loc.MadVerifyS = 60; });
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 2);
            if (c.RunState(sub.Id) != "verify") return "no verify: " + c.RunState(sub.Id);
            if (!Log(f, "pass start legs=8 alt_ft=100 limit_s=60")) return "no pass start with limit_s: " + AswT173AttackTests.Dump(f);
            int ticks = 2;
            while (c.RunState(sub.Id) == "verify" && ticks < 200) { Run(c, f, 1); ticks++; }
            if (ticks > 60 + 3) return "verify lasted " + ticks;
            if (!Log(f, "pass result=none") || !Log(f, "elapsed_s=60")) return "no result with elapsed_s=60: " + AswT173AttackTests.Dump(f);
            Run(c, f, 5);
            if (f.AttackTrackCalls.Count != 1) return "attack calls " + f.AttackTrackCalls.Count;
            return null;
        }

        private static string NotRead()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSubMad(f, AswContact.MadNotRead);
            var m = AswT173AttackTests.Shooter(f, 1, 1.0, 3, sub); m.Mad = true;
            var c = AswT173AttackTests.Group(f, cfg => cfg.Loc.MadVerify = true);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            for (int i = 0; i < 10; i++)
            {
                Run(c, f, 1);
                if (c.RunState(sub.Id) == "verify") return "verify at tick " + i;
            }
            int n = f.Logs.Count(l => l.Contains("pass result=skipped (no MAD detection reading)"));
            if (n != 1) return "skipped lines " + n;
            if (f.AttackTrackCalls.Count != 1) return "attack calls " + f.AttackTrackCalls.Count;
            return null;
        }

        private static string Ttw(bool madVerify, double madFix)
        {
            var f = new AswFakeGame();
            f.ShipsList.Add(new AswShipPos { Uid = 500, Name = "Yorktown", Lat = f.Tgt.Lat, Lon = f.Tgt.Lon });
            var sub = AswT173AttackTests.AddSub(f, 21); sub.MadFixS = madFix;
            var m = AswFakeGame.Helo(1, "air"); m.Torpedoes = 2; m.Mad = true; m.Lat = f.Tgt.Lat; m.Lon = f.Tgt.Lon;
            f.Mem.Add(m); f.FixPos.Add(1);
            var c = AswT173AttackTests.Make(f, cfg => cfg.Loc.MadVerify = madVerify);
            c.Group(AswT173AttackTests.P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            string r = c.Order(AswT173AttackTests.P("{\"group\":\"G\",\"order\":\"strike\",\"ref_uid\":500}"));
            if (AswController.IsErr(r)) return "ERR " + r;
            Run(c, f, 3);
            string line = f.Logs.Find(l => l.StartsWith("asw strike G assign ", StringComparison.Ordinal));
            if (line == null) return "ERR no assign line";
            int i = line.IndexOf("ttw_s=", StringComparison.Ordinal);
            return i < 0 ? "ERR no ttw" : line.Substring(i).Split(' ')[0];
        }

        private static string TtwNoMad()
        {
            string a = Ttw(true, AswContact.MadNotRead), b = Ttw(false, -1), c = Ttw(true, -1);
            if (a.StartsWith("ERR") || b.StartsWith("ERR") || c.StartsWith("ERR")) return a + " / " + b + " / " + c;
            if (a != b) return "not-read ttw " + a + " != no-pass ttw " + b;
            if (c == b) return "a pass (readable MAD) should lengthen ttw: " + c + " vs " + b;
            return null;
        }
    }
}
