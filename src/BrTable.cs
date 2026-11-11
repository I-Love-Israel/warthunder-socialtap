using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace WarThunderSocialTap
{
    /// <summary>
    /// Vehicle id -> battle rating. The game's local page only reports which vehicle you are in,
    /// so ratings are looked up in the public War Thunder datamine and cached on disk.
    /// </summary>
    static class BrTable
    {
        static volatile Dictionary<string, int[]> table = new Dictionary<string, int[]>();
        static DateTime nextAttempt = DateTime.MinValue;
        static int updating;

        static string CachePath { get { return Paths.In("br-cache.tsv"); } }

        public static int Count { get { return table.Count; } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var t = new Dictionary<string, int[]>();
                foreach (string line in File.ReadAllLines(CachePath))
                {
                    string[] p = line.Split('\t');
                    if (p.Length != 4) continue;
                    t[p[0]] = new int[] { int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]) };
                }
                table = t;
                Log.Write("BR table loaded from cache: " + t.Count + " vehicles");
            }
            catch (Exception ex) { Log.Write("BR cache unreadable: " + ex.Message); }
        }

        /// <summary>Refreshes the cache in the background when it is missing or older than a week.</summary>
        public static void EnsureFresh()
        {
            DateTime now = DateTime.Now;
            if (now < nextAttempt) return;
            bool stale = !File.Exists(CachePath) ||
                         (now - File.GetLastWriteTime(CachePath)).TotalDays > Config.BrCacheDays ||
                         table.Count == 0;
            if (!stale) { nextAttempt = now.AddHours(1); return; }
            if (Interlocked.Exchange(ref updating, 1) == 1) return;
            nextAttempt = now.AddMinutes(30);
            var th = new Thread(Download);
            th.IsBackground = true;
            th.Start();
        }

        public static double? Lookup(string vehicleId)
        {
            if (vehicleId == null) return null;
            int[] ranks;
            if (!table.TryGetValue(vehicleId, out ranks)) return null;
            int idx = Config.BrMode == "AB" ? 0 : (Config.BrMode == "SB" ? 2 : 1);
            int rank = ranks[idx];
            if (rank < 0) rank = ranks[1];
            if (rank < 0) return null;
            return Math.Round(rank / 3.0 + 1.0, 1);
        }

        static void Download()
        {
            try
            {
                var t = new Dictionary<string, int[]>();
                var req = (HttpWebRequest)WebRequest.Create(Config.BrDataUrl);
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.UserAgent = "WarThunderSocialTap/1.0";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    // The file is ~30 MB of pretty-printed JSON, so it is scanned line by line
                    // instead of being loaded whole: top-level keys are vehicles.
                    string current = null;
                    int[] ranks = null;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string trimmed = line.TrimStart();
                        int indent = line.Length - trimmed.Length;
                        if (indent == 2 && trimmed.StartsWith("\"") && trimmed.EndsWith("{"))
                        {
                            Commit(t, current, ranks);
                            int q = trimmed.IndexOf('"', 1);
                            current = q > 1 ? trimmed.Substring(1, q - 1).ToLowerInvariant() : null;
                            ranks = new int[] { -1, -1, -1 };
                        }
                        else if (indent == 4 && current != null && trimmed.StartsWith("\"economicRank"))
                        {
                            if (trimmed.StartsWith("\"economicRankArcade\"")) ranks[0] = ParseValue(trimmed);
                            else if (trimmed.StartsWith("\"economicRankHistorical\"")) ranks[1] = ParseValue(trimmed);
                            else if (trimmed.StartsWith("\"economicRankSimulation\"")) ranks[2] = ParseValue(trimmed);
                        }
                    }
                    Commit(t, current, ranks);
                }

                if (t.Count < 500) throw new Exception("only " + t.Count + " vehicles parsed, keeping old data");

                var sb = new StringBuilder();
                foreach (KeyValuePair<string, int[]> kv in t)
                    sb.Append(kv.Key).Append('\t').Append(kv.Value[0]).Append('\t').Append(kv.Value[1]).Append('\t').Append(kv.Value[2]).Append('\n');
                File.WriteAllText(CachePath, sb.ToString());
                table = t;
                Log.Write("BR table updated: " + t.Count + " vehicles");
            }
            catch (Exception ex)
            {
                Log.Write("BR table update failed (will retry later): " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref updating, 0);
            }
        }

        static void Commit(Dictionary<string, int[]> t, string id, int[] ranks)
        {
            if (id == null || ranks == null) return;
            if (ranks[0] < 0 && ranks[1] < 0 && ranks[2] < 0) return;
            t[id] = ranks;
        }

        static int ParseValue(string trimmedLine)
        {
            int colon = trimmedLine.IndexOf(':');
            if (colon < 0) return -1;
            string v = trimmedLine.Substring(colon + 1).Trim().TrimEnd(',').Trim();
            int n;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : -1;
        }
    }
}
