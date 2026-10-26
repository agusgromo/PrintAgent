# PrintAgent: instalación y operación

## Crear instaladores

Incorporá los cambios a la rama principal del repositorio y ejecutá **Actions → Build macOS installers → Run workflow** en GitHub. Cuando termine, descargá el artifact `PrintAgent-macOS-installers` y extraé `PrintAgent-Setup-Mac-osx-arm64.dmg` (Apple Silicon) o `PrintAgent-Setup-Mac-osx-x64.dmg` (Intel). El workflow utiliza un runner macOS y .NET 10; no requiere una Mac propia ni credenciales de Apple. También se puede ejecutar `sh package-mac.sh` en una Mac con el SDK de .NET 10.

Para Windows, ejecutá `dotnet publish -c Release -r win-x64` y `ISCC.exe install-windows.iss` desde Windows con Inno Setup 6 o 7. El resultado es `artifacts/PrintAgent-Setup-Windows.exe`. [El uso comercial de Inno Setup requiere una licencia](https://jrsoftware.org/ishelp/topic_purchase.htm).

## Instalar y configurar

En macOS, configurá las colas de impresión para el usuario que ejecutará el agente y comprobá sus nombres con `lpstat -p`. Abrí el `.dmg` correspondiente y ejecutá **PrintAgent Setup.app**. La instalación deja el ejecutable y la configuración en `~/Library/Application Support/PrintAgent/`, copia el instalador a `~/Applications/PrintAgent Setup.app` y registra `~/Library/LaunchAgents/com.medpointe.printagent.plist`. Se inicia automáticamente al ingresar a la cuenta y `launchd` lo vuelve a ejecutar si falla. No requiere .NET en la Mac del cliente. Como usa una firma ad hoc y no está notarizado, macOS puede requerir **Configuración del Sistema → Privacidad y seguridad → Abrir de todos modos**. Comprobá el servicio con `launchctl print gui/$(id -u)/com.medpointe.printagent`.

En Windows, ejecutá `PrintAgent-Setup-Windows.exe`. Se instala en `%LOCALAPPDATA%\PrintAgent` y se inicia al ingresar a esa cuenta de Windows. La aplicación PDF asociada con `.pdf` debe ofrecer la acción `printto`. Los dos instaladores conservan `appsettings.json` en las actualizaciones.

Editá `MedpointePort` en el `appsettings.json` **instalado**, no en el del repositorio. Debe ser un número entero entre 1 y 65535; el valor inicial `0` deja al agente esperando configuración. Por ejemplo: `"MedpointePort": 443`. Guardá JSON válido. El Worker comprueba el archivo cada cinco segundos y aplica un puerto válido al terminar la operación en curso, sin reiniciar el proceso. Si el archivo está incompleto, el JSON es incorrecto o el puerto está fuera de rango, mantiene el último puerto válido y registra una advertencia. Los trabajos locales se separan por puerto; si se vuelve al anterior, retoma sus trabajos pendientes.

Para actualizar en macOS, abrí nuevamente **PrintAgent Setup.app** y elegí **Instalar**. Para desinstalar, abrilo y elegí **Quitar**; se detiene y elimina el LaunchAgent y el ejecutable. Se conservan la configuración, los documentos, los trabajos pendientes y los logs para una reinstalación. En Windows, desinstalá desde **Aplicaciones instaladas**. Ambos agentes requieren una sesión del sistema operativo iniciada. Se utiliza LaunchAgent en macOS porque el Worker debe ver las colas de impresión configuradas para ese usuario; un LaunchDaemon tendría otro contexto y podría no verlas o no tener permisos para enviar trabajos.

Cada agente vuelve a listar sus impresoras cuando hay trabajos y omite trabajos para impresoras que no puede usar. PMP convierte los documentos de la cola a PDF. Configurá cada impresora en un solo agente por cliente hasta que PMP ofrezca una reserva atómica de trabajos.

## Estados de impresión

| Estado en PMP | Significado en PrintAgent |
| --- | --- |
| Vacío | En espera. `getPrintQueue` devuelve estos trabajos. Si la impresora no está disponible, el agente no lo toma. |
| `P` | El agente guardó el trabajo localmente e intenta enviarlo a la impresora. |
| `E` | Falló el intento local. El agente conserva el trabajo y reintenta aproximadamente cada minuto. |
| `D` | Se envió el comando de impresión y PMP confirmó el estado. No confirma que haya salido el papel. |

PMP solo devuelve trabajos sin estado desde `getPrintQueue`. El cambio de estado no reserva el trabajo de forma atómica; dos agentes que ven la misma impresora pueden imprimirlo dos veces.

## Archivos en disco

Todas las rutas siguientes son relativas a la carpeta de instalación: `%LOCALAPPDATA%\PrintAgent` en Windows o `~/Library/Application Support/PrintAgent` en macOS.

| Ruta | Contenido |
| --- | --- |
| `Downloads/<puerto>/<id>_<archivo>` | Documento descargado. Se conserva para reintentos. |
| `Downloads/<puerto>/<id>_<archivo>.download` | Descarga temporal. |
| `Workers/Jobs/<puerto>/<id>.printjob.json` | Trabajo pendiente de envío o reintento. |
| `Workers/Jobs/<puerto>/<id>.submitted.printjob.json` | Ya se envió a la impresora; solo falta confirmar `D` con PMP. |

Los trabajos y documentos quedan bajo el puerto con que se recibieron. Cambiar de puerto no borra los trabajos del puerto anterior; al volver a él, se retoman.

## Logs

El agente instalado registra errores y cambios de estado en `Logs/printagent.log`. En macOS se puede consultar con `tail -n 50 "$HOME/Library/Application Support/PrintAgent/Logs/printagent.log"`; `launchd` también puede escribir en `Logs/stdout.log` y `Logs/stderr.log`. Durante el desarrollo, los logs aparecen en la terminal.

Para investigar con más detalle, cambiá `Logging:LogLevel:Default` de `Information` a `Debug` en el `appsettings.json` instalado y volvé a ingresar a la sesión para reiniciar el agente. Solo `MedpointePort` se aplica en caliente; el nivel de log se lee al iniciar. El agente consulta la cola cada cinco segundos y vuelve a listar impresoras cada 30 segundos cuando hay trabajos.
