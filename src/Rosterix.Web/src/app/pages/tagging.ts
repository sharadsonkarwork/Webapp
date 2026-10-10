import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { Toasts } from '../core/ui.services';
import { DatePipeRx } from '../shared/fmt';
import { RxSavedViews } from '../shared/tools';
import { Column, RxAiCard, RxAiSkeleton, RxConfidence, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxTable, RxWhy } from '../shared/ui';

@Component({
  selector: 'rx-tagging', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxTable, RxIcon, RxSavedViews],
  template: `
  <rx-page-header title="Employee tagging" sub="Who is tagged to which project, and what is waiting for approval.">
    <ng-container *ngIf="session.has('PM','LM')"> <a class="btn primary" routerLink="/tagging/new"><rx-icon name="plus"/>Tag employees</a> </ng-container>
  </rx-page-header>
  <div class="card"><div class="card-h"><div class="row">
    <label class="field"><span class="sr-only">Project</span><select [(ngModel)]="project" aria-label="Filter by project" (ngModelChange)="load()"><option value="">All projects</option><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }} · {{ p.name }}</option> </ng-container></select></label>
    <label class="field"><span class="sr-only">Status</span><select [(ngModel)]="status" aria-label="Filter by status" (ngModelChange)="load()"><option value="">All statuses</option><ng-container *ngFor="let s of statuses"> <option [value]="s">{{ s }}</option> </ng-container></select></label>
    <label class="field"><span class="sr-only">Search</span><input type="search" [(ngModel)]="q" placeholder="Search employee" aria-label="Search employee" (ngModelChange)="filter()"></label>
  </div><rx-saved-views key="tagging" [state]="{ project, status, q }" (apply)="apply($event)"/></div>
  <div class="card-b" style="padding:0"><rx-table [rows]="rows()" [columns]="cols" caption="Employee tags" [pageSize]="20" emptyTitle="No tags match" emptyText="Change the filters or tag some employees."/></div></div>`,
})
export class TaggingPage implements OnInit {
  private api = inject(Api); session = inject(Session);
  all = signal<J[]>([]); rows = signal<J[]>([]); projects = signal<J[]>([]); project = ''; status = ''; q = '';
  statuses = ['Draft', 'PendingApproval', 'Approved', 'Rejected', 'Withdrawn', 'Backtagged'];
  cols: Column[] = [{ key: 'name', label: 'Employee' }, { key: 'number', label: 'No.', type: 'mono' }, { key: 'project', label: 'Project' }, { key: 'validFrom', label: 'From', type: 'date' }, { key: 'validTo', label: 'To', type: 'date' }, { key: 'type', label: 'Type' }, { key: 'status', label: 'Status', type: 'chip' }];
  ngOnInit() { this.api.get('/api/lookups').subscribe(l => this.projects.set(l.projects)); this.load(); }
  apply(s: J) { this.project = s.project ?? ''; this.status = s.status ?? ''; this.q = s.q ?? ''; this.load(); }
  load() { this.api.get<J[]>('/api/employee-tags', { projectId: this.project, status: this.status }).subscribe(r => { this.all.set(r.map(t => ({ id: t.id, name: t.employee.name, number: t.employee.number, project: t.project.code + ' · ' + t.project.name, validFrom: t.validFrom, validTo: t.validTo, type: t.type, status: t.status }))); this.filter(); }); }
  filter() { const q = this.q.toLowerCase(); this.rows.set(this.all().filter(r => !q || r.name.toLowerCase().includes(q) || r.number.toLowerCase().includes(q))); }
}

