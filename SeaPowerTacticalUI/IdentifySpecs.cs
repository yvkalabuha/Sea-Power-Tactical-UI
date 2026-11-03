namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M9 (ADR-0043): the game members of the identify statuses (module identify: IdentifyGame.cs, IdentifyPatches.cs, IdentifyFeature.cs).
    /// The two patch targets are required (without them the observer cannot be installed); the reference texts need only the optional members.
    /// </summary>
    internal static class IdentifySpecs
    {
        private const string Cap = "identify";
        private const string Mvm = "SeapowerUI.MapUnitViewModel";
        private const string Gvm = "SeapowerUI.ViewModels.MainGameViewModel";

        public static readonly MemberSpec CommResult = MemberSpec.Method(Cap, Mvm, "CommunicationResult", new[] { "Vehicle", "string", "ObjectBase&" }, "ObjectBase");
        public static readonly MemberSpec FormatComm = MemberSpec.Method(Cap, Mvm, "FormatCommText", new[] { "Vehicle", "string" }, "string", false, true);
        public static readonly MemberSpec RaiseAlertObj = MemberSpec.Method(Cap, Gvm, "RaiseAlert", new[] { "ObjectBase", "string" }, "void");

        // The player's "Identify yourself" request: prefix opens the scope, postfix classifies the outcome, the finalizer closes it on an exception (T-131).
        public static readonly PatchSpec PatchRequest = new PatchSpec("identify_request", Cap, Mvm, "CommunicationResult", new[] { "Vehicle", "string", "ObjectBase&" }, "ObjectBase", PatchKind.PrefixPostfixFinalizer, "always");

        // The alert texts that the game shows inside that call (NoLOS / ignore / refuse).
        public static readonly PatchSpec PatchAlert = new PatchSpec("identify_alert", Cap, Gvm, "RaiseAlert", new[] { "ObjectBase", "string" }, "void", PatchKind.Postfix, "always", false);   // optional: only supplies reason texts (T-120 review)

        public static readonly SpecSet Set = SpecSet.Of("identify", typeof(IdentifySpecs));
    }
}
