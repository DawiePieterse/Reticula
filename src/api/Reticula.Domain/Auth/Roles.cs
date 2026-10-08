namespace Reticula.Domain.Auth;

public static class Roles
{
    /// <summary>The registered engineer. Only this role can create projects, finalise designs and sign off.</summary>
    public const string Engineer = "engineer";

    /// <summary>Field inspector. Captures inspections; cannot finalise or sign off.</summary>
    public const string Inspector = "inspector";

    public static readonly IReadOnlyList<string> All = [Engineer, Inspector];
}

public static class Policies
{
    public const string Engineer = "engineer";
    public const string FieldUser = "field-user";
}
