using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>Exact type name matters: ConfigurationManager looks the tag up by the name "ConfigurationManagerAttributes" and its fields.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
        public bool? Browsable;
    }

    /// <summary>IUiHost on top of BepInEx (T-118, ADR-0044). The only place of the core that knows ConfigFile, Logger, Paths and the Chainloader.</summary>
    internal sealed class BepInExHost : IUiHost
    {
        private readonly ConfigFile _config;
        private readonly ManualLogSource _log;
        private readonly string _pluginDir;

        /// <summary>T-105: takes only the config, the log and the DLL folder, so both entries (BepInEx plugin, AnchorChain) can drive the core. pluginDir = folder of the DLL.</summary>
        public BepInExHost(ConfigFile config, ManualLogSource log, string pluginDir) { _config = config; _log = log; _pluginDir = pluginDir; }

        private sealed class Setting<T> : IUiSetting<T>
        {
            private readonly ConfigEntry<T> _e;
            public Setting(ConfigEntry<T> e) { _e = e; }
            public T Value { get { return _e.Value; } set { _e.Value = value; } }
            public T Default { get { return (T)_e.DefaultValue; } }

            // ConfigEntry.SettingChanged fires only when the value really changed (BoxedValue setter, Reload)
            private readonly Dictionary<Action<T>, EventHandler> _subs = new Dictionary<Action<T>, EventHandler>();
            public event Action<T> Changed
            {
                add
                {
                    EventHandler h = (s, a) => value(_e.Value);
                    _subs[value] = h;
                    _e.SettingChanged += h;
                }
                remove
                {
                    EventHandler h;
                    if (_subs.TryGetValue(value, out h)) { _e.SettingChanged -= h; _subs.Remove(value); }
                }
            }
        }

        /// <summary>T-144: SaveOnConfigSet off for the scope, one Save on Dispose, the previous flag restored in finally. Nested scopes save once (the outermost).</summary>
        private sealed class BatchScope : IDisposable
        {
            private readonly BepInExHost _h; private readonly bool _prev; private bool _done;
            public BatchScope(BepInExHost h)
            {
                _h = h;
                _prev = h._config.SaveOnConfigSet;
                h._config.SaveOnConfigSet = false;
                h._depth++;
            }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _h._depth--;
                try { if (_h._depth == 0) _h._config.Save(); }
                finally { _h._config.SaveOnConfigSet = _prev; }
            }
        }

        private int _depth;

        public IDisposable Batch() { return new BatchScope(this); }

        public IUiSetting<T> Bind<T>(string section, string key, T defaultValue, string description, UiKeyClass cls = UiKeyClass.Basic, UiSettingMeta meta = null)
        {
            // T-144: a numeric key with both bounds (and no "0 = auto") gets a range; ConfigurationManager shows a slider and BepInEx clamps. No strict option lists.
            AcceptableValueBase range = null;
            if (meta != null && !meta.ZeroIsAuto && !double.IsNaN(meta.Min) && !double.IsNaN(meta.Max)
                && (typeof(T) == typeof(int) || typeof(T) == typeof(float) || typeof(T) == typeof(double)))
            {
                range = (AcceptableValueBase)Activator.CreateInstance(typeof(AcceptableValueRange<>).MakeGenericType(typeof(T)),
                    Convert.ChangeType(meta.Min, typeof(T)), Convert.ChangeType(meta.Max, typeof(T)));
            }
            ConfigDescription d;
            if (cls == UiKeyClass.Advanced) d = new ConfigDescription("(advanced) " + description, range, new ConfigurationManagerAttributes { IsAdvanced = true });
            else if (cls == UiKeyClass.Internal) d = new ConfigDescription("(internal) " + description, range, new ConfigurationManagerAttributes { Browsable = false });
            else d = new ConfigDescription(description, range);
            return new Setting<T>(_config.Bind(new ConfigDefinition(section, key), defaultValue, d));
        }

        /// <summary>T-123: upgrade the stored file (UiConfigMigrator) before the first Bind; backup "&lt;cfg&gt;.bak-v&lt;old&gt;" once.</summary>
        public IList<string> PrepareConfig()
        {
            var lines = new List<string>();
            try
            {
                string path = _config.ConfigFilePath;
                if (!File.Exists(path)) return lines;
                byte[] raw = File.ReadAllBytes(path);
                string text = new UTF8Encoding(false).GetString(raw);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                int old = UiConfigMigrator.DetectVersion(text);
                List<string> changes;
                string up = UiConfigMigrator.Upgrade(text, out changes);
                if (changes.Count == 0) return lines;
                string bak = path + ".bak-v" + old;
                if (!File.Exists(bak)) File.WriteAllBytes(bak, raw);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, new UTF8Encoding(false).GetBytes(up));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _config.Reload();
                lines.AddRange(changes);
            }
            catch (Exception e) { lines.Add("upgrade failed, the file stays as it is: " + e.Message); }
            return lines;
        }

        /// <summary>T-123: after every Bind, drop the keys no module bound (OrphanedEntries is not public API: reflection, never throws).</summary>
        public IList<string> FinishConfig(bool allBound)
        {
            var removed = new List<string>();
            if (!allBound) return removed;
            try
            {
                PropertyInfo p = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var orphans = p == null ? null : p.GetValue(_config, null) as Dictionary<ConfigDefinition, string>;
                if (orphans == null || orphans.Count == 0) return removed;
                foreach (var k in orphans.Keys) removed.Add("[" + k.Section + "] " + k.Key);
                orphans.Clear();
                _config.Save();
            }
            catch (Exception e) { _log.LogWarning("config: orphan cleanup skipped: " + e.Message); removed.Clear(); }
            return removed;
        }

        public void LogInfo(string m) { _log.LogInfo(UiLogStamp.Apply(m, DateTime.Now)); }
        public void LogWarning(string m) { _log.LogWarning(UiLogStamp.Apply(m, DateTime.Now)); }
        public void LogError(string m) { _log.LogError(UiLogStamp.Apply(m, DateTime.Now)); }
        public string ConfigDir { get { return Paths.ConfigPath; } }
        public string RootDir { get { return Paths.BepInExRootPath; } }

        public string PluginDir
        {
            get { return _pluginDir; }
        }

        public string GameVersion { get { return Application.version; } }
        public string UnityVersion { get { return Application.unityVersion; } }
        public double RealTime { get { return Time.unscaledTime; } }

        public bool PointerBusy
        {
            get
            {
                try
                {
                    if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)) return true;
                    return ModUiRects.Contains(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                }
                catch { return true; }
            }
        }

        public Version FindPluginVersion(string guid)
        {
            BepInEx.PluginInfo vi;
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out vi) && vi != null) return vi.Metadata.Version;
            return null;
        }
    }
}
