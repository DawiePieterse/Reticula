namespace Reticula.Domain.Design;

public static class DesignRunModes
{
    /// <summary>One design with the engineer's options.</summary>
    public const string Run = "run";
    /// <summary>Three options by local search and siting (plan 5.2–5.4); the result holds every option.</summary>
    public const string Optimise = "optimise";
    /// <summary>An option of an optimisation run, adopted as the project's design.</summary>
    public const string Adopted = "adopted";
    /// <summary>A revision's design run again from its stored request, to show it reproduces (plan 7.2).</summary>
    public const string Reproduce = "reproduce";

    public static readonly IReadOnlyList<string> All = [Run, Optimise, Adopted, Reproduce];
}

/// <summary>
/// A design run (ADR 0011): the exact request sent to the calc service and its full result, never edited afterwards. The request holds
/// every input (routes, sites, loads, classes, contours, connection point, options and rates), so the run can be repeated; its hash
/// tells when the project's inputs have moved on.
/// </summary>
public sealed class DesignRun
{
    private DesignRun() { } // EF

    public DesignRun(Guid id, Guid projectId, int number, string mode, Guid? parentRunId, string rulesRef, string inputsHash, string inputsPartsJson,
        string requestJson, Guid createdBy, DateTimeOffset now, Guid? jobId)
    {
        InputsPartsJson = inputsPartsJson;
        Id = id;
        ProjectId = projectId;
        Number = number;
        Mode = mode;
        ParentRunId = parentRunId;
        RulesRef = rulesRef;
        InputsHash = inputsHash;
        RequestJson = requestJson;
        CreatedBy = createdBy;
        CreatedAt = now;
        JobId = jobId;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    /// <summary>1, 2, 3… within the project.</summary>
    public int Number { get; private set; }
    public string Mode { get; private set; } = DesignRunModes.Run;
    /// <summary>The optimisation run an adopted design came from, or the run a reproduction repeats.</summary>
    public Guid? ParentRunId { get; private set; }
    public string RulesRef { get; private set; } = "";
    public string RulesHash { get; private set; } = "";
    /// <summary>
    /// SHA-256 of the project's inputs as the API gathered them (routes and sites, loads, contours, connection point, rates, rules), without
    /// the engineer's run options: a later hash that differs means the project has moved on and this run is stale.
    /// </summary>
    public string InputsHash { get; private set; } = "";
    /// <summary>The hash of each part of the inputs, so a stale run can say what changed.</summary>
    public string InputsPartsJson { get; private set; } = "{}";
    public string RequestJson { get; private set; } = "{}";
    /// <summary>The calc service's result: a Design, or an optimisation result with its options.</summary>
    public string? ResultJson { get; private set; }
    /// <summary>SHA-256 of the result in canonical form (keys sorted, numbers as IEEE doubles), for bit-for-bit comparison.</summary>
    public string? ResultHash { get; private set; }
    public string? Construction { get; private set; }
    public bool FitToSubmit { get; private set; }
    public int Checks { get; private set; }
    public int Failures { get; private set; }
    public double? Capex { get; private set; }
    public double? Lifetime { get; private set; }
    public string? SummaryJson { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public Guid? JobId { get; private set; }

    public void Complete(string rulesHash, string resultJson, string resultHash, string? construction, bool fit, int checks, int failures,
        double? capex, double? lifetime, string summaryJson, DateTimeOffset now)
    {
        RulesHash = rulesHash;
        ResultJson = resultJson;
        ResultHash = resultHash;
        Construction = construction;
        FitToSubmit = fit;
        Checks = checks;
        Failures = failures;
        Capex = capex;
        Lifetime = lifetime;
        SummaryJson = summaryJson;
        FinishedAt = now;
    }

    /// <summary>A run that produced a single design (not an optimisation, which holds several).</summary>
    public bool HasDesign => Mode != DesignRunModes.Optimise && ResultJson is not null;
}
