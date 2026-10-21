using System.Text.Json.Serialization;

namespace PrintAgent.Models;

public sealed class PrintQueueItem

{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("filename")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public string User { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("office")]
    public string Office { get; init; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("printer")]
    public string Printer { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}