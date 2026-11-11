using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace WarThunderSocialTap
{
    /// <summary>Publishes / clears the SocialTap integration status card (scope: integration.status).</summary>
    static class SocialTap
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static string access = Secrets.AccessToken;
        static string refresh = Secrets.RefreshToken;

        static string TokenPath { get { return Paths.In("tokens.dat"); } }

        /// <summary>True while no token has been pasted into Secrets.cs: nothing is sent, only logged.</summary>
        public static bool DryRun
        {
            get { return string.IsNullOrEmpty(Secrets.AccessToken) || Secrets.AccessToken.StartsWith("PASTE_"); }
        }

        public static void Init()
        {
            // If the app renewed the token earlier, keep using the renewed pair -
            // unless a different token has been hardcoded since.
            try
            {
                if (!File.Exists(TokenPath)) return;
                string[] lines = File.ReadAllLines(TokenPath);
                if (lines.Length >= 3 && lines[0] == Secrets.AccessToken)
                {
                    access = lines[1];
                    refresh = lines[2];
                }
            }
            catch { }
        }

        /// <summary>Returns the HTTP status (200 = published, 0 = no connection).</summary>
        public static int Put(string title, string subtitle)
        {
            if (DryRun)
            {
                Log.Write("[no token yet - not sent] " + title + " | " + subtitle);
                return 200;
            }
            var body = new Dictionary<string, string>();
            body["title"] = Limit(title, 128);
            body["subtitle"] = Limit(subtitle, 256);
            body["background_photo"] = Config.BackgroundPhoto;
            body["visibility"] = Config.Visibility;
            HttpResult r = Http.Send("PUT", Config.SocialTapBase + "/api/integration-status", access,
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
                Log.Write("[no token yet - not sent] clear card");
                return 200;
            }
            HttpResult r = Http.Send("DELETE", Config.SocialTapBase + "/api/integration-status", access, null, null, 10000);
            Log.Write("Cleared card: HTTP " + r.Status);
            return r.Status;
        }

        /// <summary>Standard OAuth refresh_token grant. Only possible when RefreshToken and ClientId are set.</summary>
        public static bool TryRefresh()
        {
            if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(Secrets.ClientId)) return false;
            string form = "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refresh) +
                          "&client_id=" + Uri.EscapeDataString(Secrets.ClientId);
            HttpResult r = Http.Send("POST", Config.SocialTapBase + "/oauth/token", null, form,
                "application/x-www-form-urlencoded", 15000);
            if (r.Status != 200)
            {
                Log.Write("Token refresh failed: HTTP " + r.Status);
                return false;
            }
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(r.Body);
                object a;
                if (!d.TryGetValue("access_token", out a) || !(a is string) || ((string)a).Length == 0) return false;
                access = (string)a;
                object rt;
                if (d.TryGetValue("refresh_token", out rt) && rt is string && ((string)rt).Length > 0) refresh = (string)rt;
                File.WriteAllLines(TokenPath, new string[] { Secrets.AccessToken, access, refresh });
                Log.Write("Access token renewed");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Token refresh response unreadable: " + ex.Message);
                return false;
            }
        }

        static string Limit(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
