using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PrintAgent.Models;
using PrintAgent.Services;

namespace PrintAgent;

public class Worker(ILogger<Worker> logger, MedpointeService medpointeService, IHostEnvironment hostEnvironment) : BackgroundService
{
    private const string PendingSuffix = ".printjob.json";
    private const string SubmittedSuffix = ".submitted.printjob.json";
    private readonly Dictionary<string, DateTimeOffset> retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> submittedJobs = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> availablePrinters = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset nextPrinterRefresh;
    private bool? printersReady;
    private bool? pdfReady;
    private int port;
    private string? lastSettings;
    private bool settingsReadFailed;
    private string JobsDirectory => Path.Combine(hostEnvironment.ContentRootPath, "Workers", "Jobs", port.ToString(CultureInfo.InvariantCulture));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RefreshPort();
            try
            {
                if (port != 0) await ProcessQueue(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to check the print queue.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void RefreshPort()
    {
        string settings;
        try
        {
            settings = File.ReadAllText(Path.Combine(hostEnvironment.ContentRootPath, "appsettings.json"));
            settingsReadFailed = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!settingsReadFailed) logger.LogWarning(exception, "Unable to read appsettings.json. Keeping port {Port}.", port);
            settingsReadFailed = true;
            return;
        }

        if (settings == lastSettings) return;
        lastSettings = settings;
        try
        {
            using JsonDocument document = JsonDocument.Parse(settings);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("MedpointePort", out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out int newPort) || newPort is < 1 or > 65535)
            {
                logger.LogWarning("Invalid MedpointePort in appsettings.json. Keeping port {Port}.", port);
                return;
            }

            if (newPort == port) return;
            port = newPort;
            medpointeService.Port = newPort;
            logger.LogInformation("Medpointe port changed to {Port}.", port);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Invalid appsettings.json. Keeping port {Port}.", port);
        }
    }

