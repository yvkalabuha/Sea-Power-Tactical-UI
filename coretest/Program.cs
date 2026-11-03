using System;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main()
        {
            int bad = 0;
            bad += Run("MemberResolver", MemberResolver.SelfTest());   // T-120
            bad += Run("UiRegistry", UiRegistry.SelfTest());
            bad += Run("UiDegradation", UiDegradation.SelfTest());
            bad += Run("UiContext", UiContext.SelfTest());
            bad += Run("UiCore", UiCore.SelfTest());
            bad += Run("UiSettingsRegistry", UiSettingsRegistry.SelfTest());
            bad += Run("SettingsDraft", SettingsDraft.SelfTest());   // T-144
            bad += Run("UiConfigMigrator", UiConfigMigrator.SelfTest());
            bad += Run("HotkeyBinding", HotkeyBinding.SelfTest());
            bad += Run("ReleaseRegistry", ReleaseRegistry.SelfTest());
            bad += Run("DiagHost", DiagHost.SelfTest());
            bad += Run("UiGuard", UiGuard.SelfTest());   // T-129
            bad += Run("UiPumpTimer", UiPumpTimer.SelfTest());   // T-124
            bad += Run("MissionLifecycle", MissionLifecycle.SelfTest());   // T-130
            bad += Run("InputBlockWatch", InputBlockWatch.SelfTest());
            bad += Run("UnitSteering", UnitSteering.SelfTest());   // T-138
            bad += Run("UiBoot", UiBoot.SelfTest());   // T-105
            bad += Run("LoaderInstall", LoaderInstall.SelfTest());   // T-314
            return bad == 0 ? 0 : 1;
        }

        private static int Run(string name, string err)
        {
            Console.WriteLine(name + " " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }
    }
}
