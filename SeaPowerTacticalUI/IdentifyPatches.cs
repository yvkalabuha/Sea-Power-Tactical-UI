using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-111: bodies of the Harmony patches that observe the player's "Identify yourself" request (the targets are IdentifySpecs.PatchRequest / PatchAlert,
    /// installed by HarmonyFeature). Prefix + postfix + finalizer on MapUnitViewModel.CommunicationResult
    /// (non-null result = the contact follows), postfix on MainGameViewModel.RaiseAlert(ObjectBase, string) inside the call (the texts the game
    /// shows to the player tell NoLOS / ignore / refuse). Nothing else is read from the contact. Bodies never throw into the game.
    /// </summary>
    internal static class IdentifyPatches
    {
        private const string Request = "IdentificationRequest";
        private static bool _loggedFail;
        private static int _unrecognisedLogged;

        internal static void Fail(string where, Exception e)
        {
            IdentifyCapture.Abort();
            if (_loggedFail) return;
            _loggedFail = true;
            try { if (Patches.Api != null) Patches.Api.Log("identify " + where + " failed: " + e.GetType().Name + ": " + e.Message); } catch { }
        }

        // Finalizer: closes the scope if the original threw; returns the exception unchanged (the game's behaviour is not altered).
        internal static Exception IdentifyFinalizer(Exception __exception)
        {
            return IdentifyCapture.AfterRequest(__exception);
        }

        // Prefix: opens the scope for the player's identification request only.
        internal static void Prefix(object __0, string __1)
        {
            try
            {
                IdentifyCapture.Abort();
                var api = Patches.Api;
                if (api == null || __0 == null) return;
                if (!string.Equals(__1, Request, StringComparison.Ordinal)) return;
                int id = api.IdentifyTrackId(__0);
                if (id >= 0) IdentifyCapture.Begin(id);
            }
            catch (Exception e) { Fail("prefix", e); }
        }

        // Postfix of RaiseAlert(ObjectBase, string): collects the alert text while a request is running.
        internal static void AlertPostfix(string __1)
        {
            try
            {
                if (!IdentifyCapture.Open) return;
                IdentifyCapture.Alert(__1);
            }
            catch (Exception e) { Fail("alert", e); }
        }

        // Postfix: classifies and records. A non-null result means the contact agreed to follow.
        internal static void Postfix(object __result, object __0)
        {
            try
            {
                if (!IdentifyCapture.Open) return;
                var api = Patches.Api;
                if (api == null) { IdentifyCapture.Abort(); return; }
                IdentifyTemplates t = null;
                if (__result == null) t = api.IdentifyTemplatesFor(__0, Request);
                int id; IdentifyResult r;
                if (IdentifyCapture.End(__result != null, t, out id, out r) && r != IdentifyResult.None) api.OnIdentifyRequest(id, r);
                else if (r == IdentifyResult.None && __result == null && _unrecognisedLogged++ < 10) api.Log("identify: unrecognised outcome for track " + id);
            }
            catch (Exception e) { Fail("postfix", e); }
        }
    }
}
