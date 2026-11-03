namespace VoiceBotBridge
{
    /// <summary>
    /// T-234 (ADR-0096): the game members of the "Orders:" label of a unit (module unit_action: UnitActionGame.cs, UnitActionFeature.cs).
    /// The game shows ObjectBaseViewModel.ObjectOrders = OrderFeedbackText when it is not empty, else CurrentOrderText. The module writes its label into
    /// OrderFeedbackText (a public ReactiveProperty&lt;string&gt;) and clears it again when it lets a unit go; without that member the module is off.
    /// OwnOnly: only the player's own units are touched.
    /// </summary>
    internal static class UnitActionSpecs
    {
        private const string Cap = "unit_action", Ob = "SeaPower.ObjectBase", Tf = "SeaPower.Taskforce";

        public static readonly MemberSpec Feedback = MemberSpec.Prop(Cap, Ob, "OrderFeedbackText", true, false, true);       // required: the channel we write
        public static readonly MemberSpec OrderText = MemberSpec.Prop(Cap, Ob, "CurrentOrderText", false, false, true);     // smart mode only: the game's own text (read, never written)
        public static readonly MemberSpec ShowFeedback = MemberSpec.Method(Cap, Ob, "ShowOrderFeedback", new[] { "string", "float" }, "void", false);   // proof: the channel is still a transient feedback
        public static readonly MemberSpec VmOrders = MemberSpec.Prop(Cap, "SeapowerUI.ViewModels.ObjectBaseViewModel", "ObjectOrders", false);     // proof: the field reads the channel

        // uid -> object (one pass per tick instead of FindOwnUnit per label)
        public static readonly MemberSpec ObjUniqueId = MemberSpec.Prop(Cap, Ob, "UniqueID", true);
        public static readonly MemberSpec TfVessels = MemberSpec.Field(Cap, Tf, "_taskforceVessels", false);
        public static readonly MemberSpec TfSubmarines = MemberSpec.Field(Cap, Tf, "_taskforceSubmarines", false);
        public static readonly MemberSpec TfAircraft = MemberSpec.Field(Cap, Tf, "_taskforceAircraft", false);
        public static readonly MemberSpec TfHelicopters = MemberSpec.Field(Cap, Tf, "_taskforceHelicopters", false);

        public static readonly MemberSpec[] Lists = { TfVessels, TfSubmarines, TfAircraft, TfHelicopters };

        public static readonly SpecSet Set = SpecSet.Of("unit_action", typeof(UnitActionSpecs));
    }
}
