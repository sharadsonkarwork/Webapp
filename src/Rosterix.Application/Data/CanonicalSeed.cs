using Rosterix.Application.Allowance;
using Rosterix.Domain;
using Rosterix.Domain.Rules;
using static Rosterix.Application.Data.Personas;

namespace Rosterix.Application.Data;

/// <summary>
/// Deterministic seed (Random(42), hash-derived GUIDs, fixed 'today'). Built once; the DbSeeder persists it
/// and the AI services + tests read the very same snapshot.
/// </summary>
public static class CanonicalSeed
{
    static readonly string[] First = { "Aarav","Vivaan","Aditya","Sai","Arnav","Ishaan","Kabir","Rohan","Karan","Dev","Neel","Yash","Om","Tanmay","Harsh","Ananya","Diya","Isha","Kavya","Meena","Nisha","Pooja","Riya","Sneha","Tanvi","Uma","Divya","Aisha","Bhavna","Charu","Gauri","Hema","Indira","Jaya","Lata","Mala","Nandini","Padma","Rekha","Sita","Anand","Balu","Chetan","Dinesh","Farhan","Girish","Hari","Imran","Jatin","Kiran" };
    static readonly string[] Last = { "Sharma","Verma","Iyer","Nair","Pillai","Reddy","Rao","Naidu","Gupta","Singh","Kapoor","Mehta","Joshi","Patel","Shah","Khan","Das","Bose","Menon","Kulkarni","Desai","Bhat","Hegde","Shetty","Chopra","Malhotra","Sinha","Mishra","Tiwari","Yadav" };

