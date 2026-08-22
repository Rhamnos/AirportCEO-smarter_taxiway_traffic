#!/usr/bin/env bash
# Build and install Advanced Taxiways into BepInEx/plugins for local testing.
#
# Not a Workshop item yet, so it goes in the stock BepInEx plugin folder rather
# than under workshop/content -- Steam reverts hand-edits there. Standalone: no
# Mod Loader dependency since the ramp/priority features were removed. That folder lives
# inside the game install, so a "verify integrity of game files" can remove the
# DLL; re-running this script restores it.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME="$HOME/.steam/debian-installation/steamapps/common/Airport CEO"
PLUGINS="$GAME/BepInEx/plugins"
CACHE="$GAME/BepInEx/cache"
# Once the mod has been staged for the Workshop it loads from there instead. Adding a
# second copy here means two folders share the plugin GUID and BepInEx silently skips
# one -- and you cannot tell which. Stage instead.
STAGED="$HOME/.steam/debian-installation/steamapps/compatdata/673610/pfx/drive_c/users/steamuser/AppData/Roaming/Apoapsis Studios/Airport CEO/Mods/SmarterTaxiwayTraffic"
if [ -d "$STAGED" ]; then
  echo "A staged Workshop copy exists at:" >&2
  echo "  $STAGED" >&2
  echo "Use ./stage-for-upload.sh instead -- two copies share a plugin GUID and one gets skipped." >&2
  echo "If you really want a loose build here, delete the staged folder first." >&2
  exit 1
fi

( cd "$HERE/src" && dotnet build -c Release -v quiet --nologo )
mkdir -p "$PLUGINS"
cp "$HERE/src/bin/Release/AirportCEOAdvancedTaxiways.dll" "$PLUGINS/"
# Recreating a plugin path desyncs BepInEx's type cache and the plugin then
# silently stops loading. Clear it every time.
rm -f "$CACHE/chainloader_typeloader.dat" "$CACHE/harmony_interop_cache.dat"
echo "deployed AirportCEOAdvancedTaxiways.dll to $PLUGINS; BepInEx cache cleared"
