namespace VoiceBotBridge
{
    /// <summary>
    /// T-165 / ADR-0062: the game members of light smoke. All optional: a missing one takes its capability away and the option is "unavailable" in the settings window.
    /// light_smoke = the spawn point of effects (EffectsManager.spawnEffect, postfix); light_smoke_direct = pool spawns that bypass it
    /// (launcher smoke, dust, barrel smoke; postfix on ParticleUtils.SetAutoDestroyAndCustomWorldSpace, autodestroy=true only);
    /// light_smoke_chaff = chaff / flare clouds (prefix + postfix on ChaffCloud.instantiateChaffRound, the new items of the private list _clouds).
    /// </summary>
    internal static class PerfSpecs
    {
        private const string Cap = "light_smoke";
        private const string CapDirect = "light_smoke_direct";
        private const string CapChaff = "light_smoke_chaff";

        public static readonly PatchSpec PatchSpawnEffect = new PatchSpec("light_smoke", Cap, "SeaPower.EffectsManager", "spawnEffect", new[] { "EffectParameters", "float" }, "GameObject", PatchKind.Postfix, "always", false);
        public static readonly PatchSpec PatchDirect = new PatchSpec("light_smoke_direct", CapDirect, "SeaPower.ParticleUtils", "SetAutoDestroyAndCustomWorldSpace", new[] { "GameObject", "bool", "List`1" }, "ParticleAutoDestroy", PatchKind.Postfix, "always", false);
        public static readonly PatchSpec PatchChaff = new PatchSpec("light_smoke_chaff", CapChaff, "SeaPower.ChaffCloud", "instantiateChaffRound", new string[0], "void", PatchKind.PrefixPostfix, "always", false);
        public static readonly MemberSpec ChaffClouds = MemberSpec.Field(CapChaff, "SeaPower.ChaffCloud", "_clouds", false);

        public static readonly SpecSet Set = SpecSet.Of("perf", typeof(PerfSpecs));
    }
}
