using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// Unity part of the target pick (T-102): fresh left click after the mode opened (the click that chose the ring sector does not count),
    /// Esc or right button cancel, hint "pick a target" at the cursor. Click on the map -> GameApi.TrackAtMap, in 3D -> Physics.RaycastAll
    /// (the way InputHandler picks) -> GameApi.VehicleForObject. Not part of the offline selfcheck (Physics/IMGUI); see the live checks in the plan.
    /// </summary>
    internal sealed class SmartAttackPick
    {
        private const float ClickTolPx = 6f;
        private readonly GameApi _api;
        private readonly System.Action<string> _log;
        private readonly SmartPickModel _model = new SmartPickModel();
        private bool _waitRelease, _down, _errLogged;
        private UnityEngine.Vector2 _downPos;
        private string _hint;
        private float _hintUntil;
        private System.Type _objectBaseType;
        private UnityEngine.GUIStyle _style;

        public SmartAttackPick(GameApi api, System.Action<string> log) { _api = api; _log = log ?? (m => { }); }

        public bool Active { get { return _model.Active; } }

        /// <summary>Opens the mode for the given submarine. Called through GameApi.SmartPick (command smart_attack with pick:true).</summary>
        public void Begin(int subUid)
        {
            _model.Start(subUid, _api.Frame.MissionTimeS ?? 0);
            _waitRelease = UnityEngine.Input.GetMouseButton(0);   // the click that chose the sector is still held
            _down = false; _hint = null;
            _api.SmartAttackPickActive = true;
            GameApi.SmartPickBlocking = true;
        }

        /// <summary>The pick click must not reach the game (map selection / 3D switchToObject) in this frame and the two after it.</summary>
        private void Swallow() { GameApi.SmartPickSwallowUntilFrame = UnityEngine.Time.frameCount + 2; }

        private void UpdateBlocking()
        {
            GameApi.SmartPickBlocking = _api.SmartAttackPickActive || UnityEngine.Time.frameCount <= GameApi.SmartPickSwallowUntilFrame;
        }

        private void Toast(string text) { _hint = text; _hintUntil = UnityEngine.Time.unscaledTime + 5f; }

        private static string Tx(string key) { return UiText.Get(key); }

        private void End(string why)
        {
            _api.SmartAttackPickActive = false;
            _down = false;
            Swallow();
            if (why != null)
            {
                string txt = Tx("smart.pick.cancelled");
                _api.SmartAttackMessage(txt); Toast(txt); _log("smart attack pick: " + why);
            }
        }

        public void Update()
        {
            UiEscGuard.Set("smart_pick", _model.Active, UnityEngine.Time.frameCount);   // T-199: Esc cancels the pick, not the game's pause menu
            if (!_model.Active) { if (_api.SmartAttackPickActive) End(null); UpdateBlocking(); ShowPendingToast(); return; }
            UpdateBlocking();
            try
            {
                string tick = _model.Tick(_api.Frame.MissionTimeS ?? 0);
                if (tick != null) { End(tick); return; }
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape) || UnityEngine.Input.GetMouseButtonDown(1))
                {
                    End(_model.OnEsc());
                    return;
                }
                if (_waitRelease) { if (!UnityEngine.Input.GetMouseButton(0)) _waitRelease = false; return; }
                var mp = UnityEngine.Input.mousePosition;
                if (UnityEngine.Input.GetMouseButtonDown(0))
                {
                    _down = !ModUiRects.Contains(mp.x, UnityEngine.Screen.height - mp.y);
                    _downPos = new UnityEngine.Vector2(mp.x, mp.y);
                }
                else if (_down && UnityEngine.Input.GetMouseButtonUp(0))
                {
                    _down = false;
                    if ((new UnityEngine.Vector2(mp.x, mp.y) - _downPos).magnitude <= ClickTolPx) Click(_downPos.x, _downPos.y);
                }
            }
            catch (System.Exception e)
            {
                try { _model.OnEsc(); End(null); } catch { }
                if (!_errLogged) { _errLogged = true; _log("smart attack pick failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void ShowPendingToast()
        {
            string t = _api.SmartAttackToast;
            if (t == null) return;
            _api.SmartAttackToast = null;
            Toast(t);
        }

        private void Click(float px, float pyBottom)
        {
            int h = UnityEngine.Screen.height;
            double topY = h - pyBottom;
            bool dummy;
            string view = _api.RadialProbe(px, topY, h, null, out dummy);
            object vehicle;
            if (view == "map") vehicle = _api.TrackAtMap(px, topY, h);
            else if (view == "3d") { if (!Ray3D(px, pyBottom, out vehicle)) return; }
            else return;   // over the game UI: not a pick
            int uid = _model.SubUid;
            TrackInfo ti = vehicle == null ? null : _api.SmartTrackInfo(vehicle);
            string detail = null;
            string allow = ti == null ? null : _api.SmartPickCheck(uid, ti.Id, out detail);
            _model.AllowDetail = detail;
            int? target;
            string r = _model.OnClick(ti, allow, out target);
            if (r == "picked" && target.HasValue)
            {
                _api.SmartAttackPickActive = false;
                Swallow(); UpdateBlocking();
                _log("smart attack pick: track " + target.Value);
                _api.SmartAttackStartPicked(uid, target.Value);
            }
            else if (r == "hint:refused")
            {
                _log("smart attack pick refused: track " + (ti == null ? 0 : ti.Id) + " code=" + allow + " detail=" + (detail ?? ""));
                Toast(_model.LastRefusal);
            }
            else if (r != null && r.StartsWith("hint:"))
            {
                Toast(SmartPickModel.HintText(r));
            }
        }

        /// <summary>The game's pick (InputHandler): RaycastAll(CameraManager.Main.ScreenPointToRay, 1000, -131073). false = nothing selectable under the cursor.</summary>
        private bool Ray3D(float px, float pyBottom, out object vehicle)
        {
            vehicle = null;
            var cam = _api.RadialCamera() as UnityEngine.Camera;
            if (cam == null) return false;
            if (_objectBaseType == null) _objectBaseType = _api.TypeOf(CoreSpecs.ObjectBaseType);
            if (_objectBaseType == null) return false;
            var hits = UnityEngine.Physics.RaycastAll(cam.ScreenPointToRay(new UnityEngine.Vector3(px, pyBottom, 0)), 1000f, -131073);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            object sub = _api.FindOwnUnit(_model.SubUid);
            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;
                var ob = hit.collider.GetComponentInParent(_objectBaseType);
                if (ob == null) continue;
                vehicle = _api.VehicleForObject(ob, sub);
                return true;   // null vehicle = a unit without a track of ours: hint no_track
            }
            return false;
        }

        public void OnGUI()
        {
            bool toast = _hint != null && UnityEngine.Time.unscaledTime < _hintUntil;
            if (!_model.Active && !toast) return;
            try
            {
                if (_style == null)
                {
                    _style = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.box) { fontSize = 13, alignment = UnityEngine.TextAnchor.MiddleLeft };
                    _style.normal.textColor = new UnityEngine.Color(1f, 0.9f, 0.5f);
                }
                var mp = UnityEngine.Event.current != null ? UnityEngine.Event.current.mousePosition : new UnityEngine.Vector2(UnityEngine.Input.mousePosition.x, UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y);
                string text = toast ? _hint : Tx("smart.pick.hint");
                int oldDepth = UnityEngine.GUI.depth;
                UnityEngine.GUI.depth = -1001;
                UnityEngine.GUI.Box(new UnityEngine.Rect(mp.x + 18, mp.y + 14, 330, 24), text, _style);
                UnityEngine.GUI.depth = oldDepth;
            }
            catch { }
        }
    }
}
