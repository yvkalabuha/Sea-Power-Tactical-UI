using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-148 A6: the ASW / SAR window on the shared frame (UiWindowFrame) and control set (UiKit). The sections live in AswWindowBody (pure);
    /// this class keeps what needs Unity: the frame, the mouse input on the map (rectangle / point), the attack dialog and the mouse block.
    /// </summary>
    internal sealed class AswWindow
    {
        private const int WindowId = 0x56420095;
        private readonly Func<AswView> _view;
        private readonly Func<string, Dictionary<string, object>, string> _send;
        private readonly IAswMap _map;
        private readonly Action<bool> _block;
        private readonly Action<string> _log;
        private readonly UiWindowFrame _frame;
        private readonly AswWindowBody _body;
        private UnityEngine.GUIStyle _head;
        private bool _frameBlock, _extraBlock, _blockOn, _errLogged;
        private readonly UiMapDrawInput _in = new UiMapDrawInput();
        private string _inMode;   // what the armed input is for: asw_rect | sar_rect | sar_point
        private int _seenNote;
        private int _titleRev;
        private ZoneHandle _hover = ZoneHandle.None, _editHandle = ZoneHandle.None;   // T-178 A5: the handle under the cursor / being dragged
        private double _editLat, _editLon;                                           // geo point of the mouse at the start of a handle drag
        private int _pickHover;                                                      // T-178 A2: the own ship ringed during the map pick (uid, 0 = none)
        private float[] _pickHoverXY;
        private const float HandlePx = 14f;

        private static string Tx(string key, params object[] a) { return a == null || a.Length == 0 ? UiText.Get(key) : UiText.Fmt(key, a); }

        public bool Visible { get; set; }

        /// <summary>Centres the tactical map on a unit and selects it (a click on a list row); null = done, else an error code. Wired by the feature (GameApi.FocusOwnUnit).</summary>
        public Func<int, string> FocusUnit { get; set; }

        /// <summary>The body: the sockets of other tasks (contacts, loadout, deck wait) get their services through it.</summary>
        internal AswWindowBody Body { get { return _body; } }

        private sealed class Env : IAswWindowEnv
        {
            private readonly AswWindow _w;
            public Env(AswWindow w) { _w = w; }
            public double Now() { return UnityEngine.Time.unscaledTime; }
            public void Log(string m) { _w._log(m); }
            public string Focus(int uid)
            {
                var f = _w.FocusUnit;
                if (f == null) return null;
                string code;
                try { code = f(uid); }
                catch (Exception e) { _w._log("asw focus failed: " + e.GetType().Name); return Tx("asw.say.focus_failed"); }
                if (code == null) return null;
                return code == "map_closed" ? Tx("overview.note.map_closed") : Tx("asw.say.focus_failed");
            }
        }

        public AswWindow(Func<AswView> view, Func<string, Dictionary<string, object>, string> send, IAswMap map, Action<bool> block, Action<string> log)
        {
            _view = view; _send = send; _map = map; _block = block ?? (b => { }); _log = log ?? (m => { });
            _frame = new UiWindowFrame(WindowId, "asw", Tx("asw.title"), 520f, 440f, 800f, b => { _frameBlock = b; ApplyBlock(); }, _log);
            _body = new AswWindowBody(_send, new Env(this));
        }

        /// <summary>Ring / menu "ASW for this ship's group": preselect the first ASW group whose target carries this name (no match = the window keeps its choice).</summary>
        public bool SelectByTarget(string targetName) { return _body.SelectByTarget(_view(), targetName); }

        public void Toggle() { Visible = !Visible; if (!Visible) Release(); }

        private void Release()
        {
            _frame.Visible = false;
            _frame.Release();
            _frameBlock = false; _extraBlock = false;
            UiMouseBlock.Arm("asw.draw", false);
            UiEscGuard.Set("asw.mode", false, UnityEngine.Time.frameCount);   // T-199
            ApplyBlock();
            ModUiRects.Clear("asw_dialog");
            _in.Reset(); _inMode = null;
            if (_editHandle != ZoneHandle.None) { try { _body.EndEdit(false); } catch { } }
            _editHandle = ZoneHandle.None; _hover = ZoneHandle.None; _pickHover = 0;
            _body.CancelInput(null);
            if (_body.TakePreviewShown()) { try { _send("asw_preview_clear", new Dictionary<string, object>()); } catch { } }
        }

        public void Dispose() { Release(); }

        /// <summary>The map block is on while the frame wants it, the dialog is under the mouse, a rectangle is dragged or a point is awaited.</summary>
        private void ApplyBlock()
        {
            bool want = _frameBlock || _extraBlock;
            if (want && !_blockOn) { _blockOn = true; try { _block(true); } catch { } }
            else if (!want && _blockOn) { _blockOn = false; try { _block(false); } catch { } }
        }

        private static bool MouseDown()
        {
            return UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2);
        }

        /// <summary>Per frame (Update): mouse block, drawing the area. Never throws.</summary>
        public void Update()
        {
            try
            {
                if (_frame.TakeCloseRequest()) { Visible = false; Release(); }   // [x] of the frame (T-154 A1): the owner closes
                ModUiRects.Clear("asw_dialog");   // T-173: there is no attack dialog any more (ADR-0074); a leftover zone of an older frame must not stay
                if (!Visible) { Release(); return; }
                _frame.Visible = Visible;
                _frame.Update();
                double mx = UnityEngine.Input.mousePosition.x, my = UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y;
                var r = _frame.Rect;
                bool inside = Visible && mx >= r.x && mx < r.x + r.width && my >= r.y && my < r.y + r.height;
                string want = Visible ? _body.WantInput : null;
                UpdateRightClick();
                UpdateDraw(want, inside, mx, my);
                UpdatePick(inside, mx, my);
                UpdateEdit(inside, mx, my);
                bool picking = _body.MapPick.Active && _body.WantInput == "ship_pick";
                _extraBlock = _in.WantBlock || picking || _editHandle != ZoneHandle.None;
                UiMouseBlock.Arm("asw.draw", _in.Phase != DrawPhase.Idle || picking || _hover != ZoneHandle.None || _editHandle != ZoneHandle.None);
                UiEscGuard.Set("asw.mode", _in.Phase != DrawPhase.Idle || picking || _editHandle != ZoneHandle.None, UnityEngine.Time.frameCount);   // T-199: a map mode owns the Esc key
                ApplyBlock();
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("asw window update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>T-178 A5: RMB ends a map mode without a change: a handle drag, the pick of a ship, the drawing of an area or the pointing of a SAR circle (the previous area stays).</summary>
        private void UpdateRightClick()
        {
            if (!UnityEngine.Input.GetMouseButtonDown(1)) return;
            if (_editHandle != ZoneHandle.None) { EndEditDrag(false); return; }
            if (_body.MapPick.Active && _body.WantInput == "ship_pick") { _body.CancelInput("asw.pick.cancelled"); return; }
            if (_in.RightClick()) { string mode = _inMode; if (mode != null) _body.CancelInput(mode == "sar_point" ? "sar.ui.cancelled" : "asw.say.draw_cancelled"); }
        }

        /// <summary>A pseudo-linear map around the cursor, in the local units of the map (like the drawing): lat/lon of the mouse and the degrees per pixel.</summary>
        private bool Linear(double mx, double my, AswView rv, out double la, out double lo, out double dLonX, out double dLatY)
        {
            la = lo = dLonX = dLatY = 0;
            double la2, lo2, la3, lo3;
            if (_map == null || rv == null) return false;
            if (!_map.TryPixelToGeo(mx, my, rv.RefLat, rv.RefLon, out la, out lo) || !_map.TryPixelToGeo(mx + 50, my, rv.RefLat, rv.RefLon, out la2, out lo2)
                || !_map.TryPixelToGeo(mx, my + 50, rv.RefLat, rv.RefLon, out la3, out lo3)) return false;
            dLonX = (lo2 - lo) / 50.0; dLatY = (la3 - la) / 50.0;
            return Math.Abs(dLonX) > 1e-12 && Math.Abs(dLatY) > 1e-12;
        }

        /// <summary>T-178 A2: the map pick of an own ship (UiMapPick): the nearest own ship within 24 px is ringed, a left click outside the window assigns it.</summary>
        private void UpdatePick(bool inside, double mx, double my)
        {
            _pickHover = 0; _pickHoverXY = null;
            if (!Visible || !_body.MapPick.Active || _body.WantInput != "ship_pick") return;
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) && !_frame.ListOpen) { _body.CancelInput("asw.pick.cancelled"); return; }
            var rv = _view();
            double la, lo, dLonX, dLatY;
            if (!Linear(mx, my, rv, out la, out lo, out dLonX, out dLatY)) return;
            var items = _body.PickItems; var geo = _body.PickGeo;
            var xy = new List<float[]>();
            for (int i = 0; i < items.Count; i++)
            {
                var g = i < geo.Count ? geo[i] : null;
                xy.Add(g == null ? null : new[] { (float)(mx + (g[1] - lo) / dLonX), (float)(my + (g[0] - la) / dLatY) });
            }
            if (inside) return;
            _pickHover = _body.MapPick.Hover(items, xy, (float)mx, (float)my);
            for (int i = 0; i < items.Count; i++) if (items[i].Uid == _pickHover) _pickHoverXY = xy[i];
            if (UnityEngine.Input.GetMouseButtonDown(0))
            {
                int uid = _body.MapPick.OnClick(items, xy, (float)mx, (float)my);
                if (uid != 0) _body.OnShipPicked(uid);
            }
        }

        // ---- T-178 A5: handles of the area being composed (ZoneEditMath); picking works in the local units around the cursor

        private ZoneHandle HandleAt(double mx, double my, AswView rv, out double la, out double lo)
        {
            la = lo = 0;
            double dLonX, dLatY;
            if (!Linear(mx, my, rv, out la, out lo, out dLonX, out dLatY)) return ZoneHandle.None;
            var z = _body.EditableRect;
            if (z != null)
            {
                var rect = new[] { (float)(mx + (z[1] - lo) / dLonX), (float)(my + (z[0] - la) / dLatY), (float)(mx + (z[3] - lo) / dLonX), (float)(my + (z[2] - la) / dLatY) };
                var h = ZoneEditMath.Hit(rect, (float)mx, (float)my, HandlePx);
                return h == ZoneHandle.Inside ? ZoneHandle.None : h;   // the inside belongs to the map (panning); only the 8 handles are ours
            }
            var c = _body.EditableCircle;
            if (c != null)
            {
                double cx = mx + (c[1] - lo) / dLonX, cy = my + (c[0] - la) / dLatY;
                double rx = cx + c[2] / (60.0 * ZoneEditMath.LonK(c[0], AswMath.WorldGlobe)) / dLonX;   // the radius handle: the east point of the circle
                if (Math.Abs(mx - rx) <= HandlePx / 2 && Math.Abs(my - cy) <= HandlePx / 2) return ZoneHandle.Radius;
                if (Math.Abs(mx - cx) <= HandlePx / 2 && Math.Abs(my - cy) <= HandlePx / 2) return ZoneHandle.Center;
            }
            return ZoneHandle.None;
        }

        private void UpdateEdit(bool inside, double mx, double my)
        {
            if (!Visible || _in.Phase != DrawPhase.Idle || _body.WantInput != null) { if (_editHandle != ZoneHandle.None) EndEditDrag(false); _hover = ZoneHandle.None; return; }
            var rv = _view();
            double la, lo;
            if (_editHandle != ZoneHandle.None)
            {
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) && !_frame.ListOpen) { EndEditDrag(false); return; }
                if (!UnityEngine.Input.GetMouseButton(0)) { EndEditDrag(true); return; }
                double dLonX, dLatY;
                if (Linear(mx, my, rv, out la, out lo, out dLonX, out dLatY))
                {
                    double dx = (lo - _editLon) * 60.0 * ZoneEditMath.LonK(_editLat, AswMath.WorldGlobe), dy = (la - _editLat) * 60.0;
                    if (_body.EditableCircle != null) _body.EditCircle(_editHandle, dx, dy); else _body.EditRect(_editHandle, dx, dy);
                }
                return;
            }
            _hover = ZoneHandle.None;
            if (inside || rv == null || (_body.EditableRect == null && _body.EditableCircle == null)) return;
            var h = HandleAt(mx, my, rv, out la, out lo);
            _hover = h;
            if (h != ZoneHandle.None && UnityEngine.Input.GetMouseButtonDown(0))
            {
                _body.BeginEdit();
                if (!_body.Editing) return;
                _editHandle = h; _editLat = la; _editLon = lo;
            }
        }

        private void EndEditDrag(bool keep)
        {
            _body.EndEdit(keep);
            _editHandle = ZoneHandle.None; _hover = ZoneHandle.None;
        }

        private void DrawHandles(AswView rv)
        {
            if (UnityEngine.Event.current == null || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint || _in.Phase != DrawPhase.Idle || _body.WantInput != null) return;
            double[] aff, rect;
            if (_map == null || rv == null || !_map.TryProjection(rv.RefLat, rv.RefLon, out aff, out rect)) return;
            Func<double, double, UnityEngine.Vector2> px = (la, lo) => new UnityEngine.Vector2((float)(aff[0] * la + aff[1] * lo + aff[2]), (float)(aff[3] * la + aff[4] * lo + aff[5]));
            var z = _body.EditableRect; var c = _body.EditableCircle;
            if (z == null && c == null) return;
            int oldDepth = UnityEngine.GUI.depth;
            UnityEngine.GUI.depth = -1001;
            if (z != null)
            {
                var a = px(z[0], z[1]); var b = px(z[2], z[3]);
                float x0 = Math.Min(a.x, b.x), x1 = Math.Max(a.x, b.x), y0 = Math.Min(a.y, b.y), y1 = Math.Max(a.y, b.y), cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f;
                foreach (var p in new[] { new UnityEngine.Vector2(x0, y0), new UnityEngine.Vector2(x1, y0), new UnityEngine.Vector2(x1, y1), new UnityEngine.Vector2(x0, y1),
                                          new UnityEngine.Vector2(cx, y0), new UnityEngine.Vector2(cx, y1), new UnityEngine.Vector2(x0, cy), new UnityEngine.Vector2(x1, cy) })
                    UnityEngine.GUI.Box(new UnityEngine.Rect(p.x - HandlePx / 2f, p.y - HandlePx / 2f, HandlePx, HandlePx), "");
                UnityEngine.GUI.Label(new UnityEngine.Rect(x0, y0 - 20f, 220f, 20f), AswWindowBody.SizeLabel(z));
            }
            else
            {
                var m = px(c[0], c[1]);
                var e = px(c[0], c[1] + c[2] / (60.0 * ZoneEditMath.LonK(c[0], AswMath.WorldGlobe)));
                UnityEngine.GUI.Box(new UnityEngine.Rect(m.x - HandlePx / 2f, m.y - HandlePx / 2f, HandlePx, HandlePx), "");
                UnityEngine.GUI.Box(new UnityEngine.Rect(e.x - HandlePx / 2f, e.y - HandlePx / 2f, HandlePx, HandlePx), "");
                UnityEngine.GUI.Label(new UnityEngine.Rect(m.x + 8f, m.y - 20f, 220f, 20f), UnitFmt.Nm(c[2], 0));
            }
            UnityEngine.GUI.depth = oldDepth;
        }

        private void DrawPick()
        {
            if (UnityEngine.Event.current == null || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint || !_body.MapPick.Active || _body.WantInput != "ship_pick") return;
            int oldDepth = UnityEngine.GUI.depth;
            UnityEngine.GUI.depth = -1001;
            if (_pickHoverXY != null) UnityEngine.GUI.Box(new UnityEngine.Rect(_pickHoverXY[0] - 14f, _pickHoverXY[1] - 14f, 28f, 28f), "");
            var mp = UnityEngine.Input.mousePosition;
            UnityEngine.GUI.Label(new UnityEngine.Rect(mp.x + 16f, UnityEngine.Screen.height - mp.y + 12f, 420f, 20f), Tx("ui.ship.hint"));
            UnityEngine.GUI.depth = oldDepth;
        }

        /// <summary>T-154 A2/A4: the map input (rectangle / point) through UiMapDrawInput: the block is on from the moment the mode is armed (before the press)
        /// until a frame after the release, so the map never starts to pan under the drag.</summary>
        private void UpdateDraw(string want, bool inside, double mx, double my)
        {
            bool mapInput = want == "asw_rect" || want == "sar_rect" || want == "sar_point";
            if (mapInput && _in.Phase == DrawPhase.Idle) { _in.Arm(); _inMode = want; }
            else if (!mapInput && _in.Phase == DrawPhase.Armed) { _in.Cancel(); _inMode = null; }
            else if (!mapInput && _in.Phase == DrawPhase.Dragging && _inMode != "sar_point") { _in.Cancel(); _inMode = null; }   // cancelled elsewhere (body / language)
            else if (mapInput && _in.Phase == DrawPhase.Armed) _inMode = want;
            if (_in.Phase == DrawPhase.Idle) { _inMode = null; return; }
            string ev = _in.Tick(inside, UnityEngine.Input.GetMouseButton(0), UnityEngine.Input.GetMouseButtonDown(0), UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) && !_frame.ListOpen, mx, my);   // Esc closes an open list first
            if (ev == null) return;
            string mode = _inMode;
            if (ev == "cancel") { if (mode != null) _body.CancelInput(mode == "sar_point" ? "sar.ui.cancelled" : "asw.say.draw_cancelled"); return; }
            if (mode == "sar_point") { if (ev == "start") PickPoint(mx, my); return; }
            if (ev == "finish") FinishRect();
        }

        private void PickPoint(double mx, double my)
        {
            var rv = _view();
            double lat, lon;
            if (_map == null || rv == null || !_map.TryPixelToGeo(mx, my, rv.RefLat, rv.RefLon, out lat, out lon)) { _body.CancelInput("sar.ui.no_map"); return; }
            _body.OnPoint(lat, lon);
        }

        private void FinishRect()
        {
            double[] r;
            if (!AswWindowModel.NormalizeRect(_in.StartX, _in.StartY, _in.EndX, _in.EndY, 12, out r)) { _body.CancelInput("asw.say.area_small"); return; }
            var rv = _view();
            if (_map == null || rv == null) { _body.CancelInput("sar.ui.no_map"); return; }
            // mouse input goes through the map's local units (T-142 W4), not the screen projection
            var geo = AswWindowModel.RectToGeo((x, y) => { double la, lo; return _map.TryPixelToGeo(x, y, rv.RefLat, rv.RefLon, out la, out lo) ? new[] { la, lo } : null; }, r);
            if (geo == null) { _body.CancelInput("sar.ui.no_map"); return; }
            _body.OnRect(geo);
        }

        private string GhostLabel(AswView rv, double[] rr)
        {
            if (_map == null || rv == null) return null;
            var geo = AswWindowModel.RectToGeo((x, y) => { double la, lo; return _map.TryPixelToGeo(x, y, rv.RefLat, rv.RefLon, out la, out lo) ? new[] { la, lo } : null; }, rr);
            if (geo == null) return null;
            double la0 = Math.Min(Math.Min(geo[0], geo[2]), Math.Min(geo[4], geo[6])), la1 = Math.Max(Math.Max(geo[0], geo[2]), Math.Max(geo[4], geo[6]));
            double lo0 = Math.Min(Math.Min(geo[1], geo[3]), Math.Min(geo[5], geo[7])), lo1 = Math.Max(Math.Max(geo[1], geo[3]), Math.Max(geo[5], geo[7]));
            return AswWindowBody.SizeLabel(new[] { la0, lo0, la1, lo1 });
        }

        private void EnsureStyles()
        {
            if (_head != null) return;
            _head = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label) { fontSize = 13, wordWrap = false, fontStyle = UnityEngine.FontStyle.Bold };
            _head.normal.textColor = new UnityEngine.Color(0.85f, 0.9f, 0.95f);
        }

        public void OnGUI()
        {
            if (_frame.TakeCloseRequest()) { Visible = false; Release(); }
            var v = _view();
            if (!Visible) return;
            try
            {
                EnsureStyles();
                int oldDepth = UnityEngine.GUI.depth;
                UnityEngine.GUI.depth = -1000;
                if (Visible)
                {
                    var view = v ?? new AswView();
                    _frame.Visible = true;
                    if (_titleRev != UiText.Revision) { _titleRev = UiText.Revision; _frame.Title = Tx("asw.title"); }   // language switch
                    _frame.OnGUI(k => DrawBody(k, view), () => _body.Summary(view), _body.Warnings(view));
                }
                if (_in.Phase == DrawPhase.Dragging && _inMode != "sar_point" && UnityEngine.Event.current != null && UnityEngine.Event.current.type == UnityEngine.EventType.Repaint)
                {
                    double[] rr;
                    AswWindowModel.NormalizeRect(_in.StartX, _in.StartY, _in.EndX, _in.EndY, 0, out rr);
                    UnityEngine.GUI.Box(new UnityEngine.Rect((float)rr[0], (float)rr[1], (float)(rr[2] - rr[0]), (float)(rr[3] - rr[1])), "");
                    var ghost = GhostLabel(v ?? new AswView(), rr);   // T-178 A5: the size of the area being drawn
                    if (ghost != null) UnityEngine.GUI.Label(new UnityEngine.Rect((float)rr[0], (float)rr[1] - 20f, 220f, 20f), ghost);
                }
                DrawHandles(v ?? new AswView());
                DrawPick();
                UnityEngine.GUI.depth = oldDepth;
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("asw window OnGUI failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void DrawBody(UiKit k, AswView view)
        {
            _body.Draw(k, view);
            if (_body.NoteSerial != _seenNote) { _seenNote = _body.NoteSerial; _frame.Say(_body.Note, _body.NoteTone); }
        }
    }
}
