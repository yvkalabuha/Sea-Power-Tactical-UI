using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-104 (ADR-0037 2a): two checkable items "Overview" and "ASW" in the map window's hamburger menu, right after "World View: Open".
    /// The Noesis tree is walked by reflection inside the main map window only; the World View item is recognised by its Header ==
    /// WorldViewButtonName of the window's DataContext (the menu items' own DataContext is the window itself) or by the path of its
    /// IsChecked binding (DataContext.WorldViewOpen). Items are also inserted when the hamburger submenu opens (the header can be
    /// empty before that). Failed tries back off 3..60 s and after MaxTries the layer fallback takes over.
    /// Insertion is idempotent (items carry Tag "VB_*"), one attempt per menu root. If the capability is missing, ForceFallback is set
    /// or the insertion throws, the same switches appear as layers in "Map layers" (GameApi.PumpWindowLayer). Never both.
    /// Check mark state follows the window (source of truth) through MapMenuSync, without echo.
    /// </summary>
    internal sealed class MapMenuWindows
    {
        internal const string TagOverview = "VB_OVERVIEW", TagAsw = "VB_ASW", TagCap = "VB_CAP", TagSettings = "VB_SETTINGS";
        internal const string LayerOverview = "VOICEBOT_OVERVIEW", LayerAsw = "VOICEBOT_ASW", LayerCap = "VOICEBOT_CAP", LayerSettings = "VOICEBOT_SETTINGS";
        // T-228: session checks of the two map legends (appended after Settings, layer numbers 205 / 206)
        internal const string TagLegendRings = "VB_LEGEND_RINGS", TagLegendRadar = "VB_LEGEND_RADAR";
        internal const string LayerLegendRings = "VOICEBOT_LEGEND_RINGS", LayerLegendRadar = "VOICEBOT_LEGEND_RADAR";

        private sealed class Entry
        {
            public string Tag, Key;
            public Func<bool> IsOpen;
            public Action<bool> SetOpen;
            public WeakReference Item = new WeakReference(null);
            public MapMenuSync Sync;
            public WindowLayerSpec Layer;
            // T-228: an item that can be inactive (the permanent key is off); null IsEnabled = always active
            public Func<bool> IsEnabled;
            public string HintKey, OffHintKey;
            public bool? EnabledShown;
        }

        private readonly GameApi _api;
        private readonly List<Entry> _entries = new List<Entry>();
        // T-143: cache of the menu we inserted into (hamburger with World View); alive = our items are members of its Items
        private object _menu, _worldView;   // T-262: strong refs (Noesis proxies died on GC every ~5 s); dropped by ClearCache on mission exit / stale scan
        private int _menuKey;
        private string _buttonName;
        private readonly MapMenuLogGate _logGate = new MapMenuLogGate();
        private readonly HashSet<int> _failedRoots = new HashSet<int>();
        private Type _tMenuItem;
        private MethodInfo _mCount, _mChild;
        private double _nextScan, _nextCheck;
        private string _mode = "pending";   // pending | menu | fallback
        private int _failures;
        private string _lastReason;
        private bool _wasInMission, _notFoundLogged;
        private readonly HashSet<int> _hooked = new HashSet<int>();      // hamburger items with a SubmenuOpened handler
        private readonly List<WeakReference> _polled = new List<WeakReference>();   // hamburgers without the event: IsSubmenuOpen is polled
        private readonly List<bool> _polledOpen = new List<bool>();
        private double _nextPoll;

        /// <summary>[Ui] ForceMapMenuFallback: skip the Noesis insertion and use the map layers.</summary>
        internal bool ForceFallback;
        internal bool Debug;
        /// <summary>[Ui] MapMenuMaxTries: failed insertion tries before the layer fallback (bound by the integrator).</summary>
        internal int MaxTries = 6;
        internal string Mode { get { return _mode; } }

        public MapMenuWindows(GameApi api, Func<bool> aswOpen, Action<bool> aswSet, Func<bool> capOpen = null, Action<bool> capSet = null,
                              Func<bool> settingsOpen = null, Action<bool> settingsSet = null)
        {
            _api = api;
            _entries.Add(new Entry
            {
                Tag = TagOverview, Key = "mapmenu.overview",
                IsOpen = () => _api.OverviewOn, SetOpen = on => _api.SetOverview(on, "map_menu"),
                Layer = new WindowLayerSpec { Name = LayerOverview, Number = 201 }
            });
            if (aswOpen != null && aswSet != null)
                _entries.Add(new Entry
                {
                    Tag = TagAsw, Key = "mapmenu.asw",
                    IsOpen = aswOpen, SetOpen = aswSet,
                    Layer = new WindowLayerSpec { Name = LayerAsw, Number = 202 }
                });
            if (capOpen != null && capSet != null)   // v0.18 (T-112): third item
                _entries.Add(new Entry
                {
                    Tag = TagCap, Key = "mapmenu.cap",
                    IsOpen = capOpen, SetOpen = capSet,
                    Layer = new WindowLayerSpec { Name = LayerCap, Number = 203 }
                });
            if (settingsOpen != null && settingsSet != null)   // T-144: fourth item
                _entries.Add(new Entry
                {
                    Tag = TagSettings, Key = "mapmenu.settings",
                    IsOpen = settingsOpen, SetOpen = settingsSet,
                    Layer = new WindowLayerSpec { Name = LayerSettings, Number = 204 }
                });
            foreach (var e in _entries)
            {
                e.Sync = new MapMenuSync(false);
                e.Layer.IsOpen = e.IsOpen; e.Layer.SetOpen = e.SetOpen;
            }
        }

        /// <summary>T-228: appends a check item after Settings. Call before the first Update. enabled == null: always active.
        /// An inactive item is grey, carries the suffix mapmenu.legend.off_suffix and the tooltip offHintKey.</summary>
        public void AddToggle(string tag, string key, string hintKey, string offHintKey, Func<bool> isOn, Action<bool> set, Func<bool> enabled, int layerNumber)
        {
            if (isOn == null || set == null) return;
            var e = new Entry
            {
                Tag = tag, Key = key, HintKey = hintKey, OffHintKey = offHintKey,
                IsOpen = isOn, SetOpen = v => set(v), IsEnabled = enabled,
                Layer = new WindowLayerSpec { Name = tag == TagLegendRings ? LayerLegendRings : LayerLegendRadar, Number = layerNumber },
                Sync = new MapMenuSync(false)
            };
            e.Layer.IsOpen = e.IsOpen; e.Layer.SetOpen = e.SetOpen;
            _entries.Add(e);
        }

        private string HeaderOf(Entry e)
        {
            string h = UiText.Get(UiText.DefaultSection, e.Key);
            if (e.IsEnabled != null && !e.IsEnabled()) h += " " + UiText.Get(UiText.DefaultSection, "mapmenu.legend.off_suffix");   // the ini parser trims, so the space is added here
            return h;
        }

        /// <summary>Applies IsEnabled, the tooltip and the caption suffix of an item (T-228). Members are optional (Aux): a missing one is skipped.</summary>
        private void ApplyEnabled(Entry e, object item, bool enabled)
        {
            _api.Set(item, MapMenuSpecs.ItemIsEnabled, enabled);
            string hintKey = enabled ? e.HintKey : e.OffHintKey;
            if (hintKey != null)
            {
                _api.Set(item, MapMenuSpecs.ItemToolTip, UiText.Get(UiText.DefaultSection, hintKey));
                if (_api.Present(MapMenuSpecs.ShowOnDisabled)) { try { _api.CallStatic(MapMenuSpecs.ShowOnDisabled, item, true); } catch { } }
            }
            _api.Set(item, MapMenuSpecs.ItemHeader, HeaderOf(e));
        }

        /// <summary>Plugin.Update: every frame, cheap. Wires the menu, falls back to layers, keeps the check marks in step.</summary>
        public void Update()
        {
            try
            {
                if (_rev != UiText.Revision) RefreshHeaders();
                if (!_api.Capabilities.Contains("map_menu_windows") || ForceFallback) { UseFallback(ForceFallback ? "ForceMapMenuFallback" : "capability map_menu_windows missing"); }
                if (_mode == "fallback") { foreach (var e in _entries) _api.PumpWindowLayer(e.Layer); return; }
                double now = Time.unscaledTime;
                if (now >= _nextCheck)
                {
                    _nextCheck = now + 0.5;
                    EnsureInserted(now, false);
                }
                PollSubmenus(now);
                int sc, ri, ro;
                if (_logGate.SummaryDue(now, out sc, out ri, out ro)) _api.Log("map menu: last 60 s scans=" + sc + " reinserts=" + ri + " roots=" + ro);
                foreach (var e in _entries) Pull(e);
            }
            catch (Exception ex)
            {
                UseFallback("update failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private int _rev = -1;

        /// <summary>The game language changed (UiText.Revision): the item captions in the map menu are set again.</summary>
        private void RefreshHeaders()
        {
            _rev = UiText.Revision;
            foreach (var e in _entries)
            {
                var it = e.Item.Target;
                if (it == null) continue;
                try { Put(it, MapMenuSpecs.ItemHeader, HeaderOf(e)); if (e.IsEnabled != null) e.EnabledShown = null; } catch { }
            }
        }

        private void UseFallback(string why)
        {
            if (_mode == "fallback") return;
            _mode = "fallback";
            _api.Log("map menu: layer fallback (" + why + ")");
            // a half-inserted menu would show the windows twice
            foreach (var e in _entries) { var it = e.Item.Target; if (it != null) { try { RemoveItem(it); } catch { } e.Item.Target = null; } }
        }

        // ---------------------------------------------------------------- sync

        private void Pull(Entry e)
        {
            var item = e.Item.Target;
            if (item == null) return;
            if (e.IsEnabled != null)
            {
                bool en = e.IsEnabled();
                if (e.EnabledShown != en) { e.EnabledShown = en; ApplyEnabled(e, item, en); }
            }
            bool? v = e.Sync.Pull(e.IsOpen());
            if (!v.HasValue) return;
            e.Sync.Begin(v.Value);
            try { Put(item, MapMenuSpecs.ItemIsChecked, v.Value); }
            catch (Exception ex) { _api.Log("map menu: check mark failed: " + ex.GetType().Name); }
            finally { e.Sync.End(); }
        }

        private void OnItemChanged(Entry e, object sender)
        {
            try
            {
                bool isChecked = GameApi.BoolOf2(_api.Get(sender, MapMenuSpecs.ItemIsChecked));
                bool? apply = e.Sync.OnItemChanged(isChecked, e.IsOpen());
                if (apply.HasValue)
                {
                    if (Debug) _api.Log("map menu: " + e.Tag + " -> " + (apply.Value ? "open" : "close"));
                    e.SetOpen(apply.Value);
                }
            }
            catch (Exception ex) { _api.Log("map menu click failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ---------------------------------------------------------------- insertion

        private bool ResolveTypes()
        {
            if (_mCount != null) return true;
            _tMenuItem = _api.TypeOf(MapMenuSpecs.MenuItemType);
            if (_tMenuItem == null || !_api.Present(MapMenuSpecs.MainViewInstance)) return false;
            _mCount = _api.Member(MapMenuSpecs.TreeChildCount) as MethodInfo;
            _mChild = _api.Member(MapMenuSpecs.TreeChild) as MethodInfo;
            return _mCount != null && _mChild != null;
        }

        /// <summary>Sets a property of a Noesis element; a missing member throws (the caller falls back to the layers).</summary>
        private void Put(object o, MemberSpec s, object value)
        {
            if (!_api.Set(o, s, value)) throw new MissingMemberException(s.Type, s.Member);
        }

        /// <summary>The cached hamburger menu is on screen (IsLoaded of the menu itself, not of our items: a closed submenu's items are never loaded).</summary>
        private bool MenuLoaded(object menu)
        {
            try { return menu != null && GameApi.BoolOf2(_api.Get(menu, MapMenuSpecs.ItemIsLoaded)); } catch { return false; }
        }

        private bool OwnItemsInMenu(object menu)
        {
            if (menu == null) return false;
            var ours = new List<object>();
            foreach (var e in _entries) ours.Add(e.Item.Target);
            try { return MapMenuMath.AllMembers(_api.Get(menu, MapMenuSpecs.ItemItems) as IEnumerable, ours); } catch { return false; }
        }

        private void SetCache(object menu, object worldView)
        {
            _menu = menu; _worldView = worldView;
            _menuKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(menu);
        }

        private void ClearCache() { _menu = null; _worldView = null; _menuKey = 0; }

        private static int IndexOf(IEnumerable items, object item)
        {
            if (items == null || item == null) return -1;
            int i = 0;
            foreach (var it in items) { if (ReferenceEquals(it, item)) return i; i++; }
            return -1;
        }

        /// <summary>Records why a try failed: logs a new reason once, counts the failure (not for a forced try) and gives up after MaxTries.</summary>
        private void Miss(double now, string reason, bool force)
        {
            if (!force) { _failures++; _nextScan = now + MapMenuMath.RetryDelayS(_failures); }
            double next = Math.Max(0, _nextScan - now);
            if (Debug)
            {
                if (reason != _lastReason) _api.Log("map menu: miss reason=" + reason + " try=" + _failures + " next=" + Math.Round(next) + "s");
            }
            else if (!_notFoundLogged) { _notFoundLogged = true; _api.Log("map menu: not found yet (" + reason + ")"); }
            _lastReason = reason;
            if (!force && MapMenuMath.GiveUp(_failures, MaxTries))
                UseFallback("world view item not found after " + _failures + " tries: " + reason);
        }

        private void EnsureInserted(double now, bool force, object opened = null)
        {
            bool inMission = _api.Frame.InMission;
            if (inMission && !_wasInMission) { _failures = 0; _lastReason = null; _notFoundLogged = false; _nextScan = 0; }
            if (!inMission && _wasInMission) ClearCache();
            _wasInMission = inMission;
            if (!inMission || !ResolveTypes()) return;
            // the menu that just opened: recognised without walking the tree
            if (opened != null && _tMenuItem.IsInstanceOfType(opened))
            {
                object ow, parent;
                int oi;
                if (TryFindInOpened(opened, out ow, out oi, out parent)) SetCache(parent, ow);
            }
            var menu = _menu;
            var act = MapMenuMath.NextAction(inMission, menu != null, MenuLoaded(menu), OwnItemsInMenu(menu), force, now, _nextScan);
            if (act != MenuAction.Insert && act != MenuAction.Scan) { if (menu != null && _mode != "menu" && OwnItemsInMenu(menu)) _mode = "menu"; return; }
            bool noCount = force;
            // T-262: the strong ref must not keep a detached / replaced menu forever: a cached menu that is no longer on screen is dropped before the scan
            if (act == MenuAction.Scan && menu != null && !MenuLoaded(menu)) ClearCache();
            if (act == MenuAction.Insert)
            {
                var sw0 = Stopwatch.StartNew();
                var wv = _worldView;
                int wvIndex = -1;
                try { wvIndex = IndexOf(_api.Get(menu, MapMenuSpecs.ItemItems) as IEnumerable, wv); } catch { }
                if (wvIndex < 0) { ClearCache(); noCount = true; }   // the game changed the item set: walk the tree once
                else
                {
                    if (_failedRoots.Contains(_menuKey)) return;
                    _logGate.NoteReinsert();
                    InsertInto(menu, wv, wvIndex, opened != null ? "submenu" : "cache", sw0, _menuKey);
                    return;
                }
            }
            object top = _api.GetStatic(MapMenuSpecs.MainViewInstance);
            if (top == null) { Miss(now, "no_root", noCount); return; }
            var sw = Stopwatch.StartNew();
            object parentItem, worldView; int index; string reason;
            if (!FindWorldView(top, out parentItem, out worldView, out index, out reason)) { Miss(now, reason, noCount); return; }
            _failures = 0; _lastReason = null;
            _nextScan = now + MapMenuMath.MinRescanS;
            SetCache(parentItem, worldView);
            _logGate.NoteScan();
            if (_failedRoots.Contains(_menuKey)) return;
            InsertInto(parentItem, worldView, index, "scan", sw, _menuKey);
        }

        /// <summary>Inserts the missing items right after World View (found ones are only attached). One Info line per menu instance. false when the insertion threw (layer fallback).</summary>
        private bool InsertInto(object menu, object worldView, int index, string via, Stopwatch sw, int key)
        {
            try
            {
                var items = _api.Get(menu, MapMenuSpecs.ItemItems);
                var en = items as IEnumerable;
                int before = MapMenuMath.Count(en), inserted = 0, found = 0;
                string tagType = "null";
                foreach (var e in _entries)
                {
                    var existing = MapMenuMath.FindOwn(en, e.Tag, e.Item.Target, TagOfItem);
                    if (existing != null)
                    {
                        found++;
                        tagType = MapMenuMath.TagTypeName(existing, TagOfItem);
                        if (!ReferenceEquals(existing, e.Item.Target)) Attach(e, existing, false);
                        continue;
                    }
                    var mi = _api.New(MapMenuSpecs.MenuItemCtor);
                    Put(mi, MapMenuSpecs.ItemHeader, HeaderOf(e));
                    Put(mi, MapMenuSpecs.ItemIsCheckable, true);
                    Put(mi, MapMenuSpecs.ItemStaysOpen, true);
                    Put(mi, MapMenuSpecs.ItemTag, e.Tag);
                    var style = _api.Get(worldView, MapMenuSpecs.ItemStyle);
                    if (style != null) Put(mi, MapMenuSpecs.ItemStyle, style);
                    e.Sync = new MapMenuSync(false);
                    Put(mi, MapMenuSpecs.ItemIsChecked, e.IsOpen());
                    e.Sync = new MapMenuSync(e.IsOpen());
                    Attach(e, mi, true);
                    index++;
                    InsertAt(items, index, mi);
                    inserted++;
                }
                _mode = "menu";
                if (_logGate.ShouldLogRoot(key))
                    _api.Log("map menu: root=" + key + " via=" + via + " wv=" + index + " items=" + before + "->" + MapMenuMath.Count(en)
                        + " new=" + inserted + " found=" + found + " tag=" + tagType + " loaded=" + MenuLoaded(menu) + " scan=" + sw.ElapsedMilliseconds + " ms");
                return true;
            }
            catch (Exception ex)
            {
                _failedRoots.Add(key);
                UseFallback("insert failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private void Attach(Entry e, object item, bool fresh)
        {
            e.Item.Target = item;
            e.EnabledShown = null;   // T-228: a new menu instance gets IsEnabled / ToolTip again
            foreach (var evSpec in new[] { MapMenuSpecs.ItemChecked, MapMenuSpecs.ItemUnchecked })
            {
                var ev = _api.Member(evSpec) as EventInfo;
                if (ev == null) throw new MissingMemberException(item.GetType().Name, evSpec.Member);
                var captured = e;
                // Delegate parameter contravariance lets (object, object) bind to RoutedEventHandler(object, RoutedEventArgs)
                Action<object, object> act = (s, a) => OnItemChanged(captured, s);
                var h = Delegate.CreateDelegate(ev.EventHandlerType, act.Target, act.Method);
                ev.AddEventHandler(item, h);
            }
        }

        private void InsertAt(object items, int index, object item)
        {
            _api.Call(items, MapMenuSpecs.ItemsInsert, index, item);   // a missing member throws MissingMemberException
        }

        private void RemoveItem(object item)
        {
            var parent = _api.Get(item, MapMenuSpecs.ItemParent);
            var items = parent == null ? null : _api.Get(parent, MapMenuSpecs.ItemItems);
            if (items == null || !_api.Present(MapMenuSpecs.ItemsRemove)) return;
            _api.Call(items, MapMenuSpecs.ItemsRemove, item);
        }

        private bool IsWindow(object o) { return _api.IsOfType(o, MapMenuSpecs.WindowType); }

        /// <summary>The Tag of a menu item (null when it has none): the reader the pure MapMenuMath is given.</summary>
        private object TagOfItem(object item) { return _api.Get(item, MapMenuSpecs.ItemTag); }

        /// <summary>Walks the tree for the main map window: SeapowerUI.Window whose DataContext is MainGameViewModel.Map (else any SceneMapViewModel window).</summary>
        private object FindMapWindow(object top)
        {
            object mainVm = null;
            try { var dc = _api.Get(top, MapMenuSpecs.ItemDataContext); mainVm = dc == null ? null : _api.Get(dc, MapMenuSpecs.MainVmMap); } catch { }
            object byType = null;
            var stack = new Stack<object>();
            stack.Push(top);
            int budget = 60000;
            while (stack.Count > 0 && budget-- > 0)
            {
                var node = stack.Pop();
                if (IsWindow(node))
                {
                    var dc = _api.Get(node, MapMenuSpecs.WindowDataContext);
                    if (dc != null)
                    {
                        if (mainVm != null && ReferenceEquals(dc, mainVm)) return node;
                        if (byType == null && dc.GetType().Name.Contains("SceneMapViewModel")) byType = node;
                    }
                }
                int n;
                try { n = Convert.ToInt32(_mCount.Invoke(null, new[] { node })); } catch { continue; }
                for (int i = 0; i < n; i++)
                {
                    try { var c = _mChild.Invoke(null, new[] { node, (object)i }); if (c != null) stack.Push(c); } catch { }
                }
            }
            return byType;
        }

        /// <summary>Finds the World View item inside the main map window. On failure <paramref name="reason"/> says why (no_map_window, no_hamburger, no_world_view).</summary>
        private bool FindWorldView(object top, out object parentItem, out object worldView, out int index, out string reason)
        {
            parentItem = null; worldView = null; index = -1; reason = null;
            var window = FindMapWindow(top);
            if (window == null) { reason = "no_map_window"; return false; }
            object vm = _api.Get(window, MapMenuSpecs.WindowDataContext);
            string buttonName = vm == null ? null : _api.Get(vm, MapMenuSpecs.WorldViewButtonName) as string;
            _buttonName = buttonName;
            var stack = new Stack<object>();
            stack.Push(window);
            int budget = 60000, menus = 0;
            var headers = new List<string>();
            var hamburgers = new List<object>();
            while (stack.Count > 0 && budget-- > 0)
            {
                var node = stack.Pop();
                if (_tMenuItem.IsInstanceOfType(node))
                {
                    menus++;
                    var sub = _api.Get(node, MapMenuSpecs.ItemItems) as IEnumerable;
                    bool hasSub = false;
                    if (sub != null) foreach (var sit in sub) if (sit != null) { hasSub = true; break; }
                    var up = _api.Get(node, MapMenuSpecs.ItemParent);
                    if (hasSub && (up == null || !_tMenuItem.IsInstanceOfType(up))) hamburgers.Add(node);
                    if (TryWorldViewIn(node, buttonName, out worldView, out index, headers)) { parentItem = node; HookMenus(hamburgers, node); return true; }
                }
                int n;
                try { n = Convert.ToInt32(_mCount.Invoke(null, new[] { node })); } catch { continue; }
                for (int i = 0; i < n; i++)
                {
                    try { var c = _mChild.Invoke(null, new[] { node, (object)i }); if (c != null) stack.Push(c); } catch { }
                }
            }
            HookMenus(hamburgers, null);
            if (hamburgers.Count == 0) { reason = "no_hamburger(menus=" + menus + ")"; return false; }
            reason = "no_world_view(items=" + headers.Count + " headers=[" + string.Join("|", headers.ToArray()) + "] button=" + buttonName + ")";
            return false;
        }

        /// <summary>T-154 F3: the game builds a new menu instance on every open. Look for World View in the opened menu itself, then in its
        /// direct sub-items, by template features. O(items); the full tree walk is only the caller's fallback.</summary>
        private bool TryFindInOpened(object menu, out object worldView, out int index, out object parent)
        {
            worldView = null; index = -1; parent = null;
            if (TryWorldViewIn(menu, _buttonName, out worldView, out index, null)) { parent = menu; return true; }
            var items = _api.Get(menu, MapMenuSpecs.ItemItems) as IEnumerable;
            if (items == null) return false;
            foreach (var it in items)
            {
                if (it == null || !_tMenuItem.IsInstanceOfType(it)) continue;
                if (TryWorldViewIn(it, _buttonName, out worldView, out index, null)) { parent = it; return true; }
            }
            return false;
        }

        private bool TryWorldViewIn(object menuItem, string buttonName, out object worldView, out int index, List<string> headers)
        {
            worldView = null; index = -1;
            var items = _api.Get(menuItem, MapMenuSpecs.ItemItems) as IEnumerable;
            if (items == null) return false;
            worldView = MapMenuMath.FindWorldViewIn(items, buttonName, it => _tMenuItem.IsInstanceOfType(it),
                it => { string head = _api.Get(it, MapMenuSpecs.ItemHeader) as string; if (headers != null && headers.Count < 12) headers.Add(head ?? "<null>"); return head; },
                IsCheckedPath, out index);
            return worldView != null;
        }

        /// <summary>Path of the IsChecked binding of a menu item (e.g. "DataContext.WorldViewOpen") through Noesis.BindingOperations; null when unavailable.</summary>
        private string IsCheckedPath(object item)
        {
            try
            {
                if (!_api.Present(MapMenuSpecs.BindingGet)) return null;
                object dp = _api.GetStatic(MapMenuSpecs.IsCheckedProperty);
                if (dp == null) return null;
                var b = _api.CallStatic(MapMenuSpecs.BindingGet, item, dp);
                var path = b == null ? null : _api.Get(b, MapMenuSpecs.BindingPath);
                if (path == null) return null;
                return (path as string) ?? (_api.Get(path, MapMenuSpecs.PathText) as string);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- submenu opened

        /// <summary>Subscribes SubmenuOpened on the hamburger menus (once each): the items are inserted the moment the menu opens. Without the event IsSubmenuOpen is polled.</summary>
        private void HookMenus(List<object> hamburgers, object preferred)
        {
            var all = new List<object>(hamburgers);
            if (preferred != null && !all.Contains(preferred)) all.Add(preferred);
            foreach (var h in all)
            {
                int k = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(h);
                if (!_hooked.Add(k)) continue;
                try
                {
                    var ev = _api.Member(MapMenuSpecs.SubmenuOpened) as EventInfo;
                    if (ev == null) { _polled.Add(new WeakReference(h)); _polledOpen.Add(false); continue; }
                    Action<object, object> act = (s, a) => OnSubmenuOpened(s);
                    ev.AddEventHandler(h, Delegate.CreateDelegate(ev.EventHandlerType, act.Target, act.Method));
                }
                catch (Exception ex) { _api.Log("map menu: SubmenuOpened hook failed: " + ex.GetType().Name); }
            }
        }

        private void OnSubmenuOpened(object sender)
        {
            try { if (_mode != "fallback") EnsureInserted(Time.unscaledTime, true, sender); }
            catch (Exception ex) { _api.Log("map menu: submenu open failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void PollSubmenus(double now)
        {
            if (_polled.Count == 0 || now < _nextPoll) return;
            _nextPoll = now + 0.5;
            for (int i = 0; i < _polled.Count; i++)
            {
                var h = _polled[i].Target;
                if (h == null) continue;
                bool open = GameApi.BoolOf2(_api.Get(h, MapMenuSpecs.IsSubmenuOpen));
                if (open && !_polledOpen[i]) OnSubmenuOpened(h);
                _polledOpen[i] = open;
            }
        }

        // ---------------------------------------------------------------- T-240: the open hamburger submenu as a game popup

        private double[] _openRect;
        private double _openRectAt = -1;

        /// <summary>T-240: the hamburger submenu with our items is open; rect = union of the screen rects of its items (and of the items of an open nested submenu),
        /// null when any item is not a visual / a member is missing / nesting is deeper than one level. Reads IsSubmenuOpen every call (cheap); the geometry at most every 0.1 s while open.</summary>
        internal bool OpenMenu(out double[] rect)
        {
            double now = Time.unscaledTime;
            bool raw = OpenMenuRaw(out rect, now);
            // T-247: the game's IsSubmenuOpen (and the menu cache) drops for a moment while the menu is on screen: hold the last open state
            bool open = GamePopupMath.Hold(raw, now, ref _holdOpenAt, GamePopupMath.HoldS);
            if (raw) { _holdRect = rect; return true; }
            if (open) { rect = _holdRect; return true; }
            _holdRect = null;
            return false;
        }

        private double _holdOpenAt = double.NegativeInfinity;
        private double[] _holdRect;

        /// <summary>T-262: why the last OpenMenuRaw returned false (null while raw); LastDropAt = Time.unscaledTime of the true-&gt;false edge.</summary>
        internal string LastDropReason { get; private set; }
        internal double LastDropAt { get; private set; }

        private bool OpenMenuRaw(out double[] rect, double now)
        {
            rect = null;
            try
            {
                var menu = _menu;
                bool alive = menu != null;
                bool modeMenu = alive && _mode == "menu";
                bool loaded = modeMenu && MenuLoaded(menu);
                bool flag = loaded && GameApi.BoolOf2(_api.Get(menu, MapMenuSpecs.IsSubmenuOpen));
                string reason = GamePopupMath.DropReason(alive, modeMenu, loaded, flag);
                if (reason != null) { _openRect = null; _openRectAt = -1; NoteDrop(reason, now); return false; }
                LastDropReason = null;
                if (_openRectAt < 0 || now - _openRectAt >= 0.1)
                {
                    _openRectAt = now;
                    _openRect = SubmenuRect(menu);
                }
                rect = _openRect;
                return true;
            }
            catch { rect = null; return true; }
        }

        private void NoteDrop(string reason, double now)
        {
            if (LastDropReason == null)
            {
                LastDropAt = now;
                if (Debug) _api.Log("map menu: popup raw drop reason=" + reason);
            }
            LastDropReason = reason;
        }

        private double[] SubmenuRect(object menu)
        {
            var rects = new List<double[]>();
            var items = _api.Get(menu, MapMenuSpecs.ItemItems) as IEnumerable;
            if (items == null) return null;
            foreach (var it in items)
            {
                if (it == null) return null;
                double[] r;
                if (!_api.PopupElementRect(it, out r)) { if (VisibleOrUnknown(it)) return null; continue; }
                rects.Add(r);
                if (GameApi.BoolOf2(_api.Get(it, MapMenuSpecs.IsSubmenuOpen)))
                {
                    var sub = _api.Get(it, MapMenuSpecs.ItemItems) as IEnumerable;
                    if (sub == null) return null;
                    foreach (var si in sub)
                    {
                        double[] sr;
                        if (si == null) return null;
                        if (!_api.PopupElementRect(si, out sr)) { if (VisibleOrUnknown(si)) return null; continue; }
                        if (GameApi.BoolOf2(_api.Get(si, MapMenuSpecs.IsSubmenuOpen))) return null;   // deeper than one level: unknown
                        rects.Add(sr);
                    }
                }
            }
            return GamePopupMath.Union(rects);
        }

        /// <summary>A collapsed item has no geometry and does not matter; anything else without a rectangle makes the whole popup unknown.</summary>
        private bool VisibleOrUnknown(object item)
        {
            var v = _api.Get(item, MapMenuSpecs.ItemVisibility);
            return !(v is bool) || (bool)v;
        }
    }
}
