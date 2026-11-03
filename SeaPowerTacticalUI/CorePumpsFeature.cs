namespace VoiceBotBridge.Ui
{
    // The small per-frame pumps of GameApi that need no state of their own (T-118). One class per pump keeps the Update order
    // of the former UiPlugin.Update visible in UiFeatures.cs.

    /// <summary>Hands over CoreSpecs; the UI mod never presses Ignore (T-306).</summary>
    internal sealed class DialogsFeature : UiFeatureBase
    {
        public DialogsFeature(UiContext ctx) : base(ctx) { }
        public override string Id { get { return "dialogs"; } }

        /// <summary>The shared members of the registry (CoreSpecs) belong to no function; the first core pump hands them over.</summary>
        public override void SelfCheck(ISelfCheck check) { check.Specs(CoreSpecs.Set); }
    }

    /// <summary>The tactics presets and the fire veto pump.</summary>
    internal sealed class TacticsFeature : UiFeatureBase
    {
        public TacticsFeature(UiContext ctx) : base(ctx) { }
        public override string Id { get { return "tactics"; } }
        public override void Update(UiFrame frame) { Ctx.Api.PumpTactics(frame.RealTime); }
    }

    /// <summary>The selected-unit tracking.</summary>
    internal sealed class SelectionFeature : UiFeatureBase
    {
        public SelectionFeature(UiContext ctx) : base(ctx) { }
        public override string Id { get { return "selection"; } }
        public override void Update(UiFrame frame) { Ctx.Api.PumpSelection(); }
    }

    /// <summary>The map layer list entries (radar zone, Overview).</summary>
    internal sealed class MapLayerFeature : UiFeatureBase
    {
        public MapLayerFeature(UiContext ctx) : base(ctx) { }
        public override string Id { get { return "map_layer"; } }
        public override void Update(UiFrame frame) { Ctx.Api.PumpMapLayer(); }

        public override void SelfCheck(ISelfCheck check) { check.Specs(MapLayerSpecs.Set); }
    }

    /// <summary>Without the voice mod nobody listens: keep the event queue empty, log what matters (fire_vetoed, Harmony warning).</summary>
    internal sealed class DrainFeature : UiFeatureBase
    {
        public DrainFeature(UiContext ctx) : base(ctx) { }
        public override string Id { get { return "drain"; } }

        public override void Update(UiFrame frame)
        {
            if (Ctx.VoicePresent) return;   // with the voice mod it takes the events through link_events
            var evs = Ctx.Api.DrainEvents();
            if (evs == null) return;
            foreach (var e in evs)
                if (e.Contains("\"name\":\"fire_vetoed\"") || e.Contains("\"name\":\"harmony_unverified_version\"")) Ctx.Host.LogInfo(e);
        }
    }
}
