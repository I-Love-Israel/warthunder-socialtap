#!/usr/bin/env python3
"""War Thunder -> SocialTap status, macOS version.

Runs in the background (as a login item installed by install.sh) and shows what you are doing in
War Thunder as an activity card on your SocialTap profile. Uses only the Python standard library.

It talks to the game ONLY through War Thunder's own local web page (http://127.0.0.1:8111) and by
reading replay files the game has already saved. It never touches the game process.

    python3 wt_socialtap.py --connect   # one-time SocialTap login in your browser
    python3 wt_socialtap.py             # run (install.sh sets this up to start at login)
"""
import base64
import hashlib
import json
import os
import re
import secrets
import signal
import socket
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
from pathlib import Path

HERE = Path(__file__).resolve().parent
DATA = Path.home() / "Library" / "Application Support" / "WarThunderSocialTap"

DEFAULTS = {
    "client_id": "",                      # your SocialTap OAuth client ID
    "access_token": "",                   # optional: paste a token instead of using --connect
    "br_mode": "RB",                      # AB, RB or SB
    "visibility": "friends",              # friends or all
    "show_idle_when_game_closed": False,  # True keeps an "Idle" card up when the game is closed
    "session_gap_minutes": 20,            # hangar time after which "Playing for" starts over
    "background_photo": "https://cdn.cloudflare.steamstatic.com/steam/apps/236390/header.jpg",
    "game_dir": "",                       # War Thunder folder, only needed if replays aren't found
}

TELEMETRY = os.environ.get("WTST_TELEMETRY_URL", "http://127.0.0.1:8111").rstrip("/")
SOCIALTAP = os.environ.get("WTST_SOCIALTAP_URL", "https://www.socialtap.social").rstrip("/")
REDIRECT_URI = "http://127.0.0.1:8765/callback"
CALLBACK_PORT = 8765
SCOPE = "integration.status"
BR_DATA_URL = ("https://raw.githubusercontent.com/gszabi99/War-Thunder-Datamine/master/"
               "char.vromfs.bin_u/config/wpcost.blkx")
BR_CACHE_DAYS = 7
POLL_SECONDS = 5
BATTLE_REFRESH_SECONDS = 60
IDLE_REFRESH_SECONDS = 300
USER_AGENT = "WarThunderSocialTap/1.0"
DOT = " · "


# ---------------------------------------------------------------- basics

def load_config():
    cfg = dict(DEFAULTS)
    path = HERE / "config.json"
    if path.exists():
        try:
            cfg.update(json.loads(path.read_text(encoding="utf-8")))
        except Exception as e:  # noqa: BLE001
            log("config.json could not be read: %s" % e)
    return cfg


def log(msg):
    try:
        DATA.mkdir(parents=True, exist_ok=True)
        path = DATA / "app.log"
        if path.exists() and path.stat().st_size > 1_000_000:
            path.replace(DATA / "app.log.old")
        with open(path, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S") + "  " + msg + "\n")
    except Exception:  # noqa: BLE001
        pass
    if sys.stdout and sys.stdout.isatty():
        print(msg)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """A bounce to a login page must not look like success."""

    def redirect_request(self, *args, **kwargs):
        return None


_local = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect)
_remote = urllib.request.build_opener(_NoRedirect)


def http(method, url, token=None, body=None, ctype=None, timeout=15):
    """Returns (status, body_bytes). Status 0 means the connection failed."""
    req = urllib.request.Request(url, data=body, method=method)
    req.add_header("User-Agent", USER_AGENT)
    if token:
        req.add_header("Authorization", "Bearer " + token)
    if ctype:
        req.add_header("Content-Type", ctype)
    opener = _local if url.startswith(("http://127.0.0.1", "http://localhost")) else _remote
    try:
        with opener.open(req, timeout=timeout) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as e:
        try:
            return e.code, e.read()
        except Exception:  # noqa: BLE001
            return e.code, b""
    except Exception:  # noqa: BLE001
        return 0, b""


# ---------------------------------------------------------------- game telemetry

VALID_TRUE = re.compile(r'"valid"\s*:\s*true')
TYPE_FIELD = re.compile(r'"type"\s*:\s*"([^"]+)"')


