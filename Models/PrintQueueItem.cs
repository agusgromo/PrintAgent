using System.Text.Json.Serialization;

namespace PrintAgent.Models;

public sealed class PrintQueueItem
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("filename")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("printer")]
    public string Printer { get; init; } = string.Empty;
}