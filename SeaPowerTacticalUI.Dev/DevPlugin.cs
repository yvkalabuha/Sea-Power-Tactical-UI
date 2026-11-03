using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace VoiceBotBridge.Dev
{
    /// <summary>
    /// Sea Power Tactical UI Dev (ADR-0046): test channel on 127.0.0.1 for the voicebot-dev MCP. Off by default ([Dev] Enabled = false).
    /// Reads the UI mod through the diag slot, drives it through the closed link command table. Never shipped to Workshop.
    /// </summary>
    [BepInPlugin(DevInfo.Guid, "Sea Power Tactical UI Dev", DevInfo.Version)]
    [BepInDependency(DevInfo.UiGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class DevPlugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> _fire;
        private ConfigEntry<bool> _force;
        private DevServer _server;
        private DevPump _pump;
        private DevExecutor _exec;
        private DevLogListener _listener;
        private string _sessionDir;
        private DevStartGate _gate;
        private string _token;
        private int[] _ports;
        private bool _serverStarted;
        private bool _sceneHooked;

        private void Awake()
        {
            var enabled = Config.Bind("Dev", "Enabled", false, "Open the dev channel on 127.0.0.1 (developer machines only).");
            _fire = Config.Bind("Dev", "FireCommands", true, "Allow attack / smart_attack / asw_attack / set_roe (hostile targets only, every call is logged as [Dev][FIRE]).");
            _force = Config.Bind("Dev", "ForceCommands", true, "Allow force (QA scenarios by real game orders: buoy on a sub, route shift, divert, a friend into the line of fire, a hostile to a SAR helicopter); every call is logged as [Dev][FORCE].");
            var port = Config.Bind("Dev", "Port", 47390, "First port to try (the next 4 are tried when it is busy).");
            var startDelay = Config.Bind("Dev", "StartDelayS", 20f, "T-282: earliest second (since game start) the channel may open. 0/0/0 = open at once.");
            var startQuiet = Config.Bind("Dev", "StartQuietS", 5f, "T-282: seconds without a scene load before the channel opens (counted only after the main-menu/mission scene is loaded, T-285).");
            var startMax = Config.Bind("Dev", "StartMaxDelayS", 180f, "T-282: the channel opens at this second at the latest, scene loads or not.");
            if (!enabled.Value) { Logger.LogInfo("dev channel: off ([Dev] Enabled = false)"); return; }
            try { string prev = VoiceBotBridge.Ui.ModShutdown.ReadPrevious(Paths.BepInExRootPath, VoiceBotBridge.Ui.ModShutdown.DevFile); if (prev != null) Logger.LogInfo("previous dev shutdown: " + prev); } catch (Exception) { }   // T-232
            var ring = new DevLogRing();
            _listener = new DevLogListener(ring);
            BepInEx.Logging.Logger.Listeners.Add(_listener);
            var exec = new DevExecutor(ring, () => _fire.Value, line => Logger.LogWarning(line), forceOn: () => _force.Value);
            // hang triage: every command one line with wall time, result and main-thread exec ms; timeouts too (any group)
            _pump = new DevPump(exec.Handle, (r, why) => { Logger.LogWarning(DevCmdLog.Timeout(DateTime.Now, r.Cmd, why)); exec.FireTimeout(r, why); }, null,
                                (r, reply, ms, waitMs) => Logger.LogInfo(DevCmdLog.Line(DateTime.Now, r.Cmd, reply, ms, waitMs)));
            _exec = exec;
            _token = DevSession.NewToken();
            _ports = new int[5];
            for (int i = 0; i < _ports.Length; i++) _ports[i] = port.Value + i;
            _server = new DevServer(_ports, _token, r => _pump.Await(_pump.Enqueue(r)), exec.Hello, TimeSpan.FromMinutes(2), 65536, line => Logger.LogWarning(line));
            _sessionDir = Path.Combine(Paths.ConfigPath, "SeaPowerTacticalUI.Dev");
            DevSession.Delete(_sessionDir);   // a stale file from the previous run: clients must see "unavailable: no session file" until we listen
            // T-282: the channel opens after the load/JIT window (no sockets and no threads while the game starts)
            _gate = new DevStartGate(startDelay.Value, startQuiet.Value, startMax.Value);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneHooked = true;
            Logger.LogInfo("dev server start delayed: min " + startDelay.Value + " s, quiet " + startQuiet.Value + " s, max " + startMax.Value + " s");
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (_gate == null) return;
            bool ready = DevStartGate.IsReadyScene(scene.buildIndex);
            _gate.OnSceneLoaded(Time.realtimeSinceStartup, ready);
            Logger.LogInfo("dev gate: scene '" + scene.name + "' #" + scene.buildIndex + " loaded at " + Time.realtimeSinceStartup.ToString("0.0") + "s (ready=" + ready + ")");
        }

        private void StartServer()
        {
            if (_serverStarted) return;
            _serverStarted = true;
            UnhookScene();
            if (!_server.Start()) { Logger.LogError("dev channel: no free port " + _ports[0] + ".." + _ports[_ports.Length - 1]); _server = null; return; }
            DevSession.Write(_sessionDir, _server.Port, _token, System.Diagnostics.Process.GetCurrentProcess().Id, DevInfo.Version, DateTime.UtcNow.ToString("o"), _fire.Value);
            Logger.LogWarning("dev server started: listening on 127.0.0.1:" + _server.Port + " (dev-only, FireCommands=" + _fire.Value + ", ForceCommands=" + _force.Value + ") (after " + _gate.Why + " " + Time.realtimeSinceStartup.ToString("0.0") + "s)");
        }

        private static bool SceneLoadedByIndex(int i)
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetSceneByBuildIndex(i).isLoaded; } catch (Exception) { return false; }
        }

        private void UnhookScene()
        {
            if (!_sceneHooked) return;
            _sceneHooked = false;
            try { UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded; } catch (Exception) { }
        }

        // Unity main thread: the only place where requests touch the game.
        private void Update()
        {
            if (_pump == null) return;
            if (!_serverStarted && _gate != null)
            {
                // the menu scene may have loaded before our hook: an already loaded menu/mission scene counts too
                if (!_gate.Ready && (SceneLoadedByIndex(1) || SceneLoadedByIndex(2))) _gate.MarkReady(Time.realtimeSinceStartup);
                if (_gate.ShouldStart(Time.realtimeSinceStartup)) StartServer();
            }
            _pump.Pump(4, 2);
        }

        private void OnApplicationQuit() { Shutdown(); }

        private void OnDestroy() { Shutdown(); }

        private bool _shutDown;

        /// <summary>T-232: flushes the buffered BepInEx disk log (DiskLogListener.LogWriter).</summary>
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
            catch (Exception) { }
        }

        /// <summary>T-220: idempotent (OnApplicationQuit first, OnDestroy repeats as a no-op): stop the server and join its threads, drop the session file and the listener.</summary>
        private void Shutdown()
        {
            if (_shutDown) return;
            _shutDown = true;
            UnhookScene();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = true;
                try { if (_pump != null) _pump.Close(); } catch (Exception) { }
                try { if (_server != null && _serverStarted) ok = _server.Stop(1000); } catch (Exception) { ok = false; }
                if (_server != null && _serverStarted)
                {
                    string line = "dev shutdown: stopped=" + ok + " in " + sw.ElapsedMilliseconds + " ms";
                    // T-232: own file first (the buffered BepInEx log may not reach the disk before the process dies), then the log, then flush
                    try { VoiceBotBridge.Ui.ModShutdown.WriteRecord(Paths.BepInExRootPath, VoiceBotBridge.Ui.ModShutdown.DevFile, line, DateTime.UtcNow); } catch (Exception) { }
                    Logger.LogInfo(line);
                    FlushDiskLog();
                }
                if (_sessionDir != null && _serverStarted) DevSession.Delete(_sessionDir);
                if (_listener != null) BepInEx.Logging.Logger.Listeners.Remove(_listener);
            }
            catch (Exception) { }
        }
    }
}