    public static Dataset Build()
    {
        var rng = new Random(42);
        var ds = new Dataset { Today = SeedClock.Today };
        var today = ds.Today;
        var created = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        T Stamp<T>(T e) where T : Entity { e.CreatedAtUtc = created; e.CreatedBy = "seed"; return e; }

        // ---- Regions / BUs
        var south = Stamp(new Region { Id = Gid("region:south"), Code = "SOUTH", Name = "South" });
        var north = Stamp(new Region { Id = Gid("region:north"), Code = "NORTH", Name = "North" });
        ds.Regions.AddRange(new[] { south, north });
        BusinessUnit Bu(string code, string name, Region r) => Stamp(new BusinessUnit { Id = Gid("bu:" + code), Code = code, Name = name, RegionId = r.Id });
        var ret = Bu("RET", "Retail Ops", south); var log = Bu("LOG", "Logistics", south); var ccr = Bu("CCR", "Customer Care", north); var mfg = Bu("MFG", "Manufacturing", north);
        ds.BusinessUnits.AddRange(new[] { ret, log, ccr, mfg });
        var regionOfBu = ds.BusinessUnits.ToDictionary(b => b.Id, b => b.RegionId);

        // ---- Shift types (12)
        ShiftType St(string code, string name, string s, string e, bool eligible)
        {
            var start = TimeOnly.Parse(s); var end = TimeOnly.Parse(e);
            return Stamp(new ShiftType { Id = Gid("st:" + code), Code = code, Name = name, StartTime = start, EndTime = end, SpansMidnight = end <= start, IsAllowanceEligible = eligible });
        }
        ds.ShiftTypes.AddRange(new[]
        {
            St("MORN","Morning","06:00","14:00",false), St("GEN","General","09:00","17:00",false), St("EVE","Evening","14:00","22:00",true),
            St("NIGHT_A","Night A","22:00","06:00",true), St("NIGHT_B","Night B","21:00","05:00",true), St("EARLY","Early","04:00","12:00",true),
            St("LATE","Late","16:00","00:00",true), St("WKND","Weekend cover","08:00","20:00",true), St("HOL_COVER","Holiday cover","08:00","16:00",true),
            St("ON_CALL","On call","18:00","06:00",true), St("TRAIN","Training","10:00","16:00",false), St("HALF","Half day","09:00","13:00",false),
        });
        Guid S(string c) => Gid("st:" + c);

        // ---- Rules (12 defaults, editable parameters)
        ShiftRule Rule(string code, string name, RuleType t, string pars, RuleSeverity sev) => Stamp(new ShiftRule
        { Id = Gid("rule:" + code), Code = code, Name = name, RuleType = t, Scope = RuleScope.Global, Parameters = pars, Severity = sev, EffectiveFrom = new DateOnly(2026, 1, 1) });
        ds.ShiftRules.AddRange(new[]
        {
            Rule("BR-01","No overlapping tags",RuleType.Other,"{}",RuleSeverity.Block), Rule("BR-02","Max consecutive working days",RuleType.MaxConsecutiveDays,"{\"max\":6}",RuleSeverity.Block),
            Rule("BR-03","Minimum rest between shifts",RuleType.MinRest,"{\"hours\":11}",RuleSeverity.Block), Rule("BR-04","Roster entry needs approved tag",RuleType.Other,"{}",RuleSeverity.Block),
            Rule("BR-05","Dates within project range",RuleType.Other,"{}",RuleSeverity.Block), Rule("BR-06","No shift on leave or restricted holiday",RuleType.Other,"{}",RuleSeverity.Warn),
            Rule("BR-07","Max weekly hours",RuleType.MaxWeeklyHours,"{\"hours\":48}",RuleSeverity.Warn), Rule("BR-08","Allowance only for eligible shifts",RuleType.Other,"{}",RuleSeverity.Block),
            Rule("BR-09","Minimum weekly offs",RuleType.MinWeeklyOffs,"{\"min\":1}",RuleSeverity.Block), Rule("BR-10","Effective allowance rate must exist",RuleType.Other,"{}",RuleSeverity.Block),
            Rule("BR-11","Delegation to same-level peer",RuleType.Other,"{}",RuleSeverity.Block), Rule("BR-12","Backdated change may not orphan tags",RuleType.Other,"{}",RuleSeverity.Block),
        });

        // ---- Rates (~21, with the mid-period NIGHT_A change for Retail Ops)
        var rateFrom = new DateOnly(2026, 1, 1);
        int rn = 0;
        AllowanceRate Rate(string st, BusinessUnit? bu, string? grade, decimal amt, DateOnly from, DateOnly? to = null) => Stamp(new AllowanceRate
        { Id = Gid("rate:" + (rn++)), ShiftTypeId = S(st), BusinessUnitId = bu?.Id, Grade = grade, Amount = amt, EffectiveFrom = from, EffectiveTo = to });
        ds.Rates.AddRange(new[]
        {
            Rate("EVE",null,null,150,rateFrom), Rate("NIGHT_A",null,null,400,rateFrom), Rate("NIGHT_B",null,null,380,rateFrom), Rate("EARLY",null,null,120,rateFrom),
            Rate("LATE",null,null,140,rateFrom), Rate("WKND",null,null,600,rateFrom), Rate("HOL_COVER",null,null,700,rateFrom), Rate("ON_CALL",null,null,500,rateFrom),
            Rate("NIGHT_A",ret,null,450,rateFrom,new DateOnly(2026,7,31)), Rate("NIGHT_A",ret,null,525,new DateOnly(2026,8,1)),
            Rate("EVE",log,null,175,rateFrom), Rate("NIGHT_B",log,null,420,rateFrom), Rate("NIGHT_A",null,"G2",380,rateFrom), Rate("EVE",null,"G4",170,rateFrom),
            Rate("LATE",ccr,null,160,rateFrom), Rate("EARLY",mfg,null,130,rateFrom), Rate("WKND",null,"G4",650,rateFrom), Rate("ON_CALL",ccr,null,520,rateFrom),
            Rate("NIGHT_A",ccr,null,410,rateFrom), Rate("NIGHT_A",mfg,null,405,rateFrom), Rate("HOL_COVER",ret,null,750,rateFrom),
        });

        // ---- Holidays 2026 (both regions); restricted ones carry Holiday roster entries
        (int m, int d, string n, bool r)[] hol = { (1,26,"Republic Day",false),(3,20,"Ugadi",false),(4,3,"Good Friday",false),(5,1,"May Day",false),(6,26,"Muharram",false),(8,15,"Independence Day",true),(9,14,"Ganesh Chaturthi",false),(10,2,"Gandhi Jayanti",true),(10,20,"Dussehra",true),(11,8,"Diwali",true),(12,25,"Christmas",false) };
        foreach (var reg in ds.Regions) foreach (var h in hol)
            ds.Holidays.Add(Stamp(new HolidayCalendar { Id = Gid($"hol:{reg.Code}:{h.m}:{h.d}"), RegionId = reg.Id, Date = new DateOnly(2026, h.m, h.d), Name = h.n, IsRestricted = h.r }));
        var restrictedDates = hol.Where(h => h.r).Select(h => new DateOnly(2026, h.m, h.d)).ToHashSet();

        // ---- Employees
        int empNo = 1000;
        Employee Emp(Guid id, string fn, string ln, Role roles, string grade, BusinessUnit bu, Guid? mgr, bool shift = true)
        {
            empNo++;
            return Stamp(new Employee { Id = id, EmployeeNumber = "E" + empNo, FirstName = fn, LastName = ln, Email = $"{fn}.{ln}@rosterix.example".ToLowerInvariant(), Grade = grade,
                BusinessUnitId = bu.Id, ManagerId = mgr, Roles = roles | Role.EMP, IsShiftEligible = shift, JoinedOn = new DateOnly(2019 + rng.Next(0, 6), 1 + rng.Next(0, 12), 1 + rng.Next(0, 27)) });
        }
        var karthik = Emp(Karthik, "Karthik", "Subramaniam", Role.RH, "G7", ret, null, false);
        var vikram = Emp(Gid("emp:vikram"), "Vikram", "Singh", Role.RH, "G7", ccr, null, false);
        var arjun = Emp(Arjun, "Arjun", "Mehta", Role.LM, "G5", ret, Karthik, false);
        var nikhil = Emp(Gid("emp:nikhil"), "Nikhil", "Rao", Role.LM, "G5", ret, Karthik, false);
        var sunita = Emp(Gid("emp:sunita"), "Sunita", "Joshi", Role.LM, "G4", ret, Karthik, false);
        var rohit = Emp(Gid("emp:rohit"), "Rohit", "Bansal", Role.LM, "G5", log, Karthik, false);
        var arvind = Emp(Gid("emp:arvind"), "Arvind", "Kapoor", Role.LM, "G5", ccr, vikram.Id, false);
        var tara = Emp(Gid("emp:tara"), "Tara", "Bhatt", Role.LM, "G5", mfg, vikram.Id, false);
        var meera = Emp(Meera, "Meera", "Iyer", Role.HRBP, "G5", ret, Karthik, false);
        var pooja = Emp(Gid("emp:pooja"), "Pooja", "Sharma", Role.HRBP, "G5", ccr, vikram.Id, false);
        var fathima = Emp(Fathima, "Fathima", "Rasheed", Role.HRSS, "G5", ret, Karthik, false);
        var rahul = Emp(Gid("emp:rahul"), "Rahul", "Verma", Role.HRSS, "G4", ret, Karthik, false);
        var priya = Emp(Priya, "Priya", "Nair", Role.PM, "G4", ret, Arjun, false);
        var anil = Emp(Gid("emp:anil"), "Anil", "Desai", Role.PM, "G4", log, rohit.Id, false);
        var lakshmi = Emp(Gid("emp:lakshmi"), "Lakshmi", "Pillai", Role.PM, "G4", ccr, arvind.Id, false);
        var suresh = Emp(Gid("emp:suresh"), "Suresh", "Reddy", Role.PM, "G4", mfg, tara.Id, false);
        var deepa = Emp(Gid("emp:deepa"), "Deepa", "Menon", Role.PM, "G3", ret, Priya, false);
        ds.Employees.AddRange(new[] { karthik, vikram, arjun, nikhil, sunita, rohit, arvind, tara, meera, pooja, fathima, rahul, priya, anil, lakshmi, suresh, deepa });

        var assoc = new Dictionary<string, List<Employee>> { ["RET"] = new(), ["LOG"] = new(), ["CCR"] = new(), ["MFG"] = new() };
        var sizes = new (BusinessUnit bu, int n, Guid mgr)[] { (ret, 60, Priya), (log, 20, anil.Id), (ccr, 14, lakshmi.Id), (mfg, 9, suresh.Id) };
        var usedNames = new HashSet<string>();
        foreach (var (bu, n, mgr) in sizes)
            for (var i = 0; i < n; i++)
            {
                Employee e;
                if (bu == ret && i == 0) e = Emp(Ravi, "Ravi", "Kumar", Role.None, "G3", ret, Priya);
                else if (bu == ret && i == 1) e = Emp(Gid("emp:sanjay"), "Sanjay", "Pillai", Role.None, "G3", ret, Priya);
                else
                {
                    string fn, ln, key; var tries = 0;
                    do { fn = First[rng.Next(First.Length)]; ln = Last[rng.Next(Last.Length)]; key = fn + ln; } while (!usedNames.Add(key) && ++tries < 100);
                    var gr = rng.NextDouble() < 0.3 ? "G2" : rng.NextDouble() < 0.7 ? "G3" : "G4";
                    e = Emp(Gid($"emp:{bu.Code}:{i}"), fn, ln, Role.None, gr, bu, (bu == ret && i >= 52) ? deepa.Id : mgr, shift: i % 11 != 10);
                }
                assoc[bu.Code].Add(e); ds.Employees.Add(e);
            }
        ds.ResetIndexes();
        var sanjay = ds.Emp(Gid("emp:sanjay")); sanjay.ExitedOn = new DateOnly(2026, 10, 31);

        // ---- Projects (10)
        var shiftMix = new Dictionary<string, string> { ["RET-01"] = "MORN,GEN,EVE,NIGHT_A", ["RET-02"] = "EARLY,LATE,GEN", ["LOG-01"] = "MORN,GEN,EVE,NIGHT_B" };
        string Csv(params string[] c) => string.Join(',', c.Select(x => S(x)));
        Project Proj(string code, string name, BusinessUnit bu, Employee pm, ProjectStatus st, bool shift, string cc, DateOnly start, DateOnly? end, string shifts) => Stamp(new Project
        { Id = Gid("proj:" + code), Code = "P-" + code, Name = name, BusinessUnitId = bu.Id, OwnerPmId = pm.Id, Status = st, IsShiftProject = shift, CostCentre = cc, StartDate = start, EndDate = end,
          ShiftTypeIdsCsv = string.Join(',', shifts.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => S(x.Trim()))) });
        var d = (int y, int m, int dd) => new DateOnly(y, m, dd);
        var pRet1 = Proj("RET-01", "Store operations South", ret, priya, ProjectStatus.Active, true, "CC-4101", d(2026,3,1), null, shiftMix["RET-01"]);
        var pRet2 = Proj("RET-02", "Festive fulfilment", ret, priya, ProjectStatus.Active, true, "CC-4102", d(2026,3,1), d(2026,12,31), shiftMix["RET-02"]);
        var pRet3 = Proj("RET-03", "Click-and-collect pilot", ret, priya, ProjectStatus.PendingApproval, true, "CC-4103", d(2026,11,1), d(2027,4,30), "MORN,EVE");
        var pRet4 = Proj("RET-04", "Warehouse returns desk", ret, priya, ProjectStatus.Draft, false, "CC-4104", d(2026,12,1), null, "GEN");
        var pRet5 = Proj("RET-05", "Summer sale 2026", ret, priya, ProjectStatus.Closed, true, "CC-4105", d(2026,1,1), d(2026,3,31), "MORN,EVE");
        var pLog1 = Proj("LOG-01", "Hub sorting Chennai", log, anil, ProjectStatus.Active, true, "CC-5101", d(2026,3,1), null, shiftMix["LOG-01"]);
        var pLog2 = Proj("LOG-02", "Last-mile dispatch", log, anil, ProjectStatus.Active, true, "CC-5102", d(2026,5,1), null, "MORN,EVE");
        var pCcr1 = Proj("CCR-01", "Support desk 24x7", ccr, lakshmi, ProjectStatus.Active, true, "CC-6101", d(2026,2,1), null, "LATE,NIGHT_A,GEN");
        var pCcr2 = Proj("CCR-02", "Escalations cell", ccr, lakshmi, ProjectStatus.Active, true, "CC-6102", d(2026,4,1), null, "MORN,ON_CALL");
        var pMfg1 = Proj("MFG-01", "Line 3 production", mfg, suresh, ProjectStatus.Active, true, "CC-7101", d(2026,1,15), null, "EARLY,EVE,NIGHT_A");
        var projects = new[] { pRet1, pRet2, pRet3, pRet4, pRet5, pLog1, pLog2, pCcr1, pCcr2, pMfg1 };
        ds.Projects.AddRange(projects); ds.ResetIndexes();
        foreach (var p in projects.Where(p => p.IsShiftProject))
            ds.ProjectShiftTags.Add(Stamp(new ProjectShiftTag { Id = Gid("pst:" + p.Code), ProjectId = p.Id, ValidFrom = p.StartDate, ValidTo = p.EndDate, ShiftTypeIdsCsv = p.ShiftTypeIdsCsv,
                Status = p.Status == ProjectStatus.PendingApproval ? ShiftTagStatus.PendingApproval : ShiftTagStatus.Approved }));

