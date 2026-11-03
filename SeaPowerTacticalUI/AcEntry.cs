using System.IO;
using AnchorChain;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-105: AnchorChain entry (Workshop install). TriggerEntryPoint runs once from the AnchorChain preloader;
    /// it shares the config file with the BepInEx entry and starts the same core through UiRunner.Attach (UiBoot keeps one copy).
    /// </summary>
    [ACPlugin(ModManifest.Guid, "Sea Power Tactical UI", ModInfo.Version)]
    public sealed class AcEntry : IAnchorChainMod
    {
        public void TriggerEntryPoint()
        {
            var cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, ModManifest.Guid + ".cfg"), true);
            var log = Logger.CreateLogSource("Sea Power Tactical UI");
            UiRunner.Attach("anchorchain", cfg, log, Path.GetDirectoryName(typeof(AcEntry).Assembly.Location));
        }
    }
}
