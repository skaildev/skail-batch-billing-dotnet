using System.Net.Http.Json;
using SkailBatchBilling.Contracts;

namespace SkailBatchBilling.Worker.Progress;

public sealed class HttpProgressNotifier(HttpClient http) : IProgressNotifier
{
    public async Task NotifyAsync(BatchEvent evt, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"internal/batches/{Uri.EscapeDataString(evt.BatchId)}/events", evt, Json.Options, ct);
        response.EnsureSuccessStatusCode();
    }
}
