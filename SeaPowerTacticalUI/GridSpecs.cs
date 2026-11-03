namespace VoiceBotBridge
{
    /// <summary>
    /// T-308 (ADR-0120): the beta's grid-to-true conversion of the game (SeaPower.Utils.UnityYawToTrueHeading, LCC terrain). Optional: absent on 261021 ->
    /// the layer is the identity (conv 0). Only GridHeadingGame.cs knows these members.
    /// </summary>
    internal static class GridSpecs
    {
        private const string Cap = "grid_heading";

        public static readonly MemberSpec YawToTrue = MemberSpec.Method(Cap, "SeaPower.Utils", "UnityYawToTrueHeading", new[] { "float", "GeoPosition" }, "float", false, true);
        public static readonly MemberSpec GeoCtor = MemberSpec.Ctor(Cap, "SeaPower.GeoPosition", new[] { "double", "double", "double" }, false);

        public static readonly SpecSet Set = SpecSet.Of("grid", typeof(GridSpecs));
    }
}
