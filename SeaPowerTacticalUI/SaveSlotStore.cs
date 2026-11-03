using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoiceBotBridge
{
    // T-122: one place for the per-.sav files of the window modules (ASW+SAR, CAP, smart attack, identify).
    // Pure (System.IO only); the file system is replaceable so a crash between steps can be tested.

    internal interface ISaveFs
    {
        bool Exists(string path);
        string ReadAllText(string path);
        void WriteAllText(string path, string text);
        void Copy(string src, string dst);
        void Replace(string src, string dst);
        void Move(string src, string dst);
        void Delete(string path);
        void CreateDirectory(string dir);
    }

    internal sealed class RealSaveFs : ISaveFs
    {
        public bool Exists(string path) { return File.Exists(path); }
        public string ReadAllText(string path) { return File.ReadAllText(path); }
        public void WriteAllText(string path, string text) { File.WriteAllText(path, text, new UTF8Encoding(false)); }
        public void Copy(string src, string dst) { File.Copy(src, dst, true); }
        public void Replace(string src, string dst) { File.Replace(src, dst, null); }
        public void Move(string src, string dst) { File.Move(src, dst); }
        public void Delete(string path) { if (File.Exists(path)) File.Delete(path); }
        public void CreateDirectory(string dir) { Directory.CreateDirectory(dir); }
    }

    /// <summary>T-198 (ADR-0086): identity of a game save. The game does not restore the mission time on load, so the mod
    /// keeps the value that went into the .sav ([GameTime] MissionElapsedTime) and the file creation time, and compares them on load.</summary>
    internal sealed class SavStamp
    {
        public double ElapsedS = double.NaN;   // GameTime.missionElapsedTime when the save was made = [GameTime] MissionElapsedTime
        public string Created;                 // [File] CreationTime "yyyy-MM-ddTHH:mm:ssZ"; null = unknown
        public string Src;                     // "hook" | "sav"

        public const double MaxSaveCallS = 120.0;

        /// <summary>T-217: start of the SaveGame call (prefix) when it is not after nowUtc and not older than MaxSaveCallS; otherwise nowUtc.</summary>
        public static DateTime HookCreatedUtc(DateTime? startedUtc, DateTime nowUtc)
        {
            if (startedUtc.HasValue && startedUtc.Value <= nowUtc && (nowUtc - startedUtc.Value).TotalSeconds <= MaxSaveCallS) return startedUtc.Value;
            return nowUtc;
        }

        public static SavStamp Now(double elapsedS, DateTime utc, string src)
        {
            return new SavStamp { ElapsedS = elapsedS, Created = utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), Src = src };
        }

        /// <summary>Null when there is no usable [GameTime] MissionElapsedTime.</summary>
        public static SavStamp FromSavLines(IEnumerable<string> lines)
        {
            if (lines == null) return null;
            string section = null, created = null; double el = double.NaN;
            foreach (string raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[' && line[line.Length - 1] == ']') { section = line.Substring(1, line.Length - 2); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (section == "GameTime" && k == "MissionElapsedTime")
                {
                    double d;
                    if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d)) el = d;
                }
                else if (section == "File" && k == "CreationTime") created = v.Length == 0 ? null : v;
                if (!double.IsNaN(el) && created != null) break;
            }
            if (double.IsNaN(el)) return null;
            return new SavStamp { ElapsedS = el, Created = created, Src = "sav" };
        }

        /// <summary>Streams the header of a .sav (it stops after both keys or after maxLines). Null on any error.</summary>
        public static SavStamp ReadSav(string savPath, int maxLines = 2000)
        {
            try
            {
                if (string.IsNullOrEmpty(savPath) || !File.Exists(savPath)) return null;
                using (var r = new StreamReader(savPath, Encoding.UTF8, true))
                    return FromSavLines(Take(r, maxLines));
            }
            catch (Exception) { return null; }
        }

        private static IEnumerable<string> Take(TextReader r, int max)
        {
            for (int i = 0; i < max; i++) { string l = r.ReadLine(); if (l == null) yield break; yield return l; }
        }

        public string ToJson()
        {
            var sb = new StringBuilder("{\"elapsed_s\":").Append(Json.Num(ElapsedS));
            if (Created != null) sb.Append(",\"created\":").Append(Json.Str(Created));
            if (Src != null) sb.Append(",\"src\":").Append(Json.Str(Src));
            return sb.Append('}').ToString();
        }

        public static SavStamp FromJson(object node)
        {
            var d = node as Dictionary<string, object>;
            if (d == null) return null;
            object o;
            if (!d.TryGetValue("elapsed_s", out o) || !(o is double)) return null;
            double el = (double)o;
            if (double.IsNaN(el) || double.IsInfinity(el)) return null;
            return new SavStamp
            {
                ElapsedS = el,
                Created = d.TryGetValue("created", out o) ? o as string : null,
                Src = d.TryGetValue("src", out o) ? o as string : null,
            };
        }
    }

    /// <summary>T-198 (ADR-0086): is a mod state file made for the save that was just loaded.</summary>
    internal static class SaveFreshness
    {
        public const double HookTolS = 0.5, LegacyTolS = 5.0, CreatedTolS = 15.0, OldRuleTolS = 5.0;

        /// <summary>Null = fresh; otherwise "stamp_mismatch" | "created_mismatch" | "stale_time" | "legacy_time".</summary>
        public static string Check(SavStamp file, double fileMissionTimeS, SavStamp loaded, double nowMissionTimeS)
        {
            if (loaded != null)
            {
                if (file != null)
                {
                    if (!(Math.Abs(file.ElapsedS - loaded.ElapsedS) <= HookTolS)) return "stamp_mismatch";
                    DateTime a, b;
                    if (file.Created != null && loaded.Created != null && TryUtc(file.Created, out a) && TryUtc(loaded.Created, out b)
                        && Math.Abs((a - b).TotalSeconds) > CreatedTolS) return "created_mismatch";
                    return null;
                }
                return Math.Abs(fileMissionTimeS - loaded.ElapsedS) <= LegacyTolS ? null : "legacy_time";
            }
            return Math.Abs(fileMissionTimeS - nowMissionTimeS) <= OldRuleTolS ? null : "stale_time";
        }

        private static bool TryUtc(string s, out DateTime t)
        {
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);
        }

        /// <summary>Added to every mission time kept in a file to move it to the time base of the running mission.</summary>
        public static double Shift(double fileMissionTimeS, double nowMissionTimeS) { return nowMissionTimeS - fileMissionTimeS; }
    }

    internal sealed class SaveSlotStore
    {
        public const string TmpExt = ".tmp", BakExt = ".bak";

        private readonly string _dir;
        private readonly Action<string> _log;
        private readonly ISaveFs _fs;

        public SaveSlotStore(string dir, Action<string> log, ISaveFs fs = null)
        {
            _dir = dir; _log = log ?? (s => { }); _fs = fs ?? new RealSaveFs();
        }

        /// <summary>T-198: SHA1 of the lower-cased .sav path with '\' turned into '/' (UTF-8), hex. A path typed by the game's file picker and one built by SaveGame differ only in separators.</summary>
        public static string Key(string savPath)
        {
            return Sha1Hex(((savPath ?? "").Replace('\\', '/')).ToLowerInvariant());
        }

        /// <summary>Key of the files written before T-198: SHA1 of the lower-cased path as it was.</summary>
        public static string LegacyKey(string savPath)
        {
            return Sha1Hex((savPath ?? "").ToLowerInvariant());
        }

        private static string Sha1Hex(string s)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Path of the slot file; null when the directory or the .sav path is empty.</summary>
        public string PathFor(string savPath)
        {
            if (string.IsNullOrEmpty(_dir) || string.IsNullOrEmpty(savPath)) return null;
            return Path.Combine(_dir, Key(savPath) + ".json");
        }

        private string LegacyPathFor(string savPath)
        {
            if (string.IsNullOrEmpty(_dir) || string.IsNullOrEmpty(savPath)) return null;
            string p = Path.Combine(_dir, LegacyKey(savPath) + ".json");
            return p == PathFor(savPath) ? null : p;
        }

        private void DeleteFiles(string path)
        {
            _fs.Delete(path); _fs.Delete(path + BakExt); _fs.Delete(path + TmpExt);
        }

        /// <summary>tmp, then a copy of the old file to .bak (only when validateOld accepts it), then Replace (Move when there is no file yet). A crash leaves the old or the new main file.</summary>
        public bool Write(string savPath, string json, Func<string, string> validateOld = null)
        {
            string path = PathFor(savPath);
            if (path == null) return false;
            string tmp = path + TmpExt;
            try
            {
                _fs.CreateDirectory(_dir);
                _fs.WriteAllText(tmp, json);
                bool had = _fs.Exists(path);
                if (had)
                {
                    try
                    {
                        // the backup is refreshed only from a main file that is usable; otherwise the previous good backup stays
                        string why = null;
                        try { string old = _fs.ReadAllText(path); why = string.IsNullOrEmpty(old) ? "empty file" : (validateOld == null ? null : validateOld(old)); }
                        catch (Exception e) { why = e.Message; }
                        if (why == null) _fs.Copy(path, path + BakExt);
                        else _log("save slot backup kept: main unusable (" + why + ")");
                    }
                    catch (Exception e) { _log("save slot backup failed: " + e.Message); }
                    _fs.Replace(tmp, path);
                }
                else _fs.Move(tmp, path);
                try { string lp = LegacyPathFor(savPath); if (lp != null) DeleteFiles(lp); } catch (Exception e) { _log("save slot legacy cleanup failed: " + e.Message); }
                return true;
            }
            catch (Exception e)
            {
                _log("save slot write failed: " + e.Message);
                try { _fs.Delete(tmp); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>validate(text) returns null when the text is usable, else a reason. source = "main" | "backup" | null (nothing usable).</summary>
        public string Read(string savPath, Func<string, string> validate, out string source)
        {
            source = null;
            string path = PathFor(savPath);
            if (path == null) return null;
            string lp = LegacyPathFor(savPath);
            string[] files = lp == null ? new[] { path, path + BakExt } : new[] { path, path + BakExt, lp, lp + BakExt };
            string[] names = lp == null ? new[] { "main", "backup" } : new[] { "main", "backup", "main", "backup" };
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    if (!_fs.Exists(files[i])) continue;
                    string text = _fs.ReadAllText(files[i]);
                    string why = string.IsNullOrEmpty(text) ? "empty file" : (validate == null ? null : validate(text));
                    if (why == null) { source = names[i]; return text; }
                    _log("save slot " + names[i] + " unusable: " + why);
                }
                catch (Exception e) { _log("save slot " + names[i] + " read failed: " + e.Message); }
            }
            return null;
        }

        /// <summary>Removes the main file, the backup and a forgotten tmp (under the new key and the legacy key). True when the main file existed.</summary>
        public bool Delete(string savPath)
        {
            string path = PathFor(savPath);
            if (path == null) return false;
            bool had = false;
            try
            {
                had = _fs.Exists(path);
                DeleteFiles(path);
                string lp = LegacyPathFor(savPath);
                if (lp != null) { had = had || _fs.Exists(lp); DeleteFiles(lp); }
            }
            catch (Exception e) { _log("save slot delete failed: " + e.Message); }
            return had;
        }

        // ---- self-test ------------------------------------------------------------------------------------------------

        private sealed class FakeFs : ISaveFs
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            public string FailOp; public int FailAt = -1; private int _n;
            private void Step(string op) { if (op == FailOp && _n++ == FailAt) throw new IOException("crash in " + op); }
            public bool Exists(string p) { return Files.ContainsKey(p); }
            public string ReadAllText(string p) { return Files[p]; }
            public void WriteAllText(string p, string t) { Step("write"); Files[p] = t; }
            public void Copy(string s, string d) { Step("copy"); Files[d] = Files[s]; }
            public void Replace(string s, string d) { Step("replace"); Files[d] = Files[s]; Files.Remove(s); }
            public void Move(string s, string d) { Step("move"); Files[d] = Files[s]; Files.Remove(s); }
            public void Delete(string p) { Files.Remove(p); }
            public void CreateDirectory(string d) { }
        }

        private static string Valid(string t) { return t.StartsWith("{") ? null : "not json"; }


        // header of the customer's save New Fileqa0305a.sav (without [Mods] and descriptions)
        private static readonly string[] CustomerHeader =
        {
            "[File]", "CreationTime=2026-10-23T02:07:37Z", "CurrentUid=1372", "",
            "[Environment]", "Time=10,40", "",
            "[GameTime]", "MissionElapsedTime=2425.65602779947", "",
            "[Ship1]", "MissionElapsedTime=1",
        };

        private static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-9; }

        private static string StampTests()
        {
            var st = SavStamp.FromSavLines(CustomerHeader);
            if (st == null || !Near(st.ElapsedS, 2425.65602779947) || st.Created != "2026-10-23T02:07:37Z" || st.Src != "sav") return "stamp: header";
            if (SavStamp.FromSavLines(new[] { "[File]", "CreationTime=x" }) != null) return "stamp: no GameTime";
            if (SavStamp.FromSavLines(null) != null) return "stamp: null lines";
            var c = SavStamp.FromSavLines(new[] { "[GameTime]", "MissionElapsedTime=12,5" });
            if (c == null || !Near(c.ElapsedS, 12.5) || c.Created != null) return "stamp: comma";
            if (SavStamp.FromSavLines(new[] { "[GameTime]", "MissionElapsedTime=abc" }) != null) return "stamp: garbage number";
            if (SavStamp.FromSavLines(new[] { "[Other]", "MissionElapsedTime=5" }) != null) return "stamp: wrong section";

            var h = SavStamp.Now(42.25, new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc), "hook");
            if (h.Created != "2026-10-23T02:07:37Z") return "stamp: now created";
            var back = SavStamp.FromJson(Json.Parse(h.ToJson()));
            if (back == null || !Near(back.ElapsedS, 42.25) || back.Created != h.Created || back.Src != "hook") return "stamp: json round trip";
            if (SavStamp.FromJson(Json.Parse(new SavStamp { ElapsedS = double.NaN }.ToJson())) != null) return "stamp: NaN must not be written";
            if (SavStamp.FromJson(null) != null || SavStamp.FromJson("x") != null || SavStamp.FromJson(Json.Parse("{}")) != null) return "stamp: bad json";

            // ReadSav: temp file, maxLines, missing file
            string tmp = Path.Combine(Path.GetTempPath(), "t198_" + Guid.NewGuid().ToString("N") + ".sav");
            try
            {
                File.WriteAllLines(tmp, CustomerHeader);
                var r = SavStamp.ReadSav(tmp);
                if (r == null || !Near(r.ElapsedS, 2425.65602779947)) return "readsav";
                if (SavStamp.ReadSav(tmp, 5) != null) return "readsav: maxLines";
                if (SavStamp.ReadSav(tmp + ".none") != null || SavStamp.ReadSav("") != null) return "readsav: missing";
            }
            finally { try { File.Delete(tmp); } catch (Exception) { } }

            // freshness
            var loaded = SavStamp.FromSavLines(CustomerHeader);
            if (SaveFreshness.Check(null, 2426.22, loaded, 3.0) != null) return "fresh: customer file";
            if (SaveFreshness.Check(null, 2426.22, loaded, 2430.0) != null) return "fresh: legacy ignores now";
            if (SaveFreshness.Check(null, 2440.0, loaded, 3.0) != "legacy_time") return "fresh: legacy_time";
            if (SaveFreshness.Check(null, 2425.656 + 5.0, loaded, 3.0) != null || SaveFreshness.Check(null, 2425.656 + 5.1, loaded, 3.0) != "legacy_time") return "fresh: legacy border";
            var a = new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:37Z" };
            if (SaveFreshness.Check(a, 2426.0, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:37Z" }, 3.0) != null) return "fresh: same stamp";
            if (SaveFreshness.Check(a, 2426.0, new SavStamp { ElapsedS = 2425.6 + 0.5 }, 3.0) != null) return "fresh: hook border";
            if (SaveFreshness.Check(a, 2426.0, new SavStamp { ElapsedS = 2425.6 + 0.6 }, 3.0) != "stamp_mismatch") return "fresh: hook border over";
            if (SaveFreshness.Check(a, 2426.0, new SavStamp { ElapsedS = 3100.2 }, 3.0) != "stamp_mismatch") return "fresh: A loaded as B";
            if (SaveFreshness.Check(a, 2426.0, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:53Z" }, 3.0) != "created_mismatch") return "fresh: created_mismatch";
            if (SaveFreshness.Check(a, 2426.0, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:52Z" }, 3.0) != null) return "fresh: created border";
            // T-217: save B of run 6 (file 07:37:36Z, .sav 07:37:32Z) and slower writes pass; a different time of the mission does not
            var sb = new SavStamp { ElapsedS = 3100.2, Created = "2026-10-23T07:37:36Z" };
            if (SaveFreshness.Check(sb, 3100.2, new SavStamp { ElapsedS = 3100.2, Created = "2026-10-23T07:37:32Z" }, 3.0) != null) return "fresh: save B delta 4";
            if (SaveFreshness.Check(sb, 3100.2, new SavStamp { ElapsedS = 3100.2, Created = "2026-10-23T07:37:26Z" }, 3.0) != null) return "fresh: delta 10";
            if (SaveFreshness.Check(sb, 3100.2, new SavStamp { ElapsedS = 3100.2, Created = "2026-10-23T07:37:21Z" }, 3.0) != null) return "fresh: delta 15";
            if (SaveFreshness.Check(sb, 3100.2, new SavStamp { ElapsedS = 3100.2, Created = "2026-10-23T07:37:20Z" }, 3.0) != "created_mismatch") return "fresh: delta 16";
            if (SaveFreshness.Check(sb, 3100.2, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T07:37:32Z" }, 3.0) != "stamp_mismatch") return "fresh: other save, near time";
            // T-217: HookCreatedUtc
            var t0 = new DateTime(2026, 10, 23, 7, 37, 32, DateTimeKind.Utc);
            if (SavStamp.HookCreatedUtc(t0, t0.AddSeconds(4)) != t0) return "hookcreated: start";
            if (SavStamp.HookCreatedUtc(null, t0) != t0) return "hookcreated: null";
            if (SavStamp.HookCreatedUtc(t0.AddSeconds(5), t0) != t0) return "hookcreated: start after now";
            if (SavStamp.HookCreatedUtc(t0.AddSeconds(-121), t0) != t0) return "hookcreated: stale";
            if (SavStamp.Now(1.0, SavStamp.HookCreatedUtc(t0, t0.AddSeconds(4)), "hook").Created != "2026-10-23T07:37:32Z") return "hookcreated: stamp";
            if (SaveFreshness.Check(new SavStamp { ElapsedS = 2425.6 }, 2426.0, new SavStamp { ElapsedS = 2425.6, Created = "2026-10-23T02:07:47Z" }, 3.0) != null) return "fresh: created unknown in file";
            if (SaveFreshness.Check(a, 2426.0, null, 2428.0) != null || SaveFreshness.Check(a, 2426.0, null, 2431.1) != "stale_time") return "fresh: old rule";
            if (SaveFreshness.Check(null, 2400.0, null, 2400.0 + 100.0) != "stale_time") return "fresh: old rule 100 s";
            if (!Near(SaveFreshness.Shift(2426.2, 3.0), -2423.2)) return "shift";
            return null;
        }

        public static string SelfTest()
        {
            // reference keys computed with sha1sum before T-122
            if (Key("C:/Users/X/Saves/a.sav") != "5441c3f05456a75267cebe3e14b52bf0baf4fe32") return "key: ascii";
            if (Key("C:/Users/X/Saves/A.SAV") != Key("c:/users/x/saves/a.sav")) return "key: case";
            if (Key("/home/Привет/save.sav") != "5c64fe72229006fbb20e08a6e2b56785b0cfa890") return "key: cyrillic";
            if (Key("") != "da39a3ee5e6b4b0d3255bfef95601890afd80709" || Key(null) != Key("")) return "key: empty";

            const string sav = "/s/a.sav";
            var logs = new List<string>();
            string src;

            var fs = new FakeFs();
            var st = new SaveSlotStore("d", logs.Add, fs);
            string path = st.PathFor(sav);
            if (path == null || st.PathFor("") != null || new SaveSlotStore(null, null, fs).PathFor(sav) != null) return "pathfor";
            if (!st.Write(sav, "{\"v\":1}") || fs.Files[path] != "{\"v\":1}" || fs.Files.ContainsKey(path + BakExt) || fs.Files.ContainsKey(path + TmpExt)) return "first write";
            if (!st.Write(sav, "{\"v\":2}") || fs.Files[path] != "{\"v\":2}" || fs.Files[path + BakExt] != "{\"v\":1}" || fs.Files.ContainsKey(path + TmpExt)) return "overwrite";
            if (st.Read(sav, Valid, out src) != "{\"v\":2}" || src != "main") return "read main";

            // crash at each step: the main file stays readable (old or new)
            foreach (string op in new[] { "write", "copy", "replace" })
            {
                var f = new FakeFs();
                var s = new SaveSlotStore("d", logs.Add, f);
                s.Write(sav, "{\"v\":1}");
                f.FailOp = op; f.FailAt = 0;
                bool ok = s.Write(sav, "{\"v\":2}");
                string t = s.Read(sav, Valid, out src);
                if (t == null) return "crash " + op + ": nothing readable";
                if (op == "copy" && (!ok || t != "{\"v\":2}")) return "crash copy: write must go on";
                if (op != "copy" && (ok || t != "{\"v\":1}")) return "crash " + op + ": old file expected";
                if (f.Files.ContainsKey(path + TmpExt)) return "crash " + op + ": tmp left";
            }
            {   // crash of the first write (Move)
                var f = new FakeFs(); var s = new SaveSlotStore("d", logs.Add, f);
                f.FailOp = "move"; f.FailAt = 0;
                if (s.Write(sav, "{\"v\":1}") || s.Read(sav, Valid, out src) != null) return "crash move";
            }

            // broken main -> backup; both broken -> null; empty main -> backup
            fs.Files[path] = "garbage";
            if (st.Read(sav, Valid, out src) != "{\"v\":1}" || src != "backup") return "backup read";
            fs.Files[path] = "";
            if (st.Read(sav, Valid, out src) != "{\"v\":1}" || src != "backup") return "empty main";
            fs.Files[path + BakExt] = "also garbage";
            if (st.Read(sav, Valid, out src) != null || src != null) return "both broken";
            if (logs.Count == 0) return "reasons not logged";

            {   // a broken main must not replace a good backup
                var f = new FakeFs(); var s = new SaveSlotStore("d", logs.Add, f);
                s.Write(sav, "{\"v\":1}", Valid); s.Write(sav, "{\"v\":2}", Valid);
                f.Files[path] = "garbage";
                if (!s.Write(sav, "{\"v\":3}", Valid) || f.Files[path] != "{\"v\":3}" || f.Files[path + BakExt] != "{\"v\":1}") return "bad main kept the good backup";
                f.Files[path] = "";
                s.Write(sav, "{\"v\":4}", Valid);
                if (f.Files[path + BakExt] != "{\"v\":1}") return "empty main kept the good backup";
            }


            // ---- T-198: key normalisation and the legacy key
            if (SaveSlotStore.Key("C:\\a\\B.sav") != SaveSlotStore.Key("c:/a/b.sav")) return "key: separators";
            if (SaveSlotStore.LegacyKey("C:/Users/X/Saves/a.sav") != "5441c3f05456a75267cebe3e14b52bf0baf4fe32") return "legacykey: ascii";
            if (SaveSlotStore.LegacyKey("C:\\a\\b.sav") == SaveSlotStore.Key("C:\\a\\b.sav")) return "legacykey must differ for backslashes";
            {
                const string mixed = "C:\\users\\x\\saves\\New.sav";
                var f = new FakeFs(); var s = new SaveSlotStore("d", logs.Add, f);
                string legacy = Path.Combine("d", SaveSlotStore.LegacyKey(mixed) + ".json");
                f.Files[legacy] = "{\"v\":0}"; f.Files[legacy + BakExt] = "{\"v\":-1}";
                if (s.Read(mixed, Valid, out src) != "{\"v\":0}" || src != "main") return "legacy read";
                if (!s.Write(mixed, "{\"v\":1}") || f.Files.ContainsKey(legacy) || f.Files.ContainsKey(legacy + BakExt)) return "legacy removed by write";
                if (s.Read(mixed, Valid, out src) != "{\"v\":1}") return "new read";
                f.Files[legacy] = "{}";
                if (!s.Delete(mixed) || f.Files.Count != 0) return "delete both keys";
                f.Files[legacy] = "{}";
                if (!s.Delete(mixed) || f.Files.Count != 0) return "delete legacy only";
            }
            {
                string e = StampTests(); if (e != null) return e;
            }

            // delete removes all three
            fs.Files[path + TmpExt] = "{}";
            fs.Files[path] = "{}";
            if (!st.Delete(sav) || fs.Files.Count != 0) return "delete";
            if (st.Delete(sav)) return "delete twice";
            return null;
        }
    }
}
