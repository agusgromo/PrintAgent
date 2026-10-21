using System.Diagnostics;
using System.Text.Json;
using PrintAgent.Models;

namespace PrintAgent.Services;

public class MedpointeService(HttpClient httpClient, ILogger<MedpointeService> logger, IHostEnvironment hostEnvironment)
{
    public async Task<List<PrintQueueItem>> GetPrintQueue(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync("getPrintQueue.mp", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument queueResponse = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        List<PrintQueueItem> printQueue = queueResponse.RootElement.GetProperty("printq").Deserialize<List<PrintQueueItem>>() ?? [];
        logger.LogDebug("GetPrintQueue - Jobs: {Count}", printQueue.Count);
        return printQueue;
    }

    public async Task SetPrintStatus(string id, string status, CancellationToken cancellationToken)
    {
        string requestUri = $"setPrintStatus.mp?id={Uri.EscapeDataString(id)}&status={Uri.EscapeDataString(status)}";
        using HttpResponseMessage response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument statusResponse = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        if (statusResponse.RootElement.GetProperty("response").GetString() != "OK")
            throw new InvalidDataException($"PMP did not confirm the status update for print job {id}.");
    }

    public async Task<Dictionary<PrintQueueItem, string>> GetFiles(IEnumerable<PrintQueueItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        Dictionary<PrintQueueItem, string> downloadedPrintFiles = [];
        Directory.CreateDirectory(hostEnvironment.ContentRootPath);

        foreach (PrintQueueItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) throw new ArgumentException("Each print queue item must have an ID.", nameof(items));

            string requestUri = $"getPrintDoc.mp?id={Uri.EscapeDataString(item.Id)}";
            using HttpResponseMessage response = await httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument printDocResponse = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            string? printDoc = printDocResponse.RootElement.TryGetProperty("printdoc", out JsonElement value) ? value.GetString() : null;

            if (string.IsNullOrEmpty(printDoc)) throw new FileNotFoundException($"PMP did not return a file for print job {item.Id}.", item.FileName);

            byte[] content;

            try
            {
                content = Convert.FromBase64String(printDoc);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException($"PMP returned invalid Base64 for print job {item.Id}.", exception);
            }

            string fileName = Path.GetFileName(item.FileName.Trim().Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidDataException($"Print job {item.Id} has no filename.");

            string filePath = Path.Combine(hostEnvironment.ContentRootPath, $"{Guid.NewGuid():N}_{fileName}");
            await File.WriteAllBytesAsync(filePath, content, cancellationToken);
            downloadedPrintFiles.Add(item, filePath);
        }

        return downloadedPrintFiles;
    }

    public async Task SendPrint(List<PrintQueueItem> ItemsToPrint, CancellationToken cancellationToken)
    {
        Dictionary<PrintQueueItem, string> files = await GetFiles(ItemsToPrint, cancellationToken);

        foreach ((PrintQueueItem item, string filePath) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Printer)) throw new ArgumentException($"Print job {item.Id} has no printer.", nameof(ItemsToPrint));

            if (OperatingSystem.IsWindows())
            {
                ProcessStartInfo startInfo = new(filePath)
                {
                    UseShellExecute = true,
                    Verb = "printto",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add(item.Printer);
                if (!startInfo.Verbs.Contains("printto", StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"No application is registered to print {Path.GetExtension(filePath)} files to a specific printer.");

                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Unable to start printing job {item.Id}.");
            }
            else
            {
                ProcessStartInfo startInfo = new("lp")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-d");
                startInfo.ArgumentList.Add(item.Printer);
                startInfo.ArgumentList.Add(filePath);

                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Unable to start the lp print command.");
                Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Unable to print job {item.Id}: {await error}");
            }
        }
    }
}