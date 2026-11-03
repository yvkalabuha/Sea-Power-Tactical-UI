using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-101 (ADR-0071, ADR-0043): the game members of the submarine torpedo evasion (module evasion: SubEvasion.cs, EvasionFeature.cs).
    /// sub_evasion = the threatening torpedo and the heading (the button, the automatics and the own fallback mode need only these and set_telegraph);
    /// sub_evasion_native = the game's own state TorpedoEvasion (state machine, setState, the private state field). Depth members come from SmartAttackSpecs
    /// (setPresetDepth, GetPresetDepthFeet, setDepth, _currentPresetDepth, _maxDepthUnity, GetHeightAt), the waypoint and telegraph orders from Common.
    /// </summary>
    internal static class SubEvasionSpecs
    {
        private const string Cap = "sub_evasion";
        private const string Nat = "sub_evasion_native";
        private const string Ob = "SeaPower.ObjectBase";
        private const string Sub = "SeaPower.Submarine";
        private const string Te = "SubmarineStates.TorpedoEvasion";

        // ---- required: the unit and its threat ----
        public static readonly MemberSpec ObjThreatingTorpedo = MemberSpec.Prop(Cap, Ob, "ThreatingTorpedo");
        public static readonly MemberSpec ObjHeading = MemberSpec.Prop(Cap, Ob, "Heading");

        // ---- the native state ----
        public static readonly MemberSpec SubStateTorpedoEvasion = MemberSpec.Field(Nat, Sub, "_state_torpedoEvasion");
        public static readonly MemberSpec ObjStateMachine = MemberSpec.Prop(Nat, Ob, "StateMachine");
        public static readonly MemberSpec SmCurrentState = MemberSpec.Prop(Nat, "SeaPower.StateMachine", "CurrentState");
        public static readonly MemberSpec SmSetState = MemberSpec.Method(Nat, "SeaPower.StateMachine", "setState", new[] { "IState" }, "void");

        // ---- optional: without them the feature degrades (no state name, no end of the native state by us, no panic in the log) ----
        public static readonly MemberSpec SmCurrentStateName = MemberSpec.Prop(Nat, "SeaPower.StateMachine", "CurrentStateName", false);
        public static readonly MemberSpec StatePriority = MemberSpec.Prop(Nat, "IState", "Priority", false);
        public static readonly MemberSpec StateIsFinished = MemberSpec.Prop(Nat, Te, "IsFinished", false);
        public static readonly MemberSpec StatePanicMode = MemberSpec.Field(Nat, Te, "_panicMode", false);
        public static readonly MemberSpec ObjSetPlayerCommandOverride = MemberSpec.Method(Nat, Ob, "setPlayerCommandOverride", new[] { "bool" }, "void", false);
        public static readonly MemberSpec ObjPlayerCommandOverride = MemberSpec.Field(Nat, Ob, "_playerCommandOverride", false);
        public static readonly MemberSpec ObjCavitationForbidden = MemberSpec.Prop(Cap, Ob, "CavitationForbidden", false);   // ReactiveProperty<bool>
        public static readonly MemberSpec ObjIsCavitating = MemberSpec.Field(Cap, Ob, "_isCavitating", false);
        public static readonly MemberSpec ObjAi = MemberSpec.Field(Cap, Ob, "_ai", false);
        public static readonly MemberSpec AiHidingState = MemberSpec.Field(Cap, "SeaPower.AI", "_hidingState", false);
        public static readonly MemberSpec ObjLaunchNoisemaker = MemberSpec.Method(Cap, Ob, "LaunchNoisemaker", new[] { "string" }, "void", false);

        public static void Derive(GameApi api, ISet<string> caps)
        {
            if (!api.Capabilities.Contains("auto_evade")) { caps.Remove(Cap); caps.Remove(Nat); return; }
            if (!caps.Contains(Cap)) caps.Remove(Nat);
            if (!caps.Contains(Nat) && !api.Capabilities.Contains("set_telegraph")) caps.Remove(Cap);
        }

        public static readonly SpecSet Set = SpecSet.Of("sub_evasion", typeof(SubEvasionSpecs));
    }
}
