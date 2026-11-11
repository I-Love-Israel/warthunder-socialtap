using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WarThunderSocialTap
{
    /// <summary>
    /// War Thunder's local page shows the minimap picture but never the map's name.
    /// The name IS written into the replay file the game saves when a battle ends, so the app
    /// learns as you play: it fingerprints the minimap during a battle, reads the map name from
    /// the replay afterwards, and remembers the pair. The next time that map comes up, the name
    /// is known from the first seconds of the battle.
    /// </summary>
    static class MapLearner
    {
        const int MaxHashDistance = 10; // out of 256 bits
        static readonly Dictionary<string, string> known = new Dictionary<string, string>();
        static readonly Regex LevelPath = new Regex("levels/([A-Za-z0-9_]+)\\.bin", RegexOptions.Compiled);
        static readonly Regex Prefix = new Regex("^(avg|air|avn|avia|arcade)_", RegexOptions.Compiled);

        static string StorePath { get { return Paths.In("known-maps.tsv"); } }

        public static int Count { get { return known.Count; } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return;
                foreach (string line in File.ReadAllLines(StorePath))
                {
                    string[] p = line.Split('\t');
                    if (p.Length == 2 && p[0].Length == 64) known[p[0]] = p[1];
                }
                Log.Write("Known maps loaded: " + known.Count);
            }
            catch (Exception ex) { Log.Write("Known maps unreadable: " + ex.Message); }
        }

        /// <summary>256-bit average hash of the minimap image, as 64 hex characters. Null if not an image.</summary>
        public static string Fingerprint(byte[] image)
        {
            try
            {
                using (var ms = new MemoryStream(image))
                using (var src = new Bitmap(ms))
                using (var small = new Bitmap(16, 16, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, new Rectangle(0, 0, 16, 16));
                    }
                    var gray = new double[256];
                    double sum = 0;
                    for (int y = 0; y < 16; y++)
                    {
                        for (int x = 0; x < 16; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            double v = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                            gray[y * 16 + x] = v;
                            sum += v;
                        }
                    }
                    double mean = sum / 256.0;
                    var bytes = new byte[32];
                    for (int i = 0; i < 256; i++)
                        if (gray[i] > mean) bytes[i / 8] |= (byte)(1 << (i % 8));
                    var sb = new StringBuilder(64);
                    foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        public static string Lookup(string hash)
        {
            if (hash == null) return null;
            string best = null;
            int bestDist = MaxHashDistance + 1;
            foreach (KeyValuePair<string, string> kv in known)
            {
                int d = Distance(hash, kv.Key);
                if (d < bestDist) { bestDist = d; best = kv.Value; }
            }
            return best;
        }

        public static void Learn(string hash, string name)
        {
            if (hash == null || string.IsNullOrEmpty(name)) return;
            known[hash] = name;
            try
            {
                var sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in known) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                File.WriteAllText(StorePath, sb.ToString());
            }
            catch (Exception ex) { Log.Write("Could not save known maps: " + ex.Message); }
            Log.Write("Learned map: " + name);
        }

        /// <summary>Map name from the newest replay saved between the two times, or null.</summary>
        public static string FindReplayMap(DateTime from, DateTime to)
        {
            FileInfo best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string dir in Paths.GameDirs())
            {
                try
                {
                    var rdir = new DirectoryInfo(Path.Combine(dir, "Replays"));
                    if (!rdir.Exists) continue;
                    foreach (FileInfo f in rdir.GetFiles("*.wrpl"))
                    {
                        DateTime t = f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime;
                        if (t < from || t > to || t <= bestTime) continue;
                        best = f;
                        bestTime = t;
                    }
                }
                catch { }
            }
            if (best == null) return null;
            try
            {
                var buf = new byte[2048];
                int n;
                using (var fs = new FileStream(best.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    n = fs.Read(buf, 0, buf.Length);
                Match m = LevelPath.Match(Encoding.ASCII.GetString(buf, 0, n));
                return m.Success ? PrettyName(m.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        // "avg_normandy" -> "Normandy"
        public static string PrettyName(string level)
        {
            string s = Prefix.Replace(level.ToLowerInvariant(), "");
            s = s.Replace('_', ' ').Trim();
            if (s.Length == 0) return null;
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
        }

        static int Distance(string a, string b)
        {
            if (a.Length != 64 || b.Length != 64) return int.MaxValue;
            int d = 0;
            for (int i = 0; i < 64; i++)
            {
                int x = HexVal(a[i]) ^ HexVal(b[i]);
                while (x != 0) { d += x & 1; x >>= 1; }
            }
            return d;
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }
    }
}
