# PrintAgent: installation and operation

## Build installers

Merge the changes into the repository's main branch, then run **Actions → Build macOS installers → Run workflow** on GitHub. Download the `PrintAgent-macOS-installers` artifact and extract `PrintAgent-Setup-Mac-osx-arm64.dmg` for Apple Silicon or `PrintAgent-Setup-Mac-osx-x64.dmg` for Intel. The workflow uses a macOS runner and .NET 10; it does not require your own Mac or Apple credentials. You can also run `sh package-mac.sh` on a Mac with the .NET 10 SDK.

On Windows, run `dotnet publish -c Release -r win-x64`, then `ISCC.exe install-windows.iss` with Inno Setup 6 or 7. The result is `artifacts/PrintAgent-Setup-Windows.exe`. [Commercial use of Inno Setup requires a license](https://jrsoftware.org/ishelp/topic_purchase.htm).

## Install and configure

On macOS, set up the print queues for the user who will run the agent and check their names with `lpstat -p`. Open the matching `.dmg` and run **PrintAgent Setup.app**. It installs the Worker and configuration in `~/Library/Application Support/PrintAgent/`, asks for the port, and copies the app to `~/Applications/PrintAgent Setup.app`. Open that app again and choose **Support** to change the port, view the last 600 log lines, or open the Downloads folder; choose **Update** to reinstall or **Remove** to uninstall. The LaunchAgent at `~/Library/LaunchAgents/com.medpointe.printagent.plist` starts the Worker when the user signs in and restarts it after a failure. The client Mac does not need .NET. Because the app uses an ad hoc signature and is not notarized, macOS may require **System Settings → Privacy & Security → Open Anyway**. Check the service with `launchctl print gui/$(id -u)/com.medpointe.printagent`.

On Windows, run `PrintAgent-Setup-Windows.exe`. It installs the Worker and **PrintAgent - Support** together. The PrintAgent icon starts in the Windows notification area when the user signs in and starts the Worker. If the Worker fails, the icon starts it again; if the icon process exits, the Worker stops. If the icon is hidden, expand the **^** beside the clock. Double-click the icon to open the support window, which includes **Open downloads**; or right-click the icon to choose **Configure port**, **View logs**, or **Open downloads** directly. The log window shows the last 600 lines and refreshes every second while open; **Pause** lets you read older messages without the view moving. The support window opens automatically after an interactive installation. You can also open it from **Start → PrintAgent → PrintAgent - Support**. **Exit PrintAgent** stops the Worker and closes the icon; the Start menu shortcut starts both again. The installer stops both during updates and uninstallation. Everything is installed in `%LOCALAPPDATA%\PrintAgent`. A shortcut in `shell:startup` starts the icon and Worker without a console window when that Windows user signs in. The PDF application associated with `.pdf` must support the `printto` action. Both installers preserve `appsettings.json` during updates.

Edit `MedpointePort` in the **installed** `appsettings.json`, not the repository copy. It must be an integer from 1 to 65535; the initial value `0` leaves the agent waiting for configuration. For example: `"MedpointePort": 443`. Save valid JSON. The Worker checks the file every five seconds and applies a valid port after the current operation, without restarting. If the file is incomplete, the JSON is invalid, or the port is out of range, it keeps the last valid port and logs a warning. Local jobs are separated by port; returning to a previous port resumes its pending jobs.

To update on macOS, reopen **PrintAgent Setup.app** and choose **Update**. To uninstall, choose **Remove**; this stops and removes the LaunchAgent and executable. Configuration, downloaded documents, pending jobs, and logs remain for a later installation. On Windows, uninstall through **Installed apps**. Both agents require an operating system session. macOS uses a LaunchAgent because the Worker needs access to the print queues configured for that user; a LaunchDaemon would run in a different context and might not see those queues or have permission to print.

Each agent refreshes its available printers every 30 seconds. On Windows, file-output printers using `PORTPROMPT:` or `FILE:` are excluded because they require an interactive save dialog. If there is no usable printer, queued jobs remain unchanged and local pending jobs wait. On Windows, they also wait until a PDF application supports the `printto` action. PMP converts queued documents to PDF. Configure each printer on only one agent per client until PMP supports atomic job reservations.

## Print statuses

| Status in PMP | Meaning in PrintAgent |
| --- | --- |
| Empty | Waiting. `getPrintQueue` returns these jobs. The agent does not take a job unless its printer and PDF print handler are available. |
| `P` | The agent saved the job locally and is trying to send it to the printer. |
| `E` | A local attempt failed. The agent keeps the job and retries about once a minute when the printer and PDF print handler are available. |
| `D` | The print command was sent and PMP confirmed the status. This does not confirm that paper was printed. |

PMP returns only jobs with no status from `getPrintQueue`. Changing a status does not reserve a job atomically; two agents that see the same printer may print it twice.

## Files on disk

The paths below are relative to the installation directory: `%LOCALAPPDATA%\PrintAgent` on Windows or `~/Library/Application Support/PrintAgent` on macOS. **Open downloads** opens the `Downloads` folder; PDFs are inside its port-specific subfolders.

| Path | Contents |
| --- | --- |
| `Downloads/<port>/<id>_<file>` | Downloaded document, kept for retries. |
| `Downloads/<port>/<id>_<file>.download` | Temporary download. |
| `Workers/Jobs/<port>/<id>.printjob.json` | Job awaiting submission or retry. |
| `Workers/Jobs/<port>/<id>.submitted.printjob.json` | Already sent to the printer; only PMP status `D` remains to be confirmed. |

Jobs and documents stay under the port on which they were received. Changing ports does not delete old jobs; returning to that port resumes them.

## Logs

The installed agent records errors and status changes in `Logs/printagent.log`. On macOS, inspect it with `tail -n 50 "$HOME/Library/Application Support/PrintAgent/Logs/printagent.log"`. `launchd` may also write to `Logs/stdout.log` and `Logs/stderr.log`. During development, logs appear in the terminal.

For more detail, change `Logging:LogLevel:Default` from `Information` to `Debug` in the installed `appsettings.json`, then sign out and back in to restart the agent. Only `MedpointePort` updates while the Worker is running; the log level is read at startup. The agent refreshes printers every 30 seconds and checks the queue every five seconds when printing is possible.

PrintAgent's own messages and installer text are in English. The Worker requests English .NET messages and the C locale for macOS print commands; messages supplied by Windows, printer drivers, or other applications may still use the system language. Existing log entries keep their original text.
