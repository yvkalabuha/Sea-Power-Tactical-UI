using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-314 (ADR-0121): third entry, called by reflection from SeaPowerTacticalUI.Loader.dll.</summary>
    public static class LoaderEntry
    {
        /// <summary>null = ok (started, or another copy already runs); else the reason text for the loader log.</summary>
        public static string Start(int protocol, string loaderVersion)
        {
            string bad = UiBoot.CheckLoaderProtocol(protocol);
            if (bad != null) return bad;
            var cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, ModManifest.Guid + ".cfg"), true,
                new BepInPlugin(ModManifest.Guid, "Sea Power Tactical UI", ModInfo.Version)); // N1 r30: keeps the BepInEx cfg header
            var log = Logger.CreateLogSource("Sea Power Tactical UI");
            log.LogInfo("loader " + loaderVersion);
            UiRunner.Attach("loader", cfg, log, Path.GetDirectoryName(typeof(LoaderEntry).Assembly.Location));
            return null;
        }
    }
}
