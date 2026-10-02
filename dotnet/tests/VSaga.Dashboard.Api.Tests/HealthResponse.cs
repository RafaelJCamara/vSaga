using System.Text.Json;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>The <c>/health</c> body, <c>{ status, checks: [{ name, status, description }] }</c>, parsed.</summary>
public sealed record HealthResponse(string Status, IReadOnlyDictionary<string, HealthCheckEntry> Checks)
{
    public static async Task<HealthResponse> ReadAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var checks = root.GetProperty("checks").EnumerateArray().ToDictionary(
            c => c.GetProperty("name").GetString()!,
            c => new HealthCheckEntry(c.GetProperty("status").GetString()!, c.GetProperty("description").GetString()),
            StringComparer.Ordinal);
        return new HealthResponse(root.GetProperty("status").GetString()!, checks);
    }
}

public sealed record HealthCheckEntry(string Status, string? Description);
