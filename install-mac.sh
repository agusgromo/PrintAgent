#!/bin/sh
set -eu

bundle=$(cd "$(dirname "$0")/../.." && pwd -P)
target="$HOME/Library/Application Support/PrintAgent"
setup="$HOME/Applications/PrintAgent Setup.app"
plist="$HOME/Library/LaunchAgents/com.medpointe.printagent.plist"
service="gui/$(id -u)"

stop_agent() {
    if launchctl print "$service/com.medpointe.printagent" >/dev/null 2>&1; then
        launchctl bootout "$service" "$plist"
    fi
}

trap 'status=$?; if [ "$status" -ne 0 ]; then osascript -e '\''display alert "No se pudo instalar PrintAgent" message "Comprueba los permisos de la carpeta Library de tu usuario."'\''; fi' EXIT

if [ -f "$target/PrintAgent" ]; then
    action=$(osascript -e 'button returned of (display dialog "PrintAgent ya esta instalado." buttons {"Cancelar", "Quitar", "Instalar"} default button "Instalar")') || exit 0
    if [ "$action" = "Quitar" ]; then
        stop_agent
        rm -f "$plist" "$target/PrintAgent"
        osascript -e 'display alert "PrintAgent eliminado" message "Los archivos descargados y los logs se conservaron en Application Support."'
        rm -rf "$setup"
        exit 0
    fi
fi

mkdir -p "$target/Logs" "$(dirname "$plist")" "$HOME/Applications"
stop_agent
cp "$bundle/Contents/Resources/PrintAgent" "$target/PrintAgent.new"
chmod +x "$target/PrintAgent.new"
mv -f "$target/PrintAgent.new" "$target/PrintAgent"
settings="$target/appsettings.json"
if [ ! -f "$settings" ]; then cp "$bundle/Contents/Resources/appsettings.json" "$settings"; fi

xml_target=$(printf '%s' "$target" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g')
cat > "$plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>Label</key><string>com.medpointe.printagent</string>
    <key>ProgramArguments</key><array><string>$xml_target/PrintAgent</string></array>
    <key>WorkingDirectory</key><string>$xml_target</string>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>ThrottleInterval</key><integer>60</integer>
    <key>StandardOutPath</key><string>$xml_target/Logs/stdout.log</string>
    <key>StandardErrorPath</key><string>$xml_target/Logs/stderr.log</string>
</dict></plist>
PLIST

launchctl bootstrap "$service" "$plist"
if [ "$bundle" != "$setup" ]; then ditto "$bundle" "$setup"; fi
osascript -e 'display alert "PrintAgent instalado" message "Edita MedpointePort en ~/Library/Application Support/PrintAgent/appsettings.json. El agente se iniciara cada vez que ingreses a tu cuenta."'
