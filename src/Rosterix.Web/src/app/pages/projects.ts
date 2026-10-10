import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { AiPanel, Toasts } from '../core/ui.services';
import { DatePipeRx, DateTimePipeRx, InrPipe } from '../shared/fmt';
import { RxColumnChooser, RxSavedViews } from '../shared/tools';
import { Column, RxAiCard, RxAiSkeleton, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxTable } from '../shared/ui';
import { Location } from '@angular/common';

@Component({
  selector: 'rx-projects', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxTable, RxSavedViews, RxColumnChooser, RxIcon, RxStatus],
  template: `
  <rx-page-header title="Projects" sub="Create projects, tag them as shift projects, and see their teams.">
    <ng-container *ngIf="session.has('PM')"> <a class="btn primary" routerLink="/projects/new"><rx-icon name="plus"/>New project</a> </ng-container>
  </rx-page-header>
  <div class="card"><div class="card-h">
    <div class="row">
      <label class="field"><span class="sr-only">Search projects</span><input type="search" [(ngModel)]="q" placeholder="Search name or code" aria-label="Search projects" (ngModelChange)="touch()"></label>
      <label class="field"><span class="sr-only">Status</span><select [(ngModel)]="status" aria-label="Filter by status" (ngModelChange)="touch()"><option value="">All statuses</option><ng-container *ngFor="let s of statuses"> <option [value]="s">{{ s }}</option> </ng-container></select></label>
      <label class="field"><span class="sr-only">Business unit</span><select [(ngModel)]="bu" aria-label="Filter by business unit" (ngModelChange)="touch()"><option value="">All business units</option><ng-container *ngFor="let b of bus()"> <option [value]="b">{{ b }}</option> </ng-container></select></label>
    </div>
    <div class="row"><rx-saved-views key="projects" [state]="{ q, status, bu, hidden }" (apply)="applyView($event)"/><rx-column-chooser [columns]="cols" [hidden]="hidden" (hiddenChange)="hidden = $event"/></div>
  </div>
  <div class="card-b" style="padding:0">
    <rx-table [rows]="filtered()" [columns]="cols" [hiddenKeys]="hidden" [rowTpl]="act" caption="Projects" emptyTitle="No projects match" emptyText="Clear a filter or create a new project."/>
    <ng-template #act let-r><a class="btn sm" [routerLink]="['/projects', r.id]" [attr.aria-label]="'Open ' + r.name">Open</a></ng-template>
  </div></div>`,
})
export class ProjectsPage implements OnInit {
  private api = inject(Api); session = inject(Session);
  items = signal<J[]>([]); q = ''; status = ''; bu = ''; hidden: string[] = ['costCentre']; private tick = signal(0);
  statuses = ['Draft', 'PendingApproval', 'Active', 'Suspended', 'Closed'];
  cols: Column[] = [{ key: 'code', label: 'Code', type: 'mono' }, { key: 'name', label: 'Project' }, { key: 'businessUnit', label: 'Business unit' }, { key: 'owner', label: 'Owner' }, { key: 'status', label: 'Status', type: 'chip' }, { key: 'isShiftLabel', label: 'Shift project' }, { key: 'teamSize', label: 'Team', type: 'num' }, { key: 'pendingTags', label: 'Pending tags', type: 'num' }, { key: 'startDate', label: 'Start', type: 'date' }, { key: 'endDate', label: 'End', type: 'date' }, { key: 'costCentre', label: 'Cost centre', type: 'mono' }];
  bus = computed(() => [...new Set(this.items().map(i => i.businessUnit))]);
  filtered = computed(() => { this.tick(); const q = this.q.toLowerCase(); return this.items().filter(i => (!this.status || i.status === this.status) && (!this.bu || i.businessUnit === this.bu) && (!q || i.name.toLowerCase().includes(q) || i.code.toLowerCase().includes(q))); });
  touch() { this.tick.update(n => n + 1); }
  applyView(s: J) { this.q = s.q ?? ''; this.status = s.status ?? ''; this.bu = s.bu ?? ''; this.hidden = s.hidden ?? ['costCentre']; this.touch(); }
  ngOnInit() { this.api.get<J[]>('/api/projects').subscribe(r => this.items.set(r.map(p => ({ ...p, isShiftLabel: p.isShiftProject ? 'Yes' : 'No' })))); }
}

