using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace WarThunderSocialTap
{
    static class Paths
    {
        public static readonly string DataDir = InitDataDir();
        static List<string> gameDirs;

        static string InitDataDir()
        {
            string d = Environment.GetEnvironmentVariable("WTST_DATA_DIR");
            if (string.IsNullOrEmpty(d))
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarThunderSocialTap");
            Directory.CreateDirectory(d);
            return d;
        }

        public static string In(string name) { return Path.Combine(DataDir, name); }

        /// <summary>Folders where War Thunder is installed (used for the tray icon and saved replays).</summary>
        public static List<string> GameDirs()
        {
            if (gameDirs != null) return gameDirs;
            var found = new List<string>();
            AddDir(found, Environment.GetEnvironmentVariable("WTST_WT_DIR"));
            string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            AddFromRegistry(found, Registry.CurrentUser, uninstall);
            AddFromRegistry(found, Registry.LocalMachine, uninstall);
            AddFromRegistry(found, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
            AddDir(found, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarThunder"));
            AddDir(found, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Steam\steamapps\common\War Thunder"));
            gameDirs = found;
            return found;
        }

        static void AddFromRegistry(List<string> found, RegistryKey root, string path)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(path))
                {
                    if (key == null) return;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        using (RegistryKey k = key.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string name = k.GetValue("DisplayName") as string;
                            if (name == null || !name.StartsWith("War Thunder", StringComparison.OrdinalIgnoreCase)) continue;
                            AddDir(found, k.GetValue("InstallLocation") as string);
                        }
                    }
                }
            }
            catch { }
        }

        static void AddDir(List<string> found, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                dir = dir.Trim().TrimEnd('\\');
                if (!Directory.Exists(dir)) return;
                foreach (string f in found)
                    if (string.Equals(f, dir, StringComparison.OrdinalIgnoreCase)) return;
                found.Add(dir);
            }
            catch { }
        }
    }

    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    string p = Paths.In("app.log");
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 1000000)
                    {
                        string old = p + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    class HttpResult
    {
        public int Status;          // 0 = could not connect
        public string Body = "";
        public byte[] Bytes = new byte[0];
    }

    static class Http
    {
        public static HttpResult Send(string method, string url, string bearer, string body, string contentType, int timeoutMs)
        {
            var res = new HttpResult();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "WarThunderSocialTap/1.0";
                req.KeepAlive = false;
                // Never follow redirects: a bounce to a login page must not look like success.
                req.AllowAutoRedirect = false;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                if (url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
                    req.Proxy = null;
                if (bearer != null) req.Headers["Authorization"] = "Bearer " + bearer;
                if (body != null)
                {
                    byte[] b = Encoding.UTF8.GetBytes(body);
                    req.ContentType = contentType;
                    req.ContentLength = b.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse()) Fill(res, resp);
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null)
                {
                    try { using (r) Fill(res, r); } catch { }
                }
            }
            catch { }
            return res;
        }

        public static HttpResult Get(string url, int timeoutMs)
        {
            return Send("GET", url, null, null, null, timeoutMs);
        }

        static void Fill(HttpResult res, HttpWebResponse resp)
        {
            res.Status = (int)resp.StatusCode;
            using (Stream s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                if (s != null) s.CopyTo(ms);
                res.Bytes = ms.ToArray();
                res.Body = Encoding.UTF8.GetString(res.Bytes);
            }
        }
    }
}