        // ---- Employee tags
        var tagged = new Dictionary<Guid, List<Employee>>();
        void Tag(Project p, IEnumerable<Employee> emps, DateOnly from, DateOnly? to, TagStatus st, string key, TagType tt = TagType.Primary)
        {
            foreach (var e in emps)
                ds.EmployeeTags.Add(Stamp(new EmployeeTag { Id = Gid($"tag:{p.Code}:{e.Id}:{key}"), EmployeeId = e.Id, ProjectId = p.Id, ValidFrom = from, ValidTo = to, TagType = tt, Status = st }));
        }
        var r = assoc["RET"];
        var closedTeam = r.Take(12).ToList();
        Tag(pRet5, closedTeam, d(2026,1,1), d(2026,3,31), TagStatus.Approved, "a");
        var ret1 = r.Where((e, i) => i < 32 && e.IsShiftEligible).ToList();
        foreach (var e in ret1) Tag(pRet1, new[] { e }, r.IndexOf(e) < 12 ? d(2026,4,1) : d(2026,3,1), null, TagStatus.Approved, "a");
        var ret2 = r.Where((e, i) => i >= 32 && i < 52 && e.IsShiftEligible).ToList();
        Tag(pRet2, ret2, d(2026,3,1), null, TagStatus.Approved, "a");
        var ret3 = r.Where((e, i) => i >= 52).Take(8).ToList();
        Tag(pRet3, ret3, d(2026,11,1), null, TagStatus.PendingApproval, "a");
        Tag(pRet2, r.Where((e, i) => i >= 52 && i < 55).Take(2), d(2026,10,1), null, TagStatus.Rejected, "r");
        Tag(pRet2, r.Where((e, i) => i == 55), d(2026,10,1), null, TagStatus.Withdrawn, "w");
        Tag(pRet4, r.Where((e, i) => i >= 40 && i < 46), d(2026,12,1), null, TagStatus.Draft, "d");
        var logTeam = assoc["LOG"].Where(e => e.IsShiftEligible).ToList();
        var log1 = logTeam.Take(16).ToList();
        Tag(pLog1, log1, d(2026,3,1), null, TagStatus.Approved, "a");
        Tag(pLog2, logTeam.Skip(16), d(2026,5,1), null, TagStatus.Approved, "a");
        Tag(pCcr1, assoc["CCR"].Where(e => e.IsShiftEligible).Take(8), d(2026,2,1), null, TagStatus.Approved, "a");
        Tag(pCcr2, assoc["CCR"].Where(e => e.IsShiftEligible).Skip(8), d(2026,4,1), null, TagStatus.Approved, "a");
        Tag(pMfg1, assoc["MFG"].Where(e => e.IsShiftEligible), d(2026,1,15), null, TagStatus.Approved, "a");

