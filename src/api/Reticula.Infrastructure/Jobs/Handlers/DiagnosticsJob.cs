using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Jobs.Handlers;

/// <summary>
/// End-to-end check of the job pipeline and its dependencies: database, calc service and rules files.
/// Proves progress reporting before the first real design run exists.
/// </summary>
public sealed class DiagnosticsJob(ReticulaDbContext db, ICalcClient calc) : IJobHandler
{
    public const string JobKind = "system.diagnostics";
    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        await context.Progress.ReportAsync(10, "Checking database", ct);
        var projects = await db.Projects.CountAsync(p => p.ArchivedAt == null, ct);

        await context.Progress.ReportAsync(30, "Checking calc service", ct);
        if (!await calc.IsHealthyAsync(ct)) throw new CalcUnavailableException("Calc service is not healthy.");

        await context.Progress.ReportAsync(50, "Listing rules files", ct);
        var refs = await calc.ListRulesAsync(ct);

        var rules = new List<object>(refs.Count);
        for (var i = 0; i < refs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await context.Progress.ReportAsync(50 + 45 * (i + 1) / Math.Max(refs.Count, 1), $"Validating {refs[i]}", ct);
            var info = await calc.GetRulesInfoAsync(refs[i], ct)
                       ?? throw new InvalidOperationException($"Rules file {refs[i]} is listed but cannot be loaded.");
            rules.Add(new { info.Ref, info.Hash, info.EffectiveDate });
        }

        return new { projects, calc = "ok", rules, durationMs = sw.ElapsedMilliseconds };
    }
}
