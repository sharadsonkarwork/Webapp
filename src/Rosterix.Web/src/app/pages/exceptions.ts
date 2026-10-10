import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { AiPanel, Toasts } from '../core/ui.services';
import { RxSavedViews } from '../shared/tools';
import { Column, RxAiCard, RxConfidence, RxDialog, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxTable, RxWhy } from '../shared/ui';

@Component({
  selector: 'rx-exceptions', standalone: true, imports: [CommonModule, FormsModule, RxPageHeader, RxTable, RxStatus, RxIcon, RxDialog, RxSavedViews, RxConfidence],
  template: `
  <rx-page-header title="Exceptions" sub="Things the rules or the AI flagged. Highest severity first."/>
  <div class="card"><div class="card-h"><div class="row">
    <label class="field"><span class="sr-only">Category</span><select [(ngModel)]="f.category" name="c" aria-label="Category" (ngModelChange)="load()"><option value="">All categories</option><ng-container *ngFor="let c of cats"> <option [value]="c">{{ c }}</option> </ng-container></select></label>
    <label class="field"><span class="sr-only">Severity</span><select [(ngModel)]="f.severity" name="s" aria-label="Severity" (ngModelChange)="load()"><option value="">All severities</option><option>High</option><option>Medium</option><option>Low</option></select></label>
    <label class="field"><span class="sr-only">Status</span><select [(ngModel)]="f.status" name="st" aria-label="Status" (ngModelChange)="load()"><option value="">All statuses</option><option>Open</option><option>Acknowledged</option><option>Resolved</option></select></label>
    <label class="field"><span class="sr-only">Project</span><select [(ngModel)]="f.projectId" name="p" aria-label="Project" (ngModelChange)="load()"><option value="">All projects</option><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }}</option> </ng-container></select></label>
    <label class="check"><input type="checkbox" [(ngModel)]="f.assignedToMe" name="a" (ngModelChange)="load()"> Assigned to me</label>
  </div><div class="row"><rx-saved-views key="exceptions" [state]="f" (apply)="apply($event)"/>
    <button type="button" class="btn" [disabled]="!picked().length" (click)="bulk()">Acknowledge selected ({{ picked().length }})</button></div></div>
  <div class="card-b" style="padding:0"><rx-table [rows]="rows()" [columns]="cols" [selectable]="true" (selectionChange)="picked.set($event)" [rowTpl]="act" caption="Exceptions" [pageSize]="20" emptyTitle="No exceptions match" emptyText="Great news, or change the filters."/>
    <ng-template #act let-r><ng-container *ngIf="r.status !== 'Resolved'"> <button type="button" class="btn sm" (click)="resolving.set(r)">Resolve…</button> </ng-container></ng-template></div></div>

  <ng-container *ngIf="resolving() as r">
    <rx-dialog title="Resolve exception" (close)="resolving.set(null)">
      <p><rx-status [value]="r.severity"/> {{ r.description }}</p>
      <label class="field">How was it resolved?<textarea [(ngModel)]="resolution" name="res" rows="3" data-autofocus [attr.aria-invalid]="touched() && !resolution.trim()"></textarea></label>
      <ng-container *ngIf="touched() && !resolution.trim()"> <p class="err" role="alert">Say what you did so the next reviewer can trust it.</p> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="resolving.set(null)">Cancel</button><button type="button" class="btn primary" (click)="resolve()">Mark resolved</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class ExceptionsPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts);
  rows = signal<J[]>([]); projects = signal<J[]>([]); picked = signal<J[]>([]); resolving = signal<J | null>(null); touched = signal(false); resolution = '';
  f: J = { category: '', severity: '', status: 'Open', projectId: '', assignedToMe: false }; cats = ['RuleViolation', 'DataGap', 'Anomaly', 'Duplicate', 'RateMismatch'];
  cols: Column[] = [{ key: 'severity', label: 'Severity', type: 'chip' }, { key: 'category', label: 'Category' }, { key: 'description', label: 'What happened', wrap: true }, { key: 'project', label: 'Project' }, { key: 'employee', label: 'Employee' }, { key: 'status', label: 'Status', type: 'chip' }, { key: 'aiLabel', label: 'AI confidence' }];
  ngOnInit() { this.api.get('/api/lookups').subscribe(l => this.projects.set(l.projects)); this.load(); }
  apply(s: J) { this.f = { category: '', severity: '', status: '', projectId: '', assignedToMe: false, ...s }; this.load(); }
  load() { this.api.get<J[]>('/api/exceptions', this.f).subscribe(r => this.rows.set(r.map(e => ({ ...e, aiLabel: e.aiConfidence ? `${Math.round(e.aiConfidence * 100)}%` : '—' })))); }
  bulk() { this.api.post('/api/exceptions/bulk-acknowledge', { ids: this.picked().map(p => p.id) }).subscribe({ next: r => { this.toast.success(`${r.acknowledged} acknowledged.`); this.load(); this.picked.set([]); }, error: e => this.toast.error(e.title) }); }
  resolve() { this.touched.set(true); if (!this.resolution.trim()) return; this.api.post(`/api/exceptions/${this.resolving()!.id}/resolve`, { resolution: this.resolution }).subscribe({ next: () => { this.toast.success('Marked resolved.'); this.resolving.set(null); this.resolution = ''; this.touched.set(false); this.load(); }, error: e => this.toast.error(e.title) }); }
}

@Component({
  selector: 'rx-anomalies', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxAiCard, RxStatus, RxIcon, RxDialog, RxEmpty, RxConfidence, RxWhy],
  template: `
  <rx-page-header title="Anomaly dashboard" sub="An org-wide AI scan of allowance lines and rosters. It flags and explains; it never changes or blocks anything.">
    <label class="check"><input type="checkbox" [(ngModel)]="includeDismissed" name="d" (ngModelChange)="load()"> Show dismissed</label>
  </rx-page-header>
  <ng-container *ngIf="res() as r">
    <rx-ai-card heading="Scan result" [summary]="r.summary" [impact]="r.impact" [confidence]="r.confidence" [reasons]="r.reasons"/>
    <div class="grid-3" style="margin:16px 0" role="list">
      <ng-container *ngFor="let k of kpis()"> <div class="card kpi" role="listitem"><span class="v">{{ k.v }}</span><span class="l">{{ k.l }}</span></div> </ng-container>
    </div>
    <div class="stack">
      <ng-container *ngFor="let a of r.payload.items">
        <article class="card" [attr.aria-label]="a.kind + ' for ' + a.employeeName"><div class="card-b">
          <div class="row between"><div class="row"><rx-status [value]="a.severity"/><strong>{{ a.kind }}</strong><span class="muted">{{ a.employeeName }} · {{ a.projectName }}</span></div><div class="row"><rx-confidence [value]="a.confidence"/><ng-container *ngIf="a.status !== 'Open'"> <rx-status [value]="a.status === 'Dismissed' ? 'Dismissed' : 'Acknowledged'"/> </ng-container></div></div>
          <p style="margin:8px 0 4px">{{ a.description }}</p>
          <ul class="ai-reasons"><ng-container *ngFor="let e of a.evidence"> <li>{{ e.text }}</li> </ng-container></ul>
          <div class="row" style="margin-top:8px">
            <ng-container *ngIf="a.status === 'Open'"> <button type="button" class="btn sm" (click)="ack(a)">Acknowledge</button><button type="button" class="btn sm ghost" (click)="dismissing.set(a)"><rx-icon name="x" [size]="14"/>Dismiss with reason…</button> </ng-container>
            <rx-why [reasons]="a.evidence"/>
            <ng-container *ngIf="a.lineId"> <a class="btn sm ghost" routerLink="/allowance">Open allowance</a> </ng-container>
          </div></div></article>
      </ng-container><ng-container *ngIf="!(r.payload.items)?.length"> <rx-empty title="No anomalies" text="The scan found nothing unusual."/> </ng-container>
    </div>
  </ng-container><ng-container *ngIf="!(res())"> <p class="muted" role="status">Scanning…</p> </ng-container>

  <ng-container *ngIf="dismissing() as a">
    <rx-dialog title="Dismiss this anomaly" (close)="dismissing.set(null)">
      <p>{{ a.kind }}: {{ a.employeeName }}. {{ a.description }}</p>
      <label class="field">Why is this normal?<textarea [(ngModel)]="reason" name="r" rows="3" data-autofocus [attr.aria-invalid]="touched() && !reason.trim()" aria-describedby="rh"></textarea><span class="hint" id="rh">Your reason is kept and helps tune future scans.</span></label>
      <ng-container *ngIf="touched() && !reason.trim()"> <p class="err" role="alert">A reason is required to dismiss.</p> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="dismissing.set(null)">Cancel</button><button type="button" class="btn primary" (click)="dismiss()">Dismiss</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class AnomaliesPage implements OnInit, OnDestroy {
  private api = inject(Api); private toast = inject(Toasts); private panel = inject(AiPanel);
  res = signal<J | null>(null); dismissing = signal<J | null>(null); touched = signal(false); reason = ''; includeDismissed = false;
  kpis = computed(() => { const p = this.res()?.payload; return p ? [{ v: p.outlierCount, l: 'Statistical outliers (z ≥ 2.5)' }, { v: p.duplicateCount, l: 'Duplicate claims' }, { v: p.rateMismatchCount, l: 'Rate mismatches' }, { v: p.impossibleCount, l: 'Impossible hours' }] : []; });
  ngOnInit() { this.load(); }
  ngOnDestroy() { this.panel.clear('anomalies'); }
  load() { this.api.get('/api/ai/anomalies', { includeDismissed: this.includeDismissed }).subscribe({ next: r => { this.res.set(r); this.panel.publish('anomalies', [{ key: 'a', kind: 'suggestion', title: 'Anomaly scan (AI-UC-09)', summary: r.summary, impact: r.impact, confidence: r.confidence, reasons: r.reasons }]); }, error: e => this.toast.error(e.title) }); }
  act(a: J, action: 'Accept' | 'Dismiss', reason?: string) { return this.api.post('/api/ai/feedback', { useCaseCode: 'AI-UC-09', contextType: 'Anomaly', contextId: a.id, action, reason, confidence: a.confidence }); }
  ack(a: J) { this.act(a, 'Accept').subscribe({ next: () => { this.toast.success('Acknowledged.'); this.load(); }, error: e => this.toast.error(e.title) }); }
  dismiss() { this.touched.set(true); if (!this.reason.trim()) return; const a = this.dismissing()!; this.act(a, 'Dismiss', this.reason).subscribe({ next: () => { this.toast.success('Dismissed. Your reason was recorded.'); this.dismissing.set(null); this.reason = ''; this.touched.set(false); this.load(); }, error: e => this.toast.error(e.title) }); }
}
