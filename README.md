# War Thunder → SocialTap status

A tiny Windows tray app that shows what you're doing in War Thunder as an activity card on your
[SocialTap](https://www.socialtap.social) profile.

| You are…                | Card title              | Card subtitle                               |
| ----------------------- | ----------------------- | ------------------------------------------- |
| In a battle             | `War Thunder · BR 9.0`  | `In battle on Normandy · Playing for 1h 05m` |
| In the hangar / menus   | `War Thunder`           | `Idle`                                      |
| Not running War Thunder | *(card is removed)*     |                                             |

- **BR** – the battle rating of the highest-rated vehicle you've used in the current battle.
- **Playing for** – how long your current play session has lasted. Short breaks in the hangar
  (under 20 minutes) don't reset it.
- **Map** – the game doesn't expose the map name while you play, so the app *learns* it: the first
  time you play a map it reads the name from the replay the game saves afterwards, and from then on
  it recognises that map in the first seconds of a battle. (Needs replay auto-save, which is on by default.)
- **Team placement** is not shown – War Thunder does not make the scoreboard available to outside programs.

## Is this safe with BattlEye / Easy Anti-Cheat?

The app only does two things with the game:

1. Reads `http://127.0.0.1:8111` – War Thunder's own built-in local web page (the official
   "browser map"), using ordinary HTTP requests like a web browser would.
2. Reads the header of replay files the game has already saved to disk.

It never opens, inspects, injects into, or sends input to the game process, and it doesn't touch
game files. No promise can be made on Gaijin's behalf, but this is the same interface that
long-standing community tools use.

## Install

Requires Windows 10/11. Nothing else to install – it compiles with the C# compiler built into Windows.

1. Copy `src/Secrets.cs.example` to `src/Secrets.cs` and paste your SocialTap OAuth access token into it.
2. Right-click `install.ps1` → **Run with PowerShell**
   (or run `powershell -ExecutionPolicy Bypass -File install.ps1`).

That builds the app, installs it to `%LOCALAPPDATA%\Programs\WarThunderSocialTap`, registers it to
start with Windows, and launches it. Look for the War Thunder icon in the system tray; hover over it
for the current status, right-click for the log or to exit.

Run `install.ps1` again after changing any setting. Run `uninstall.ps1` to remove everything.

### OAuth scope

The token needs exactly one scope:

```
integration.status
```

Without a token the app still runs, but only writes what it *would* publish to its log.

## Settings

Edit `src/Config.cs`, then re-run `install.ps1`:

| Setting                  | Default   | Meaning                                                        |
| ------------------------ | --------- | -------------------------------------------------------------- |
| `BrMode`                 | `RB`      | Which rating to show: `AB`, `RB` or `SB`                       |
| `Visibility`             | `friends` | `friends` or `all` (any signed-in SocialTap user)              |
| `ShowIdleWhenGameClosed` | `false`   | `true` keeps an "Idle" card up even when the game is closed    |
| `SessionGapMinutes`      | `20`      | Hangar time after which "Playing for" starts over              |
| `BackgroundPhoto`        | Steam art | Public HTTPS image shown behind the card                       |

## How it's organised

```
src/
  Program.cs       tray icon, menu, background loop
  Tracker.cs       battle/idle state and when to publish
  Telemetry.cs     reads the game's local web page
  BrTable.cs       vehicle → battle rating lookup (public datamine, cached weekly)
  MapLearner.cs    minimap fingerprinting + map names from replays
  SocialTap.cs     SocialTap integration-status API
  Util.cs          paths, logging, HTTP
  Config.cs        settings
  Secrets.cs.example   template for your token (Secrets.cs itself is git-ignored)
build.ps1 / install.ps1 / uninstall.ps1
```

App data (log, BR cache, learned maps) lives in `%LOCALAPPDATA%\WarThunderSocialTap`.

Vehicle ratings come from the community [War Thunder Datamine](https://github.com/gszabi99/War-Thunder-Datamine).
This project is not affiliated with Gaijin Entertainment or SocialTap.
