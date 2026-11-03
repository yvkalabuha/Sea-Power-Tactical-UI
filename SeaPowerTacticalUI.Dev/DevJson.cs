using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Dev
{
    /// <summary>A value that is already JSON text (e.g. a diag section); written verbatim.</summary>
    internal sealed class DevRaw
    {
        public readonly string Text;
        public DevRaw(string text) { Text = string.IsNullOrEmpty(text) ? "null" : text; }
    }

    /// <summary>Writer for dict/list/string/number/bool/null trees. Parsing is Common/Json.cs.</summary>
    internal static class DevJson
    {
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            Put(sb, v);
            return sb.ToString();
        }

        private static void Put(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var raw = v as DevRaw;
            if (raw != null) { sb.Append(raw.Text); return; }
            var s = v as string;
            if (s != null) { sb.Append(Json.Str(s)); return; }
            if (v is bool) { sb.Append(Json.Bool((bool)v)); return; }
            if (v is double) { sb.Append(Json.Num((double)v)); return; }
            if (v is int || v is long || v is short || v is byte) { sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is float) { sb.Append(Json.Num((float)v)); return; }
            var d = v as IDictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Json.Str(kv.Key)).Append(':');
                    Put(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var e = v as IEnumerable;
            if (e != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in e)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Put(sb, o);
                }
                sb.Append(']');
                return;
            }
            sb.Append(Json.Str(v.ToString()));
        }
    }
}
