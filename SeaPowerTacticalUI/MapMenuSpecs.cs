namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M16 (ADR-0043): the game members of the windows in the map menu (MapMenuWindows.cs): the Noesis MenuItem we insert after "World View:
    /// Open", the tree walk to find the main map window, the binding path of the World View item. Capability map_menu_windows = all of the first group
    /// (every member optional, as before: present only when all are there); map_menu_aux = the T-113 extras (submenu event, binding lookup, window
    /// model) that never gate it. Without the capability the same windows appear as layers in "Map layers".
    /// </summary>
    internal static class MapMenuSpecs
    {
        private const string Cap = "map_menu_windows";
        private const string Aux = "map_menu_aux";
        private const string Mi = "Noesis.MenuItem";
        private const string Fe = "Noesis.FrameworkElement";
        private const string Tree = "Noesis.VisualTreeHelper";
        private const string Scene = "SeapowerUI.ViewModels.SceneMapViewModel";

        public static readonly MemberSpec MainViewInstance = MemberSpec.Field(Cap, "SeapowerUI.MainGameView", "Instance", false, true);
        public static readonly MemberSpec WorldViewButtonName = MemberSpec.Prop(Cap, Scene, "WorldViewButtonName", false);
        public static readonly MemberSpec WorldViewOpen = MemberSpec.Prop(Cap, Scene, "WorldViewOpen", false);

        public static readonly MemberSpec MenuItemType = MemberSpec.TypeOf(Cap, Mi, false);
        public static readonly MemberSpec MenuItemCtor = MemberSpec.Ctor(Cap, Mi, new string[0], false);
        public static readonly MemberSpec ItemHeader = MemberSpec.Prop(Cap, "Noesis.HeaderedItemsControl", "Header", false);
        public static readonly MemberSpec ItemIsCheckable = MemberSpec.Prop(Cap, Mi, "IsCheckable", false);
        public static readonly MemberSpec ItemIsChecked = MemberSpec.Prop(Cap, Mi, "IsChecked", false);
        public static readonly MemberSpec ItemStaysOpen = MemberSpec.Prop(Cap, Mi, "StaysOpenOnClick", false);
        public static readonly MemberSpec ItemTag = MemberSpec.Prop(Cap, Fe, "Tag", false);
        public static readonly MemberSpec ItemStyle = MemberSpec.Prop(Cap, Fe, "Style", false);
        public static readonly MemberSpec ItemItems = MemberSpec.Prop(Cap, "Noesis.ItemsControl", "Items", false);
        public static readonly MemberSpec ItemDataContext = MemberSpec.Prop(Cap, Fe, "DataContext", false);
        public static readonly MemberSpec ItemParent = MemberSpec.Prop(Cap, Fe, "Parent", false);
        public static readonly MemberSpec ItemIsLoaded = MemberSpec.Prop(Cap, Fe, "IsLoaded", false);
        public static readonly MemberSpec ItemChecked = MemberSpec.Event(Cap, Mi, "Checked", false);
        public static readonly MemberSpec ItemUnchecked = MemberSpec.Event(Cap, Mi, "Unchecked", false);
        public static readonly MemberSpec ItemsInsert = MemberSpec.Method(Cap, "Noesis.ItemCollection", "Insert", new[] { "int", "object" }, "void", false);
        public static readonly MemberSpec ItemsRemove = MemberSpec.Method(Cap, "Noesis.ItemCollection", "Remove", new[] { "object" }, "bool", false);
        public static readonly MemberSpec TreeChildCount = MemberSpec.Method(Cap, Tree, "GetChildrenCount", new[] { "Noesis.DependencyObject" }, "int", false, true);
        public static readonly MemberSpec TreeChild = MemberSpec.Method(Cap, Tree, "GetChild", new[] { "Noesis.DependencyObject", "int" }, "Noesis.DependencyObject", false, true);

        // T-113 extras: optional, they never gate the capability
        public static readonly MemberSpec SubmenuOpened = MemberSpec.Event(Aux, Mi, "SubmenuOpened", false);
        public static readonly MemberSpec IsSubmenuOpen = MemberSpec.Prop(Aux, Mi, "IsSubmenuOpen", false);
        // T-228: inactive items with a tooltip; optional, a missing member leaves only the caption suffix
        public static readonly MemberSpec ItemIsEnabled = MemberSpec.Prop(Aux, "Noesis.UIElement", "IsEnabled", false);
        // T-240: geometry of the open hamburger submenu (the mod windows yield to it); optional, a missing member makes the popup rectangle unknown (all windows yield)
        public static readonly MemberSpec ElementWidth = MemberSpec.Prop(Aux, Fe, "ActualWidth", false);
        public static readonly MemberSpec ElementHeight = MemberSpec.Prop(Aux, Fe, "ActualHeight", false);
        public static readonly MemberSpec ItemVisibility = MemberSpec.Prop(Aux, "Noesis.UIElement", "IsVisible", false);
        public static readonly MemberSpec ItemToolTip = MemberSpec.Prop(Aux, Fe, "ToolTip", false);
        public static readonly MemberSpec ShowOnDisabled = MemberSpec.Method(Aux, "Noesis.ToolTipService", "SetShowOnDisabled", new[] { "Noesis.DependencyObject", "bool" }, "void", false, true);
        public static readonly MemberSpec BindingGet = MemberSpec.Method(Aux, "Noesis.BindingOperations", "GetBinding", new[] { "Noesis.DependencyObject", "Noesis.DependencyProperty" }, "Noesis.Binding", false, true);
        public static readonly MemberSpec IsCheckedProperty = MemberSpec.Prop(Aux, Mi, "IsCheckedProperty", false, true);
        public static readonly MemberSpec BindingPath = MemberSpec.Prop(Aux, "Noesis.Binding", "Path", false);
        public static readonly MemberSpec PathText = MemberSpec.Prop(Aux, "Noesis.PropertyPath", "Path", false);
        public static readonly MemberSpec WindowType = MemberSpec.TypeOf(Aux, "SeapowerUI.Window", false);
        public static readonly MemberSpec WindowDataContext = MemberSpec.Prop(Aux, "SeapowerUI.Window", "DataContext", false);
        public static readonly MemberSpec MainVmMap = MemberSpec.Prop(Aux, "SeapowerUI.ViewModels.MainGameViewModel", "Map", false);

        public static readonly SpecSet Set = SpecSet.Of("map_menu", typeof(MapMenuSpecs));
    }
}
