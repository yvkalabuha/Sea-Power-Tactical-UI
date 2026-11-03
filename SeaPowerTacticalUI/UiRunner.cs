using System;
using System.Diagnostics;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-105: the MonoBehaviour that drives UiCore for both entries (BepInEx UiPlugin, AnchorChain AcEntry).
    /// Both go through UiBoot.TryClaim: the second copy of the DLL in the process stays off and says so in the log.
    /// </summary>
    internal sealed class UiRunner : MonoBehaviour
    {
        private UiCore _core;
        private string _entry;
        private string _location;
        private ManualLogSource _log;
        private bool _shutDown;

        /// <summary>Claims the slot, creates the host object and starts the core. null = another copy runs (logged).</summary>
        internal static UiRunner Attach(string entry, ConfigFile config, ManualLogSource log, string pluginDir)
        {
            string location = pluginDir ?? "";
            string other;
            if (!UiBoot.TryClaim(entry, ModInfo.Version, location, out other))
            {
                string msg = UiBoot.DuplicateMessage(other, entry, location);
                if (UiBoot.IsQuietPair(other, entry, location)) log.LogInfo(msg); else log.LogWarning(msg);   // T-314: the two Workshop entries of one folder
                return null;
            }
            try
            {
                var go = new GameObject("SeaPowerTacticalUI");
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                var runner = go.AddComponent<UiRunner>();
                runner._entry = entry;
                runner._location = location;
                runner._log = log;
                GameApi.FrameClock = () => Time.frameCount;   // T-125: the frame number of FrameState, before the core creates the GameApi
                runner._core = UiCore.Start(new BepInExHost(config, log, pluginDir), UiFeatures.All, DiagWiring.Build);
                log.LogInfo("Sea Power Tactical UI " + ModInfo.Version + " started (" + entry + ")");
                try { string prev = ModShutdown.ReadPrevious(BepInEx.Paths.BepInExRootPath, ModShutdown.UiFile); if (prev != null) log.LogInfo("previous shutdown: " + prev); } catch { }   // T-232
                try
                {
                    bool on = true;
                    ConfigEntry<bool> e;
                    if (config.TryGetEntry("Loader", "InstallToBepInEx", out e)) on = e.Value;   // T-314 (ADR-0121)
                    var inst = LoaderInstall.Run(entry, on, location, BepInEx.Paths.PluginPath, LoaderInstall.ReadVersion);
                    if (inst.Line != null) { if (inst.Warning) log.LogWarning(inst.Line); else log.LogInfo(inst.Line); }
                }
                catch { }
                return runner;
            }
            catch (Exception)
            {
                UiBoot.Release(entry, location);
                throw;
            }
        }

        // Unity main thread: the ONLY place that touches game objects.
        private void Update() { if (_core != null) _core.Update(new UiFrame { RealTime = Time.unscaledTime, DeltaTime = Time.unscaledDeltaTime }); }

        private void OnGUI() { if (_core != null) _core.OnGUI(); }

        private void OnApplicationQuit() { Shutdown("quit"); }

        private void OnDestroy() { Shutdown("destroy"); }

        /// <summary>T-232: flushes the buffered BepInEx disk log (DiskLogListener.LogWriter) so the last lines survive a fast process exit.</summary>
        private static void FlushDiskLog()
        {
            try
            {
                foreach (var l in BepInEx.Logging.Logger.Listeners)
                {
                    var d = l as BepInEx.Logging.DiskLogListener;
                    if (d != null && d.LogWriter != null) d.LogWriter.Flush();
                }
            }
            catch { }
        }

        /// <summary>T-220: idempotent orderly stop (OnApplicationQuit first, OnDestroy repeats as a no-op): dispose the core, wait up to 1 s for the mod threads, log one "shutdown" line.</summary>
        private void Shutdown(string why)
        {
            if (_shutDown) return;
            _shutDown = true;
            try
            {
                var sw = Stopwatch.StartNew();
                try { if (_core != null) { _core.Dispose(); _core = null; } } catch { }
                double disposeMs = sw.Elapsed.TotalMilliseconds;
                try { while (sw.ElapsedMilliseconds < 1000 && ModThreads.AliveDedicated().Count > 0) Thread.Sleep(20); } catch { }
                string line = ModShutdown.Line(_entry, why, disposeMs, ModThreads.Snapshot());
                // T-232: the BepInEx DiskLogListener buffers ~2 s and the process dies sooner: own file first, then the log, then flush
                try { ModShutdown.WriteRecord(BepInEx.Paths.BepInExRootPath, ModShutdown.UiFile, line, DateTime.UtcNow); } catch { }
                try { if (_log != null) _log.LogInfo(line); } catch { }
                FlushDiskLog();
            }
            catch { }
            finally { try { UiBoot.Release(_entry, _location); } catch { } }
        }
    }
}
