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
        string issuesJson, int errorCount, Guid builtBy, DateTimeOffset builtAt)
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
