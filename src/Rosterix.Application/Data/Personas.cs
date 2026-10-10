using System.Security.Cryptography;
using System.Text;

namespace Rosterix.Application.Data;

public sealed record PersonaInfo(string Key, Guid Id, string Title, string Blurb);

public static class Personas
{
    public static readonly Guid Priya = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid Arjun = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid Meera = new("00000000-0000-0000-0000-000000000003");
    public static readonly Guid Karthik = new("00000000-0000-0000-0000-000000000004");
    public static readonly Guid Fathima = new("00000000-0000-0000-0000-000000000005");
    public static readonly Guid Ravi = new("00000000-0000-0000-0000-000000000006");

    public static readonly IReadOnlyList<PersonaInfo> All = new[]
    {
        new PersonaInfo("priya", Priya, "Project Manager", "Creates projects, tags people, builds rosters, processes allowances"),
        new PersonaInfo("arjun", Arjun, "Line Manager", "Approves PM requests, often on a phone; delegates when travelling"),
        new PersonaInfo("meera", Meera, "HR Business Partner", "Approves tagging for a business unit, reviews exceptions weekly"),
        new PersonaInfo("karthik", Karthik, "Regional Head", "Final approval on tagging and aged arrears"),
        new PersonaInfo("fathima", Fathima, "HRSS Specialist", "Owns foundation data, movements, anomalies and payroll"),
        new PersonaInfo("ravi", Ravi, "Shift Associate", "Sees own roster, signs declarations, understands allowance"),
    };

    public static PersonaInfo? ByKey(string key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Deterministic GUID from a stable key so seeded evidence links never change between boots.</summary>
    public static Guid Gid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes("rosterix:" + key)));
}
