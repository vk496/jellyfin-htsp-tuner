#!/usr/bin/env bash
#
# deploy.sh -- inner dev loop: build the plugin, stage it into a running Jellyfin's
# plugin directory, restart the server, and report the plugin's load status.
#
# NO SECRETS LIVE IN THIS FILE. The Jellyfin API key is read from $JELLYFIN_TOKEN and
# there is deliberately no default -- the script fails loudly if it is unset.
#
#   Usage:
#     export JELLYFIN_TOKEN=<your Jellyfin API key>
#     export PLUGIN_DIR=<path to your Jellyfin "plugins" directory>
#     ./tools/deploy.sh
#
#   Optional overrides (env):
#     JELLYFIN_URL   default http://localhost:8096
#     CONFIG         default Release
#
set -euo pipefail

# --- required + configurable inputs -------------------------------------------------
# Secrets and host-specific paths come from the environment; none are baked into this file.
: "${JELLYFIN_TOKEN:?export JELLYFIN_TOKEN with your Jellyfin API key}"
: "${PLUGIN_DIR:?export PLUGIN_DIR with the path to your Jellyfin plugins directory}"
JELLYFIN_URL="${JELLYFIN_URL:-http://localhost:8096}"
CONFIG="${CONFIG:-Release}"

# Strip a trailing slash from the URL so we can concatenate paths cleanly.
JELLYFIN_URL="${JELLYFIN_URL%/}"

AUTH_HEADER="Authorization: MediaBrowser Token=${JELLYFIN_TOKEN}"

# --- locate the project -------------------------------------------------------------
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." &>/dev/null && pwd)"
PROJECT="${REPO_ROOT}/HtspTuner/HtspTuner.csproj"
ASSEMBLY_NAME="HtspTuner"

if [[ ! -f "${PROJECT}" ]]; then
  echo "FATAL: project not found at ${PROJECT}" >&2
  exit 1
fi

# GUID must match build.yaml / Plugin.cs. Kept here only to build meta.json's guid field.
PLUGIN_GUID="48472334-959b-4838-a963-3a92e5a603d1"

log() { printf '\033[1;36m==>\033[0m %s\n' "$*"; }

# --- 1. build -----------------------------------------------------------------------
log "Building ${ASSEMBLY_NAME} (${CONFIG})"
dotnet build -c "${CONFIG}" "${PROJECT}"

# Read the version straight out of the produced assembly-info via the csproj <Version>.
VERSION="$(dotnet msbuild "${PROJECT}" -nologo -getProperty:Version 2>/dev/null || true)"
VERSION="${VERSION//[$'\t\r\n ']/}"
if [[ -z "${VERSION}" ]]; then
  VERSION="1.0.0.0"
  log "Could not read <Version>; falling back to ${VERSION}"
fi

BUILD_OUT="${REPO_ROOT}/HtspTuner/bin/${CONFIG}/net9.0"
DLL="${BUILD_OUT}/${ASSEMBLY_NAME}.dll"
if [[ ! -f "${DLL}" ]]; then
  echo "FATAL: built assembly not found at ${DLL}" >&2
  exit 1
fi

# --- 2. stage into the plugin directory ---------------------------------------------
STAGE="${PLUGIN_DIR}/${ASSEMBLY_NAME}_${VERSION}"
log "Staging into ${STAGE}"
rm -rf "${STAGE}"
mkdir -p "${STAGE}"
cp "${DLL}" "${STAGE}/"

# meta.json schema mirrors a real installed plugin (e.g. Fanart_14.0.0.0/meta.json).
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)"
cat >"${STAGE}/meta.json" <<JSON
{
  "category": "LiveTV",
  "changelog": "Dev build via tools/deploy.sh",
  "description": "Native HTSP streaming from Tvheadend, remuxed locally to MPEG-TS.",
  "guid": "${PLUGIN_GUID}",
  "name": "HTSP Tuner",
  "overview": "Native HTSP streaming from Tvheadend.",
  "owner": "vk496",
  "targetAbi": "10.11.0.0",
  "timestamp": "${TIMESTAMP}",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
JSON

# --- 3. restart Jellyfin ------------------------------------------------------------
log "Restarting Jellyfin at ${JELLYFIN_URL}"
restart_code="$(curl -s -o /dev/null -w '%{http_code}' -X POST \
  -H "${AUTH_HEADER}" "${JELLYFIN_URL}/System/Restart" || true)"
# 204 No Content is the documented success; accept any 2xx.
if [[ ! "${restart_code}" =~ ^2 ]]; then
  echo "WARNING: /System/Restart returned HTTP ${restart_code}" >&2
fi

# --- 4. poll until the server is back ------------------------------------------------
log "Waiting for the server to come back up"
deadline=$(( SECONDS + 120 ))
up=0
# Give it a moment to actually drop before we start polling.
sleep 3
while (( SECONDS < deadline )); do
  info_code="$(curl -s -o /dev/null -w '%{http_code}' \
    -H "${AUTH_HEADER}" "${JELLYFIN_URL}/System/Info" || true)"
  if [[ "${info_code}" == "200" ]]; then
    up=1
    break
  fi
  sleep 2
done

if (( ! up )); then
  echo "FATAL: server did not return to /System/Info within timeout" >&2
  exit 1
fi
log "Server is back up"

# --- 5. report our plugin's status --------------------------------------------------
log "Plugin status from ${JELLYFIN_URL}/Plugins"
plugins_json="$(curl -s -H "${AUTH_HEADER}" "${JELLYFIN_URL}/Plugins" || true)"

if command -v jq >/dev/null 2>&1; then
  match="$(printf '%s' "${plugins_json}" \
    | jq -r --arg id "${PLUGIN_GUID}" \
        '.[] | select((.Id // "") | ascii_downcase == ($id | ascii_downcase))
             | "  name:    \(.Name)\n  version: \(.Version)\n  status:  \(.Status)"' \
    2>/dev/null || true)"
  if [[ -n "${match}" ]]; then
    echo "${match}"
  else
    echo "  Plugin ${PLUGIN_GUID} not found in /Plugins response." >&2
    echo "  (Is Plugin.cs present with this GUID, and did the assembly load?)" >&2
  fi
else
  # jq not installed -- dump raw so the developer can still see it.
  echo "  (jq not installed; raw /Plugins response follows)"
  printf '%s\n' "${plugins_json}"
fi

log "Done. Version ${VERSION} staged at ${STAGE}"
