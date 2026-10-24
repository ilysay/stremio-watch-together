#!/usr/bin/env bash
# Stremio Watch Together for Linux: installs everything in one go, and only what's missing.
#   bash install.sh
# Put StremioWT-linux-x64 next to this script (or it is downloaded from the latest release).
#
# What it checks and installs:
#   1. Stremio (Flathub) - unless Stremio 4 is already installed
#   2. A Chromium browser for Stremio Web (Chrome, Chromium, Edge, Brave) - unless Stremio 4 is installed
#   3. Stremio's streaming server (plays torrents and formats the browser can't), started with your session
#   4. StremioWT itself, with an icon in your applications menu
set -u

RELEASE=https://github.com/ilysay/stremio-watch-together/releases/latest/download
SERVER_URL=https://dl.strem.io/server/v4.20.12/desktop/server.js
HERE="$(cd "$(dirname "$0")" && pwd)"
DATA="$HOME/.local/share/StremioWT"
BIN="$HOME/.local/bin"
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
todo() { printf '  \033[33m→\033[0m %s\n' "$1"; }
fail() { printf '\n\033[31m✗ %s\033[0m\n' "$1"; exit 1; }
step() { printf '\n\033[1m%s\033[0m\n' "$1"; }
has()  { command -v "$1" > /dev/null 2>&1; }

[ "$(id -u)" -eq 0 ] && fail "Run this script as your normal user (not with sudo): it asks for your password when needed."
[ "$(uname -m)" = "x86_64" ] || fail "Only 64-bit Intel/AMD PCs (x86_64) are supported for now."

# Paquets système : la commande dépend de la distribution.
if has apt-get; then PKG="apt"; install_pkgs() { sudo apt-get update -qq && sudo apt-get install -y "$@"; }
elif has dnf; then PKG="dnf"; install_pkgs() { sudo dnf install -y "$@"; }
elif has pacman; then PKG="pacman"; install_pkgs() { sudo pacman -S --needed --noconfirm "$@"; }
elif has zypper; then PKG="zypper"; install_pkgs() { sudo zypper install -y "$@"; }
else fail "Unknown package manager: install Stremio, Chromium, Node.js and ffmpeg by hand, then run StremioWT."
fi

printf '\033[1mStremio Watch Together · Linux installer\033[0m (%s)\n' "$(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-Linux}")"
echo "Your password may be asked once, to install missing system packages."

# curl sert à tout télécharger, mais Ubuntu ne l'installe pas toujours par défaut.
if ! has curl; then
  step "0. Download tool"
  todo "Installing curl"
  install_pkgs curl || fail "Couldn't install curl."
fi

# --- 1. Stremio -------------------------------------------------------------------------------
step "1. Stremio"
STREMIO4=""
for p in /usr/bin/stremio /usr/local/bin/stremio /opt/stremio/stremio "$HOME/.local/bin/stremio"; do [ -x "$p" ] && STREMIO4="$p"; done
if [ -n "$STREMIO4" ]; then
  ok "Stremio 4 found ($STREMIO4): StremioWT drives it directly, no browser needed"
else
  if ! has flatpak; then
    todo "Installing Flatpak"
    case $PKG in apt|dnf|zypper) install_pkgs flatpak ;; pacman) install_pkgs flatpak ;; esac || fail "Couldn't install Flatpak."
  fi
  flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo > /dev/null 2>&1 \
    || sudo flatpak remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
  if flatpak info com.stremio.Stremio > /dev/null 2>&1; then
    ok "Stremio (Flathub) already installed"
  else
    todo "Installing Stremio from Flathub (a few minutes the first time)"
    flatpak install --user -y --noninteractive flathub com.stremio.Stremio || fail "Couldn't install Stremio from Flathub."
    ok "Stremio installed"
  fi
fi

# --- 2. Navigateur pour Stremio Web ---------------------------------------------------------------
step "2. Browser for Stremio Web"
BROWSER=""
for b in google-chrome google-chrome-stable chromium chromium-browser microsoft-edge microsoft-edge-stable brave-browser vivaldi; do
  for d in /usr/bin /usr/local/bin /snap/bin; do [ -z "$BROWSER" ] && [ -x "$d/$b" ] && BROWSER="$d/$b"; done
done
if [ -n "$STREMIO4" ]; then
  ok "Not needed (Stremio 4)"
elif [ -n "$BROWSER" ]; then
  ok "Found: $BROWSER"
