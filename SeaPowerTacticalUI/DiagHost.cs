using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge.Ui
{
    /// <summary>Optional: a module that reports its own diag section (ADR-0046 section 2). Read only, main thread.</summary>
    internal interface IUiDiag
    {
        string DiagSection { get; }      // one of DiagHost.Sections
        string DiagJson(int limit);      // a JSON value
    }

    /// <summary>Game-side data for the diag slot; any delegate may be null (= section null).</summary>
    internal sealed class DiagSources
    {
        public Func<string> Mission, Selfcheck, Degradation, Windows, Modules;
        public Func<int, string> Relation;   // contact id -> {"rel":"hostile|neutral|friendly|unknown|none","class":..,"kind":..,"map_hostile":bool}
        public Func<int, string> Unit;       // own uid -> {"uid":..,"name":..} | null
        public readonly Dictionary<string, Func<int, string>> Sections = new Dictionary<string, Func<int, string>>(); // fallbacks: asw, sar, cap, smart_attack, radar, overview
    }

    /// <summary>
    /// Read-only diagnostic slot "ru.voicebot.seapower.ui/diag" for the dev mod (ADR-0046). BCL only, no reference to the dev mod:
    /// the slot is registered only when the dev mod is installed (UiCore checks DevGuid). Queries are JSON lines in, JSON out, never throw.
    /// </summary>
    internal static class DiagHost
    {
        public const string DevGuid = "ru.voicebot.seapower.ui.dev";
        public const string SlotKey = "ru.voicebot.seapower.ui/diag";
        public const int Version = 1;
        public static readonly string[] Sections = { "mission", "windows", "asw", "sar", "cap", "deck", "contacts", "smart_attack", "selfcheck", "degradation", "modules", "radar", "overview", "evasion", "memory", "rings", "air_evasion", "units", "perf", "hit_estimate", "sub_datum", "strike" };

        private static DiagSources _src;
        private static Dictionary<string, IUiDiag> _modules = new Dictionary<string, IUiDiag>();
        private static string _uiVersion = "";

        internal static void Register(DiagSources src, IEnumerable<IUiDiag> modules, string uiVersion)
        {
            _src = src ?? new DiagSources();
            _uiVersion = uiVersion ?? "";
            _modules = new Dictionary<string, IUiDiag>();
            if (modules != null) foreach (var m in modules) { if (m != null && m.DiagSection != null) _modules[m.DiagSection] = m; }
            var slot = new Dictionary<string, object>();
            slot["diag"] = Version;
            slot["version"] = _uiVersion;
            slot["query"] = (Func<string, string>)Query;
            AppDomain.CurrentDomain.SetData(SlotKey, slot);
        }

        internal static void Unregister()
        {
            AppDomain.CurrentDomain.SetData(SlotKey, null);
            _src = null;
            _modules = new Dictionary<string, IUiDiag>();
        }

        /// <summary>{"q":"hello"|"state"|"relation"|"unit", ...}. Never throws: {"error":..}.</summary>
        internal static string Query(string json)
        {
            try
            {
                var src = _src;
                if (src == null) return Error("not_registered");
                var req = Json.Parse(json) as Dictionary<string, object>;
                if (req == null || !req.ContainsKey("q") || !(req["q"] is string)) return Error("bad_request");
                switch ((string)req["q"])
                {
                    case "hello": return Hello();
                    case "state": return State(src, req);
                    case "relation": return Relation(src, req);
                    case "unit": return UnitOf(src, req);
                    default: return Error("bad_query");
                }
            }
            catch (Exception e) { return Error(e.GetType().Name + ": " + e.Message); }
        }

        private static string Error(string what) { return "{\"error\":" + Json.Str(what) + "}"; }

        private static string Hello()
        {
            var sb = new StringBuilder("{\"diag\":").Append(Version).Append(",\"ui\":").Append(Json.Str(_uiVersion)).Append(",\"sections\":[");
            for (int i = 0; i < Sections.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Str(Sections[i])); }
            return sb.Append("]}").ToString();
        }

        private static int IntOf(Dictionary<string, object> req, string key, int def)
        {
            object o;
            return req.TryGetValue(key, out o) && o is double ? (int)Math.Max(int.MinValue / 2, Math.Min(int.MaxValue / 2, (double)o)) : def;
        }

        private static string State(DiagSources src, Dictionary<string, object> req)
        {
            int limit = Math.Max(1, Math.Min(100, IntOf(req, "limit", 20)));
            var names = new List<string>();
            object sec;
            if (req.TryGetValue("sections", out sec) && sec != null)
            {
                var l = sec as List<object>;
                if (l == null) return Error("bad_section");
                foreach (var o in l)
                {
                    var s = o as string;
                    if (s == null || Array.IndexOf(Sections, s) < 0) return Error("bad_section");
                    if (!names.Contains(s)) names.Add(s);
                }
            }
            else names.AddRange(Sections);
            var body = new StringBuilder();
            var errors = new StringBuilder();
            foreach (var name in names)
            {
                string value = null, err = null;
                try { value = SectionJson(src, name, limit); }
                catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
                if (value != null)
                {
                    try { Json.Parse(value); }
                    catch (Exception) { value = null; err = "invalid_json"; }
                }
                if (body.Length > 0) body.Append(',');
                body.Append(Json.Str(name)).Append(':').Append(value ?? "null");
                if (err != null)
                {
                    if (errors.Length > 0) errors.Append(',');
                    errors.Append(Json.Str(name)).Append(':').Append(Json.Str(err));
                }
            }
            var res = new StringBuilder("{\"ui\":").Append(Json.Str(_uiVersion)).Append(",\"sections\":{").Append(body).Append('}');
            if (errors.Length > 0) res.Append(",\"errors\":{").Append(errors).Append('}');
            return res.Append('}').ToString();
        }

        private static string SectionJson(DiagSources src, string name, int limit)
        {
            IUiDiag m;
            if (_modules.TryGetValue(name, out m)) return m.DiagJson(limit);
            Func<string> f = null;
            switch (name)
            {
                case "mission": f = src.Mission; break;
                case "windows": f = src.Windows; break;
                case "selfcheck": f = src.Selfcheck; break;
                case "degradation": f = src.Degradation; break;
                case "modules": f = src.Modules; break;
                default:
                    Func<int, string> g;
                    return src.Sections.TryGetValue(name, out g) && g != null ? g(limit) : null;
            }
            return f == null ? null : f();
        }

        private static readonly string[] Rels = { "hostile", "neutral", "friendly", "unknown", "none" };

        private static string Relation(DiagSources src, Dictionary<string, object> req)
        {
            object o;
            if (!req.TryGetValue("id", out o) || !(o is double)) return Error("bad_request");
            string raw = src.Relation == null ? null : src.Relation((int)(double)o);
            if (raw == null) return "{\"rel\":\"none\"}";
            var d = Json.Parse(raw) as Dictionary<string, object>;
            string rel = d != null && d.ContainsKey("rel") ? d["rel"] as string : null;
            if (rel == null || Array.IndexOf(Rels, rel) < 0) rel = "unknown";
            string cls = d != null && d.ContainsKey("class") ? d["class"] as string : null;
            string kind = d != null && d.ContainsKey("kind") ? d["kind"] as string : null;
            // 0.27.10: the main map paints the track red (AttackHostility); the dev fire guard follows the same rule as the ASW attack
            bool mapHostile = d != null && d.ContainsKey("map_hostile") && d["map_hostile"] is bool && (bool)d["map_hostile"];
            return "{\"rel\":" + Json.Str(rel) + ",\"class\":" + Json.Str(cls) + ",\"kind\":" + Json.Str(kind) + ",\"map_hostile\":" + Json.Bool(mapHostile) + "}";
        }

        private static string UnitOf(DiagSources src, Dictionary<string, object> req)
        {
            object o;
            if (!req.TryGetValue("uid", out o) || !(o is double)) return Error("bad_request");
            string raw = src.Unit == null ? null : src.Unit((int)(double)o);
            if (raw == null) return "null";
            Json.Parse(raw);
            return raw;
        }

        // ------------------------------------------------------------------ self-test

        private sealed class FakeMod : IUiDiag
        {
            public string Sec; public string Text; public bool Throw;
            public string DiagSection { get { return Sec; } }
            public string DiagJson(int limit) { if (Throw) throw new InvalidOperationException("boom"); return Text.Replace("LIMIT", limit.ToString()); }
        }

        private static Dictionary<string, object> P(string s) { return Json.Parse(s) as Dictionary<string, object>; }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            try
            {
                Unregister();
                if (!Query("{\"q\":\"hello\"}").Contains("error")) return "diag: Query without a registration must be an error";
                var src = new DiagSources();
                src.Mission = () => "{\"in_mission\":true}";
                src.Degradation = () => "{\"x\":1}";
                src.Sections["cap"] = lim => "{\"fallback\":true}";
                src.Sections["asw"] = lim => { throw new InvalidOperationException("asw boom"); };
                src.Sections["overview"] = lim => "{not json";
                src.Relation = id => id == 101 ? "{\"rel\":\"hostile\",\"class\":\"c\",\"kind\":\"Surface\"}" : id == 104 ? "{\"rel\":\"weird\"}" : null;
                src.Unit = uid => uid == 1 ? "{\"uid\":1,\"name\":\"Kirov\"}" : null;
                var mods = new List<IUiDiag>
                {
                    new FakeMod { Sec = "cap", Text = "{\"module\":true,\"limit\":LIMIT}" },
                    new FakeMod { Sec = "deck", Text = "{\"decks\":[]}" },
                    new FakeMod { Sec = "contacts", Text = "{}", Throw = true },
                };
                Register(src, mods, "9.9");
                var slot = AppDomain.CurrentDomain.GetData(SlotKey) as Dictionary<string, object>;
                if (slot == null || !(slot["diag"] is int) || (int)slot["diag"] != 1 || (slot["version"] as string) != "9.9" || !(slot["query"] is Func<string, string>)) return "diag: slot shape";
                var h = P(Query("{\"q\":\"hello\"}"));
                if (h == null || !(h["diag"] is double) || (double)h["diag"] != 1) return "diag: hello";
                var st = P(Query("{\"q\":\"state\",\"limit\":500}"));
                if (st == null) return "diag: state is not JSON";
                var secs = st["sections"] as Dictionary<string, object>;
                if (secs == null || secs.Count != Sections.Length) return "diag: state must carry all sections";
                foreach (var n in Sections) if (!secs.ContainsKey(n)) return "diag: section missing: " + n;
                if (secs["sar"] != null || secs["radar"] != null || secs["windows"] != null) return "diag: absent sections must be null";
                var mission = secs["mission"] as Dictionary<string, object>;
                if (mission == null || !(mission["in_mission"] is bool)) return "diag: mission";
                var cap = secs["cap"] as Dictionary<string, object>;
                if (cap == null || !cap.ContainsKey("module")) return "diag: IUiDiag must override the fallback";
                if ((double)cap["limit"] != 100) return "diag: limit 500 must become 100";
                var st0 = P(Query("{\"q\":\"state\",\"sections\":[\"cap\"],\"limit\":0}"));
                var cap0 = (st0["sections"] as Dictionary<string, object>)["cap"] as Dictionary<string, object>;
                if ((double)cap0["limit"] != 1) return "diag: limit 0 must become 1";
                var stDef = P(Query("{\"q\":\"state\",\"sections\":[\"cap\"]}"));
                if ((double)((stDef["sections"] as Dictionary<string, object>)["cap"] as Dictionary<string, object>)["limit"] != 20) return "diag: default limit 20";
                var errs = st.ContainsKey("errors") ? st["errors"] as Dictionary<string, object> : null;
                if (errs == null || !errs.ContainsKey("asw") || !errs.ContainsKey("contacts") || !errs.ContainsKey("overview")) return "diag: errors per section";
                if (!(errs["asw"] as string).StartsWith("InvalidOperationException")) return "diag: error text";
                if (secs["asw"] != null || secs["contacts"] != null || secs["overview"] != null) return "diag: a failed section must be null";
                if (secs["deck"] == null || secs["degradation"] == null) return "diag: other sections must survive a failing one";
                if (Query("{\"q\":\"state\",\"sections\":[\"nope\"]}") != "{\"error\":\"bad_section\"}") return "diag: bad_section";
                if (!Query("{oops").Contains("\"error\"")) return "diag: broken JSON";
                if (!Query("{\"q\":\"zzz\"}").Contains("\"error\"")) return "diag: unknown query";
                if (P(Query("{\"q\":\"relation\",\"id\":101}"))["rel"] as string != "hostile") return "diag: relation hostile";
                if (P(Query("{\"q\":\"relation\",\"id\":105}"))["rel"] as string != "none") return "diag: relation 105 must be none";
                if (P(Query("{\"q\":\"relation\",\"id\":104}"))["rel"] as string != "unknown") return "diag: relation weird must be unknown";
                if (P(Query("{\"q\":\"unit\",\"uid\":1}"))["name"] as string != "Kirov") return "diag: unit";
                if (Query("{\"q\":\"unit\",\"uid\":7}") != "null") return "diag: unknown unit must be null";
                Unregister();
                if (AppDomain.CurrentDomain.GetData(SlotKey) != null) return "diag: Unregister must clear the slot";
                if (!Query("{\"q\":\"hello\"}").Contains("error")) return "diag: Query after Unregister";
                return null;
            }
            catch (Exception e) { Unregister(); return "diag: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