@Component({
  selector: 'rx-project-form', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxAiCard, RxAiSkeleton, RxIcon],
  template: `
  <rx-page-header [title]="editing ? 'Edit project' : 'New project'" sub="A new project starts as a draft. Tag it as a shift project when you’re ready to roster people."/>
  <div class="grid-2" style="align-items:start">
    <form class="card" (ngSubmit)="save()" #f="ngForm" novalidate aria-label="Project details"><div class="card-b stack">
      <ng-container *ngIf="errors().length"> <div class="banner danger" role="alert" tabindex="-1" id="errs"><rx-icon name="alert"/><div><strong>Fix {{ errors().length }} thing{{ errors().length === 1 ? '' : 's' }} to continue:</strong><ul style="margin:4px 0 0 18px;padding:0"><ng-container *ngFor="let e of errors()"> <li>{{ e }}</li> </ng-container></ul></div></div> </ng-container>
      <div class="form-grid">
        <label class="field">Project name<input type="text" name="name" [(ngModel)]="m.name" required maxlength="120" [attr.aria-invalid]="touched() && !m.name" aria-describedby="n-h"><span class="hint" id="n-h">Up to 120 characters.</span></label>
        <label class="field">Business unit<select name="bu" [(ngModel)]="m.businessUnitId" (ngModelChange)="onBu()" [disabled]="editing" required><ng-container *ngFor="let b of lookups()?.businessUnits ?? []"> <option [value]="b.id">{{ b.name }}</option> </ng-container></select></label>
        <label class="field">Start date<input type="date" name="start" [(ngModel)]="m.startDate" required [attr.aria-invalid]="touched() && !m.startDate"></label>
        <label class="field">End date <span class="hint">optional</span><input type="date" name="end" [(ngModel)]="m.endDate"></label>
        <label class="field">Cost centre<input type="text" name="cc" [(ngModel)]="m.costCentre" required placeholder="CC-4101" [attr.aria-invalid]="touched() && !m.costCentre"></label>
      </div>
      <label class="check"><input type="checkbox" name="shift" [(ngModel)]="m.isShiftProject"> This is a shift project</label>
      <ng-container *ngIf="m.isShiftProject">
        <fieldset><legend>Shift types used</legend><div class="row">
          <ng-container *ngFor="let s of lookups()?.shiftTypes ?? []"> <ng-container *ngIf="s.isActive"> <label class="check"><input type="checkbox" [checked]="m.shiftTypeIds.includes(s.id)" (change)="toggleShift(s.id, $any($event.target).checked)"> {{ s.name }} <span class="muted xs">{{ s.start }}–{{ s.end }}<ng-container *ngIf="s.isAllowanceEligible"> · allowance </ng-container></span></label> </ng-container> </ng-container>
        </div></fieldset>
      </ng-container>
      <div class="row"><button type="submit" class="btn primary" [disabled]="busy()">{{ editing ? 'Save changes' : 'Create draft project' }}</button><a class="btn ghost" routerLink="/projects">Cancel</a></div>
    </div></form>

    <aside aria-label="AI suggestion">
      <ng-container *ngIf="loadingAi()"> <rx-ai-skeleton/> </ng-container><ng-container *ngIf="!(loadingAi())"><ng-container *ngIf="sug() as s">
        <ng-container *ngIf="!dismissed()">
          <rx-ai-card heading="Pre-fill from your last projects" [summary]="s.summary" [impact]="s.impact" [confidence]="s.confidence" [reasons]="s.reasons"
            acceptLabel="Apply to form" dismissLabel="Dismiss" (accept)="apply()" (dismiss)="dismiss()">
            <dl class="kv small" style="margin-top:6px"><dt>Shifts</dt><dd>{{ s.payload.shiftTypeNames.join(', ') || '—' }}</dd><dt>Duration</dt><dd>{{ s.payload.durationMonths }} months</dd><dt>Cost centre</dt><dd class="mono">{{ s.payload.costCentre || '—' }}</dd></dl>
          </rx-ai-card>
        </ng-container><ng-container *ngIf="!(!dismissed())"> <p class="muted small">Suggestion dismissed. <button type="button" class="btn sm ghost" (click)="dismissed.set(false)">Show it again</button></p> </ng-container>
      </ng-container></ng-container>
    </aside>
  </div>`,
})
export class ProjectFormPage implements OnInit {
  private api = inject(Api); private router = inject(Router); private route = inject(ActivatedRoute); private toast = inject(Toasts); private session = inject(Session);
  lookups = signal<J | null>(null); errors = signal<string[]>([]); touched = signal(false); busy = signal(false); loadingAi = signal(false); sug = signal<J | null>(null); dismissed = signal(false);
  editing = false; id = '';
  m: J = { name: '', businessUnitId: '', startDate: '', endDate: '', costCentre: '', shiftTypeIds: [] as string[], isShiftProject: true };
  ngOnInit() {
    this.id = this.route.snapshot.paramMap.get('id') ?? ''; this.editing = !!this.id;
    this.api.get('/api/lookups').subscribe(l => {
      this.lookups.set(l);
      if (this.editing) this.api.get(`/api/projects/${this.id}`).subscribe(d => { const p = d.project; this.m = { name: p.name, businessUnitId: p.businessUnitId, startDate: p.startDate, endDate: p.endDate ?? '', costCentre: p.costCentre, isShiftProject: p.isShiftProject, shiftTypeIds: (l.shiftTypes as J[]).filter(s => p.shiftTypes.includes(s.code)).map(s => s.id) }; });
      else { this.m.businessUnitId = this.session.me()?.businessUnit?.id ?? l.businessUnits[0]?.id; this.onBu(); }
    });
  }
  onBu() { if (this.editing || !this.m.businessUnitId) return; this.loadingAi.set(true); this.sug.set(null); this.api.get('/api/ai/project-setup', { businessUnitId: this.m.businessUnitId }).subscribe({ next: s => { this.sug.set(s); this.loadingAi.set(false); }, error: () => this.loadingAi.set(false) }); }
  toggleShift(id: string, on: boolean) { this.m.shiftTypeIds = on ? [...this.m.shiftTypeIds, id] : this.m.shiftTypeIds.filter((x: string) => x !== id); }
  apply() {
    const p = this.sug()!.payload; this.m.shiftTypeIds = [...p.shiftTypeIds]; this.m.costCentre = p.costCentre; this.m.isShiftProject = p.isShiftProject;
    if (this.m.startDate) { const d = new Date(this.m.startDate); d.setMonth(d.getMonth() + p.durationMonths); this.m.endDate = d.toISOString().slice(0, 10); }
    this.toast.info('Applied. Review each field before saving; nothing is saved yet.');
    this.api.post('/api/ai/feedback', { useCaseCode: 'AI-UC-01', contextType: 'Project', contextId: this.m.businessUnitId, action: 'Accept', confidence: this.sug()!.confidence, summary: this.sug()!.summary }).subscribe();
  }
  dismiss() { this.dismissed.set(true); this.api.post('/api/ai/feedback', { useCaseCode: 'AI-UC-01', contextType: 'Project', contextId: this.m.businessUnitId, action: 'Dismiss', confidence: this.sug()!.confidence }).subscribe(); }
  save() {
    this.touched.set(true); const e: string[] = [];
    if (!this.m.name?.trim()) e.push('Enter a project name.'); if (!this.m.startDate) e.push('Choose a start date.'); if (!this.m.costCentre?.trim()) e.push('Enter a cost centre, for example CC-4101.');
    if (this.m.endDate && this.m.endDate < this.m.startDate) e.push('The end date can’t be before the start date.'); if (this.m.isShiftProject && this.m.shiftTypeIds.length === 0) e.push('Choose at least one shift type for a shift project.');
    this.errors.set(e); if (e.length) { setTimeout(() => document.getElementById('errs')?.focus()); return; }
    this.busy.set(true); const body = { ...this.m, endDate: this.m.endDate || null };
    const req = this.editing ? this.api.put(`/api/projects/${this.id}`, body) : this.api.post('/api/projects', body);
    req.subscribe({ next: r => { this.toast.success(this.editing ? 'Project saved.' : 'Draft project created.'); void this.router.navigate(['/projects', this.editing ? this.id : r.id]); }, error: er => { this.busy.set(false); this.errors.set([er.title]); setTimeout(() => document.getElementById('errs')?.focus()); } });
  }
}

