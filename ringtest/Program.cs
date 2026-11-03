using System;
using System.Linq;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>Runs every static string SelfTest() of the types named RangeRing* / RingTest* / RtOverlays (null = ok).</summary>
    internal static class Program
    {
        private static int Main()
        {
            int bad = 0;
            var types = typeof(Program).Assembly.GetTypes()
                .Where(t => t.Name.StartsWith("RangeRing", StringComparison.Ordinal) || t.Name.StartsWith("RingTest", StringComparison.Ordinal) || t.Name == "RtOverlays")
                .OrderBy(t => t.Name, StringComparer.Ordinal);
            foreach (var t in types)
            {
                MethodInfo m = t.GetMethod("SelfTest", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(string)) continue;
                string err;
                try { err = (string)m.Invoke(null, null); }
                catch (Exception e) { err = "exception " + (e.InnerException ?? e); }
                Console.WriteLine(t.Name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
                if (err != null) bad++;
            }
            string drawGuard = DrawGuard.SelfTest();
            Console.WriteLine("DrawGuard selftest " + (drawGuard == null ? "ok" : "FAILED: " + drawGuard));
            if (drawGuard != null) bad++;
            return bad == 0 ? 0 : 1;
        }
    }
}
