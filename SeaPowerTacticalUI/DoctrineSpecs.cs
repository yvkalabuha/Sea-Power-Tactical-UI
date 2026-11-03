using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 M8 (ADR-0043): the game members of the Standing Orders window and of its save / load / reset / text lookup (module doctrine:
    /// DoctrineGame.cs, DoctrineHooks.cs, PatchesDoctrine.cs). Capability standing_orders = all of them; air_defense_ready is derived from the
    /// Common commands (it needs no game member of its own).
    /// </summary>
    internal static class DoctrineSpecs
    {
        private const string So = "standing_orders";
        private const string Vm = "SeapowerUI.ViewModels.TacticalDoctrineViewModel";
        private const string Tab = "SeapowerUI.ViewModels.TacticalDoctrineTabViewModel";
        private const string Cat = "SeapowerUI.ViewModels.TacticalDoctrineCategoryViewModel";
        private const string Tgl = "SeaPower.TacticalDoctrineToggleViewModel";
        private const string Td = "SeaPower.TacticalDoctrine";
        private const string Ini = "SeaPower.IniHandler";

        // the window (view model) and its tabs
        public static readonly MemberSpec VmBuildGlobal = MemberSpec.Method(So, Vm, "BuildGlobalDoctrine", new string[0], "void");
        public static readonly MemberSpec VmBuildFormation = MemberSpec.Method(So, Vm, "BuildFormationDoctrine", new string[0], "void");
        public static readonly MemberSpec VmBuildUnit = MemberSpec.Method(So, Vm, "BuildUnitDoctrine", new string[0], "void");
        public static readonly MemberSpec VmTaskforce = MemberSpec.Field(So, Vm, "_taskforce");
        public static readonly MemberSpec VmSelectedObject = MemberSpec.Field(So, Vm, "_selectedObject");
        public static readonly MemberSpec VmTabs = MemberSpec.Prop(So, Vm, "Tabs");
        public static readonly MemberSpec TabHeaderKey = MemberSpec.Prop(So, Tab, "HeaderKey");
        public static readonly MemberSpec TabItems = MemberSpec.Prop(So, Tab, "Items");
        public static readonly MemberSpec CatIconKey = MemberSpec.Prop(So, Cat, "IconKey");
        public static readonly MemberSpec CatItems = MemberSpec.Prop(So, Cat, "Items");

        // the game's own toggle (our items are real toggles)
        public static readonly MemberSpec TglParent = MemberSpec.Prop(So, Tgl, "ParentSetting");
        public static readonly MemberSpec TglIsSet = MemberSpec.Prop(So, Tgl, "IsSet");
        public static readonly MemberSpec TglValue = MemberSpec.Prop(So, Tgl, "Value");
        public static readonly MemberSpec TglResolved = MemberSpec.Prop(So, Tgl, "ResolvedValue");
        public static readonly MemberSpec TglRefresh = MemberSpec.Method(So, Tgl, "Refresh", new string[0], "void");
        public static readonly MemberSpec TglCtor = MemberSpec.Ctor(So, Tgl, new[] { Tgl, "string", "string", "bool", "Nullable`1", "Func`1" });

        // the doctrine of a unit / a group / the taskforce
        public static readonly MemberSpec TdSaveOverrides = MemberSpec.Method(So, Td, "SaveOverridesToFile", new[] { "IniHandler", "string" }, "void");
        public static readonly MemberSpec TdLoadOverrides = MemberSpec.Method(So, Td, "LoadOverridesFromFile", new[] { "IniHandler", "string" }, "void");
        public static readonly MemberSpec TdSaveGlobal = MemberSpec.Method(So, Td, "SaveGlobalDoctrineToOptions", new string[0], "bool");
        public static readonly MemberSpec TdReset = MemberSpec.Method(So, Td, "ResetToDefaults", new string[0], "bool");
        public static readonly MemberSpec TdOwner = MemberSpec.Field(So, Td, "_ownerObject");
        public static readonly MemberSpec ObjDoctrine = MemberSpec.Prop(So, "SeaPower.ObjectBase", "TacticalDoctrine");
        public static readonly MemberSpec ObjFormation = MemberSpec.Prop(So, "SeaPower.ObjectBase", "Formation");
        public static readonly MemberSpec ObjTaskforce = MemberSpec.Prop(So, "SeaPower.ObjectBase", "UnitTaskforce");
        public static readonly MemberSpec FormationDoctrine = MemberSpec.Prop(So, "SeaPower.UnitFormation", "TacticalDoctrine");
        public static readonly MemberSpec FormationTaskforce = MemberSpec.Field(So, "SeaPower.UnitFormation", "_taskforce");
        public static readonly MemberSpec FormationStations = MemberSpec.Prop(So, "SeaPower.UnitFormation", "Stations");
        public static readonly MemberSpec TaskforceDoctrine = MemberSpec.Prop(So, "SeaPower.Taskforce", "TacticalDoctrine");
        public static readonly MemberSpec StationUnit = MemberSpec.Prop(So, "SeaPower.Station", "UnitObject", false);

        // the game's options file and its ini
        public static readonly MemberSpec IniHas = MemberSpec.Method(So, Ini, "doesKeyExist", new[] { "string", "string" }, "bool");
        public static readonly MemberSpec IniRead = MemberSpec.Method(So, Ini, "readValue", new[] { "string", "string", "bool", "bool" }, "bool");
        public static readonly MemberSpec IniWrite = MemberSpec.Method(So, Ini, "writeValue", new[] { "string", "string", "bool" }, "void");
        public static readonly MemberSpec OptionsType = MemberSpec.TypeOf(So, "SeaPower.OptionsManager");
        public static readonly MemberSpec OptionsIni = MemberSpec.Prop(So, "SeaPower.OptionsManager", "OptionsIni");
        public static readonly MemberSpec OptionsWrite = MemberSpec.Method(So, "SeaPower.OptionsManager", "WriteOptions", new string[0], "void");

        // Harmony targets (installed by PatchesDoctrine until M19; the tabs of a group and of a unit are separate ids)
        public static readonly PatchSpec PatchBuildGlobal = new PatchSpec("standing_orders_build", So, Vm, "BuildGlobalDoctrine", new string[0], "void", PatchKind.Postfix, "always");
        public static readonly PatchSpec PatchBuildFormation = new PatchSpec("standing_orders_build_formation", So, Vm, "BuildFormationDoctrine", new string[0], "void", PatchKind.Postfix, "always");
        public static readonly PatchSpec PatchBuildUnit = new PatchSpec("standing_orders_build_unit", So, Vm, "BuildUnitDoctrine", new string[0], "void", PatchKind.Postfix, "always");
        public static readonly PatchSpec PatchSave = new PatchSpec("standing_orders_save", So, Td, "SaveOverridesToFile", new[] { "IniHandler", "string" }, "void", PatchKind.Postfix, "always");
        public static readonly PatchSpec PatchLoad = new PatchSpec("standing_orders_load", So, Td, "LoadOverridesFromFile", new[] { "IniHandler", "string" }, "void", PatchKind.Postfix, "always");
        public static readonly PatchSpec PatchGlobal = new PatchSpec("standing_orders_global", So, Td, "SaveGlobalDoctrineToOptions", new string[0], "bool", PatchKind.Postfix, "always");
        public static readonly PatchSpec PatchReset = new PatchSpec("standing_orders_reset", So, Td, "ResetToDefaults", new string[0], "bool", PatchKind.Postfix, "always");
        public static readonly PatchSpec PatchText = new PatchSpec("standing_orders_text", So, "SeaPower.LanguageResourceHandler", "getText", new[] { "string", "string", "bool", "bool" }, "string", PatchKind.Prefix, "always");

        /// <summary>The one-shot "air defense ready" only needs the existing commands and the flag store hooks (not tied to Standing Orders).</summary>
        public static void Derive(GameApi api, ISet<string> caps)
        {
            if (api == null) return;
            var c = api.Capabilities;
            if (c.Contains("set_emcon") && c.Contains("set_radar") && c.Contains("set_roe") && c.Contains("unit_flags")) caps.Add("air_defense_ready");
        }

        public static readonly SpecSet Set = SpecSet.Of("doctrine", typeof(DoctrineSpecs));
    }
}
