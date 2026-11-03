using System;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>
    /// Game side of the radial menu (T-068): reflection only, no Unity drawing, so the offline selfcheck covers every member it touches.
    /// Radial.cs (Unity input/drawing) and Patches.ApplyRadial (Harmony, behind Radial.InputPatches) call into here.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>True while the ring (and two frames after it) blocks mouse input. Read by the optional Harmony patches P1-P4.</summary>
        internal static volatile bool RadialBlocking;
        private bool _radialBlockSet;

        /// <summary>For state.radial: set by Radial.cs.</summary>
        internal bool RadialOpen = false;
        /// <summary>Noesis PointToScreen / PointFromScreen unit in screen pixels: 1.0 (the game's Noesis already works in pixels, T-066). Set by CoverageOverlay.Calibrate; replaces the old UIScale*H/1080 division.</summary>
        internal double NoesisPxScale = 1.0;
        internal double RadialUnitUid = 0;
        internal string RadialViewName = null;
        /// <summary>KeyCode name when Radial.Hotkey parsed, else null. State: "off" (not set / bad name) | "unknown" | "free" | "taken" (T-082).</summary>
        internal string RadialHotkey = null;
        internal string RadialHotkeyState = "off";

        private MethodInfo _radHit, _radParent;
        private readonly Dictionary<Type, MethodInfo[]> _radMapMethods = new Dictionary<Type, MethodInfo[]>();

        /// <summary>The ring layout parsed (actions registry + radial layout with the ship and air views). Check line for the self-check; the members are in RadialSpecs.</summary>
        internal bool RadialLayoutReady()
        {
            EnsureActions();
            bool menu = Actions.Count > 0 && RadialLayout != null && RadialLayout.ContainsKey("ship") && RadialLayout.ContainsKey("air");
            Checks.Add(new Check { Capability = "radial_menu", Member = "radial layout parsed" + (RadialError == null ? "" : ": " + RadialError), Required = false, Present = menu });
            return menu;
        }

        /// <summary>Snapshot for MenuModel (the same one the "XO" menu uses).</summary>
        internal UnitView RadialView(object unit)
        {
            var v = MenuView(unit);
            if (v.Kind == "air" || v.Kind == "helo")   // T-328: group sectors; the XO menu (MenuView only) does not pay for them
            {
                try { RadialGroupsView(unit, v); }
                catch (Exception e) { v.Groups = null; Log("radial groups view failed: " + e.GetType().Name); }
            }
            return v;
        }

        /// <summary>Runs the chosen node (source "radial"); toggles take their value at this moment. Returns the protocol reply.</summary>
        internal string RadialRun(MenuNode node, object unit) { return RunNode(node, unit, "radial"); }

        /// <summary>Blocks (true, every frame while the ring is open) or releases (false, once) the game's mouse handling.</summary>
        internal void SetRadialBlock(bool on)
        {
            RadialBlocking = on;
            if (!on && !_radialBlockSet) return;
            var mcs = SingletonInstance(CoreSpecs.MouseStateType);
            if (mcs != null) Call(mcs, RadialSpecs.MouseOverContextMenu, on);
            _radialBlockSet = on;
        }

        internal string RadialStateJson()
        {
            return "{\"open\":" + Json.Bool(RadialOpen) + ",\"unit_uid\":" + (RadialOpen ? Json.Num(RadialUnitUid) : "null") +
                   ",\"view\":" + (RadialOpen && RadialViewName != null ? Json.Str(RadialViewName) : "null") +
                   ",\"hotkey\":" + (RadialHotkey == null ? "null" : Json.Str(RadialHotkey)) + ",\"hotkey_state\":" + Json.Str(RadialHotkeyState) + "}";
        }

        /// <summary>The own, controllable selected unit and its kind (ship|air), or null.</summary>
        internal object RadialTargetUnit(out string kind)
        {
            kind = null;
            var rp = RenderPositionInstance();
            if (rp == null) return null;
            object u;
            try { u = Call(rp, RadialSpecs.RenderSelected); } catch { return null; }
            if (!IsAlive(u)) return null;
            if (!BoolOf(Get(u, CoreSpecs.ObjIsPlayer)) || !BoolOf(Get(u, CoreSpecs.ObjIsControllable))) return null;
            kind = UnitKindOfObj(u);
            return kind == null ? null : u;
        }

        /// <summary>Game flags of the environment (everything but focus and the config switches, which Radial.cs knows).</summary>
        internal RadialEnv RadialEnvironment(string view, bool targetOk)
        {
            var e = new RadialEnv { View = view, TargetOk = targetOk, InMission = Frame.InMission };
            try { e.HudVisible = BoolOf(CallStatic(RadialSpecs.HudVisible)); } catch { e.HudVisible = false; }
            var ih = SingletonInstance(CoreSpecs.InputType);
            e.EscMenuOpen = BoolOf(Get(ih, RadialSpecs.MenuOpen));
            e.Typing = BoolOf(Get(ih, RadialSpecs.TypingActive));
            e.MessagePause = BoolOf(Get(ih, RadialSpecs.MessagePause));
            e.OverWorldDisplay = BoolOf(Get(SingletonInstance(RadialSpecs.CameraManagerType), RadialSpecs.MouseOnWorldDisplay));
            e.PipMouseOver = BoolOf(GetStatic(RadialSpecs.PipMouseOver));
            try
            {
                var mv = GetStatic(RadialSpecs.MainViewInstance);
                var cm = Get(Get(mv, RadialSpecs.ContextMenuCanvas), RadialSpecs.ContextMenu);
                e.ContextMenuOpen = BoolOf(Get(cm, RadialSpecs.MenuIsOpen));
            }
            catch { }
            e.MapCapable = Capabilities.Contains("radial_map");
            return e;
        }

        /// <summary>
        /// What lies under the cursor, by one Noesis hit test (the way the game's MouseOverManager does it): "3d" (nothing of the UI),
        /// "map" (inside a MapControl.MapBase; mapUnitIsSelected tells whether the icon under the cursor is the selected unit) or "ui".
        /// x,y in pixels, y from the top. null = cannot tell.
        /// </summary>
        internal string RadialProbe(double x, double y, int screenH, object selected, out bool mapUnitIsSelected)
        {
            object map; string hitVm;
            return RadialProbe(x, y, screenH, selected, out mapUnitIsSelected, out map, out hitVm);
        }

        /// <summary>As above; map = the MapBase found (view "map"), hitVm = type name of the first non-null DataContext up from the hit visual (diagnostics).</summary>
        internal string RadialProbe(double x, double y, int screenH, object selected, out bool mapUnitIsSelected, out object map, out string hitVm)
        {
            mapUnitIsSelected = false; map = null; hitVm = null;
            try
            {
                var vtc = TypeOf(RadialSpecs.TreeType);
                var root = GetStatic(RadialSpecs.MainViewInstance);
                if (vtc == null || root == null) return null;
                if (_radHit == null || _radParent == null)
                {
                    _radHit = Member(RadialSpecs.HitTest) as MethodInfo;
                    _radParent = Member(RadialSpecs.GetParent) as MethodInfo;
                }
                if (_radHit == null || _radParent == null) return null;
                var from = Member(CoreSpecs.PointFromScreen) as MethodInfo;
                if (from == null) return null;
                double k = NoesisPxScale > 0 ? NoesisPxScale : 1.0;
                var pt = Activator.CreateInstance(from.GetParameters()[0].ParameterType, new object[] { (float)(x * k), (float)(y * k) });
                var res = _radHit.Invoke(null, new[] { root, from.Invoke(root, new[] { pt }) });
                var v = res == null ? null : Get(res, RadialSpecs.HitVisual);
                if (v == null) return "3d";
                var pip = TypeOf(RadialSpecs.PipType);
                object unitVm = null;
                bool win = false;
                for (object o = v; o != null; o = _radParent.Invoke(null, new[] { o }))
                {
                    if (pip != null && pip.IsInstanceOfType(o)) return "3d";   // MouseOverManager does not count the PIP view as UI
                    if (!win && IsTypeNamed(o, RadialSpecs.WindowType.Type)) win = true;
                    if (unitVm == null && IsTypeNamed(o, RadialSpecs.MapUnitVmType.Type)) unitVm = o;
                    var dc = Get(o, RadialSpecs.DataContext);
                    if (hitVm == null && dc != null) hitVm = dc.GetType().Name;
                    if (unitVm == null && dc != null && IsTypeNamed(dc, RadialSpecs.MapUnitVmType.Type)) unitVm = dc;
                    if (IsTypeNamed(o, RadialSpecs.MapBaseType.Type))
                    {
                        if (win) return "ui";
                        map = o;
                        if (unitVm != null)
                        {
                            var veh = Get(unitVm, RadialSpecs.UnitOfViewModel);
                            var bo = Get(veh, RadialSpecs.VehicleBase);
                            mapUnitIsSelected = bo != null && ReferenceEquals(bo, selected);
                        }
                        return "map";
                    }
                }
                return "ui";
            }
            catch { return null; }
        }

        /// <summary>CameraManager.Main (the camera the game picks with), or null. Returned as object: GameApi stays Unity-free for the selfcheck.</summary>
        internal object RadialCamera()
        {
            try { return GetStatic(RadialSpecs.CameraMain); } catch { return null; }
        }

        /// <summary>The unit's point on this MapBase in screen pixels (y down): Position -> LocationToView -> PointToScreen (pixels, NoesisPxScale). false on any failure.</summary>
        internal bool RadialMapPoint(object map, object unit, int screenH, out double x, out double y)
        {
            x = y = 0;
            if (map == null || unit == null) return false;
            try
            {
                double lat, lon, h;
                if (!OwnPos(unit, out lat, out lon, out h)) return false;
                var mt = map.GetType();
                MethodInfo[] ms;
                if (!_radMapMethods.TryGetValue(mt, out ms))
                {
                    ms = new[] { Member(CoreSpecs.LocationToView) as MethodInfo, Member(CoreSpecs.PointToScreen) as MethodInfo, Member(MapProjectorSpecs.TryLocationToView) as MethodInfo };
                    _radMapMethods[mt] = ms;
                }
                if (ms[1] == null || (ms[0] == null && ms[2] == null)) return false;
                object vp;
                if (ms[2] != null)   // T-309: the globe map answers "behind the horizon" itself (false = hide the point)
                {
                    var args = new object[] { GeoObj(lat, lon), null };
                    object ok = ms[2].Invoke(map, args);
                    if (!(ok is bool) || !(bool)ok) return false;
                    vp = args[1];
                }
                else if (ms[0] != null) vp = ms[0].Invoke(map, new[] { GeoObj(lat, lon) });
                else return false;
                if (vp == null) return false;
                var sp = ms[1].Invoke(map, new[] { vp });
                if (sp == null) return false;
                double nx = D2(Get(sp, RadialSpecs.PointX)), ny = D2(Get(sp, RadialSpecs.PointY));
                RadialMath.MapPointFromNoesis(nx, ny, NoesisPxScale, out nx, out ny);
                if (double.IsNaN(nx) || double.IsNaN(ny) || double.IsInfinity(nx) || double.IsInfinity(ny)) return false;
                x = nx; y = ny;
                return true;
            }
            catch { return false; }
        }

        /// <summary>"free" | "taken" | "unknown" (rule: RadialMath.KeyVerdict): the fallback hotkey must not steal a game key.</summary>
        internal string RadialKeyCheck(string keyName, out string conflict)
        {
            conflict = null;
            try
            {
                List<string[]> all; List<string> names;
                if (!ReadKeyBindings(out all, out names)) return "unknown";
                return RadialMath.KeyVerdict(keyName, all, names, out conflict);
            }
            catch { return "unknown"; }
        }

        private static bool IsTypeNamed(object o, string fullName)
        {
            for (var t = o == null ? null : o.GetType(); t != null; t = t.BaseType) if (t.FullName == fullName) return true;
            return false;
        }
    }
}