def read_telemetry():
    """Returns (game_running, in_battle, vehicle_id)."""
    status, ind = http("GET", TELEMETRY + "/indicators", timeout=2.5)
    if status == 0:
        return False, False, None
    # A mission map only exists inside a battle. (The indicators stay "valid" in the hangar.)
    mstatus, minfo = http("GET", TELEMETRY + "/map_info.json", timeout=2.5)
    in_battle = mstatus == 200 and bool(VALID_TRUE.search(minfo.decode("utf-8", "replace")))
    vehicle = None
    if in_battle and status == 200:
        text = ind.decode("utf-8", "replace")
        if VALID_TRUE.search(text):
            m = TYPE_FIELD.search(text)
            if m:
                vehicle = m.group(1).strip().lower().rsplit("/", 1)[-1] or None
    return True, in_battle, vehicle


def map_fingerprint():
    status, img = http("GET", TELEMETRY + "/map.img", timeout=5)
    if status != 200 or len(img) < 100:
        return None
    return hashlib.sha256(img).hexdigest()


# ---------------------------------------------------------------- battle ratings

class BrTable:
    def __init__(self, mode):
        self.idx = {"AB": 0, "RB": 1, "SB": 2}.get(mode, 1)
        self.table = {}
        self.path = DATA / "br-cache.tsv"
        self.next_attempt = 0.0
        self.updating = False
        self._load()

    def _load(self):
        try:
            if not self.path.exists():
                return
            t = {}
            for line in self.path.read_text(encoding="utf-8").splitlines():
                p = line.split("\t")
                if len(p) == 4:
                    t[p[0]] = (int(p[1]), int(p[2]), int(p[3]))
            self.table = t
            log("BR table loaded from cache: %d vehicles" % len(t))
        except Exception as e:  # noqa: BLE001
            log("BR cache unreadable: %s" % e)

    def ensure_fresh(self):
        now = time.time()
        if now < self.next_attempt or self.updating:
            return
        stale = (not self.path.exists() or not self.table or
                 now - self.path.stat().st_mtime > BR_CACHE_DAYS * 86400)
        if not stale:
            self.next_attempt = now + 3600
            return
        self.next_attempt = now + 1800
        self.updating = True
        threading.Thread(target=self._download, daemon=True).start()

    def _download(self):
        try:
            t = {}
            req = urllib.request.Request(BR_DATA_URL, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=60) as resp:
                current, ranks = None, None
                # ~30 MB of pretty-printed JSON: scan line by line, top-level keys are vehicles.
                for raw in resp:
                    line = raw.decode("utf-8", "replace").rstrip("\r\n")
                    trimmed = line.lstrip()
                    indent = len(line) - len(trimmed)
                    if indent == 2 and trimmed.startswith('"') and trimmed.endswith("{"):
                        if current and max(ranks) >= 0:
                            t[current] = tuple(ranks)
                        current = trimmed[1:trimmed.index('"', 1)].lower()
                        ranks = [-1, -1, -1]
                    elif indent == 4 and current and trimmed.startswith('"economicRank'):
                        for i, key in enumerate(('"economicRankArcade"', '"economicRankHistorical"',
                                                 '"economicRankSimulation"')):
                            if trimmed.startswith(key):
                                try:
                                    ranks[i] = int(trimmed.split(":", 1)[1].strip().rstrip(","))
                                except ValueError:
                                    pass
                if current and max(ranks) >= 0:
                    t[current] = tuple(ranks)
            if len(t) < 500:
                raise RuntimeError("only %d vehicles parsed, keeping old data" % len(t))
            self.path.write_text("".join("%s\t%d\t%d\t%d\n" % ((k,) + v) for k, v in t.items()),
                                 encoding="utf-8")
            self.table = t
            log("BR table updated: %d vehicles" % len(t))
        except Exception as e:  # noqa: BLE001
            log("BR table update failed (will retry later): %s" % e)
        finally:
            self.updating = False

    def lookup(self, vehicle):
        ranks = self.table.get(vehicle) if vehicle else None
        if not ranks:
            return None
        rank = ranks[self.idx] if ranks[self.idx] >= 0 else ranks[1]
        if rank < 0:
            return None
        return round(rank / 3.0 + 1.0, 1)


# ---------------------------------------------------------------- map names

LEVEL_PATH = re.compile(rb"levels/([A-Za-z0-9_]+)\.bin")


