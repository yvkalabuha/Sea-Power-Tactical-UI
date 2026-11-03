using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-120 M21 (ADR-0043 section 6): after a game update some functions may be off or reduced (UiDegradation). The player sees ONE line, once per
    /// game launch, on the first frame of the first mission: in the game's own alert list (MainGameViewModel.RaiseAlert), or, when that member is not
    /// there, as an IMGUI line at the top of the screen for 15 s. The text is in the language of the game (deg.* keys); the log has the same line.
    /// A function the player switched off in the config is not a degradation and is not in the line.
    /// </summary>
    internal sealed class DegradeNoticeFeature : UiFeatureBase
    {
        private const double ShowS = 15;
        private static bool _shown;   // once per process: a later mission in the same run does not repeat it

        private readonly GameApi _api;
        private DegradeView _view;
        private string _line;
        private double _until;
        private GUIStyle _style;

        public DegradeNoticeFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "notice"; } }
        public override string[] DependsOn { get { return new[] { "text" }; } }

        public override void Init(UiContext ctx) { _view = ctx.Get<DegradeView>(); }

        public override void Update(UiFrame frame)
        {
            if (_shown || _view == null || !_api.Frame.InMission) return;
            _shown = true;
            string line = UiDegradation.PlayerLine(_view.Current, k => UiText.Get(k), (k, a) => UiText.Fmt(k, a));
            if (line == null) return;
            Ctx.Host.LogWarning("notice: " + line);
            if (_api.RaiseGameAlert(line)) return;
            _line = line; _until = frame.RealTime + ShowS;
        }

        public override void OnGUI()
        {
            if (_line == null) return;
            if (Time.unscaledTime > _until) { _line = null; return; }
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            var r = new Rect(Screen.width * 0.2f, 8f, Screen.width * 0.6f, 44f);
            var saved = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.85f, 0.4f, 1f);
            GUI.Label(r, _line, _style);
            GUI.color = saved;
        }
    }
}
