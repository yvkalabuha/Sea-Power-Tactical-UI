using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): the game members of the map layer switches (module map_layer: GameApi.PumpMapLayer / PumpWindowLayer).
    /// Capability radar_layer = the game's layer list can take our entries; the row "map_layer_radar" of UiDegradation.
    /// </summary>
    internal static class MapLayerSpecs
    {
        private const string Manager = "SeapowerUI.GizmoLayerManager";
        private const string Layer = "SeapowerUI.MapLayer";

        public static readonly MemberSpec ManagerInstance = MemberSpec.Prop("radar_layer", Manager, "Instance", false, true);
        public static readonly MemberSpec Layers = MemberSpec.Field("radar_layer", Manager, "Layers", false);
        public static readonly MemberSpec LayersByName = MemberSpec.Field("radar_layer", Manager, "LayersByName", false);
        public static readonly MemberSpec LayersById = MemberSpec.Field("radar_layer", Manager, "LayersById", false);

        public static readonly MemberSpec LayerNumberField = MemberSpec.Field("radar_layer", Layer, "<LayerNumber>k__BackingField", false);
        public static readonly MemberSpec NameField = MemberSpec.Field("radar_layer", Layer, "<Name>k__BackingField", false);
        public static readonly MemberSpec DisplayNameField = MemberSpec.Field("radar_layer", Layer, "<DisplayName>k__BackingField", false);
        public static readonly MemberSpec VisibleInMenuField = MemberSpec.Field("radar_layer", Layer, "_visibleInMenu", false);
        public static readonly MemberSpec VisibleField = MemberSpec.Field("radar_layer", Layer, "_visible", false);
        public static readonly MemberSpec Visible = MemberSpec.Prop("radar_layer", Layer, "Visible", false);

        // capability radar_layer_aux (not a player function, no row in UiDegradation): these had fallbacks or were not checked; the number read for the insert position, the label property, the change notifier, the game's constructor
        public static readonly MemberSpec LayerNumber = MemberSpec.Prop("radar_layer_aux", Layer, "LayerNumber", false);
        public static readonly MemberSpec DisplayName = MemberSpec.Prop("radar_layer_aux", Layer, "DisplayName", false, false, false, "string");
        public static readonly MemberSpec NotifyChanged = MemberSpec.Method("radar_layer_aux", Layer, "OnPropertyChanged", new[] { "string" }, "void", false);
        public static readonly MemberSpec LayerCtor = MemberSpec.Ctor("radar_layer_aux", Layer, new[] { "int", "string", "bool" }, false);

        /// <summary>The Type of the layer class (the uninitialised-instance fallback needs it).</summary>
        public static readonly MemberSpec LayerType = MemberSpec.TypeOf("radar_layer", Layer, false);

        /// <summary>The manager reads options and the language table when it is built: PumpMapLayer waits for both.</summary>
        public static readonly MemberSpec OptionsManager = MemberSpec.TypeOf("radar_layer", "SeaPower.OptionsManager", false);

        /// <summary>The layer class must tell us when the player ticks it (INotifyPropertyChanged); without that radar_layer is off (it was an optional check line before).</summary>
        public static void Derive(GameApi api, ISet<string> caps)
        {
            var t = api.TypeOf(LayerType);
            bool npc = t != null && typeof(INotifyPropertyChanged).IsAssignableFrom(t);
            api.Checks.Add(new GameApi.Check { Capability = "radar_layer", Member = Layer + " : INotifyPropertyChanged", Required = false, Present = npc });
            if (!npc) caps.Remove("radar_layer");
        }

        public static readonly SpecSet Set = SpecSet.Of("map_layer", typeof(MapLayerSpecs));
    }
}