    private async Task ProcessQueue(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(JobsDirectory);
        List<string> pendingJobs = [];
        foreach (string path in Directory.GetFiles(JobsDirectory, "*" + PendingSuffix))
        {
            if (path.EndsWith(SubmittedSuffix, StringComparison.OrdinalIgnoreCase))
                await CompleteSubmitted(path, cancellationToken);
            else
                pendingJobs.Add(path);
        }
        if (DateTimeOffset.UtcNow >= nextPrinterRefresh)
        {
            try
            {
                availablePrinters = await GetAvailablePrinters(cancellationToken);
                bool ready = availablePrinters.Count > 0;
                if (ready != printersReady) logger.LogInformation(ready ? "Usable local printers are available." : "No usable local printers are available. Print jobs will wait.");
                printersReady = ready;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Unable to check local printers.");
                availablePrinters = new(StringComparer.OrdinalIgnoreCase);
            }
            if (availablePrinters.Count > 0)
            {
                bool ready = MedpointeService.CanPrintPdf();
                if (!ready && pdfReady != false) logger.LogWarning("No PDF application supports printing to a specific printer. Print jobs will wait.");
                if (ready && pdfReady == false) logger.LogInformation("PDF printing is available again.");
                pdfReady = ready;
            }
            nextPrinterRefresh = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        HashSet<string> printers = availablePrinters;
        if (printers.Count == 0 || pdfReady == false) return;

        List<PrintQueueItem> queue;
        try
        {
            queue = await medpointeService.GetPrintQueue(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Unable to read the print queue. Retrying local jobs.");
            queue = [];
        }

        if (queue.Count == 0 && pendingJobs.Count == 0) return;

        foreach (PrintQueueItem item in queue)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Printer)) continue;
            if (!printers.Contains(item.Printer.Trim()))
            {
                logger.LogDebug("Skipping job {Id}: printer {Printer} is unavailable locally.", item.Id, item.Printer);
                continue;
            }

            string path = JobPath(item.Id);
            if (File.Exists(path) || File.Exists(SubmittedPath(item.Id))) continue;

            try
            {
                PrintQueueItem localJob = new() { Id = item.Id, FileName = item.FileName, Printer = item.Printer };
                string tempPath = path + ".download";
                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(localJob), cancellationToken);
                File.Move(tempPath, path, overwrite: true);
                pendingJobs.Add(path);
                logger.LogDebug("Saved local job {Id} at {Path}.", item.Id, path);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Unable to save print job {Id}.", item.Id);
            }
        }

        foreach (string path in pendingJobs)
            await ProcessPending(path, printers, cancellationToken);
    }

    private async Task ProcessPending(string path, HashSet<string> printers, CancellationToken cancellationToken)
    {
        if (retryAfter.TryGetValue(path, out DateTimeOffset nextAttempt) && DateTimeOffset.UtcNow < nextAttempt) return;

        PrintQueueItem? item = null;
        try
        {
            item = JsonSerializer.Deserialize<PrintQueueItem>(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidDataException($"Invalid local print job: {path}");
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Printer))
                throw new InvalidDataException($"Invalid local print job: {path}");

            if (!submittedJobs.Contains(path))
            {
                if (!printers.Contains(item.Printer.Trim())) return;

                await medpointeService.SetPrintStatus(item.Id, "P", cancellationToken);
                logger.LogInformation("Job {Id} marked P for printer {Printer}.", item.Id, item.Printer);
                await medpointeService.SendPrint(item, cancellationToken);
                submittedJobs.Add(path);
            }

            File.Move(path, SubmittedPath(item.Id), overwrite: true);
            retryAfter.Remove(path);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            retryAfter[path] = DateTimeOffset.UtcNow.AddMinutes(1);
            logger.LogError(exception, "Unable to process local print job {Path}. Retrying in one minute.", path);
            if (item is not null && !string.IsNullOrWhiteSpace(item.Id) && !submittedJobs.Contains(path))
                await MarkError(item.Id, cancellationToken);
            return;
        }

        await CompleteSubmitted(SubmittedPath(item.Id), cancellationToken);
    }

    private async Task CompleteSubmitted(string path, CancellationToken cancellationToken)
    {
        if (retryAfter.TryGetValue(path, out DateTimeOffset nextAttempt) && DateTimeOffset.UtcNow < nextAttempt) return;

        try
        {
            PrintQueueItem item = JsonSerializer.Deserialize<PrintQueueItem>(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidDataException($"Invalid local print job: {path}");
            if (string.IsNullOrWhiteSpace(item.Id)) throw new InvalidDataException($"Invalid local print job: {path}");
            await medpointeService.SetPrintStatus(item.Id, "D", cancellationToken);
            File.Delete(path);
            retryAfter.Remove(path);
            submittedJobs.Remove(JobPath(item.Id));
            logger.LogInformation("Job {Id} marked D after submitting it to {Printer}.", item.Id, item.Printer);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            retryAfter[path] = DateTimeOffset.UtcNow.AddMinutes(1);
            logger.LogError(exception, "Unable to complete print job {Path}. Retrying in one minute.", path);
        }
    }

    private async Task MarkError(string id, CancellationToken cancellationToken)
    {
        try
        {
            await medpointeService.SetPrintStatus(id, "E", cancellationToken);
            logger.LogWarning("Job {Id} marked E; the local agent will retry.", id);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Unable to mark print job {Id} as failed.", id);
        }
    }

    private string JobPath(string id) => Path.Combine(JobsDirectory, Uri.EscapeDataString(id) + PendingSuffix);

    private string SubmittedPath(string id) => Path.Combine(JobsDirectory, Uri.EscapeDataString(id) + SubmittedSuffix);

    private static async Task<HashSet<string>> GetAvailablePrinters(CancellationToken cancellationToken)
    {
        bool windows = OperatingSystem.IsWindows();
        ProcessStartInfo startInfo = new(windows ? "powershell.exe" : "/usr/bin/lpstat")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        if (windows)
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-CimInstance Win32_Printer | Where-Object { -not $_.WorkOffline -and $_.PrinterStatus -notin 6,7 -and $_.PortName -notin 'PORTPROMPT:', 'FILE:' } | Select-Object -ExpandProperty Name");
            startInfo.StandardOutputEncoding = Encoding.UTF8;
        }
        else
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.Environment["LC_ALL"] = "C";
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Unable to check local printers.");

        HashSet<string> printers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (windows)
                printers.Add(line.Trim());
            else if (line.StartsWith("printer ") && line.Contains(" enabled "))
                printers.Add(line[8..].Split(' ', 2)[0]);
        }

        return printers;
    }
}
