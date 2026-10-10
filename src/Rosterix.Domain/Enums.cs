namespace Rosterix.Domain;

[Flags]
public enum Role { None = 0, PM = 1, LM = 2, HRBP = 4, RH = 8, HRSS = 16, EMP = 32 }

public enum ProjectStatus { Draft, PendingApproval, Active, Suspended, Closed }
public enum TagType { Primary, Secondary }
public enum TagStatus { Draft, PendingApproval, Approved, Rejected, Withdrawn, Backtagged }
public enum ShiftTagStatus { PendingApproval, Approved, Rejected }
public enum EntryType { Shift, WeeklyOff, Leave, Holiday }
public enum RosterStatus { Draft, PendingPublish, Published, Locked }
public enum RosterSource { Manual, Upload, AiDraft }
public enum DelegationScope { ProjectApproval, EmployeeTaggingApproval, ExceptionReview }
public enum RuleType { MaxConsecutiveDays, MinRest, MaxWeeklyHours, MinWeeklyOffs, Other }
public enum RuleScope { Global, BusinessUnit, Project }
public enum RuleSeverity { Block, Warn }
public enum ApprovalType { ProjectShiftTagging, EmployeeTagging, AllowanceCompensation, Arrear, Movement, Exception }
public enum ApprovalStatus { Pending, Approved, Rejected, Withdrawn }
public enum StepDecision { Pending, Approved, Rejected }
public enum RunStatus { Calculated, PendingCompensation, CompensationApproved, Submitted }
public enum LineStatus { Calculated, Adjusted, Flagged, Approved }
public enum ExceptionCategory { RuleViolation, DataGap, Anomaly, Duplicate, RateMismatch }
public enum ExceptionSeverity { High, Medium, Low }
public enum ExceptionStatus { Open, Acknowledged, Resolved }
public enum ArrearStatus { Raised, PendingApproval, Approved, Rejected, Paid }
public enum BatchStatus { Submitted, Acknowledged, Failed }
public enum DeclarationStatus { Due, Signed }
public enum ClearanceStatus { Open, InProgress, Cleared }
public enum MovementStatus { Staged, Committed, Cancelled }
public enum NotificationSeverity { Info, Success, Warning, Critical }
public enum AiStatus { Suggested, Accepted, Modified, Dismissed }
public enum AiAction { Accept, Modify, Dismiss }
