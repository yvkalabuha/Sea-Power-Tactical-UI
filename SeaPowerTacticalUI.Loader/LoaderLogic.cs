using System;
using System.Collections.Generic;
using System.IO;

namespace SeaPowerTacticalUI.Loader
{
    internal interface ILoaderFs
    {
        bool DirExists(string dir);
        string[] Files(string dir);   // full paths, top level only
        string[] Dirs(string dir);    // full paths, top level only
    }

    internal sealed class LoaderFs : ILoaderFs
    {
        public bool DirExists(string dir) { return Directory.Exists(dir); }
        public string[] Files(string dir) { return Directory.GetFiles(dir); }
        public string[] Dirs(string dir) { return Directory.GetDirectories(dir); }
    }

    internal enum FindKind { Found, NotFound, Ambiguous }

    internal sealed class FindResult
    {
        public FindKind Kind;
        public string Dir;            // searched item dir (null if the game is not in <lib>/steamapps/common/<game>)
        public string Path;           // Found
        public string[] Candidates;   // Ambiguous
    }

    internal enum DecisionKind { Skip, Load, Report }

    internal sealed class Decision
    {
        public DecisionKind Kind;
        public string Path;   // Load
        public string Line;   // Skip / Report: the one log line
    }

    /// <summary>T-314 (ADR-0121): pure logic of the loader (BCL only; compiled into the loader and into the loadertest stand).</summary>
    internal static class LoaderLogic
    {
        public const string SlotKey = "ru.voicebot.seapower.ui/started";   // == UiBoot.SlotKey (ADR-0059), duplicated on purpose: no reference to the mod
        public const string AppId = "1286220";
        public const string ItemId = "3827955062";
        public const string ModDll = "SeaPowerTacticalUI.dll";
        public const string EntryType = "VoiceBotBridge.Ui.LoaderEntry";
        public const string EntryMethod = "Start";
        public const int Protocol = 1;

        private static bool IsSep(char c) { return c == '/' || c == '\\'; }

        private static string BaseName(string path)
        {
            int i = path.Length - 1;
            while (i >= 0 && !IsSep(path[i])) i--;
            return path.Substring(i + 1);
        }

        /// <summary>&lt;lib&gt;/steamapps/common/&lt;game&gt; -> &lt;lib&gt;/steamapps/workshop/content/1286220/3827955062.
        /// Pure string work (no Path.GetFullPath): '/' and '\' both work, the separator of the input is kept, trailing separators ignored.
        /// null when the parent folder of the game is not "common" (case-insensitive).</summary>
        public static string WorkshopItemDir(string gameRoot)
        {
            if (string.IsNullOrEmpty(gameRoot)) return null;
            int end = gameRoot.Length;
            while (end > 0 && IsSep(gameRoot[end - 1])) end--;
            int g = end - 1;
            while (g >= 0 && !IsSep(gameRoot[g])) g--;     // separator before the game folder
            if (g <= 0 || g == end - 1) return null;
            int c = g - 1;
            while (c >= 0 && !IsSep(gameRoot[c])) c--;     // separator before "common"
            if (c < 0) return null;
            string common = gameRoot.Substring(c + 1, g - c - 1);
            if (!string.Equals(common, "common", StringComparison.OrdinalIgnoreCase)) return null;
            char sep = gameRoot[g];
            string steamapps = gameRoot.Substring(0, c);   // ends with ".../steamapps"
            return steamapps + sep + "workshop" + sep + "content" + sep + AppId + sep + ItemId;
        }

        /// <summary>SeaPowerTacticalUI.dll (name compared case-insensitively) in dir or one level deeper. Exactly one = Found.</summary>
        public static FindResult FindModDll(string dir, ILoaderFs fs)
        {
            var res = new FindResult { Kind = FindKind.NotFound, Dir = dir };
            if (string.IsNullOrEmpty(dir) || !fs.DirExists(dir)) return res;
            var hits = new List<string>();
            AddHits(fs.Files(dir), hits);
            foreach (string sub in fs.Dirs(dir))
            {
                string[] files = fs.Files(sub);
                if (files != null) AddHits(files, hits);
            }
            if (hits.Count == 0) return res;
            if (hits.Count == 1) { res.Kind = FindKind.Found; res.Path = hits[0]; return res; }
            res.Kind = FindKind.Ambiguous;
            res.Candidates = hits.ToArray();
            return res;
        }

        private static void AddHits(string[] files, List<string> hits)
        {
            if (files == null) return;
            foreach (string f in files)
                if (string.Equals(BaseName(f), ModDll, StringComparison.OrdinalIgnoreCase)) hits.Add(f);
        }

        /// <summary>slot not empty -> Skip "already started (&lt;slot&gt;)"; Found -> Load; NotFound -> Report "not found at &lt;dir&gt;"
        /// (dir null -> "not found: the game is not in a Steam library"); Ambiguous -> Report "ambiguous: &lt;p1&gt;; &lt;p2&gt;".</summary>
        public static Decision Decide(string slot, FindResult find)
        {
            if (!string.IsNullOrEmpty(slot))
                return new Decision { Kind = DecisionKind.Skip, Line = "already started (" + slot + ")" };
            switch (find.Kind)
            {
                case FindKind.Found:
                    return new Decision { Kind = DecisionKind.Load, Path = find.Path };
                case FindKind.Ambiguous:
                    return new Decision { Kind = DecisionKind.Report, Line = "ambiguous: " + string.Join("; ", find.Candidates) };
                default:
                    return new Decision
                    {
                        Kind = DecisionKind.Report,
                        Line = find.Dir == null ? "not found: the game is not in a Steam library" : "not found at " + find.Dir
                    };
            }
        }

