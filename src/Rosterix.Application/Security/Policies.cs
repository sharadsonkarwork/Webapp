using Rosterix.Domain;
using Rosterix.Domain.Rules;

namespace Rosterix.Application.Security;

/// <summary>Single source of truth: policy name → roles that satisfy it.</summary>
public static class Policies
{
    public const string CanApproveCompensation = nameof(CanApproveCompensation);
    public const string CanApproveProjectTagging = nameof(CanApproveProjectTagging);
    public const string CanApproveEmployeeTagging = nameof(CanApproveEmployeeTagging);
    public const string CanManageFoundationData = nameof(CanManageFoundationData);
    public const string CanSubmitPayroll = nameof(CanSubmitPayroll);
    public const string CanDelegate = nameof(CanDelegate);
    public const string CanBuildRoster = nameof(CanBuildRoster);
    public const string CanManageProjects = nameof(CanManageProjects);
    public const string IsHrss = nameof(IsHrss);
    public const string CanDecideApprovals = nameof(CanDecideApprovals);
    public const string CanReviewExceptions = nameof(CanReviewExceptions);

    public static readonly IReadOnlyDictionary<string, Role> Map = new Dictionary<string, Role>
    {
        [CanApproveCompensation] = Role.HRSS,
        [CanApproveProjectTagging] = Role.LM | Role.HRBP | Role.RH,
        [CanApproveEmployeeTagging] = Role.LM | Role.HRBP,
        [CanManageFoundationData] = Role.HRSS,
        [CanSubmitPayroll] = Role.HRSS,
        [CanDelegate] = Role.PM | Role.LM,
        [CanBuildRoster] = Role.PM | Role.LM | Role.HRSS,
        [CanManageProjects] = Role.PM | Role.LM,
        [IsHrss] = Role.HRSS,
        [CanDecideApprovals] = Role.LM | Role.HRBP | Role.RH | Role.HRSS,
        [CanReviewExceptions] = Role.PM | Role.LM | Role.HRBP | Role.HRSS,
    };

    public static bool Satisfies(string policy, Role roles) => (Map[policy] & roles) != 0;

    public static IEnumerable<Role> Split(Role r) => Enum.GetValues<Role>().Where(x => x != Role.None && r.HasFlag(x));
}

public sealed record DelegationCheck(bool Allowed, IReadOnlyList<RuleResult> Violations);

/// <summary>SEC-03 — wraps BR-11 so API and tests share one definition of "same-level peer".</summary>
public static class DelegationValidator
{
    private static readonly Br11DelegationPeer Rule = new();

    public static bool IsSubordinate(Data.Dataset ds, Guid fromId, Guid toId)
    {
        // walk up the manager chain from the target; if we meet 'from', the target is a subordinate
        var cur = ds.EmpOrNull(toId); var guard = 0;
        while (cur?.ManagerId != null && guard++ < 20)
        {
            if (cur.ManagerId == fromId) return true;
            cur = ds.EmpOrNull(cur.ManagerId);
        }
        return false;
    }

    public static DelegationCheck Validate(Data.Dataset ds, Guid fromId, Guid toId)
    {
        var from = ds.Emp(fromId); var to = ds.Emp(toId);
        var ctx = new RuleContext
        {
            EmployeeId = fromId, EmployeeName = from.FullName,
            Delegation = new DelegationInfo(from.Roles, to.Roles, from.Grade, to.Grade, IsSubordinate(ds, fromId, toId))
        };
        var v = fromId == toId
            ? new[] { new RuleResult("BR-11", RuleSeverity.Block, "You can't delegate to yourself.", new[] { "delegation" }) }
            : Rule.Evaluate(ctx).ToArray();
        return new DelegationCheck(v.Length == 0, v);
    }
}
