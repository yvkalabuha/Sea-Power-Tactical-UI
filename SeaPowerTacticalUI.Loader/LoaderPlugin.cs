using System;
using System.Reflection;
using BepInEx;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace SeaPowerTacticalUI.Loader
{
    /// <summary>T-314 (ADR-0121): loads SeaPowerTacticalUI.dll from the Workshop item folder and calls LoaderEntry.Start.
    /// Soft dependencies make BepInEx start the other entries first, so a running copy is seen in the slot.</summary>
    [BepInPlugin("ru.voicebot.seapower.ui.loader", "Sea Power Tactical UI Loader", "1.0.0")]
    [BepInDependency("io.github.seapower_modders.anchorchain_preloader", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("ru.voicebot.seapower.ui", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class LoaderPlugin : BaseUnityPlugin
    {
        public const string Version = "1.0.0";

        private void Awake()
        {
            try
            {
                string slot = AppDomain.CurrentDomain.GetData(LoaderLogic.SlotKey) as string;
                string dir = LoaderLogic.WorkshopItemDir(Paths.GameRootPath);
                Decision d = LoaderLogic.Decide(slot, LoaderLogic.FindModDll(dir, new LoaderFs()));
                if (d.Kind == DecisionKind.Skip) { Logger.LogInfo(d.Line); return; }
                if (d.Kind == DecisionKind.Report) { Logger.LogWarning(d.Line); return; }
                Load(d.Path);
            }
            catch (Exception ex)
            {
                Logger.LogError("start failed: " + ex);
            }
        }

        private void Load(string path)
        {
            try
            {
                Assembly asm = Assembly.LoadFile(path);
                Type t = asm.GetType(LoaderLogic.EntryType, false);   // only the one type: scanning all exported types would load the AnchorChain entry
                MethodInfo m = t == null ? null : t.GetMethod(LoaderLogic.EntryMethod, new[] { typeof(int), typeof(string) });
                if (m == null) { Logger.LogError("start failed: no LoaderEntry.Start in " + path); return; }
                var text = m.Invoke(null, new object[] { LoaderLogic.Protocol, Version }) as string;
                if (text != null) Logger.LogError("start failed: " + text);
            }
            catch (TargetInvocationException ex)
            {
                Logger.LogError("start failed: " + (ex.InnerException ?? ex));
            }
        }
    }
}
