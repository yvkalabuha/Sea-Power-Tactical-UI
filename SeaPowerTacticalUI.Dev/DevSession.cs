using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoiceBotBridge.Dev
{
    /// <summary>session.json (port, token, pid) for the MCP client, next to the dev mod config.</summary>
    internal static class DevSession
    {
        public const string FileName = "session.json";

        public static string NewToken()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static void Write(string dir, int port, string token, int pid, string ver, string started, bool fire)
        {
            Directory.CreateDirectory(dir);
            var d = new Dictionary<string, object>
            {
                { "port", port }, { "token", token }, { "pid", pid }, { "dev_version", ver }, { "started", started }, { "fire_commands", fire },
            };
            string path = Path.Combine(dir, FileName), tmp = path + ".tmp";
            File.WriteAllText(tmp, DevJson.Write(d), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static void Delete(string dir)
        {
            try { File.Delete(Path.Combine(dir, FileName)); } catch (Exception) { }
        }
    }
}