class MapLearner:
    """The game's local page never names the map, but the replay saved after a battle does.
    The minimap is fingerprinted during the battle and paired with the name afterwards, so the
    next time that map comes up it is recognised straight away."""

    def __init__(self, game_dir):
        self.path = DATA / "known-maps.json"
        self.known = {}
        try:
            if self.path.exists():
                self.known = json.loads(self.path.read_text(encoding="utf-8"))
        except Exception:  # noqa: BLE001
            self.known = {}
        home = Path.home()
        candidates = [
            Path(game_dir) if game_dir else None,
            home / "My Games" / "WarThunder",
            home / "Library" / "Application Support" / "Steam" / "steamapps" / "common" / "War Thunder",
            home / "Library" / "Application Support" / "WarThunder",
            home / "Library" / "Application Support" / "Gaijin" / "WarThunder",
            Path("/Applications/WarThunder"),
        ]
        self.replay_dirs = [c / "Replays" for c in candidates if c]

    def lookup(self, fingerprint):
        return self.known.get(fingerprint)

    def learn(self, fingerprint, name):
        self.known[fingerprint] = name
        try:
            self.path.write_text(json.dumps(self.known, indent=1), encoding="utf-8")
        except Exception as e:  # noqa: BLE001
            log("Could not save known maps: %s" % e)
        log("Learned map: " + name)

    def find_replay_map(self, start, end):
        best, best_time = None, 0.0
        for d in self.replay_dirs:
            try:
                if not d.is_dir():
                    continue
                for f in d.glob("*.wrpl"):
                    t = f.stat().st_mtime
                    if start <= t <= end and t > best_time:
                        best, best_time = f, t
            except Exception:  # noqa: BLE001
                continue
        if not best:
            return None
        try:
            with open(best, "rb") as f:
                m = LEVEL_PATH.search(f.read(2048))
            return pretty_map_name(m.group(1).decode("ascii")) if m else None
        except Exception:  # noqa: BLE001
            return None


def pretty_map_name(level):
    s = re.sub(r"^(avg|air|avn|avia|arcade)_", "", level.lower()).replace("_", " ").strip()
    return s.title() if s else None


# ---------------------------------------------------------------- SocialTap

