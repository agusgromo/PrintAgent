using PrintAgent;
using PrintAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;

CultureInfo english = CultureInfo.GetCultureInfo("en-US");
CultureInfo.CurrentUICulture = english;
CultureInfo.DefaultThreadCurrentUICulture = english;

bool packaged = !File.Exists(Path.Combine(AppContext.BaseDirectory, "PrintAgent.dll"));
if (packaged && OperatingSystem.IsWindows() && args.Length > 0 && args[0] == "--tray")
{
    ProcessStartInfo startInfo = new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
    {
        WorkingDirectory = AppContext.BaseDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "-NoProfile", "-Sta", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "support-windows.ps1") }
    };
    for (int i = 1; i < args.Length; i++) startInfo.ArgumentList.Add(args[i]);
    using Process? tray = Process.Start(startInfo);
    if (args.Contains("-Stop", StringComparer.OrdinalIgnoreCase)) tray?.WaitForExit();
    return;
}
FileStream? instanceLock = null;
if (packaged)
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    Directory.CreateDirectory("Logs");
    StreamWriter log = new(new FileStream(Path.Combine("Logs", "printagent.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
    {
        AutoFlush = true
    };
    Console.SetOut(TextWriter.Synchronized(log));
    Console.SetError(TextWriter.Synchronized(log));

    try
    {
        instanceLock = new FileStream("agent.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        return;
    }
}

HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = packaged ? AppContext.BaseDirectory : Directory.GetCurrentDirectory()
});
builder.Configuration.AddJsonFile(source =>
{
    source.Path = "appsettings.json";
    source.Optional = true;
    source.OnLoadException = context => context.Ignore = true;
});
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<MedpointeService>();
builder.Services.AddHostedService<Worker>();

using IHost host = builder.Build();
IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
using EventWaitHandle? stopSignal = packaged && OperatingSystem.IsWindows()
    ? new(false, EventResetMode.AutoReset, "Local\\PrintAgentWorkerStop")
    : null;
RegisteredWaitHandle? stopRegistration = stopSignal is null ? null :
    ThreadPool.RegisterWaitForSingleObject(stopSignal, (_, _) => lifetime.StopApplication(), null, Timeout.Infinite, true);
if (packaged && OperatingSystem.IsWindows() && args.Length == 2 && args[0] == "--parent-pid" && int.TryParse(args[1], out int parentPid))
{
    _ = Task.Run(async () =>
    {
        try
        {
            using Process parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync();
        }
        catch (ArgumentException) { }
        lifetime.StopApplication();
    });
}
try { using (instanceLock) host.Run(); }
finally { stopRegistration?.Unregister(null); }
