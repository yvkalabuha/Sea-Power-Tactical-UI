using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>One bound config value (T-118, ADR-0044). BepInExHost wraps a ConfigEntry; tests use a plain holder.</summary>
    internal interface IUiSetting<T>
    {
        T Value { get; set; }
        T Default { get; }
        /// <summary>T-144: raised only when the value really changed.</summary>
        event Action<T> Changed;
    }

    /// <summary>T-144 (ADR-0050): the editor control of a setting in the settings window.</summary>
    internal enum UiControl { Auto, Check, Number, Choice, Multi, Hotkey, Text }

    /// <summary>T-144: when a changed value takes effect. A key is honest: Live needs a subscriber, otherwise it stays Restart.</summary>
    internal enum UiApply { Restart = 0, NextOrder = 1, Live = 2 }

    /// <summary>T-144 (ADR-0050 section 5): what the settings window needs to know about a key. Declared by the owner module in Bind.</summary>
    internal sealed class UiSettingMeta
    {
        public UiControl Control = UiControl.Auto;
        public UiApply Apply = UiApply.Restart;
        public string Section;            // settings section id; null = owner module id
        public int Order;                 // row order inside the section; 0 = Bind order
        public double Min = double.NaN, Max = double.NaN, Step = 1;
        public bool ZeroIsAuto;
        public string UnitKey;            // settings.unit.<id>
        public double DisplayFactor = 1;  // shown = stored * factor (Cap.MissilePct: 100 -> %)
        public string[] Options;          // Choice/Multi stored values
        public string[] Required;         // Multi: always on
        public string[][] Parts;          // T-178 S4: Text "a,b,c": each part is a Segments row of its options (ContactInfo Units: {nm,km},{kn,kmh},{ft,m})
        public bool HoldKey;              // Hotkey with hold semantics (Radial)
        public string Capability;         // T-165: the row is "unavailable" while this capability is missing; null = always available
        public string Gate;               // T-164: id of a gate (UiSettingsRegistry.SetGate); null = no gate
        public bool GateSoft;             // T-164: true = the row stays editable, the gate note only goes to the hint

        /// <summary>The same description of one key bound twice (AirTick + Cap).</summary>
        public bool SameAs(UiSettingMeta o)
        {
            if (o == null) return false;
            return Control == o.Control && Apply == o.Apply && Section == o.Section && Order == o.Order
                && SameD(Min, o.Min) && SameD(Max, o.Max) && Step.Equals(o.Step) && ZeroIsAuto == o.ZeroIsAuto
                && UnitKey == o.UnitKey && DisplayFactor.Equals(o.DisplayFactor)
                && SameArr(Options, o.Options) && SameArr(Required, o.Required) && SameParts(Parts, o.Parts) && HoldKey == o.HoldKey && Capability == o.Capability
                && Gate == o.Gate && GateSoft == o.GateSoft;
        }

        private static bool SameParts(string[][] a, string[][] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!SameArr(a[i], b[i])) return false;
            return true;
        }

        private static bool SameD(double a, double b) { return (double.IsNaN(a) && double.IsNaN(b)) || a.Equals(b); }

        private static bool SameArr(string[] a, string[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>T-123: who sees a config key. Basic = the player (and the settings window), Advanced = ConfigurationManager "Advanced" only,
    /// Internal = written by the mod itself, hidden.</summary>
    internal enum UiKeyClass { Basic = 0, Advanced = 1, Internal = 2 }

    internal interface IUiConfig
    {
        IUiSetting<T> Bind<T>(string section, string key, T defaultValue, string description,
                              UiKeyClass cls = UiKeyClass.Basic, UiSettingMeta meta = null);
    }

    /// <summary>Everything the mod core needs from the host (BepInEx today, AnchorChain later). No BepInEx / Unity types here.</summary>
    internal interface IUiHost : IUiConfig
    {
        void LogInfo(string m);
        void LogWarning(string m);
        void LogError(string m);
        string ConfigDir { get; }       // BepInEx.Paths.ConfigPath
        string RootDir { get; }         // BepInEx.Paths.BepInExRootPath (heartbeat)
        string PluginDir { get; }       // folder of the DLL (lang/); null if unknown
        string GameVersion { get; }     // Application.version
        string UnityVersion { get; }    // Application.unityVersion
        double RealTime { get; }        // Time.unscaledTime (GameApi.Clock)
        /// <summary>T-130: any mouse button is down or the cursor is inside a mod window (ModUiRects); the input-block watchdog stays quiet then.</summary>
        bool PointerBusy { get; }
        Version FindPluginVersion(string guid);   // Chainloader.PluginInfos; null = not installed
        /// <summary>Before the first Bind: upgrade the stored file to UiConfigMigrator.Current. Returns the log lines (empty = nothing).</summary>
        IList<string> PrepareConfig();
        /// <summary>After all Bind: when allBound, drop the keys no module bound (orphans) and save. Returns the removed "[Section] Key".</summary>
        IList<string> FinishConfig(bool allBound);
        /// <summary>T-144: no Save per Set inside the scope; one Save on Dispose.</summary>
        IDisposable Batch();
    }
}
