namespace WarThunderSocialTap
{
    /// <summary>Everything you might want to tweak lives here. Rebuild with install.ps1 after editing.</summary>
    static class Config
    {
        // War Thunder's built-in local web interface (the official "browser map").
        // This is the ONLY way the app talks to the game: plain HTTP GETs, nothing else.
        public const string TelemetryUrl = "http://127.0.0.1:8111";

        public const string SocialTapBase = "https://www.socialtap.social";

        // Picture shown behind the card. Must be a public HTTPS image URL.
        public const string BackgroundPhoto = "https://cdn.cloudflare.steamstatic.com/steam/apps/236390/header.jpg";

        // "friends" or "all" (all = any signed-in SocialTap user).
        public const string Visibility = "friends";

        // Which battle rating to show: "AB" (arcade), "RB" (realistic) or "SB" (simulator).
        public const string BrMode = "RB";

        // true  = show an "Idle" card even when War Thunder is closed.
        // false = remove the card when War Thunder is closed (it only says "Idle" in the hangar).
        public static readonly bool ShowIdleWhenGameClosed = false;

        // A "play session" keeps counting between battles as long as the gap is shorter than this.
        public const int SessionGapMinutes = 20;

        public const int PollSeconds = 5;            // how often the local game page is read
        public const int BattleRefreshSeconds = 60;  // card refresh while in battle
        public const int IdleRefreshSeconds = 300;   // card refresh while idle (cards expire after 15 min)

        // Vehicle battle ratings come from the public War Thunder datamine and are cached for a week.
        public const string BrDataUrl = "https://raw.githubusercontent.com/gszabi99/War-Thunder-Datamine/master/char.vromfs.bin_u/config/wpcost.blkx";
        public const int BrCacheDays = 7;
    }
}