class SocialTap:
    def __init__(self, cfg):
        self.cfg = cfg
        self.client_id = cfg["client_id"]
        self.access = cfg["access_token"]
        self.refresh = ""
        self.token_path = DATA / "tokens.json"
        try:
            if self.token_path.exists():
                saved = json.loads(self.token_path.read_text(encoding="utf-8"))
                if saved.get("hardcoded", "") == cfg["access_token"]:
                    self.access = saved.get("access", self.access)
                    self.refresh = saved.get("refresh", "")
        except Exception as e:  # noqa: BLE001
            log("Saved login unreadable: %s" % e)

    @property
    def dry_run(self):
        return not self.access

    def set_tokens(self, access, refresh):
        self.access = access
        if refresh:
            self.refresh = refresh
        try:
            DATA.mkdir(parents=True, exist_ok=True)
            self.token_path.write_text(json.dumps(
                {"hardcoded": self.cfg["access_token"], "access": self.access, "refresh": self.refresh}),
                encoding="utf-8")
            os.chmod(self.token_path, 0o600)  # readable by your user only
        except Exception as e:  # noqa: BLE001
            log("Could not save login: %s" % e)

    def put(self, title, subtitle):
        if self.dry_run:
            log("[not connected - not sent] %s | %s" % (title, subtitle))
            return 200
        body = json.dumps({"title": title[:128], "subtitle": subtitle[:256],
                           "background_photo": self.cfg["background_photo"],
                           "visibility": self.cfg["visibility"]}).encode("utf-8")
        status, resp = http("PUT", SOCIALTAP + "/api/integration-status", self.access, body,
                            "application/json; charset=utf-8")
        if status == 200 and b"integration_status" not in resp:
            log("SocialTap answered 200 but not with a status card - treating as failure")
            return 500
        if status == 200:
            log("Published: %s | %s" % (title, subtitle))
        else:
            log("Publish failed: HTTP %d %s" % (status, resp[:200].decode("utf-8", "replace")))
        return status

    def delete(self):
        if self.dry_run:
            return 200
        status, _ = http("DELETE", SOCIALTAP + "/api/integration-status", self.access, timeout=10)
        log("Cleared card: HTTP %d" % status)
        return status

    def _token_request(self, fields):
        body = urllib.parse.urlencode(fields).encode("ascii")
        status, resp = http("POST", SOCIALTAP + "/api/oauth/token", None, body,
                            "application/x-www-form-urlencoded")
        if status != 200:
            return status, None
        try:
            return status, json.loads(resp.decode("utf-8"))
        except Exception:  # noqa: BLE001
            return status, None

    def try_refresh(self):
        if not self.refresh or not self.client_id:
            return False
        status, d = self._token_request({"grant_type": "refresh_token", "refresh_token": self.refresh,
                                         "client_id": self.client_id})
        if not d or not d.get("access_token"):
            log("Token renewal failed: HTTP %d" % status)
            return False
        self.set_tokens(d["access_token"], d.get("refresh_token"))
        log("Access token renewed")
        return True

    def connect(self):
        """One-time login: OAuth authorization code flow with PKCE. Returns None on success."""
        if not self.client_id:
            return "No client ID yet. Put it in config.json first."
        verifier = _b64url(secrets.token_bytes(32))
        challenge = _b64url(hashlib.sha256(verifier.encode("ascii")).digest())
        state = _b64url(secrets.token_bytes(16))
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            srv.bind(("127.0.0.1", CALLBACK_PORT))
        except OSError:
            return "Port %d is in use by another program. Close it and try again." % CALLBACK_PORT
        srv.listen(5)
        srv.settimeout(1.0)
        url = SOCIALTAP + "/api/oauth/authorize?" + urllib.parse.urlencode({
            "response_type": "code", "client_id": self.client_id, "redirect_uri": REDIRECT_URI,
            "scope": SCOPE, "state": state, "code_challenge": challenge,
            "code_challenge_method": "S256"})
        print("Opening SocialTap in your browser. If nothing opens, go to:\n" + url)
        webbrowser.open(url)
        code, error = None, None
        deadline = time.time() + 300
        try:
            while code is None and error is None and time.time() < deadline:
                try:
                    conn, _ = srv.accept()
                except socket.timeout:
                    continue
                with conn:
                    conn.settimeout(5)
                    try:
                        request = conn.recv(8192).decode("ascii", "replace")
                    except Exception:  # noqa: BLE001
                        continue
                    target = request.split(" ")[1] if request.count(" ") >= 2 else ""
                    q = urllib.parse.parse_qs(urllib.parse.urlsplit(target).query)
                    if not target.startswith("/callback"):
                        page = "Nothing to see here."
                    elif "error" in q:
                        error = "SocialTap said: " + q["error"][0]
                        page = "Login was not completed. You can close this tab."
                    elif q.get("state", [""])[0] != state:
                        page = "This login link is out of date. Run --connect again."
                    elif "code" not in q:
                        page = "No code received. Run --connect again."
                    else:
                        code = q["code"][0]
                        page = ("Connected! War Thunder will now show on your SocialTap profile. "
                                "You can close this tab.")
                    html = ('<!doctype html><meta charset="utf-8"><body style="font-family:sans-serif;'
                            'background:#111;color:#eee;text-align:center;padding-top:15vh"><h2>%s</h2>'
                            % page).encode("utf-8")
                    conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                                 b"Content-Length: %d\r\nConnection: close\r\n\r\n" % len(html) + html)
        finally:
            srv.close()
        if error:
            return error
        if code is None:
            return "Timed out waiting for the SocialTap login."
        status, d = self._token_request({"grant_type": "authorization_code", "code": code,
                                         "redirect_uri": REDIRECT_URI, "client_id": self.client_id,
                                         "code_verifier": verifier})
        if not d or not d.get("access_token"):
            return "SocialTap refused the login code (HTTP %d)." % status
        self.set_tokens(d["access_token"], d.get("refresh_token"))
        log("Connected to SocialTap")
        return None


