using NetTopologySuite.Geometries;

namespace Reticula.Domain.Design;

/// <summary>
/// A project's LV network model (plan 2.1): the LV routes and sites marked in the field, joined into nodes and branches by
/// the calc service. A project has at most one; building it again replaces it. The topology checks, feeders and summary are
/// kept as the calc service returned them.
/// </summary>
public sealed class LvNetwork
{
    private LvNetwork() { } // EF

    public LvNetwork(Guid id, Guid projectId, string rulesRef, string rulesHash, string clause, string summaryJson, string feedersJson,
        string issuesJson, int errorCount, Guid builtBy, DateTimeOffset builtAt, string? loadsClause = null, string? loadsSummaryJson = null,
        string phasesJson = "[]", string boxesJson = "[]", string? analysisJson = null, string? overheadJson = null)
    {
        Id = id;
        ProjectId = projectId;
        RulesRef = rulesRef;
        RulesHash = rulesHash;
        Clause = clause;
        SummaryJson = summaryJson;
        FeedersJson = feedersJson;
        IssuesJson = issuesJson;
        ErrorCount = errorCount;
        BuiltBy = builtBy;
        BuiltAt = builtAt;
        LoadsClause = loadsClause;
        LoadsSummaryJson = loadsSummaryJson;
        PhasesJson = phasesJson;
        BoxesJson = boxesJson;
        AnalysisJson = analysisJson;
        OverheadJson = overheadJson;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string RulesRef { get; private set; } = "";
    public string RulesHash { get; private set; } = "";

    /// <summary>Where the joining tolerances come from (the rules file's lv_network clause).</summary>
    public string Clause { get; private set; } = "";
    public string SummaryJson { get; private set; } = "{}";
    public string FeedersJson { get; private set; } = "[]";
    public string IssuesJson { get; private set; } = "[]";

    /// <summary>Issues of severity error, such as loops or sources tied together: the network is not fit for design checks.</summary>
    public int ErrorCount { get; private set; }
    public Guid BuiltBy { get; private set; }
    public DateTimeOffset BuiltAt { get; private set; }

    /// <summary>Where the service and phasing rules come from; null when the rules file has none and loads were not connected.</summary>
    public string? LoadsClause { get; private set; }

    /// <summary>Counts of loads connected, not connected and without an estimate; null when loads were not connected.</summary>
    public string? LoadsSummaryJson { get; private set; }

    /// <summary>Customers, kVA and boxes on each phase of each feeder.</summary>
    public string PhasesJson { get; private set; } = "[]";

    /// <summary>Service distribution boxes on poles, with their phase and loads.</summary>
    public string BoxesJson { get; private set; } = "[]";

    /// <summary>Voltage drop, thermal loading and fault level (plan 2.4); null when the rules file has no design settings.</summary>
    public string? AnalysisJson { get; private set; }

    /// <summary>Spans, sag and tension, ground clearance, pole loads and stays (plan 2.5); null when the rules file has no overhead settings.</summary>
    public string? OverheadJson { get; private set; }
}

/// <summary>
/// One building's load as connected to the LV network (plan 2.2): where its service meets the network and which phase it is on.
/// A snapshot taken when the network was built; the load point and building ids are kept without foreign keys.
/// </summary>
public sealed class LvLoad
{
    private LvLoad() { } // EF

    public LvLoad(Guid id, Guid networkId, Guid loadPointId, Guid buildingId, string? label, string kind, double kva, string branchKey,
        string? nodeKey, double offsetM, double serviceM, string? box, string? feeder, double? distanceM, string? phase, LineString service)
    {
        Box = box;
        Id = id;
        NetworkId = networkId;
        LoadPointId = loadPointId;
        BuildingId = buildingId;
        Label = label;
        Kind = kind;
        Kva = kva;
        BranchKey = branchKey;
        NodeKey = nodeKey;
        OffsetM = offsetM;
        ServiceM = serviceM;
        Feeder = feeder;
        DistanceM = distanceM;
        Phase = phase;
        Service = service;
    }

