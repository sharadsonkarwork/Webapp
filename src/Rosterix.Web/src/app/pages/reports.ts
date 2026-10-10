import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, J, download } from '../core/api.service';
import { Session } from '../core/auth.service';
import { Toasts } from '../core/ui.services';
import { DatePipeRx, InrPipe, MonthPipe } from '../shared/fmt';
import { RxColumnChooser, RxSavedViews } from '../shared/tools';
import { Column, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxTable } from '../shared/ui';

@Component({
  selector: 'rx-reports', standalone: true, imports: [CommonModule, FormsModule, RxPageHeader, RxTable, RxIcon, RxSavedViews, RxColumnChooser],
  template: `
  <rx-page-header title="Reports" sub="Everything is scoped to what your role can see. Large reports are paged on the server and can be exported."/>
  <div class="grid-2" style="grid-template-columns:240px minmax(0,1fr);align-items:start">
    <nav class="card" aria-label="Report list"><div class="card-b" style="padding:6px">
      <ng-container *ngFor="let r of catalogue()"> <button type="button" class="btn ghost" style="width:100%;justify-content:flex-start" [attr.aria-current]="r.name === name ? 'page' : null" [style.background]="r.name === name ? 'var(--rx-action-subtle)' : ''" (click)="pick(r.name)">{{ r.title }}</button> </ng-container>
    </div></nav>
    <section class="card" [attr.aria-label]="title()"><div class="card-h"><h2 style="font-size:16px">{{ title() }}</h2>
      <div class="row"><button type="button" class="btn sm" (click)="export('csv')"><rx-icon name="download" [size]="14"/>CSV</button><button type="button" class="btn sm" (click)="export('xlsx')"><rx-icon name="download" [size]="14"/>Excel</button></div></div>
      <div class="card-b stack">
        <div class="row">
          <label class="field" style="flex-direction:row;align-items:center;gap:6px">Month<input type="month" [(ngModel)]="month" name="m" (ngModelChange)="reload()" aria-label="Month"></label>
          <label class="field" style="flex-direction:row;align-items:center;gap:6px">Project<select [(ngModel)]="projectId" name="p" (ngModelChange)="reload()" aria-label="Project"><option value="">All</option><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }}</option> </ng-container></select></label>
          <label class="field"><span class="sr-only">Search</span><input type="search" [(ngModel)]="q" name="q" placeholder="Search rows" aria-label="Search rows" (keydown.enter)="reload()" (change)="reload()"></label>
          <rx-saved-views [key]="'reports.' + name" [state]="{ month, projectId, q, hidden }" (apply)="apply($event)"/><rx-column-chooser [columns]="cols()" [hidden]="hidden" (hiddenChange)="hidden = $event; "/>
        </div>
        <rx-table [rows]="rows()" [columns]="cols()" [hiddenKeys]="hidden" [paged]="false" [serverSide]="true" (sortChange)="sortChange($event)" [caption]="title()" emptyTitle="No rows" emptyText="Try a different month or project."/>
        <div class="pager"><span class="muted small" aria-live="polite">{{ total() ? ((page - 1) * pageSize + 1) + '–' + min(page * pageSize, total()) + ' of ' + total() : '0 rows' }}</span>
          <label class="field" style="flex-direction:row;align-items:center;gap:6px"><span class="small">Rows</span><select [(ngModel)]="pageSize" name="ps" (ngModelChange)="page = 1; reload()" aria-label="Rows per page"><option [ngValue]="10">10</option><option [ngValue]="25">25</option><option [ngValue]="50">50</option><option [ngValue]="100">100</option></select></label>
          <button type="button" class="btn sm" [disabled]="page === 1" (click)="page = page - 1; reload()">Previous</button><button type="button" class="btn sm" [disabled]="page * pageSize >= total()" (click)="page = page + 1; reload()">Next</button></div>
      </div></section>
  </div>`,
})
export class ReportsPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts); session = inject(Session);
  catalogue = signal<J[]>([]); name = 'allowance-summary'; projects = signal<J[]>([]); month = ''; projectId = ''; q = ''; page = 1; pageSize = 25; sort = ''; dir = 'asc'; hidden: string[] = [];
  data = signal<J | null>(null); cols = computed<Column[]>(() => (this.data()?.columns ?? []).map((c: J) => ({ key: c.key, label: c.label, type: c.type === 'number' ? 'num' : c.type === 'money' ? 'money' : 'text' }))); rows = computed(() => this.data()?.rows ?? []); total = computed(() => this.data()?.total ?? 0);
  title = () => this.catalogue().find(c => c.name === this.name)?.title ?? 'Report'; min = Math.min;
  ngOnInit() { this.api.get<J[]>('/api/reports').subscribe(c => { this.catalogue.set(c); this.reload(); }); this.api.get('/api/lookups').subscribe(l => this.projects.set(l.projects)); }
  pick(n: string) { this.name = n; this.page = 1; this.sort = ''; this.hidden = []; this.reload(); }
  apply(s: J) { this.month = s.month ?? ''; this.projectId = s.projectId ?? ''; this.q = s.q ?? ''; this.hidden = s.hidden ?? []; this.page = 1; this.reload(); }
  sortChange(e: J) { this.sort = e.key; this.dir = e.dir; this.reload(); }
  private params(extra: J = {}) { const visible = this.cols().filter(c => !this.hidden.includes(c.key)).map(c => c.key).join(','); return { month: this.month, projectId: this.projectId, q: this.q, sort: this.sort, dir: this.dir, page: this.page, pageSize: this.pageSize, columns: this.hidden.length ? visible : '', ...extra }; }
  reload() { this.api.get(`/api/reports/${this.name}`, this.params()).subscribe({ next: d => this.data.set(d), error: e => this.toast.error(e.title) }); }
  export(format: 'csv' | 'xlsx') { this.api.blob(`/api/reports/${this.name}`, this.params({ format, page: 1, pageSize: 200 })).subscribe({ next: r => { download(r.body!, `${this.name}.${format}`); this.toast.success(`Exported ${this.title()} as ${format.toUpperCase()}. Nothing is stored on the server.`); }, error: e => this.toast.error(e.title) }); }
}

