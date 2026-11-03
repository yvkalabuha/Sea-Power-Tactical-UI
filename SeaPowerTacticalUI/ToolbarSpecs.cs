namespace VoiceBotBridge
{
    /// <summary>
    /// T-174 (ADR-0073 section 3): the game members of the two ASW / CAP buttons on the game panel (ToolbarButtons.cs). Capability game_toolbar; every member
    /// is optional (Required=false): without it no buttons appear, the map menu items and the hotkeys stay. Own specs on purpose (MapMenuSpecs / RadarSpecs
    /// are not shared). Signatures confirmed by a MetadataLoadContext probe over Noesis.NoesisGUI.dll / Seapower-Scripts.dll (game 0.8.7):
    /// MainGameView.Instance static field; FrameworkElement.FindName(string) object, Parent, Style, Tag, IsLoaded, ToolTip (object), Width / Height (float),
    /// Horizontal/VerticalAlignment; UIElement.Visibility (Noesis.Visibility: Visible/Hidden/Collapsed), Focusable; Grid.ColumnDefinitions
    /// (ColumnDefinitionCollection: Insert(int, ColumnDefinition), Remove(ColumnDefinition), Count), RowDefinitions, static Get/SetColumn, Get/SetColumnSpan,
    /// Get/SetRow(DependencyObject); ColumnDefinition() + Width (GridLength; GridLength.Auto static property); Panel.Children (UIElementCollection:
    /// Add/Remove(UIElement), Count, get_Item(int)); ToggleButton() + IsChecked (bool?); ButtonBase.Click (RoutedEventHandler); ContentControl.Content;
    /// Image() + Source (ImageSource) + Stretch (None, Fill, Uniform, UniformToFill); TextureSource(Texture2D) / (Texture).
    /// </summary>
    internal static class ToolbarSpecs
    {
        private const string Cap = "game_toolbar";
        private const string Fe = "Noesis.FrameworkElement";
        private const string Grid = "Noesis.Grid";

        public static readonly MemberSpec MainViewInstance = MemberSpec.Field(Cap, "SeapowerUI.MainGameView", "Instance", false, true);
        public static readonly MemberSpec FindName = MemberSpec.Method(Cap, Fe, "FindName", new[] { "string" }, "object", false);
        public static readonly MemberSpec Parent = MemberSpec.Prop(Cap, Fe, "Parent", false);
        public static readonly MemberSpec Style = MemberSpec.Prop(Cap, Fe, "Style", false);
        public static readonly MemberSpec Tag = MemberSpec.Prop(Cap, Fe, "Tag", false);
        public static readonly MemberSpec IsLoaded = MemberSpec.Prop(Cap, Fe, "IsLoaded", false);
        public static readonly MemberSpec ToolTip = MemberSpec.Prop(Cap, Fe, "ToolTip", false);
        public static readonly MemberSpec Width = MemberSpec.Prop(Cap, Fe, "Width", false);
        public static readonly MemberSpec Height = MemberSpec.Prop(Cap, Fe, "Height", false);
        public static readonly MemberSpec HAlign = MemberSpec.Prop(Cap, Fe, "HorizontalAlignment", false);
        public static readonly MemberSpec VAlign = MemberSpec.Prop(Cap, Fe, "VerticalAlignment", false);
        public static readonly MemberSpec Visibility = MemberSpec.Prop(Cap, "Noesis.UIElement", "Visibility", false);
        public static readonly MemberSpec Focusable = MemberSpec.Prop(Cap, "Noesis.UIElement", "Focusable", false);

        public static readonly MemberSpec GridType = MemberSpec.TypeOf(Cap, Grid, false);
        public static readonly MemberSpec ColumnDefinitions = MemberSpec.Prop(Cap, Grid, "ColumnDefinitions", false);
        public static readonly MemberSpec RowDefinitions = MemberSpec.Prop(Cap, Grid, "RowDefinitions", false);
        public static readonly MemberSpec GetColumn = MemberSpec.Method(Cap, Grid, "GetColumn", new[] { "Noesis.DependencyObject" }, "int", false, true);
        public static readonly MemberSpec SetColumn = MemberSpec.Method(Cap, Grid, "SetColumn", new[] { "Noesis.DependencyObject", "int" }, "void", false, true);
        public static readonly MemberSpec GetColumnSpan = MemberSpec.Method(Cap, Grid, "GetColumnSpan", new[] { "Noesis.DependencyObject" }, "int", false, true);
        public static readonly MemberSpec SetColumnSpan = MemberSpec.Method(Cap, Grid, "SetColumnSpan", new[] { "Noesis.DependencyObject", "int" }, "void", false, true);
        public static readonly MemberSpec GetRow = MemberSpec.Method(Cap, Grid, "GetRow", new[] { "Noesis.DependencyObject" }, "int", false, true);
        public static readonly MemberSpec SetRow = MemberSpec.Method(Cap, Grid, "SetRow", new[] { "Noesis.DependencyObject", "int" }, "void", false, true);

        public static readonly MemberSpec ColumnDefCtor = MemberSpec.Ctor(Cap, "Noesis.ColumnDefinition", new string[0], false);
        public static readonly MemberSpec ColumnDefWidth = MemberSpec.Prop(Cap, "Noesis.ColumnDefinition", "Width", false);
        public static readonly MemberSpec GridLengthAuto = MemberSpec.Prop(Cap, "Noesis.GridLength", "Auto", false, true);
        public static readonly MemberSpec ColsInsert = MemberSpec.Method(Cap, "Noesis.ColumnDefinitionCollection", "Insert", new[] { "int", "Noesis.ColumnDefinition" }, "void", false);
        public static readonly MemberSpec ColsRemove = MemberSpec.Method(Cap, "Noesis.ColumnDefinitionCollection", "Remove", new[] { "Noesis.ColumnDefinition" }, "bool", false);
        public static readonly MemberSpec ColsCount = MemberSpec.Prop(Cap, "Noesis.ColumnDefinitionCollection", "Count", false);

        public static readonly MemberSpec RowsCount = MemberSpec.Prop(Cap, "Noesis.RowDefinitionCollection", "Count", false);

        public static readonly MemberSpec Children = MemberSpec.Prop(Cap, "Noesis.Panel", "Children", false);
        public static readonly MemberSpec ChildAdd = MemberSpec.Method(Cap, "Noesis.UIElementCollection", "Add", new[] { "Noesis.UIElement" }, "void", false);
        public static readonly MemberSpec ChildRemove = MemberSpec.Method(Cap, "Noesis.UIElementCollection", "Remove", new[] { "Noesis.UIElement" }, "bool", false);
        public static readonly MemberSpec ChildCount = MemberSpec.Prop(Cap, "Noesis.UIElementCollection", "Count", false);
        public static readonly MemberSpec ChildItem = MemberSpec.Method(Cap, "Noesis.UIElementCollection", "get_Item", new[] { "int" }, "Noesis.UIElement", false);

        public static readonly MemberSpec ToggleCtor = MemberSpec.Ctor(Cap, "Noesis.ToggleButton", new string[0], false);
        public static readonly MemberSpec IsChecked = MemberSpec.Prop(Cap, "Noesis.ToggleButton", "IsChecked", false);
        public static readonly MemberSpec Click = MemberSpec.Event(Cap, "Noesis.ButtonBase", "Click", false);
        public static readonly MemberSpec Content = MemberSpec.Prop(Cap, "Noesis.ContentControl", "Content", false);

        public static readonly MemberSpec ImageCtor = MemberSpec.Ctor(Cap, "Noesis.Image", new string[0], false);
        public static readonly MemberSpec ImageSource = MemberSpec.Prop(Cap, "Noesis.Image", "Source", false);
        public static readonly MemberSpec ImageStretch = MemberSpec.Prop(Cap, "Noesis.Image", "Stretch", false);
        public static readonly MemberSpec TextureSourceCtor = MemberSpec.Ctor(Cap, "Noesis.TextureSource", new[] { "UnityEngine.Texture" }, false);

        public static readonly SpecSet Set = SpecSet.Of("game_toolbar", typeof(ToolbarSpecs));
    }
}
