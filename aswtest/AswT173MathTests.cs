using System;

namespace VoiceBotBridge
{
    // T-173: the pure maths (the selftests live in the math files; here they are chained for the stand "t173-math").
    internal static class AswT173MathTests
    {
        public static string Run()
        {
            return AswStrikeMath.SelfTest() ?? AswLaneMath.SelfTest() ?? AttackHostility.SelfTest() ?? Names();
        }

        private static string Names()
        {
            // the strike prefix is a known group prefix and the order renames an automatic name
            if (AswGroupNames.SelfTest() != null) return "names: " + AswGroupNames.SelfTest();
            return null;
        }
    }
}
