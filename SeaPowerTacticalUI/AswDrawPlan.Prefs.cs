// T-174 W3: the remembered "Route" flag of the area order (the part of AswDrawPlan that needs the window prefs; the stands of the controller do not compile it).
namespace VoiceBotBridge
{
    internal static partial class AswDrawPlan
    {
        public static bool AreaRouteOn() { return UiWindowPrefs.State != null && UiWindowPrefs.State.Flag(AreaRouteId, false); }

        public static void SetAreaRoute(bool on) { if (UiWindowPrefs.State != null) UiWindowPrefs.State.SetFlag(AreaRouteId, on); }

        /// <summary>T-176 W6: the "Route" flag of the course order (persistent like the area one).</summary>
        public static bool CourseRouteOn() { return UiWindowPrefs.State != null && UiWindowPrefs.State.Flag(CourseRouteId, false); }

        public static void SetCourseRoute(bool on) { if (UiWindowPrefs.State != null) UiWindowPrefs.State.SetFlag(CourseRouteId, on); }
    }
}
