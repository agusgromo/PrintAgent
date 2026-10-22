# PrintAgent operations

## Print status codes

| Status in PMP | Meaning in PrintAgent |
| --- | --- |
| Empty | Waiting in PMP. `getPrintQueue` returns these jobs. The agent leaves a job in this state if its named printer is not available locally. |
| `P` | Claimed for processing. The agent has saved a local job record and is attempting to send the document to the named printer. |
| `E` | The local attempt failed. The agent keeps its local job record and retries approximately once a minute, even though PMP no longer returns this job in `getPrintQueue`. |
| `D` | The print command was submitted and PMP confirmed the status update. This does **not** confirm that paper came out of the printer. |

PrintAgent uses only `P`, `E`, and `D`. PMP returns only jobs with an empty status in `getPrintQueue`; it does not return `P`, `E`, or `D` jobs through that endpoint. The PMP status update is not an atomic claim, so two agents that read the same empty job at the same time can still race.

## Files on disk

All paths below are relative to the application's **content root**. When run from the project folder, that is the project folder. When installed as a Windows Service, it is the folder containing the deployed application.

| Path | Contents |
| --- | --- |
| `Downloads/<job-id>_<filename>` | Downloaded documents. The job ID is URL escaped, and the filename is reduced to its final path component. Files remain here after status `D`; retries reuse them. |
| `Downloads/<job-id>_<filename>.download` | Temporary file while a download is being saved. |
| `Workers/Jobs/<job-id>.printjob.json` | Local job record waiting for a print attempt or retry. This is why `P` and `E` jobs survive an agent restart. |
| `Workers/Jobs/<job-id>.submitted.printjob.json` | The print command was submitted; the agent only needs to confirm `D` with PMP. It does not send the document again while this record exists. The record is removed after `D` is confirmed. |

The agent moves old job records from the application root into `Workers/Jobs` when it starts. An older cached document in the root is moved into `Downloads` when that job is processed again.

## Logs

When run interactively, logs appear in the terminal. When run **as a Windows Service**, open **Event Viewer > Windows Logs > Application** and filter by source **PrintAgent**. The service writes errors, `P`/`E`/`D` transitions, and download events there. The application does not create a separate log file. [Microsoft documents the Windows Service Event Log integration](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.windowsservicelifetimehostbuilderextensions.addwindowsservice?view=net-10.0-pp).

`appsettings.json` sets the Windows Event Log level to `Information`. To investigate a production issue in more detail, change `Logging:EventLog:LogLevel:Default` from `Information` to `Debug` in the deployed `appsettings.json`, then restart the service. Debug logs include queue counts and jobs skipped because their printer is unavailable. Return the setting to `Information` after troubleshooting: the queue is checked every five seconds. [Microsoft documents the Event Log level setting](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/providers).
