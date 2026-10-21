using PrintAgent;
using PrintAgent.Services;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

int port =  builder.Configuration.GetValue<int>("MedpointePort");

if (port is < 1 or > 65535) throw new InvalidOperationException("No valid port is set. Modify the appsettings.json file and set the client port.");

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => { options.ServiceName = "PrintAgent"; });
}
if (OperatingSystem.IsMacOS())
{

}
builder.Services.AddSingleton(new HttpClient
{
    BaseAddress = new Uri($"https://medpointe.bmismartcloud.com:{port}/medpointe/web/")
});
builder.Services.AddSingleton<MedpointeService>();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();