def _b64url(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode("ascii")


# ---------------------------------------------------------------- tracker

def format_duration(seconds):
    minutes = max(1, int(seconds // 60))
    if minutes < 60:
        return "%dm" % minutes
    return "%dh %02dm" % (minutes // 60, minutes % 60)


class Tracker:
    def __init__(self, cfg, st):
        self.cfg, self.st = cfg, st
        self.br = BrTable(cfg["br_mode"])
        self.maps = MapLearner(cfg["game_dir"])
        self.in_battle = False
        self.missed = 0
        self.battle_start = 0.0
        self.last_battle_end = 0.0
        self.session_start = 0.0
        self.max_br = None
        self.map_hash = None
        self.map_name = None
        self.last_map_try = 0.0
        self.pending = []  # (fingerprint, battle_start, battle_end)
        self.last_key = None
        self.last_sent = 0.0
        self.next_allowed = 0.0
        self.card_active = False

    def tick(self):
        now = time.time()
        self.br.ensure_fresh()
        running, battle, vehicle = read_telemetry()

        if running and battle:
            self.missed = 0
            if not self.in_battle:
                self.in_battle = True
                self.battle_start = now
                if now - self.last_battle_end > self.cfg["session_gap_minutes"] * 60:
                    self.session_start = now
                self.max_br = self.map_hash = self.map_name = None
                self.last_map_try = 0.0
                log("Battle started")
            # Let the mission finish loading so the minimap belongs to this battle.
            if self.map_hash is None and now - self.battle_start >= 10 and now - self.last_map_try >= 10:
                self.last_map_try = now
                self.map_hash = map_fingerprint()
                if self.map_hash:
                    self.map_name = self.maps.lookup(self.map_hash)
                    log("Map recognised: " + self.map_name if self.map_name
                        else "Map not known yet - will learn it from the replay")
            br = self.br.lookup(vehicle)
            # Keep the highest BR used this battle, so a low-BR backup doesn't change the readout.
            if br is not None and (self.max_br is None or br > self.max_br):
                self.max_br = br
        elif self.in_battle:
            # Telemetry blinks off between respawns; wait a few cycles before calling it over.
            self.missed += 1
            if not running or self.missed >= 3:
                self.in_battle = False
                self.missed = 0
                self.last_battle_end = now
                if self.map_hash and not self.map_name:
                    self.pending.append((self.map_hash, self.battle_start, now))
                log("Battle ended")

        for item in list(self.pending):
            fingerprint, start, end = item
            name = self.maps.find_replay_map(start, end + 180)
            if name:
                self.maps.learn(fingerprint, name)
                self.pending.remove(item)
            elif now > end + 180:
                self.pending.remove(item)  # no replay was saved (test drive, or replays off)

        title = subtitle = None
        if not running:
            if self.cfg["show_idle_when_game_closed"]:
                title, subtitle = "War Thunder", "Idle"
        elif self.in_battle:
            title = "War Thunder" + (DOT + "BR %.1f" % self.max_br if self.max_br is not None else "")
            subtitle = ("In battle" + (" on " + self.map_name if self.map_name else "") + DOT +
                        "Playing for " + format_duration(now - self.session_start))
        else:
            title, subtitle = "War Thunder", "Idle"

        self.publish(now, title, subtitle)
        return POLL_SECONDS if running else POLL_SECONDS * 3

    def publish(self, now, title, subtitle):
        if title is None:
            if self.card_active:
                self.st.delete()
                self.card_active = False
                self.last_key = None
            return
        key = title + "\n" + subtitle
        refresh_every = BATTLE_REFRESH_SECONDS if self.in_battle else IDLE_REFRESH_SECONDS
        since = now - self.last_sent
        due = since >= 15 if key != self.last_key else since >= refresh_every
        if not due or now < self.next_allowed:
            return
        status = self.st.put(title, subtitle)
        if status == 200:
            self.card_active = True
            self.last_key, self.last_sent = key, now
        elif status == 401 or 300 <= status < 400:
            self.next_allowed = now if self.st.try_refresh() else now + 300
        elif status == 403:
            log("The token lacks the integration.status scope")
            self.next_allowed = now + 300
        elif status == 429:
            self.next_allowed = now + 120
        else:
            self.next_allowed = now + 30

    def shutdown(self):
        if self.card_active:
            self.st.delete()
            self.card_active = False


# ---------------------------------------------------------------- main

def main():
    cfg = load_config()
    st = SocialTap(cfg)

    if "--connect" in sys.argv:
        problem = st.connect()
        print(problem or "Connected to SocialTap.")
        return 1 if problem else 0

    log("App started" + (" (not connected to SocialTap yet - nothing will be sent)" if st.dry_run else ""))
    tracker = Tracker(cfg, st)

    def stop(*_):
        tracker.shutdown()
        log("App closed")
        sys.exit(0)

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    while True:
        wait = POLL_SECONDS
        try:
            wait = tracker.tick()
        except Exception as e:  # noqa: BLE001
            log("Error: %r" % e)
        time.sleep(wait)


if __name__ == "__main__":
    sys.exit(main())
