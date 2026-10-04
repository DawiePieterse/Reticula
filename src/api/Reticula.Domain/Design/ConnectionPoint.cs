using NetTopologySuite.Geometries;

namespace Reticula.Domain.Design;

/// <summary>
/// The supply authority's point of connection for a project (plan 4.1): where the MV network is fed from and what the
/// authority's network offers there. Capacity and fault levels come from the authority's quotation or budget letter;
/// the bulk supply study refuses to run while either is missing.
/// </summary>
public sealed class ConnectionPoint
{
    private ConnectionPoint() { } // EF

    public ConnectionPoint(Guid projectId, Point location, double voltageKv, Guid updatedBy, DateTimeOffset now)
    {
        ProjectId = projectId;
        Location = location;
        VoltageKv = voltageKv;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    public Guid ProjectId { get; private set; }
    public Point Location { get; private set; } = null!;
    public double VoltageKv { get; private set; }
    public double? AvailableCapacityKva { get; private set; }
    /// <summary>Maximum three-phase fault level at the point, MVA.</summary>
    public double? FaultMvaMax { get; private set; }
    /// <summary>Minimum fault level, MVA (for protection reach); the maximum is assumed when missing.</summary>
    public double? FaultMvaMin { get; private set; }
    public double? XR { get; private set; }
    /// <summary>Sending-end voltage the authority holds, % of nominal.</summary>
    public double? SendingVoltagePct { get; private set; }
    /// <summary>The authority's letter, quotation or reference number the values come from.</summary>
    public string? Reference { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(Point location, double voltageKv, double? availableCapacityKva, double? faultMvaMax, double? faultMvaMin, double? xr,
        double? sendingVoltagePct, string? reference, Guid updatedBy, DateTimeOffset now)
    {
        Location = location;
        VoltageKv = voltageKv;
        AvailableCapacityKva = availableCapacityKva;
        FaultMvaMax = faultMvaMax;
        FaultMvaMin = faultMvaMin;
        XR = xr;
        SendingVoltagePct = sendingVoltagePct;
        Reference = reference;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    /// <summary>What the bulk supply study still needs from the authority.</summary>
    public IReadOnlyList<string> MissingForStudy()
    {
        var missing = new List<string>();
        if (AvailableCapacityKva is null) missing.Add("available capacity");
        if (FaultMvaMax is null) missing.Add("fault level");
        return missing;
    }
}
