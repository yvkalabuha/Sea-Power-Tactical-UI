// T-112 §3.6 / T-148 C4: the Unity shell of the CAP window. The frame (title, drag, collapse, width, hint line, mouse block) is UiWindowFrame, the controls are
// UiKit via UnityPainter, the whole body is CapWindowBody (pure, tested in capwintest). This class only wires the game: the view, the map (drawing a zone with
// the mouse), the selected ship and the commands. Texts are UiText keys (cap.*); nothing is hardcoded.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class CapWindowGui : ICapWindowEnv
    {
        private const int WindowId = 0x56420096;
        private readonly Func<CapView> _view;
        private readonly IAswMap _map;
        private readonly Action<bool> _block;
        private readonly Action<bool> _drawBlock;                   // the block of drawing on the map: an owner of its own ("cap.draw")
        private readonly Action<string> _log;
        private readonly Func<GeoPt?> _refPoint;                    // map reference (own ship / map centre) for the projection and the polar origin
        private readonly Func<int> _selectedShip;                   // uid of the ship selected in the game (0 = none): polar origin and anchor
        private readonly Func<string, IList<string>> _loadoutNames; // type -> CAP loadout names of the game (null = only "auto")
        private readonly Func<string, string, string> _loadoutLabel; // (type, name) -> the game's display name of the loadout (null = the name)
        private readonly Func<string, string, double> _loadoutReady; // T-175: (type, name) -> minutes to prepare it on the deck (< 0 = unknown)
        private readonly Action<bool> _resolveRestore;              // RestorePending: true = restore groups from the save
        private readonly Func<int, string> _shipName;               // uid -> ship name of the game (null / empty = "#uid")
        /// <summary>T-179: the own ships a group can protect (wired by the feature from the ASW course targets); null = none.</summary>
        public Func<IList<AswTargetOption>> OwnShipsFn { get; set; }
        private int _titleRev = -1;
        private readonly CapWindowBody _body;
        private UiWindowFrame _frame;                               // created on first use: the title needs the loaded texts
        private readonly UiMapDrawInput _in = new UiMapDrawInput();
        private bool _dragBlock, _visible, _errLogged;
        private readonly ZoneHandleDrag _drag = new ZoneHandleDrag();               // T-178 C8 / fix 2026-10-22: hover -> drag -> release of a zone handle
        private double _editLat, _editLon;                                           // geo point of the mouse at the start of a handle drag
        private double[] _geoPx;                                                     // this frame's geo -> pixel map (local units of the map), null = none
        private CapView _last = new CapView();

        public bool Visible
        {
            get { return _visible; }
            set { _visible = value; if (_frame != null) _frame.Visible = value; if (!value) Release(); }
        }

        /// <summary>Centres the tactical map on a unit and selects it (a click on an aircraft / ship name); null = done, else an error code. Wired by the feature (GameApi.FocusOwnUnit).</summary>
        public Func<int, string> FocusUnit { get; set; }

        private static string Tx(string key, params object[] args) { return UiText.Fmt(key, args); }

        public CapWindowGui(Func<CapView> view, Func<string, Dictionary<string, object>, string> send, IAswMap map, Action<bool> block, Action<string> log,
                            Func<GeoPt?> refPoint, Func<int> selectedShip, Func<string, IList<string>> loadoutNames, Action<bool> resolveRestore,
                            Func<int, string> shipName = null, Action<bool> drawBlock = null, Func<string, string, string> loadoutLabel = null, Func<string, string, double> loadoutReady = null)
        {
            _view = view; _map = map; _block = block ?? (b => { }); _drawBlock = drawBlock ?? _block; _log = log ?? (m => { });
            _refPoint = refPoint ?? (() => null); _selectedShip = selectedShip ?? (() => 0); _loadoutNames = loadoutNames; _resolveRestore = resolveRestore; _shipName = shipName; _loadoutLabel = loadoutLabel; _loadoutReady = loadoutReady;
            _body = new CapWindowBody(send, this);
        }

        // ---- ICapWindowEnv ----
        public int SelectedShip() { return _selectedShip(); }
        public string ShipName(int uid)
        {
            string n = null;
            try { n = _shipName == null ? null : _shipName(uid); } catch { }
            return string.IsNullOrEmpty(n) ? "#" + uid.ToString(System.Globalization.CultureInfo.InvariantCulture) : n;
        }
        public GeoPt? RefPoint() { return _refPoint(); }
        public List<ShipItem> OwnShips(List<double[]> geo)
        {
            var res = new List<ShipItem>();
            IList<AswTargetOption> src = null;
            try { src = OwnShipsFn == null ? null : OwnShipsFn(); } catch { }
            if (src == null) return res;
            var rp = _refPoint();
            foreach (var o in src)
            {
                if ((o.Kind ?? "ship") != "ship") continue;
                double dist = rp.HasValue && o.HasPos ? AswMath.DistNm(rp.Value, new GeoPt(o.Lat, o.Lon)) : double.MaxValue;
                res.Add(new ShipItem { Uid = o.Uid, Name = o.Name, Icon = "ntds:Surface:Own", DistNm = dist, Own = true });
                geo.Add(o.HasPos ? new[] { o.Lat, o.Lon } : null);
            }
            return res;
        }
        public IList<string> LoadoutNames(string type) { return _loadoutNames == null ? null : _loadoutNames(type); }
        public string LoadoutLabel(string type, string name) { try { return _loadoutLabel == null ? null : _loadoutLabel(type, name); } catch { return null; } }
        public double LoadoutReadyMin(string type, string name) { try { return _loadoutReady == null ? -1 : _loadoutReady(type, name); } catch { return -1; } }
        public void ResolveRestore(bool yes) { if (_resolveRestore != null) _resolveRestore(yes); }
        public string Focus(int uid)
        {
            var f = FocusUnit;
            if (f == null) return null;
            string code;
            try { code = f(uid); }
            catch (Exception e) { _log("cap focus failed: " + e.GetType().Name); return Tx("cap.say.focus_failed"); }
            if (code == null) return null;
            return code == "map_closed" ? Tx("overview.note.map_closed") : Tx("cap.say.focus_failed");
        }
        public double Now() { return UnityEngine.Time.unscaledTime; }
        /// <summary>T-178 C3: the course of an own ship (wired by the feature: GameApi.CapShipCourse); null = unknown or slower than 3 kn.</summary>
        public Func<int, double?> ShipCourseOf { get; set; }
        public double? ShipCourse(int uid) { try { return ShipCourseOf == null ? null : ShipCourseOf(uid); } catch { return null; } }
        /// <summary>T-178 I4: [Cap] MaxZone, the longest side of a zone the handles allow (miles).</summary>
        public double MaxZoneNm { set { if (value >= ZoneEditMath.MinNm) _body.MaxZoneNm = value; } }
        /// <summary>T-178 I3 (dev command ui_undo): the same as pressing "Undo" on the card; null = nothing to undo.</summary>
        public string PressUndo() { return _body.PressUndo(); }
        /// <summary>Outside a mission: no snapshot survives.</summary>
        public void ResetUndo() { _body.ResetUndo(); }
        public void Log(string message) { _log(message); }

        private void EnsureFrame()
        {
            if (_frame != null) return;
            _frame = new UiWindowFrame(WindowId, "cap", Tx("cap.title"), 500f, 420f, 760f, _block, _log);
            _frame.Visible = _visible;
            _titleRev = UiText.Revision;
        }

        /// <summary>The game language changed (UiText.Revision): the frame title is read again.</summary>
        private void RefreshTitle()
        {
            if (_titleRev == UiText.Revision) return;
            _titleRev = UiText.Revision;
            _frame.Title = Tx("cap.title");
        }

        public void Toggle() { Visible = !Visible; }

        private void Release()
        {
            if (_drag.Dragging) { try { _body.EndEdit(false); } catch { } }
            _drag.Reset();
            try { _body.CancelShipPick("closed"); } catch { }
            EndDragBlock();
            UiMouseBlock.Arm("cap.draw", false);
            UiEscGuard.Set("cap.mode", false, UnityEngine.Time.frameCount);   // T-199
            _in.Reset();
            if (_frame != null) _frame.Release();
        }

        public void Dispose() { Release(); }

        public void Update()
        {
            try
            {
                EnsureFrame();
                if (_frame.TakeCloseRequest()) Visible = false;   // [x] of the frame (T-154 A1): the owner closes, the frame does not touch Visible
                _frame.Visible = _visible;
                if (!_visible) { Release(); return; }
                _frame.Update();
                UpdateDraw();
                // T-199: a pick / draw / handle mode owns the Esc key (its own cancel runs above; the game's pause menu must not open with it)
                UiEscGuard.Set("cap.mode", _in.Phase != DrawPhase.Idle || _body.WantRect || _body.WantShipPick || _drag.Dragging, UnityEngine.Time.frameCount);
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("cap window update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>T-154 A2/A4: drawing the zone through UiMapDrawInput. The block is on from the moment the mode is armed (before the press) until a frame after the release.</summary>
        private void UpdateDraw()
        {
            if (_body.WantRect && _in.Phase == DrawPhase.Idle) _in.Arm();
            else if (!_body.WantRect && _in.Phase == DrawPhase.Armed) _in.Cancel();
            // T-178 C8: RMB cancels a drawing mode (a handle drag: in UpdateEdit); the previous zone stays
            bool rmb = UnityEngine.Input.GetMouseButtonDown(1);
            if (rmb && !_drag.Dragging && _in.RightClick()) _body.CancelInput();
            if (rmb && _body.WantShipPick) _body.CancelShipPick("rmb");
            UpdatePick();
            UpdateEdit(rmb);
            if (_in.Phase != DrawPhase.Idle)
            {
                double mx = UnityEngine.Input.mousePosition.x, my = UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y;
                bool inside = _frame.Rect.Contains(new UnityEngine.Vector2((float)mx, (float)my));
                string ev = _in.Tick(inside, UnityEngine.Input.GetMouseButton(0), UnityEngine.Input.GetMouseButtonDown(0), UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) && !_frame.ListOpen, mx, my);   // Esc closes an open list first
                if (ev == "cancel") _body.CancelInput();
                else if (ev == "finish") FinishRect();
            }
            // one block for both: from the hover / the armed mode (before the press) until a frame after the release
            UiMouseBlock.Arm("cap.draw", _in.Phase != DrawPhase.Idle || _drag.WantBlock || _body.WantShipPick);
            SetDragBlock(_in.WantBlock || _drag.WantBlock || _body.WantShipPick);
        }

        // ---- T-178 C8: handles of the draft / current zone (ZoneEditMath, ZoneHandleDrag). Drawing, hit test and drag all use the map's local units
        // (TryPixelToGeo, the space the zone itself is drawn in): fix 2026-10-22, the handles were drawn with the screen projection and hit through the
        // local units, so they could be hit where they were not seen.

        private bool MouseGeo(double mx, double my, GeoPt pt, out double la, out double lo)
        {
            la = lo = 0;
            return _map != null && _map.TryPixelToGeo(mx, my, pt.Lat, pt.Lon, out la, out lo);
        }

        /// <summary>This frame's geo -> pixel map from three samples around the screen centre; null = no map.</summary>
        private double[] GeoPx(GeoPt pt)
        {
            double x0 = UnityEngine.Screen.width / 2.0, y0 = UnityEngine.Screen.height / 2.0, la, lo, la2, lo2, la3, lo3;
            if (!MouseGeo(x0, y0, pt, out la, out lo) || !MouseGeo(x0 + 100, y0, pt, out la2, out lo2) || !MouseGeo(x0, y0 + 100, pt, out la3, out lo3)) return null;
            return ZoneHandleDrag.GeoToPx(x0, y0, 100, new[] { la, lo }, new[] { la2, lo2 }, new[] { la3, lo3 });
        }

        private UnityEngine.Vector2? _pickHoverXY;

        /// <summary>T-179: the map pick of the ship to protect (UiMapPick): the nearest own ship within 24 px is ringed, a left click outside the window assigns it.</summary>
        private void UpdatePick()
        {
            _pickHoverXY = null;
            if (!_body.WantShipPick) return;
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) && !_frame.ListOpen) { _body.CancelShipPick("esc"); return; }
            var pt = _refPoint();
            if (!pt.HasValue) return;
            double mx = UnityEngine.Input.mousePosition.x, my = UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y;
            var aff = GeoPx(pt.Value);
            if (aff == null) return;
            var items = _body.PickItems; var geo = _body.PickGeo;
            var xy = new List<float[]>();
            for (int i = 0; i < items.Count; i++)
            {
                var g = i < geo.Count ? geo[i] : null;
                xy.Add(g == null ? null : ZoneHandleDrag.Px(aff, g[0], g[1]));
            }
            if (_frame.Rect.Contains(new UnityEngine.Vector2((float)mx, (float)my))) return;
            int hover = _body.MapPick.Hover(items, xy, (float)mx, (float)my);
            for (int i = 0; i < items.Count; i++) if (items[i].Uid == hover && xy[i] != null) _pickHoverXY = new UnityEngine.Vector2(xy[i][0], xy[i][1]);
            if (UnityEngine.Input.GetMouseButtonDown(0))
            {
                int uid = _body.MapPick.OnClick(items, xy, (float)mx, (float)my);
                if (uid != 0) _body.OnShipPicked(uid);
            }
        }

        private void DrawPick()
        {
            if (UnityEngine.Event.current == null || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint || !_body.WantShipPick) return;
            int oldDepth = UnityEngine.GUI.depth;
            UnityEngine.GUI.depth = -1001;
            if (_pickHoverXY.HasValue) UnityEngine.GUI.Box(new UnityEngine.Rect(_pickHoverXY.Value.x - 14f, _pickHoverXY.Value.y - 14f, 28f, 28f), "");
            var mp = UnityEngine.Input.mousePosition;
            UnityEngine.GUI.Label(new UnityEngine.Rect(mp.x + 16f, UnityEngine.Screen.height - mp.y + 12f, 420f, 20f), Tx("ui.ship.hint"));
            UnityEngine.GUI.depth = oldDepth;
        }

        private void UpdateEdit(bool rmb)
        {
            double mx = UnityEngine.Input.mousePosition.x, my = UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y;
            var pt = _refPoint();
            var z = _body.EditableZone;
            bool enabled = _in.Phase == DrawPhase.Idle && !_body.WantRect && z != null && pt.HasValue;
            _geoPx = enabled ? GeoPx(pt.Value) : null;
            if (_geoPx == null) enabled = false;
            var hit = ZoneHandle.None;
            if (enabled && !_frame.Rect.Contains(new UnityEngine.Vector2((float)mx, (float)my)))
                hit = ZoneEditMath.Hit(ZoneHandleDrag.RectPx(_geoPx, z), (float)mx, (float)my, ZoneHandleDrag.HitPx);
            bool down = UnityEngine.Input.GetMouseButton(0);
            bool esc = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) && !_frame.ListOpen;
            string ev = _drag.Tick(hit, down, UnityEngine.Input.GetMouseButtonDown(0), esc, rmb, UnityEngine.Application.isFocused, enabled || _drag.Dragging && _in.Phase == DrawPhase.Idle && !_body.WantRect);
            double la, lo;
            if (ev == "begin")
            {
                if (!pt.HasValue || !MouseGeo(mx, my, pt.Value, out la, out lo)) { _drag.Reset(); return; }
                _body.BeginEdit();
                if (!_body.Editing) { _drag.Reset(); return; }
                _editLat = la; _editLon = lo;
                return;
            }
            if (ev == "end" || ev == "cancel")
            {
                bool keep = ev == "end";
                _log("cap zone handle " + (keep ? "released" : "cancelled"));
                _body.EndEdit(keep);
                return;
            }
            if (_drag.Dragging && pt.HasValue && MouseGeo(mx, my, pt.Value, out la, out lo))
                _body.EditZone(_drag.Handle, (lo - _editLon) * 60.0 * ZoneEditMath.LonK(_editLat, AswMath.WorldGlobe), (la - _editLat) * 60.0);
        }

        private void DrawHandles()
        {
            if (UnityEngine.Event.current == null || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint) return;
            var z = _body.EditableZone;
            var r = ZoneHandleDrag.RectPx(_geoPx, z);
            if (r == null) return;
            float x0 = r[0], y0 = r[1], x1 = r[2], y1 = r[3], cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f, h = ZoneHandleDrag.DrawPx;
            int oldDepth = UnityEngine.GUI.depth;
            UnityEngine.GUI.depth = -1001;
            var pts = new[] { ZoneHandle.NW, ZoneHandle.NE, ZoneHandle.SE, ZoneHandle.SW, ZoneHandle.N, ZoneHandle.S, ZoneHandle.W, ZoneHandle.E };
            var xy = new[] { new UnityEngine.Vector2(x0, y0), new UnityEngine.Vector2(x1, y0), new UnityEngine.Vector2(x1, y1), new UnityEngine.Vector2(x0, y1),
                             new UnityEngine.Vector2(cx, y0), new UnityEngine.Vector2(cx, y1), new UnityEngine.Vector2(x0, cy), new UnityEngine.Vector2(x1, cy) };
            for (int i = 0; i < xy.Length; i++)
            {
                float hh = pts[i] == _drag.Handle ? h + 4f : h;   // the hovered / dragged handle is larger
                UnityEngine.GUI.Box(new UnityEngine.Rect(xy[i].x - hh / 2f, xy[i].y - hh / 2f, hh, hh), "");
            }
            UnityEngine.GUI.Label(new UnityEngine.Rect(x0, y0 - 20f, 360f, 20f), _body.EditableLabel(z));
            UnityEngine.GUI.depth = oldDepth;
        }

        private void SetDragBlock(bool on)
        {
            if (on == _dragBlock) return;
            _dragBlock = on;
            try { _drawBlock(on); } catch { }
        }

        private void EndDragBlock() { SetDragBlock(false); }

        private void FinishRect()
        {
            double[] r;
            if (!CapWindowModel.NormalizeRect(_in.StartX, _in.StartY, _in.EndX, _in.EndY, 12, out r)) { _body.CancelInput(); _frame.Say(Tx("cap.ui.too_small"), UiTone.Error); return; }
            var pt = _refPoint();
            if (_map == null || !pt.HasValue) { _body.CancelInput(); _frame.Say(Tx("cap.ui.no_map"), UiTone.Error); return; }
            var zone = PixelRectToZone(r);
            if (zone == null) { _body.CancelInput(); _frame.Say(Tx("cap.ui.no_map"), UiTone.Error); return; }
            _body.OnRect(zone);   // into the draft: the order goes out with "Start" / "Apply"
        }

        /// <summary>A pixel rectangle {x0, y0, x1, y1} of the screen as a zone {lat0, lon0, lat1, lon1}; null = no map or no reference point.</summary>
        private double[] PixelRectToZone(double[] r)
        {
            var pt = _refPoint();
            if (_map == null || !pt.HasValue) return null;
            // mouse input goes through the map's local units (T-142 W4), not the screen projection
            var corners = AswWindowModel.RectToGeo((x, y) => { double la, lo; return _map.TryPixelToGeo(x, y, pt.Value.Lat, pt.Value.Lon, out la, out lo) ? new[] { la, lo } : null; }, r);
            if (corners == null) return null;
            return new[] { Math.Min(Math.Min(corners[0], corners[2]), Math.Min(corners[4], corners[6])), Math.Min(Math.Min(corners[1], corners[3]), Math.Min(corners[5], corners[7])),
                           Math.Max(Math.Max(corners[0], corners[2]), Math.Max(corners[4], corners[6])), Math.Max(Math.Max(corners[1], corners[3]), Math.Max(corners[5], corners[7])) };
        }

        public void OnGUI()
        {
            if (!_visible) return;
            try
            {
                EnsureFrame();
                if (_frame.TakeCloseRequest()) { Visible = false; return; }
                RefreshTitle();
                _frame.Visible = _visible;
                var v = _view() ?? new CapView();
                _last = v;
                _frame.OnGUI(k => _body.Draw(k, _last), () => _body.Summary(_last), _body.Warnings(v));
                SyncNote();
                if (!_visible) return;
                if (_in.Phase == DrawPhase.Dragging && UnityEngine.Event.current != null && UnityEngine.Event.current.type == UnityEngine.EventType.Repaint)
                {
                    double[] rr;
                    CapWindowModel.NormalizeRect(_in.StartX, _in.StartY, _in.EndX, _in.EndY, 0, out rr);
                    int oldDepth = UnityEngine.GUI.depth;
                    UnityEngine.GUI.depth = -1001;
                    UnityEngine.GUI.Box(new UnityEngine.Rect((float)rr[0], (float)rr[1], (float)(rr[2] - rr[0]), (float)(rr[3] - rr[1])), "");
                    var ghost = PixelRectToZone(rr);   // T-178 C8: the size of the zone being drawn
                    if (ghost != null) UnityEngine.GUI.Label(new UnityEngine.Rect((float)rr[0], (float)rr[1] - 20f, 220f, 20f), CapWindowBody.SizeLabel(ghost));
                    UnityEngine.GUI.depth = oldDepth;
                }
                if (_in.Phase == DrawPhase.Idle) DrawHandles();
                DrawPick();
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("cap window OnGUI failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>T-179: the group selected in the window (the overlay draws the circles of an alert group for it).</summary>
        public string SelectedGroup { get { return _visible ? _body.SelectedGroup : null; } }

        /// <summary>The zone being composed for the selected group (or null): drawn dashed on the map by CapOverlay.</summary>
        public double[] DraftZone { get { return _visible ? _body.DraftZone : null; } }

        private string _sentNote;
        private double _sentUntil;

        /// <summary>The body keeps its own answer line; the frame shows it in the hint line.</summary>
        private void SyncNote()
        {
            if (_body.Note == null || (_body.Note == _sentNote && _body.NoteUntil == _sentUntil)) return;
            _sentNote = _body.Note; _sentUntil = _body.NoteUntil;
            _frame.Say(_body.Note, _body.NoteTone);
        }
    }
}
