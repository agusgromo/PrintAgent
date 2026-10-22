using System.Diagnostics;
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
    private bool legacyJobsMoved;
    private string JobsDirectory => Path.Combine(hostEnvironment.ContentRootPath, "Workers", "Jobs");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PrintAgent started. Downloads: {Downloads}; local jobs: {Jobs}.",
            Path.Combine(hostEnvironment.ContentRootPath, "Downloads"), JobsDirectory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueue(stoppingToken);
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

    private async Task ProcessQueue(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(JobsDirectory);
        if (!legacyJobsMoved)
        {
            foreach (string oldPath in Directory.GetFiles(hostEnvironment.ContentRootPath, "*" + PendingSuffix))
            {
                string newPath = Path.Combine(JobsDirectory, Path.GetFileName(oldPath));
                if (!File.Exists(newPath)) File.Move(oldPath, newPath);
            }
            legacyJobsMoved = true;
        }

        List<string> localJobs = Directory.GetFiles(JobsDirectory, "*" + PendingSuffix).ToList();
        foreach (string path in localJobs.Where(path => path.EndsWith(SubmittedSuffix, StringComparison.OrdinalIgnoreCase)))
            await CompleteSubmitted(path, cancellationToken);

        List<string> pendingJobs = localJobs.Where(path => !path.EndsWith(SubmittedSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
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

        HashSet<string> printers;
        try
        {
            printers = await GetAvailablePrinters(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Unable to check local printers.");
            printers = new(StringComparer.OrdinalIgnoreCase);
        }

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

            if (!submittedJobs.Contains(item.Id))
            {
                if (!printers.Contains(item.Printer.Trim()))
                    throw new InvalidOperationException($"Printer {item.Printer} is unavailable.");

                await medpointeService.SetPrintStatus(item.Id, "P", cancellationToken);
                logger.LogInformation("Job {Id} marked P for printer {Printer}.", item.Id, item.Printer);
                await medpointeService.SendPrint([item], cancellationToken);
                submittedJobs.Add(item.Id);
            }

            File.Move(path, SubmittedPath(item.Id), overwrite: true);
            retryAfter.Remove(path);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            retryAfter[path] = DateTimeOffset.UtcNow.AddMinutes(1);
            logger.LogError(exception, "Unable to process local print job {Path}. Retrying in one minute.", path);
            if (item is not null && !submittedJobs.Contains(item.Id))
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
            await medpointeService.SetPrintStatus(item.Id, "D", cancellationToken);
            File.Delete(path);
            retryAfter.Remove(path);
            submittedJobs.Remove(item.Id);
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
        ProcessStartInfo startInfo = new(windows ? "powershell.exe" : "lpstat")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        if (windows)
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-CimInstance Win32_Printer | Where-Object { -not $_.WorkOffline -and $_.PrinterStatus -notin 6,7 } | Select-Object -ExpandProperty Name");
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