    public Guid Id { get; private set; }
    public Guid NetworkId { get; private set; }
    public Guid LoadPointId { get; private set; }
    public Guid BuildingId { get; private set; }

    /// <summary>Erf number, when the building is on a stand.</summary>
    public string? Label { get; private set; }
    public string Kind { get; private set; } = "";
    public double Kva { get; private set; }

    /// <summary>The branch the service connects to, and where along it from its from_node.</summary>
    public string BranchKey { get; private set; } = "";
    public string? NodeKey { get; private set; }
    public double OffsetM { get; private set; }
    public double ServiceM { get; private set; }

    /// <summary>The service distribution box on the pole, e.g. P3-1; null for three-phase loads.</summary>
    public string? Box { get; private set; }
    public string? Feeder { get; private set; }
    public double? DistanceM { get; private set; }

    /// <summary>R, W or B for single-phase loads, RWB for three-phase; null off a feeder.</summary>
    public string? Phase { get; private set; }

    /// <summary>From the building to where the service meets the network.</summary>
    public LineString Service { get; private set; } = null!;
}

public static class LvNodeKinds
{
    public const string Source = "source";
    public const string Pole = "pole";
    public const string Junction = "junction";
    public const string Joint = "joint";
    public const string End = "end";
}

public sealed class LvNode
{
    private LvNode() { } // EF

    public LvNode(Guid id, Guid networkId, string key, string kind, Point geometry, string? label, Guid? candidateId, string? feeder, double? distanceM)
    {
        Id = id;
        NetworkId = networkId;
        Key = key;
        Kind = kind;
        Geometry = geometry;
        Label = label;
        CandidateId = candidateId;
        Feeder = feeder;
        DistanceM = distanceM;
    }

    public Guid Id { get; private set; }
    public Guid NetworkId { get; private set; }

    /// <summary>The calc service's id within the network, e.g. N12. Branches refer to nodes by it.</summary>
    public string Key { get; private set; } = "";
    public string Kind { get; private set; } = "";
    public Point Geometry { get; private set; } = null!;

    /// <summary>TX1, MS1 or P1 at a marked site.</summary>
    public string? Label { get; private set; }

    /// <summary>The marked site the node stands for.</summary>
    public Guid? CandidateId { get; private set; }
    public string? Feeder { get; private set; }

    /// <summary>Along the network from the source, when the node is in a radial part with one source.</summary>
    public double? DistanceM { get; private set; }
}

public static class LvBranchKinds
{
    public const string Route = "route";
    public const string Link = "link";
}

public sealed class LvBranch
{
    private LvBranch() { } // EF

    public LvBranch(Guid id, Guid networkId, string key, string kind, string fromKey, string toKey, LineString geometry, double lengthM,
        Guid? candidateId, string? feeder)
    {
        Id = id;
        NetworkId = networkId;
        Key = key;
        Kind = kind;
        FromKey = fromKey;
        ToKey = toKey;
        Geometry = geometry;
        LengthM = lengthM;
        CandidateId = candidateId;
        Feeder = feeder;
    }

    public Guid Id { get; private set; }
    public Guid NetworkId { get; private set; }
    public string Key { get; private set; } = "";

    /// <summary>route: part of a marked LV route. link: from a source to the nearest point on its route.</summary>
    public string Kind { get; private set; } = "";

    /// <summary>In a fed radial part, the end nearer the source.</summary>
    public string FromKey { get; private set; } = "";
    public string ToKey { get; private set; } = "";
    public LineString Geometry { get; private set; } = null!;
    public double LengthM { get; private set; }

    /// <summary>The marked route this branch is part of, or the source a link connects.</summary>
    public Guid? CandidateId { get; private set; }
    public string? Feeder { get; private set; }
}
