namespace VoiceBotBridge
{
    /// <summary>
    /// T-094: the game members of the buoy revive. SceneCreator.LaunchWeapons (called once per loaded mission) launches only the weapons whose
    /// launch platform it finds; a sonobuoy whose aircraft is gone has no HomeBase in the save and stays _isLaunched=false (no icon, sensors off,
    /// life frozen). The postfix launches such buoys the way the game launches the normal ones: Container_Launch(null, pos, 0, null).
    /// All optional: without the patch the behaviour is the one of the game (selfcheck shows the missing member).
    /// </summary>
    internal static class BuoyReviveSpecs
    {
        private const string Cap = "buoy_revive";
        private const string Wb = "SeaPower.WeaponBase";

        public static readonly PatchSpec PatchLaunchWeapons = new PatchSpec("buoy_revive", Cap, "SeaPower.SceneCreator", "LaunchWeapons", new string[0], "void", PatchKind.Postfix, "always", false);
        public static readonly MemberSpec WeaponsToLaunch = MemberSpec.Field(Cap, "SeaPower.SceneCreator", "_weaponsToLaunch", false);   // Dictionary<WeaponBase, SharedUnitData>
        public static readonly MemberSpec IsLaunched = MemberSpec.Method(Cap, Wb, "isLaunched", new string[0], "bool", false);
        public static readonly MemberSpec LaunchPlatform = MemberSpec.Field(Cap, Wb, "_launchPlatform", false);
        public static readonly MemberSpec SkipReport = MemberSpec.Field(Cap, Wb, "_skipWeaponReportLaunch", false);
        public static readonly MemberSpec Ap = MemberSpec.Field(Cap, Wb, "_ap", false);
        public static readonly MemberSpec ContainerLaunch = MemberSpec.Method(Cap, Wb, "Container_Launch", new[] { "ObjectBase", "Vector3", "Vector3", "WeaponSystem" }, "void", false);
        public static readonly MemberSpec IsASaveFile = MemberSpec.Prop(Cap, "SeaPower.SaveLoadManager", "IsASaveFile", false, true);
        public static readonly MemberSpec Taskforce = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "_taskforce", false);
        public static readonly MemberSpec IsDestroyed = MemberSpec.Prop(Cap, "SeaPower.ObjectBase", "IsDestroyed", false);

        // T-296: the status text of a buoy without a launch platform (prefix of the private view-model method)
        private const string Vm = "SeapowerUI.ViewModels.ObjectSonobouyControlViewModel";
        public static readonly PatchSpec PatchSonobuoyStatus = new PatchSpec("buoy_status", Cap, Vm, "SonobouyStatus", new[] { "Bomb" }, "string", PatchKind.Prefix, "always", false);   // T-296
        public static readonly MemberSpec Sensors = MemberSpec.Field(Cap, Wb, "_sensors", false);   // TrulyObservableCollection<SensorSystem>
        public static readonly MemberSpec InTransmitterRange = MemberSpec.Method(Cap, "SeaPower.BaseSystem", "IsWithinTransmitterRange", new string[0], "bool", false);
        public static readonly MemberSpec TransmitterRange = MemberSpec.Field(Cap, "SeaPower.AmmunitionParameters", "_transmitterRange", false);
        public static readonly MemberSpec MountObject = MemberSpec.Field(Cap, "SeaPower.Bomb", "_mountObject", false);   // GameObject
        public static readonly MemberSpec NoLayer = MemberSpec.Field(Cap, "SeaPower.Bomb", "_deployedWithNoLayer", false);

        public static readonly SpecSet Set = SpecSet.Of("buoy_revive", typeof(BuoyReviveSpecs));
    }
}
