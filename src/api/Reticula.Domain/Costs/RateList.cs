namespace Reticula.Domain.Costs;

/// <summary>
/// An editable rate list (plan 6.1): a copy of a shipped list (rates/&lt;name&gt;/&lt;date&gt;.yaml) or of another rate
/// list, with dated overrides from the engineer or a supplier price list. The content is the calc service's rate-list
/// format (snake_case JSON) with the overrides applied; design runs send it inline and store it with their input.
/// </summary>
public sealed class RateList
{
    private RateList() { } // EF

    public RateList(Guid id, string name, string basedOn, DateOnly rateDate, string currency, string contentJson, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        Name = name;
        BasedOn = basedOn;
        RateDate = rateDate;
        Currency = currency;
        ContentJson = contentJson;
        CreatedBy = createdBy;
        CreatedAt = now;
        UpdatedBy = createdBy;
        UpdatedAt = now;
        Revision = 1;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>The shipped list or rate list this one was copied from.</summary>
    public string BasedOn { get; private set; } = "";
    public DateOnly RateDate { get; private set; }
    public string Currency { get; private set; } = "ZAR";
    public string ContentJson { get; private set; } = "{}";
    /// <summary>Every override applied: section, code, rate, previous rate, date, source.</summary>
    public string OverridesJson { get; private set; } = "[]";
    /// <summary>Goes up with every change, so a design's input names the exact rates it used.</summary>
    public int Revision { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>Optimistic concurrency token (Postgres xmin).</summary>
    public uint Version { get; private set; }

    public void Change(string contentJson, string overridesJson, DateOnly rateDate, Guid by, DateTimeOffset now)
    {
        ContentJson = contentJson;
        OverridesJson = overridesJson;
        RateDate = rateDate;
        UpdatedBy = by;
        UpdatedAt = now;
        Revision++;
    }

    public void Archive(DateTimeOffset now) => ArchivedAt = now;
}
