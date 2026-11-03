using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    // T-252: ASW memory (radar, ROE, depth) after a load is matched by the saved uid; two aircraft with one name keep their own records. `aswtest t252 <name>`.
    internal static class AswT252Tests
    {
        public static readonly string[] Names = { "same_name_both_restored", "no_duplicate_uid_in_file", "snapshot_key_wins", "old_dup_file" };

        private static AswFakeGame Fleet(string dir, string last)
        {
            var f = AswT180Retreat.Helos(2); f.SaveDir = dir; f.LastSavePath = last;
            foreach (var m in f.Mem) { m.Name = "Ka-27PL"; m.Type = "wp_ka-27"; }
            return f;
        }

        private static AirRadarFakeIo NewIo() { var io = new AirRadarFakeIo(); io.Add(1, false, true); io.Add(2, false, false); return io; }   // only uid 1 has a radar

        private static AswController Make(AswFakeGame f, AirRadarFakeIo io, out AirRadarLedger l)
        {
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
            l = new AirRadarLedger(io); c.Radar = l;
            return c;
        }

        private static void Run(AswFakeGame f, AirRadarFakeIo io, AswController c, int n) { for (int i = 0; i < n; i++) { f.Advance(1); io.NowS = f.Now; f.Tick(c); } }

        private static string SavedFile(string dir) { foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) if (!f.EndsWith(".bak")) return f; return null; }

        private static AswSaveData Read(string dir, out string err)
        {
            string file = SavedFile(dir); err = null;
            if (file == null) { err = "no file written"; return null; }
            string perr; var d = AswSave.Parse(File.ReadAllText(file), out perr);
            if (d == null) err = "parse " + perr;
            return d;
        }

        private static int Count(AswSaveData d, int uid) { return d.Memory.Count(m => m.Uid == uid); }

        private static string Save1(string dir, out AirRadarFakeIo io, out SavStamp stamp)
        {
            var f = Fleet(dir, "/s/a.sav"); io = NewIo(); AirRadarLedger l;
            var c = Make(f, io, out l);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}")); c.SearchCourse(P("{\"tactics\":[\"rows\",\"dip\"]}"));
            Run(f, io, c, 60);
            stamp = new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" };
            c.OnGameSaved("/s/b.sav", stamp);
            return null;
        }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static string Load(string dir, AirRadarFakeIo io, SavStamp stamp, out AswController c2, out AirRadarLedger l2, out AswFakeGame g)
        {
            g = Fleet(dir, "/s/b.sav"); g.Now = 2000;
            c2 = Make(g, io, out l2);
            c2.OnMissionChanged("/s/b.sav", g.Now, stamp);
            Run(g, io, c2, 6);
            return null;
        }

        public static string Run(string only = null)
        {
            bool known = only == null;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                known = true;
                string e;
                string dir = Path.Combine(Path.GetTempPath(), "asw252-" + Guid.NewGuid().ToString("N"));
                try
                {
                    switch (n)
                    {
                        case "same_name_both_restored": e = SameName(dir); break;
                        case "no_duplicate_uid_in_file": e = NoDup(dir); break;
                        case "snapshot_key_wins": e = KeyWins(dir); break;
                        case "old_dup_file": e = OldDup(dir); break;
                        default: e = null; break;
                    }
                }
                finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
                if (e != null) return n + ": " + e;
            }
            return known ? null : "unknown scenario " + only;
        }

        private static string SameName(string dir)
        {
            AirRadarFakeIo io; SavStamp st; Save1(dir, out io, out st);
            string err; var d = Read(dir, out err); if (d == null) return err;
            if (Count(d, 1) != 1 || Count(d, 2) != 1) return "first file memory " + string.Join(",", d.Memory.Select(m => m.Uid.ToString()).ToArray());
            AswController c2; AirRadarLedger l2; AswFakeGame g;
            Load(dir, io, st, out c2, out l2, out g);
            if (l2.StateOf(1) != "on") return "uid 1 state " + l2.StateOf(1);
            if (l2.StateOf(2) == "on") return "uid 2 took the radar of uid 1";
            return null;
        }

        private static string NoDup(string dir)
        {
            AirRadarFakeIo io; SavStamp st; Save1(dir, out io, out st);
            AswController c2; AirRadarLedger l2; AswFakeGame g;
            Load(dir, io, st, out c2, out l2, out g);
            c2.OnGameSaved("/s/c.sav", st);
            string err; var d = Read(dir, out err); if (d == null) return err;
            // the newest file may be either; every file must have each uid at most once
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".bak")) continue;
                string perr; var x = AswSave.Parse(File.ReadAllText(f), out perr); if (x == null) return "parse " + perr;
                if (Count(x, 1) != 1 || Count(x, 2) != 1) return "duplicates: " + string.Join(",", x.Memory.Select(m => m.Uid.ToString()).ToArray());
            }
            return null;
        }

        private static string KeyWins(string dir)
        {
            // a file whose second record carries a foreign uid: after the load and a new save the uid is not written twice
            AirRadarFakeIo io; SavStamp st; Save1(dir, out io, out st);
            string file = SavedFile(dir); string txt = File.ReadAllText(file);
            int first = txt.IndexOf("\"memory\":", StringComparison.Ordinal);
            if (first < 0) return "no memory section";
            File.WriteAllText(file, txt.Substring(0, first) + txt.Substring(first).Replace("\"uid\":2", "\"uid\":1"));
            AswController c2; AirRadarLedger l2; AswFakeGame g;
            Load(dir, io, st, out c2, out l2, out g);
            c2.OnGameSaved("/s/d.sav", st);
            int seen = 0;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".bak") || f == file) continue;
                seen++;
                string perr; var x = AswSave.Parse(File.ReadAllText(f), out perr); if (x == null) return "parse " + perr;
                if (Count(x, 1) > 1) return "uid 1 written twice";
            }
            if (seen == 0) return "no second file written";
            return null;
        }

        private static string OldDup(string dir)
        {
            AirRadarFakeIo io; SavStamp st; Save1(dir, out io, out st);
            string file = SavedFile(dir); string txt = File.ReadAllText(file);
            int first = txt.IndexOf("\"memory\":", StringComparison.Ordinal);
            File.WriteAllText(file, txt.Substring(0, first) + txt.Substring(first).Replace("\"uid\":2", "\"uid\":1"));   // memory = [1, 1]
            AswController c2; AirRadarLedger l2; AswFakeGame g;
            try { Load(dir, io, st, out c2, out l2, out g); } catch (Exception e) { return "exception " + e.Message; }
            if (l2.StateOf(2) == "on") return "uid 2 took a record of uid 1";
            c2.OnGameSaved("/s/e.sav", st);
            int seen = 0;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".bak") || f == file) continue;
                seen++;
                string perr; var x = AswSave.Parse(File.ReadAllText(f), out perr); if (x == null) return "parse " + perr;
                if (Count(x, 1) > 1) return "uid 1 written twice";
            }
            if (seen == 0) return "no second file written";
            return null;
        }
    }
}
