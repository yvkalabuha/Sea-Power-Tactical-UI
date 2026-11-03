// T-341 phase B: the Unity shell of the strike window. The frame (title, drag, collapse, width, hint line, mouse block) is UiWindowFrame, the controls are UiKit through UnityPainter,
// the whole body is StrikeWindowBody (pure, tested in strikewintest). This class only wires the game: the view, the map pick of a hostile ship, the commands. Texts: UiText keys strike.*.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class StrikeWindowGui : IStrikeWindowEnv
    {
        private const int WindowId = 0x56420341;
        private readonly Func<StrikeView> _view;
        private readonly IAswMap _map;
        private readonly Action<bool> _block;
        private readonly Action<string> _log;
        private readonly Action<bool> _pickBlock;
        private readonly Func<GeoPt?> _refPoint;
        private readonly StrikeWindowBody _body;
        private readonly StrikeTargetPick _pick = new StrikeTargetPick();
        private UiWindowFrame _frame;
        private int _titleRev = -1;
        private bool _visible, _errLogged, _pickBlockOn;
        private StrikeView _last = new StrikeView();
        private UnityEngine.Vector2? _hoverXY;
        private string _sentNote;
        private double _sentUntil;

        /// <summary>Centres the map on an own unit (GameApi.FocusOwnUnit); null = done, else an error code.</summary>
        public Func<int, string> FocusUnitFn { get; set; }

        private static string Tx(string key, params object[] args) { return UiText.Fmt(key, args); }

        public StrikeWindowGui(Func<StrikeView> view, Func<string, Dictionary<string, object>, string> send, IAswMap map, Action<bool> block, Action<string> log, Func<GeoPt?> refPoint, Action<bool> pickBlock)
        {
            _view = view; _map = map; _block = block ?? (b => { }); _log = log ?? (m => { }); _refPoint = refPoint ?? (() => null); _pickBlock = pickBlock ?? _block;
            _body = new StrikeWindowBody(send, this);
        }

        public bool Visible
        {
            get { return _visible; }
            set { _visible = value; if (_frame != null) _frame.Visible = value; if (!value) Release(); }
        }

        public string SelectedGroup { get { return _body.SelectedGroup; } }

        // ---- IStrikeWindowEnv
        public string FocusUnit(int uid)
        {
            var f = FocusUnitFn;
            if (f == null) return null;
            try { return f(uid); } catch (Exception e) { _log("strike focus failed: " + e.GetType().Name); return "focus_failed"; }
        }
        public void StartPick() { _pick.Start(); }
        public void CancelPick(string why) { _pick.Cancel(why); }
        public bool PickActive { get { return _pick.Active; } }
        public double Now() { return UnityEngine.Time.unscaledTime; }

        private void EnsureFrame()
        {
            if (_frame != null) return;
            _frame = new UiWindowFrame(WindowId, "strike", Tx("strike.title"), 520f, 440f, 820f, _block, _log);
            _frame.Visible = _visible;
            _titleRev = UiText.Revision;
        }

        private void RefreshTitle()
        {
            if (_titleRev == UiText.Revision) return;
            _titleRev = UiText.Revision;
            _frame.Title = Tx("strike.title");
        }

        public void Toggle() { Visible = !Visible; }

        private void Release()
        {
            try { _pick.Cancel("closed"); } catch { }
            SetPickBlock(false);
            UiMouseBlock.Arm("strike.pick", false);
            UiEscGuard.Set("strike.mode", false, UnityEngine.Time.frameCount);
            _hoverXY = null;
            if (_frame != null) _frame.Release();
        }

        public void Dispose() { Release(); }

        public void Update()
        {
            try
            {
                EnsureFrame();
                if (_frame.TakeCloseRequest()) Visible = false;
                _frame.Visible = _visible;
                if (!_visible) { Release(); return; }
                _frame.Update();
                UpdatePick();
                UiEscGuard.Set("strike.mode", _pick.Active, UnityEngine.Time.frameCount);   // a map mode owns the Esc key
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("strike window update failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void SetPickBlock(bool on)
        {
            if (on == _pickBlockOn) return;
            _pickBlockOn = on;
            try { _pickBlock(on); } catch { }
        }

        /// <summary>The ships of the view on the screen (IMGUI pixels, y from the top) through the map projection; null = not on the map.</summary>
        private List<float[]> ShipPx(IList<StrikeShipRow> rows)
        {
            var res = new List<float[]>();
            var rp = _refPoint();
            double[] aff, rect;
            if (_map == null || !rp.HasValue || !_map.TryProjection(rp.Value.Lat, rp.Value.Lon, out aff, out rect)) return null;
            foreach (var s in rows)
            {
                if (s.Lat == 0 && s.Lon == 0) { res.Add(null); continue; }
                res.Add(new[] { (float)(aff[0] * s.Lat + aff[1] * s.Lon + aff[2]), (float)(aff[3] * s.Lat + aff[4] * s.Lon + aff[5]) });
            }
            return res;
        }

        private void UpdatePick()
        {
            _hoverXY = null;
            if (!_pick.Active) { SetPickBlock(false); UiMouseBlock.Arm("strike.pick", false); return; }
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) && !_frame.ListOpen) { _pick.Cancel("esc"); return; }
            if (UnityEngine.Input.GetMouseButtonDown(1)) { _pick.Cancel("rmb"); return; }
            UiMouseBlock.Arm("strike.pick", true);
            SetPickBlock(true);
            var v = _view();
            double mx = UnityEngine.Input.mousePosition.x, my = UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y;
            if (v == null || _frame.Rect.Contains(new UnityEngine.Vector2((float)mx, (float)my))) return;
            var xy = ShipPx(v.Ships);
            if (xy == null) return;
            int hover = _pick.Hover(v.Ships, xy, (float)mx, (float)my);
            for (int i = 0; i < v.Ships.Count; i++) if (v.Ships[i].Id == hover && xy[i] != null) _hoverXY = new UnityEngine.Vector2(xy[i][0], xy[i][1]);
            if (UnityEngine.Input.GetMouseButtonDown(0))
            {
                int id = _pick.OnClick(v.Ships, xy, (float)mx, (float)my);
                if (id != 0) _body.OnShipPicked(id, v);
            }
        }

        private void DrawPick()
        {
            if (UnityEngine.Event.current == null || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint || !_pick.Active) return;
            int oldDepth = UnityEngine.GUI.depth;
            UnityEngine.GUI.depth = -1001;
            if (_hoverXY.HasValue) UnityEngine.GUI.Box(new UnityEngine.Rect(_hoverXY.Value.x - 14f, _hoverXY.Value.y - 14f, 28f, 28f), "");
            var mp = UnityEngine.Input.mousePosition;
            UnityEngine.GUI.Label(new UnityEngine.Rect(mp.x + 16f, UnityEngine.Screen.height - mp.y + 12f, 420f, 20f), Tx("strike.pick.hint"));
            UnityEngine.GUI.depth = oldDepth;
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
                var v = _view() ?? new StrikeView();
                _last = v;
                _frame.OnGUI(k => _body.Draw(k, _last), () => _body.Summary(_last), _body.Warnings(_last));
                SyncNote();
                DrawPick();
            }
            catch (Exception e)
            {
                try { Release(); } catch { }
                if (!_errLogged) { _errLogged = true; _log("strike window OnGUI failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void SyncNote()
        {
            if (_body.Note == null || (_body.Note == _sentNote && _body.NoteUntil == _sentUntil)) return;
            _sentNote = _body.Note; _sentUntil = _body.NoteUntil;
            _frame.Say(_body.Note, _body.NoteTone);
        }
    }
}
