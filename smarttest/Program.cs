using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            if (args.Length > 0 && args[0] == "save-sample") { Console.WriteLine(SmartAttackSave.Sample()); return 0; }
            if (args.Length > 1 && args[0] == "ammo") { Console.WriteLine(AmmoJson(args[1])); return 0; }
            int bad = 0;
            bad += Report("smart math", SmartAttackMath.SelfTest());
            bad += Report("smart save", SmartAttackSave.SelfTest());
            bad += Report("smart commands", SmartAttackCommandsParse.SelfTest());
            bad += Report("smart controller", SmartControllerTests.Run());
            string pick = PickSelfTest();
            if (pick == "absent") Console.WriteLine("smart pick selftest skipped (SmartPickModel absent)");
            else bad += Report("smart pick", pick);
            return bad == 0 ? 0 : 1;
        }

        /// <summary>SmartPickModel lives in stream D1's file; "absent" until that file exists (then the line is skipped).</summary>
        private static string PickSelfTest()
        {
            Type t = Type.GetType("VoiceBotBridge.SmartPickModel");
            if (t == null) return "absent";
            MethodInfo m = t.GetMethod("SelfTest", BindingFlags.Public | BindingFlags.Static);
            if (m == null) return "SmartPickModel.SelfTest missing";
            return (string)m.Invoke(null, null);
        }

        private static int Report(string name, string err)
        {
            Console.WriteLine(name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
            return err == null ? 0 : 1;
        }

        private static string D(double d) { return d.ToString("R", CultureInfo.InvariantCulture); }

        private static string AmmoJson(string path)
        {
            WireAmmo a = WireAmmo.FromIni(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
            var sb = new StringBuilder("{");
            sb.Append("\"file\":" + Json.Str(a.File));
            sb.Append(",\"wire_guided\":" + Json.Bool(a.WireGuided) + ",\"wire_controls_depth\":" + Json.Bool(a.WireControlsDepth));
            sb.Append(",\"asw\":" + Json.Bool(a.Asw) + ",\"asuw\":" + Json.Bool(a.Asuw));
            sb.Append(",\"speeds\":[" + string.Join(",", Array.ConvertAll(a.SpeedsKn, D)) + "]");
            sb.Append(",\"ranges\":[" + string.Join(",", Array.ConvertAll(a.RangesNm, D)) + "]");
            sb.Append(",\"wire_break_kn\":" + D(a.WireBreakKn) + ",\"seabed_ft\":" + D(a.SeafloorClearanceFt));
            sb.Append(",\"terminal_kn\":" + D(a.TerminalKn) + ",\"seeker_nm\":" + D(a.SeekerRangeNm));
            sb.Append(",\"activate_fraction\":" + D(a.ActivateFraction) + ",\"activate_nm\":" + D(a.ActivateRadiusNm));
            sb.Append(",\"fov\":" + D(a.SeekerFovDeg) + ",\"max_depth_ft\":" + D(a.MaxDepthFt));
            sb.Append(",\"valid\":" + Json.Bool(SmartAttackMath.ValidateAmmo(a) == null));
            return sb.Append('}').ToString();
        }
    }
}
