using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace WarThunderSocialTap
{
    /// <summary>Publishes / clears the SocialTap integration status card (scope: integration.status).</summary>
    static class SocialTap
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly object Gate = new object();
        static string access = Secrets.AccessToken;
        static string refresh = Secrets.RefreshToken;

        public static readonly string BaseUrl = InitBase();

        public static string ClientId { get { return Secrets.ClientId; } }

        static string TokenPath { get { return Paths.In("tokens.dat"); } }

        static string InitBase()
        {
            string b = Environment.GetEnvironmentVariable("WTST_SOCIALTAP_URL");
            if (string.IsNullOrEmpty(b)) b = Config.SocialTapBase;
            return b.TrimEnd('/');
        }

        /// <summary>True while there is no token (none hardcoded and not connected yet): nothing is sent, only logged.</summary>
        public static bool DryRun
        {
            get { lock (Gate) return string.IsNullOrEmpty(access) || access.StartsWith("PASTE_"); }
        }

        public static void Init()
        {
            // Tokens obtained through "Connect to SocialTap" (or renewed later) are kept on disk,
            // encrypted for your Windows account. They are used unless a different token has been
            // hardcoded into Secrets.cs since they were saved.
            try
            {
                if (!File.Exists(TokenPath)) return;
                byte[] raw = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
                string[] lines = Encoding.UTF8.GetString(raw).Split('\n');
                if (lines.Length >= 3 && lines[0] == Secrets.AccessToken)
                {
                    lock (Gate) { access = lines[1]; refresh = lines[2]; }
                }
            }
            catch (Exception ex) { Log.Write("Saved login unreadable: " + ex.Message); }
        }

        public static void SetTokens(string newAccess, string newRefresh)
        {
            lock (Gate)
            {
                access = newAccess;
                if (!string.IsNullOrEmpty(newRefresh)) refresh = newRefresh;
                try
                {
                    byte[] raw = Encoding.UTF8.GetBytes(Secrets.AccessToken + "\n" + access + "\n" + refresh);
                    File.WriteAllBytes(TokenPath, ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
                }
                catch (Exception ex) { Log.Write("Could not save login: " + ex.Message); }
            }
        }

        /// <summary>Returns the HTTP status (200 = published, 0 = no connection).</summary>
        public static int Put(string title, string subtitle)
        {
            if (DryRun)
            {
                Log.Write("[not connected - not sent] " + title + " | " + subtitle);
                return 200;
            }
            var body = new Dictionary<string, string>();
            body["title"] = Limit(title, 128);
            body["subtitle"] = Limit(subtitle, 256);
            body["background_photo"] = Config.BackgroundPhoto;
            body["visibility"] = Config.Visibility;
            HttpResult r = Http.Send("PUT", BaseUrl + "/api/integration-status", Token(),
                Json.Serialize(body), "application/json; charset=utf-8", 15000);
            if (r.Status == 200 && r.Body.IndexOf("integration_status", StringComparison.Ordinal) < 0)
            {
                Log.Write("SocialTap answered 200 but not with a status card - treating as failure");
                return 500;
            }
            if (r.Status == 200) Log.Write("Published: " + title + " | " + subtitle);
            else Log.Write("Publish failed: HTTP " + r.Status + " " + Limit(r.Body.Replace('\n', ' '), 200));
            return r.Status;
        }

        public static int Delete()
        {
            if (DryRun)
            {
                Log.Write("[not connected - not sent] clear card");
                return 200;
            }
            HttpResult r = Http.Send("DELETE", BaseUrl + "/api/integration-status", Token(), null, null, 10000);
            Log.Write("Cleared card: HTTP " + r.Status);
            return r.Status;
        }

        /// <summary>Standard OAuth refresh_token grant. Needs a refresh token and the Client ID.</summary>
        public static bool TryRefresh()
        {
            string rt;
            lock (Gate) rt = refresh;
            if (string.IsNullOrEmpty(rt) || string.IsNullOrEmpty(ClientId)) return false;
            string form = "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(rt) +
                          "&client_id=" + Uri.EscapeDataString(ClientId);
            HttpResult r = Http.Send("POST", BaseUrl + "/oauth/token", null, form,
                "application/x-www-form-urlencoded", 15000);
            if (r.Status != 200)
            {
                Log.Write("Token renewal failed: HTTP " + r.Status);
                return false;
            }
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(r.Body);
                object a, newRt;
                if (!d.TryGetValue("access_token", out a) || !(a is string) || ((string)a).Length == 0) return false;
                d.TryGetValue("refresh_token", out newRt);
                SetTokens((string)a, newRt as string);
                Log.Write("Access token renewed");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Token renewal response unreadable: " + ex.Message);
                return false;
            }
        }

        static string Token() { lock (Gate) return access; }

        static string Limit(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
