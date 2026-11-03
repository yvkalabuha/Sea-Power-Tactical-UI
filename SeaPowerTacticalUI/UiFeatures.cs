using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// The module list of the UI mod in registration order = Update / OnGUI / Dispose order (ADR-0044). Init runs in the order of DependsOn.
    /// The order reproduces the former UiPlugin.Update; do not reorder without a reason.
    /// </summary>
    internal static class UiFeatures
    {
        public static IList<IUiFeature> All(UiContext ctx)
        {
            var list = new List<IUiFeature>
            {
                new DialogsFeature(ctx),
                new TacticsFeature(ctx),
                new DoctrineFeature(ctx),
                new TextFeature(ctx),
                new DegradeNoticeFeature(ctx),
                new SelectionFeature(ctx),
                new RadarFeature(ctx),
                new OverviewFeature(ctx),
                new HitEstimateFeature(ctx),   // T-168: after the Overview (it reads the service lazily)
                new ContactInfoFeature(ctx),
                new RangeRingFeature(ctx),
                new RadialFeature(ctx),
                new MenuFeature(ctx),
                new EvasionFeature(ctx),
                new AirEvasionFeature(ctx),   // T-100: after TacticsFeature (the after-launch guard)
                new ToastFeature(ctx),   // T-175: the shared card stack, before the modules that show cards
                new AswFeature(ctx),
                new CapFeature(ctx),
                new StrikeFeature(ctx),   // T-341: after the CAP (the veto chain), before the air guard
                new AirGuardFeature(ctx),   // T-175: after Asw/Cap (the owners), before the air tick
                new AirTickFeature(ctx),
                new RadarPumpFeature(ctx),
                new MapLayerFeature(ctx),
                new SettingsFeature(ctx),
                new MapMenuFeature(ctx),
                new ToolbarFeature(ctx),   // T-174: after the map menu (same windows, game-panel buttons)
                new IdentifyFeature(ctx),
                new SarFeature(ctx),
                new SmartAttackFeature(ctx),
                new InterceptFeature(ctx),
                new SubDatumFeature(ctx),   // T-288: after the ASW window and the card stack (Update after both)
                new UnitActionFeature(ctx),   // T-234
                new PerfFeature(ctx),   // T-165
                new MessageColorFeature(ctx),   // T-329
                new WaypointQuietFeature(ctx),   // T-331
                new HeartbeatFeature(ctx),
                new HarmonyFeature(ctx),
                new DrainFeature(ctx),
            };
            foreach (var f in list) { var hb = f as HeartbeatFeature; if (hb != null) hb.Features = list; }   // T-151: the memory line asks every module for its counters
            return list;
        }
    }
}
