using System.IO;
using BepInEx;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// Sea Power Tactical UI: BepInEx entry point. The functions are modules (IUiFeature) driven by UiCore (T-118, ADR-0044).
    /// Works alone (no bot, no TCP, no microphone). Owner decisions with the voice mod come from Ownership.Plan in the first Update.
    /// T-105: the plugin only hands Config/Logger/folder to UiRunner (which claims UiBoot, so a second copy via AnchorChain stays off);
    /// the frame loop runs in UiRunner, not here.
    /// </summary>
    [BepInPlugin(ModManifest.Guid, "Sea Power Tactical UI", ModInfo.Version)]
    public sealed class UiPlugin : BaseUnityPlugin
    {
        public const string Version = ModInfo.Version;

        private void Awake() { UiRunner.Attach("bepinex", Config, Logger, Path.GetDirectoryName(Info.Location)); }
    }
}
