using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>Static description of the interface mod: the plugin takes HarmonyId and patch ids from here, `selfcheck manifest` prints Json().</summary>
    internal static class ModManifest
    {
        public const string Guid = Ownership.UiGuid;
        public const string HarmonyId = Ownership.UiGuid;
        public const string PatchFireVeto = "fire_veto";

        /// <summary>
        /// The manifest is built from the registry (T-120 M20, ADR-0043): `features` and `menus` are the rows of UiDegradation, `hotkeys` their config keys,
        /// `patches` the PatchSpecs of every module. Nothing here is typed by hand any more; tests/test_sea_power_mod_manifest.py keeps the lists whole.
        /// </summary>
        public static string Json()
        {
            var rows = UiDegradation.Rows;
            var hotkeys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var r in rows) foreach (var k in r.Hotkeys) hotkeys.Add(k);
            foreach (var k in UiDegradation.OtherHotkeys) hotkeys.Add(k);
            var menus = new List<string>();
            var features = new List<ManifestJson.FeatureInfo>();
            foreach (var r in rows)
            {
                menus.Add(r.Id);
                features.Add(new ManifestJson.FeatureInfo(r.Id, r.Module, r.Needs, r.FallbackKey));
            }
            var patches = new List<ManifestJson.PatchInfo>();
            foreach (var set in UiRegistry.All())
                foreach (var p in set.Patches) patches.Add(new ManifestJson.PatchInfo(p.Id, p.Target, p.When));
            return ManifestJson.Build("ui", Guid, HarmonyId, ModInfo.Version, false, false, "never", hotkeys, menus, patches,
                new[]
                {
                    new KeyValuePair<string, OwnershipPlan>("ui", Ownership.Plan(ModSide.Ui, null, null, false)),
                    new KeyValuePair<string, OwnershipPlan>("both", Ownership.Plan(ModSide.Ui, new Version(0, 14, 0), null, false)),
                    new KeyValuePair<string, OwnershipPlan>("old_voice", Ownership.Plan(ModSide.Ui, new Version(0, 13, 1), null, false)),
                }, features);
        }
    }
}
