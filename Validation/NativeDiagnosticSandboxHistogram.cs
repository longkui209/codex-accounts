using System.Text.Json;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

/// <summary>Only the numeric OS errors of this new browser's sandbox launches; no process memory or user data.</summary>
internal sealed class NativeDiagnosticSandboxHistogram
{
    internal sealed record Bucket(long Low, long High, long Count);
    internal sealed record Sample(long Count, long Sum, IReadOnlyList<Bucket> Buckets);
    private const string HistogramName = "Process.Sandbox.Launch.Error";
    internal Sample? Latest { get; private set; }
    internal int SuccessfulQueries { get; private set; }
    internal string? Termination { get; private set; }

    internal async Task ObserveAsync(NativeSurfacePipe pipe, CancellationToken cancellation)
    {
        try
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                var result = await pipe.SendAsync("Browser.getHistograms", new { query = HistogramName, delta = false }, cancellation: cancellation).ConfigureAwait(false);
                if (!result.TryGetProperty("histograms", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 64)
                    throw new IOException();
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.GetProperty("name").GetString() != HistogramName) continue;
                    var buckets = row.GetProperty("buckets");
                    if (buckets.ValueKind != JsonValueKind.Array || buckets.GetArrayLength() > 256) throw new IOException();
                    Latest = new(row.GetProperty("count").GetInt64(), row.GetProperty("sum").GetInt64(),
                        buckets.EnumerateArray().Select(item => new Bucket(item.GetProperty("low").GetInt64(), item.GetProperty("high").GetInt64(), item.GetProperty("count").GetInt64())).ToArray());
                }
                SuccessfulQueries++;
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            Termination = "observation-bound-reached";
        }
        catch (OperationCanceledException) { Termination = "cancelled"; }
        catch (Exception error) { Termination = error.GetType().Name; }
    }
}
