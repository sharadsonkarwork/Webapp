namespace Rosterix.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[]? RowVersion { get; set; }
}

// ---- Organisation & people
public class Region : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
public class BusinessUnit : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public Guid RegionId { get; set; } }

public class Employee : Entity
{
    public string EmployeeNumber { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Grade { get; set; } = "G3";
    public Guid BusinessUnitId { get; set; }
    public Guid? ManagerId { get; set; }
    public Role Roles { get; set; } = Role.EMP;
    public bool IsShiftEligible { get; set; } = true;
    public DateOnly JoinedOn { get; set; }
    public DateOnly? ExitedOn { get; set; }
    public bool IsActive { get; set; } = true;
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class EmployeePreference : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid? PreferredShiftTypeId { get; set; }
    public DayOfWeek? PreferredOffDay { get; set; }
    public string? Note { get; set; }
}

public class Delegation : Entity
{
    public Guid FromEmployeeId { get; set; }
    public Guid ToEmployeeId { get; set; }
    public DelegationScope Scope { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly ValidTo { get; set; }
    public string Reason { get; set; } = "";
    public bool IsRevoked { get; set; }
    public bool IsActiveOn(DateOnly d) => !IsRevoked && d >= ValidFrom && d <= ValidTo;
}

public class LeaveRecord : Entity { public Guid EmployeeId { get; set; } public DateOnly Date { get; set; } public bool Approved { get; set; } = true; public string Kind { get; set; } = "Annual"; }

// ---- Projects & tagging
public class Project : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid BusinessUnitId { get; set; }
    public Guid OwnerPmId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public ProjectStatus Status { get; set; }
    public bool IsShiftProject { get; set; }
    public string CostCentre { get; set; } = "";
    public string ShiftTypeIdsCsv { get; set; } = ""; // shift types planned for this project
    public IEnumerable<Guid> ShiftTypeIds => ShiftTypeIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse);
}

public class ProjectShiftTag : Entity
{
    public Guid ProjectId { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public string ShiftTypeIdsCsv { get; set; } = "";
    public Guid? ApprovalRequestId { get; set; }
    public ShiftTagStatus Status { get; set; }
}

public class EmployeeTag : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid ProjectId { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public TagType TagType { get; set; }
    public TagStatus Status { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public Guid? SourceMovementId { get; set; }
    public bool Covers(DateOnly d) => d >= ValidFrom && (ValidTo == null || d <= ValidTo);
}

public class Movement : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid? FromProjectId { get; set; }
    public Guid ToProjectId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool IsBackdated { get; set; }
    public string Reason { get; set; } = "";
    public MovementStatus Status { get; set; }
    public string? RippleJson { get; set; }
    public string? StagedBy { get; set; }
    public string? CommittedBy { get; set; }
}

// ---- Foundation data
public class ShiftType : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public bool SpansMidnight { get; set; }
    public bool IsAllowanceEligible { get; set; }
    public bool IsActive { get; set; } = true;
    public double DurationHours => ShiftMath.DurationHours(StartTime, EndTime, SpansMidnight);
}

