namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): the game members of the "Старпом" submenu of the unit context menu (module menu: Menu.cs). The Harmony target (bot_menu) is a
    /// PatchSpec (installed by HarmonyFeature); the members the submenu builds from are listed here as bot_menu_aux. The constructors of ContextMenuItem and
    /// ReactiveProperty take generic parameters (the signature syntax has none): Menu.cs picks them by arity, as before.
    /// </summary>
    internal static class MenuSpecs
    {
        private const string Aux = "bot_menu_aux";

        /// <summary>
        /// The generic members stay manual (the signature syntax has no generic parameters): ReactiveProperty&lt;bool&gt;(bool[, comparer]) is found on the
        /// closed type, ToReadOnlyReactiveProperty is a generic method definition closed with bool. The name is kept here, the code in Menu.cs.
        /// </summary>
        public const string ToReadOnlyName = "ToReadOnlyReactiveProperty";

        public static readonly MemberSpec ItemType = MemberSpec.TypeOf(Aux, "SeapowerUI.ViewModels.ContextMenuItem", false);
        public static readonly MemberSpec CommandType = MemberSpec.TypeOf(Aux, "SeaPower.DelegateCommand", false);
        public static readonly MemberSpec ReactivePropertyType = MemberSpec.TypeOf(Aux, "UniRx.ReactiveProperty`1", false);
        public static readonly MemberSpec ReactiveExtensionsType = MemberSpec.TypeOf(Aux, "UniRx.ReactivePropertyExtensions", false);

        public static readonly MemberSpec ItemItems = MemberSpec.Prop(Aux, "SeapowerUI.ViewModels.ContextMenuItem", "Items", false);
        public static readonly MemberSpec ItemCommand = MemberSpec.Prop(Aux, "SeapowerUI.ViewModels.ContextMenuItem", "Command", false);
        public static readonly MemberSpec ItemCheckable = MemberSpec.Prop(Aux, "SeapowerUI.ViewModels.ContextMenuItem", "IsCheckable", false);

        public static readonly MemberSpec ViewModelObject = MemberSpec.Field(Aux, "SeapowerUI.ViewModels.ObjectBaseViewModel", "_objectBase", false);
        /// <summary>Postfix of the unit context menu: appends the "Старпом" submenu (installed by HarmonyFeature; MenuSettings.Enabled).</summary>
        public static readonly PatchSpec PatchBotMenu = new PatchSpec("bot_menu", "bot_menu", "SeapowerUI.ViewModels.ObjectBaseViewModel", "GetContextMenuItems", new string[0], "TrulyObservableCollection`1", PatchKind.Postfix, "always");

        /// <summary>Foreign-contact menu (Ctrl+RMB on a hostile/unidentified contact): built by MapUnitViewModel, not ObjectBaseViewModel (T-138/T-162 fix).</summary>
        public static readonly PatchSpec PatchContactMenu = new PatchSpec("bot_menu_contact", "bot_menu", "SeapowerUI.MapUnitViewModel", "GetContextMenuItems", new string[0], "TrulyObservableCollection`1", PatchKind.Postfix, "always");
        public static readonly MemberSpec MapUnitVehicle = MemberSpec.Prop(Aux, "SeapowerUI.MapUnitViewModel", "Unit", false);
        public static readonly MemberSpec VehicleBaseObject = MemberSpec.Prop(Aux, "SeaPower.Vehicle", "BaseObject", false);

        public static readonly MemberSpec ObjFormation = MemberSpec.Prop(Aux, "SeaPower.ObjectBase", "Formation", false);

        /// <summary>The check lines of the generic constructors (optional, as before): they need the resolved types.</summary>
        public static void Derive(GameApi api, System.Collections.Generic.ISet<string> caps) { api.MenuChecks(); }

        public static readonly SpecSet Set = SpecSet.Of("menu", typeof(MenuSpecs));
    }
}