        private sealed class FakeFs : ILoaderFs
        {
            public readonly Dictionary<string, string[]> Tree = new Dictionary<string, string[]>();   // dir -> entries (subdirs end with '/')
            public bool DirExists(string dir) { return Tree.ContainsKey(dir); }
            public string[] Files(string dir)
            {
                var l = new List<string>();
                string[] e;
                if (Tree.TryGetValue(dir, out e)) foreach (string x in e) if (!x.EndsWith("/")) l.Add(dir + "/" + x);
                return l.ToArray();
            }
            public string[] Dirs(string dir)
            {
                var l = new List<string>();
                string[] e;
                if (Tree.TryGetValue(dir, out e)) foreach (string x in e) if (x.EndsWith("/")) l.Add(dir + "/" + x.TrimEnd('/'));
                return l.ToArray();
            }
        }

        public static string SelfTest()
        {
            const string win = "Z:\\home\\u\\SteamLibrary\\steamapps\\workshop\\content\\1286220\\3827955062";
            if (WorkshopItemDir("Z:\\home\\u\\SteamLibrary\\steamapps\\common\\Sea Power") != win) return "WorkshopItemDir: windows path -> " + WorkshopItemDir("Z:\\home\\u\\SteamLibrary\\steamapps\\common\\Sea Power");
            if (WorkshopItemDir("C:\\Program Files (x86)\\Steam\\steamapps\\common\\Sea Power\\") != "C:\\Program Files (x86)\\Steam\\steamapps\\workshop\\content\\1286220\\3827955062")
                return "WorkshopItemDir: trailing separator";
            if (WorkshopItemDir("/home/u/lib/steamapps/common/Sea Power") != "/home/u/lib/steamapps/workshop/content/1286220/3827955062") return "WorkshopItemDir: unix path";
            if (WorkshopItemDir("/home/u/lib/steamapps/COMMON/Sea Power") != "/home/u/lib/steamapps/workshop/content/1286220/3827955062") return "WorkshopItemDir: case";
            if (WorkshopItemDir("D:\\Games\\Sea Power") != null) return "WorkshopItemDir: not in common must be null";
            if (WorkshopItemDir(null) != null || WorkshopItemDir("") != null) return "WorkshopItemDir: null/empty must be null";

            var fs = new FakeFs();
            const string d = "/w/item";
            FindResult r = FindModDll(d, fs);
            if (r.Kind != FindKind.NotFound || r.Dir != d) return "Find: missing dir must be NotFound";
            if (FindModDll(null, fs).Kind != FindKind.NotFound) return "Find: null dir must be NotFound";
            fs.Tree[d] = new string[0];
            if (FindModDll(d, fs).Kind != FindKind.NotFound) return "Find: empty dir must be NotFound";
            fs.Tree[d] = new[] { "ModInfo.json", "SeaPowerTacticalUI.dll" };
            r = FindModDll(d, fs);
            if (r.Kind != FindKind.Found || r.Path != d + "/SeaPowerTacticalUI.dll") return "Find: root file";
            fs.Tree[d] = new[] { "sub/" };
            fs.Tree[d + "/sub"] = new[] { "SeaPowerTacticalUI.dll" };
            r = FindModDll(d, fs);
            if (r.Kind != FindKind.Found || r.Path != d + "/sub/SeaPowerTacticalUI.dll") return "Find: subfolder file";
            fs.Tree[d] = new[] { "SeaPowerTacticalUI.dll", "sub/" };
            r = FindModDll(d, fs);
            if (r.Kind != FindKind.Ambiguous || r.Candidates.Length != 2) return "Find: root + subfolder must be Ambiguous(2)";
            fs.Tree[d] = new[] { "a/" };
            fs.Tree[d + "/a"] = new[] { "b/" };
            fs.Tree[d + "/a/b"] = new[] { "SeaPowerTacticalUI.dll" };
            if (FindModDll(d, fs).Kind != FindKind.NotFound) return "Find: depth 2 must not be seen";
            fs.Tree[d] = new[] { "seapowertacticalui.DLL" };
            if (FindModDll(d, fs).Kind != FindKind.Found) return "Find: case-insensitive name";
            fs.Tree[d] = new[] { "loader/" };
            fs.Tree[d + "/loader"] = new[] { "SeaPowerTacticalUI.Loader.dll" };
            if (FindModDll(d, fs).Kind != FindKind.NotFound) return "Find: the loader dll is not a candidate";

            var found = new FindResult { Kind = FindKind.Found, Dir = d, Path = d + "/x.dll" };
            Decision dec = Decide("bepinex|0.30.30|/x", found);
            if (dec.Kind != DecisionKind.Skip || dec.Line != "already started (bepinex|0.30.30|/x)") return "Decide: busy slot must Skip";
            dec = Decide(null, found);
            if (dec.Kind != DecisionKind.Load || dec.Path != d + "/x.dll") return "Decide: Found must Load";
            dec = Decide("", new FindResult { Kind = FindKind.NotFound, Dir = d });
            if (dec.Kind != DecisionKind.Report || dec.Line != "not found at " + d) return "Decide: NotFound -> " + dec.Line;
            dec = Decide(null, new FindResult { Kind = FindKind.NotFound, Dir = null });
            if (dec.Kind != DecisionKind.Report || !dec.Line.StartsWith("not found: the game is not in a Steam library")) return "Decide: null dir -> " + dec.Line;
            dec = Decide(null, new FindResult { Kind = FindKind.Ambiguous, Dir = d, Candidates = new[] { "/p1", "/p2" } });
            if (dec.Kind != DecisionKind.Report || !dec.Line.Contains("/p1") || !dec.Line.Contains("/p2")) return "Decide: Ambiguous -> " + dec.Line;
            return null;
        }
    }
}