public class ShiftRule : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public RuleType RuleType { get; set; }
    public RuleScope Scope { get; set; }
    public Guid? ScopeId { get; set; }
    public string Parameters { get; set; } = "{}";
    public RuleSeverity Severity { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public class AllowanceRate : Entity
{
    public Guid ShiftTypeId { get; set; }
    public Guid? BusinessUnitId { get; set; }
    public string? Grade { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public class HolidayCalendar : Entity { public Guid RegionId { get; set; } public DateOnly Date { get; set; } public string Name { get; set; } = ""; public bool IsRestricted { get; set; } }

// ---- Roster
public class Roster : Entity
{
    public Guid ProjectId { get; set; }
    public string PeriodMonth { get; set; } = ""; // yyyy-MM
    public RosterStatus Status { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public string? PublishedBy { get; set; }
    public RosterSource Source { get; set; }
}

public class RosterEntry : Entity
{
    public Guid RosterId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public Guid? ShiftTypeId { get; set; }
    public EntryType EntryType { get; set; }
    public bool IsAiSuggested { get; set; }
    public Guid? AiSuggestionId { get; set; }
    public string? Notes { get; set; }
}

public class RosterChangeLog : Entity
{
    public Guid RosterEntryId { get; set; }
    public Guid RosterId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = "";
    public bool LeadTimeFlag { get; set; }
}

// ---- Approvals
public class ApprovalRequest : Entity
{
    public ApprovalType Type { get; set; }
    public Guid SubjectId { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public Guid RaisedBy { get; set; }
    public DateTime RaisedAtUtc { get; set; } = DateTime.UtcNow;
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public int CurrentStepIndex { get; set; }
    public double? RiskScore { get; set; }
    public string? RiskReasons { get; set; }
    public decimal? Value { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? SubjectEmployeeId { get; set; }
}

public class ApprovalStep : Entity
{
    public Guid ApprovalRequestId { get; set; }
    public int StepIndex { get; set; }
    public Role RequiredRole { get; set; }
    public Guid AssignedToEmployeeId { get; set; }
    public Guid? ActedByEmployeeId { get; set; }
    public Guid? ActedViaDelegationId { get; set; }
    public StepDecision Decision { get; set; } = StepDecision.Pending;
    public string? Comment { get; set; }
    public DateTime? ActedAtUtc { get; set; }
}

public class ApprovalChainDefinition : Entity { public ApprovalType RequestType { get; set; } public int StepIndex { get; set; } public Role RequiredRole { get; set; } public string? Condition { get; set; } }

// ---- Allowance, exceptions, payroll
public class AllowanceRun : Entity
{
    public Guid ProjectId { get; set; }
    public string PeriodMonth { get; set; } = "";
    public RunStatus Status { get; set; }
    public Guid CalculatedBy { get; set; }
    public decimal TotalAmount { get; set; }
    public int LineCount { get; set; }
    public int ExceptionCount { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public Guid? PayrollBatchId { get; set; }
}

public class AllowanceLine : Entity
{
    public Guid AllowanceRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public Guid ShiftTypeId { get; set; }
    public decimal RateApplied { get; set; }
    public decimal Amount { get; set; }
    public string CalculationTrace { get; set; } = "";
    public LineStatus Status { get; set; }
    public string? AdjustmentReason { get; set; }
}

public class ExceptionItem : Entity
{
    public Guid? AllowanceLineId { get; set; }
    public Guid? RosterEntryId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? EmployeeId { get; set; }
    public ExceptionCategory Category { get; set; }
    public string? RuleCode { get; set; }
    public ExceptionSeverity Severity { get; set; }
    public string Description { get; set; } = "";
    public double? AiConfidence { get; set; }
    public ExceptionStatus Status { get; set; }
    public Guid? AssignedTo { get; set; }
    public Guid? ResolvedBy { get; set; }
    public string? Resolution { get; set; }
}

public class Arrear : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid ProjectId { get; set; }
    public string ForPeriodMonth { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime RaisedAtUtc { get; set; } = DateTime.UtcNow;
    public ArrearStatus Status { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public double? RiskScore { get; set; }
    public string Reason { get; set; } = "";
    public int AgeMonths(DateOnly today)
    {
        var y = int.Parse(ForPeriodMonth[..4]); var m = int.Parse(ForPeriodMonth[5..7]);
        return (today.Year - y) * 12 + (today.Month - m);
    }
    public string AgeBucket(DateOnly today) { var a = AgeMonths(today); return a <= 3 ? "0-3" : a <= 6 ? "4-6" : ">6"; }
}

public class PayrollBatch : Entity
{
    public string PeriodMonth { get; set; } = "";
    public Guid SubmittedBy { get; set; }
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public string RunIdsCsv { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public int LineCount { get; set; }
    public string FileRef { get; set; } = "";
    public BatchStatus Status { get; set; }
}

public class Declaration : Entity { public Guid EmployeeId { get; set; } public string PeriodMonth { get; set; } = ""; public string Type { get; set; } = ""; public DateTime? SignedAtUtc { get; set; } public DeclarationStatus Status { get; set; } }

public class EmployeeQuery : Entity { public Guid EmployeeId { get; set; } public string PeriodMonth { get; set; } = ""; public Guid? AllowanceLineId { get; set; } public string Message { get; set; } = ""; public string Status { get; set; } = "Open"; }

public class Settlement : Entity
{
    public Guid EmployeeId { get; set; }
    public DateOnly ExitDate { get; set; }
    public decimal PendingAllowanceTotal { get; set; }
    public decimal PendingArrearTotal { get; set; }
    public ClearanceStatus ClearanceStatus { get; set; }
    public Guid? ClearedBy { get; set; }
    public DateTime? ClearedAtUtc { get; set; }
}

// ---- Cross-cutting
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipientEmployeeId { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationSeverity Severity { get; set; }
    public string LinkRoute { get; set; } = "/home";
    public DateTime? ReadAtUtc { get; set; }
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public string Channel { get; set; } = "InApp";
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? ActorEmployeeId { get; set; }
    public bool Succeeded { get; set; }
    public string OriginIp { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string? ResourceId { get; set; }
    public string Detail { get; set; } = "{}";
}

public class AiSuggestionRecord : Entity
{
    public string UseCaseCode { get; set; } = "";
    public string ContextType { get; set; } = "";
    public string ContextId { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public double Confidence { get; set; }
    public string Reasons { get; set; } = "[]";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public AiStatus Status { get; set; }
}

public class AiFeedback : Entity
{
    public Guid AiSuggestionId { get; set; }
    public string UseCaseCode { get; set; } = "";
    public string ContextId { get; set; } = "";
    public Guid ActorEmployeeId { get; set; }
    public AiAction Action { get; set; }
    public string? Reason { get; set; }
    public DateTime ActedAtUtc { get; set; } = DateTime.UtcNow;
}