@Component({
  selector: 'rx-tag-flow', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxAiCard, RxAiSkeleton, RxConfidence, RxWhy, RxIcon, RxStatus, RxEmpty, DatePipeRx],
  template: `
  <rx-page-header title="Tag employees to a project" sub="Four steps. Nothing is saved until you submit for approval at the end."/>
  <ol class="steps" aria-label="Progress" style="margin-bottom:16px"><ng-container *ngFor="let s of stepNames; let i = index"> <li [attr.aria-current]="step() === i ? 'step' : null" [class.done]="i < step()">{{ s }}</li> </ng-container></ol>

  <ng-container *ngIf="step() === 0">
    <form class="card" (ngSubmit)="toPick()"><div class="card-b stack">
      <div class="form-grid">
        <label class="field">Project<select name="p" [(ngModel)]="projectId" required><option value="">Choose a project</option><ng-container *ngFor="let p of eligible()"> <option [value]="p.id">{{ p.code }} · {{ p.name }}</option> </ng-container></select></label>
        <label class="field">Tag type<select name="t" [(ngModel)]="tagType"><option value="Primary">Primary</option><option value="Secondary">Secondary</option></select></label>
        <label class="field">From<input type="date" name="f" [(ngModel)]="from" required></label>
        <label class="field">To <span class="hint">leave empty for open ended</span><input type="date" name="to" [(ngModel)]="to"></label>
      </div>
      <ng-container *ngIf="from && from < today()"> <div class="banner warn"><rx-icon name="alert"/><span>This start date is in the past, so it will be a <strong>backtag</strong>. It needs extra care: Rosterix checks it can’t overlap another approved tag.</span></div> </ng-container>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div><button type="submit" class="btn primary">Continue to people</button></div>
    </div></form>
  </ng-container>

  <ng-container *ngIf="step() === 1">
    <div class="grid-2" style="align-items:start">
      <section class="stack" aria-label="Suggested people">
        <ng-container *ngIf="loadingAi()"> <rx-ai-skeleton/> </ng-container><ng-container *ngIf="!(loadingAi())"><ng-container *ngIf="ai() as a">
          <rx-ai-card heading="Suggested people" [summary]="a.summary" [impact]="a.impact" [confidence]="a.confidence" [reasons]="a.reasons" acceptLabel="Add top 5" (accept)="addTop(5)"/>
          <div class="card"><ul style="list-style:none;margin:0;padding:0">
            <ng-container *ngFor="let c of a.payload.candidates">
              <li class="queue-item" style="grid-template-columns:auto 1fr auto;align-items:start">
                <input type="checkbox" [checked]="isSel(c.employeeId)" (change)="toggleCand(c, $any($event.target).checked)" [attr.aria-label]="'Add ' + c.name" style="margin-top:6px;width:18px;height:18px">
                <div><div class="t">{{ c.name }} <span class="muted small">{{ c.grade }} · <span class="mono">{{ c.employeeNumber }}</span></span></div>
                  <div class="small muted">{{ c.reasons[0].text }}<ng-container *ngIf="c.cautions.length"> <span class="chip warn" style="margin-left:6px"><rx-icon name="alert" [size]="12"/>{{ c.cautions[0] }}</span> </ng-container></div></div>
                <div class="row"><rx-confidence [value]="c.score"/><rx-why [reasons]="c.reasons"/></div>
              </li>
            </ng-container>
          </ul></div>
        </ng-container></ng-container>
        <details class="card"><summary class="card-h" style="cursor:pointer;list-style:none"><h2 style="font-size:15px">Bulk upload (CSV)</h2><span class="small muted">employeeNumber,from,to</span></summary>
          <div class="card-b stack">
            <label class="field">Paste rows or choose a file<textarea [(ngModel)]="csv" name="csv" rows="4" placeholder="E1020,2026-11-02,"></textarea></label>
            <div class="row"><input type="file" accept=".csv,text/csv" (change)="file($event)" aria-label="Choose CSV file"><button type="button" class="btn" (click)="validateCsv()" [disabled]="!csv.trim()">Check rows</button></div>
            <ng-container *ngIf="csvRows().length">
              <div class="table-wrap"><table class="rx"><caption class="sr-only">Row-level results</caption><thead><tr><th>Row</th><th>Employee</th><th>Result</th></tr></thead><tbody>
                <ng-container *ngFor="let r of csvRows()"> <tr><td class="mono">{{ r.row }}</td><td>{{ r.name || r.employeeNumber }}</td><td class="wrap"><ng-container *ngIf="r.ok"> <rx-status value="Pass" label="Ready"/> </ng-container><ng-container *ngIf="!(r.ok)"> <rx-status value="Fail" label="Fix"/> </ng-container> <span class="small">{{ r.messages.join(' ') }}</span></td></tr> </ng-container>
              </tbody></table></div>
              <button type="button" class="btn primary" (click)="addCsv()" [disabled]="!csvOk()">Add {{ csvOk() }} valid row{{ csvOk() === 1 ? '' : 's' }}</button>
            </ng-container>
          </div></details>
      </section>
      <section class="card" aria-label="Chosen people"><div class="card-h"><h2 style="font-size:16px">Chosen ({{ selected().length }})</h2>
        <label class="field" style="flex-direction:row;gap:6px;align-items:center"><span class="sr-only">Find anyone</span><input type="search" [(ngModel)]="q" name="q" placeholder="Find anyone to add" aria-label="Find anyone to add" (ngModelChange)="search()"></label></div>
        <div class="card-b">
          <ng-container *ngIf="found().length"> <ul style="list-style:none;margin:0 0 12px;padding:0"><ng-container *ngFor="let f of found()"> <li class="row between" style="padding:2px 0"><span>{{ f.name }} <span class="muted small">{{ f.grade }} · {{ f.number }}</span></span><button type="button" class="btn sm" (click)="add(f)" [attr.aria-label]="'Add ' + f.name">Add</button></li> </ng-container></ul><hr class="sep"> </ng-container>
          <ng-container *ngFor="let s of selected()"> <div class="row between" style="padding:4px 0"><span>{{ s.name }} <span class="muted small">{{ s.grade }} · {{ s.number }}</span><ng-container *ngIf="s.from"> <span class="chip neutral" style="margin-left:6px">from {{ s.from | rxDate:false }}</span> </ng-container></span><button type="button" class="btn sm ghost" (click)="remove(s.id)" [attr.aria-label]="'Remove ' + s.name"><rx-icon name="x" [size]="14"/></button></div> </ng-container>
          <ng-container *ngIf="!selected().length"> <p class="muted">Pick people from the suggestions, search, or upload a CSV.</p> </ng-container>
        </div></section>
    </div>
    <div class="row" style="margin-top:14px"><button type="button" class="btn" (click)="step.set(0)">Back</button><button type="button" class="btn primary" (click)="toReview()" [disabled]="!selected().length">Review conflicts</button></div>
  </ng-container>

  <ng-container *ngIf="step() === 2">
    <div class="stack">
      <div class="banner info"><rx-icon name="info"/><span>The same rules that protect rosters are checked here. Blocking conflicts must be fixed; warnings need your acknowledgement.</span></div>
      <div class="card"><div class="card-b" style="padding:0">
        <ng-container *ngFor="let c of checks()">
          <div class="queue-item" style="grid-template-columns:1fr auto"><div><div class="t">{{ c.name }}</div>
            <ng-container *ngIf="!c.violations.length"> <div class="small" style="color:var(--rx-success)">No conflicts.</div> </ng-container>
            <ng-container *ngFor="let v of c.violations"> <div class="small" style="margin-top:4px"><rx-status [value]="v.severity" /> <strong>{{ v.ruleCode }}</strong> {{ v.message }} <ng-container *ngIf="v.suggestedFix"> <span class="muted">{{ v.suggestedFix }}</span> </ng-container></div> </ng-container>
          </div>
          <ng-container *ngIf="c.blocked"> <button type="button" class="btn sm danger-outline" (click)="remove(c.employeeId); toReview()">Remove from list</button> </ng-container></div>
        </ng-container>
      </div></div>
      <ng-container *ngIf="hasWarn()"> <label class="check"><input type="checkbox" [(ngModel)]="ackWarn" name="ack"> I’ve read the warnings and want to continue.</label> </ng-container>
      <div class="row"><button type="button" class="btn" (click)="step.set(1)">Back</button><button type="button" class="btn primary" (click)="step.set(3)" [disabled]="hasBlock() || (hasWarn() && !ackWarn)">Continue</button></div>
    </div>
  </ng-container>

  <ng-container *ngIf="step() === 3">
    <div class="card"><div class="card-b stack">
      <h2>Ready to submit</h2>
      <dl class="kv"><dt>Project</dt><dd>{{ projectName() }}</dd><dt>People</dt><dd>{{ selected().length }}</dd><dt>Window</dt><dd>{{ from | rxDate }} – {{ to ? (to | rxDate) : 'open ended' }} ({{ tagType }})</dd><dt>Approval</dt><dd>Line manager, then HRBP</dd></dl>
      <p class="muted small">Submitting creates pending tags and one approval request. People aren’t rostered until it’s approved.</p>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div class="row"><button type="button" class="btn" (click)="step.set(2)">Back</button><button type="button" class="btn primary" (click)="submit()" [disabled]="busy()">Submit for approval</button></div>
    </div></div>
  </ng-container>`,
})
export class TagFlowPage implements OnInit {
  private api = inject(Api); private route = inject(ActivatedRoute); private router = inject(Router); private toast = inject(Toasts); session = inject(Session);
  stepNames = ['Window', 'People', 'Conflicts', 'Confirm']; step = signal(0);
  projects = signal<J[]>([]); eligible = computed(() => this.projects().filter(p => p.status === 'Active'));
  projectId = ''; tagType = 'Primary'; from = ''; to = ''; today = signal(''); err = signal(''); busy = signal(false);
  ai = signal<J | null>(null); loadingAi = signal(false); selected = signal<J[]>([]); found = signal<J[]>([]); q = ''; csv = ''; csvRows = signal<J[]>([]); csvOk = computed(() => this.csvRows().filter(r => r.ok).length);
  checks = signal<J[]>([]); ackWarn = false;
  hasBlock = computed(() => this.checks().some(c => c.blocked)); hasWarn = computed(() => this.checks().some(c => c.violations.length && !c.blocked));
  projectName = () => this.projects().find(p => p.id === this.projectId)?.name ?? '';
  ngOnInit() {
    this.api.get('/api/lookups').subscribe(l => { this.projects.set(l.projects); this.today.set(l.today); this.from = l.today; const p = this.route.snapshot.queryParamMap.get('project'); if (p) this.projectId = p; });
  }
  toPick() { if (!this.projectId || !this.from) { this.err.set('Choose a project and a start date.'); return; } if (this.to && this.to < this.from) { this.err.set('The end date can’t be before the start date.'); return; } this.err.set(''); this.step.set(1); this.loadAi(); }
  loadAi() { this.loadingAi.set(true); this.api.get('/api/ai/employee-tagging', { projectId: this.projectId, from: this.from, to: this.to, limit: 10 }).subscribe({ next: a => { this.ai.set(a); this.loadingAi.set(false); }, error: () => this.loadingAi.set(false) }); }
  isSel(id: string) { return this.selected().some(s => s.id === id); }
  toggleCand(c: J, on: boolean) { on ? this.add({ id: c.employeeId, name: c.name, grade: c.grade, number: c.employeeNumber }) : this.remove(c.employeeId); }
  addTop(n: number) { for (const c of this.ai()!.payload.candidates.slice(0, n)) this.add({ id: c.employeeId, name: c.name, grade: c.grade, number: c.employeeNumber }); this.toast.info(`Added up to ${n} people. You can remove anyone before submitting.`); }
  add(e: J) { if (!this.isSel(e.id)) this.selected.update(s => [...s, e]); }
  remove(id: string) { this.selected.update(s => s.filter(x => x.id !== id)); }
  search() { if (this.q.trim().length < 2) { this.found.set([]); return; } this.api.get<J[]>('/api/employees', { q: this.q, take: 6 }).subscribe(r => this.found.set(r.filter(e => e.isShiftEligible && !this.isSel(e.id)))); }
  file(e: Event) { const f = (e.target as HTMLInputElement).files?.[0]; if (f) f.text().then(t => (this.csv = t)); }
  validateCsv() {
    const rows = this.csv.split('\n').map(l => l.trim()).filter(l => l && !l.toLowerCase().startsWith('employeenumber')).map(l => { const c = l.split(',').map(x => x.trim()); return { employeeNumber: c[0], from: c[1] || this.from, to: c[2] || null }; });
    this.api.post<J[]>('/api/employee-tags/bulk-validate', { projectId: this.projectId, rows, tagType: this.tagType }).subscribe({ next: r => this.csvRows.set(r), error: e => this.toast.error(e.title) });
  }
  addCsv() { for (const r of this.csvRows().filter(x => x.ok)) this.add({ id: r.employeeId, name: r.name, grade: '', number: r.employeeNumber, from: r.from }); this.toast.success(`${this.csvOk()} rows added.`); this.csvRows.set([]); this.csv = ''; }
  toReview() { this.api.post<J[]>('/api/employee-tags/validate', { projectId: this.projectId, employeeIds: this.selected().map(s => s.id), from: this.from, to: this.to || null, tagType: this.tagType }).subscribe({ next: c => { this.checks.set(c); this.step.set(2); }, error: e => this.toast.error(e.title) }); }
  submit() {
    this.busy.set(true);
    this.api.post('/api/employee-tags', { projectId: this.projectId, employeeIds: this.selected().map(s => s.id), from: this.from, to: this.to || null, tagType: this.tagType }).subscribe({
      next: r => { this.toast.success(`Submitted for approval. ${r.count} people will be tagged once it’s approved.`); void this.router.navigate(['/approvals', r.requestId]); },
      error: e => { this.busy.set(false); this.err.set(e.title); }
    });
  }
}
