using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal enum ModSide { Voice, Ui }

    internal sealed class OwnershipPlan
    {
        public bool Enabled;       // ui: false with a voice mod < 0.14.0 -> the whole mod is off
        public bool TacticsOwner;  // PumpTactics, the fire veto patch, tactics capabilities
        public bool Link;          // voice: forwards UI commands through the slot
        public bool PressIgnore;   // auto-press "Ignore"
        public string Warning;     // log line or null
    }

    /// <summary>
    /// Who does what when one or both mods are installed (ADR-0033). Pure: no Unity / Harmony / BepInEx, covered by SelfTest.
    /// </summary>
    internal static class Ownership
    {
        public const int LinkVersion = 1;
        public const string VoiceGuid = "ru.voicebot.seapower.bridge";
        public const string UiGuid = "ru.voicebot.seapower.ui";
        public const string SlotKey = "ru.voicebot.seapower.ui/link";
        public static readonly Version MinVoiceWithUi = new Version(0, 14, 0);

        /// <summary>voiceVersion: null = no voice mod (for self=Ui); uiLink: null = no slot, else its "link" (for self=Voice); autoIgnoreCfg is read for self=Voice only.</summary>
        public static OwnershipPlan Plan(ModSide self, Version voiceVersion, int? uiLink, bool autoIgnoreCfg)
        {
            var p = new OwnershipPlan { Enabled = true };
            if (self == ModSide.Voice)
            {
                // "Ignore" is always pressed by the voice mod (when configured); the interface mod only takes over when voice is absent
                p.PressIgnore = autoIgnoreCfg;
                if (uiLink == null) p.TacticsOwner = true;
                else if (uiLink.Value == LinkVersion) p.Link = true;
                else p.Warning = "ui link v" + uiLink.Value + " unsupported";
                return p;
            }
            if (voiceVersion == null) { p.TacticsOwner = true; }   // T-306: the UI mod never presses "Ignore" (PressIgnore stays false)
            else if (voiceVersion >= MinVoiceWithUi) p.TacticsOwner = true;
            else { p.Enabled = false; p.Warning = "update VoiceBot Bridge to 0.14+"; }
            return p;
        }

        /// <summary>false when another VoiceBot mod (VoiceGuid / UiGuid other than selfId) already patched the target.</summary>
        public static bool MayPatch(string selfHarmonyId, IEnumerable<string> existingOwners)
        {
            if (existingOwners == null) return true;
            foreach (var o in existingOwners)
                if ((o == VoiceGuid || o == UiGuid) && o != selfHarmonyId) return false;
            return true;
        }

        private static string Row(string name, OwnershipPlan p, bool en, bool tac, bool link, bool ign, string warn)
        {
            if (p == null) return name + ": null";
            if (p.Enabled != en || p.TacticsOwner != tac || p.Link != link || p.PressIgnore != ign || p.Warning != warn)
                return name + ": got enabled=" + p.Enabled + " tactics=" + p.TacticsOwner + " link=" + p.Link + " ignore=" + p.PressIgnore + " warning=" + (p.Warning ?? "null");
            return null;
        }

        /// <summary>null = ok, else the first failure.</summary>
        public static string SelfTest()
        {
            try
            {
                var rows = new[]
                {
                    Row("voice alone", Plan(ModSide.Voice, null, null, true), true, true, false, true, null),
                    Row("voice alone, ignore off", Plan(ModSide.Voice, null, null, false), true, true, false, false, null),
                    Row("voice + ui link 1", Plan(ModSide.Voice, null, 1, true), true, false, true, true, null),
                    Row("voice + ui link 2", Plan(ModSide.Voice, null, 2, true), true, false, false, true, "ui link v2 unsupported"),
                    Row("ui alone", Plan(ModSide.Ui, null, null, true), true, true, false, false, null),
                    Row("ui alone, ignore off", Plan(ModSide.Ui, null, null, false), true, true, false, false, null),
                    Row("ui + voice 0.14.0", Plan(ModSide.Ui, new Version(0, 14, 0), null, true), true, true, false, false, null),
                    Row("ui + old voice 0.13.1", Plan(ModSide.Ui, new Version(0, 13, 1), null, true), false, false, false, false, "update VoiceBot Bridge to 0.14+"),
                };
                foreach (var r in rows) if (r != null) return r;
                const string ui = UiGuid, vo = VoiceGuid;
                if (MayPatch(ui, new[] { vo })) return "MayPatch(ui, [voice]) must be false";
                if (MayPatch(vo, new[] { ui })) return "MayPatch(voice, [ui]) must be false";
                if (!MayPatch(ui, new[] { ui })) return "MayPatch(ui, [ui]) must be true";
                if (!MayPatch(ui, new[] { "other.mod" })) return "MayPatch(ui, [other]) must be true";
                if (!MayPatch(ui, null) || !MayPatch(ui, new string[0])) return "MayPatch(ui, null/[]) must be true";
                return null;
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }
    }
}