        // ---- Leaves
        foreach (var e in ret1.Concat(ret2).Concat(log1))
            for (var m = 6; m <= 11; m++)
                if (rng.NextDouble() < 0.35)
                {
                    var day = rng.Next(3, 26); var len = rng.Next(1, 3);
                    for (var k = 0; k < len; k++) ds.Leaves.Add(Stamp(new LeaveRecord { Id = Gid($"leave:{e.Id}:{m}:{day + k}"), EmployeeId = e.Id, Date = new DateOnly(2026, m, day + k) }));
                }
        // planted: shift on an approved leave day in the October draft (BR-06 warning)
        var leaveDaysByEmp = ds.Leaves.GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => g.Select(l => l.Date).ToHashSet());

        // ---- Rosters (Jun–Sep published, Oct draft) for the 3 demo projects
        var weekdayOff = new[] { DayOfWeek.Sunday, DayOfWeek.Sunday, DayOfWeek.Saturday, DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Tuesday };
        string[] shiftsOf(Project p) => p.ShiftTypeIdsCsv.Split(',').Select(x => ds.ShiftTypes.First(s => s.Id == Guid.Parse(x)).Code).ToArray();
        var alt = new Dictionary<string, string?> { ["MORN"] = "GEN", ["GEN"] = "MORN", ["EVE"] = "GEN", ["NIGHT_A"] = null, ["NIGHT_B"] = null, ["EARLY"] = "GEN", ["LATE"] = null };
        var pattern = new Dictionary<Guid, (string shift, DayOfWeek off)>();
        void BuildRosters(Project p, List<Employee> team)
        {
            var codes = shiftsOf(p);
            foreach (var e in team)
            {
                var pick = codes[rng.Next(codes.Length)];
                pattern[e.Id] = (pick, weekdayOff[rng.Next(weekdayOff.Length)]);
                if (e.Id == Ravi) pattern[e.Id] = ("EVE", DayOfWeek.Sunday);          // matches Ravi's stated preference; guarantees eligible shifts for the self-service demo
                if (e.Id == Gid("emp:sanjay")) pattern[e.Id] = ("NIGHT_A", DayOfWeek.Saturday); // exiting employee with real pending allowance
            }
            foreach (var month in new[] { "2026-06", "2026-07", "2026-08", "2026-09", "2026-10" })
            {
                var published = month != "2026-10";
                var roster = Stamp(new Roster { Id = Gid($"roster:{p.Code}:{month}"), ProjectId = p.Id, PeriodMonth = month, Status = published ? RosterStatus.Published : RosterStatus.Draft,
                    Source = RosterSource.Manual, PublishedAtUtc = published ? ShiftMath.MonthStart(month).AddDays(-6).ToDateTime(new TimeOnly(10, 0)) : null, PublishedBy = published ? p.OwnerPmId.ToString() : null });
                ds.Rosters.Add(roster);
                for (var dt = ShiftMath.MonthStart(month); dt <= ShiftMath.MonthEnd(month); dt = dt.AddDays(1))
                    foreach (var e in team)
                    {
                        var (shift, off) = pattern[e.Id];
                        EntryType et; Guid? sid = null;
                        if (restrictedDates.Contains(dt)) et = EntryType.Holiday;
                        else if (leaveDaysByEmp.TryGetValue(e.Id, out var ls) && ls.Contains(dt)) et = EntryType.Leave;
                        else if (dt.DayOfWeek == off) et = EntryType.WeeklyOff;
                        else
                        {
                            et = EntryType.Shift; var code = shift;
                            if (alt.TryGetValue(shift, out var a) && a != null && rng.NextDouble() < 0.08 && codes.Contains(a)) code = a;
                            sid = S(code);
                        }
                        ds.RosterEntries.Add(new RosterEntry { Id = Gid($"re:{roster.Id}:{e.Id}:{dt}"), RosterId = roster.Id, EmployeeId = e.Id, WorkDate = dt, ShiftTypeId = sid, EntryType = et, CreatedAtUtc = created, CreatedBy = "seed" });
                    }
            }
        }
        BuildRosters(pRet1, ret1); BuildRosters(pRet2, ret2); BuildRosters(pLog1, log1);

        // Planted: two statistical outliers (non-eligible pattern suddenly on nights from 14 Sep) — AI-UC-09
        var outlierA = ret1.First(e => pattern[e.Id].shift is "MORN" or "GEN" && e.Id != Ravi && e.Id != sanjay.Id);
        var outlierB = ret1.Where(e => pattern[e.Id].shift is "MORN" or "GEN" && e.Id != Ravi && e.Id != sanjay.Id && e.Id != outlierA.Id).Skip(1).First();
        var sepRet1 = ds.FindRoster(pRet1.Id, "2026-09")!;
        var octRet1Id = ds.FindRoster(pRet1.Id, "2026-10")!.Id;
        foreach (var o in new[] { outlierA, outlierB })
            foreach (var en in ds.RosterEntries.Where(x => (x.RosterId == sepRet1.Id || x.RosterId == octRet1Id) && x.EmployeeId == o.Id && x.WorkDate >= d(2026,9,14) && x.EntryType == EntryType.Shift))
                en.ShiftTypeId = S("NIGHT_A");

        // Planted in the October draft: BR-02/BR-09 (7 days straight), BR-03 (11h rest), BR-06 (shift on leave)
        var octRet1 = ds.FindRoster(pRet1.Id, "2026-10")!;
        var victimStreak = ret1.First(e => e.Id != Ravi && e.Id != sanjay.Id && pattern[e.Id].shift == "MORN" && e.Id != outlierA.Id && e.Id != outlierB.Id);
        foreach (var en in ds.RosterEntries.Where(x => x.RosterId == octRet1.Id && x.EmployeeId == victimStreak.Id && x.WorkDate >= d(2026,10,11) && x.WorkDate <= d(2026,10,19)))
        {
            var off = en.WorkDate.Day is 11 or 19;
            if (en.EntryType is EntryType.Leave or EntryType.Holiday) continue;
            en.EntryType = off ? EntryType.WeeklyOff : EntryType.Shift; en.ShiftTypeId = off ? null : S("MORN");
        }
        var victimRest = ret1.First(e => e.Id != Ravi && e.Id != victimStreak.Id && pattern[e.Id].shift == "GEN" && e.Id != outlierA.Id && e.Id != outlierB.Id);
        var e14 = ds.RosterEntries.First(x => x.RosterId == octRet1.Id && x.EmployeeId == victimRest.Id && x.WorkDate == d(2026,10,14));
        var e15 = ds.RosterEntries.First(x => x.RosterId == octRet1.Id && x.EmployeeId == victimRest.Id && x.WorkDate == d(2026,10,15));
        e14.EntryType = EntryType.Shift; e14.ShiftTypeId = S("EVE"); e15.EntryType = EntryType.Shift; e15.ShiftTypeId = S("MORN");
        var victimLeave = ret1.First(e => e.Id != Ravi && e.Id != sanjay.Id && e.Id != victimStreak.Id && e.Id != victimRest.Id && e.Id != outlierA.Id && e.Id != outlierB.Id);
        ds.Leaves.Add(Stamp(new LeaveRecord { Id = Gid("leave:planted"), EmployeeId = victimLeave.Id, Date = d(2026,10,7) }));
        var e7 = ds.RosterEntries.First(x => x.RosterId == octRet1.Id && x.EmployeeId == victimLeave.Id && x.WorkDate == d(2026,10,7));
        e7.EntryType = EntryType.Shift; e7.ShiftTypeId ??= S("GEN");

        // ---- Preferences (editable overrides)
        ds.Preferences.Add(Stamp(new EmployeePreference { Id = Gid("pref:ravi"), EmployeeId = Ravi, PreferredShiftTypeId = S("EVE"), PreferredOffDay = DayOfWeek.Sunday, Note = "Evening shifts suit my commute." }));

        // ---- Approval chains (read-only, SEC-02)
        int ci = 0;
        void Chain(ApprovalType t, string? cond, params Role[] roles) { for (var i = 0; i < roles.Length; i++) ds.Chains.Add(Stamp(new ApprovalChainDefinition { Id = Gid("chain:" + (ci++)), RequestType = t, StepIndex = i, RequiredRole = roles[i], Condition = cond })); }
        Chain(ApprovalType.ProjectShiftTagging, null, Role.LM, Role.HRBP, Role.RH);
        Chain(ApprovalType.EmployeeTagging, null, Role.LM, Role.HRBP);
        Chain(ApprovalType.AllowanceCompensation, null, Role.HRSS);
        Chain(ApprovalType.Arrear, "0-3 months", Role.HRBP);
        Chain(ApprovalType.Arrear, "4-6 months", Role.RH);
        Chain(ApprovalType.Arrear, ">6 months", Role.RH, Role.HRSS);
        Chain(ApprovalType.Movement, null, Role.HRBP, Role.HRSS);
        Chain(ApprovalType.Exception, null, Role.HRBP);

        // ---- Allowance runs: Jul RET-01 (submitted), Sep RET-01 (pending compensation), Sep RET-02 (approved)
        var calc = new AllowanceCalculator(new RuleEngine());
        AllowanceRun MakeRun(Project p, string month, RunStatus status)
        {
            var res = calc.Calculate(ds, p.Id, month);
            var run = Stamp(new AllowanceRun { Id = Gid($"run:{p.Code}:{month}"), ProjectId = p.Id, PeriodMonth = month, Status = status, CalculatedBy = p.OwnerPmId, TotalAmount = res.Total, LineCount = res.Lines.Count, ExceptionCount = res.Exceptions.Count });
            run.CreatedAtUtc = ShiftMath.MonthEnd(month).AddDays(3).ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc);
            foreach (var l in res.Lines)
                ds.Lines.Add(Stamp(new AllowanceLine { Id = Gid($"line:{run.Id}:{l.EmployeeId}:{l.WorkDate}"), AllowanceRunId = run.Id, EmployeeId = l.EmployeeId, WorkDate = l.WorkDate, ShiftTypeId = l.ShiftTypeId, RateApplied = l.Rate, Amount = l.Amount, CalculationTrace = l.Trace, Status = status is RunStatus.Submitted or RunStatus.CompensationApproved ? LineStatus.Approved : LineStatus.Calculated }));
            ds.Runs.Add(run);
            return run;
        }
        var julRun = MakeRun(pRet1, "2026-07", RunStatus.Submitted);
        var sepRun1 = MakeRun(pRet1, "2026-09", RunStatus.PendingCompensation);
        var sepRun2 = MakeRun(pRet2, "2026-09", RunStatus.CompensationApproved);
        sepRun2.ApprovedBy = Fathima; sepRun2.ApprovedAtUtc = d(2026,10,6).ToDateTime(new TimeOnly(15, 30), DateTimeKind.Utc);

        // Planted: duplicate claim + rate mismatch in the Sep RET-01 run (AI-UC-09)
        var sepLines = ds.Lines.Where(l => l.AllowanceRunId == sepRun1.Id).ToList();
        var dupSrc = sepLines.First(l => l.EmployeeId != Ravi && l.EmployeeId != outlierA.Id && l.EmployeeId != outlierB.Id && l.WorkDate.Day == 9);
        var dup = Stamp(new AllowanceLine { Id = Gid("line:duplicate"), AllowanceRunId = sepRun1.Id, EmployeeId = dupSrc.EmployeeId, WorkDate = dupSrc.WorkDate, ShiftTypeId = dupSrc.ShiftTypeId, RateApplied = dupSrc.RateApplied, Amount = dupSrc.Amount, CalculationTrace = dupSrc.CalculationTrace + " (second claim for the same day)", Status = LineStatus.Flagged });
        ds.Lines.Add(dup);
        var mismatchSrc = sepLines.First(l => l.ShiftTypeId == S("NIGHT_A") && l.EmployeeId != outlierA.Id && l.EmployeeId != outlierB.Id && l.RateApplied == 525 && l.WorkDate.Day == 12);
        mismatchSrc.RateApplied = 450; mismatchSrc.Amount = 450; mismatchSrc.Status = LineStatus.Flagged;
        mismatchSrc.CalculationTrace = "Night A shift keyed with the pre-August rate of ₹450. (Manual override recorded by the roster desk.)";
        sepRun1.LineCount = ds.Lines.Count(l => l.AllowanceRunId == sepRun1.Id); sepRun1.TotalAmount = ds.Lines.Where(l => l.AllowanceRunId == sepRun1.Id).Sum(l => l.Amount);

        // ---- Payroll batch (historic July)
        var batch = Stamp(new PayrollBatch { Id = Gid("batch:2026-07"), PeriodMonth = "2026-07", SubmittedBy = Fathima, SubmittedAtUtc = d(2026,8,6).ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc), RunIdsCsv = julRun.Id.ToString(), TotalAmount = julRun.TotalAmount, LineCount = julRun.LineCount, FileRef = "cnb/2026-07/batch-0001.csv", Status = BatchStatus.Acknowledged });
        ds.Batches.Add(batch); julRun.PayrollBatchId = batch.Id;

        // ---- Exceptions (~25 across categories/severities/statuses)
        var rosterOfOct = octRet1.Id;
        var cats = new[] { ExceptionCategory.RuleViolation, ExceptionCategory.DataGap, ExceptionCategory.Anomaly, ExceptionCategory.Duplicate, ExceptionCategory.RateMismatch };
        var sevs = new[] { ExceptionSeverity.High, ExceptionSeverity.Medium, ExceptionSeverity.Low };
        var excEmps = ret1.Concat(ret2).Concat(log1).ToList();
        string[] ruleCodes = { "BR-02", "BR-03", "BR-06", "BR-07", "BR-09" };
        for (var i = 0; i < 22; i++)
        {
            var cat = cats[i % 5]; var sev = sevs[(i * 7 / 3) % 3]; var e = excEmps[(i * 5) % excEmps.Count];
            var pid = ret1.Contains(e) ? pRet1.Id : ret2.Contains(e) ? pRet2.Id : pLog1.Id;
            string rc = cat == ExceptionCategory.RuleViolation ? ruleCodes[i % ruleCodes.Length] : (cat == ExceptionCategory.DataGap ? "BR-10" : "");
            var desc = cat switch
            {
                ExceptionCategory.RuleViolation => $"{e.FullName}: {rc} breached in the {ShiftMath.Month(d(2026,9,1))} roster. Review the flagged days.",
                ExceptionCategory.DataGap => $"{e.FullName}: allowance claimed without a matching effective rate on one work date.",
                ExceptionCategory.Anomaly => $"{e.FullName}: allowance total is well above their usual month.",
                ExceptionCategory.Duplicate => $"{e.FullName}: two claims recorded for the same work date.",
                _ => $"{e.FullName}: rate applied differs from the rate in effect on the work date."
            };
            var status = i % 6 == 0 ? ExceptionStatus.Resolved : i % 4 == 0 ? ExceptionStatus.Acknowledged : ExceptionStatus.Open;
            ds.Exceptions.Add(Stamp(new ExceptionItem { Id = Gid("exc:" + i), ProjectId = pid, EmployeeId = e.Id, Category = cat, RuleCode = rc == "" ? null : rc, Severity = sev, Description = desc,
                AiConfidence = cat is ExceptionCategory.Anomaly or ExceptionCategory.Duplicate or ExceptionCategory.RateMismatch ? 0.7 + (i % 3) * 0.1 : null, Status = status,
                AssignedTo = i % 3 == 0 ? Meera : i % 3 == 1 ? Fathima : Priya,
                ResolvedBy = status == ExceptionStatus.Resolved ? Meera : null, Resolution = status == ExceptionStatus.Resolved ? "Verified with the roster owner; no change needed." : null }));
        }
        ds.Exceptions.Add(Stamp(new ExceptionItem { Id = Gid("exc:dup"), AllowanceLineId = dup.Id, ProjectId = pRet1.Id, EmployeeId = dup.EmployeeId, Category = ExceptionCategory.Duplicate, Severity = ExceptionSeverity.High, Description = $"{ds.Emp(dup.EmployeeId).FullName}: duplicate claim on {dup.WorkDate:d MMM yyyy}.", AiConfidence = 0.95, Status = ExceptionStatus.Open, AssignedTo = Fathima }));
        ds.Exceptions.Add(Stamp(new ExceptionItem { Id = Gid("exc:mismatch"), AllowanceLineId = mismatchSrc.Id, ProjectId = pRet1.Id, EmployeeId = mismatchSrc.EmployeeId, Category = ExceptionCategory.RateMismatch, Severity = ExceptionSeverity.High, Description = $"{ds.Emp(mismatchSrc.EmployeeId).FullName}: ₹450 applied where ₹525 was in effect on {mismatchSrc.WorkDate:d MMM yyyy}.", AiConfidence = 0.97, Status = ExceptionStatus.Open, AssignedTo = Fathima }));
        ds.Exceptions.Add(Stamp(new ExceptionItem { Id = Gid("exc:oct-streak"), RosterEntryId = null, ProjectId = pRet1.Id, EmployeeId = victimStreak.Id, Category = ExceptionCategory.RuleViolation, RuleCode = "BR-02", Severity = ExceptionSeverity.High, Description = $"{victimStreak.FullName}: 7 consecutive working days in the October draft (12–18 Oct).", Status = ExceptionStatus.Open, AssignedTo = Priya }));

        // ---- Approvals
        Employee Pm(Project p) => ds.Emp(p.OwnerPmId);
        Employee Assignee(Role role, Project p)
        {
            var pm = Pm(p); var region = regionOfBu[pm.BusinessUnitId];
            return role switch
            {
                Role.LM => ds.Emp(pm.ManagerId!.Value),
                Role.HRBP => region == south.Id ? meera : pooja,
                Role.RH => region == south.Id ? karthik : vikram,
                _ => fathima
            };
        }
        var decidedGuid = 0;
        ApprovalRequest Req(string key, ApprovalType t, Project p, Employee raisedBy, string title, string summary, int daysAgo, int done, decimal? value, DateOnly? from, DateOnly? to, Guid? subjEmp, ApprovalStatus status = ApprovalStatus.Pending, Role[]? roles = null, Guid? subject = null, bool rejectLast = false)
        {
            var chain = roles ?? ds.Chains.Where(c => c.RequestType == t && c.Condition == null).OrderBy(c => c.StepIndex).Select(c => c.RequiredRole).ToArray();
            var raised = today.AddDays(-daysAgo).ToDateTime(new TimeOnly(9, 30), DateTimeKind.Utc);
            var req = Stamp(new ApprovalRequest { Id = Gid("apr:" + key), Type = t, SubjectId = subject ?? p.Id, Title = title, Summary = summary, RaisedBy = raisedBy.Id, RaisedAtUtc = raised, Status = status,
                CurrentStepIndex = Math.Min(done, chain.Length - 1), Value = value, FromDate = from, ToDate = to, ProjectId = p.Id, SubjectEmployeeId = subjEmp });
            req.CreatedAtUtc = raised;
            for (var i = 0; i < chain.Length; i++)
            {
                var a = Assignee(chain[i], p);
                var isDone = i < done;
                var dec = isDone ? StepDecision.Approved : StepDecision.Pending;
                if (status == ApprovalStatus.Rejected && i == chain.Length - 1 - (chain.Length - 1 - Math.Min(done, chain.Length - 1))) dec = StepDecision.Rejected;
                if (status == ApprovalStatus.Approved) dec = StepDecision.Approved;
                ds.ApprovalSteps.Add(Stamp(new ApprovalStep { Id = Gid($"aps:{key}:{i}"), ApprovalRequestId = req.Id, StepIndex = i, RequiredRole = chain[i], AssignedToEmployeeId = a.Id,
                    ActedByEmployeeId = dec != StepDecision.Pending ? a.Id : null, Decision = dec, Comment = dec == StepDecision.Rejected ? "Dates don't match the project plan." : dec == StepDecision.Approved ? "Looks fine." : null,
                    ActedAtUtc = dec != StepDecision.Pending ? raised.AddHours(6 * (i + 1)) : null }));
            }
            ds.ApprovalRequests.Add(req); decidedGuid++;
            return req;
        }
        // decided history (feeds AI-UC-07 baselines): Priya reliable, Anil mixed, Lakshmi frequently rejected
        for (var i = 0; i < 30; i++)
        {
            var (p, who) = (i % 3) switch { 0 => (pRet1, priya), 1 => (pLog1, anil), _ => (pCcr1, lakshmi) };
            var rejectRate = who == priya ? 0.06 : who == anil ? 0.25 : 0.5;
            var rejected = rng.NextDouble() < rejectRate;
            var t = i % 4 == 3 ? ApprovalType.ProjectShiftTagging : ApprovalType.EmployeeTagging;
            var from = d(2026, 1 + i % 8, 1 + (i * 3) % 25);
            Req($"hist-{i}", t, p, who, $"{(t == ApprovalType.EmployeeTagging ? "Tag" : "Shift-tag")} request {p.Code}", "Historical request", 20 + i * 6, rejected ? 0 : 99, null, from, null, null, rejected ? ApprovalStatus.Rejected : ApprovalStatus.Approved);
        }
        var logEmp = log1[0];
        // 15 pending, full risk range
        var ps1 = Req("p01", ApprovalType.ProjectShiftTagging, pRet3, priya, "Shift-tag Click-and-collect pilot", "Morning and evening shifts from 1 Nov 2026; 8 people proposed.", 1, 0, null, d(2026,11,1), d(2027,4,30), null, subject: pRet3.Id);
        var ps2 = Req("p02", ApprovalType.ProjectShiftTagging, pLog2, anil, "Extend shift tagging: Last-mile dispatch", "Adds Evening shift; backdated to 1 Sep 2026.", 9, 1, null, d(2026,9,1), null, null);
        var ps3 = Req("p03", ApprovalType.ProjectShiftTagging, pCcr2, lakshmi, "Shift-tag Escalations cell", "On-call cover from 15 Oct; requester has a high rejection rate.", 3, 2, null, d(2026,10,15), null, null);
        var et1 = Req("p04", ApprovalType.EmployeeTagging, pRet3, priya, "Tag 8 people to Click-and-collect pilot", "From 1 Nov 2026, primary tags, all approved eligible.", 1, 0, null, d(2026,11,1), null, null);
        // overlapping tags for risk: employees already tagged to RET-02 get a second tag on RET-01 from 15 Oct
        var overlapPeople = ret2.Take(3).ToList();
        foreach (var e in overlapPeople) ds.EmployeeTags.Add(Stamp(new EmployeeTag { Id = Gid($"tag:overlap:{e.Id}"), EmployeeId = e.Id, ProjectId = pRet1.Id, ValidFrom = d(2026,10,15), TagType = TagType.Secondary, Status = TagStatus.PendingApproval }));
        var et2 = Req("p05", ApprovalType.EmployeeTagging, pRet1, priya, "Tag 3 people from Festive fulfilment to Store operations", "From 15 Oct 2026; these people already hold an approved tag on Festive fulfilment.", 2, 1, null, d(2026,10,15), null, null);
        foreach (var t in ds.EmployeeTags.Where(t => t.Id.ToString().Length > 0 && overlapPeople.Any(o => Gid($"tag:overlap:{o.Id}") == t.Id))) t.ApprovalRequestId = et2.Id;
        foreach (var t in ds.EmployeeTags.Where(t => t.ProjectId == pRet3.Id && t.Status == TagStatus.PendingApproval)) t.ApprovalRequestId = et1.Id;
        var et3 = Req("p06", ApprovalType.EmployeeTagging, pLog1, anil, "Backtag Kiran to Hub sorting from 1 Aug", "Backdated 70 days; requester has a mixed approval record.", 4, 1, null, d(2026,8,1), null, logEmp.Id);
        var et4 = Req("p07", ApprovalType.EmployeeTagging, pRet1, priya, "Tag 4 people to Store operations from 15 Aug", "Backdated by about 8 weeks to cover late onboarding.", 1, 0, null, d(2026,8,15), null, null);
        var et5 = Req("p08", ApprovalType.EmployeeTagging, pMfg1, suresh, "Tag 4 people to Line 3 production", "From 20 Oct 2026.", 5, 1, null, d(2026,10,20), null, null);
        var comp = Req("p09", ApprovalType.AllowanceCompensation, pRet1, priya, "Compensation for Store operations, September 2026", "Allowance run for September: needs HRSS approval.", 2, 0, sepRun1.TotalAmount, d(2026,9,1), d(2026,9,30), null, subject: sepRun1.Id);
        sepRun1.ApprovalRequestId = comp.Id;
        // Arrears (8)
        Arrear Arr(string key, Employee e, Project p, string forMonth, decimal amt, ArrearStatus st, string reason) => Stamp(new Arrear { Id = Gid("arr:" + key), EmployeeId = e.Id, ProjectId = p.Id, ForPeriodMonth = forMonth, Amount = amt, Status = st, Reason = reason, RaisedAtUtc = today.AddDays(-5).ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc) });
        var rep = ret1.Skip(3).First();
        var arrears = new[]
        {
            Arr("1", ret1[3], pRet1, "2026-09", 1050, ArrearStatus.Raised, "Night A shifts keyed at the old rate."), Arr("2", ret1[4], pRet1, "2026-08", 2400, ArrearStatus.PendingApproval, "Rate revision not applied on 12 shifts."),
            Arr("3", ret1[5], pRet1, "2026-06", 3150, ArrearStatus.PendingApproval, "Missed evening allowance."), Arr("4", ret2[2], pRet2, "2026-05", 1800, ArrearStatus.Raised, "Roster published after month-end."),
            Arr("5", ret1[3], pRet1, "2026-02", 5250, ArrearStatus.PendingApproval, "Late-tagged employee: allowance never calculated."), Arr("6", log1[2], pLog1, "2025-12", 8400, ArrearStatus.PendingApproval, "Holiday cover not claimed."),
            Arr("7", log1[5], pLog1, "2025-10", 2100, ArrearStatus.Approved, "Duplicate recovery reversed."), Arr("8", sanjay, pRet1, "2026-08", 1575, ArrearStatus.Raised, "Settlement-linked: pending night allowance."),
        };
        ds.Arrears.AddRange(arrears);
        ApprovalRequest ArrReq(string key, Arrear a, Role[] roles, int done, int daysAgo)
        {
            var r2 = Req(key, ApprovalType.Arrear, ds.Proj(a.ProjectId), priya, $"Arrear for {ds.Emp(a.EmployeeId).FullName}, {a.ForPeriodMonth}", a.Reason, daysAgo, done, a.Amount, null, null, a.EmployeeId, roles: roles, subject: a.Id);
            a.ApprovalRequestId = r2.Id; return r2;
        }
        ArrReq("p10", arrears[1], new[] { Role.HRBP }, 0, 3);
        ArrReq("p11", arrears[2], new[] { Role.RH }, 0, 6);
        ArrReq("p12", arrears[4], new[] { Role.RH, Role.HRSS }, 1, 8);
        ArrReq("p13", arrears[5], new[] { Role.RH, Role.HRSS }, 0, 12);
        // Movements
        var mvStaged = Stamp(new Movement { Id = Gid("mov:staged"), EmployeeId = ret2[0].Id, FromProjectId = pRet2.Id, ToProjectId = pRet1.Id, EffectiveFrom = d(2026,9,20), IsBackdated = true, Reason = "Role change agreed with both PMs.", Status = MovementStatus.Staged, StagedBy = Fathima.ToString() });
        ds.Movements.Add(mvStaged);
        ds.Movements.Add(Stamp(new Movement { Id = Gid("mov:done"), EmployeeId = ret1[8].Id, FromProjectId = pRet5.Id, ToProjectId = pRet1.Id, EffectiveFrom = d(2026,4,1), Reason = "Project closed; moved to Store operations.", Status = MovementStatus.Committed, StagedBy = Fathima.ToString(), CommittedBy = Fathima.ToString() }));
        Req("p14", ApprovalType.Movement, pRet1, priya, $"Move {ret2[0].FullName} from Festive fulfilment to Store operations", "Backdated to 20 Sep 2026; needs a ripple check.", 2, 0, null, d(2026,9,20), null, ret2[0].Id, roles: new[] { Role.HRBP, Role.HRSS }, subject: mvStaged.Id);
        Req("p15", ApprovalType.Exception, pRet1, priya, "Accept two exceptions for Store operations, September", "Both are deliberate rest-day swaps agreed with employees.", 4, 0, null, null, null, null, subject: Gid("exc:oct-streak"));

        // ---- Delegations
        ds.Delegations.Add(Stamp(new Delegation { Id = Gid("deleg:arjun"), FromEmployeeId = Arjun, ToEmployeeId = nikhil.Id, Scope = DelegationScope.ProjectApproval, ValidFrom = today.AddDays(-3), ValidTo = today.AddDays(4), Reason = "Travelling for the regional offsite" }));
        ds.Delegations.Add(Stamp(new Delegation { Id = Gid("deleg:priya"), FromEmployeeId = Priya, ToEmployeeId = anil.Id, Scope = DelegationScope.ExceptionReview, ValidFrom = today.AddDays(-5), ValidTo = today.AddDays(2), Reason = "Annual leave" }));

        // ---- Declarations, settlement, notifications
        ds.Declarations.Add(Stamp(new Declaration { Id = Gid("decl:aug"), EmployeeId = Ravi, PeriodMonth = "2026-08", Type = "Monthly allowance declaration", Status = DeclarationStatus.Signed, SignedAtUtc = d(2026,9,3).ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc) }));
        ds.Declarations.Add(Stamp(new Declaration { Id = Gid("decl:sep"), EmployeeId = Ravi, PeriodMonth = "2026-09", Type = "Monthly allowance declaration", Status = DeclarationStatus.Due }));
        ds.Declarations.Add(Stamp(new Declaration { Id = Gid("decl:q3"), EmployeeId = Ravi, PeriodMonth = "2026-09", Type = "Quarterly shift-work compliance", Status = DeclarationStatus.Due }));
        var sanjayLines = ds.Lines.Where(l => l.EmployeeId == sanjay.Id && l.AllowanceRunId == sepRun1.Id).Sum(l => l.Amount);
        ds.Settlements.Add(Stamp(new Settlement { Id = Gid("settle:sanjay"), EmployeeId = sanjay.Id, ExitDate = sanjay.ExitedOn!.Value, PendingAllowanceTotal = sanjayLines, PendingArrearTotal = arrears[7].Amount, ClearanceStatus = ClearanceStatus.Open }));
        Notification Note(Guid to, string type, string title, string body, NotificationSeverity sev, string route, int hoursAgo, bool read = false) => new()
        { Id = Gid($"note:{to}:{type}:{title}"), RecipientEmployeeId = to, Type = type, Title = title, Body = body, Severity = sev, LinkRoute = route, SentAtUtc = today.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc).AddHours(-hoursAgo), ReadAtUtc = read ? today.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc).AddHours(-hoursAgo + 1) : null };
        ds.Notifications.AddRange(new[]
        {
            Note(Ravi, "RosterPublished", "October roster is ready", "Your October roster for Store operations is published. Check your weekly offs.", NotificationSeverity.Info, "/me", 30),
            Note(Ravi, "RosterChanged", "Shift changed for 14 Oct", "Your shift on Wed 14 Oct moved to Evening. Lead time is under 24 hours.", NotificationSeverity.Warning, "/me", 6),
            Note(Ravi, "DeclarationDue", "September declaration due", "Sign your monthly allowance declaration by 12 Oct.", NotificationSeverity.Warning, "/me", 20),
            Note(Arjun, "ApprovalAssigned", "3 requests are waiting for you", "Click-and-collect pilot, tagging and backtag requests need your review.", NotificationSeverity.Info, "/approvals", 3),
            Note(Arjun, "DelegationStarted", "Your delegation to Nikhil Rao is active", "Ends in 4 days.", NotificationSeverity.Info, "/delegation", 70, true),
            Note(Meera, "ApprovalAssigned", "Overlap risk on a tagging request", "Three people already hold approved tags elsewhere.", NotificationSeverity.Critical, "/approvals", 5),
            Note(Meera, "ExceptionAssigned", "7 exceptions assigned to you", "Weekly review is due.", NotificationSeverity.Info, "/exceptions", 28),
            Note(Karthik, "ApprovalAssigned", "Aged arrear needs your decision", "A 4-month-old arrear was routed to you.", NotificationSeverity.Warning, "/approvals", 12),
            Note(Fathima, "ApprovalAssigned", "Compensation approval waiting", "Store operations, September: ₹ approval required.", NotificationSeverity.Warning, "/approvals", 4),
            Note(Fathima, "Anomaly", "Anomaly scan found 4 items", "A duplicate claim, a rate mismatch and 2 outliers.", NotificationSeverity.Critical, "/anomalies", 2),
            Note(Priya, "DelegationExpiring", "Your delegation to Anil Desai expires in 2 days", "Extend it if you're still away.", NotificationSeverity.Warning, "/delegation", 8),
            Note(Priya, "ApprovalDecided", "Tag request moved to HR review", "Arjun approved 'Tag 3 people…'.", NotificationSeverity.Success, "/approvals", 15),
        });
        ds.ResetIndexes();
        return ds;
    }
}