@Component({
  selector: 'rx-payroll', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxStatus, RxIcon, RxTable, RxEmpty, InrPipe, DatePipeRx, MonthPipe],
  template: `
  <rx-page-header title="Payroll" sub="Rosterix assembles an approved batch and hands it to CNB. It does not calculate payroll."/>
  <div class="tabs" role="tablist" aria-label="Payroll sections">
    <ng-container *ngIf="session.has('HRSS')"> <button type="button" role="tab" id="pt-s" [attr.aria-selected]="tab() === 'submit'" aria-controls="pp-s" [tabindex]="tab()==='submit'?0:-1" (click)="tab.set('submit')">Submit a batch</button> </ng-container>
    <button type="button" role="tab" id="pt-b" [attr.aria-selected]="tab() === 'batches'" aria-controls="pp-b" [tabindex]="tab()==='batches'?0:-1" (click)="tab.set('batches')">Submitted batches</button>
  </div>
  <ng-container *ngIf="tab() === 'submit' && session.has('HRSS')">
    <div id="pp-s" role="tabpanel" aria-labelledby="pt-s" class="stack" style="margin-top:14px">
      <label class="field" style="max-width:220px">Payroll month<input type="month" [(ngModel)]="month" name="m" (ngModelChange)="check()" aria-label="Payroll month"></label>
      <ng-container *ngIf="cl() as c">
        <section class="card" aria-labelledby="chk"><div class="card-h"><h2 id="chk" style="font-size:16px">Pre-submission checklist</h2><rx-status [value]="c.canSubmit ? 'Pass' : 'Fail'" [label]="c.canSubmit ? 'Ready to submit' : 'Not ready'"/></div>
          <div class="card-b" style="padding:0"><ng-container *ngFor="let k of c.checks"> <div class="queue-item" style="grid-template-columns:auto 1fr auto;align-items:start"><rx-status [value]="k.status"/><div><strong>{{ k.label }}</strong><div class="small muted">{{ k.detail }}</div></div><ng-container *ngIf="k.link && k.status !== 'Pass'"> <a class="btn sm" [routerLink]="k.link">Resolve</a> </ng-container></div> </ng-container></div></section>
        <section class="card" aria-labelledby="rn"><div class="card-h"><h2 id="rn" style="font-size:16px">Runs for {{ month | rxMonth }}</h2></div>
          <div class="card-b" style="padding:0"><ng-container *ngFor="let r of c.runs"> <div class="queue-item" style="grid-template-columns:auto 1fr auto auto"><input type="checkbox" [disabled]="!r.selectable" [checked]="picked().includes(r.id)" (change)="toggle(r.id, $any($event.target).checked)" [attr.aria-label]="'Include ' + r.project"><div><strong>{{ r.project }}</strong><div class="small muted">{{ r.lineCount }} lines</div></div><span class="num">{{ r.totalAmount | inr }}</span><rx-status [value]="r.status"/></div> </ng-container> <ng-container *ngIf="!c.runs.length"> <rx-empty title="No runs for this month"/> </ng-container></div></section>
        <ng-container *ngIf="hasWarn()"> <label class="check"><input type="checkbox" [(ngModel)]="ack" name="ack"> I’ve read the warnings above and want to continue.</label> </ng-container>
        <div class="row"><button type="button" class="btn primary" [disabled]="!c.canSubmit || !picked().length || (hasWarn() && !ack) || busy()" (click)="submit()">Submit {{ picked().length }} run{{ picked().length === 1 ? '' : 's' }} ({{ total() | inr }}) to payroll</button><span class="small muted">Submitted runs become read-only. A later rate change never rewrites them.</span></div>
      </ng-container>
    </div>
  </ng-container><ng-container *ngIf="!(tab() === 'submit' && session.has('HRSS'))">
    <div id="pp-b" role="tabpanel" aria-labelledby="pt-b" style="margin-top:14px" class="card"><div class="card-b" style="padding:0">
      <ng-container *ngFor="let b of batches()"> <div class="queue-item" style="grid-template-columns:1fr auto"><div><div class="t">{{ b.month | rxMonth }}: {{ b.totalAmount | inr }}</div><div class="small muted">{{ b.lineCount }} lines · <span class="mono">{{ b.fileRef }}</span> · submitted by {{ b.submittedBy }} on {{ b.submittedAtUtc | rxDate }}</div><div class="small"><ng-container *ngFor="let r of b.runs"> <span class="chip neutral" style="margin-right:4px">{{ r.project }}</span> </ng-container></div></div><rx-status [value]="b.status"/></div> </ng-container>
      <ng-container *ngIf="!batches().length"> <rx-empty title="No batches yet" text="Submitted batches and their CNB status appear here."/> </ng-container></div></div>
  </ng-container>`,
})
export class PayrollPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts); session = inject(Session);
  tab = signal<'submit' | 'batches'>('batches'); month = ''; cl = signal<J | null>(null); picked = signal<string[]>([]); ack = false; busy = signal(false); batches = signal<J[]>([]);
  hasWarn = computed(() => (this.cl()?.checks ?? []).some((k: J) => k.status === 'Warn'));
  total = computed(() => (this.cl()?.runs ?? []).filter((r: J) => this.picked().includes(r.id)).reduce((s: number, r: J) => s + r.totalAmount, 0));
  ngOnInit() { this.tab.set(this.session.has('HRSS') ? 'submit' : 'batches'); this.api.get('/api/lookups').subscribe(l => { const d = new Date(l.today); d.setMonth(d.getMonth() - 1); this.month = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`; if (this.session.has('HRSS')) this.check(); }); this.loadBatches(); }
  loadBatches() { this.api.get<J[]>('/api/payroll/batches').subscribe(b => this.batches.set(b)); }
  check() { if (!/^\d{4}-\d{2}$/.test(this.month)) return; this.api.get('/api/payroll/checklist', { month: this.month }).subscribe({ next: c => { this.cl.set(c); this.picked.set(c.runs.filter((r: J) => r.selectable).map((r: J) => r.id)); this.ack = false; }, error: e => this.toast.error(e.title) }); }
  toggle(id: string, on: boolean) { this.picked.update(p => (on ? [...p, id] : p.filter(x => x !== id))); }
  submit() { this.busy.set(true); this.api.post('/api/payroll/batches', { month: this.month, runIds: this.picked(), acknowledgeWarnings: this.ack }).subscribe({ next: r => { this.busy.set(false); this.toast.success(`Submitted batch ${r.fileRef}.`); this.tab.set('batches'); this.loadBatches(); this.check(); }, error: e => { this.busy.set(false); this.toast.error(e.title); this.check(); } }); }
}
