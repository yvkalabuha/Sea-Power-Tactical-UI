// T-199 C: "the game shows the loading screen" for the UI mod. The game writes the percent as (step + sub-step) * weight and never resets the sub-step between
// steps, so at the last steps ("DEM Data", "Terrain Chunks") it is >= 100 while the screen is still up. The reliable sign is LoadScreen.cameraObject.enabled
// (true from the start of ExecuteLoadScreen until "Done"). Pure (no Unity).
using System;

namespace VoiceBotBridge
{
    internal static class LoadScreenMath
    {
        /// <summary>cameraOn = LoadScreen.cameraObject.enabled (null = the member is missing: the fallback rule is used).</summary>
        public static bool Active(bool? cameraOn, double percent, string text)
        {
            if (cameraOn.HasValue) return cameraOn.Value;
            if (percent > 0 && percent < 100) return true;
            if (percent >= 100 && !string.IsNullOrEmpty(text) && !IsDone(text)) return true;
            return false;
        }

        private static bool IsDone(string text)
        {
            return text.Trim().StartsWith("Done", StringComparison.OrdinalIgnoreCase);
        }

        public static string SelfTest()
        {
            if (!Active(true, 100, "Loading Terrain Chunks ...")) return "camera on at 100% must be active";
            if (!Active(true, 0, null)) return "camera on at 0 must be active";
            if (Active(false, 50, "Loading x")) return "camera off must be inactive";
            if (Active(false, 100, "Done")) return "camera off at Done";
            if (!Active(null, 50, "x")) return "fallback 0<p<100";
            if (!Active(null, 100, "Loading Terrain Chunks ...")) return "fallback 100 and not Done";
            if (Active(null, 100, "Done")) return "fallback Done";
            if (Active(null, 0, null)) return "fallback empty";
            if (Active(null, 100, null)) return "fallback 100 without text";
            return null;
        }
    }
}
