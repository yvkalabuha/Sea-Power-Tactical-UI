using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VoiceBotBridge
{
    // T-098 (ADR-0037 section 2): pure core of the mod's Standing Orders settings. No game, no Unity, no reflection.
    // The game layer (DoctrineGame.cs) supplies IDoctrineOwners / ICellFactory; tests use fakes.

    internal enum DoctrineLevel { Global = 0, Group = 1, Unit = 2 }

    internal static class VbKeys
    {
        public const string AutoEvade = "VB_AutoEvade", VetoNeutral = "VB_VetoNeutral", AirDefenseReady = "VB_AirDefenseReady",
                            PostLaunchBreak = "VB_PostLaunchBreak", PostLaunchRetarget = "VB_PostLaunchRetarget", AutoEvadeAir = "VB_AutoEvadeAir";
        public static readonly string[] All = { AutoEvade, VetoNeutral, AirDefenseReady, PostLaunchBreak, PostLaunchRetarget, AutoEvadeAir };
        public const string IniPrefix = "TacticalDoctrine_";

        /// <summary>T-101 (ADR-0071): set at run time by the evasion module when the sub_evasion capability is present; adds the Submarine category for VB_AutoEvade.</summary>
        public static bool AutoEvadeForSubs { get; set; }

        /// <summary>IconKey of the game categories that hold the item: Ship, Submarine, Aircraft, Helicopter.</summary>
        public static string[] Categories(string key)
        {
            switch (key)
            {
                case AutoEvade: return AutoEvadeForSubs ? new[] { "Ship", "Submarine" } : new[] { "Ship" };
                case VetoNeutral: return new[] { "Ship", "Submarine", "Aircraft", "Helicopter" };
                case AirDefenseReady: return new[] { "Ship" };
                case PostLaunchBreak: return new[] { "Aircraft" };
                case PostLaunchRetarget: return new[] { "Aircraft" };
                case AutoEvadeAir: return new[] { "Aircraft" };
                default: return new string[0];
            }
        }

        /// <summary>kind4: ship|sub|air|helo.</summary>
        public static bool AppliesTo(string key, string kind4)
        {
            switch (key)
            {
                case AutoEvade: return kind4 == "ship" || (kind4 == "sub" && AutoEvadeForSubs);
                case VetoNeutral: return kind4 == "ship" || kind4 == "sub" || kind4 == "air" || kind4 == "helo";
                case AirDefenseReady: return kind4 == "ship";
                case PostLaunchBreak: return kind4 == "air";
                case PostLaunchRetarget: return kind4 == "air";
                case AutoEvadeAir: return kind4 == "air";
                default: return false;
            }
        }

        public static bool Default(string key) { return false; }

        /// <summary>auto_evade|veto_neutral|air_defense_ready -> key; post_launch (two keys) and unknown -> null.</summary>
        public static string KeyOfFlag(string flag)
        {
            switch (flag)
            {
                case "auto_evade": return AutoEvade;
                case "veto_neutral": return VetoNeutral;
                case "air_defense_ready": return AirDefenseReady;
                default: return null;
            }
        }
    }

    internal static class PostLaunchBits
    {
        public static string ToMode(bool brk, bool retarget) { return !brk ? "off" : retarget ? "retarget" : "evade"; }

        public static bool FromMode(string mode, out bool brk, out bool retarget)
        {
            brk = false; retarget = false;
            switch (mode)
            {
                case "off": return true;
                case "evade": brk = true; return true;
                case "retarget": brk = true; retarget = true; return true;
                default: return false;
            }
        }
    }

    /// <summary>Value of one key at one level for one owner. Global: IsSet is always true.</summary>
    internal interface IBoolCell { bool Value { get; set; } bool IsSet { get; set; } }
    internal sealed class PlainCell : IBoolCell { public bool Value { get; set; } public bool IsSet { get; set; } }

    internal interface IDoctrineOwners
    {
        object FormationOf(object unit);      // null = not in a group
        object TaskforceOf(object owner);     // for a unit and for a group; null = none
        string Kind4Of(object unit);          // ship|sub|air|helo|null
    }

    internal interface ICellFactory
    {
        /// <summary>Global cells are created with IsSet = true, Group/Unit with IsSet = false. parent = cell one level up (null for Global).</summary>
        IBoolCell Create(object owner, DoctrineLevel level, string key, IBoolCell parent, bool initial);
        void Relink(IBoolCell cell, IBoolCell parent);
    }

    internal sealed class PlainCellFactory : ICellFactory
    {
        public IBoolCell Create(object owner, DoctrineLevel level, string key, IBoolCell parent, bool initial)
        {
            return new PlainCell { Value = initial, IsSet = level == DoctrineLevel.Global };
        }
        public void Relink(IBoolCell cell, IBoolCell parent) { }
    }

    internal interface IFlagStore
    {
        string Name { get; }
        bool Resolve(object unit, string key);
        string Source(object unit, string key);
        /// <summary>value=null removes the override (Group/Unit); for Global null restores Default(key).</summary>
        void Set(object owner, DoctrineLevel level, string key, bool? value);
        /// <summary>null = not set at this level (Group/Unit: no cell or not IsSet). Global always has a value (cell is created lazily).</summary>
        bool? Get(object owner, DoctrineLevel level, string key);
        /// <summary>Creates lazily (for the Standing Orders items).</summary>
        IBoolCell Cell(object owner, DoctrineLevel level, string key);
        List<KeyValuePair<string, bool>> Overrides(object owner, DoctrineLevel level);
        void Reset(object owner, DoctrineLevel level);
        void ResetMission();
    }

    /// <summary>Shared logic: owner -> cells tables (weak), resolution unit -> group -> global.</summary>
    internal class FlagStoreCore : IFlagStore
    {
        private sealed class Entry { public IBoolCell Cell, Parent; }
        private sealed class Table : Dictionary<string, Entry> { }

        private readonly string _name;
        private readonly IDoctrineOwners _owners;
        private readonly ICellFactory _cells;
        private readonly Func<object, string, bool> _globalInitial;
        private ConditionalWeakTable<object, Table>[] _tables = NewTables();

        public FlagStoreCore(string name, IDoctrineOwners owners, ICellFactory cells, Func<object, string, bool> globalInitial)
        {
            _name = name; _owners = owners; _cells = cells; _globalInitial = globalInitial;
        }

        private static ConditionalWeakTable<object, Table>[] NewTables()
        {
            return new[] { new ConditionalWeakTable<object, Table>(), new ConditionalWeakTable<object, Table>(), new ConditionalWeakTable<object, Table>() };
        }

        public string Name { get { return _name; } }

        private Entry Peek(object owner, DoctrineLevel level, string key)
        {
            Table t; Entry e;
            if (owner == null || !_tables[(int)level].TryGetValue(owner, out t) || !t.TryGetValue(key, out e)) return null;
            return e;
        }

        private IBoolCell ParentFor(object owner, DoctrineLevel level, string key)
        {
            if (level == DoctrineLevel.Global) return null;
            if (level == DoctrineLevel.Unit)
            {
                var f = _owners.FormationOf(owner);
                if (f != null) return Cell(f, DoctrineLevel.Group, key);
            }
            var tf = _owners.TaskforceOf(owner);
            return tf != null ? Cell(tf, DoctrineLevel.Global, key) : null;
        }

        public IBoolCell Cell(object owner, DoctrineLevel level, string key)
        {
            if (owner == null || key == null) return null;
            var e = Peek(owner, level, key);
            if (e != null) return e.Cell;
            var parent = ParentFor(owner, level, key);
            bool initial = level == DoctrineLevel.Global ? _globalInitial(owner, key) : parent != null ? parent.Value : VbKeys.Default(key);
            var c = _cells.Create(owner, level, key, parent, initial);
            if (level == DoctrineLevel.Global) c.IsSet = true;
            _tables[(int)level].GetOrCreateValue(owner)[key] = new Entry { Cell = c, Parent = parent };
            return c;
        }

        public bool Resolve(object unit, string key)
        {
            if (unit == null || key == null) return false;
            var ue = Peek(unit, DoctrineLevel.Unit, key);
            if (ue != null)
            {
                var np = ParentFor(unit, DoctrineLevel.Unit, key);   // unit may have changed group
                if (!ReferenceEquals(np, ue.Parent)) { ue.Parent = np; _cells.Relink(ue.Cell, np); }
                if (ue.Cell.IsSet) return ue.Cell.Value;
            }
            if (!VbKeys.AppliesTo(key, _owners.Kind4Of(unit))) return false;
            var f = _owners.FormationOf(unit);
            var ge = f != null ? Peek(f, DoctrineLevel.Group, key) : null;
            if (ge != null && ge.Cell.IsSet) return ge.Cell.Value;
            var tf = _owners.TaskforceOf(unit);
            return tf != null ? Cell(tf, DoctrineLevel.Global, key).Value : VbKeys.Default(key);
        }

        public string Source(object unit, string key)
        {
            if (unit == null || key == null) return "global";
            var ue = Peek(unit, DoctrineLevel.Unit, key);
            if (ue != null && ue.Cell.IsSet) return "unit";
            if (!VbKeys.AppliesTo(key, _owners.Kind4Of(unit))) return "global";
            var f = _owners.FormationOf(unit);
            var ge = f != null ? Peek(f, DoctrineLevel.Group, key) : null;
            return ge != null && ge.Cell.IsSet ? "group" : "global";
        }

        public void Set(object owner, DoctrineLevel level, string key, bool? value)
        {
            if (owner == null || key == null) return;
            if (level == DoctrineLevel.Global)
            {
                Cell(owner, level, key).Value = value ?? VbKeys.Default(key);
                return;
            }
            if (value.HasValue)
            {
                var c = Cell(owner, level, key);
                c.Value = value.Value; c.IsSet = true;
            }
            else
            {
                var e = Peek(owner, level, key);
                if (e != null) { e.Cell.IsSet = false; if (e.Parent != null) e.Cell.Value = e.Parent.Value; }
            }
        }

        public bool? Get(object owner, DoctrineLevel level, string key)
        {
            if (level == DoctrineLevel.Global) { var g = Cell(owner, level, key); return g != null ? (bool?)g.Value : null; }
            var e = Peek(owner, level, key);
            return e != null && e.Cell.IsSet ? (bool?)e.Cell.Value : null;
        }

        public List<KeyValuePair<string, bool>> Overrides(object owner, DoctrineLevel level)
        {
            var r = new List<KeyValuePair<string, bool>>();
            foreach (var k in VbKeys.All)
            {
                var e = Peek(owner, level, k);
                if (e != null && e.Cell.IsSet) r.Add(new KeyValuePair<string, bool>(k, e.Cell.Value));
            }
            return r;
        }

        public void Reset(object owner, DoctrineLevel level)
        {
            if (owner == null) return;
            foreach (var k in VbKeys.All)
            {
                if (level == DoctrineLevel.Global) Set(owner, level, k, null);
                else Set(owner, level, k, null);
            }
        }

        public virtual void ResetMission() { }

        protected void ClearAll() { _tables = NewTables(); }
    }

    internal sealed class MemoryFlagStore : FlagStoreCore
    {
        public MemoryFlagStore(IDoctrineOwners owners)
            : base("memory", owners, new PlainCellFactory(), (tf, k) => VbKeys.Default(k)) { }

        public override void ResetMission() { ClearAll(); }
    }

    internal static class FlagProtocol
    {
        public static void ApplySetFlags(IFlagStore s, object unit, bool? autoEvade, bool? vetoNeutral, string postLaunch)
        {
            if (s == null || unit == null) return;
            if (autoEvade.HasValue) s.Set(unit, DoctrineLevel.Unit, VbKeys.AutoEvade, autoEvade);
            if (vetoNeutral.HasValue) s.Set(unit, DoctrineLevel.Unit, VbKeys.VetoNeutral, vetoNeutral);
            if (postLaunch == null) return;
            if (postLaunch == "default")
            {
                s.Set(unit, DoctrineLevel.Unit, VbKeys.PostLaunchBreak, null);
                s.Set(unit, DoctrineLevel.Unit, VbKeys.PostLaunchRetarget, null);
                return;
            }
            bool b, r;
            if (!PostLaunchBits.FromMode(postLaunch, out b, out r)) return;
            s.Set(unit, DoctrineLevel.Unit, VbKeys.PostLaunchBreak, b);
            s.Set(unit, DoctrineLevel.Unit, VbKeys.PostLaunchRetarget, r);
        }

        /// <summary>Ship and air veto share one key; different values merge with OR (defending neutrals wins), merged=true.</summary>
        public static void ApplyDefaults(IFlagStore s, object taskforce, bool? shipAutoEvade, bool? shipVeto, bool? airVeto, string airPostLaunch, out bool vetoMerged)
        {
            vetoMerged = false;
            if (s == null || taskforce == null) return;
            if (shipAutoEvade.HasValue) s.Set(taskforce, DoctrineLevel.Global, VbKeys.AutoEvade, shipAutoEvade);
            bool? veto = null;
            if (shipVeto.HasValue && airVeto.HasValue)
            {
                veto = shipVeto.Value || airVeto.Value;
                vetoMerged = shipVeto.Value != airVeto.Value;
            }
            else veto = shipVeto ?? airVeto;
            if (veto.HasValue) s.Set(taskforce, DoctrineLevel.Global, VbKeys.VetoNeutral, veto);
            bool b, r;
            if (airPostLaunch != null && PostLaunchBits.FromMode(airPostLaunch, out b, out r))
            {
                s.Set(taskforce, DoctrineLevel.Global, VbKeys.PostLaunchBreak, b);
                s.Set(taskforce, DoctrineLevel.Global, VbKeys.PostLaunchRetarget, r);
            }
        }

        public static string ResolvePostLaunch(IFlagStore s, object unit)
        {
            return PostLaunchBits.ToMode(s.Resolve(unit, VbKeys.PostLaunchBreak), s.Resolve(unit, VbKeys.PostLaunchRetarget));
        }

        private static bool G(IFlagStore s, object tf, string key) { return s.Get(tf, DoctrineLevel.Global, key) ?? VbKeys.Default(key); }

        public static string DefaultsJson(IFlagStore s, object taskforce)
        {
            bool ae = G(s, taskforce, VbKeys.AutoEvade), vn = G(s, taskforce, VbKeys.VetoNeutral);
            string pl = PostLaunchBits.ToMode(G(s, taskforce, VbKeys.PostLaunchBreak), G(s, taskforce, VbKeys.PostLaunchRetarget));
            return "{\"ship\":{\"auto_evade\":" + Json.Bool(ae) + ",\"veto_neutral\":" + Json.Bool(vn) +
                   "},\"air\":{\"post_launch\":" + Json.Str(pl) + ",\"veto_neutral\":" + Json.Bool(vn) + "}}";
        }
    }

    /// <summary>Arguments of set_doctrine / doctrine.</summary>
    internal sealed class DoctrineArgs
    {
        public DoctrineLevel Level; public string Flag; public object Value;

        /// <summary>null = ok, otherwise the bad_args text. readOnly (doctrine): only uid is required.</summary>
        public static string Parse(Dictionary<string, object> cmd, bool readOnly, out DoctrineArgs a)
        {
            a = null;
            if (cmd == null || !cmd.ContainsKey("uid") || cmd["uid"] == null) return "uid required";
            var r = new DoctrineArgs { Level = DoctrineLevel.Unit };
            object lv;
            if (cmd.TryGetValue("level", out lv) && lv != null)
            {
                string l = lv as string;
                if (l == "unit") r.Level = DoctrineLevel.Unit;
                else if (l == "group") r.Level = DoctrineLevel.Group;
                else if (l == "global") r.Level = DoctrineLevel.Global;
                else return "level: unit|group|global";
            }
            if (readOnly) { a = r; return null; }
            object fl;
            string flag = cmd.TryGetValue("flag", out fl) ? fl as string : null;
            if (flag != "auto_evade" && flag != "veto_neutral" && flag != "air_defense_ready" && flag != "post_launch")
                return "flag: auto_evade|veto_neutral|air_defense_ready|post_launch";
            r.Flag = flag;
            if (!cmd.ContainsKey("value")) return "value required (null removes the override)";
            object v = cmd["value"];
            if (v != null)
            {
                if (flag == "post_launch")
                {
                    bool b, rt;
                    if (!(v is string) || !PostLaunchBits.FromMode((string)v, out b, out rt)) return "value: off|evade|retarget or null";
                }
                else if (!(v is bool)) return "value: true|false or null";
            }
            r.Value = v;
            a = r;
            return null;
        }
    }

    internal interface IIniLike { bool Has(string section, string key); bool Read(string section, string key, bool def); void Write(string section, string key, bool value); }

    internal static class DoctrineIni
    {
        public const string TacticsSection = "Tactics";

        public static void Save(IFlagStore s, object owner, DoctrineLevel level, IIniLike ini, string section)
        {
            foreach (var kv in s.Overrides(owner, level)) ini.Write(section, VbKeys.IniPrefix + kv.Key, kv.Value);
        }

        public static void Load(IFlagStore s, object owner, DoctrineLevel level, IIniLike ini, string section)
        {
            foreach (var k in VbKeys.All)
                if (ini.Has(section, VbKeys.IniPrefix + k)) s.Set(owner, level, k, ini.Read(section, VbKeys.IniPrefix + k, VbKeys.Default(k)));
        }

        public static void SaveGlobal(IFlagStore s, object taskforce, IIniLike ini)
        {
            foreach (var k in VbKeys.All) ini.Write(TacticsSection, k, s.Get(taskforce, DoctrineLevel.Global, k) ?? VbKeys.Default(k));
        }

        public static bool ReadGlobal(IIniLike ini, string key) { return ini.Read(TacticsSection, key, VbKeys.Default(key)); }
    }

    /// <summary>Texts of our VB_* Standing Orders items: own language tables only ([TacticalDoctrine] and [Tooltip] of doctrine.ini).</summary>
    internal static class DoctrineText
    {
        /// <summary>null = not ours (the game answers itself). Uses UiText.GetOwn, never UiText.Get: Get asks the game's getText, which we intercept (recursion).</summary>
        public static string Get(string section, string key)
        {
            if (section != "Tooltip" && section != "TacticalDoctrine") return null;
            if (key == null || !key.StartsWith("VB_", StringComparison.Ordinal)) return null;
            return UiText.GetOwn(section, key);
        }
    }

    internal static class DoctrineFlagsTests
    {
        private sealed class DGroup { public object TF; }
        private sealed class DUnit { public string Kind = "ship"; public DGroup Group; public object TF; }

        private sealed class DOwners : IDoctrineOwners
        {
            public object FormationOf(object u) { var d = u as DUnit; return d != null ? d.Group : null; }
            public object TaskforceOf(object o)
            {
                var d = o as DUnit; if (d != null) return d.Group != null ? d.Group.TF : d.TF;
                var g = o as DGroup; return g != null ? g.TF : null;
            }
            public string Kind4Of(object u) { var d = u as DUnit; return d != null ? d.Kind : null; }
        }

        private sealed class FakeIni : IIniLike
        {
            public readonly Dictionary<string, bool> D = new Dictionary<string, bool>();
            public bool Has(string s, string k) { return D.ContainsKey(s + "/" + k); }
            public bool Read(string s, string k, bool def) { bool v; return D.TryGetValue(s + "/" + k, out v) ? v : def; }
            public void Write(string s, string k, bool v) { D[s + "/" + k] = v; }
        }

        public static string SelfTest()
        {
            return Keys() ?? Inheritance() ?? Protocol() ?? Args() ?? Ini() ?? Text();
        }

        private static string Keys()
        {
            if (VbKeys.All.Length != 6) return "keys: 6";
            if (VbKeys.Categories(VbKeys.AutoEvadeAir).Length != 1 || VbKeys.Categories(VbKeys.AutoEvadeAir)[0] != "Aircraft") return "categories: auto_evade_air only Aircraft";
            if (!VbKeys.AppliesTo(VbKeys.AutoEvadeAir, "air") || VbKeys.AppliesTo(VbKeys.AutoEvadeAir, "helo") || VbKeys.AppliesTo(VbKeys.AutoEvadeAir, "ship")) return "applies: auto_evade_air";
            if (VbKeys.All[5] != VbKeys.AutoEvadeAir || VbKeys.KeyOfFlag("auto_evade_air") != null) return "auto_evade_air: last key, no voice flag";
            bool subsWas = VbKeys.AutoEvadeForSubs;
            try
            {
                VbKeys.AutoEvadeForSubs = false;
                if (VbKeys.Categories(VbKeys.AutoEvade).Length != 1 || VbKeys.Categories(VbKeys.AutoEvade)[0] != "Ship") return "categories: auto_evade only Ship (sub_evasion off)";
                if (VbKeys.AppliesTo(VbKeys.AutoEvade, "sub")) return "applies: auto_evade sub with sub_evasion off";
                VbKeys.AutoEvadeForSubs = true;   // T-101: set when the sub_evasion capability is present
                if (string.Join(",", VbKeys.Categories(VbKeys.AutoEvade)) != "Ship,Submarine") return "categories: auto_evade Ship,Submarine (sub_evasion on)";
                if (!VbKeys.AppliesTo(VbKeys.AutoEvade, "sub") || VbKeys.AppliesTo(VbKeys.AutoEvade, "air")) return "applies: auto_evade sub with sub_evasion on";
            }
            finally { VbKeys.AutoEvadeForSubs = subsWas; }
            if (string.Join(",", VbKeys.Categories(VbKeys.VetoNeutral)) != "Ship,Submarine,Aircraft,Helicopter") return "categories: veto";
            if (VbKeys.Categories(VbKeys.AirDefenseReady)[0] != "Ship") return "categories: adr";
            if (VbKeys.Categories(VbKeys.PostLaunchBreak)[0] != "Aircraft" || VbKeys.Categories(VbKeys.PostLaunchRetarget)[0] != "Aircraft") return "categories: post_launch";
            if (VbKeys.Categories("x").Length != 0) return "categories: unknown";
            if (!VbKeys.AppliesTo(VbKeys.AutoEvade, "ship")) return "applies: auto_evade ship";
            if (!VbKeys.AppliesTo(VbKeys.VetoNeutral, "helo")) return "applies: veto helo";
            if (VbKeys.AppliesTo(VbKeys.PostLaunchBreak, "helo")) return "applies: break helo";
            if (!VbKeys.AppliesTo(VbKeys.PostLaunchRetarget, "air") || VbKeys.AppliesTo(VbKeys.AirDefenseReady, "air")) return "applies: air";
            if (VbKeys.AppliesTo("x", "ship") || VbKeys.AppliesTo(VbKeys.VetoNeutral, null)) return "applies: unknown";
            foreach (var k in VbKeys.All) if (VbKeys.Default(k)) return "default: false";
            if (VbKeys.KeyOfFlag("auto_evade") != VbKeys.AutoEvade || VbKeys.KeyOfFlag("veto_neutral") != VbKeys.VetoNeutral
                || VbKeys.KeyOfFlag("air_defense_ready") != VbKeys.AirDefenseReady || VbKeys.KeyOfFlag("post_launch") != null || VbKeys.KeyOfFlag("x") != null) return "KeyOfFlag";
            foreach (var m in new[] { "off", "evade", "retarget" })
            {
                bool b, r;
                if (!PostLaunchBits.FromMode(m, out b, out r) || PostLaunchBits.ToMode(b, r) != m) return "post_launch circle " + m;
            }
            if (PostLaunchBits.ToMode(false, true) != "off") return "retarget without break = off";
            bool b2, r2;
            if (PostLaunchBits.FromMode("zzz", out b2, out r2) || b2 || r2) return "FromMode bad";
            return null;
        }

        private static string Inheritance()
        {
            var tf = new object();
            var g = new DGroup { TF = tf };
            var ship = new DUnit { Kind = "ship", Group = g };
            var loner = new DUnit { Kind = "ship", TF = tf };
            var sub = new DUnit { Kind = "sub", TF = tf };
            var s = new MemoryFlagStore(new DOwners());
            const string VN = VbKeys.VetoNeutral;
            if (s.Name != "memory") return "store name";
            if (s.Resolve(ship, VN) || s.Source(ship, VN) != "global") return "default resolve";
            s.Set(tf, DoctrineLevel.Global, VN, true);
            if (!s.Resolve(ship, VN) || s.Source(ship, VN) != "global") return "global true -> unit true";
            s.Set(g, DoctrineLevel.Group, VN, false);
            if (s.Resolve(ship, VN) || s.Source(ship, VN) != "group") return "group false wins";
            if (!s.Resolve(loner, VN)) return "other unit keeps global";
            s.Set(ship, DoctrineLevel.Unit, VN, true);
            if (!s.Resolve(ship, VN) || s.Source(ship, VN) != "unit") return "unit true wins";
            s.Set(ship, DoctrineLevel.Unit, VN, null);
            if (s.Resolve(ship, VN) || s.Source(ship, VN) != "group") return "unit cleared -> group";
            ship.Group = null; ship.TF = tf;
            if (!s.Resolve(ship, VN) || s.Source(ship, VN) != "global") return "left the group -> global";
            s.Set(tf, DoctrineLevel.Global, VN, null);
            if (s.Resolve(ship, VN) || s.Get(tf, DoctrineLevel.Global, VN) != false) return "global null -> default";
            if (s.Resolve(sub, VbKeys.AutoEvade)) return "key not for the type";
            s.Set(tf, DoctrineLevel.Global, VbKeys.AutoEvade, true);
            if (s.Resolve(sub, VbKeys.AutoEvade)) return "global does not reach subs";
            s.Set(sub, DoctrineLevel.Unit, VbKeys.AutoEvade, true);
            if (!s.Resolve(sub, VbKeys.AutoEvade)) return "explicit unit override on a sub";
            var fresh = new DUnit { Kind = "ship", TF = tf };
            if (s.Get(fresh, DoctrineLevel.Unit, VN) != null || s.Overrides(fresh, DoctrineLevel.Unit).Count != 0) return "no cell for reading";
            s.Resolve(fresh, VN); s.Source(fresh, VN);
            if (s.Get(fresh, DoctrineLevel.Unit, VN) != null) return "Resolve creates no unit cell";
            s.Set(fresh, DoctrineLevel.Unit, VN, null);
            if (s.Get(fresh, DoctrineLevel.Unit, VN) != null) return "clearing a missing override";
            s.Set(g, DoctrineLevel.Group, VbKeys.PostLaunchBreak, true);
            var ov = s.Overrides(g, DoctrineLevel.Group);
            if (ov.Count != 2 || ov[0].Key != VN || ov[1].Key != VbKeys.PostLaunchBreak) return "Overrides order/content";
            s.Reset(g, DoctrineLevel.Group);
            if (s.Overrides(g, DoctrineLevel.Group).Count != 0) return "Reset group";
            s.Set(tf, DoctrineLevel.Global, VN, true); s.Reset(tf, DoctrineLevel.Global);
            if (s.Get(tf, DoctrineLevel.Global, VN) != false) return "Reset global";
            s.Set(sub, DoctrineLevel.Unit, VN, true);
            s.ResetMission();
            if (s.Get(sub, DoctrineLevel.Unit, VN) != null || s.Resolve(sub, VbKeys.AutoEvade)) return "memory ResetMission clears";
            return null;
        }

        private static string Protocol()
        {
            var tf = new object();
            var g = new DGroup { TF = tf };
            var air = new DUnit { Kind = "air", TF = tf };
            var s = new MemoryFlagStore(new DOwners());
            FlagProtocol.ApplySetFlags(s, air, null, true, "retarget");
            if (!s.Resolve(air, VbKeys.VetoNeutral) || s.Get(air, DoctrineLevel.Unit, VbKeys.PostLaunchBreak) != true || s.Get(air, DoctrineLevel.Unit, VbKeys.PostLaunchRetarget) != true) return "set_flags retarget -> both bits";
            if (FlagProtocol.ResolvePostLaunch(s, air) != "retarget") return "ResolvePostLaunch retarget";
            FlagProtocol.ApplySetFlags(s, air, null, null, "evade");
            if (FlagProtocol.ResolvePostLaunch(s, air) != "evade" || s.Get(air, DoctrineLevel.Unit, VbKeys.PostLaunchRetarget) != false) return "set_flags evade";
            FlagProtocol.ApplySetFlags(s, air, null, null, "default");
            if (s.Get(air, DoctrineLevel.Unit, VbKeys.PostLaunchBreak) != null || s.Get(air, DoctrineLevel.Unit, VbKeys.PostLaunchRetarget) != null) return "set_flags default clears both";
            if (FlagProtocol.ResolvePostLaunch(s, air) != "off") return "post_launch off by default";
            FlagProtocol.ApplySetFlags(s, air, null, null, "junk");
            if (s.Get(air, DoctrineLevel.Unit, VbKeys.PostLaunchBreak) != null) return "junk mode ignored";

            bool merged;
            FlagProtocol.ApplyDefaults(s, tf, true, true, false, "evade", out merged);
            if (!merged || s.Get(tf, DoctrineLevel.Global, VbKeys.VetoNeutral) != true) return "defaults veto OR + merged";
            if (s.Get(tf, DoctrineLevel.Global, VbKeys.AutoEvade) != true) return "defaults auto_evade";
            string j = FlagProtocol.DefaultsJson(s, tf);
            if (j != "{\"ship\":{\"auto_evade\":true,\"veto_neutral\":true},\"air\":{\"post_launch\":\"evade\",\"veto_neutral\":true}}") return "DefaultsJson: " + j;
            FlagProtocol.ApplyDefaults(s, tf, null, false, false, null, out merged);
            if (merged || s.Get(tf, DoctrineLevel.Global, VbKeys.VetoNeutral) != false) return "defaults same values not merged";
            FlagProtocol.ApplyDefaults(s, tf, null, null, true, null, out merged);
            if (merged || s.Get(tf, DoctrineLevel.Global, VbKeys.VetoNeutral) != true) return "defaults air only";
            return null;
        }

        private static Dictionary<string, object> Cmd(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static string Args()
        {
            DoctrineArgs a;
            if (DoctrineArgs.Parse(Cmd("flag", "auto_evade", "value", true), false, out a) == null) return "args: no uid";
            if (DoctrineArgs.Parse(Cmd("uid", 1.0, "level", "team", "flag", "auto_evade", "value", true), false, out a) == null) return "args: bad level";
            if (DoctrineArgs.Parse(Cmd("uid", 1.0, "flag", "zzz", "value", true), false, out a) == null) return "args: bad flag";
            if (DoctrineArgs.Parse(Cmd("uid", 1.0, "flag", "post_launch", "value", true), false, out a) == null) return "args: post_launch needs a string";
            if (DoctrineArgs.Parse(Cmd("uid", 1.0, "flag", "post_launch", "value", "zzz"), false, out a) == null) return "args: post_launch bad mode";
            if (DoctrineArgs.Parse(Cmd("uid", 1.0, "flag", "veto_neutral", "value", "evade"), false, out a) == null) return "args: veto needs bool";
            if (DoctrineArgs.Parse(Cmd("uid", 1.0, "flag", "veto_neutral"), false, out a) == null) return "args: value missing";
            string e = DoctrineArgs.Parse(Cmd("uid", 1.0, "flag", "air_defense_ready", "value", true), false, out a);
            if (e != null || a.Level != DoctrineLevel.Unit || a.Flag != "air_defense_ready" || !(a.Value is bool)) return "args: ok default level " + e;
            e = DoctrineArgs.Parse(Cmd("uid", 1.0, "level", "global", "flag", "post_launch", "value", "retarget"), false, out a);
            if (e != null || a.Level != DoctrineLevel.Global || (string)a.Value != "retarget") return "args: global post_launch " + e;
            e = DoctrineArgs.Parse(Cmd("uid", 1.0, "level", "group", "flag", "auto_evade", "value", null), false, out a);
            if (e != null || a.Level != DoctrineLevel.Group || a.Value != null) return "args: null removes " + e;
            e = DoctrineArgs.Parse(Cmd("uid", 7.0), true, out a);
            if (e != null || a.Flag != null) return "args: readOnly " + e;
            if (DoctrineArgs.Parse(Cmd("level", "unit"), true, out a) == null) return "args: readOnly needs uid";
            if (DoctrineArgs.Parse(Cmd("uid", 1.0, "level", "x"), true, out a) == null) return "args: readOnly bad level";
            return null;
        }

        private static string Ini()
        {
            var tf = new object();
            var g = new DGroup { TF = tf };
            var u = new DUnit { Kind = "ship", Group = g };
            var s = new MemoryFlagStore(new DOwners());
            var ini = new FakeIni();
            DoctrineIni.Save(s, u, DoctrineLevel.Unit, ini, "Unit1");
            if (ini.D.Count != 0) return "ini: nothing set -> nothing written";
            s.Set(u, DoctrineLevel.Unit, VbKeys.AirDefenseReady, true);
            s.Set(g, DoctrineLevel.Group, VbKeys.VetoNeutral, false);
            DoctrineIni.Save(s, u, DoctrineLevel.Unit, ini, "Unit1");
            DoctrineIni.Save(s, g, DoctrineLevel.Group, ini, "Group1");
            if (ini.D.Count != 2 || ini.D["Unit1/TacticalDoctrine_VB_AirDefenseReady"] != true || ini.D["Group1/TacticalDoctrine_VB_VetoNeutral"] != false) return "ini: save content";
            var s2 = new MemoryFlagStore(new DOwners());
            DoctrineIni.Load(s2, u, DoctrineLevel.Unit, new FakeIni(), "Unit1");
            if (s2.Overrides(u, DoctrineLevel.Unit).Count != 0) return "ini: load without keys";
            DoctrineIni.Load(s2, u, DoctrineLevel.Unit, ini, "Unit1");
            DoctrineIni.Load(s2, g, DoctrineLevel.Group, ini, "Group1");
            if (s2.Get(u, DoctrineLevel.Unit, VbKeys.AirDefenseReady) != true || s2.Get(g, DoctrineLevel.Group, VbKeys.VetoNeutral) != false) return "ini: circle";
            if (s2.Overrides(u, DoctrineLevel.Unit).Count != 1 || s2.Overrides(g, DoctrineLevel.Group).Count != 1) return "ini: load touches only present keys";
            s.Set(tf, DoctrineLevel.Global, VbKeys.AutoEvade, true);
            var gi = new FakeIni();
            DoctrineIni.SaveGlobal(s, tf, gi);
            if (gi.D.Count != 6 || gi.D["Tactics/VB_AutoEvade"] != true || gi.D["Tactics/VB_VetoNeutral"] != false) return "ini: SaveGlobal";
            if (!DoctrineIni.ReadGlobal(gi, VbKeys.AutoEvade) || DoctrineIni.ReadGlobal(new FakeIni(), VbKeys.AutoEvade)) return "ini: ReadGlobal";
            var s3 = new FlagStoreCore("doctrine", new DOwners(), new PlainCellFactory(), (t, k) => DoctrineIni.ReadGlobal(gi, k));
            if (!s3.Resolve(u, VbKeys.AutoEvade) || s3.Name != "doctrine") return "ini: global initial from usersettings";
            s3.ResetMission();
            if (!s3.Resolve(u, VbKeys.AutoEvade)) return "doctrine ResetMission keeps cells";
            return null;
        }

        private static string Text()
        {
            foreach (var k in VbKeys.All)
                foreach (var sec in new[] { "TacticalDoctrine", "Tooltip" })
                {
                    string ru, en, de;
                    using (UiText.Push("ru")) ru = DoctrineText.Get(sec, k);
                    using (UiText.Push("en")) en = DoctrineText.Get(sec, k);
                    using (UiText.Push("de")) de = DoctrineText.Get(sec, k);
                    if (string.IsNullOrEmpty(ru) || string.IsNullOrEmpty(en) || ru == en) return "text " + sec + "/" + k;
                    if (de != en) return "text lang fallback " + k;
                }
            if (DoctrineText.Get("TacticalDoctrine", "Foreign") != null || DoctrineText.Get("Other", VbKeys.AutoEvade) != null) return "text foreign";
            // The getText interception must not recurse into the game lookup (UiText.Get would call it).
            var savedLookup = UiText.GameLookup;
            try
            {
                UiText.GameLookup = (s, k) => { throw new InvalidOperationException("game lookup must not be called"); };
                using (UiText.Push("ru"))
                    if (string.IsNullOrEmpty(DoctrineText.Get("TacticalDoctrine", VbKeys.AutoEvade))) return "text without game lookup";
            }
            finally { UiText.GameLookup = savedLookup; }
            return null;
        }
    }
}
