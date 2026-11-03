using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-175 (ADR-0075 item 7): the shared card stack. The service for the other modules is UiToastStackModel (also as IUiToasts): Show / Update / Close with a key.
    /// No config keys (the cards of a module follow its own Toast settings); the place of the stack is in [Ui] WindowState (id toast).</summary>
    internal sealed class ToastFeature : UiFeatureBase
    {
        private readonly GameApi _api;
        private UiToastStackModel _model;
        private UiToastStack _stack;

        public ToastFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        public override string Id { get { return "toast"; } }

        public override void Init(UiContext ctx)
        {
            try
            {
                _model = new UiToastStackModel();
                _stack = new UiToastStack(_model, b => _api.SetMouseBlock("toast", b), m => ctx.Host.LogInfo("[Toast] " + m));
                ctx.Provide<UiToastStackModel>(_model);
                ctx.Provide<IUiToasts>(_model);
                ctx.Mission.Subscribe(66, "toast", e => _model.CloseAll(), why => _model.CloseAll());   // a new mission or leaving it: the cards go
            }
            catch (Exception e) { ctx.Host.LogError("toast init crashed: " + e); _stack = null; }
        }

        public override void Update(UiFrame frame)
        {
            if (_stack != null) _stack.Update();
        }

        public override void OnGUI()
        {
            if (_stack != null) _stack.OnGUI();
        }

        public override void Dispose()
        {
            if (_stack != null) _stack.Release();
        }
    }
}
