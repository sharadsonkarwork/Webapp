using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Application.Data;

/// <summary>In-memory read model shared by the seeder, the AI services, the calculator and tests.</summary>
public sealed class Dataset
{
    public DateOnly Today { get; set; } = SeedClock.Today;
    public List<Region> Regions { get; set; } = new();
    public List<BusinessUnit> BusinessUnits { get; set; } = new();
    public List<Employee> Employees { get; set; } = new();
    public List<EmployeePreference> Preferences { get; set; } = new();
    public List<Delegation> Delegations { get; set; } = new();
    public List<LeaveRecord> Leaves { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    public List<ProjectShiftTag> ProjectShiftTags { get; set; } = new();
    public List<EmployeeTag> EmployeeTags { get; set; } = new();
    public List<Movement> Movements { get; set; } = new();
    public List<ShiftType> ShiftTypes { get; set; } = new();
    public List<ShiftRule> ShiftRules { get; set; } = new();
    public List<AllowanceRate> Rates { get; set; } = new();
    public List<HolidayCalendar> Holidays { get; set; } = new();
    public List<Roster> Rosters { get; set; } = new();
    public List<RosterEntry> RosterEntries { get; set; } = new();
    public List<ApprovalRequest> ApprovalRequests { get; set; } = new();
    public List<ApprovalStep> ApprovalSteps { get; set; } = new();
    public List<ApprovalChainDefinition> Chains { get; set; } = new();
    public List<AllowanceRun> Runs { get; set; } = new();
    public List<AllowanceLine> Lines { get; set; } = new();
    public List<ExceptionItem> Exceptions { get; set; } = new();
    public List<Arrear> Arrears { get; set; } = new();
    public List<PayrollBatch> Batches { get; set; } = new();
    public List<Declaration> Declarations { get; set; } = new();
    public List<Settlement> Settlements { get; set; } = new();
    public List<Notification> Notifications { get; set; } = new();
    public List<AiFeedback> AiFeedback { get; set; } = new();

    Dictionary<Guid, Employee>? _emp; Dictionary<Guid, ShiftType>? _st; Dictionary<Guid, Project>? _pr; Dictionary<Guid, BusinessUnit>? _bu;
    public Employee Emp(Guid id) => (_emp ??= Employees.ToDictionary(e => e.Id))[id];
    public Employee? EmpOrNull(Guid? id) => id != null && (_emp ??= Employees.ToDictionary(e => e.Id)).TryGetValue(id.Value, out var e) ? e : null;
    public ShiftType Shift(Guid id) => (_st ??= ShiftTypes.ToDictionary(e => e.Id))[id];
    public Project Proj(Guid id) => (_pr ??= Projects.ToDictionary(e => e.Id))[id];
    public BusinessUnit BU(Guid id) => (_bu ??= BusinessUnits.ToDictionary(e => e.Id))[id];
    public void ResetIndexes() { _emp = null; _st = null; _pr = null; _bu = null; }

    public IEnumerable<RosterEntry> EntriesFor(Guid rosterId) => RosterEntries.Where(e => e.RosterId == rosterId);
    public Roster? FindRoster(Guid projectId, string month) => Rosters.FirstOrDefault(r => r.ProjectId == projectId && r.PeriodMonth == month);
    public Guid RosterProject(Guid rosterId) => Rosters.First(r => r.Id == rosterId).ProjectId;
}

public static class SeedClock
{
    /// <summary>Deterministic 'today' used by the canonical seed so heuristics are reproducible.</summary>
    public static readonly DateOnly Today = new(2026, 10, 10);
}
