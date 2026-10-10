import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { AiPanel, Toasts } from '../core/ui.services';
import { DatePipeRx, DateTimePipeRx, InrPipe } from '../shared/fmt';
import { RxAiCard, RxConfidence, RxDialog, RxEmpty, RxIcon, RxImpact, RxPageHeader, RxStatus, RxWhy } from '../shared/ui';

const KEY = 'rx.reviewed';
const reviewed = (): Set<string> => { try { return new Set(JSON.parse(sessionStorage.getItem(KEY) ?? '[]')); } catch { return new Set(); } };
const markReviewed = (id: string) => { try { const s = reviewed(); s.add(id); sessionStorage.setItem(KEY, JSON.stringify([...s])); } catch { /* review tracking is per tab */ } };
const TYPE_LABEL: Record<string, string> = { ProjectShiftTagging: 'Shift tagging', EmployeeTagging: 'Employee tagging', AllowanceCompensation: 'Compensation', Arrear: 'Arrear', Movement: 'Movement', Exception: 'Exception' };

@Component({
  selector: 'rx-approvals', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxStatus, RxIcon, RxDialog, RxEmpty, RxAiCard, DatePipeRx, InrPipe],
  template: `
  <rx-page-header title="Approvals" sub="Requests waiting for you. The riskiest are listed first, and you can change the order any time."/>
  <div class="tabs" role="tablist" aria-label="Approval lists">
    <button type="button" role="tab" id="t-q" [attr.aria-selected]="tab() === 'queue'" aria-controls="p-q" [tabindex]="tab()==='queue'?0:-1" (click)="tab.set('queue')">My queue <ng-container *ngIf="total()"> <span class="chip neutral" style="margin-left:6px">{{ total() }}</span> </ng-container></button>
    <button type="button" role="tab" id="t-m" [attr.aria-selected]="tab() === 'mine'" aria-controls="p-m" [tabindex]="tab()==='mine'?0:-1" (click)="tab.set('mine')">Raised by me</button>
  </div>

  <ng-container *ngIf="tab() === 'queue'">
    <div id="p-q" role="tabpanel" aria-labelledby="t-q" style="margin-top:14px" class="stack">
      <div class="row between">
        <div class="row">
          <label class="field" style="flex-direction:row;align-items:center;gap:6px"><span class="small muted">Sort by</span>
            <select [(ngModel)]="sort" (ngModelChange)="load()" aria-label="Sort queue"><option value="risk">Risk, highest first (AI)</option><option value="oldest">Oldest first</option><option value="type">Request type</option><option value="value">Value, highest first</option></select></label>
          <label class="field" style="flex-direction:row;align-items:center;gap:6px"><span class="small muted">Type</span>
            <select [(ngModel)]="type" (ngModelChange)="load()" aria-label="Filter by type"><option value="">All</option><ng-container *ngFor="let t of types"> <option [value]="t.k">{{ t.l }}</option> </ng-container></select></label>
        </div>
        <button type="button" class="btn primary" [disabled]="!picked().length" (click)="bulkOpen.set(true)">Approve selected ({{ picked().length }})</button>
      </div>
      <ng-container *ngIf="sort === 'risk'"> <p class="small muted"><span class="ai-badge">AI</span> Sorted by AI-UC-07 risk score. It only changes the order; you can still open or decide any request.</p> </ng-container>
      <div class="card" aria-live="polite">
        <ng-container *ngFor="let i of items()">
          <div class="queue-item" style="grid-template-columns:auto 1fr auto;align-items:start">
            <input type="checkbox" [checked]="picked().includes(i.id)" (change)="pick(i.id, $any($event.target).checked)" [attr.aria-label]="'Select ' + i.title" style="width:20px;height:20px;margin-top:4px">
            <a [routerLink]="['/approvals', i.id]" style="text-decoration:none;color:inherit">
              <div class="t">{{ i.title }}</div>
              <div class="small muted">{{ label(i.type) }} · {{ i.raisedBy.name }} · {{ i.ageDays }} day{{ i.ageDays === 1 ? '' : 's' }} waiting<ng-container *ngIf="i.value"> · {{ i.value | inr }} </ng-container><ng-container *ngIf="i.viaDelegationFrom"> · <strong>for {{ i.viaDelegationFrom }} (delegation)</strong> </ng-container></div>
              <ng-container *ngIf="i.risk"> <div class="small" style="margin-top:2px"><rx-icon name="spark" [size]="12"/> {{ i.risk.headline }}</div> </ng-container>
            </a>
            <div style="text-align:right">
              <ng-container *ngIf="i.risk"> <div class="risk" [attr.aria-label]="'Risk ' + i.risk.band + ', score ' + i.risk.score"><rx-status [value]="i.risk.band"/><span>{{ i.risk.score.toFixed(2) }}</span></div> </ng-container>
              <div class="small muted" style="margin-top:4px">Step {{ i.step?.index }} of {{ i.step?.total }}</div>
              <ng-container *ngIf="isReviewed(i.id)"> <div class="xs" style="color:var(--rx-success)"><rx-icon name="check" [size]="12"/> Reviewed</div> </ng-container>
            </div>
          </div>
        </ng-container><ng-container *ngIf="!(items())?.length"> <rx-empty title="Your queue is clear" text="New requests will appear here and in your notifications."/> </ng-container>
      </div>
    </div>
  </ng-container><ng-container *ngIf="!(tab() === 'queue')">
    <div id="p-m" role="tabpanel" aria-labelledby="t-m" style="margin-top:14px" class="card">
      <ng-container *ngFor="let i of mine()"> <a class="queue-item" [routerLink]="['/approvals', i.id]"><div><div class="t">{{ i.title }}</div><div class="small muted">{{ label(i.type) }} · raised {{ i.raisedAtUtc | rxDate }}<ng-container *ngIf="i.step && i.status === 'Pending'"> · waiting for {{ i.step.assignedTo }} ({{ i.step.role }}) </ng-container></div></div><rx-status [value]="i.status"/></a> </ng-container><ng-container *ngIf="!(mine())?.length"> <rx-empty title="You haven’t raised any requests" text="Requests you start (project tagging, employee tagging, compensation) are tracked here."/> </ng-container>
    </div>
  </ng-container>

  <ng-container *ngIf="bulkOpen()">
    <rx-dialog title="Approve in bulk" (close)="bulkOpen.set(false)">
      <p>Every request must be reviewed first. Open each one, read its context, then come back here.</p>
      <ul style="list-style:none;margin:0;padding:0"><ng-container *ngFor="let id of picked()"> <li class="row between" style="padding:6px 0;border-bottom:1px solid var(--rx-border)"><span>{{ find(id)?.title }}</span><ng-container *ngIf="isReviewed(id)"> <rx-status value="Pass" label="Reviewed"/> </ng-container><ng-container *ngIf="!(isReviewed(id))"> <a class="btn sm" [routerLink]="['/approvals', id]" (click)="bulkOpen.set(false)">Review first</a> </ng-container></li> </ng-container></ul>
      <label class="field" style="margin-top:12px">Comment (optional)<input type="text" [(ngModel)]="comment" name="c" maxlength="300"></label>
      <div footer class="row"><button type="button" class="btn" (click)="bulkOpen.set(false)">Cancel</button><button type="button" class="btn primary" [disabled]="!allReviewed() || busy()" (click)="bulkApprove()">Approve {{ picked().length }}</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class ApprovalsPage implements OnInit, OnDestroy {
  private api = inject(Api); private toast = inject(Toasts); private panel = inject(AiPanel); session = inject(Session);
  tab = signal<'queue' | 'mine'>('queue'); items = signal<J[]>([]); mine = signal<J[]>([]); total = signal(0); picked = signal<string[]>([]); bulkOpen = signal(false); busy = signal(false); sort = 'risk'; type = ''; comment = '';
  types = Object.entries(TYPE_LABEL).map(([k, l]) => ({ k, l }));
  allReviewed = computed(() => this.picked().length > 0 && this.picked().every(id => reviewed().has(id)));
  label = (t: string) => TYPE_LABEL[t] ?? t; isReviewed = (id: string) => reviewed().has(id); find = (id: string) => this.items().find(i => i.id === id);
  ngOnInit() { this.load(); this.api.get<J[]>('/api/approvals/mine').subscribe(m => this.mine.set(m)); }
  ngOnDestroy() { this.panel.clear('approvals'); }
  load() {
    this.api.get('/api/approvals/pending', { sort: this.sort, type: this.type }).subscribe(r => {
      this.items.set(r.items); this.total.set(r.total); this.picked.set(this.picked().filter(id => r.items.some((i: J) => i.id === id)));
      const hi = r.items.filter((i: J) => i.risk?.band === 'High').length;
      this.panel.publish('approvals', r.items.length ? [{ key: 'q', kind: 'suggestion', title: 'Queue order (AI-UC-07)', summary: `${hi} high-risk request${hi === 1 ? '' : 's'} first. Reasons are shown under each title.`, impact: 'Changes order only. Nothing is blocked or decided for you.', confidence: 0.8 }] : []);
    });
  }
  pick(id: string, on: boolean) { this.picked.update(p => (on ? [...p, id] : p.filter(x => x !== id))); }
  bulkApprove() {
    this.busy.set(true);
    this.api.post('/api/approvals/bulk-approve', { ids: this.picked(), reviewedIds: this.picked().filter(id => reviewed().has(id)), comment: this.comment || null }).subscribe({
      next: r => { const ok = r.results.filter((x: J) => x.ok).length; const bad = r.results.length - ok; this.toast.success(`${ok} approved${bad ? `, ${bad} could not be approved` : ''}.`); if (bad) this.toast.error(r.results.find((x: J) => !x.ok).error); this.busy.set(false); this.bulkOpen.set(false); this.picked.set([]); this.load(); },
      error: e => { this.busy.set(false); this.toast.error(e.title); },
    });
  }
}

@Component({
  selector: 'rx-approval-detail', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxStatus, RxIcon, RxDialog, RxImpact, RxConfidence, RxWhy, RxAiCard, DatePipeRx, DateTimePipeRx, InrPipe],
  template: `
  <ng-container *ngIf="d() as d">
    <a routerLink="/approvals" class="small">← Back to approvals</a>
    <rx-page-header [title]="d.request.title" [sub]="d.request.summary"><rx-status [value]="d.request.status"/></rx-page-header>
    <p class="small muted" style="margin-top:-8px">Raised by <strong>{{ d.request.raisedBy.name }}</strong> on {{ d.request.raisedAtUtc | rxDateTime }}<ng-container *ngIf="d.request.value"> · value {{ d.request.value | inr }} </ng-container></p>

    <ng-container *ngIf="d.risk">
      <rx-ai-card heading="Risk assessment" [summary]="d.risk.headline" [confidence]="d.risk.confidence" [reasons]="d.risk.reasons" impact="Advisory. It never blocks or decides for you."> 
        <div class="risk" style="margin:6px 0"><rx-status [value]="d.risk.band"/> score {{ d.risk.score.toFixed(2) }} <span class="bar" aria-hidden="true"><i [style.width.%]="d.risk.score * 100"></i></span></div>
        <ng-container *ngIf="d.risk.features.length"> <ul class="ai-reasons"><ng-container *ngFor="let f of d.risk.features"> <li><strong>{{ f.name }}:</strong> {{ f.detail }}</li> </ng-container></ul> </ng-container>
      </rx-ai-card>
    </ng-container>

    <div class="grid-2" style="margin-top:16px;align-items:start">
      <section class="card" aria-labelledby="ctx"><div class="card-h"><h2 id="ctx" style="font-size:16px">What you’re approving</h2></div><div class="card-b stack">
        <ng-container [ngSwitch]="d.context?.kind"><ng-container *ngSwitchCase="'tagging'"> <p>{{ d.context.rows.length }} people → <strong>{{ d.context.project }}</strong><ng-container *ngIf="d.context.overlapCount"> <span class="chip danger" style="margin-left:8px"><rx-icon name="alert" [size]="12"/>{{ d.context.overlapCount }} already tagged elsewhere</span> </ng-container></p>
            <div class="table-wrap"><table class="rx"><caption class="sr-only">People in this request</caption><thead><tr><th>Employee</th><th>From</th><th>Overlap check</th></tr></thead><tbody>
              <ng-container *ngFor="let r of d.context.rows"> <tr><td>{{ r.employee.name }} <span class="muted xs">{{ r.employee.grade }}</span></td><td>{{ r.validFrom | rxDate }}</td><td class="wrap"><ng-container *ngIf="r.overlaps.length"> <rx-status value="Fail" label="Overlap"/> <span class="small">{{ r.overlaps.join('; ') }}</span> </ng-container><ng-container *ngIf="!(r.overlaps.length)"> <rx-status value="Pass" label="Clear"/> </ng-container></td></tr> </ng-container></tbody></table></div> </ng-container><ng-container *ngSwitchCase="'project'"> <dl class="kv"><dt>Project</dt><dd>{{ d.context.name }} <span class="mono">{{ d.context.code }}</span></dd><dt>Business unit</dt><dd>{{ d.context.businessUnit }}</dd><dt>Owner</dt><dd>{{ d.context.owner }}</dd><dt>Dates</dt><dd>{{ d.context.startDate | rxDate }} – {{ d.context.endDate ? (d.context.endDate | rxDate) : 'open ended' }}</dd><dt>Shift types</dt><dd>{{ d.context.shiftTypes.join(', ') }}</dd><dt>Cost centre</dt><dd class="mono">{{ d.context.costCentre }}</dd></dl> </ng-container><ng-container *ngSwitchCase="'compensation'"> <dl class="kv"><dt>Project</dt><dd>{{ d.context.project }}</dd><dt>Month</dt><dd class="mono">{{ d.context.periodMonth }}</dd><dt>Total</dt><dd class="mono">{{ d.context.totalAmount | inr }} across {{ d.context.lineCount }} lines</dd><dt>Open exceptions</dt><dd>{{ d.context.openExceptions }}</dd></dl>
            <div class="bar-chart" role="list" aria-label="By shift"><ng-container *ngFor="let b of d.context.byShift"> <div class="b" role="listitem"><span>{{ b.shift }}</span><span class="track"><i [style.width.%]="(b.amount / d.context.totalAmount) * 100"></i></span><span class="num">{{ b.amount | inr }}</span></div> </ng-container></div>
            <ng-container *ngIf="d.context.anomalies.length"> <div class="banner warn"><rx-icon name="alert"/><div><strong>AI anomalies in this project</strong><ul style="margin:4px 0 0 18px;padding:0"><ng-container *ngFor="let a of d.context.anomalies"> <li>{{ a.kind }}: {{ a.employeeName }}. {{ a.description }}</li> </ng-container></ul></div></div> </ng-container>
            <a [routerLink]="['/allowance', d.context.runId]" class="btn sm">Open allowance run</a> </ng-container><ng-container *ngSwitchCase="'arrear'"> <dl class="kv"><dt>Employee</dt><dd>{{ d.context.employee.name }}</dd><dt>Project</dt><dd>{{ d.context.project }}</dd><dt>For month</dt><dd class="mono">{{ d.context.forPeriodMonth }}</dd><dt>Amount</dt><dd class="mono">{{ d.context.amount | inr }}</dd><dt>Age</dt><dd>{{ d.context.ageMonths }} months ({{ d.context.bucket }} bucket)</dd><dt>Other arrears</dt><dd>{{ d.context.others }}</dd><dt>Reason</dt><dd>{{ d.context.reason }}</dd></dl> </ng-container><ng-container *ngSwitchCase="'movement'"> <dl class="kv"><dt>Employee</dt><dd>{{ d.context.employee.name }}</dd><dt>From</dt><dd>{{ d.context.from || '—' }}</dd><dt>To</dt><dd>{{ d.context.to }}</dd><dt>Effective</dt><dd>{{ d.context.effectiveFrom | rxDate }} <ng-container *ngIf="d.context.isBackdated"> <span class="chip warn">backdated</span> </ng-container></dd><dt>Reason</dt><dd>{{ d.context.reason }}</dd></dl> </ng-container><ng-container *ngSwitchCase="'exception'"> <ng-container *ngFor="let e of d.context.items"> <p><rx-status [value]="e.severity"/> {{ e.description }}</p> </ng-container> </ng-container></ng-container>
      </div></section>

      <section class="stack">
        <div class="card"><div class="card-h"><h2 style="font-size:16px">Approval path</h2></div><div class="card-b">
          <ol class="timeline"><ng-container *ngFor="let s of d.steps"> <li [class.done]="s.decision === 'Approved'" [class.rej]="s.decision === 'Rejected'" [class.cur]="s.current"><div><strong>{{ s.role }}</strong> · {{ s.assignedTo }} <rx-status [value]="s.decision === 'Pending' ? (s.current ? 'Pending' : 'Draft') : s.decision" [label]="s.decision === 'Pending' && !s.current ? 'Later' : undefined"/></div>
            <ng-container *ngIf="s.actedBy"> <div class="small muted">{{ s.actedBy }}{{ s.viaDelegation ? ' (via delegation)' : '' }} · {{ s.actedAtUtc | rxDateTime }}</div> </ng-container><ng-container *ngIf="s.comment"> <div class="small">“{{ s.comment }}”</div> </ng-container></li> </ng-container></ol></div></div>
        <div class="card"><div class="card-h"><h2 style="font-size:16px">Requester history</h2></div><div class="card-b small">
          <p>{{ d.requesterHistory.approved }} approved, {{ d.requesterHistory.rejected }} rejected of {{ d.requesterHistory.total }} earlier requests.</p>
          <ul style="margin:0;padding-left:18px"><ng-container *ngFor="let r of d.requesterHistory.recent"> <li>{{ r.title }} — {{ r.status }}</li> </ng-container></ul></div></div>
      </section>
    </div>

    <ng-container *ngIf="d.diff.length"> <section class="stack" style="margin-top:16px" aria-labelledby="df"><h2 id="df" style="font-size:16px">What changes</h2><rx-impact [rows]="d.diff"/></section> </ng-container>

    <ng-container *ngIf="d.canDecide">
      <div class="sticky-actions" role="group" aria-label="Decision" style="margin-top:20px">
        <button type="button" class="btn primary" (click)="approve()" [disabled]="busy()"><rx-icon name="check"/>Approve{{ d.steps.length > 1 && !isLast(d) ? ' this step' : '' }}</button>
        <button type="button" class="btn danger-outline" (click)="rejectOpen.set(true)" [disabled]="busy()"><rx-icon name="x"/>Reject…</button>
      </div>
    </ng-container><ng-container *ngIf="!(d.canDecide) && (d.request.status === 'Pending')">
      <div class="banner info" style="margin-top:20px"><rx-icon name="info"/><span>You can read this request but it isn’t waiting for your decision. <ng-container *ngIf="d.request.step"> It’s with {{ d.request.step.assignedTo }} ({{ d.request.step.role }}). </ng-container><ng-container *ngIf="d.request.type === 'AllowanceCompensation'"> Only HRSS can approve compensation. </ng-container></span></div>
    </ng-container>
  </ng-container><ng-container *ngIf="!(d())"> <p class="muted" role="status">Loading request…</p> </ng-container>

  <ng-container *ngIf="rejectOpen()">
    <rx-dialog title="Reject this request" (close)="rejectOpen.set(false)">
      <label class="field">Reason (required)<textarea [(ngModel)]="reason" name="r" rows="4" [attr.aria-invalid]="rejTouched() && !reason.trim()" aria-describedby="r-h" data-autofocus></textarea><span class="hint" id="r-h">The requester sees this. Say what to fix.</span></label>
      <ng-container *ngIf="rejTouched() && !reason.trim()"> <p class="err" role="alert">Enter a reason so the requester knows what to change.</p> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="rejectOpen.set(false)">Cancel</button><button type="button" class="btn danger" (click)="reject()">Reject request</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class ApprovalDetailPage implements OnInit {
  private api = inject(Api); private route = inject(ActivatedRoute); private toast = inject(Toasts); private router = inject(Router); private session = inject(Session);
  d = signal<J | null>(null); busy = signal(false); rejectOpen = signal(false); rejTouched = signal(false); reason = '';
  isLast = (d: J) => d.steps.every((s: J) => s.stepIndex <= d.steps.find((x: J) => x.current)?.stepIndex);
  ngOnInit() { const id = this.route.snapshot.paramMap.get('id')!; this.api.get(`/api/approvals/${id}`).subscribe({ next: d => { this.d.set(d); markReviewed(id); }, error: () => { this.toast.error('That request isn’t available to you.'); void this.router.navigateByUrl('/approvals'); } }); }
  approve() { this.decide(true, null); }
  reject() { this.rejTouched.set(true); if (!this.reason.trim()) return; this.decide(false, this.reason.trim()); }
  private decide(approve: boolean, comment: string | null) {
    this.busy.set(true);
    this.api.post(`/api/approvals/${this.d()!.request.id}/decide`, { approve, comment }).subscribe({
      next: r => { this.toast.success(approve ? (r.completed ? 'Approved. The request is complete.' : 'Approved. It moves to the next step.') : 'Rejected. The requester has been told why.'); this.rejectOpen.set(false); void this.session.refresh(); void this.router.navigateByUrl('/approvals'); },
      error: e => { this.busy.set(false); this.toast.error(e.title); },
    });
  }
}
