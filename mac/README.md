# macOS version

Same card as the Windows app (BR, map, play time, "Idle" in the hangar), as a background app that
starts when you log in. It has no menu bar icon; it just runs quietly.

> This version was written on Windows and has **not been run on a Mac yet**. If something doesn't
> work, the log at `~/Library/Application Support/WarThunderSocialTap/app.log` says why.

## Install

1. Copy this `mac` folder to your Mac.
2. Copy `config.example.json` to `config.json` and put your SocialTap client ID in it.
   The OAuth client needs the scope `integration.status` and the callback URL
   `http://127.0.0.1:8765/callback`.
3. Open Terminal in the folder and run:

```bash
bash install.sh
```

Your browser opens once so you can approve access on SocialTap. After that it runs by itself at
every login. `bash uninstall.sh` removes it.

Needs Python 3, which macOS installs with `xcode-select --install`.

## Notes

- Settings live in `config.json` (see the top of `wt_socialtap.py` for all of them). Run
  `install.sh` again after changing one.
- If map names never show up, set `"game_dir"` in `config.json` to your War Thunder folder (the one
  that contains `Replays`).
- Like the Windows app, it only reads War Thunder's own local web page (`127.0.0.1:8111`) and saved
  replay files. It never touches the game process.
