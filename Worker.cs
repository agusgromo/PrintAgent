using System.Diagnostics;
using System.Text;
using PrintAgent.Services;

namespace PrintAgent;

public class Worker(ILogger<Worker> logger, MedpointeService medpointeService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HashSet<string> submittedJobs = new(StringComparer.OrdinalIgnoreCase);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueue(submittedJobs, stoppingToken);
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

    private async Task ProcessQueue(HashSet<string> submittedJobs, CancellationToken cancellationToken)
    {
        var queue = await medpointeService.GetPrintQueue(cancellationToken);
        submittedJobs.IntersectWith(queue.Select(item => item.Id));
        if (queue.Count == 0) return;

        var printers = await GetAvailablePrinters(cancellationToken);
        foreach (var item in queue.Where(item => printers.Contains(item.Printer.Trim())))
        {
            try
            {
                if (!submittedJobs.Contains(item.Id))
                {
                    await medpointeService.SendPrint([item], cancellationToken);
                    submittedJobs.Add(item.Id);
                }

                await medpointeService.SetPrintStatus(item.Id, "D", cancellationToken);
                submittedJobs.Remove(item.Id);
                logger.LogInformation("Printed job {Id} on {Printer}.", item.Id, item.Printer);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Unable to process print job {Id}.", item.Id);
            }
        }
    }

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