@Component({
  selector: 'rx-project-detail', standalone: true, imports: [CommonModule, RouterLink, RxPageHeader, RxStatus, RxTable, RxIcon, DatePipeRx, DateTimePipeRx],
  template: `
  <ng-container *ngIf="d() as d">
    <rx-page-header [title]="d.project.name" [sub]="d.project.code + ' · ' + d.project.businessUnit + ' · owner ' + d.project.owner">
      <ng-container *ngIf="d.canEdit"> <a class="btn" [routerLink]="['/projects', d.project.id, 'edit']"><rx-icon name="edit"/>Edit</a> </ng-container>
      <ng-container *ngIf="d.canEdit && (d.project.status === 'Draft' || (d.project.status === 'Active' && !d.shiftTags.length))"> <button type="button" class="btn primary" (click)="shiftTag()" [disabled]="busy()">Tag as shift project</button> </ng-container>
      <ng-container *ngIf="session.has('PM','LM') && d.project.status === 'Active'"> <a class="btn" routerLink="/tagging/new" [queryParams]="{ project: d.project.id }">Tag employees</a> </ng-container>
      <ng-container *ngIf="session.has('PM','LM','HRSS') && d.project.isShiftProject"> <a class="btn" routerLink="/roster" [queryParams]="{ project: d.project.id }">Open roster</a> </ng-container>
    </rx-page-header>
    <div class="row" style="margin-bottom:12px"><rx-status [value]="d.project.status"/><span class="small muted">{{ d.project.startDate | rxDate }} – {{ d.project.endDate ? (d.project.endDate | rxDate) : 'open ended' }} · cost centre <span class="mono">{{ d.project.costCentre }}</span></span></div>
    <ng-container *ngIf="d.project.status === 'PendingApproval'"> <div class="banner info" style="margin-bottom:12px"><rx-icon name="clock"/><span>Shift tagging is waiting for approval (line manager, then HRBP, then Regional Head).</span></div> </ng-container>
    <div class="tabs" role="tablist" aria-label="Project sections">
      <ng-container *ngFor="let t of tabs"> <button type="button" role="tab" [id]="'tab-' + t" [attr.aria-selected]="tab() === t" [attr.aria-controls]="'panel-' + t" [tabindex]="tab() === t ? 0 : -1" (click)="tab.set(t)" (keydown)="key($event)">{{ t }}</button> </ng-container>
    </div>
    <div role="tabpanel" [id]="'panel-' + tab()" [attr.aria-labelledby]="'tab-' + tab()" style="margin-top:14px" tabindex="0">
      <ng-container [ngSwitch]="tab()"><ng-container *ngSwitchCase="'Team'"> <div class="card"><rx-table [rows]="team()" [columns]="teamCols" caption="Employee tags on this project" emptyTitle="No one is tagged yet" emptyText="Use “Tag employees” to propose people."/></div> </ng-container><ng-container *ngSwitchCase="'Shift tagging'"> <div class="card"><div class="card-b">
          <ng-container *ngFor="let t of d.shiftTags"> <div class="row between" style="padding:6px 0;border-bottom:1px solid var(--rx-border)"><div><strong>{{ t.shiftTypes.join(', ') }}</strong><div class="small muted">{{ t.validFrom | rxDate }} – {{ t.validTo ? (t.validTo | rxDate) : 'open ended' }}</div></div><rx-status [value]="t.status"/></div> </ng-container>
          <ng-container *ngIf="!d.shiftTags.length"> <p class="muted">This project hasn’t been tagged as a shift project yet.</p> </ng-container></div></div> </ng-container><ng-container *ngSwitchCase="'History'">
          <ol class="timeline"><ng-container *ngFor="let e of history()"> <li [class.done]="e.kind !== 'Created'"><div><strong>{{ e.title }}</strong> <span class="small muted">{{ e.at | rxDateTime }}</span></div><div class="small">{{ e.detail }} <ng-container *ngIf="e.link"> <a [routerLink]="e.link.split('?')[0]">Open</a> </ng-container></div></li> </ng-container></ol> </ng-container><ng-container *ngSwitchDefault>
          <dl class="kv card" style="padding:16px"><dt>Business unit</dt><dd>{{ d.project.businessUnit }}</dd><dt>Owner</dt><dd>{{ d.project.owner }}</dd><dt>Shift project</dt><dd>{{ d.project.isShiftProject ? 'Yes' : 'No' }}</dd><dt>Shift types</dt><dd>{{ d.project.shiftTypes.join(', ') || '—' }}</dd><dt>Team size today</dt><dd class="mono">{{ d.project.teamSize }}</dd><dt>Pending tags</dt><dd class="mono">{{ d.project.pendingTags }}</dd></dl> </ng-container></ng-container>
    </div>
  </ng-container><ng-container *ngIf="!(d())"> <p class="muted" role="status">Loading project…</p> </ng-container>`,
})
export class ProjectDetailPage implements OnInit {
  private api = inject(Api); private route = inject(ActivatedRoute); private toast = inject(Toasts); private router = inject(Router); session = inject(Session);
  d = signal<J | null>(null); history = signal<J[]>([]); tab = signal('Overview'); busy = signal(false); tabs = ['Overview', 'Team', 'Shift tagging', 'History'];
  teamCols: Column[] = [{ key: 'name', label: 'Employee' }, { key: 'number', label: 'No.', type: 'mono' }, { key: 'grade', label: 'Grade' }, { key: 'type', label: 'Type' }, { key: 'validFrom', label: 'From', type: 'date' }, { key: 'validTo', label: 'To', type: 'date' }, { key: 'status', label: 'Status', type: 'chip' }];
  team = computed(() => (this.d()?.tags ?? []).map((t: J) => ({ id: t.id, name: t.employee.name, number: t.employee.number, grade: t.employee.grade, type: t.type, validFrom: t.validFrom, validTo: t.validTo, status: t.status })));
  ngOnInit() { this.load(); }
  load() { const id = this.route.snapshot.paramMap.get('id'); this.api.get(`/api/projects/${id}`).subscribe({ next: d => this.d.set(d), error: () => void this.router.navigateByUrl('/projects') }); this.api.get<J[]>(`/api/projects/${id}/history`).subscribe({ next: h => this.history.set(h), error: () => {} }); }
  key(e: KeyboardEvent) { const i = this.tabs.indexOf(this.tab()); const n = e.key === 'ArrowRight' ? (i + 1) % this.tabs.length : e.key === 'ArrowLeft' ? (i + this.tabs.length - 1) % this.tabs.length : -1; if (n >= 0) { this.tab.set(this.tabs[n]); setTimeout(() => document.getElementById('tab-' + this.tabs[n])?.focus()); } }
  shiftTag() { this.busy.set(true); this.api.post(`/api/projects/${this.d()!.project.id}/shift-tag`).subscribe({ next: () => { this.toast.success('Sent for approval: line manager, then HRBP, then Regional Head.'); this.busy.set(false); this.load(); }, error: e => { this.toast.error(e.title); this.busy.set(false); } }); }
}
