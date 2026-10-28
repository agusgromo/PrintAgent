#!/bin/sh
set -eu

bundle=$(cd "$(dirname "$0")/../.." && pwd -P)
target="$HOME/Library/Application Support/PrintAgent"
settings="$target/appsettings.json"
setup="$HOME/Applications/PrintAgent Setup.app"
plist="$HOME/Library/LaunchAgents/com.medpointe.printagent.plist"
service="gui/$(id -u)"

stop_agent() {
    if launchctl print "$service/com.medpointe.printagent" >/dev/null 2>&1; then
        launchctl bootout "$service" "$plist"
    fi
}

show_logs() {
    if [ -f "$target/Logs/printagent.log" ]; then
        tail -n 600 "$target/Logs/printagent.log" > "$target/Logs/recent.log"
        open -a TextEdit "$target/Logs/recent.log"
    else
        osascript -e 'display alert "No PrintAgent logs are available yet."'
    fi
}

open_downloads() {
    mkdir -p "$target/Downloads"
    open "$target/Downloads"
}

configure_port() {
    if [ ! -f "$settings" ]; then
        osascript -e 'display alert "appsettings.json was not found."'
        return
    fi

    current=$(sed -n 's/.*"MedpointePort"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$settings" | head -n 1)
    while :; do
        port=$(osascript -e "text returned of (display dialog \"Medpointe port (1-65535)\" default answer \"$current\" buttons {\"Cancel\", \"Save\"} default button \"Save\")") || return 0
        case "$port" in
            ''|*[!0-9]*) valid=no ;;
            *)
                port=$(printf '%s' "$port" | sed 's/^0*//')
                if [ -z "$port" ]; then port=0; fi
                if [ "${#port}" -le 5 ] && [ "$port" -ge 1 ] && [ "$port" -le 65535 ]; then valid=yes; else valid=no; fi
                ;;
        esac
        if [ "$valid" = yes ]; then break; fi
        osascript -e 'display alert "Enter a port between 1 and 65535."'
    done

    if ! grep -q '"MedpointePort"[[:space:]]*:[[:space:]]*[0-9]' "$settings"; then
        osascript -e 'display alert "Unable to read MedpointePort from appsettings.json."'
        return
    fi
    sed -e "s/\(\"MedpointePort\"[[:space:]]*:[[:space:]]*\)[0-9][0-9]*/\1$port/" "$settings" > "$settings.new"
    mv -f "$settings.new" "$settings"
    osascript -e 'display alert "Port saved" message "PrintAgent will apply the change automatically."'
}

trap 'status=$?; if [ "$status" -ne 0 ]; then osascript -e '\''display alert "Unable to install PrintAgent" message "Check the permissions of your Library folder."'\''; fi' EXIT

new_install=yes
if [ -f "$target/PrintAgent" ]; then
    new_install=no
    action=$(osascript -e 'button returned of (display dialog "PrintAgent is already installed." buttons {"Remove", "Support", "Update"} default button "Support")') || exit 0
    if [ "$action" = "Remove" ]; then
        stop_agent
        rm -f "$plist" "$target/PrintAgent"
        osascript -e 'display alert "PrintAgent removed" message "Downloaded files and logs were kept in Application Support."'
        rm -rf "$setup"
        exit 0
    fi
    if [ "$action" = "Support" ]; then
        option=$(osascript -e 'set selectedOption to choose from list {"Change port", "View logs", "Open downloads"} with prompt "What would you like to do?" default items {"Change port"}' -e 'if selectedOption is false then return ""' -e 'return item 1 of selectedOption') || exit 0
        if [ "$option" = "View logs" ]; then show_logs; fi
        if [ "$option" = "Change port" ]; then configure_port; fi
        if [ "$option" = "Open downloads" ]; then open_downloads; fi
        exit 0
    fi
fi

mkdir -p "$target/Logs" "$(dirname "$plist")" "$HOME/Applications"
stop_agent
cp "$bundle/Contents/Resources/PrintAgent" "$target/PrintAgent.new"
chmod +x "$target/PrintAgent.new"
mv -f "$target/PrintAgent.new" "$target/PrintAgent"
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
if [ "$new_install" = yes ]; then configure_port; fi
osascript -e 'display alert "PrintAgent installed" message "Open PrintAgent Setup.app in Applications to view logs, open downloads, or change the port."'
