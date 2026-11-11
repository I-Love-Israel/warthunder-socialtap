using System;
using System.Collections.Generic;
using System.Globalization;

namespace WarThunderSocialTap
{
    /// <summary>Turns game telemetry into the card text and decides when to publish it.</summary>
    class Tracker
    {
        class PendingMap
        {
            public string Hash;
            public DateTime Start;
            public DateTime End;
        }

        const string Dot = " · ";

        // battle state
        bool inBattle;
        int missedTicks;
        DateTime battleStart;
        DateTime lastBattleEnd = DateTime.MinValue;
        DateTime sessionStart = DateTime.MinValue;
        double? maxBr;
        string mapHash;
        string mapName;
        DateTime lastMapTry = DateTime.MinValue;
        readonly List<PendingMap> pending = new List<PendingMap>();

        // publish state
        string lastSentKey;
        DateTime lastSentAt = DateTime.MinValue;
        DateTime nextAllowed = DateTime.MinValue;
        bool cardActive;
        string publishNote;

        public string Status = "Starting";

        /// <summary>Set after a new login so the card is sent again straight away.</summary>
        public volatile bool ForceRepublish;

        /// <summary>Runs one cycle. Returns how many seconds to wait before the next one.</summary>
        public int Tick()
        {
            DateTime now = DateTime.Now;
            TelemetrySnapshot snap = Telemetry.Read();

            if (snap.Reachable && snap.InBattle)
            {
                missedTicks = 0;
                if (!inBattle) StartBattle(now);
                IdentifyMap(now);
                double? br = BrTable.Lookup(snap.VehicleId);
                // Keep the highest BR used this battle, so a low-BR backup doesn't change the readout.
                if (br.HasValue && (!maxBr.HasValue || br.Value > maxBr.Value)) maxBr = br;
            }
            else if (inBattle)
            {
                // Telemetry blinks off for a moment between respawns; wait a few cycles before calling it over.
                missedTicks++;
                if (!snap.Reachable || missedTicks >= 3) EndBattle(now);
            }

            LearnPendingMaps(now);

            string title = null, subtitle = null, state;
            if (!snap.Reachable)
            {
                state = "War Thunder not running";
                if (Config.ShowIdleWhenGameClosed) { title = "War Thunder"; subtitle = "Idle"; }
            }
            else if (inBattle)
            {
                title = "War Thunder";
                if (maxBr.HasValue) title += Dot + "BR " + maxBr.Value.ToString("0.0", CultureInfo.InvariantCulture);
                subtitle = "In battle";
                if (mapName != null) subtitle += " on " + mapName;
                subtitle += Dot + "Playing for " + FormatDuration(now - sessionStart);
                state = "In battle" + (maxBr.HasValue ? ", BR " + maxBr.Value.ToString("0.0", CultureInfo.InvariantCulture) : "") +
                        (mapName != null ? ", " + mapName : "");
            }
            else
            {
                title = "War Thunder";
                subtitle = "Idle";
                state = "Idle";
            }

            Publish(now, title, subtitle);
            Status = publishNote == null ? state : state + " (" + publishNote + ")";
            return snap.Reachable ? Config.PollSeconds : Config.PollSeconds * 3;
        }

        /// <summary>Removes the card when the app is closed.</summary>
        public void Shutdown()
        {
            if (cardActive) SocialTap.Delete();
            cardActive = false;
        }

        void StartBattle(DateTime now)
        {
            inBattle = true;
            battleStart = now;
            if ((now - lastBattleEnd).TotalMinutes > Config.SessionGapMinutes) sessionStart = now;
            maxBr = null;
            mapHash = null;
            mapName = null;
            lastMapTry = DateTime.MinValue;
            Log.Write("Battle started");
        }

        void EndBattle(DateTime now)
        {
            inBattle = false;
            missedTicks = 0;
            lastBattleEnd = now;
            if (mapHash != null && mapName == null)
            {
                var p = new PendingMap();
                p.Hash = mapHash;
                p.Start = battleStart;
                p.End = now;
                pending.Add(p);
            }
            Log.Write("Battle ended" + (mapName != null ? " (" + mapName + ")" : ""));
        }

        void IdentifyMap(DateTime now)
        {
            if (mapHash != null) return;
            // Give the mission a moment to finish loading so the minimap belongs to this battle.
            if ((now - battleStart).TotalSeconds < 10) return;
            if ((now - lastMapTry).TotalSeconds < 10) return;
            lastMapTry = now;
            byte[] img = Telemetry.MapImage();
            if (img == null) return;
            mapHash = MapLearner.Fingerprint(img);
            if (mapHash == null) return;
            mapName = MapLearner.Lookup(mapHash);
            Log.Write(mapName != null ? "Map recognised: " + mapName : "Map not known yet - will learn it from the replay");
        }

        void LearnPendingMaps(DateTime now)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingMap p = pending[i];
                string name = MapLearner.FindReplayMap(p.Start, p.End.AddMinutes(3));
                if (name != null)
                {
                    MapLearner.Learn(p.Hash, name);
                    pending.RemoveAt(i);
                }
                else if (now > p.End.AddMinutes(3))
                {
                    pending.RemoveAt(i); // no replay was saved (test drive, or replays turned off)
                }
            }
        }

        void Publish(DateTime now, string title, string subtitle)
        {
            if (ForceRepublish)
            {
                ForceRepublish = false;
                lastSentKey = null;
                lastSentAt = DateTime.MinValue;
                nextAllowed = DateTime.MinValue;
                publishNote = null;
            }

            if (title == null)
            {
                if (cardActive)
                {
                    SocialTap.Delete();
                    cardActive = false;
                    lastSentKey = null;
                }
                publishNote = SocialTap.DryRun ? "not connected" : null;
                return;
            }

            string key = title + "\n" + subtitle;
            int refreshEvery = inBattle ? Config.BattleRefreshSeconds : Config.IdleRefreshSeconds;
            double sinceLast = (now - lastSentAt).TotalSeconds;
            bool due = key != lastSentKey ? sinceLast >= 15 : sinceLast >= refreshEvery;
            if (!due || now < nextAllowed) return;

            int status = SocialTap.Put(title, subtitle);
            if (status == 200)
            {
                cardActive = true;
                lastSentKey = key;
                lastSentAt = now;
                publishNote = SocialTap.DryRun ? "not connected" : null;
            }
            else if (status == 401 || (status >= 300 && status < 400))
            {
                if (SocialTap.TryRefresh()) { nextAllowed = now; publishNote = "token renewed"; }
                else { nextAllowed = now.AddMinutes(5); publishNote = "token rejected"; }
            }
            else if (status == 403)
            {
                nextAllowed = now.AddMinutes(5);
                publishNote = "token lacks integration.status scope";
            }
            else if (status == 429)
            {
                nextAllowed = now.AddMinutes(2);
                publishNote = "rate limited";
            }
            else
            {
                nextAllowed = now.AddSeconds(30);
                publishNote = status == 0 ? "SocialTap unreachable" : "SocialTap error " + status;
            }
        }

        static string FormatDuration(TimeSpan t)
        {
            int minutes = (int)Math.Max(1, Math.Floor(t.TotalMinutes));
            if (minutes < 60) return minutes + "m";
            return (minutes / 60) + "h " + (minutes % 60).ToString("00") + "m";
        }
    }
}
