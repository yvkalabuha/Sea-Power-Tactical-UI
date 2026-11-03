using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>
    /// "XO" (Старпом) submenu of the unit context menu (T-064). Pure reflection (no Harmony here): Patches.cs calls AppendBotMenu from a
    /// postfix of ObjectBaseViewModel.GetContextMenuItems. The menu is rebuilt by the game on every opening, so check marks are
    /// a snapshot at that moment. Clicks run the same registry actions as the protocol command `action` (source "menu").
    /// The tree comes from MenuModel (shared with the radial ring, T-068). "retarget" is a plain value here: from the menu it needs
    /// no confirmation (customer decision 2026-10-16, ADR-0028 item 4); the bot protocol still demands "confirm":true.
    /// </summary>
    internal sealed partial class GameApi
    {
        public bool MenuEnabled = true;
        private bool _menuErrLogged;

        private Type _tItem, _tCmd, _tRp;
        private ConstructorInfo _ctorItem, _ctorCmd, _ctorRp;
        private MethodInfo _toRo;

        /// <summary>Finds the game/UniRx types the menu needs. UniRx may not be loaded yet when the plugin starts: load it by name.</summary>
        private void ResolveMenuTypes()
        {
            if (FindType(MenuSpecs.ReactivePropertyType.Type) == null)
            {
                try { Assembly.Load("UniRx"); } catch { }
                _types.Remove(MenuSpecs.ReactivePropertyType.Type); _types.Remove(MenuSpecs.ReactiveExtensionsType.Type);
            }
            _tItem = FindType(MenuSpecs.ItemType.Type);
            _tCmd = FindType(MenuSpecs.CommandType.Type);
            _tRp = FindType(MenuSpecs.ReactivePropertyType.Type);
            _ctorItem = null; _ctorCmd = null; _ctorRp = null; _toRo = null;
            if (_tItem != null)
                foreach (var c in _tItem.GetConstructors())
                {
                    var p = c.GetParameters();
                    if (p.Length == 10 && p[0].ParameterType == typeof(string) && p[1].ParameterType.IsGenericType) _ctorItem = c;
                }
            if (_tCmd != null) _ctorCmd = _tCmd.GetConstructor(new[] { typeof(Action<object>) });
            if (_tRp != null)
                foreach (var c in _tRp.MakeGenericType(typeof(bool)).GetConstructors())
                {
                    var p = c.GetParameters(); // ReactiveProperty(T initialValue, IEqualityComparer<T> comparer = null)
                    if (p.Length >= 1 && p[0].ParameterType == typeof(bool) && (_ctorRp == null || p.Length < _ctorRp.GetParameters().Length)) _ctorRp = c;
                }
            var ext = FindType(MenuSpecs.ReactiveExtensionsType.Type);
            if (ext != null)
                foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != MenuSpecs.ToReadOnlyName || !m.IsGenericMethodDefinition) continue;
                    if (_toRo == null || m.GetParameters().Length < _toRo.GetParameters().Length) _toRo = m;
                }
        }

        /// <summary>Check lines of the generic constructors the menu builds items with (optional; the members themselves are in MenuSpecs, via the registry).</summary>
        internal void MenuChecks()
        {
            ResolveMenuTypes();
            Checks.Add(new Check { Capability = "bot_menu", Member = "ContextMenuItem..ctor(string,collection,ICommand,ReadOnlyReactiveProperty,...) x10", Required = false, Present = _ctorItem != null });
            Checks.Add(new Check { Capability = "bot_menu", Member = "SeaPower.DelegateCommand..ctor(Action<object>)", Required = false, Present = _ctorCmd != null });
            Checks.Add(new Check { Capability = "bot_menu", Member = "UniRx.ReactiveProperty`1..ctor(bool[,comparer])", Required = false, Present = _ctorRp != null });
            Checks.Add(new Check { Capability = "bot_menu", Member = "UniRx.ReactivePropertyExtensions.ToReadOnlyReactiveProperty<T>", Required = false, Present = _toRo != null });
            var coll = _ctorItem == null ? null : _ctorItem.GetParameters()[1].ParameterType;
            Checks.Add(new Check { Capability = "bot_menu", Member = "TrulyObservableCollection<ContextMenuItem> default ctor", Required = false, Present = coll != null && coll.GetConstructor(Type.EmptyTypes) != null });
        }

        internal bool MenuReady { get { return _ctorItem != null && _ctorCmd != null && _ctorRp != null && _toRo != null; } }

        private object MkRo(bool value)
        {
            var cp = _ctorRp.GetParameters();
            var ra = new object[cp.Length]; ra[0] = value;
            for (int i = 1; i < cp.Length; i++) ra[i] = DefaultFor(cp[i]);
            var rp = _ctorRp.Invoke(ra);
            return _toRo.MakeGenericMethod(typeof(bool)).Invoke(null, _toRo.GetParameters().Length == 1 ? new[] { rp } : PadArgs(_toRo, rp));
        }

        private static object[] PadArgs(MethodInfo m, object first)
        {
            var ps = m.GetParameters();
            var a = new object[ps.Length];
            a[0] = first;
            for (int i = 1; i < ps.Length; i++) a[i] = DefaultFor(ps[i]);
            return a;
        }

        private object MkItem(string label, object children, Action click, bool? isChecked, bool enabled)
        {
            object cmd = click == null ? null : _ctorCmd.Invoke(new object[] { (Action<object>)(o =>
            {
                try { click(); }
                catch (Exception e) { Log("menu click failed: " + e.GetType().Name + ": " + e.Message); }
            }) });
            var args = new object[10];
            args[0] = label; args[1] = children; args[2] = cmd;
            args[3] = enabled ? null : MkRo(false);
            args[4] = isChecked.HasValue ? MkRo(isChecked.Value) : null;
            return _ctorItem.Invoke(args);
        }

        /// <summary>
        /// Hooks filled in by MenuGame.cs (they need the Standing Orders and smart attack code, which the offline selfcheck does not compile):
        /// the four-way unit kind, the air-defence-ready flag, the Standing Orders switch and the wire-torpedo check (T-098 F2, T-102 R2).
        /// </summary>
        /// <summary>T-160: set by the range_rings module (a service). Null = no ring items in the XO menu.</summary>
        internal Ui.IRingMenu RingMenu;

        partial void MenuViewExt(object unit, UnitView v);
        partial void RadialOrdersView(object unit, UnitView v);   // T-243 (RadialOrdersGame.cs): FormationRing, RtbRing, ModHolds
        partial void RadialGroupsView(object unit, UnitView v);   // T-328 (RadialGroupsGame.cs): UnitView.Groups of aircraft / helicopters for the "+" / "-" ring sectors
        partial void MenuRunExt(string nodeId, object unit, ref string result);

        /// <summary>Snapshot of a unit for MenuModel (also used by the radial ring). Kind: ship|sub|air|helo (T-099).</summary>
        internal UnitView MenuView(object unit)
        {
            var v = new UnitView { Uid = unit == null ? 0 : UidOf(unit), Kind = UnitKindOfObj(unit), AllowAttack = AllowAttack, OverlayOn = OverlayOn, Name = unit == null ? null : Get(unit, CoreSpecs.ObjName) as string };
            v.ModOn["overview"] = OverviewOn;
            foreach (var c in Capabilities) v.Caps.Add(c);
            v.Flags["auto_evade"] = ResolveFlag(unit, "auto_evade");
            v.Flags["veto_neutral"] = ResolveFlag(unit, "veto_neutral");
            v.InGroup = unit != null && Get(unit, MenuSpecs.ObjFormation) != null;
            MenuViewExt(unit, v);
            try { RadialOrdersView(unit, v); } catch (Exception e) { v.FormationRing = v.RtbRing = null; Log("menu radial orders view failed: " + e.GetType().Name); }   // T-243
            v.PostLaunch = v.Kind == "air" ? ResolvePostLaunch(unit) : null;
            return v;
        }

        /// <summary>T-160: MenuView + the ring and intercept items; only the XO menu asks for it (the radial ring does not pay for it). A failure = no item.</summary>
        private UnitView XoView(object unit)
        {
            var v = MenuView(unit);
            if (unit == null) return v;
            int uid = UidOf(unit);
            try { var rm = RingMenu; if (rm != null) v.Rings = rm.View(uid); } catch (Exception e) { v.Rings = null; Log("menu rings view failed: " + e.GetType().Name); }
            if (v.Kind == "ship" || v.Kind == "sub")
            {
                try { var im = InterceptMenu; string eta; if (im != null && im.UnitActive(uid, out eta)) { v.InterceptActive = true; v.InterceptEta = eta; } }
                catch (Exception e) { v.InterceptActive = false; Log("menu intercept view failed: " + e.GetType().Name); }
            }
            return v;
        }

        /// <summary>"Release control of the whole group": restore_control for every member (player-owned, alive) of the unit's formation.</summary>
        private string ReleaseGroup(object unit, string source)
        {
            var def = FindAction("release_control");
            var f = unit == null ? null : Get(unit, MenuSpecs.ObjFormation);
            if (def == null || f == null) return "{\"ok\":false,\"error\":\"not_applicable\"}";
            int ok = 0, fail = 0;
            foreach (var m in MembersOf(f))
            {
                if (!BoolOf(Get(m, CoreSpecs.ObjIsPlayer)) || !BoolOf(Get(m, CoreSpecs.ObjIsControllable))) continue;
                var c = new Dictionary<string, object> { { "name", def.Id }, { "uid", (double)UidOf(m) } };
                if (RunAction("\"" + source + "\"", def, c, source).Contains("\"ok\":true")) ok++; else fail++;
            }
            Log(source + " release_control_group uid=" + UidOf(unit) + " -> ok=" + ok + " failed=" + fail);
            return fail == 0 && ok > 0 ? "{\"ok\":true,\"released\":" + ok + "}" : "{\"ok\":false,\"error\":\"partial\",\"released\":" + ok + ",\"failed\":" + fail + "}";
        }

        /// <summary>Runs the action behind a menu node. A toggle takes its value at the moment of the call (not when the menu was built).</summary>
        internal string RunNode(MenuNode n, object unit, string source)
        {
            switch (n.ActionId)
            {
                case "intercept_cancel":
                    {
                        var im = InterceptMenu;
                        if (im == null || unit == null) return "{\"ok\":false,\"error\":\"unsupported\"}";
                        string ie = im.CancelUnit(UidOf(unit));
                        Log(source + " intercept_cancel uid=" + UidOf(unit) + " -> " + (ie ?? "ok"));
                        return ie == null ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"" + ie + "\"}";
                    }
                case "rings_sonar":
                    {
                        var rm = RingMenu;
                        if (rm == null || unit == null) return "{\"ok\":false,\"error\":\"unsupported\"}";
                        bool on = rm.ToggleSonar(UidOf(unit));
                        Log(source + " " + n.ActionId + " uid=" + UidOf(unit) + " -> " + (on ? "on" : "off"));
                        return "{\"ok\":true,\"on\":" + (on ? "true" : "false") + "}";
                    }
                case "rings_weapon":
                    {
                        var rm = RingMenu;
                        if (rm == null || unit == null) return "{\"ok\":false,\"error\":\"unsupported\"}";
                        // 2026-10-22: a weapon value toggles that weapon (several at once); "" = Clear (unchecks all)
                        bool chosen = rm.ChooseWeapon(UidOf(unit), n.Value ?? "");
                        Log(source + " rings_weapon uid=" + UidOf(unit) + " " + (string.IsNullOrEmpty(n.Value) ? "-> cleared" : n.Value + " -> " + (chosen ? "on" : "off")));
                        return "{\"ok\":true,\"on\":" + (chosen ? "true" : "false") + "}";
                    }
                case "release_control_group":
                    return ReleaseGroup(unit, source);
                case "group_join":   // T-328 (ADR-0127): ring sectors of aircraft, run by RadialGroupsGame
                case "group_leave":
                    return RingGroupRun(n, unit, source);
                case "air_defense_ready":
                case "formation_return":   // T-243: UI ring sectors, run by RadialOrdersGame via MenuGame
                case "air_rtb":
                case "evade_attack":   // T-100: UI ring sector (MenuModel.UiRingSlots), run by the air_evasion module via MenuGame
                case "sub_torpedo_alert":   // T-101: UI ring sector of the submarine, run by the evasion module via MenuGame
                    string res = null;
                    MenuRunExt(n.ActionId, unit, ref res);
                    return res ?? "{\"ok\":false,\"error\":\"unsupported\"}";
            }
            var def = FindAction(n.ActionId);
            if (def == null) return "unknown action " + n.ActionId;
            var c = new Dictionary<string, object> { { "name", def.Id }, { "uid", (double)UidOf(unit) } };
            if (n.Kind == "toggle") c["on"] = !(def.Target == "mod" ? ModFlagOn(def.Flag) : ResolveFlag(unit, def.Flag));
            else if (n.Kind == "value") c["value"] = n.Value;
            string r = RunAction("\"" + source + "\"", def, c, source);
            string sum = LastActionSummary;
            LastActionSummary = null;
            Log(!string.IsNullOrEmpty(sum) ? sum : source + " action " + def.Id + (n.Value != null ? "=" + n.Value : "") + " -> " + (r.Contains("\"ok\":true") ? "ok" : r));
            return r;
        }

        private object NodeToItem(MenuNode n, object unit, Type collectionType)
        {
            if (n.Children.Count == 0 && n.Kind == "group") return null;
            if (n.Kind != "group") return MkItem(n.Label, null, () => RunNode(n, unit, "menu"), n.Kind == "action" ? (bool?)null : n.Checked, n.Enabled);
            var kids = (IList)Activator.CreateInstance(collectionType);
            foreach (var c in n.Children) { var it = NodeToItem(c, unit, collectionType); if (it != null) kids.Add(it); }
            return MkItem(n.Label, kids, null, null, n.Enabled);
        }

        private List<MenuNode> MenuNodes(UnitView v) { return MenuModel.Build(Actions, v); }

        /// <summary>Offline: builds the game's item tree from a synthetic air unit; "After launch" (under Modes without Standing Orders) has 3 plain values, no confirmation items. null = ok.</summary>
        internal string MenuTreeSmoke()
        {
            try
            {
                EnsureActions();
                if (!MenuReady) ResolveMenuTypes();
                if (!MenuReady) return "menu types missing";
                if (Actions.Count == 0) return "registry empty";
                var v = new UnitView { Kind = "air", AllowAttack = true, PostLaunch = "off" };
                v.Caps.Add("unit_flags");
                var collType = _ctorItem.GetParameters()[1].ParameterType;
                var nodes = MenuNodes(v);
                var modes = nodes.Find(x => x.Id == "modes");
                if (modes == null) return "Modes group missing without standing_orders";
                var pl = modes.Children.Find(x => x.Id == "post_launch");
                if (pl == null) return "post_launch missing";
                var item = NodeToItem(pl, null, collType);
                var sub = Get(item, MenuSpecs.ItemItems) as IList;
                if (sub == null || sub.Count != 3) return "post_launch has " + (sub == null ? -1 : sub.Count) + " items";
                foreach (var s in sub)
                {
                    var inner = Get(s, MenuSpecs.ItemItems) as IList;
                    if (inner != null && inner.Count != 0) return "nested confirmation item";
                    if (Get(s, MenuSpecs.ItemCommand) == null) return "item without a command";
                }
                if (nodes.Exists(x => x.ActionId == "radar_coverage")) return "radar_coverage must not be in the menu";
                var rv = new UnitView { Kind = "ship", AllowAttack = true, Rings = new RingMenuView { HasSonar = true } };
                rv.Rings.Checked.Add("harpoon");
                rv.Rings.Weapons.Add(new RingWeaponChoice { Key = "harpoon", Label = "Harpoon" });
                rv.Caps.Add("standing_orders");
                var rings = MenuNodes(rv).Find(x => x.Id == "rings");
                if (rings == null) return "rings group missing";
                var ringsItem = NodeToItem(rings, null, collType);
                var kids = Get(ringsItem, MenuSpecs.ItemItems) as IList;
                if (kids == null || kids.Count != 2) return "rings group has " + (kids == null ? -1 : kids.Count) + " items";
                foreach (var kid in kids)
                {
                    var sub2 = Get(kid, MenuSpecs.ItemItems) as IList;
                    if (sub2 != null && sub2.Count > 0)
                    {
                        if (sub2.Count != 2) return "weapon submenu has " + sub2.Count + " items";   // Harpoon (checked) + Clear
                        foreach (var w in sub2) if (Get(w, MenuSpecs.ItemCommand) == null || !(Get(w, MenuSpecs.ItemCheckable) is bool)) return "weapon value is not a checkable command";
                    }
                    else if (Get(kid, MenuSpecs.ItemCommand) == null || !(Get(kid, MenuSpecs.ItemCheckable) is bool)) return "ring toggle is not a checkable command";
                }
                v.Caps.Add("standing_orders");
                var nodes2 = MenuNodes(v);
                if (nodes2.Exists(x => x.Id == "modes" || x.Id == "post_launch")) return "settings must leave the menu with standing_orders";
                return null;
            }
            catch (Exception e) { var i = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; return i.GetType().Name + ": " + i.Message; }
        }

        /// <summary>Offline smoke test: builds a small item tree with the game's own classes. null = ok.</summary>
        internal string MenuSmoke()
        {
            try
            {
                if (!MenuReady) return "menu types missing";
                var coll = (IList)Activator.CreateInstance(_ctorItem.GetParameters()[1].ParameterType);
                int clicks = 0;
                var child = MkItem("child", null, () => clicks++, true, false);
                coll.Add(child);
                var parent = MkItem("Parent", coll, null, null, true);
                var items = Get(parent, MenuSpecs.ItemItems) as IList;
                var cmd = Get(child, MenuSpecs.ItemCommand) as System.Windows.Input.ICommand;
                cmd.Execute(null);
                var chk = Get(child, MenuSpecs.ItemCheckable);
                if (items == null || items.Count != 1 || clicks != 1 || !(chk is bool) || !(bool)chk) return "unexpected item state";
                return null;
            }
            catch (Exception e) { var i = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; return i.GetType().Name + ": " + i.Message; }
        }

        /// <summary>The "XO" (Старпом) item for a unit, or null (not an own controllable ship/aircraft, or the registry is empty).</summary>
        internal object BuildBotMenu(object objectBase, Type collectionType)
        {
            EnsureActions();
            if (!MenuReady) ResolveMenuTypes(); // types that were not loaded yet at start-up
            if (!MenuReady || Actions.Count == 0 || objectBase == null || !IsAlive(objectBase)) return null;
            if (!BoolOf(Get(objectBase, CoreSpecs.ObjIsPlayer)) || !BoolOf(Get(objectBase, CoreSpecs.ObjIsControllable))) return null;
            if (UnitKindOfObj(objectBase) == null) return null;
            TacticsEnsureMission();
            var sub = (IList)Activator.CreateInstance(collectionType);
            var nodes = MenuNodes(XoView(objectBase));
            if (ActionDebug) Log("menu build uid=" + UidOf(objectBase) + " : " + MenuModel.DescribeForLog(nodes));
            foreach (var n in nodes)
            {
                var it = NodeToItem(n, objectBase, collectionType);
                if (it != null) sub.Add(it);
            }
            if (sub.Count == 0) return null;
            return MkItem(UiText.Get("menu.root"), sub, null, null, true);
        }

        /// <summary>T-138: set by the intercept module (a service, not a delegate). Null = no intercept entry.</summary>
        internal Ui.IInterceptMenu InterceptMenu;
        private bool _icMenuErrLogged;

        /// <summary>Adds the "Intercept course" item to the menu of a foreign contact when an own ship or submarine is selected.</summary>
        private void AppendIntercept(object target, object collection)
        {
            var svc = InterceptMenu;
            if (svc == null || collection == null) return;
            if (!MenuReady) ResolveMenuTypes();
            if (!MenuReady) return;
            Action click;
            var it = svc.Item(target, out click);
            if (it == null) return;
            var item = MkItem(it.Label, null, it.Enabled ? click : null, null, it.Enabled);
            if (item != null) ((IList)collection).Add(item);
        }

        private bool _rrMenuErrLogged;

        /// <summary>T-162 / 2026-10-22: "Weapon >" of the range rings in the menu of an identified foreign contact (the pin items are gone).</summary>
        private void AppendRingPin(object target, object collection)
        {
            var svc = RingMenu;
            if (svc == null || collection == null) return;
            if (!MenuReady) ResolveMenuTypes();
            if (!MenuReady) return;
            var it = svc.ContactItem(target);
            if (it == null) return;
            // T-171 / 2026-10-22: only "Weapon >" of an identified foreign class (a check pins the track; "Clear" drops all)
            var node = it.Weapons == null || it.ChooseWeapon == null ? null : MenuModel.ContactWeaponNode(it.Weapons);
            if (node == null) return;
            var kids = (IList)Activator.CreateInstance(collection.GetType());
            foreach (var c in node.Children)
            {
                string key = c.Value ?? "";
                var kid = MkItem(c.Label, null, () => it.ChooseWeapon(key), c.ActionId == "rings_contact_clear" ? (bool?)null : c.Checked, true);
                if (kid != null) kids.Add(kid);
            }
            var sub = MkItem(node.Label, kids, null, null, true);
            if (sub != null) ((IList)collection).Add(sub);
        }

        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _contactDone = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

        /// <summary>Intercept + ring-pin items for the target object; once per menu collection (both postfixes may fire). Each item's error is isolated.</summary>
        private void AppendContactItems(object target, object collection)
        {
            if (collection == null) return;
            object seen;
            if (_contactDone.TryGetValue(collection, out seen)) return;
            _contactDone.Add(collection, collection);
            try { AppendIntercept(target, collection); }
            catch (Exception e)
            {
                if (!_icMenuErrLogged) { _icMenuErrLogged = true; Log("intercept menu failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
            // T-162 / 2026-10-22: the weapon list of the range rings of an identified foreign contact
            try { AppendRingPin(target, collection); }
            catch (Exception e)
            {
                if (!_rrMenuErrLogged) { _rrMenuErrLogged = true; Log("ring pin menu failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>Postfix body of SeapowerUI.MapUnitViewModel.GetContextMenuItems (the foreign-contact menu, Ctrl+RMB): intercept/pin items for Unit.BaseObject.</summary>
        internal void AppendContactMenu(object mapUnitViewModel, object collection)
        {
            try
            {
                var veh = Get(mapUnitViewModel, MenuSpecs.MapUnitVehicle);
                var target = veh == null ? null : Get(veh, MenuSpecs.VehicleBaseObject);
                if (target != null) AppendContactItems(target, collection);
            }
            catch (Exception e)
            {
                if (!_icMenuErrLogged) { _icMenuErrLogged = true; Log("contact menu failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>Postfix body: adds "XO" (Старпом) to the menu collection. Never throws, never touches the collection on error.</summary>
        internal void AppendBotMenu(object viewModel, object collection)
        {
            // T-138: the intercept entry of a contact (a foreign object) does not depend on the XO switch; its own error never reaches the XO branch
            try { AppendContactItems(Get(viewModel, MenuSpecs.ViewModelObject), collection); }
            catch (Exception e)
            {
                if (!_icMenuErrLogged) { _icMenuErrLogged = true; Log("intercept menu failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
            try
            {
                if (!MenuEnabled || collection == null) return;
                var item = BuildBotMenu(Get(viewModel, MenuSpecs.ViewModelObject), collection.GetType());
                if (item != null) ((IList)collection).Add(item);
            }
            catch (Exception e)
            {
                if (!_menuErrLogged) { _menuErrLogged = true; Log("bot menu failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }
    }
}
