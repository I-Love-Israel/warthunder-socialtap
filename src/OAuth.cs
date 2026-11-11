using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WarThunderSocialTap
{
    /// <summary>
    /// One-time "Connect to SocialTap" login: OAuth authorization-code flow with PKCE.
    /// Opens the SocialTap consent page in your browser, catches the redirect on a local port,
    /// and swaps the code for an access token + refresh token.
    /// </summary>
    static class OAuth
    {
        // Register exactly this as the callback / redirect URL of your SocialTap OAuth client.
        public const string RedirectUri = "http://127.0.0.1:8765/callback";
        const int Port = 8765;
        const string Scope = "integration.status";

        static int running;

        /// <summary>Returns null on success, otherwise a short message saying what went wrong.</summary>
        public static string Connect()
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return "A login is already in progress - check your browser.";
            try { return Run(); }
            catch (Exception ex) { Log.Write("Login error: " + ex); return "Login failed: " + ex.Message; }
            finally { Interlocked.Exchange(ref running, 0); }
        }

        static string Run()
        {
            string clientId = SocialTap.ClientId;
            if (string.IsNullOrEmpty(clientId)) return "No Client ID yet. Put it in src/Secrets.cs and run install.ps1 again.";

            string verifier = Base64Url(RandomBytes(32));
            string challenge;
            using (SHA256 sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomBytes(16));

            var listener = new TcpListener(IPAddress.Loopback, Port);
            try { listener.Start(); }
            catch (SocketException) { return "Port " + Port + " is in use by another program. Close it and try again."; }

            string code = null, error = null;
            try
            {
                string url = SocialTap.BaseUrl + "/oauth/authorize?response_type=code" +
                             "&client_id=" + Uri.EscapeDataString(clientId) +
                             "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                             "&scope=" + Uri.EscapeDataString(Scope) +
                             "&state=" + state +
                             "&code_challenge=" + challenge +
                             "&code_challenge_method=S256";
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WTST_NO_BROWSER"))) Process.Start(url);
                else Log.Write("Authorize URL: " + url);
                Log.Write("Login started - waiting for SocialTap to redirect back");

                DateTime deadline = DateTime.Now.AddMinutes(5);
                while (code == null && error == null && DateTime.Now < deadline)
                {
                    if (!listener.Pending()) { Thread.Sleep(200); continue; }
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        Dictionary<string, string> q = ReadCallback(client);
                        string page;
                        if (q == null) page = "Nothing to see here.";
                        else if (q.ContainsKey("error"))
                        {
                            error = "SocialTap said: " + q["error"] + (q.ContainsKey("error_description") ? " - " + q["error_description"] : "");
                            page = "Login was not completed. You can close this tab.";
                        }
                        else if (!q.ContainsKey("state") || q["state"] != state) page = "This login link is out of date. Start again from the tray icon.";
                        else if (!q.ContainsKey("code")) page = "No code received. Start again from the tray icon.";
                        else
                        {
                            code = q["code"];
                            page = "Connected! War Thunder will now show on your SocialTap profile. You can close this tab.";
                        }
                        Respond(client, page);
                    }
                }
            }
            finally { listener.Stop(); }

            if (error != null) return error;
            if (code == null) return "Timed out waiting for the SocialTap login.";

            string form = "grant_type=authorization_code" +
                          "&code=" + Uri.EscapeDataString(code) +
                          "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                          "&client_id=" + Uri.EscapeDataString(clientId) +
                          "&code_verifier=" + verifier;
            HttpResult r = Http.Send("POST", SocialTap.BaseUrl + "/oauth/token", null, form, "application/x-www-form-urlencoded", 15000);
            if (r.Status != 200)
            {
                Log.Write("Token exchange failed: HTTP " + r.Status);
                return "SocialTap refused the login code (HTTP " + r.Status + ").";
            }
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.Body);
            object a, rt;
            if (!d.TryGetValue("access_token", out a) || !(a is string) || ((string)a).Length == 0) return "SocialTap did not return a token.";
            d.TryGetValue("refresh_token", out rt);
            SocialTap.SetTokens((string)a, rt as string);
            Log.Write("Connected to SocialTap" + (rt is string ? " (with refresh token)" : " (no refresh token given)"));
            return null;
        }

        // Reads "GET /callback?code=...&state=... HTTP/1.1" and returns the query values (null if it's some other request).
        static Dictionary<string, string> ReadCallback(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
                string first = reader.ReadLine();
                string line;
                while ((line = reader.ReadLine()) != null && line.Length > 0) { } // skip headers
                if (first == null) return null;
                string[] parts = first.Split(' ');
                if (parts.Length < 2 || !parts[1].StartsWith("/callback")) return null;
                var q = new Dictionary<string, string>();
                int qm = parts[1].IndexOf('?');
                if (qm < 0) return q;
                foreach (string pair in parts[1].Substring(qm + 1).Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    q[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                }
                return q;
            }
            catch { return null; }
        }

        static void Respond(TcpClient client, string message)
        {
            try
            {
                string html = "<!doctype html><meta charset=\"utf-8\"><title>War Thunder > SocialTap</title>" +
                              "<body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;text-align:center;padding-top:15vh\">" +
                              "<h2>" + WebUtility.HtmlEncode(message) + "</h2></body>";
                byte[] body = Encoding.UTF8.GetBytes(html);
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                                                      body.Length + "\r\nConnection: close\r\n\r\n");
                NetworkStream s = client.GetStream();
                s.Write(head, 0, head.Length);
                s.Write(body, 0, body.Length);
                s.Flush();
            }
            catch { }
        }

        static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return b;
        }

        static string Base64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