else
  todo "Installing Chromium (open source, used only in its own StremioWT window)"
  if has snap && [ "$PKG" = "apt" ]; then sudo snap install chromium
  elif [ "$PKG" = "apt" ]; then install_pkgs chromium
  else install_pkgs chromium
  fi || fail "Couldn't install Chromium. Install Google Chrome or Chromium by hand, then run this script again."
  ok "Chromium installed"
fi

# --- 3. Serveur de Stremio ------------------------------------------------------------------------
step "3. Stremio streaming server"
if [ -n "$STREMIO4" ]; then
  ok "Not needed (included in Stremio 4)"
else
  missing=""
  has node || missing="$missing nodejs"
  has ffmpeg || missing="$missing ffmpeg"
  if [ -n "$missing" ]; then todo "Installing$missing"; install_pkgs $missing || fail "Couldn't install$missing."; fi
  mkdir -p "$DATA/server"
  if [ ! -s "$DATA/server/server.js" ]; then
    todo "Downloading Stremio's official server"
    curl -fsSL -o "$DATA/server/server.js" "$SERVER_URL" || fail "Couldn't download the Stremio server."
  fi
  # Démarré avec la session (service utilisateur), redémarré s'il s'arrête.
  mkdir -p "$HOME/.config/systemd/user"
  cat > "$HOME/.config/systemd/user/stremiowt-server.service" <<UNIT
[Unit]
Description=Stremio streaming server (for Stremio Watch Together)

[Service]
ExecStart=$(command -v node) $DATA/server/server.js
WorkingDirectory=$DATA/server
Restart=on-failure

[Install]
WantedBy=default.target
UNIT
  if systemctl --user daemon-reload 2>/dev/null && systemctl --user enable --now stremiowt-server.service > /dev/null 2>&1; then
    for i in $(seq 1 20); do curl -s --max-time 1 http://127.0.0.1:11470/settings > /dev/null && break; sleep 1; done
    if curl -s --max-time 2 http://127.0.0.1:11470/settings > /dev/null; then ok "Server running, and started automatically with your session"
    else todo "Server installed but not answering yet (it may need a few more seconds)"; fi
  else
    todo "No systemd user session: the server will be started by StremioWT's launcher"
  fi
fi

# --- 4. StremioWT ---------------------------------------------------------------------------------
step "4. Stremio Watch Together"
mkdir -p "$BIN" "$DATA"
if [ -f "$HERE/StremioWT-linux-x64" ]; then
  cp "$HERE/StremioWT-linux-x64" "$BIN/stremiowt"
  ok "Copied from this folder"
else
  todo "Downloading the latest StremioWT"
  curl -fsSL -o "$BIN/stremiowt" "$RELEASE/StremioWT-linux-x64" || fail "Couldn't download StremioWT: put StremioWT-linux-x64 next to this script."
fi
chmod +x "$BIN/stremiowt"
curl -fsSL -o "$DATA/icon.png" https://raw.githubusercontent.com/ilysay/stremio-watch-together/main/docs/logo.png 2>/dev/null || true
# Lanceur : démarre le serveur s'il ne tourne pas déjà (sans systemd), puis StremioWT.
cat > "$BIN/stremiowt-launch" <<LAUNCH
#!/usr/bin/env bash
if [ -s "$DATA/server/server.js" ] && ! curl -s --max-time 1 http://127.0.0.1:11470/settings > /dev/null; then
  (cd "$DATA/server" && nohup node server.js > "$DATA/server/server.log" 2>&1 &)
fi
exec "$BIN/stremiowt" "\$@"
LAUNCH
chmod +x "$BIN/stremiowt-launch"
mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/stremiowt.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Stremio Watch Together
Comment=Watch Stremio with friends, in sync
Exec=$BIN/stremiowt-launch %u
MimeType=x-scheme-handler/stremiowt;
Icon=$DATA/icon.png
Terminal=false
Categories=AudioVideo;Video;Network;
Actions=diary;

[Desktop Action diary]
Name=My diary
Exec=$BIN/stremiowt-launch --diary
DESKTOP
update-desktop-database "$HOME/.local/share/applications" > /dev/null 2>&1 || true
# Les liens « stremiowt:// » de la page d'invitation ouvrent StremioWT directement dans le salon.
xdg-mime default stremiowt.desktop x-scheme-handler/stremiowt > /dev/null 2>&1 || true
ok "Installed: \"Stremio Watch Together\" is now in your applications menu"

printf '\n\033[32m\033[1mAll set!\033[0m Open \033[1mStremio Watch Together\033[0m from your applications menu.\n'
[ -z "$STREMIO4" ] && echo "The first time you host or join, a Stremio Web window opens: log in to your Stremio account there."
echo "To start it now from this terminal:  $BIN/stremiowt-launch"
