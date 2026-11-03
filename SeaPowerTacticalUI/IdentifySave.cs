using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-111: identify statuses kept next to the game save (like the ASW file). Pure, built on Common/Json.cs.

    internal sealed class IdentifySaveItem { public int Id; public IdentifyResult Result; public double AtS; }

    internal sealed class IdentifySaveData
    {
        public int Format = 1;
        public double MissionTimeS;
        public string SavePath;
        public SavStamp Sav;   // T-198 (ADR-0086): stamp of the game save the file was written for; null = old file
        public List<IdentifySaveItem> Items = new List<IdentifySaveItem>();
    }

    internal static class IdentifySave
    {
        public static string Key(string savPath) { return SaveSlotStore.Key(savPath); }

        public static bool Fresh(IdentifySaveData d, double missionTimeS, double tolS = 5)
        {
            return d != null && Math.Abs(d.MissionTimeS - missionTimeS) <= tolS;
        }

        public static IdentifySaveData FromStore(IdentifyStore s, double missionTimeS, string savePath)
        {
            var d = new IdentifySaveData { MissionTimeS = missionTimeS, SavePath = savePath };
            foreach (var kv in s.All)
                if (kv.Value.Result != IdentifyResult.None)
                    d.Items.Add(new IdentifySaveItem { Id = kv.Key, Result = kv.Value.Result, AtS = kv.Value.AtS });
            d.Items.Sort((a, b) => a.Id.CompareTo(b.Id));
            return d;
        }

        public static void ToStore(IdentifySaveData d, IdentifyStore s)
        {
            foreach (var i in d.Items) s.Record(i.Id, i.Result, i.AtS);
        }

        private static string Name(IdentifyResult r)
        {
            switch (r)
            {
                case IdentifyResult.Answered: return "answered";
                case IdentifyResult.Refused: return "refused";
                case IdentifyResult.Silent: return "silent";
                case IdentifyResult.NoLink: return "nolink";
                default: return "none";
            }
        }

        private static IdentifyResult FromName(string n)
        {
            switch (n)
            {
                case "answered": return IdentifyResult.Answered;
                case "refused": return IdentifyResult.Refused;
                case "silent": return IdentifyResult.Silent;
                case "nolink": return IdentifyResult.NoLink;
                default: return IdentifyResult.None;
            }
        }

        public static string ToJson(IdentifySaveData d)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"format\":").Append(d.Format.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mission_time_s\":").Append(Json.Num(d.MissionTimeS));
            sb.Append(",\"save_path\":").Append(Json.Str(d.SavePath));
            if (d.Sav != null) sb.Append(",\"sav\":").Append(d.Sav.ToJson());
            sb.Append(",\"items\":[");
            bool first = true;
            foreach (var i in d.Items)
            {
                if (i.Result == IdentifyResult.None) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(i.Id.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"result\":").Append(Json.Str(Name(i.Result)))
                  .Append(",\"at_s\":").Append(Json.Num(i.AtS)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static IdentifySaveData Parse(string json, out string err)
        {
            err = null;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) { err = "not_an_object"; return null; }
                object f;
                if (!root.TryGetValue("format", out f) || !(f is double) || (int)(double)f != 1) { err = "unsupported_format"; return null; }
                var d = new IdentifySaveData();
                object v;
                if (root.TryGetValue("mission_time_s", out v) && v is double) d.MissionTimeS = (double)v;
                if (root.TryGetValue("save_path", out v)) d.SavePath = v as string;
                d.Sav = root.TryGetValue("sav", out v) ? SavStamp.FromJson(v) : null;
                var items = root.TryGetValue("items", out v) ? v as List<object> : null;
                foreach (object o in items ?? new List<object>())
                {
                    var m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    object id, rs, at;
                    if (!m.TryGetValue("id", out id) || !(id is double)) continue;
                    m.TryGetValue("result", out rs);
                    var r = FromName(rs as string);
                    if (r == IdentifyResult.None) continue;
                    m.TryGetValue("at_s", out at);
                    d.Items.Add(new IdentifySaveItem { Id = (int)(double)id, Result = r, AtS = at is double ? (double)at : 0 });
                }
                return d;
            }
            catch (Exception e)
            {
                err = "parse: " + e.Message;
                return null;
            }
        }
    }
}
