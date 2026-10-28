using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PrintAgent.Models;

namespace PrintAgent.Services;

public class MedpointeService(HttpClient httpClient, ILogger<MedpointeService> logger, IHostEnvironment hostEnvironment)
{
    public int Port { get; set; }

    private string Endpoint(string path) => $"https://medpointe.bmismartcloud.com:{Port}/medpointe/web/{path}";

    public static bool CanPrintPdf() => !OperatingSystem.IsWindows() ||
        new ProcessStartInfo("document.pdf") { UseShellExecute = true }.Verbs.Contains("printto", StringComparer.OrdinalIgnoreCase);

    public async Task<List<PrintQueueItem>> GetPrintQueue(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(Endpoint("getPrintQueue.mp"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        using HttpResponseMessage response = await httpClient.GetAsync(Endpoint(requestUri), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument statusResponse = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        if (statusResponse.RootElement.GetProperty("response").GetString() != "OK")
            throw new InvalidDataException($"PMP did not confirm the status update for print job {id}.");
    }

    private async Task<string> GetFile(PrintQueueItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) throw new ArgumentException("The print job must have an ID.", nameof(item));

        string downloadsDirectory = Path.Combine(hostEnvironment.ContentRootPath, "Downloads", Port.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(downloadsDirectory);

        string? fileName = Path.GetFileName(item.FileName?.Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidDataException($"Print job {item.Id} has no filename.");
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Print job {item.Id} is not a PDF.");

        string storedFileName = $"{Uri.EscapeDataString(item.Id)}_{fileName}";
        string filePath = Path.Combine(downloadsDirectory, storedFileName);
        if (File.Exists(filePath))
        {
            logger.LogDebug("Using cached file for job {Id} at {Path}.", item.Id, filePath);
            return filePath;
        }

        string requestUri = $"getPrintDoc.mp?id={Uri.EscapeDataString(item.Id)}";
        using HttpResponseMessage response = await httpClient.GetAsync(Endpoint(requestUri), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

        string tempPath = filePath + ".download";
        await File.WriteAllBytesAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, filePath, overwrite: true);
        logger.LogInformation("Downloaded file for job {Id} to {Path}.", item.Id, filePath);

        return filePath;
    }

    public async Task SendPrint(PrintQueueItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Printer)) throw new ArgumentException($"Print job {item.Id} has no printer.", nameof(item));
        string filePath = await GetFile(item, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

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
                throw new NotSupportedException($"No application is registered to print {Path.GetExtension(filePath)} files to a specific printer.");

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start printing job {item.Id}.");
        }
        else
        {
            ProcessStartInfo startInfo = new("/usr/bin/lp")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(item.Printer);
            startInfo.ArgumentList.Add(filePath);
            startInfo.Environment["LC_ALL"] = "C";

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start the lp print command.");
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Unable to print job {item.Id}: {await error}");
        }
    }
}
