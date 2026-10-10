import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { Toasts } from '../core/ui.services';
import { DatePipeRx, InrPipe } from '../shared/fmt';
import { Column, RxAiCard, RxAiSkeleton, RxDialog, RxEmpty, RxIcon, RxImpact, RxPageHeader, RxStatus, RxTable } from '../shared/ui';

// ============================================================ Movements (HRSS): ripple preview → stage → commit
@Component({
  selector: 'rx-movements', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxStatus, RxIcon, RxDialog, RxAiCard, RxAiSkeleton, RxImpact, RxEmpty, DatePipeRx, InrPipe],
  template: `
  <rx-page-header title="Movements and backtagging" sub="Move someone between projects. Rosterix shows every record the change would touch before anything is committed, and commits in two deliberate steps."/>
  <div class="grid-2" style="align-items:start">
    <section class="card" aria-labelledby="mv"><div class="card-h"><h2 id="mv" style="font-size:16px">1. Describe the move</h2></div>
      <form class="card-b stack" (ngSubmit)="preview()" #f="ngForm">
        <label class="field">Employee<input type="search" [(ngModel)]="q" name="q" placeholder="Search by name or number" (ngModelChange)="search()" aria-label="Find employee" autocomplete="off"></label>
        <ng-container *ngIf="found().length"> <ul style="list-style:none;margin:0;padding:0;border:1px solid var(--rx-border);border-radius:6px"><ng-container *ngFor="let e of found()"> <li><button type="button" class="btn ghost" style="width:100%;justify-content:flex-start;border-radius:0" (click)="pickEmp(e)">{{ e.name }} <span class="muted small">{{ e.grade }} · {{ e.number }} · {{ e.businessUnit }}</span></button></li> </ng-container></ul> </ng-container>
        <ng-container *ngIf="emp() as e"> <div class="banner info"><rx-icon name="person"/><span><strong>{{ e.name }}</strong> ({{ e.number }}) <ng-container *ngIf="current() as c"> is currently tagged to <strong>{{ c }}</strong>. </ng-container><ng-container *ngIf="!(current())"> has no current tag. </ng-container></span></div> </ng-container>
        <div class="form-grid">
          <label class="field">Move from<select [(ngModel)]="m.from" name="from"><option value="">Their current project</option><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }} · {{ p.name }}</option> </ng-container></select></label>
          <label class="field">Move to<select [(ngModel)]="m.to" name="to" required><option value="">Choose a project</option><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }} · {{ p.name }}</option> </ng-container></select></label>
          <label class="field">Effective from<input type="date" [(ngModel)]="m.from_" name="ef" required></label>
          <label class="field">Until <span class="hint">optional</span><input type="date" [(ngModel)]="m.until" name="eu"></label>
        </div>
        <ng-container *ngIf="m.from_ && m.from_ < today()"> <div class="banner warn"><rx-icon name="alert"/><span><strong>Backdated.</strong> Earlier rosters and allowance lines are affected, and the move needs HRBP and HRSS approval before it can be committed.</span></div> </ng-container>
        <label class="field">Reason<input type="text" [(ngModel)]="m.reason" name="reason" required maxlength="300"></label>
        <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
        <div><button type="submit" class="btn ai" [disabled]="!emp() || !m.to || !m.from_ || loadingAi()"><rx-icon name="spark"/>Preview the ripple (AI)</button></div>
      </form></section>

    <section aria-labelledby="rp" class="stack"><h2 id="rp" style="font-size:16px">2. Review the ripple</h2>
      <ng-container *ngIf="loadingAi()"> <rx-ai-skeleton/> </ng-container><ng-container *ngIf="!(loadingAi())"><ng-container *ngIf="rip() as r">
        <rx-ai-card heading="Conflict and ripple preview" [summary]="r.summary" [impact]="r.impact" [confidence]="r.confidence" [reasons]="r.reasons"/>
        <ng-container *ngIf="r.payload.violations.length"> <div class="banner danger"><rx-icon name="ban"/><div><strong>This would break a blocking rule.</strong><ul style="margin:4px 0 0 18px;padding:0"><ng-container *ngFor="let v of r.payload.violations"> <li><span class="mono">{{ v.ruleCode }}</span> {{ v.message }} <span class="muted">{{ v.suggestedFix }}</span></li> </ng-container></ul></div></div> </ng-container>
        <rx-impact [rows]="r.payload.rows"/>
        <div class="row"><button type="button" class="btn primary" (click)="stage()" [disabled]="r.payload.hasBlock || busy()">Stage this movement (step 1)</button><span class="small muted">Staging saves the request. It doesn’t change tags or rosters yet.</span></div>
      </ng-container></ng-container><ng-container *ngIf="!(loadingAi()) && !(rip())"> <rx-empty title="No preview yet" text="Fill in the move and choose “Preview the ripple”."/> </ng-container>
    </section>
  </div>

  <section class="card" style="margin-top:20px" aria-labelledby="ml"><div class="card-h"><h2 id="ml" style="font-size:16px">3. Commit staged movements</h2></div><div class="card-b" style="padding:0">
    <ng-container *ngFor="let mv of list()">
      <div class="queue-item" style="grid-template-columns:1fr auto"><div><div class="t">{{ mv.employee.name }}: {{ mv.from || '—' }} → {{ mv.to }}</div><div class="small muted">from {{ mv.effectiveFrom | rxDate }}<ng-container *ngIf="mv.effectiveTo"> to {{ mv.effectiveTo | rxDate }} </ng-container> · {{ mv.reason }}</div>
        <ng-container *ngIf="mv.isBackdated"> <div class="small"><span class="chip warn">backdated</span> approval: <rx-status [value]="mv.approvalStatus || 'Pending'"/> <ng-container *ngIf="mv.approvalId"> <a [routerLink]="['/approvals', mv.approvalId]">Open</a> </ng-container></div> </ng-container></div>
        <div class="row"><rx-status [value]="mv.status"/><ng-container *ngIf="mv.status === 'Staged'"> <button type="button" class="btn sm primary" [disabled]="!mv.canCommit" (click)="confirm.set(mv)" [attr.aria-describedby]="mv.canCommit ? null : 'why-' + mv.id">Commit…</button><button type="button" class="btn sm ghost" (click)="cancel(mv)">Cancel</button> </ng-container></div>
        <ng-container *ngIf="mv.status === 'Staged' && !mv.canCommit"> <div class="small muted" [id]="'why-' + mv.id" style="grid-column:1/-1">A backdated movement can be committed only after its approval completes.</div> </ng-container>
      </div>
    </ng-container><ng-container *ngIf="!(list())?.length"> <rx-empty title="No movements yet"/> </ng-container>
  </div></section>

  <ng-container *ngIf="confirm() as mv">
    <rx-dialog title="Commit this movement?" (close)="confirm.set(null)">
      <p>{{ mv.employee.name }} moves to <strong>{{ mv.to }}</strong> from {{ mv.effectiveFrom | rxDate }}. Their old tag ends the day before, roster entries on the old project from that date are removed, and any already-approved allowance for those dates becomes an arrear to review.</p>
      <div footer class="row"><button type="button" class="btn" (click)="confirm.set(null)">Not yet</button><button type="button" class="btn primary" (click)="commit()" [disabled]="busy()">Commit movement (step 2)</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class MovementsPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts);
  list = signal<J[]>([]); projects = signal<J[]>([]); found = signal<J[]>([]); emp = signal<J | null>(null); current = signal(''); rip = signal<J | null>(null); loadingAi = signal(false); busy = signal(false); err = signal(''); confirm = signal<J | null>(null); today = signal('');
  q = ''; m: J = { from: '', to: '', from_: '', until: '', reason: '' };
  ngOnInit() { this.api.get('/api/lookups').subscribe(l => { this.projects.set(l.projects); this.today.set(l.today); this.m.from_ = l.today; }); this.load(); }
  load() { this.api.get<J[]>('/api/movements').subscribe(r => this.list.set(r)); }
  search() { if (this.q.trim().length < 2) { this.found.set([]); return; } this.api.get<J[]>('/api/employees', { q: this.q, take: 6 }).subscribe(r => this.found.set(r)); }
  pickEmp(e: J) { this.emp.set(e); this.found.set([]); this.q = e.name; this.rip.set(null); this.api.get<J[]>('/api/employee-tags', { employeeId: e.id }).subscribe(t => { const cur = t.find(x => ['Approved', 'Backtagged'].includes(x.status) && !x.validTo); this.current.set(cur ? cur.project.name : ''); if (cur) this.m.from = cur.project.projectId; }); }
  private body() { return { employeeId: this.emp()!.id, fromProjectId: this.m.from || null, toProjectId: this.m.to, effectiveFrom: this.m.from_, effectiveTo: this.m.until || null }; }
  preview() { this.err.set(''); this.loadingAi.set(true); this.api.post('/api/ai/ripple', this.body()).subscribe({ next: r => { this.rip.set(r); this.loadingAi.set(false); }, error: e => { this.loadingAi.set(false); this.err.set(e.title); } }); }
  stage() { if (!this.m.reason.trim()) { this.err.set('Add a reason before staging.'); return; } this.busy.set(true); this.api.post('/api/movements', { ...this.body(), reason: this.m.reason }).subscribe({ next: r => { this.busy.set(false); this.toast.success(r.backdated ? 'Staged. It is backdated, so approval was requested.' : 'Staged. You can commit it below.'); this.rip.set(null); this.load(); }, error: e => { this.busy.set(false); this.err.set(e.title); } }); }
  commit() { this.busy.set(true); const id = this.confirm()!.id; this.api.post(`/api/movements/${id}/commit`).subscribe({ next: r => { this.busy.set(false); this.confirm.set(null); this.toast.success(`Committed. ${r.rosterEntriesRemoved} roster entries moved off the old project.`); this.load(); }, error: e => { this.busy.set(false); this.confirm.set(null); this.toast.error(e.title); } }); }
  cancel(mv: J) { this.api.post(`/api/movements/${mv.id}/cancel`).subscribe({ next: () => { this.toast.info('Movement cancelled.'); this.load(); }, error: e => this.toast.error(e.title) }); }
}

// ============================================================ Delegation (PM, LM)
@Component({
  selector: 'rx-delegation', standalone: true, imports: [CommonModule, FormsModule, RxPageHeader, RxStatus, RxIcon, RxEmpty, DatePipeRx],
  template: `
  <rx-page-header title="Delegation" sub="Hand your approvals to a colleague while you’re away. They must hold the same role and grade as you, and can’t report to you."/>
  <ng-container *ngFor="let d of expiring()"> <div class="banner warn" style="margin-bottom:12px"><rx-icon name="clock"/><span>Your delegation to <strong>{{ d.to.name }}</strong> ends in {{ d.daysLeft }} day{{ d.daysLeft === 1 ? '' : 's' }} ({{ d.validTo | rxDate }}). Create a new one below if you need longer.</span></div> </ng-container>
  <div class="grid-2" style="align-items:start">
    <ng-container *ngIf="session.has('PM','LM')">
      <form class="card" (ngSubmit)="create()" aria-label="New delegation"><div class="card-h"><h2 style="font-size:16px">New delegation</h2></div><div class="card-b stack">
        <label class="field">Delegate to
          <select [(ngModel)]="f.to" name="to" required aria-describedby="elig"><option value="">Choose a colleague</option><ng-container *ngFor="let c of cands()"> <option [value]="c.id" [disabled]="!c.eligible">{{ c.name }} · {{ c.grade }} · {{ c.businessUnit }}{{ c.eligible ? '' : ' (not eligible)' }}</option> </ng-container></select></label>
        <p id="elig" class="small muted" aria-live="polite"><ng-container *ngIf="selectedCand() as c"> <ng-container *ngIf="c.eligible"> <rx-icon name="check" [size]="14"/> Eligible: same role and grade, not in your reporting line. </ng-container><ng-container *ngIf="!(c.eligible)"> {{ c.reason }} </ng-container> </ng-container><ng-container *ngIf="!(selectedCand())"> Only same-role, same-grade peers can be chosen. Others are shown greyed out with the reason in the list below. </ng-container></p>
        <div class="form-grid"><label class="field">What to delegate<select [(ngModel)]="f.scope" name="sc"><option value="ProjectApproval">Project approvals</option><option value="EmployeeTaggingApproval">Employee tagging approvals</option><option value="ExceptionReview">Exception review</option></select></label>
          <label class="field">From<input type="date" [(ngModel)]="f.from" name="fr" required></label><label class="field">To<input type="date" [(ngModel)]="f.until" name="un" required></label></div>
        <label class="field">Reason<input type="text" [(ngModel)]="f.reason" name="re" required maxlength="200" placeholder="For example: annual leave"></label>
        <div class="banner info"><rx-icon name="lock"/><span>Allowance compensation can never be delegated. Only HRSS approves it.</span></div>
        <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
        <div><button type="submit" class="btn primary" [disabled]="busy() || !f.to">Create delegation</button></div>
      </div></form>
      <section class="card" aria-labelledby="pe"><div class="card-h"><h2 id="pe" style="font-size:16px">Who you can choose</h2></div><div class="card-b" style="padding:0;max-height:360px;overflow:auto">
        <ng-container *ngFor="let c of cands()"> <div class="queue-item" style="grid-template-columns:1fr auto"><div><div class="t">{{ c.name }}</div><div class="small muted">{{ c.grade }} · {{ c.businessUnit }}<ng-container *ngIf="!c.eligible"> · {{ c.reason }} </ng-container></div></div><rx-status [value]="c.eligible ? 'Pass' : 'Block'" [label]="c.eligible ? 'Eligible' : 'Not eligible'"/></div> </ng-container></div></section>
    </ng-container>
  </div>
  <section class="card" style="margin-top:20px" aria-labelledby="gl"><div class="card-h"><h2 id="gl" style="font-size:16px">Delegations</h2></div><div class="card-b" style="padding:0">
    <ng-container *ngFor="let d of all()">
      <div class="queue-item" style="grid-template-columns:1fr auto"><div><div class="t">{{ d.from.name }} → {{ d.to.name }}</div><div class="small muted">{{ d.scope }} · {{ d.validFrom | rxDate }} to {{ d.validTo | rxDate }} · {{ d.reason }}</div></div>
        <div class="row"><rx-status [value]="d.isRevoked ? 'Cancelled' : d.active ? 'Active' : 'Closed'" [label]="d.isRevoked ? 'Revoked' : d.active ? (d.daysLeft + ' days left') : 'Ended'"/><ng-container *ngIf="d.active && d.from.id === session.me()?.id"> <button type="button" class="btn sm danger-outline" (click)="revoke(d)">Revoke</button> </ng-container></div></div>
    </ng-container><ng-container *ngIf="!(all())?.length"> <rx-empty title="No delegations" text="Delegations you give or receive appear here."/> </ng-container></div></section>`,
})
export class DelegationPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts); session = inject(Session);
  cands = signal<J[]>([]); all = signal<J[]>([]); busy = signal(false); err = signal(''); f: J = { to: '', scope: 'ProjectApproval', from: '', until: '', reason: '' };
  expiring = computed(() => this.all().filter(d => d.expiringSoon && d.from.id === this.session.me()?.id));
  selectedCand = () => this.cands().find(c => c.id === this.f.to);
  ngOnInit() { this.api.get('/api/lookups').subscribe(l => (this.f.from = l.today)); if (this.session.has('PM', 'LM')) this.api.get<J[]>('/api/delegations/candidates').subscribe(c => this.cands.set(c)); this.load(); }
  load() { this.api.get('/api/delegations').subscribe(r => this.all.set([...r.given, ...r.received, ...r.others])); }
  create() { this.err.set(''); this.busy.set(true); this.api.post('/api/delegations', { toEmployeeId: this.f.to, scope: this.f.scope, validFrom: this.f.from, validTo: this.f.until, reason: this.f.reason }).subscribe({ next: () => { this.busy.set(false); this.toast.success('Delegation created. They’ve been notified.'); this.f.to = ''; this.f.reason = ''; this.load(); void this.session.refresh(); }, error: e => { this.busy.set(false); this.err.set(e.title); } }); }
  revoke(d: J) { this.api.post(`/api/delegations/${d.id}/revoke`).subscribe({ next: () => { this.toast.info('Delegation revoked.'); this.load(); void this.session.refresh(); }, error: e => this.toast.error(e.title) }); }
}

// ============================================================ Arrears
@Component({
  selector: 'rx-arrears', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxTable, RxStatus, RxIcon, RxDialog, InrPipe],
  template: `
  <rx-page-header title="Arrears" sub="Money owed from earlier months. Older arrears take a longer approval route and carry an AI risk score for the approver.">
    <ng-container *ngIf="session.has('PM')"> <button type="button" class="btn primary" (click)="raiseOpen.set(true)"><rx-icon name="plus"/>Raise an arrear</button> </ng-container>
  </rx-page-header>
  <div class="banner info" style="margin-bottom:12px"><rx-icon name="info"/><span><strong>Routing by age:</strong> up to 3 months → HRBP · 4 to 6 months → Regional Head · over 6 months → Regional Head, then HRSS.</span></div>
  <div class="tabs" role="tablist" aria-label="Age bucket"><ng-container *ngFor="let b of buckets"> <button type="button" role="tab" [attr.aria-selected]="bucket() === b.k" [tabindex]="bucket() === b.k ? 0 : -1" (click)="bucket.set(b.k)">{{ b.l }} <span class="chip neutral" style="margin-left:6px">{{ count(b.k) }}</span></button> </ng-container></div>
  <div class="card" style="margin-top:12px"><div class="card-b" style="padding:0"><rx-table [rows]="rows()" [columns]="cols" [rowTpl]="act" caption="Arrears" emptyTitle="No arrears in this bucket"/>
    <ng-template #act let-r><ng-container *ngIf="r.canRoute"> <button type="button" class="btn sm" (click)="route(r)" [attr.aria-label]="'Route arrear for ' + r.name + ' for approval'">Route for approval</button> </ng-container><ng-container *ngIf="!(r.canRoute) && (r.approvalId)"> <a class="btn sm ghost" [routerLink]="['/approvals', r.approvalId]">View</a> </ng-container></ng-template></div></div>
  <ng-container *ngIf="raiseOpen()">
    <rx-dialog title="Raise an arrear" (close)="raiseOpen.set(false)">
      <div class="form-grid">
        <label class="field">Employee<input type="search" [(ngModel)]="q" name="q" (ngModelChange)="search()" placeholder="Search" aria-label="Find employee" autocomplete="off"></label>
        <label class="field">Project<select [(ngModel)]="n.projectId" name="pj"><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }}</option> </ng-container></select></label>
        <label class="field">For month<input type="month" [(ngModel)]="n.month" name="mo"></label>
        <label class="field">Amount (₹)<input type="number" [(ngModel)]="n.amount" name="am" min="1"></label>
      </div>
      <ng-container *ngIf="found().length"> <ul style="list-style:none;margin:6px 0;padding:0"><ng-container *ngFor="let e of found()"> <li><button type="button" class="btn ghost sm" (click)="n.employeeId = e.id; q = e.name; found.set([])">{{ e.name }} · {{ e.number }}</button></li> </ng-container></ul> </ng-container>
      <label class="field" style="margin-top:8px">Reason<input type="text" [(ngModel)]="n.reason" name="rs" maxlength="300"></label>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert" style="margin-top:8px"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="raiseOpen.set(false)">Cancel</button><button type="button" class="btn primary" (click)="raise()">Raise arrear</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class ArrearsPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts); session = inject(Session);
  all = signal<J[]>([]); bucket = signal(''); raiseOpen = signal(false); projects = signal<J[]>([]); found = signal<J[]>([]); err = signal(''); q = ''; n: J = { employeeId: '', projectId: '', month: '', amount: null, reason: '' };
  buckets = [{ k: '', l: 'All' }, { k: '0-3', l: '0–3 months' }, { k: '4-6', l: '4–6 months' }, { k: '>6', l: 'Over 6 months' }];
  cols: Column[] = [{ key: 'name', label: 'Employee' }, { key: 'project', label: 'Project' }, { key: 'forMonth', label: 'For month', type: 'mono' }, { key: 'amount', label: 'Amount', type: 'money' }, { key: 'ageLabel', label: 'Age' }, { key: 'route', label: 'Approval route' }, { key: 'status', label: 'Status', type: 'chip' }, { key: 'riskLabel', label: 'AI risk' }, { key: 'reason', label: 'Reason', wrap: true }];
  rows = computed(() => this.all().filter(a => !this.bucket() || a.bucket === this.bucket()).map(a => ({ ...a, name: a.employee.name, ageLabel: `${a.ageMonths} mo (${a.bucket})`, riskLabel: a.risk ? `${a.risk.band} ${a.risk.score.toFixed(2)}` : '—', approvalId: a.approvalRequestId })));
  count = (k: string) => this.all().filter(a => !k || a.bucket === k).length;
  ngOnInit() { this.load(); this.api.get('/api/lookups').subscribe(l => { this.projects.set(l.projects); this.n.projectId = l.projects[0]?.id; const d = new Date(l.today); d.setMonth(d.getMonth() - 1); this.n.month = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`; }); }
  load() { this.api.get<J[]>('/api/arrears').subscribe(r => this.all.set(r)); }
  search() { if (this.q.trim().length < 2) { this.found.set([]); return; } this.api.get<J[]>('/api/employees', { q: this.q, take: 5 }).subscribe(r => this.found.set(r)); }
  route(r: J) { this.api.post(`/api/arrears/${r.id}/route`).subscribe({ next: x => { this.toast.success(`Sent for approval: ${x.route}.`); this.load(); }, error: e => this.toast.error(e.title) }); }
  raise() { this.err.set(''); this.api.post('/api/arrears', { employeeId: this.n.employeeId, projectId: this.n.projectId, forPeriodMonth: this.n.month, amount: this.n.amount, reason: this.n.reason }).subscribe({ next: () => { this.toast.success('Arrear raised. Route it for approval when ready.'); this.raiseOpen.set(false); this.load(); }, error: e => this.err.set(e.title) }); }
}

// ============================================================ Full & final settlement (HRSS)
@Component({
  selector: 'rx-settlement', standalone: true, imports: [CommonModule, FormsModule, RxPageHeader, RxStatus, RxIcon, RxDialog, RxEmpty, DatePipeRx, InrPipe],
  template: `
  <rx-page-header title="Full and final settlement" sub="What is still owed to people who are leaving. Clear a settlement only when every item is accounted for."/>
  <div class="stack">
    <ng-container *ngFor="let s of items()">
      <article class="card" [attr.aria-label]="'Settlement for ' + s.employee.name"><div class="card-h"><div><h2 style="font-size:16px">{{ s.employee.name }} <span class="muted small">{{ s.employee.number }}</span></h2><div class="small muted">Exit {{ s.exitDate | rxDate }} ({{ s.daysToExit >= 0 ? 'in ' + s.daysToExit + ' days' : -s.daysToExit + ' days ago' }})</div></div><rx-status [value]="s.status"/></div>
        <div class="card-b stack">
          <div class="grid-3"><div class="kpi"><span class="v">{{ s.pendingAllowance | inr }}</span><span class="l">Allowance not yet in payroll</span></div><div class="kpi"><span class="v">{{ s.pendingArrears | inr }}</span><span class="l">Arrears outstanding</span></div><div class="kpi"><span class="v">{{ s.total | inr }}</span><span class="l">Total to settle</span></div></div>
          <div class="table-wrap"><table class="rx"><caption class="sr-only">Allowance runs with unpaid amounts</caption><thead><tr><th>Month</th><th>Project</th><th>Run status</th><th class="num">Shifts</th><th class="num">Amount</th></tr></thead><tbody><ng-container *ngFor="let r of s.allowanceRuns"> <tr><td class="mono">{{ r.run }}</td><td>{{ r.project }}</td><td><rx-status [value]="r.status"/></td><td class="num">{{ r.shifts }}</td><td class="num">{{ r.amount | inr }}</td></tr> </ng-container></tbody></table></div>
          <ng-container *ngIf="s.arrears.length"> <div class="table-wrap"><table class="rx"><caption class="sr-only">Arrears</caption><thead><tr><th>For month</th><th>Reason</th><th>Status</th><th class="num">Amount</th></tr></thead><tbody><ng-container *ngFor="let a of s.arrears"> <tr><td class="mono">{{ a.forMonth }}</td><td class="wrap">{{ a.reason }}</td><td><rx-status [value]="a.status"/></td><td class="num">{{ a.amount | inr }}</td></tr> </ng-container></tbody></table></div> </ng-container>
          <ng-container *ngIf="s.unresolved"> <div class="banner warn"><rx-icon name="alert"/><span>{{ s.unresolved }} arrear(s) aren’t decided yet.</span></div> </ng-container>
          <ng-container *ngIf="s.status !== 'Cleared'"> <div><button type="button" class="btn primary" (click)="clearing.set(s)">Clear settlement…</button></div> </ng-container><ng-container *ngIf="!(s.status !== 'Cleared')"> <p class="small muted">Cleared by {{ s.clearedBy }} on {{ s.clearedAtUtc | rxDate }}.</p> </ng-container>
        </div></article>
    </ng-container><ng-container *ngIf="!(items())?.length"> <rx-empty title="No settlements open"/> </ng-container>
  </div>
  <ng-container *ngIf="clearing() as s">
    <rx-dialog title="Clear this settlement?" (close)="clearing.set(null)">
      <p>This records that {{ s.employee.name }}’s final dues of <strong>{{ s.total | inr }}</strong> are accounted for.</p>
      <ng-container *ngIf="s.unresolved"> <label class="check"><input type="checkbox" [(ngModel)]="ack" name="ack"> I understand {{ s.unresolved }} arrear(s) are still unresolved.</label> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="clearing.set(null)">Cancel</button><button type="button" class="btn primary" (click)="clear()" [disabled]="(s.unresolved && !ack) || busy()">Clear settlement</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class SettlementPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts);
  items = signal<J[]>([]); clearing = signal<J | null>(null); ack = false; busy = signal(false);
  ngOnInit() { this.load(); }
  load() { this.api.get<J[]>('/api/settlements').subscribe(r => this.items.set(r)); }
  clear() { this.busy.set(true); this.api.post(`/api/settlements/${this.clearing()!.id}/clear`, { acknowledgePending: this.ack }).subscribe({ next: () => { this.busy.set(false); this.clearing.set(null); this.ack = false; this.toast.success('Settlement cleared.'); this.load(); }, error: e => { this.busy.set(false); this.toast.error(e.title); } }); }
}
