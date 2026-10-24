#!/usr/bin/env bash
# Emballe les publications macOS Apple Silicon et Intel (dotnet publish -r osx-arm64 / osx-x64) en un seul
# StremioWT.app universel, signé « ad hoc », puis en StremioWT-mac.zip.
# Les deux exécutables sont côte à côte ; un petit lanceur choisit le bon selon le processeur (même sous Rosetta).
# Les bibliothèques natives (.dylib) sont fusionnées avec lipo : une seule copie qui contient les deux architectures.
# Usage : make-app.sh <publication arm64> <publication x64> <dossier de sortie>   (VERSION=… pour forcer le numéro)
set -eu
ARM=$1; X64=$2; OUT=$3; mkdir -p "$OUT"
APP="$OUT/StremioWT.app"
VERSION=${VERSION:-$(grep -o '<Version>[^<]*' Directory.Build.props | cut -d'>' -f2)}
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$ARM/StremioWT" "$APP/Contents/MacOS/StremioWT-arm64"
cp "$X64/StremioWT" "$APP/Contents/MacOS/StremioWT-x64"
for lib in "$ARM"/*.dylib; do
  name=$(basename "$lib"); archs=$(lipo -archs "$lib")
  case "$archs" in
    *arm64*x86_64*|*x86_64*arm64*) cp "$lib" "$APP/Contents/MacOS/$name" ;;   # déjà universelle
    *) lipo -create "$lib" "$X64/$name" -output "$APP/Contents/MacOS/$name" ;;
  esac
  echo "$name : $(lipo -archs "$APP/Contents/MacOS/$name")"
done
cat > "$APP/Contents/MacOS/StremioWT" <<'LAUNCHER'
#!/bin/sh
# Apple Silicon (M1…) : version arm64, même si ce lanceur tourne sous Rosetta. Sinon : version Intel.
DIR=$(cd "$(dirname "$0")" && pwd)
if [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ]; then exec "$DIR/StremioWT-arm64" "$@"; fi
exec "$DIR/StremioWT-x64" "$@"
LAUNCHER
chmod +x "$APP/Contents/MacOS/StremioWT" "$APP/Contents/MacOS/StremioWT-arm64" "$APP/Contents/MacOS/StremioWT-x64"
cp src/assets/logo.png "$APP/Contents/Resources/" 2>/dev/null || true
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>StremioWT</string>
  <key>CFBundleDisplayName</key><string>Stremio Watch Together</string>
  <key>CFBundleIdentifier</key><string>io.github.ilysay.stremiowt</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>StremioWT</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
codesign --force --deep -s - "$APP"
(cd "$OUT" && ditto -c -k --keepParent StremioWT.app StremioWT-mac.zip && rm -rf StremioWT.app)
ls -la "$OUT"
