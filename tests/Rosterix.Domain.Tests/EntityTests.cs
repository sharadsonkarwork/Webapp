using FluentAssertions;
using Rosterix.Domain;
using Xunit;

namespace Rosterix.Domain.Tests;

public class EntityTests
{
    [Fact] public void Every_entity_gets_a_unique_id_and_a_utc_creation_stamp()
    {
        var a = new Employee(); var b = new Employee();
        a.Id.Should().NotBe(b.Id); a.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc); a.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        new RosterChangeLog().ChangedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }
    [Fact] public void Audit_and_notification_rows_default_to_safe_values()
    {
        var audit = new AuditLog(); audit.OccurredAtUtc.Kind.Should().Be(DateTimeKind.Utc); audit.Detail.Should().Be("{}"); audit.Succeeded.Should().BeFalse();
        var n = new Notification(); n.ReadAtUtc.Should().BeNull(); n.Channel.Should().Be("InApp");
        var ai = new AiSuggestionRecord(); ai.Status.Should().Be(AiStatus.Suggested); ai.Payload.Should().Be("{}");
        new EmployeeQuery().Status.Should().Be("Open");
        new RosterEntry().IsAiSuggested.Should().BeFalse();
    }
    [Fact] public void Delegation_is_active_only_inside_its_window_and_until_revoked()
    {
        var d = new Delegation { ValidFrom = new DateOnly(2026, 10, 1), ValidTo = new DateOnly(2026, 10, 10) };
        d.IsActiveOn(new DateOnly(2026, 10, 1)).Should().BeTrue(); d.IsActiveOn(new DateOnly(2026, 10, 10)).Should().BeTrue();
        d.IsActiveOn(new DateOnly(2026, 9, 30)).Should().BeFalse(); d.IsActiveOn(new DateOnly(2026, 10, 11)).Should().BeFalse();
        d.IsRevoked = true; d.IsActiveOn(new DateOnly(2026, 10, 5)).Should().BeFalse();
    }
    [Fact] public void Tag_coverage_is_inclusive_and_open_ended_when_no_end_date()
    {
        var t = new EmployeeTag { ValidFrom = new DateOnly(2026, 3, 1) };
        t.Covers(new DateOnly(2026, 2, 28)).Should().BeFalse(); t.Covers(new DateOnly(2026, 3, 1)).Should().BeTrue(); t.Covers(new DateOnly(2030, 1, 1)).Should().BeTrue();
        t.ValidTo = new DateOnly(2026, 3, 31); t.Covers(new DateOnly(2026, 3, 31)).Should().BeTrue(); t.Covers(new DateOnly(2026, 4, 1)).Should().BeFalse();
    }
    [Fact] public void Project_parses_its_shift_type_list_and_employee_has_a_full_name()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        new Project { ShiftTypeIdsCsv = $"{a},{b}" }.ShiftTypeIds.Should().Equal(a, b); new Project().ShiftTypeIds.Should().BeEmpty();
        new Employee { FirstName = "Priya", LastName = "Nair" }.FullName.Should().Be("Priya Nair");
    }
    [Fact] public void Shift_type_duration_comes_from_the_value_object()
        => new ShiftType { StartTime = new(22, 0), EndTime = new(6, 0), SpansMidnight = true }.DurationHours.Should().Be(8);
    [Fact] public void Roles_are_flags_so_one_person_can_hold_several()
    {
        var r = Role.PM | Role.LM | Role.EMP; r.HasFlag(Role.LM).Should().BeTrue(); r.HasFlag(Role.HRSS).Should().BeFalse();
    }
}
