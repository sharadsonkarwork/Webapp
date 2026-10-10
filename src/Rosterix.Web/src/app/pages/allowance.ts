import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { AiPanel, Toasts } from '../core/ui.services';
import { DatePipeRx, InrPipe, MonthPipe } from '../shared/fmt';
import { Column, RxAiCard, RxAiSkeleton, RxDialog, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxTable } from '../shared/ui';

@Component({
  selector: 'rx-allowance', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxPageHeader, RxTable, RxIcon, RxDialog, RxAiCard, RxAiSkeleton, RxStatus, InrPipe, MonthPipe],
  template: `
  <rx-page-header title="Allowance" sub="Calculate a project-month, review the line-by-line trace, then send it for compensation approval. Only HRSS can approve compensation.">
    <ng-container *ngIf="session.has('PM','HRSS')"> <button type="button" class="btn primary" (click)="open.set(true)"><rx-icon name="plus"/>Calculate a month</button> </ng-container>
  </rx-page-header>

  <ng-container *ngIf="session.has('PM','LM')">
    <section class="card" aria-labelledby="fc" style="margin-bottom:16px"><div class="card-h"><h2 id="fc" style="font-size:16px">Cost forecast</h2>
      <div class="row"><label class="field" style="flex-direction:row;align-items:center;gap:6px">Project<select [(ngModel)]="fProject" name="fp" (ngModelChange)="loadForecast()" aria-label="Forecast project"><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }}</option> </ng-container></select></label>
        <label class="field" style="flex-direction:row;align-items:center;gap:6px">Month<input type="month" [(ngModel)]="fMonth" name="fm" (ngModelChange)="loadForecast()" aria-label="Forecast month"></label></div></div>
      <div class="card-b">
        <ng-container *ngIf="fLoading()"> <rx-ai-skeleton/> </ng-container><ng-container *ngIf="!(fLoading())"><ng-container *ngIf="forecast() as f">
          <div class="grid-2" style="align-items:start"><rx-ai-card heading="Predicted month-end cost" [summary]="f.summary" [impact]="f.impact" [confidence]="f.confidence" [reasons]="f.reasons"/>
            <div><div class="bar-chart ai"><ng-container *ngFor="let h of f.payload.history"> <div class="b ai"><span>{{ h.month | rxMonth }}</span><span class="track"><i [style.width.%]="(h.amount / max(f)) * 100"></i></span><span class="num">{{ h.amount | inr }}</span></div> </ng-container>
              <div class="b ai"><span><strong>{{ fMonth | rxMonth }}</strong></span><span class="track"><i [style.width.%]="(f.payload.forecast / max(f)) * 100"></i></span><span class="num"><strong>{{ f.payload.forecast | inr }}</strong></span></div></div>
              <h3 style="font-size:14px;margin:12px 0 4px">What drives it</h3><ul class="ai-reasons"><ng-container *ngFor="let d of f.payload.drivers"> <li><strong>{{ d.label }}:</strong> {{ d.detail }}</li> </ng-container></ul></div></div>
        </ng-container></ng-container><ng-container *ngIf="!(fLoading()) && !(forecast())"> <p class="muted small">Choose a project and month with a roster to see a forecast.</p> </ng-container>
      </div></section>
  </ng-container>

  <div class="card"><div class="card-b" style="padding:0"><rx-table [rows]="rows()" [columns]="cols" [rowTpl]="act" caption="Allowance runs" emptyTitle="No allowance runs yet" emptyText="Calculate a project-month once its roster is published."/>
    <ng-template #act let-r><a class="btn sm" [routerLink]="['/allowance', r.id]" [attr.aria-label]="'Open ' + r.project + ' ' + r.month">Open</a></ng-template></div></div>

  <ng-container *ngIf="open()">
    <rx-dialog title="Calculate allowance" (close)="open.set(false)">
      <p class="small muted">The roster for the month must be published. Calculation reads the roster and the rates in effect on each date; it changes no roster data.</p>
      <div class="form-grid"><label class="field">Project<select [(ngModel)]="cProject" name="cp"><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }} · {{ p.name }}</option> </ng-container></select></label>
        <label class="field">Month<input type="month" [(ngModel)]="cMonth" name="cm"></label></div>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert" style="margin-top:10px"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="open.set(false)">Cancel</button><button type="button" class="btn primary" (click)="calculate()" [disabled]="busy()">Calculate</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class AllowancePage implements OnInit {
  private api = inject(Api); private router = inject(Router); private toast = inject(Toasts); session = inject(Session);
  rows = signal<J[]>([]); projects = signal<J[]>([]); open = signal(false); busy = signal(false); err = signal(''); cProject = ''; cMonth = ''; fProject = ''; fMonth = ''; forecast = signal<J | null>(null); fLoading = signal(false);
  cols: Column[] = [{ key: 'code', label: 'Project', type: 'mono' }, { key: 'project', label: 'Name' }, { key: 'month', label: 'Month', type: 'mono' }, { key: 'status', label: 'Status', type: 'chip' }, { key: 'lineCount', label: 'Lines', type: 'num' }, { key: 'totalAmount', label: 'Total', type: 'money' }, { key: 'exceptionCount', label: 'Exceptions', type: 'num' }, { key: 'flaggedLines', label: 'Flagged lines', type: 'num' }];
  max = (f: J) => Math.max(f.payload.forecast, ...f.payload.history.map((h: J) => h.amount), 1);
  ngOnInit() {
    this.api.get<J[]>('/api/allowance-runs').subscribe(r => this.rows.set(r));
    this.api.get('/api/lookups').subscribe(l => { const ps = (l.projects as J[]).filter(p => p.status === 'Active'); this.projects.set(ps); this.cProject = this.fProject = ps[0]?.id ?? ''; const t = l.today as string; const prev = new Date(t); prev.setMonth(prev.getMonth() - 1); this.cMonth = `${prev.getFullYear()}-${String(prev.getMonth() + 1).padStart(2, '0')}`; this.fMonth = t.slice(0, 7); this.loadForecast(); });
  }
  loadForecast() { if (!this.session.has('PM', 'LM') || !this.fProject || !this.fMonth) return; this.fLoading.set(true); this.api.get('/api/ai/cost-forecast', { projectId: this.fProject, month: this.fMonth }).subscribe({ next: f => { this.forecast.set(f); this.fLoading.set(false); }, error: () => { this.forecast.set(null); this.fLoading.set(false); } }); }
  calculate() { this.busy.set(true); this.err.set(''); this.api.post('/api/allowance-runs/calculate', { projectId: this.cProject, month: this.cMonth }).subscribe({ next: r => { this.toast.success(`Calculated ${r.lineCount} lines, ${r.exceptionCount} exception(s).`); void this.router.navigate(['/allowance', r.id]); }, error: e => { this.busy.set(false); this.err.set(e.title); } }); }
}

@Component({
  selector: 'rx-run-detail', standalone: true, imports: [CommonModule, RouterLink, RxPageHeader, RxStatus, RxIcon, RxTable, RxDialog, RxEmpty, InrPipe, DatePipeRx, MonthPipe],
  template: `
  <ng-container *ngIf="d() as d">
    <a routerLink="/allowance" class="small">← All runs</a>
    <rx-page-header [title]="d.run.project + ', ' + (d.run.month | rxMonth)" [sub]="'Calculated by ' + d.run.calculatedBy">
      <rx-status [value]="d.run.status"/>
      <ng-container *ngIf="d.canRecalculate"> <button type="button" class="btn" (click)="recalc()" [disabled]="busy()"><rx-icon name="refresh"/>Recalculate</button> </ng-container>
      <ng-container *ngIf="d.canProcess"> <button type="button" class="btn primary" (click)="processOpen.set(true)">Send for compensation approval…</button> </ng-container>
      <ng-container *ngIf="d.canApprove"> <button type="button" class="btn primary" (click)="approveOpen.set(true)"><rx-icon name="check"/>Approve compensation…</button> </ng-container>
      <ng-container *ngIf="d.run.approvalRequestId"> <a class="btn" [routerLink]="['/approvals', d.run.approvalRequestId]">View approval</a> </ng-container>
    </rx-page-header>
    <ng-container *ngIf="d.run.status === 'PendingCompensation' && !d.canApprove"> <div class="banner info" style="margin-bottom:12px"><rx-icon name="clock"/><span>Waiting for HRSS to approve compensation. Project managers, line managers and Regional Heads can’t approve it.</span></div> </ng-container>
    <ng-container *ngIf="d.run.status === 'Submitted'"> <div class="banner success" style="margin-bottom:12px"><rx-icon name="check"/><span>Submitted to payroll. This run is now read-only; a later rate change never rewrites it.</span></div> </ng-container>
    <div class="grid-3" style="margin-bottom:16px">
      <div class="card kpi"><span class="v">{{ d.run.totalAmount | inr }}</span><span class="l">Total allowance</span></div>
      <div class="card kpi"><span class="v">{{ d.run.lineCount }}</span><span class="l">Lines</span></div>
      <div class="card kpi"><span class="v">{{ d.run.flaggedLines }}</span><span class="l">Flagged lines</span><span class="xs muted">{{ d.run.exceptionCount }} exceptions raised</span></div>
    </div>
    <div class="grid-2" style="align-items:start">
      <section class="card" aria-labelledby="bs"><div class="card-h"><h2 id="bs" style="font-size:16px">By shift type</h2></div><div class="card-b"><div class="bar-chart"><ng-container *ngFor="let b of d.byShift"> <div class="b"><span>{{ b.shift }}</span><span class="track"><i [style.width.%]="(b.amount / d.run.totalAmount) * 100"></i></span><span class="num">{{ b.amount | inr }}</span></div> </ng-container></div></div></section>
      <section class="card" aria-labelledby="ex"><div class="card-h"><h2 id="ex" style="font-size:16px">Open exceptions in this project</h2><a class="btn sm" routerLink="/exceptions">All exceptions</a></div>
        <div class="card-b" style="padding:0"><ng-container *ngFor="let e of d.exceptions.slice(0, 6)"> <div class="queue-item" style="grid-template-columns:auto 1fr"><rx-status [value]="e.severity"/><span class="small">{{ e.description }}</span></div> </ng-container> <ng-container *ngIf="!d.exceptions.length"> <rx-empty title="No open exceptions"/> </ng-container></div></section>
    </div>
    <section class="card" style="margin-top:16px" aria-labelledby="em"><div class="card-h"><h2 id="em" style="font-size:16px">By employee</h2><span class="small muted">Open an employee to see how each amount was worked out.</span></div>
      <div class="card-b" style="padding:0"><rx-table [rows]="emps()" [columns]="empCols" [rowTpl]="view" caption="Allowance by employee" [pageSize]="15"/><ng-template #view let-r><button type="button" class="btn sm" (click)="show(r)" [attr.aria-label]="'See breakdown for ' + r.name">Breakdown</button></ng-template></div></section>
  </ng-container><ng-container *ngIf="!(d())"> <p class="muted" role="status">Loading run…</p> </ng-container>

  <ng-container *ngIf="lines() as l">
    <rx-dialog [title]="'How ' + l.name + '’s allowance was calculated'" [wide]="true" (close)="lines.set(null)">
      <p class="small muted">Every line below shows the shift, the rate in effect on that date and any rate change. Nothing here can be edited; if something looks wrong, raise it as an exception or an employee query.</p>
      <div class="table-wrap" style="max-height:55vh"><table class="rx"><caption class="sr-only">Allowance lines</caption><thead><tr><th>Date</th><th>Shift</th><th class="num">Amount</th><th>Explanation</th></tr></thead><tbody>
        <ng-container *ngFor="let x of l.rows"> <tr><td class="mono" style="white-space:nowrap">{{ x.workDate | rxDate:false }}</td><td>{{ x.shift }}<ng-container *ngIf="x.status === 'Flagged'"> <rx-status value="Flagged" /> </ng-container></td><td class="num">{{ x.amount | inr }}</td><td class="wrap small">{{ x.trace }}</td></tr> </ng-container></tbody></table></div>
      <div footer><button type="button" class="btn" (click)="lines.set(null)">Close</button></div>
    </rx-dialog>
  </ng-container>

  <ng-container *ngIf="processOpen()">
    <rx-dialog title="Send for compensation approval?" (close)="processOpen.set(false)">
      <p>This freezes the run and asks HRSS to approve ₹{{ d()!.run.totalAmount | inr }}. After that, lines can’t be recalculated.</p>
      <ng-container *ngIf="d()!.run.flaggedLines"> <div class="banner warn"><rx-icon name="alert"/><span>{{ d()!.run.flaggedLines }} line(s) are flagged. HRSS will see them in the approval.</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="processOpen.set(false)">Not yet</button><button type="button" class="btn primary" (click)="process()" [disabled]="busy()">Send for approval</button></div>
    </rx-dialog>
  </ng-container>
  <ng-container *ngIf="approveOpen()">
    <rx-dialog title="Approve compensation?" (close)="approveOpen.set(false)">
      <p>You’re approving <strong>{{ d()!.run.totalAmount | inr }}</strong> for {{ d()!.run.project }}, {{ d()!.run.month | rxMonth }}. This is recorded against your name and can’t be undone here.</p>
      <ng-container *ngIf="d()!.run.flaggedLines"> <div class="banner warn"><rx-icon name="alert"/><span>{{ d()!.run.flaggedLines }} flagged line(s) remain. Check the anomaly dashboard before approving.</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="approveOpen.set(false)">Cancel</button><button type="button" class="btn primary" (click)="approve()" [disabled]="busy()">Approve compensation</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class RunDetailPage implements OnInit {
  private api = inject(Api); private route = inject(ActivatedRoute); private toast = inject(Toasts); private router = inject(Router); private session = inject(Session);
  d = signal<J | null>(null); lines = signal<J | null>(null); busy = signal(false); processOpen = signal(false); approveOpen = signal(false);
  empCols: Column[] = [{ key: 'name', label: 'Employee' }, { key: 'number', label: 'No.', type: 'mono' }, { key: 'count', label: 'Shifts', type: 'num' }, { key: 'amount', label: 'Amount', type: 'money' }, { key: 'flagged', label: 'Flagged', type: 'num' }];
  emps = computed(() => (this.d()?.employees ?? []).map((e: J) => ({ id: e.employee.id, name: e.employee.name, number: e.employee.number, count: e.count, amount: e.amount, flagged: e.flagged })));
  ngOnInit() { this.load(); }
  load() { this.api.get(`/api/allowance-runs/${this.route.snapshot.paramMap.get('id')}`).subscribe({ next: d => this.d.set(d), error: () => void this.router.navigateByUrl('/allowance') }); }
  show(r: J) { this.api.get(`/api/allowance-runs/${this.d()!.run.id}/lines`, { employeeId: r.id, pageSize: 100 }).subscribe(l => this.lines.set({ name: r.name, rows: l.rows })); }
  recalc() { this.busy.set(true); this.api.post('/api/allowance-runs/calculate', { projectId: this.d()!.run.projectId, month: this.d()!.run.month }).subscribe({ next: r => { this.busy.set(false); this.toast.success('Recalculated.'); void this.router.navigate(['/allowance', r.id]).then(() => this.load()); }, error: e => { this.busy.set(false); this.toast.error(e.title); } }); }
  process() { this.busy.set(true); this.api.post(`/api/allowance-runs/${this.d()!.run.id}/process`).subscribe({ next: () => { this.busy.set(false); this.processOpen.set(false); this.toast.success('Sent to HRSS for compensation approval.'); this.load(); }, error: e => { this.busy.set(false); this.toast.error(e.title); } }); }
  approve() { this.busy.set(true); this.api.post(`/api/allowance-runs/${this.d()!.run.id}/approve-compensation`).subscribe({ next: () => { this.busy.set(false); this.approveOpen.set(false); this.toast.success('Compensation approved.'); void this.session.refresh(); this.load(); }, error: e => { this.busy.set(false); this.approveOpen.set(false); this.toast.error(e.title); } }); }
}
