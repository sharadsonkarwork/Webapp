using Microsoft.EntityFrameworkCore;
using Rosterix.Domain;

namespace Rosterix.Infrastructure.Persistence;

public class RosterixDbContext : DbContext
{
    public RosterixDbContext(DbContextOptions<RosterixDbContext> options) : base(options) { }

    public DbSet<Region> Regions => Set<Region>();
    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeePreference> Preferences => Set<EmployeePreference>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<LeaveRecord> Leaves => Set<LeaveRecord>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectShiftTag> ProjectShiftTags => Set<ProjectShiftTag>();
    public DbSet<EmployeeTag> EmployeeTags => Set<EmployeeTag>();
    public DbSet<Movement> Movements => Set<Movement>();
    public DbSet<ShiftType> ShiftTypes => Set<ShiftType>();
    public DbSet<ShiftRule> ShiftRules => Set<ShiftRule>();
    public DbSet<AllowanceRate> Rates => Set<AllowanceRate>();
    public DbSet<HolidayCalendar> Holidays => Set<HolidayCalendar>();
    public DbSet<Roster> Rosters => Set<Roster>();
    public DbSet<RosterEntry> RosterEntries => Set<RosterEntry>();
    public DbSet<RosterChangeLog> RosterChangeLogs => Set<RosterChangeLog>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<ApprovalChainDefinition> Chains => Set<ApprovalChainDefinition>();
    public DbSet<AllowanceRun> Runs => Set<AllowanceRun>();
    public DbSet<AllowanceLine> Lines => Set<AllowanceLine>();
    public DbSet<ExceptionItem> Exceptions => Set<ExceptionItem>();
    public DbSet<Arrear> Arrears => Set<Arrear>();
    public DbSet<PayrollBatch> Batches => Set<PayrollBatch>();
    public DbSet<Declaration> Declarations => Set<Declaration>();
    public DbSet<EmployeeQuery> Queries => Set<EmployeeQuery>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AiSuggestionRecord> AiSuggestions => Set<AiSuggestionRecord>();
    public DbSet<AiFeedback> AiFeedback => Set<AiFeedback>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(48);
        b.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        var relational = Database.IsRelational();
        foreach (var et in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            if (relational) b.Entity(et.ClrType).Property<byte[]?>(nameof(Entity.RowVersion)).IsRowVersion();
            else b.Entity(et.ClrType).Ignore(nameof(Entity.RowVersion));

        b.Entity<Employee>().Ignore(e => e.FullName);
        b.Entity<Employee>().Property(e => e.Roles).HasConversion<int>();
        b.Entity<Employee>().HasIndex(e => e.EmployeeNumber).IsUnique();
        b.Entity<Employee>().HasIndex(e => e.Email);
        b.Entity<Project>().Ignore(p => p.ShiftTypeIds);
        b.Entity<Project>().HasIndex(p => p.Code).IsUnique();
        b.Entity<ShiftType>().Ignore(s => s.DurationHours);
        b.Entity<ShiftType>().HasIndex(s => s.Code).IsUnique();
        b.Entity<ApprovalChainDefinition>().Property(c => c.RequiredRole).HasConversion<int>();
        b.Entity<ApprovalStep>().Property(c => c.RequiredRole).HasConversion<int>();
        b.Entity<EmployeeTag>().HasIndex(t => new { t.EmployeeId, t.ProjectId });
        b.Entity<EmployeePreference>().Property(p => p.PreferredOffDay).HasConversion<string>();
        b.Entity<RosterEntry>().HasIndex(e => new { e.RosterId, e.EmployeeId, e.WorkDate }).IsUnique();
        b.Entity<RosterEntry>().HasIndex(e => new { e.EmployeeId, e.WorkDate });
        b.Entity<Roster>().HasIndex(r => new { r.ProjectId, r.PeriodMonth }).IsUnique();
        b.Entity<AllowanceLine>().HasIndex(l => new { l.AllowanceRunId, l.EmployeeId });
        b.Entity<ApprovalStep>().HasIndex(s => new { s.ApprovalRequestId, s.StepIndex });
        b.Entity<AuditLog>().HasIndex(a => a.OccurredAtUtc);
        b.Entity<Notification>().HasIndex(n => n.RecipientEmployeeId);

        // Referential integrity for the relationships that matter most (restrict: nothing in an audit trail is ever cascaded away)
        void Fk<TDep, TPrincipal>(string fk) where TDep : class where TPrincipal : class
            => b.Entity<TDep>().HasOne<TPrincipal>().WithMany().HasForeignKey(fk).OnDelete(DeleteBehavior.Restrict);
        Fk<BusinessUnit, Region>("RegionId");
        Fk<Employee, BusinessUnit>("BusinessUnitId");
        Fk<Project, BusinessUnit>("BusinessUnitId");
        Fk<Project, Employee>("OwnerPmId");
        Fk<EmployeeTag, Employee>("EmployeeId");
        Fk<EmployeeTag, Project>("ProjectId");
        Fk<Roster, Project>("ProjectId");
        Fk<RosterEntry, Roster>("RosterId");
        Fk<RosterEntry, Employee>("EmployeeId");
        Fk<ApprovalStep, ApprovalRequest>("ApprovalRequestId");
        Fk<AllowanceRun, Project>("ProjectId");
        Fk<AllowanceLine, AllowanceRun>("AllowanceRunId");
        Fk<AllowanceLine, Employee>("EmployeeId");
        Fk<AllowanceRate, ShiftType>("ShiftTypeId");
        Fk<Arrear, Employee>("EmployeeId");
        Fk<Settlement, Employee>("EmployeeId");
        Fk<AiFeedback, AiSuggestionRecord>("AiSuggestionId");
    }
}
