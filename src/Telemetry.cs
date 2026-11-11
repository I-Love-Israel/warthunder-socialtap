using System;
using System.Text.RegularExpressions;

namespace WarThunderSocialTap
{
    class TelemetrySnapshot
    {
        public bool Reachable;    // War Thunder is running
        public bool InBattle;     // a mission is loaded (battle or test drive)
        public string VehicleId;  // e.g. "us_m1_abrams", null when not in a vehicle
    }

    /// <summary>
    /// Reads War Thunder's official local web interface (http://127.0.0.1:8111).
    /// Read-only HTTP GETs. The game process itself is never opened, inspected or modified.
    /// </summary>
    static class Telemetry
    {
        static readonly string Base = InitBase();
        static readonly Regex ValidTrue = new Regex("\"valid\"\\s*:\\s*true", RegexOptions.Compiled);
        static readonly Regex TypeField = new Regex("\"type\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

        static string InitBase()
        {
            string b = Environment.GetEnvironmentVariable("WTST_TELEMETRY_URL");
            if (string.IsNullOrEmpty(b)) b = Config.TelemetryUrl;
            return b.TrimEnd('/');
        }

        public static TelemetrySnapshot Read()
        {
            var snap = new TelemetrySnapshot();
            HttpResult ind = Http.Get(Base + "/indicators", 2500);
            if (ind.Status == 0) return snap;
            snap.Reachable = true;

            // A mission map only exists inside a battle. (The vehicle indicators can't be used for this:
            // in the hangar they stay "valid" and report the vehicle parked there.)
            HttpResult map = Http.Get(Base + "/map_info.json", 2500);
            snap.InBattle = map.Status == 200 && ValidTrue.IsMatch(map.Body);

            if (snap.InBattle && ind.Status == 200 && ValidTrue.IsMatch(ind.Body))
            {
                Match m = TypeField.Match(ind.Body);
                if (m.Success) snap.VehicleId = NormalizeVehicle(m.Groups[1].Value);
            }
            return snap;
        }

        /// <summary>The minimap picture of the current mission, or null.</summary>
        public static byte[] MapImage()
        {
            HttpResult r = Http.Get(Base + "/map.img", 5000);
            if (r.Status != 200 || r.Bytes.Length < 100) return null;
            return r.Bytes;
        }

        // "tankModels/us_m1_abrams" -> "us_m1_abrams"
        public static string NormalizeVehicle(string raw)
        {
            if (raw == null) return null;
            string v = raw.Trim().ToLowerInvariant();
            int slash = v.LastIndexOf('/');
            if (slash >= 0) v = v.Substring(slash + 1);
            return v.Length == 0 ? null : v;
        }
    }
}
