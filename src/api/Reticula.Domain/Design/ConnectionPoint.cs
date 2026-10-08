using NetTopologySuite.Geometries;

namespace Reticula.Domain.Design;

/// <summary>
/// The authority's point of supply for a project (plan 4.1): where the MV network connects and what the authority says it can
/// supply there. Without the capacity and the three-phase fault level the bulk studies do not run and the design is not fit to submit.
/// </summary>
public sealed class ConnectionPoint
{
    private ConnectionPoint() { } // EF

    public ConnectionPoint(Guid projectId, Point location, Guid by, DateTimeOffset now)
    {
        ProjectId = projectId;
        Location = location;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public Guid ProjectId { get; private set; }
    public Point Location { get; private set; } = null!;
    public double? VoltageKv { get; private set; }
    /// <summary>What the authority can supply at this point.</summary>
    public double? CapacityKva { get; private set; }
    /// <summary>Three-phase fault level, maximum.</summary>
    public double? Fault3PhKa { get; private set; }
    public double? Fault3PhMinKa { get; private set; }
    public double? Fault1PhKa { get; private set; }
    public double? XOverR { get; private set; }
    /// <summary>Where the values come from: the authority's letter or quotation reference.</summary>
    public string? Source { get; private set; }
    public DateOnly? ReceivedOn { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public void Update(Point location, double? voltageKv, double? capacityKva, double? fault3PhKa, double? fault3PhMinKa, double? fault1PhKa,
        double? xOverR, string? source, DateOnly? receivedOn, Guid by, DateTimeOffset now)
    {
        Location = location;
        VoltageKv = voltageKv;
        CapacityKva = capacityKva;
        Fault3PhKa = fault3PhKa;
        Fault3PhMinKa = fault3PhMinKa;
        Fault1PhKa = fault1PhKa;
        XOverR = xOverR;
        Source = source;
        ReceivedOn = receivedOn;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    /// <summary>The studies need the capacity and the maximum three-phase fault level.</summary>
    public bool IsComplete => CapacityKva is > 0 && Fault3PhKa is > 0;
}
