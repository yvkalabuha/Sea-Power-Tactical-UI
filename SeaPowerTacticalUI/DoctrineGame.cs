using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-098 C (ADR-0037 section 2), T-107 stream D: the game layer of the Standing Orders settings.
    // By reflection through GameApi helpers; no patching library, no engine and no plugin-framework references, so the offline selfcheck-ui can compile it.
    // The patch handlers live in PatchesDoctrine.cs and only call the Doctrine* methods below (they never throw).
    internal sealed partial class GameApi
    {
        /// <summary>Active store of the five VB_* settings: "doctrine" (cells = the game's own toggle view models) or "memory". Null until EnsureFlagStore.</summary>
        internal IFlagStore FlagStore;

        /// <summary>Config [StandingOrders] ForceFallback (debug): keep the flags in memory even when the game has Standing Orders.</summary>
        internal bool DoctrineForceFallback = false;

        internal bool StandingOrdersOn { get { return Capabilities.Contains("standing_orders") && !DoctrineForceFallback; } }

        private bool _doctrineStoreReady;

        // ---------- kinds and owners ----------

        /// <summary>ship | sub | air | helo by the type chain of the game object (Submarine and Helicopter are told apart from Vessel / Aircraft). null for anything else.</summary>
        internal string UnitKind4(object u)
        {
            for (var t = u == null ? null : u.GetType(); t != null; t = t.BaseType)
            {
                switch (t.Name)
                {
                    case "Submarine": return "sub";
                    case "Helicopter": return "helo";
                    case "Aircraft": return "air";
                    case "Vessel": return "ship";
                }
            }
            return null;
        }

        private object PlayerTaskforce() { return GetStatic(CoreSpecs.PlayerTaskforce); }

        private static string IconOfKind(string kind4)
        {
            switch (kind4)
            {
                case "ship": return "Ship";
                case "sub": return "Submarine";
                case "air": return "Aircraft";
                case "helo": return "Helicopter";
                default: return null;
            }
        }

        internal sealed class GameDoctrineOwners : IDoctrineOwners
        {
            private readonly GameApi _a;
            public GameDoctrineOwners(GameApi a) { _a = a; }
            public object FormationOf(object unit) { return unit == null ? null : _a.Get(unit, DoctrineSpecs.ObjFormation); }
            public object TaskforceOf(object owner)
            {
                if (owner == null) return null;
                string n = owner.GetType().Name;
                if (n == "Taskforce") return owner;
                if (n == "UnitFormation") return _a.Get(owner, DoctrineSpecs.FormationTaskforce);
                return _a.RxGet(owner, DoctrineSpecs.ObjTaskforce) ?? _a.Get(owner, DoctrineSpecs.FormationTaskforce);
            }
            public string Kind4Of(object unit) { return _a.UnitKind4(unit); }
        }

        // ---------- cells = the game's own toggle view models ----------

        internal sealed class ToggleCell : IBoolCell
        {
            public readonly object Vm;
            private readonly GameApi _a;
            public ToggleCell(GameApi a, object vm) { _a = a; Vm = vm; }
            public bool Value { get { return (bool)_a.Get(Vm, DoctrineSpecs.TglValue); } set { _a.Set(Vm, DoctrineSpecs.TglValue, value); } }
            public bool IsSet { get { return (bool)_a.Get(Vm, DoctrineSpecs.TglIsSet); } set { _a.Set(Vm, DoctrineSpecs.TglIsSet, value); } }
            public bool ResolvedValue { get { return (bool)_a.Get(Vm, DoctrineSpecs.TglResolved); } }
            public object ParentVm { get { return _a.Get(Vm, DoctrineSpecs.TglParent); } set { _a.Set(Vm, DoctrineSpecs.TglParent, value); } }
        }

        internal sealed class ToggleCellFactory : ICellFactory
        {
            private readonly GameApi _a;
            public ToggleCellFactory(GameApi a) { _a = a; }

            public IBoolCell Create(object owner, DoctrineLevel level, string key, IBoolCell parent, bool initial)
            {
                var p = parent as ToggleCell;
                Func<bool> visible = () => { try { return _a.DoctrineItemVisible(owner, level, key); } catch { return true; } };
                var vm = _a.New(DoctrineSpecs.TglCtor, p == null ? null : p.Vm, key, key, initial, level == DoctrineLevel.Global ? (bool?)true : (bool?)false, visible);
                return new ToggleCell(_a, vm);
            }

            public void Relink(IBoolCell cell, IBoolCell parent)
            {
                var c = cell as ToggleCell; var p = parent as ToggleCell;
                if (c != null) c.ParentVm = p == null ? null : p.Vm;
            }
        }

        /// <summary>Visibility of one of our items: global always; unit - the key applies to its kind; group - to some unit of the group; Retarget only while Break is on.</summary>
        internal bool DoctrineItemVisible(object owner, DoctrineLevel level, string key)
        {
            if (level == DoctrineLevel.Unit)
            {
                if (!VbKeys.AppliesTo(key, UnitKind4(owner))) return false;
            }
            else if (level == DoctrineLevel.Group)
            {
                bool any = false;
                var st = Get(owner, DoctrineSpecs.FormationStations) as IEnumerable;
                if (st != null)
                    foreach (var s in st) { if (VbKeys.AppliesTo(key, UnitKind4(Get(s, DoctrineSpecs.StationUnit)))) { any = true; break; } }
                if (!any) return false;
            }
            if (key == VbKeys.AutoEvadeAir && !Capabilities.Contains("air_evasion")) return false;   // T-100: no item without the module
            if (key == VbKeys.PostLaunchRetarget && FlagStore != null)
            {
                var brk = FlagStore.Cell(owner, level, VbKeys.PostLaunchBreak) as ToggleCell;
                if (brk != null && !brk.ResolvedValue) return false;
            }
            return true;
        }

        // ---------- ini ----------

        internal sealed class IniAdapter : IIniLike
        {
            private readonly GameApi _a;
            private readonly object _ini;
            private readonly bool _has, _read, _write;
            public IniAdapter(GameApi a, object ini)
            {
                _a = a; _ini = ini;
                _has = a.Present(DoctrineSpecs.IniHas); _read = a.Present(DoctrineSpecs.IniRead); _write = a.Present(DoctrineSpecs.IniWrite);
            }
            public bool Has(string section, string key) { return _has && (bool)_a.Call(_ini, DoctrineSpecs.IniHas, section, key); }
            public bool Read(string section, string key, bool def) { return !_read ? def : (bool)_a.Call(_ini, DoctrineSpecs.IniRead, section, key, def, false); }
            public void Write(string section, string key, bool value) { if (_write) _a.Call(_ini, DoctrineSpecs.IniWrite, section, key, value); }
        }

        private object OptionsIni()
        {
            var om = Singleton(DoctrineSpecs.OptionsType);
            return om == null ? null : Get(om, DoctrineSpecs.OptionsIni);
        }

        // ---------- store ----------

        /// <summary>Picks the store once, after SelfCheck and patching: Standing Orders cells, or memory (the five settings then live in the "Modes" fallback submenu).</summary>
        internal void EnsureFlagStore()
        {
            if (_doctrineStoreReady) return;
            _doctrineStoreReady = true;
            var owners = new GameDoctrineOwners(this);
            if (StandingOrdersOn)
            {
                try
                {
                    FlagStore = new FlagStoreCore("doctrine", owners, new ToggleCellFactory(this), GlobalInitial);
                    Log("[Doctrine] store=doctrine (Standing Orders)");
                    return;
                }
                catch (Exception e) { Log("[Doctrine] WARN doctrine store failed: " + e.GetType().Name + ": " + e.Message); Capabilities.Remove("standing_orders"); }
            }
            FlagStore = new MemoryFlagStore(owners);
            Log("[Doctrine] store=memory (" + (DoctrineForceFallback ? "ForceFallback" : "no standing_orders capability") + "): settings are kept in memory only");
        }

        private bool GlobalInitial(object taskforce, string key)
        {
            try
            {
                var ini = OptionsIni();
                if (ini == null || !ReferenceEquals(taskforce, PlayerTaskforce())) return VbKeys.Default(key);
                return DoctrineIni.ReadGlobal(new IniAdapter(this, ini), key);
            }
            catch { return VbKeys.Default(key); }
        }

        internal bool ResolveAirDefenseReady(object unit)
        {
            try { return FlagStore != null && unit != null && FlagStore.Resolve(unit, VbKeys.AirDefenseReady); }
            catch { return false; }
        }

        // ---------- postfix bodies (PatchesDoctrine.cs calls these; they never throw) ----------

        private static DoctrineLevel LevelOfOwner(object owner)
        {
            string n = owner == null ? null : owner.GetType().Name;
            return n == "Taskforce" ? DoctrineLevel.Global : n == "UnitFormation" ? DoctrineLevel.Group : DoctrineLevel.Unit;
        }

        private static readonly string[] CategoryOrder = { "Ship", "Submarine", "Aircraft", "Helicopter" };

        /// <summary>
        /// Adds our toggles to the tab of the given level. The category is the first of VbKeys.Categories(key) that matches the owner's kind
        /// (a unit or a group member), for the global tab the first listed one; so an item never shows twice. Idempotent.
        /// </summary>
        internal void DoctrineAppend(object viewModel, DoctrineLevel tab)
        {
            try
            {
                if (!StandingOrdersOn) return;   // fallback (patch target missing / ForceFallback): the menu "Modes" submenu carries the items, no duplicates
                EnsureFlagStore();
                var store = FlagStore;
                if (store == null || viewModel == null) return;
                object owner;
                string header;
                if (tab == DoctrineLevel.Global) { owner = Get(viewModel, DoctrineSpecs.VmTaskforce); header = "Header_GlobalDoctrine"; }
                else if (tab == DoctrineLevel.Group) { owner = Get(Get(viewModel, DoctrineSpecs.VmSelectedObject), DoctrineSpecs.ObjFormation); header = "Header_FormationDoctrine"; }
                else { owner = Get(viewModel, DoctrineSpecs.VmSelectedObject); header = "Header_UnitDoctrine"; }
                if (owner == null) return;
                var tabs = Get(viewModel, DoctrineSpecs.VmTabs) as IEnumerable;
                if (tabs == null) return;
                object tabVm = null;
                foreach (var t in tabs) { if (Get(t, DoctrineSpecs.TabHeaderKey) as string == header) { tabVm = t; break; } }
                var cats = tabVm == null ? null : Get(tabVm, DoctrineSpecs.TabItems) as IEnumerable;
                if (cats == null) return;
                var byIcon = new Dictionary<string, object>();
                foreach (var c in cats) { var ic = Get(c, DoctrineSpecs.CatIconKey) as string; if (ic != null && !byIcon.ContainsKey(ic)) byIcon[ic] = c; }
                var kinds = KindsOf(owner, tab);
                foreach (var key in VbKeys.All)
                {
                    if (key == VbKeys.AutoEvadeAir && !Capabilities.Contains("air_evasion")) continue;   // T-100
                    if (tab != DoctrineLevel.Global && !AnyApplies(key, kinds)) continue;
                    string icon = PickCategory(key, kinds);
                    object cat;
                    if (icon == null || !byIcon.TryGetValue(icon, out cat)) continue;
                    if (tab == DoctrineLevel.Unit) store.Resolve(owner, key);   // relinks the parent when the unit changed group
                    var cell = store.Cell(owner, tab, key) as ToggleCell;
                    var items = Get(cat, DoctrineSpecs.CatItems) as IList;
                    if (cell == null || items == null) continue;
                    bool present = false;
                    foreach (var it in items) { if (ReferenceEquals(it, cell.Vm)) { present = true; break; } }
                    if (!present) items.Add(cell.Vm);
                }
            }
            catch (Exception e) { try { Log("[Doctrine] append failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        private List<string> KindsOf(object owner, DoctrineLevel tab)
        {
            var r = new List<string>();
            if (tab == DoctrineLevel.Unit) { r.Add(UnitKind4(owner)); return r; }
            if (tab == DoctrineLevel.Group)
            {
                var st = Get(owner, DoctrineSpecs.FormationStations) as IEnumerable;
                if (st != null) foreach (var s in st) { var k = UnitKind4(Get(s, DoctrineSpecs.StationUnit)); if (k != null && !r.Contains(k)) r.Add(k); }
            }
            return r;
        }

        private static bool AnyApplies(string key, List<string> kinds)
        {
            foreach (var k in kinds) if (VbKeys.AppliesTo(key, k)) return true;
            return false;
        }

        private static string PickCategory(string key, List<string> kinds)
        {
            var cats = VbKeys.Categories(key);
            foreach (var k in kinds)
            {
                var icon = IconOfKind(k);
                if (icon != null && Array.IndexOf(cats, icon) >= 0 && VbKeys.AppliesTo(key, k)) return icon;
            }
            return cats.Length > 0 ? cats[0] : null;
        }

        private object OwnerOfDoctrine(object doctrine, out DoctrineLevel level)
        {
            var owner = Get(doctrine, DoctrineSpecs.TdOwner);
            level = LevelOfOwner(owner);
            return owner;
        }

        /// <summary>Postfix of SaveOverridesToFile(ini, section): our overrides of this unit or group, keys TacticalDoctrine_VB_*. The global level is not part of a save.</summary>
        internal void DoctrineSaved(object doctrine, object ini, string section)
        {
            try
            {
                if (FlagStore == null || ini == null || string.IsNullOrEmpty(section)) return;
                DoctrineLevel lv; var owner = OwnerOfDoctrine(doctrine, out lv);
                if (owner == null || lv == DoctrineLevel.Global) return;
                DoctrineIni.Save(FlagStore, owner, lv, new IniAdapter(this, ini), section);
            }
            catch (Exception e) { try { Log("[Doctrine] save failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        internal void DoctrineLoaded(object doctrine, object ini, string section)
        {
            try
            {
                EnsureFlagStore();
                if (FlagStore == null || ini == null || string.IsNullOrEmpty(section)) return;
                DoctrineLevel lv; var owner = OwnerOfDoctrine(doctrine, out lv);
                if (owner == null || lv == DoctrineLevel.Global) return;
                DoctrineIni.Load(FlagStore, owner, lv, new IniAdapter(this, ini), section);
            }
            catch (Exception e) { try { Log("[Doctrine] load failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        /// <summary>Postfix of SaveGlobalDoctrineToOptions: result=false (not the player's taskforce / no ini) writes nothing.</summary>
        internal void DoctrineGlobalSaved(object doctrine, bool result)
        {
            try
            {
                if (!result || FlagStore == null) return;
                DoctrineLevel lv; var owner = OwnerOfDoctrine(doctrine, out lv);
                var ini = OptionsIni();
                if (owner == null || lv != DoctrineLevel.Global || ini == null) return;
                DoctrineIni.SaveGlobal(FlagStore, owner, new IniAdapter(this, ini));
                var om = Singleton(DoctrineSpecs.OptionsType);
                if (om != null) Call(om, DoctrineSpecs.OptionsWrite);
            }
            catch (Exception e) { try { Log("[Doctrine] save global failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        internal void DoctrineReset(object doctrine)
        {
            try
            {
                if (FlagStore == null) return;
                DoctrineLevel lv; var owner = OwnerOfDoctrine(doctrine, out lv);
                if (owner != null) FlagStore.Reset(owner, lv);
            }
            catch (Exception e) { try { Log("[Doctrine] reset failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        // ---------- labels (language tables: UiText, doctrine.ini) ----------

        /// <summary>Game language code as is ("en", "ru", "de", ...); UiText normalizes it and falls back to en when the language has no table.</summary>
        private string DoctrineLang()
        {
            try
            {
                var h = Singleton(CoreSpecs.LanguageHandler);
                return h == null ? null : Get(h, TextSpecs.Language) as string;
            }
            catch { return null; }
        }

        /// <summary>Prefix body for getText(section, key): true = result filled, the game's own lookup is skipped. Only VB_* keys of two sections.</summary>
        internal static bool DoctrineTextOverride(string section, string key, ref string result)
        {
            if (key == null || key.Length < 4 || key[0] != 'V' || key[1] != 'B' || key[2] != '_') return false;
            try
            {
                var text = DoctrineText.Get(section, key);
                if (text == null) return false;
                result = text;
                return true;
            }
            catch { return false; }
        }

        // ---------- "air defense ready" (ADR-0037 section 2 points 1-5) ----------

        private readonly ControlMemory _adrMem = new ControlMemory();
        private readonly Dictionary<object, AdrUnit> _adrUnits = new Dictionary<object, AdrUnit>(new RefEq());
        private double _adrNext;

        /// <summary>1 Hz, own ships only: one-shot apply / restore through the usual commands; never throws.</summary>
        public void PumpDoctrine(double realNow)
        {
            try
            {
                if (FlagStore == null || !Capabilities.Contains("air_defense_ready") || !Frame.InMission) return;
                if (realNow < _adrNext) return;
                _adrNext = realNow + 1.0;
                TacticsEnsureMission();
                var seen = new HashSet<object>();
                foreach (var u in OwnUnits())
                {
                    if (UnitKind4(u) != "ship" || !IsAlive(u)) continue;
                    seen.Add(u);
                    AdrUnit st;
                    if (!_adrUnits.TryGetValue(u, out st)) { st = new AdrUnit(); _adrUnits[u] = st; }
                    bool want = FlagStore.Resolve(u, VbKeys.AirDefenseReady);
                    string act = AirDefenseReadyRules.OnTick(st, want, Get(u, DoctrineSpecs.ObjFormation));
                    if (act == "apply") AdrApply(u);
                    else if (act == "restore") AdrRestore(u);
                }
                var gone = new List<object>();
                foreach (var k in _adrUnits.Keys) if (!seen.Contains(k)) gone.Add(k);
                foreach (var k in gone) _adrUnits.Remove(k);
            }
            catch (Exception e) { try { Log("[Doctrine] pump failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        private static string StepsText(List<AdrStep> plan)
        {
            var parts = new List<string>();
            foreach (var s in plan) parts.Add(s.Field + "=" + s.Value);
            return string.Join(",", parts.ToArray());
        }

        private bool AdrExec(object u, AdrStep s)
        {
            var c = new Dictionary<string, object> { { "uid", (double)UidOf(u) } };
            if (s.Field == AirDefenseReadyRules.Emcon) { c["cmd"] = "set_emcon"; c["on"] = s.Value == "true"; }
            else if (s.Field == AirDefenseReadyRules.AirRadar) { c["cmd"] = "set_radar"; c["kind"] = "air"; c["on"] = s.Value == "true"; }
            else { c["cmd"] = "set_roe"; c["status"] = s.Value; c["confirm"] = true; }
            return Execute(c).Contains("\"ok\":true");
        }

        private void AdrApply(object u)
        {
            string roe = WeaponStatusName(u);
            var plan = AirDefenseReadyRules.ApplyPlan(FieldValue(u, AirDefenseReadyRules.Emcon), FieldValue(u, AirDefenseReadyRules.AirRadar), roe);
            if (plan.Count == 0) { Log("[Doctrine] adr apply uid=" + UidOf(u) + " steps=none"); return; }
            AirDefenseReadyRules.Remember(_adrMem, u, plan, k => FieldValue(u, k), roe);
            var done = new List<AdrStep>();
            foreach (var s in plan) if (AdrExec(u, s)) done.Add(s);
            AirDefenseReadyRules.NoteApplied(_adrMem, u, done);
            Log("[Doctrine] adr apply uid=" + UidOf(u) + " steps=" + StepsText(plan) + " ok=" + done.Count + "/" + plan.Count);
        }

        private void AdrRestore(object u)
        {
            var entry = _adrMem.Peek(u);
            var plan = AirDefenseReadyRules.RestorePlan(entry, k => FieldValue(u, k), WeaponStatusName(u));
            int ok = 0;
            foreach (var s in plan) if (AdrExec(u, s)) ok++;
            _adrMem.Take(u);
            Log("[Doctrine] adr restore uid=" + UidOf(u) + " steps=" + (plan.Count == 0 ? "none" : StepsText(plan)) + " ok=" + ok + "/" + plan.Count);
        }

        /// <summary>T-130: the mission was left; no game calls (the unit objects are gone).</summary>
        internal void MissionLeftDoctrine() { DoctrineMissionReset(); }

        private void DoctrineMissionReset()
        {
            _adrMem.Clear();
            _adrUnits.Clear();
            _adrNext = 0;
            if (FlagStore != null) FlagStore.ResetMission();
        }

        // ---------- offline smoke (selfcheck-ui) ----------

        /// <summary>
        /// Game classes, no game: our cells are real TacticalDoctrineToggleViewModel objects; the view model's ResolvedValue must equal
        /// FlagStoreCore.Resolve for every global / group / unit combination, and Relink must follow a change of group. null = ok.
        /// </summary>
        internal string DoctrineSelfSmoke()
        {
            try
            {
                if (!Present(DoctrineSpecs.TglCtor)) return "toggle ctor not found";
                var owners = new SmokeOwners();
                var store = new FlagStoreCore("doctrine", owners, new ToggleCellFactory(this), (o, k) => false);
                var tf = owners.TF; var g1 = new object(); var g2 = new object();
                var unit = new SmokeUnit { Group = g1 };
                owners.Units[unit] = true; owners.Groups[g1] = true; owners.Groups[g2] = true;
                string key = VbKeys.AutoEvade;
                bool?[] vals = { null, true, false };
                foreach (var gl in new[] { false, true })
                    foreach (var gv in vals)
                        foreach (var uv in vals)
                        {
                            store.Set(tf, DoctrineLevel.Global, key, gl);
                            store.Set(g1, DoctrineLevel.Group, key, gv);
                            store.Set(unit, DoctrineLevel.Unit, key, uv);
                            bool want = store.Resolve(unit, key);
                            var cell = (ToggleCell)store.Cell(unit, DoctrineLevel.Unit, key);
                            if (cell.ResolvedValue != want) return "ResolvedValue " + cell.ResolvedValue + " != Resolve " + want + " (global=" + gl + " group=" + gv + " unit=" + uv + ")";
                            if (cell.IsSet != uv.HasValue) return "IsSet mismatch for unit=" + uv;
                        }
                // the unit moves to another group: Resolve relinks ParentSetting
                store.Set(g2, DoctrineLevel.Group, key, true);
                store.Set(unit, DoctrineLevel.Unit, key, null);
                var uc = (ToggleCell)store.Cell(unit, DoctrineLevel.Unit, key);
                var before = uc.ParentVm;
                unit.Group = g2;
                bool r = store.Resolve(unit, key);
                if (ReferenceEquals(uc.ParentVm, before)) return "Relink did not change ParentSetting";
                if (!r || !uc.ResolvedValue) return "unit in group 2 must inherit true";
                return null;
            }
            catch (Exception e) { return e.GetType().Name + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message); }
        }

        private sealed class SmokeUnit { public object Group; }

        private sealed class SmokeOwners : IDoctrineOwners
        {
            public readonly object TF = new object();
            public readonly Dictionary<object, bool> Units = new Dictionary<object, bool>(), Groups = new Dictionary<object, bool>();
            public object FormationOf(object unit) { var u = unit as SmokeUnit; return u == null ? null : u.Group; }
            public object TaskforceOf(object owner) { return TF; }
            public string Kind4Of(object unit) { return unit is SmokeUnit ? "ship" : null; }
        }
    }
}
