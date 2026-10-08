namespace Reticula.Domain.Costing;

/// <summary>
/// A price list (plan 6.1): rate items and the assemblies built from them, with the date and source of the prices. One library is
/// active at a time; importing a supplier's list makes a new library from the active one with the imported rates. Every cost a design
/// reports is stamped with the library's name and date.
/// </summary>
public sealed class RateLibrary
{
    private RateLibrary() { } // EF

    public RateLibrary(Guid id, string name, DateOnly rateDate, string source, string currency, bool indicative, string itemsJson,
        string assembliesJson, Guid importedBy, DateTimeOffset now)
    {
        Id = id;
        Name = name;
        RateDate = rateDate;
        Source = source;
        Currency = currency;
        Indicative = indicative;
        ItemsJson = itemsJson;
        AssembliesJson = assembliesJson;
        ImportedBy = importedBy;
        ImportedAt = now;
        Active = true;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public DateOnly RateDate { get; private set; }
    public string Source { get; private set; } = "";
    public string Currency { get; private set; } = "ZAR";
    /// <summary>Placeholder rates, not a price list: every cost from them is an estimate.</summary>
    public bool Indicative { get; private set; }
    /// <summary>Rate items in the calc service's shape (code, description, unit, rate, category, rate_date, source, uncertainty_pct).</summary>
    public string ItemsJson { get; private set; } = "[]";
    public string AssembliesJson { get; private set; } = "[]";
    public Guid ImportedBy { get; private set; }
    public DateTimeOffset ImportedAt { get; private set; }
    public bool Active { get; private set; }

    public void Deactivate() => Active = false;
}

/// <summary>The engineer's own rate for one item, with its date and reason; it replaces the library's rate until removed.</summary>
public sealed class RateOverride
{
    private RateOverride() { } // EF

    public RateOverride(Guid id, string itemCode, double rate, DateOnly rateDate, string source, Guid by, DateTimeOffset now)
    {
        Id = id;
        ItemCode = itemCode;
        Rate = rate;
        RateDate = rateDate;
        Source = source;
        CreatedBy = by;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public string ItemCode { get; private set; } = "";
    public double Rate { get; private set; }
    public DateOnly RateDate { get; private set; }
    public string Source { get; private set; } = "";
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }

    public void Remove(DateTimeOffset now) => RemovedAt = now;
}
