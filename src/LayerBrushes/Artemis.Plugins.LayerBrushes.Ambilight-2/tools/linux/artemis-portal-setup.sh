#!/usr/bin/env sh
set -eu

APP_ID="${ARTEMIS_APP_ID:-org.artemisrgb.Artemis}"
APP_NAME="${ARTEMIS_APP_NAME:-Artemis}"
LAUNCH="${1:-${ARTEMIS_EXEC:-}}"

usage() {
    cat <<EOF
Usage:
  $0 /absolute/path/to/Artemis [--launch]

Creates the user-local Linux desktop entry and launcher scope needed by xdg-desktop-portal:
  \$XDG_DATA_HOME/applications/${APP_ID}.desktop

Environment:
  ARTEMIS_APP_ID    Desktop app id to register. Default: ${APP_ID}
  ARTEMIS_APP_NAME  Display name. Default: ${APP_NAME}
  ARTEMIS_EXEC      Artemis executable path if not passed as the first argument.

Examples:
  $0 "\$HOME/Artemis/Artemis" --launch
  ARTEMIS_EXEC="\$HOME/Artemis/Artemis" $0
EOF
}

if [ "${LAUNCH}" = "--help" ] || [ "${LAUNCH}" = "-h" ]; then
    usage
    exit 0
fi

DO_LAUNCH=0
if [ "${2:-}" = "--launch" ]; then
    DO_LAUNCH=1
fi

if [ -z "${LAUNCH}" ]; then
    echo "Missing Artemis executable path." >&2
    usage >&2
    exit 2
fi

case "${LAUNCH}" in
    /*) ;;
    *)
        echo "Artemis executable path must be absolute: ${LAUNCH}" >&2
        exit 2
        ;;
esac

if [ ! -e "${LAUNCH}" ]; then
    echo "Artemis executable does not exist: ${LAUNCH}" >&2
    exit 2
fi

DATA_HOME="${XDG_DATA_HOME:-${HOME}/.local/share}"
APPLICATIONS_DIR="${DATA_HOME}/applications"
DESKTOP_FILE="${APPLICATIONS_DIR}/${APP_ID}.desktop"
BIN_HOME="${HOME}/.local/bin"
LAUNCHER="${BIN_HOME}/artemis-portal-launch-${APP_ID}.sh"

mkdir -p "${APPLICATIONS_DIR}"
mkdir -p "${BIN_HOME}"

for OLD_DESKTOP in \
    "${APPLICATIONS_DIR}/artemis.desktop" \
    "${APPLICATIONS_DIR}/Artemis.desktop" \
    "${APPLICATIONS_DIR}/org.artemis_rgb.Artemis.desktop"
do
    if [ "${OLD_DESKTOP}" != "${DESKTOP_FILE}" ] && [ -f "${OLD_DESKTOP}" ]; then
        rm -f "${OLD_DESKTOP}"
        echo "Removed duplicate ${OLD_DESKTOP}"
    fi
done

cat > "${LAUNCHER}" <<EOF
#!/usr/bin/env sh
set -eu

APP_ID="${APP_ID}"
APP_EXEC="${LAUNCH}"

if command -v systemd-run >/dev/null 2>&1; then
    UNIT="app-\${APP_ID}-\$(date +%s%N)"
    exec systemd-run \
        --user \
        --scope \
        --quiet \
        --same-dir \
        --collect \
        --slice=app.slice \
        --unit="\${UNIT}" \
        --setenv="DISPLAY=\${DISPLAY:-}" \
        --setenv="WAYLAND_DISPLAY=\${WAYLAND_DISPLAY:-}" \
        --setenv="XAUTHORITY=\${XAUTHORITY:-}" \
        --setenv="DBUS_SESSION_BUS_ADDRESS=\${DBUS_SESSION_BUS_ADDRESS:-}" \
        --setenv="XDG_CURRENT_DESKTOP=\${XDG_CURRENT_DESKTOP:-}" \
        --setenv="XDG_SESSION_TYPE=\${XDG_SESSION_TYPE:-}" \
        --setenv="XDG_RUNTIME_DIR=\${XDG_RUNTIME_DIR:-}" \
        "\${APP_EXEC}" "\$@"
fi

exec "\${APP_EXEC}" "\$@"
EOF

chmod 0755 "${LAUNCHER}"

cat > "${DESKTOP_FILE}" <<EOF
[Desktop Entry]
Type=Application
Name=${APP_NAME}
Exec=${LAUNCHER}
Terminal=false
StartupWMClass=Artemis
Categories=Utility;
EOF

chmod 0644 "${DESKTOP_FILE}"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "${APPLICATIONS_DIR}" >/dev/null 2>&1 || true
fi

echo "Created ${DESKTOP_FILE}"
echo "Created ${LAUNCHER}"
echo "App id: ${APP_ID}"
echo "Exec: ${LAUNCH}"
echo "Desktop Exec: ${LAUNCHER}"

if [ "${DO_LAUNCH}" -eq 1 ]; then
    if command -v gtk-launch >/dev/null 2>&1; then
        echo "Launching ${APP_ID} via gtk-launch..."
        gtk-launch "${APP_ID}"
    else
        echo "gtk-launch is not installed. Launch from your application menu, or run:"
        echo "  gtk-launch ${APP_ID}"
    fi
else
    echo "Now fully close Artemis and launch it from the app menu, or run:"
    echo "  gtk-launch ${APP_ID}"
fi
