using PrintAgent;
using PrintAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

bool packaged = !File.Exists(Path.Combine(AppContext.BaseDirectory, "PrintAgent.dll"));
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
using (instanceLock) host.Run();
