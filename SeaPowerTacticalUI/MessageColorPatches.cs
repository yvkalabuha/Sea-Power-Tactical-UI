using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-329: bodies of the red hostile message patches (targets: MessageColorSpecs, installed by HarmonyFeature). Option off = one field read.
    /// Bodies never throw into the game. Only the string of the alert is changed, never anything else.
    /// </summary>
    internal static class MessageColorPatches
    {
        internal static volatile bool On;   // MessageColorFeature (the [Messages] HostileRed switch)
        [ThreadStatic] private static bool _inClassified;
        private static bool _loggedFail;

        private static void Fail(string where, Exception e)
        {
            if (_loggedFail) return;
            _loggedFail = true;
            try { if (Patches.Api != null) Patches.Api.Log("message color " + where + " failed: " + e.GetType().Name + ": " + e.Message); } catch { }
        }

        // Prefix of Taskforce.CheckContactClassifiedMessage: RaiseAlert(string) texts inside this call are the contact classification.
        internal static void ClassifiedPrefix() { _inClassified = true; }

        // Finalizer: the scope is closed also when the original throws; the exception is returned unchanged.
        internal static Exception ClassifiedFinalizer(Exception __exception) { _inClassified = false; return __exception; }

        // Prefix of RaiseAlert(string): hostile classification texts go red.
        internal static void AlertTextPrefix(ref string __0)
        {
            if (!On || !_inClassified) return;
            try
            {
                var api = Patches.Api;
                if (api == null) return;
                if (Ui.MessageColorMath.IsHostileText(__0, api.MessageTextLookup())) __0 = Ui.MessageColorMath.Wrap(__0);
            }
            catch (Exception e) { Fail("alert", e); }
        }

        // Prefix of RaiseContactAlert(Vehicle, ObjectBase, string): the contact is hostile already (new contact, classified as hostile).
        internal static void ContactAlertPrefix(object __0, ref string __2)
        {
            if (!On) return;
            try
            {
                var api = Patches.Api;
                if (api != null && api.MessageContactHostile(__0)) __2 = Ui.MessageColorMath.Wrap(__2);
            }
            catch (Exception e) { Fail("contact", e); }
        }
    }
}
