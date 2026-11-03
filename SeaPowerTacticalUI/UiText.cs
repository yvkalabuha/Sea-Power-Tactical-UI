using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-103 core: one place for player-visible strings of the UI mod.
    /// Lookup order: the game's own getText (GameLookup, may be null) -> own ini tables next to the DLL (lang/language_xx/*.ini)
    /// for the current language -> the same for "en" -> the id ("section.key"). Keys are case-insensitive, like in the game.
    /// Pure C# (no Unity, no game types): tested offline by the uitexttest stand and the selfcheck.
    /// "L0 API ready": Get(key), Get(section,key), Fmt(key,args), FmtIn(section,key,args), Configure, SetLanguage, Lang, GameLookup, MakeGameLookup, LoadIni.
    /// </summary>
    internal static class UiText
    {
        public const string FallbackLang = "en";
        public const string DefaultSection = "SeaPowerTacticalUI";
        private static Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        private static string _lang = FallbackLang;
        private static string _cfgLang = "auto";
        private static Func<string, string, string> _gameLookup;
        private static Func<string> _gameLang;
        private static string _gameLangCached;      // normalized game language, refreshed by Poll()/setters only
        private static bool _gameLangKnown;
        private static Dictionary<(string, string), string> _cache = new Dictionary<(string, string), string>();
        private static Dictionary<(string, string), bool> _hasCache = new Dictionary<(string, string), bool>();
        private static List<string> _dups = new List<string>();

        /// <summary>+1 whenever the effective language or the loaded tables change (Poll/SetLanguage/Push/Load*/Reset). Callers that cache texts redraw on a change.</summary>
        public static int Revision { get; private set; }

        /// <summary>Game text lookup (section, key) -> text or null when the game does not know the key. Set by the plugin.</summary>
        public static Func<string, string, string> GameLookup
        {
            get { return _gameLookup; }
            set { _gameLookup = value; Touch(); }
        }

        /// <summary>Current game language code ("en", "ru", ...) or null. Used when the configured language is "auto". Re-read only by Poll().</summary>
        public static Func<string> GameLang
        {
            get { return _gameLang; }
            set { _gameLang = value; _gameLangKnown = false; Touch(); }
        }

        /// <summary>Keys met in two files (or twice) of one language, as "lang:section.key". For the test stand.</summary>
        internal static IEnumerable<string> DuplicateKeys { get { return _dups; } }

        public static string Lang { get { return CurrentLang(); } }

        private static void Touch() { _cache.Clear(); _hasCache.Clear(); Revision++; }

        private static string ReadGameLang()
        {
            string g = null;
            try { g = _gameLang != null ? _gameLang() : null; } catch { }
            return Norm(g);
        }

        private static string CurrentLang()
        {
            if (_cfgLang != "auto") return _cfgLang;
            if (!_gameLangKnown) { _gameLangCached = ReadGameLang(); _gameLangKnown = true; }
            return _gameLangCached ?? _lang;
        }

        /// <summary>Re-reads the game language (cheap; call about once a second). True when the effective language changed: Revision++ and the text cache is dropped.</summary>
        public static bool Poll()
        {
            string before = CurrentLang();
            if (_cfgLang == "auto") { _gameLangCached = ReadGameLang(); _gameLangKnown = true; }
            string now = CurrentLang();
            if (now == before) return false;
            _cache.Clear(); _hasCache.Clear(); Revision++;
            return true;
        }

        private static string Norm(string l)
        {
            if (string.IsNullOrWhiteSpace(l)) return null;
            l = l.Trim().ToLowerInvariant();
            int i = l.IndexOfAny(new[] { '-', '_' });
            return i > 0 ? l.Substring(0, i) : l;
        }

        /// <summary>cfg = "auto" (follow the game) or a language code. dir = folder that holds language_xx subfolders (may be missing).</summary>
        public static void Configure(string dir, string cfg)
        {
            SetLanguage(cfg);
            if (!string.IsNullOrEmpty(dir)) LoadDir(dir);
        }

        public static void SetLanguage(string cfg)
        {
            string n = Norm(cfg);
            _cfgLang = (n == null || n == "auto") ? "auto" : n;
            if (_cfgLang != "auto") _lang = _cfgLang;
            _gameLangKnown = false;
            Touch();
        }

        /// <summary>Temporary language for a SelfTest or a stand: restores the previous setting on Dispose.</summary>
        public static IDisposable Push(string lang)
        {
            var saved = new Saved { Cfg = _cfgLang, Lang = _lang };
            SetLanguage(lang);
            return saved;
        }

        private sealed class Saved : IDisposable
        {
            public string Cfg, Lang;
            private bool _done;
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _cfgLang = Cfg; _lang = Lang; _gameLangKnown = false;
                Touch();
            }
        }

        /// <summary>
        /// Subscribes Poll() to the game's LanguageResourceHandler.LanguageChanged (System.EventHandler event; the EventInfo comes from the registry, TextSpecs).
        /// Returns a token whose Dispose unsubscribes, or null when the event is absent / subscription fails (the caller then
        /// calls Poll() at cheap moments: mission load, return to main menu, opening a mod window - never on a timer).
        /// </summary>
        public static IDisposable SubscribeLanguageChanged(object languageResourceHandler, EventInfo ev)
        {
            if (languageResourceHandler == null || ev == null) return null;
            try
            {
                if (ev.EventHandlerType != typeof(EventHandler)) return null;
                EventHandler h = (s, e) => { try { Poll(); } catch { } };
                ev.AddEventHandler(languageResourceHandler, h);
                return new Unsub { Target = languageResourceHandler, Ev = ev, H = h };
            }
            catch { return null; }
        }

        private sealed class Unsub : IDisposable
        {
            public object Target; public EventInfo Ev; public EventHandler H;
            public void Dispose()
            {
                if (H == null) return;
                var h = H; H = null;
                try { Ev.RemoveEventHandler(Target, h); } catch { }
            }
        }

        /// <summary>Walks up from startDir to a folder that contains SeaPowerTacticalUI/lang (or is SeaPowerTacticalUI itself with lang/). Null when none.</summary>
        public static string FindLangDir(string startDir)
        {
            try
            {
                var d = string.IsNullOrEmpty(startDir) ? null : new DirectoryInfo(startDir);
                while (d != null)
                {
                    string c = Path.Combine(d.FullName, "SeaPowerTacticalUI", "lang");
                    if (Directory.Exists(c)) return c;
                    if (d.Name == "SeaPowerTacticalUI" && Directory.Exists(Path.Combine(d.FullName, "lang"))) return Path.Combine(d.FullName, "lang");
                    d = d.Parent;
                }
            }
            catch { }
            return null;
        }

        /// <summary>T-271: the lang/ path baked into a stand by Directory.Build.targets (AssemblyMetadata LangDir), or null.</summary>
        private static string MetadataLangDir()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly();
                if (asm == null) return null;
                foreach (var a in asm.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                {
                    var m = (AssemblyMetadataAttribute)a;
                    if (m.Key == "LangDir") return m.Value;
                }
            }
            catch { }
            return null;
        }

        /// <summary>For stands and selfcheck: Reset + load the real tables + set the language. Returns the number of ini files read.</summary>
        public static int LoadForTests(string lang)
        {
            Reset();
            int n = LoadDir(FindLangDir(AppContext.BaseDirectory));
            if (n == 0) n = LoadDir(MetadataLangDir()); // T-271: stand built outside the tree
            SetLanguage(lang);
            return n;
        }

        /// <summary>T-144: values for the Ui.Language choice: "auto" first, then the codes of the language_xx folders under langDir, alphabetical. A missing folder gives just "auto".</summary>
        public static string[] Languages(string langDir)
        {
            var codes = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(langDir) && Directory.Exists(langDir))
                    foreach (var d in Directory.GetDirectories(langDir, "language_*"))
                    {
                        string code = Norm(Path.GetFileName(d).Substring("language_".Length));
                        if (code != null && code != "auto" && !codes.Contains(code)) codes.Add(code);
                    }
            }
            catch { }
            codes.Sort(StringComparer.Ordinal);
            codes.Insert(0, "auto");
            return codes.ToArray();
        }

        /// <summary>Loads every lang/language_xx/*.ini under dir. Returns the number of files read; broken files are skipped.</summary>
        public static int LoadDir(string dir)
        {
            int n = 0;
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
                foreach (var d in Directory.GetDirectories(dir, "language_*"))
                {
                    string lang = Norm(Path.GetFileName(d).Substring("language_".Length));
                    if (lang == null) continue;
                    var files = Directory.GetFiles(d, "*.ini");
                    Array.Sort(files, StringComparer.Ordinal);
                    foreach (var f in files)
                    {
                        try { LoadIni(lang, File.ReadAllText(f)); n++; } catch { }
                    }
                }
            }
            catch { }
            return n;
        }

        /// <summary>Parses ini text ([Section] / key=value, ';' and '#' comments, "\n" escape) into the table of lang. Later files do not override earlier keys (a repeat is recorded in DuplicateKeys).</summary>
        public static int LoadIni(string lang, string text)
        {
            Dictionary<string, string> t;
            if (!Tables.TryGetValue(lang, out t)) { t = new Dictionary<string, string>(); Tables[lang] = t; }
            string sec = DefaultSection;
            int added = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']') { sec = line.Substring(1, line.Length - 2).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = Id(sec, line.Substring(0, eq).Trim());
                string val = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                if (!t.ContainsKey(key)) { t[key] = val; added++; }
                else _dups.Add(lang + ":" + key);
            }
            Touch();
            return added;
        }

        public static void Reset()
        {
            Tables.Clear(); _dups.Clear();
            _lang = FallbackLang; _cfgLang = "auto"; _gameLookup = null; _gameLang = null; _gameLangKnown = false; _gameLangCached = null;
            Touch();
        }

        private static string Id(string section, string key) { return (section + "." + key).ToLowerInvariant(); }

        private static string Own(string lang, string id)
        {
            string v;
            Dictionary<string, string> t;
            if (Tables.TryGetValue(lang, out t) && t.TryGetValue(id, out v)) return v;
            if (lang != FallbackLang && Tables.TryGetValue(FallbackLang, out t) && t.TryGetValue(id, out v)) return v;
            return null;
        }

        /// <summary>Own tables only (current language, then en); null when absent. Never calls GameLookup: for the getText interception (no recursion).</summary>
        public static string GetOwn(string section, string key) { return Own(CurrentLang(), Id(section, key)); }

        /// <summary>True when the game or the own tables have a text for the key.</summary>
        public static bool Has(string section, string key)
        {
            bool hit;
            var ck = (section, key);
            if (_hasCache.TryGetValue(ck, out hit)) return hit;
            hit = Lookup(section, key) != null || GetOwn(section, key) != null;
            _hasCache[ck] = hit;
            return hit;
        }

        private static string Lookup(string section, string key)
        {
            var gl = _gameLookup;
            if (gl == null) return null;
            try
            {
                string g = gl(section, key);
                if (!IsMissing(g, key)) return g;
            }
            catch { }
            return null;
        }

        public static string Get(string key) { return Get(DefaultSection, key); }

        public static string Get(string section, string key)
        {
            var ck = (section, key);
            string hit;
            if (_cache.TryGetValue(ck, out hit)) return hit;
            string v = Lookup(section, key) ?? Own(CurrentLang(), Id(section, key)) ?? (section + "." + key);
            _cache[ck] = v;
            return v;
        }

        /// <summary>The game's answer is not a real text: null/empty, the key itself, or its "MISSING TEXT - [Section]key" placeholder.</summary>
        internal static bool IsMissing(string g, string key)
        {
            if (string.IsNullOrWhiteSpace(g)) return true;
            if (g == key) return true;
            return g.TrimStart().StartsWith("MISSING TEXT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A rough time span for the deck rows: "45 s" (under a minute), "26 min" (rounded up), "1 h 05 min". Negative / NaN -> em dash.</summary>
        public static string Duration(double s)
        {
            if (s < 0 || double.IsNaN(s) || double.IsInfinity(s)) return "\u2014";
            if (s < 60) return Fmt("ui.time.sec", (int)Math.Ceiling(s));
            int m = (int)Math.Ceiling(s / 60.0);
            if (m < 60) return Fmt("ui.time.min", m);
            return Fmt("ui.time.hmin", m / 60, (m % 60).ToString("00", CultureInfo.InvariantCulture));
        }

        /// <summary>T-178 Ke1: the Russian plural form for a whole number: 1, 21, 101 -> one; 2-4, 22-24 -> few; 0, 5-20, 25-30, 11-14, 111-114 -> many.</summary>
        public static string PluralRu(long n, string one, string few, string many)
        {
            long a = Math.Abs(n), m100 = a % 100, m10 = a % 10;
            if (m100 >= 11 && m100 <= 14) return many;
            if (m10 == 1) return one;
            if (m10 >= 2 && m10 <= 4) return few;
            return many;
        }

        public static string Fmt(string key, params object[] args) { return FmtIn(DefaultSection, key, args); }

        public static string FmtIn(string section, string key, params object[] args)
        {
            string s = Get(section, key);
            try { return string.Format(CultureInfo.InvariantCulture, s, args ?? new object[0]); } catch (FormatException) { return s; }
        }

        /// <summary>Adapter for LanguageResourceHandler.getText(section, key) by reflection; the instance comes from the game side. Quiet on missing keys.</summary>
        public static Func<string, string, string> MakeGameLookup(object languageResourceHandler, MethodInfo getText)
        {
            if (languageResourceHandler == null || getText == null) return null;
            var ps = getText.GetParameters();
            if (ps.Length < 2 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(string)) return null;
            return (s, k) =>
            {
                var a = new object[ps.Length];
                a[0] = s; a[1] = k;
                for (int i = 2; i < ps.Length; i++) a[i] = ps[i].ParameterType == typeof(bool) ? (object)true : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                return getText.Invoke(languageResourceHandler, a) as string;
            };
        }

        /// <summary>Runs on scratch tables and puts the previous state back (tables loaded by a stand/plugin survive).</summary>
        public static string SelfTest()
        {
            var tables = Tables; var dups = _dups; var lang = _lang; var cfg = _cfgLang;
            var gl = _gameLookup; var gg = _gameLang; var gc = _gameLangCached; var gk = _gameLangKnown;
            Tables = new Dictionary<string, Dictionary<string, string>>(); _dups = new List<string>();
            try { return SelfTestBody(); }
            finally
            {
                Tables = tables; _dups = dups; _lang = lang; _cfgLang = cfg;
                _gameLookup = gl; _gameLang = gg; _gameLangCached = gc; _gameLangKnown = gk;
                Touch();
            }
        }

        private static string SelfTestBody()
        {
            Reset();
            LoadIni("en", "[SeaPowerTacticalUI]\nhello=Hello {0}\n; c\n[Tooltip]\nVB_X=Tip\n");
            LoadIni("ru", "[SeaPowerTacticalUI]\nhello=Привет {0}\n");
            SetLanguage("ru");
            if (Get("hello") != "Привет {0}") return "ru get";
            if (Fmt("HELLO", "a") != "Привет a") return "ru fmt/case";
            if (Get("Tooltip", "vb_x") != "Tip") return "fallback en";
            if (Get("nope") != "SeaPowerTacticalUI.nope") return "unknown id";
            SetLanguage("de");
            if (Get("hello") != "Hello {0}") return "unknown language -> en";
            SetLanguage("auto");
            GameLang = () => "ru_RU";
            if (Lang != "ru") return "auto from game";
            GameLookup = (s, k) => k == "hello" ? "Game" : null;
            if (Get("hello") != "Game") return "game first";
            if (Get("Tooltip", "VB_X") != "Tip") return "game miss -> own";
            GameLookup = (s, k) => "MISSING TEXT - [" + s + "]" + k;
            if (Get("hello") != "Hello {0}" && Get("hello") != "Привет {0}") return "missing placeholder -> own";
            if (Get("nope") != "SeaPowerTacticalUI.nope") return "missing placeholder -> id";
            GameLookup = (s, k) => "  ";
            if (Get("Tooltip", "VB_X") != "Tip") return "blank -> own";
            Reset();
            string tmp = Path.Combine(Path.GetTempPath(), "uitext-lang-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (string.Join(",", Languages(tmp)) != "auto") return "Languages: missing folder";
                Directory.CreateDirectory(Path.Combine(tmp, "language_ru"));
                Directory.CreateDirectory(Path.Combine(tmp, "language_en"));
                Directory.CreateDirectory(Path.Combine(tmp, "language_de"));
                Directory.CreateDirectory(Path.Combine(tmp, "other"));
                if (string.Join(",", Languages(tmp)) != "auto,de,en,ru") return "Languages: got " + string.Join(",", Languages(tmp));
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            return null;
        }
    }
}
