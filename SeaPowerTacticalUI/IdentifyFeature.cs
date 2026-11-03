namespace VoiceBotBridge.Ui
{
    /// <summary>Identification status keeper (T-111): the save folder and the per-frame pump. The Harmony patches are installed by the harmony module.</summary>
    internal sealed class IdentifyFeature : UiFeatureBase
    {
        public IdentifyFeature(UiContext ctx) : base(ctx) { }

        public override string Id { get { return "identify"; } }

        public override void SelfCheck(ISelfCheck check) { check.Specs(IdentifySpecs.Set); }

        public override void Init(UiContext ctx)
        {
            ctx.Api.IdentifySaveDir = ctx.DataDir("identify");
        }

        public override void Update(UiFrame frame)
        {
            Ctx.Api.PumpIdentify();
        }
    }
}
