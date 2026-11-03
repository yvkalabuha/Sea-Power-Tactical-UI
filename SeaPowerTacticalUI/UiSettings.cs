using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-144 (ADR-0050): type-erased access to one bound setting. No Unity, no BepInEx.</summary>
    internal interface IUiSettingBox
    {
        Type Type { get; }
        object Get();
        void Set(object v);
        object Default { get; }
        /// <summary>Invariant culture; DisplayFactor applied.</summary>
        string Format(object v);
        /// <summary>Range / options check. error = "format" | "range" | "option".</summary>
        bool TryParse(string s, out object v, out string error);
    }

    /// <summary>One config key as the settings window sees it. Filled by UiSettingsRegistry.Track.</summary>
    internal sealed class UiSettingEntry
    {
        public string Owner, Section, CfgSection, Key, Description;   // Section = Meta.Section ?? Owner
        public UiKeyClass Class;
        public UiSettingMeta Meta;
        public IUiSettingBox Box;
        public int BindIndex;
        public object StartValue;          // value after normalisation at start
        public bool ApplyFailed;           // a handler threw: shown as a warning
        internal readonly List<string> Owners = new List<string>();
        internal readonly List<KeyValuePair<string, Action<object>>> Handlers = new List<KeyValuePair<string, Action<object>>>();

        // T-239: cached; rebuilt only if CfgSection or Key was reassigned (reference compare). Zero allocations on a hit.
        private string _fkSec, _fkKey, _fk, _tkSec, _tkKey, _tk;

        public string FullKey
        {
            get
            {
                if (_fk == null || !ReferenceEquals(_fkSec, CfgSection) || !ReferenceEquals(_fkKey, Key)) { _fkSec = CfgSection; _fkKey = Key; _fk = CfgSection + "." + Key; }
                return _fk;
            }
        }

        public string TextKey
        {
            get
            {
                if (_tk == null || !ReferenceEquals(_tkSec, CfgSection) || !ReferenceEquals(_tkKey, Key)) { _tkSec = CfgSection; _tkKey = Key; _tk = ("settings." + CfgSection + "." + Key).ToLowerInvariant(); }
                return _tk;
            }
        }

        public UiControl Control
        {
            get
            {
                if (Meta != null && Meta.Control != UiControl.Auto) return Meta.Control;
                Type t = Box.Type;
                if (t == typeof(bool)) return UiControl.Check;
                if (t == typeof(int) || t == typeof(float) || t == typeof(double)) return UiControl.Number;
                return UiControl.Text;
            }
        }
    }

    internal sealed class UiApplyReport
    {
        public int Live, NextOrder, Restart;
        public List<string> Failed = new List<string>();
        public List<string> Keys = new List<string>();
    }

    /// <summary>Every module declares its keys through Bind (+UiSettingMeta); this registry collects them. No central list of keys.</summary>
    internal sealed class UiSettingsRegistry
    {
        public const string General = "general", Hotkeys = "hotkeys";

        private readonly Action<string> _logInfo, _logWarn;
        private readonly List<UiSettingEntry> _all = new List<UiSettingEntry>();
        private readonly Dictionary<string, UiSettingEntry> _byKey = new Dictionary<string, UiSettingEntry>();
        private readonly List<KeyValuePair<object, UiSettingEntry>> _byWrapper = new List<KeyValuePair<object, UiSettingEntry>>();
        private readonly Dictionary<string, IUiSettingsApplied> _applied = new Dictionary<string, IUiSettingsApplied>();
        private readonly Dictionary<string, int> _order = new Dictionary<string, int>();
        private readonly HashSet<string> _unknownOrder = new HashSet<string>();

        public UiSettingsRegistry(Action<string> logInfo, Action<string> logWarn)
        {
            _logInfo = logInfo; _logWarn = logWarn;
            _order[General] = 10; _order[Hotkeys] = 20;
        }

        public IReadOnlyList<UiSettingEntry> All { get { return _all; } }

        /// <summary>T-239: +1 on every Track / section order change = anything that changes Sections() or InSection().</summary>
        public int Revision { get; private set; }

        /// <summary>T-239: O(1) lookup by "Section.Key", null if absent.</summary>
        public UiSettingEntry Find(string fullKey)
        {
            UiSettingEntry e;
            return fullKey != null && _byKey.TryGetValue(fullKey, out e) ? e : null;
        }

        private int _secRev = -1;
        private IList<string> _secCache;
        private readonly Dictionary<string, IList<UiSettingEntry>> _inSecCache = new Dictionary<string, IList<UiSettingEntry>>();
        private int _inSecRev = -1;

        /// <summary>T-165: null = every capability present. Set by UiContext.</summary>
        public Func<string, bool> CapabilityPresent { get; set; }

        /// <summary>T-165: false = the row needs a capability the game does not give (shown as "unavailable").</summary>
        public bool Available(UiSettingEntry e)
        {
            string c = e == null || e.Meta == null ? null : e.Meta.Capability;
            return c == null || CapabilityPresent == null || CapabilityPresent(c);
        }

        private readonly Dictionary<string, Func<string>> _gates = new Dictionary<string, Func<string>>();

        /// <summary>T-164: noteKey() returns the text key of the note while the gate is closed, null while open. A second call replaces the first.</summary>
        public void SetGate(string gate, Func<string> noteKey)
        {
            if (string.IsNullOrEmpty(gate)) return;
            if (noteKey == null) _gates.Remove(gate); else _gates[gate] = noteKey;
        }

        /// <summary>Text key of the closed gate of the row; null = no gate / unknown gate / open / noteKey threw.</summary>
        public string GateNote(UiSettingEntry e)
        {
            string g = e == null || e.Meta == null ? null : e.Meta.Gate;
            Func<string> f;
            if (g == null || !_gates.TryGetValue(g, out f)) return null;
            try { return f(); } catch (Exception) { return null; }
        }

        /// <summary>The row is shown disabled: GateNote != null and !Meta.GateSoft.</summary>
        public bool Gated(UiSettingEntry e)
        {
            return GateNote(e) != null && !e.Meta.GateSoft;
        }

        /// <summary>Set by UiCore around the Bind of a module.</summary>
        public string CurrentOwner { get; set; }

        private void Info(string m) { if (_logInfo != null) _logInfo(m); }
        private void Warn(string m) { if (_logWarn != null) _logWarn(m); }

        // ------------------------------------------------------------------ boxes

        private sealed class Box<T> : IUiSettingBox
        {
            private readonly IUiSetting<T> _s; private readonly UiSettingMeta _m;
            public Box(IUiSetting<T> s, UiSettingMeta m) { _s = s; _m = m; }
            public Type Type { get { return typeof(T); } }
            public object Get() { return _s.Value; }
            public void Set(object v) { _s.Value = (T)v; }
            public object Default { get { return _s.Default; } }
            private double Factor { get { return _m == null || _m.DisplayFactor == 0 ? 1.0 : _m.DisplayFactor; } }

            public string Format(object v)
            {
                if (v == null) return "";
                if (v is bool) return (bool)v ? "true" : "false";
                if (v is int) return ((int)v * Factor).ToString("0.######", CultureInfo.InvariantCulture);
                if (v is float) return Round(((float)v) * Factor).ToString("0.######", CultureInfo.InvariantCulture);
                if (v is double) return Round((double)v * Factor).ToString("0.######", CultureInfo.InvariantCulture);
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            }

            private static double Round(double d) { return Math.Round(d, 6); }

            public bool TryParse(string s, out object v, out string error)
            {
                v = null; error = null;
                s = (s ?? "").Trim();
                Type t = typeof(T);
                if (t == typeof(bool))
                {
                    string l = s.ToLowerInvariant();
                    if (l == "true" || l == "1" || l == "on" || l == "yes") { v = true; return true; }
                    if (l == "false" || l == "0" || l == "off" || l == "no") { v = false; return true; }
                    error = "format"; return false;
                }
                if (t == typeof(int) || t == typeof(float) || t == typeof(double))
                {
                    double d;
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || double.IsInfinity(d)) { error = "format"; return false; }
                    double stored = d / Factor;
                    if (t == typeof(int) && Math.Abs(stored - Math.Round(stored)) > 1e-9) { error = "format"; return false; }
                    bool auto = _m != null && _m.ZeroIsAuto && Math.Abs(stored) < 1e-12;
                    if (!auto && _m != null)
                    {
                        const double eps = 1e-9;
                        if (!double.IsNaN(_m.Min) && stored < _m.Min - eps) { error = "range"; return false; }
                        if (!double.IsNaN(_m.Max) && stored > _m.Max + eps) { error = "range"; return false; }
                    }
                    if (t == typeof(int)) v = (int)Math.Round(stored);
                    else if (t == typeof(float)) v = (float)stored;
                    else v = stored;
                    return true;
                }
                UiControl c = _m == null ? UiControl.Auto : _m.Control;
                if (c == UiControl.Choice && _m.Options != null)
                {
                    string o = Canon(_m.Options, s);
                    if (o == null) { error = "option"; return false; }
                    v = o; return true;
                }
                if (c == UiControl.Multi)
                {
                    List<string> unknown;
                    v = NormMulti(_m, s, out unknown);
                    if (unknown.Count > 0) { error = "option"; v = null; return false; }
                    return true;
                }
                if (c == UiControl.Hotkey)
                {
                    List<string[]> cs; string e;
                    if (!HotkeyBinding.TryParseSpec(s, out cs, out e)) { error = "format"; return false; }
                    v = s; return true;
                }
                v = s;
                return true;
            }
        }

        internal static string Canon(string[] options, string s)
        {
            foreach (var o in options) if (string.Equals(o, s, StringComparison.OrdinalIgnoreCase)) return o;
            return null;
        }

        /// <summary>Comma list: known items only (Options + Required, canonical case, no repeats), missing Required in front.</summary>
        internal static string NormMulti(UiSettingMeta m, string s, out List<string> unknown)
        {
            unknown = new List<string>();
            var known = new List<string>();
            var items = new List<string>();
            if (m.Required != null) known.AddRange(m.Required);
            if (m.Options != null) known.AddRange(m.Options);
            foreach (var raw in (s ?? "").Split(','))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                string c = Canon(known.ToArray(), t);
                if (c == null) { unknown.Add(t); continue; }
                if (!items.Contains(c)) items.Add(c);
            }
            var res = new List<string>();
            if (m.Required != null) foreach (var r in m.Required) if (!items.Contains(r)) res.Add(r);
            res.AddRange(items);
            return string.Join(",", res.ToArray());
        }

        // ------------------------------------------------------------------ track

        /// <summary>Called by UiCore for every Bind. The first Bind of a key creates the entry; a repeat only adds an owner.</summary>
        public IUiSetting<T> Track<T>(string section, string key, string description, UiKeyClass cls, UiSettingMeta meta, IUiSetting<T> s)
        {
            string owner = CurrentOwner ?? "core";
            string full = section + "." + key;
            UiSettingEntry e;
            if (_byKey.TryGetValue(full, out e))
            {
                if (!owner.Equals(e.Owner) && !e.Owners.Contains(owner)) e.Owners.Add(owner);
                bool sameMeta = (e.Meta == null && meta == null) || (e.Meta != null && e.Meta.SameAs(meta));
                if (e.Class != cls || !sameMeta)
                    Warn("settings: " + full + " is bound twice with a different " + (e.Class != cls ? "class" : "meta") + " (" + e.Owner + " / " + owner + ")");
                _byWrapper.Add(new KeyValuePair<object, UiSettingEntry>(s, e));
                return s;
            }
            e = new UiSettingEntry
            {
                Owner = owner, CfgSection = section, Key = key, Description = description, Class = cls, Meta = meta,
                Section = meta != null && meta.Section != null ? meta.Section : owner,
                Box = new Box<T>(s, meta), BindIndex = _all.Count,
            };
            e.Owners.Add(owner);
            Normalise(e);
            e.StartValue = e.Box.Get();
            _all.Add(e);
            _byKey[full] = e;
            Revision++;
            _byWrapper.Add(new KeyValuePair<object, UiSettingEntry>(s, e));
            var entry = e;
            s.Changed += v => Dispatch(entry, v);
            return s;
        }

        private void Normalise(UiSettingEntry e)
        {
            UiSettingMeta m = e.Meta;
            if (m == null || !(e.Box.Get() is string)) return;
            string cur = (string)e.Box.Get();
            if (m.Control == UiControl.Choice && m.Options != null)
            {
                string c = Canon(m.Options, cur ?? "");
                if (c == null)
                {
                    string def = e.Box.Default as string;
                    Warn("config: [" + e.CfgSection + "] " + e.Key + " '" + cur + "' unknown, default '" + def + "'");
                    e.Box.Set(def);
                }
                else if (c != cur) e.Box.Set(c);
            }
            else if (m.Control == UiControl.Multi)
            {
                List<string> unknown;
                string n = NormMulti(m, cur, out unknown);
                if (unknown.Count > 0) Info("config: [" + e.CfgSection + "] " + e.Key + " dropped unknown " + string.Join(",", unknown.ToArray()));
                if (n != cur) e.Box.Set(n);
            }
        }

        private void Dispatch(UiSettingEntry e, object v)
        {
            foreach (var h in e.Handlers.ToArray())
            {
                try { h.Value(v); }
                catch (Exception ex)
                {
                    bool first = !e.ApplyFailed;
                    e.ApplyFailed = true;
                    if (first) Warn("settings: " + e.FullKey + " handler of " + h.Key + " failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private UiSettingEntry Find(object wrapper)
        {
            foreach (var p in _byWrapper) if (ReferenceEquals(p.Key, wrapper)) return p.Value;
            return null;
        }

        /// <summary>Run h when the value really changes. Dropped with DropOwner. A throwing handler marks the key ApplyFailed; the others still run.</summary>
        public void OnChanged<T>(string owner, IUiSetting<T> s, Action<T> h)
        {
            UiSettingEntry e = Find(s);
            if (e == null) return;
            if (!e.Owners.Contains(owner)) e.Owners.Add(owner);
            e.Handlers.Add(new KeyValuePair<string, Action<object>>(owner, o => h((T)o)));
        }

        public bool HasSubscriber(UiSettingEntry e)
        {
            if (e.Handlers.Count > 0) return true;
            foreach (var o in e.Owners) if (_applied.ContainsKey(o)) return true;
            return false;
        }

        public void DropOwner(string owner)
        {
            foreach (var e in _all) e.Handlers.RemoveAll(h => h.Key == owner);
            _applied.Remove(owner);
        }

        public void SetApplied(string owner, IUiSettingsApplied target)
        {
            if (target == null) _applied.Remove(owner); else _applied[owner] = target;
        }

        public void SetSectionOrder(string sectionId, int order) { _order[sectionId] = order; Revision++; }

        // ------------------------------------------------------------------ queries

        private bool Visible(UiSettingEntry e) { return e.Class != UiKeyClass.Internal; }

        /// <summary>Section ids that have a visible entry, by order (general, hotkeys, modules...); an unknown order = 1000 and one log line.</summary>
        public IList<string> Sections()
        {
            if (_secCache != null && _secRev == Revision) return _secCache;
            var ids = new List<string>();
            foreach (var e in _all)
            {
                if (!Visible(e)) continue;
                string id = e.Control == UiControl.Hotkey ? Hotkeys : e.Section;
                if (!ids.Contains(id)) ids.Add(id);
            }
            foreach (var id in ids)
                if (!_order.ContainsKey(id) && _unknownOrder.Add(id)) Info("settings: section '" + id + "' has no order, put last");
            var sorted = ids.Select((id, i) => new { id, i, o = _order.ContainsKey(id) ? _order[id] : 1000 })
                      .OrderBy(x => x.o).ThenBy(x => x.i).Select(x => x.id).ToList();
            _secCache = sorted.AsReadOnly(); _secRev = Revision;   // T-239: read-only, cached by Revision
            return _secCache;
        }

        /// <summary>Rows of a section: Meta.Order &gt; 0 first (by Order), then Bind order. "hotkeys" = every Hotkey entry; other sections skip them. Internal keys are never listed.</summary>
        public IList<UiSettingEntry> InSection(string sectionId)
        {
            if (_inSecRev != Revision) { _inSecCache.Clear(); _inSecRev = Revision; }
            IList<UiSettingEntry> hit;
            if (sectionId != null && _inSecCache.TryGetValue(sectionId, out hit)) return hit;
            IEnumerable<UiSettingEntry> q = _all.Where(e => Visible(e) &&
                (sectionId == Hotkeys ? e.Control == UiControl.Hotkey : e.Control != UiControl.Hotkey && e.Section == sectionId));
            var res = q.OrderBy(e => e.Meta != null && e.Meta.Order > 0 ? 0 : 1)
                    .ThenBy(e => e.Meta != null && e.Meta.Order > 0 ? e.Meta.Order : 0)
                    .ThenBy(e => e.BindIndex).ToList().AsReadOnly();
            if (sectionId != null) _inSecCache[sectionId] = res;
            return res;
        }

        /// <summary>What really happens on a change: Live / NextOrder without a subscriber are honest Restart.</summary>
        public UiApply Effective(UiSettingEntry e)
        {
            UiApply a = e.Meta == null ? UiApply.Restart : e.Meta.Apply;
            return a != UiApply.Restart && !HasSubscriber(e) ? UiApply.Restart : a;
        }

        public bool PendingRestart(UiSettingEntry e)
        {
            if (e.ApplyFailed) return true;
            return Effective(e) == UiApply.Restart && !Equals(e.Box.Get(), e.StartValue);
        }

        // ------------------------------------------------------------------ apply

        /// <summary>ADR-0050 section 3: Batch, Set in registry order (-> Changed), SettingsApplied once per owner, one Save.
        /// edits: FullKey -> new value (type of the setting).</summary>
        public UiApplyReport Apply(IUiHost host, IDictionary<string, object> edits)
        {
            var rep = new UiApplyReport();
            var changedBy = new Dictionary<string, List<string>>();
            var ownerOrder = new List<string>();
            using (host.Batch())
            {
                foreach (var e in _all)
                {
                    object v;
                    if (!edits.TryGetValue(e.FullKey, out v)) continue;
                    if (Equals(e.Box.Get(), v)) continue;
                    bool failedBefore = e.ApplyFailed;
                    try { e.Box.Set(v); }
                    catch (Exception ex)
                    {
                        e.ApplyFailed = true;
                        Warn("settings: " + e.FullKey + " not set: " + ex.Message);
                    }
                    rep.Keys.Add(e.FullKey);
                    UiApply a = Effective(e);
                    if (a == UiApply.Live) rep.Live++; else if (a == UiApply.NextOrder) rep.NextOrder++; else rep.Restart++;
                    if (e.ApplyFailed && !failedBefore) rep.Failed.Add(e.FullKey);
                    foreach (var o in e.Owners)
                    {
                        List<string> l;
                        if (!changedBy.TryGetValue(o, out l)) { l = new List<string>(); changedBy[o] = l; ownerOrder.Add(o); }
                        l.Add(e.FullKey);
                    }
                }
                foreach (var o in ownerOrder)
                {
                    IUiSettingsApplied t;
                    if (!_applied.TryGetValue(o, out t)) continue;
                    try { t.SettingsApplied(changedBy[o]); }
                    catch (Exception ex) { Warn("settings: " + o + " SettingsApplied failed: " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            return rep;
        }

        // ------------------------------------------------------------------ self-test

        private sealed class Applied : IUiSettingsApplied
        {
            public readonly List<string> Calls = new List<string>();
            public void SettingsApplied(ICollection<string> keys) { Calls.Add(string.Join("+", keys.ToArray())); }
        }

        // the key-snapshot test reads every literal Bind("S", "K", ...) in the sources: the self-test binds through this helper
        private static IUiSetting<T> B<T>(UiContext.TestHost h, string section, string key, T def) { return h.Bind(section, key, def, "d"); }

        private static UiSettingMeta Meta(UiControl c, UiApply a = UiApply.Restart) { return new UiSettingMeta { Control = c, Apply = a }; }

        public static string SelfTest()
        {
            try
            {
                var warn = new List<string>(); var info = new List<string>();
                var host = new UiContext.TestHost();
                var reg = new UiSettingsRegistry(info.Add, warn.Add);

                // owner, Auto resolution, text key, section default = owner
                reg.CurrentOwner = "cap";
                var flag = reg.Track("Cap", "Enabled", "d", UiKeyClass.Basic, Meta(UiControl.Auto, UiApply.Live), B(host, "Cap", "Enabled", true));
                var num = reg.Track("Cap", "MissilePct", "d", UiKeyClass.Basic,
                    new UiSettingMeta { Control = UiControl.Number, Apply = UiApply.NextOrder, Min = 0, Max = 1, DisplayFactor = 100 }, B(host, "Cap", "MissilePct", 0.5));
                var txt = reg.Track("Cap", "Name", "d", UiKeyClass.Advanced, null, B(host, "Cap", "Name", "x"));
                var roe = reg.Track("Cap", "Roe", "d", UiKeyClass.Basic,
                    new UiSettingMeta { Control = UiControl.Choice, Options = new[] { "tight", "free", "hold" } }, B(host, "Cap", "Roe", "FREE"));
                var cols = reg.Track("Overview", "Columns", "d", UiKeyClass.Basic,
                    new UiSettingMeta { Control = UiControl.Multi, Options = new[] { "dist", "brg", "spd" }, Required = new[] { "icon", "type" } }, B(host, "Overview", "Columns", "brg,Dist,bogus"));
                var hot = reg.Track("Cap", "Hotkey", "d", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Hotkey, Apply = UiApply.Live }, B(host, "Cap", "Hotkey", "LeftAlt+P|F9"));
                reg.Track("Ui", "ConfigVersion", "d", UiKeyClass.Internal, null, B(host, "Ui", "ConfigVersion", 3));
                if (reg.All.Count != 7) return "settings: entry count " + reg.All.Count;
                // T-165: capability of a row
                if (!reg.Available(reg.All[0]) || !reg.Available(null)) return "settings: Available without capability";
                var capMeta = new UiSettingMeta { Capability = "zz" };
                var capEntry = new UiSettingEntry { Meta = capMeta };
                if (!reg.Available(capEntry)) return "settings: Available with CapabilityPresent == null";
                reg.CapabilityPresent = c => false;
                if (reg.Available(capEntry)) return "settings: Available must be false";
                reg.CapabilityPresent = c => c == "zz";
                if (!reg.Available(capEntry)) return "settings: Available must be true";
                reg.CapabilityPresent = null;
                // T-164: gates
                var gEntry = new UiSettingEntry { Meta = new UiSettingMeta { Gate = "g" } };
                if (reg.GateNote(gEntry) != null || reg.Gated(gEntry) || reg.GateNote(null) != null || reg.Gated(reg.All[0])) return "settings: gate without SetGate";
                reg.SetGate("g", () => "k");
                if (reg.GateNote(gEntry) != "k" || !reg.Gated(gEntry)) return "settings: closed gate";
                gEntry.Meta.GateSoft = true;
                if (reg.GateNote(gEntry) != "k" || reg.Gated(gEntry)) return "settings: soft gate";
                gEntry.Meta.GateSoft = false;
                reg.SetGate("g", () => null);
                if (reg.GateNote(gEntry) != null || reg.Gated(gEntry)) return "settings: open gate";
                reg.SetGate("g", () => { throw new InvalidOperationException("x"); });
                if (reg.GateNote(gEntry) != null || reg.Gated(gEntry)) return "settings: throwing gate must be open";
                reg.SetGate("g", () => "k2");
                if (reg.GateNote(gEntry) != "k2") return "settings: gate replace";
                if (reg.GateNote(new UiSettingEntry { Meta = new UiSettingMeta { Gate = "unknown" } }) != null) return "settings: unknown gate";
                if (new UiSettingMeta { Gate = "a" }.SameAs(new UiSettingMeta { Gate = "b" }) || new UiSettingMeta { GateSoft = true }.SameAs(new UiSettingMeta())) return "settings: SameAs ignores Gate";
                if (new UiSettingMeta { Capability = "a" }.SameAs(new UiSettingMeta { Capability = "b" })) return "settings: SameAs ignores Capability";
                UiSettingEntry eFlag = reg.All[0], eNum = reg.All[1], eTxt = reg.All[2], eRoe = reg.All[3], eCols = reg.All[4], eHot = reg.All[5];
                if (eFlag.Owner != "cap" || eFlag.Section != "cap" || eFlag.FullKey != "Cap.Enabled" || eFlag.TextKey != "settings.cap.enabled") return "settings: owner/section/keys";
                if (eFlag.Control != UiControl.Check || eNum.Control != UiControl.Number || eTxt.Control != UiControl.Text) return "settings: Auto resolution";
                if (eFlag.BindIndex != 0 || eHot.BindIndex != 5) return "settings: bind index";

                // Choice / Multi normalisation at start
                if (!"free".Equals(roe.Value)) return "settings: Choice case, got " + roe.Value;
                if (host.Saves != 2) return "settings: normalisation must write once per key (Roe, Columns), saves " + host.Saves;
                if (cols.Value != "icon,type,brg,dist") return "settings: Multi normalisation, got " + cols.Value;
                reg.Track("Cap", "Roe2", "d", UiKeyClass.Basic,
                    new UiSettingMeta { Control = UiControl.Choice, Options = new[] { "a", "b" } }, B(host, "Cap", "Roe2", "a"));
                var unk = B(new UiContext.TestHost(), "Cap", "Roe3", "b");
                unk.Value = "nope";
                var regU = new UiSettingsRegistry(info.Add, warn.Add);
                regU.Track("Cap", "Roe3", "d", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Options = new[] { "a", "b" } }, unk);
                if (unk.Value != "b" || !warn.Contains("config: [Cap] Roe3 'nope' unknown, default 'b'")) return "settings: unknown Choice -> default + line, got " + unk.Value + " / " + string.Join("|", warn.ToArray());
                warn.Clear();

                // TryParse: range, DisplayFactor, options
                object o; string err;
                if (!eNum.Box.TryParse("75", out o, out err) || Math.Abs((double)o - 0.75) > 1e-9) return "settings: DisplayFactor parse";
                if (eNum.Box.Format(0.25) != "25") return "settings: DisplayFactor format " + eNum.Box.Format(0.25);
                if (eNum.Box.TryParse("101", out o, out err) || err != "range") return "settings: range max";
                if (eNum.Box.TryParse("-1", out o, out err) || err != "range") return "settings: range min";
                if (eNum.Box.TryParse("abc", out o, out err) || err != "format") return "settings: number format";
                if (!eFlag.Box.TryParse("false", out o, out err) || !(o is bool) || (bool)o) return "settings: bool parse";
                if (!eRoe.Box.TryParse("HOLD", out o, out err) || (string)o != "hold") return "settings: Choice parse";
                if (eRoe.Box.TryParse("x", out o, out err) || err != "option") return "settings: Choice bad";
                if (eCols.Box.TryParse("brg,zzz", out o, out err) || err != "option") return "settings: Multi bad";
                if (!eCols.Box.TryParse("spd", out o, out err) || (string)o != "icon,type,spd") return "settings: Multi Required";
                if (eHot.Box.TryParse("Bad+Key+", out o, out err) || err != "format") return "settings: Hotkey bad";
                if (!eHot.Box.TryParse("F9|LeftAlt+O", out o, out err)) return "settings: Hotkey ok";
                var zi = new UiContext.TestHost();
                var zs = reg.Track("Z", "Auto", "d", UiKeyClass.Basic, new UiSettingMeta { Min = 1, Max = 5, ZeroIsAuto = true, Section = "Z" }, B(zi, "Z", "Auto", 3));
                if (!reg.All[reg.All.Count - 1].Box.TryParse("0", out o, out err) || (int)o != 0) return "settings: ZeroIsAuto";
                if (reg.All[reg.All.Count - 1].Box.TryParse("6", out o, out err)) return "settings: ZeroIsAuto still ranged";
                if (reg.All[reg.All.Count - 1].Box.TryParse("2.5", out o, out err) || err != "format") return "settings: int rejects fractions";
                if (zs.Value != 3) return "settings: zs";

                // double Bind: same class+meta = one entry, a second owner; different class = warning
                int before = reg.All.Count;
                reg.CurrentOwner = "air_tick";
                var flag2 = reg.Track("Cap", "Enabled", "d", UiKeyClass.Basic, Meta(UiControl.Auto, UiApply.Live), B(host, "Cap", "Enabled", true));
                if (reg.All.Count != before || warn.Count != 0 || !eFlag.Owners.Contains("air_tick")) return "settings: identical double Bind";
                reg.Track("Cap", "Enabled", "d", UiKeyClass.Advanced, Meta(UiControl.Auto, UiApply.Live), B(host, "Cap", "Enabled", true));
                if (warn.Count != 1 || !warn[0].Contains("Cap.Enabled")) return "settings: class mismatch must warn";
                reg.Track("Cap", "Enabled", "d", UiKeyClass.Basic, Meta(UiControl.Auto, UiApply.Restart), B(host, "Cap", "Enabled", true));
                if (warn.Count != 2) return "settings: meta mismatch must warn";
                warn.Clear();
                reg.CurrentOwner = "cap";

                // OnChanged / DropOwner / handler exception / HasSubscriber / PendingRestart
                if (reg.HasSubscriber(eFlag)) return "settings: no subscriber yet";
                var seen = new List<string>();
                reg.OnChanged("cap", flag, v => seen.Add("cap:" + v));
                reg.OnChanged("air_tick", flag2, v => seen.Add("air:" + v));
                reg.OnChanged("cap", flag, v => { throw new InvalidOperationException("boom"); });
                reg.OnChanged("cap", flag, v => seen.Add("cap2:" + v));
                if (!reg.HasSubscriber(eFlag)) return "settings: subscriber";
                flag.Value = false;
                if (string.Join(",", seen.ToArray()) != "cap:False,air:False,cap2:False") return "settings: handlers (exception must not stop the rest) " + string.Join(",", seen.ToArray());
                if (!eFlag.ApplyFailed || warn.Count != 1) return "settings: ApplyFailed + one log line";
                if (!reg.PendingRestart(eFlag)) return "settings: ApplyFailed must show as pending restart";
                flag.Value = false;
                if (seen.Count != 3) return "settings: Changed only on a real change";
                reg.DropOwner("air_tick");
                seen.Clear();
                flag.Value = true;
                if (string.Join(",", seen.ToArray()) != "cap:True,cap2:True") return "settings: DropOwner " + string.Join(",", seen.ToArray());
                if (reg.PendingRestart(eTxt)) return "settings: unchanged key is not pending";
                txt.Value = "y";
                if (!reg.PendingRestart(eTxt)) return "settings: changed Restart key is pending";
                txt.Value = "x";
                if (reg.PendingRestart(eTxt)) return "settings: back to the start value clears pending";
                num.Value = 0.6;
                if (reg.Effective(eNum) != UiApply.Restart || !reg.PendingRestart(eNum)) return "settings: NextOrder without a subscriber is honest Restart";
                num.Value = 0.5;

                // sections and rows
                reg.SetSectionOrder("cap", 60); reg.SetSectionOrder("overview", 30);
                var secs = string.Join(",", reg.Sections().ToArray());
                if (secs != "hotkeys,cap,Z") return "settings: sections " + secs;
                var capRows = string.Join(",", reg.InSection("cap").Select(x => x.Key).ToArray());
                if (capRows != "Enabled,MissilePct,Name,Roe,Columns,Roe2") return "settings: cap rows " + capRows;
                var hk = reg.InSection("hotkeys");
                if (hk.Count != 1 || hk[0].Key != "Hotkey") return "settings: hotkeys section";
                var u1 = reg.Sections().IndexOf("Z");
                if (u1 != reg.Sections().Count - 1) return "settings: unknown order goes last";
                if (!info.Exists(s => s.Contains("'Z'"))) return "settings: unknown order line";

                // T-239: cached key strings, Find, lists cached by Revision
                var fk = reg.Find("Cap.Roe");
                if (fk == null || !ReferenceEquals(fk.FullKey, fk.FullKey) || !ReferenceEquals(fk.TextKey, fk.TextKey) || fk.FullKey != "Cap.Roe" || fk.TextKey != "settings.cap.roe") return "settings: cached keys";
                if (reg.Find("No.Such") != null || reg.Find(null) != null) return "settings: Find of a missing key";
                string oldFull = fk.FullKey, oldKey = fk.Key;
                fk.Key = new string("Roe3".ToCharArray());
                if (fk.FullKey != "Cap.Roe3" || fk.TextKey != "settings.cap.roe3" || ReferenceEquals(fk.FullKey, oldFull)) return "settings: key rebuilt after Key was reassigned";
                fk.Key = oldKey;
                var s1 = reg.Sections(); var r1 = reg.InSection("cap");
                if (!ReferenceEquals(s1, reg.Sections()) || !ReferenceEquals(r1, reg.InSection("cap"))) return "settings: Sections / InSection are cached";
                int rev0 = reg.Revision;
                reg.CurrentOwner = "late";
                reg.Track("Late", "Key", "d", UiKeyClass.Basic, Meta(UiControl.Check, UiApply.Live), B(host, "Late", "Key", true));
                if (reg.Revision == rev0 || ReferenceEquals(s1, reg.Sections()) || reg.Sections().Count != s1.Count + 1) return "settings: Track drops the cache";

                // Apply: registry order, one Changed per key, SettingsApplied once per owner, one Save
                var target = new Applied();
                reg.SetApplied("cap", target);
                seen.Clear();
                int savesBefore = host.Saves;
                var edits = new Dictionary<string, object>
                {
                    { "Cap.MissilePct", 0.8 }, { "Cap.Enabled", false }, { "Cap.Name", "x" }, { "Cap.Hotkey", "F9" }, { "Cap.Roe", "hold" },
                };
                var rep = reg.Apply(host, edits);
                if (host.Saves - savesBefore != 1) return "settings: Apply must Save once, saves " + (host.Saves - savesBefore);
                if (string.Join(",", rep.Keys.ToArray()) != "Cap.Enabled,Cap.MissilePct,Cap.Roe,Cap.Hotkey") return "settings: Apply order " + string.Join(",", rep.Keys.ToArray());
                if (target.Calls.Count != 1 || target.Calls[0] != "Cap.Enabled+Cap.MissilePct+Cap.Roe+Cap.Hotkey") return "settings: SettingsApplied once per owner " + string.Join("|", target.Calls.ToArray());
                if (seen.Count != 0 && string.Join(",", seen.ToArray()) != "cap:False,cap2:False") return "settings: Apply Changed " + string.Join(",", seen.ToArray());
                if (rep.Live != 2 || rep.NextOrder != 1 || rep.Restart != 1) return "settings: report (IUiSettingsApplied of the owner counts as a subscriber) " + rep.Live + "/" + rep.NextOrder + "/" + rep.Restart;
                if (host.InBatch != 0) return "settings: Batch not closed";

                // a failing handler is reported
                var host2 = new UiContext.TestHost();
                var reg2 = new UiSettingsRegistry(null, null);
                reg2.CurrentOwner = "m";
                var s2 = reg2.Track("M", "K", "d", UiKeyClass.Basic, Meta(UiControl.Check, UiApply.Live), B(host2, "M", "K", true));
                reg2.OnChanged("m", s2, v => { throw new InvalidOperationException("x"); });
                var rep2 = reg2.Apply(host2, new Dictionary<string, object> { { "M.K", false } });
                if (rep2.Failed.Count != 1 || rep2.Failed[0] != "M.K") return "settings: failed list";
                return null;
            }
            catch (Exception e) { return "settings: " + e.GetType().Name + ": " + e.Message + " @ " + e.StackTrace; }
        }
    }
}
