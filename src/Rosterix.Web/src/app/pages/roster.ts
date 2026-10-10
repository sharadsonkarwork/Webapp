import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, switchMap, of } from 'rxjs';
import { Api, J, download } from '../core/api.service';
import { Session } from '../core/auth.service';
import { AiPanel, Theme, Toasts } from '../core/ui.services';
import { Cell, DropEvent, RxRosterGrid } from '../shared/roster-grid';
import { DatePipeRx, InrPipe, MonthPipe } from '../shared/fmt';
import { RxAiCard, RxAiSkeleton, RxConfidence, RxDialog, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxWhy } from '../shared/ui';

@Component({
  selector: 'rx-roster', standalone: true, imports: [CommonModule, FormsModule, RouterLink, RxRosterGrid, RxPageHeader, RxIcon, RxStatus, RxDialog, RxAiCard, RxAiSkeleton, RxEmpty, RxConfidence, RxWhy, DatePipeRx, InrPipe, MonthPipe],
  template: `
  <rx-page-header title="Roster" [sub]="'Plan who works which shift. Every change is checked against the same twelve rules the allowance run uses.'">
    <ng-container *ngIf="g() as g">
      <button type="button" class="btn" (click)="downloadCsv()"><rx-icon name="download"/>Download CSV</button>
      <ng-container *ngIf="g.canEdit"> <button type="button" class="btn" (click)="uploadOpen.set(true)"><rx-icon name="upload"/>Upload CSV</button> </ng-container>
      <ng-container *ngIf="g.canEdit"> <button type="button" class="btn ai" (click)="suggest()"><rx-icon name="spark"/>Suggest a draft</button> </ng-container>
      <ng-container *ngIf="g.canPublish"> <button type="button" class="btn primary" (click)="openPublish()">Publish roster…</button> </ng-container>
    </ng-container>
  </rx-page-header>

  <div class="card" style="margin-bottom:14px"><div class="card-b row">
    <label class="field" style="flex-direction:row;align-items:center;gap:6px">Project
      <select [(ngModel)]="projectId" name="p" (ngModelChange)="load()" aria-label="Project"><ng-container *ngFor="let p of projects()"> <option [value]="p.id">{{ p.code }} · {{ p.name }}</option> </ng-container></select></label>
    <label class="field" style="flex-direction:row;align-items:center;gap:6px">Month <input type="month" [(ngModel)]="month" name="m" (ngModelChange)="load()" aria-label="Month"></label>
    <label class="field" style="flex-direction:row;align-items:center;gap:6px">Density
      <select [value]="theme.density()" (change)="theme.density.set($any($event.target).value)" aria-label="Row density"><option value="compact">Compact</option><option value="comfortable">Comfortable</option></select></label>
    <ng-container *ngIf="g() as g"> <span class="grow"></span><rx-status [value]="g.roster.status"/> <ng-container *ngIf="g.roster.source === 'AiDraft'"> <span class="chip ai"><rx-icon name="spark" [size]="12"/>Started from AI draft</span> </ng-container> </ng-container>
  </div></div>

  <ng-container *ngIf="loading()"> <p class="muted" role="status">Loading roster…</p> </ng-container><ng-container *ngIf="!(loading())"><ng-container *ngIf="preview() as pv">
    <div class="stack">
      <rx-ai-card heading="Draft roster" [summary]="pv.summary" [impact]="pv.impact" [confidence]="pv.confidence" [reasons]="pv.reasons"
        [acceptLabel]="g() ? 'Replace draft with this' : 'Accept as draft roster'" dismissLabel="Discard suggestion" [busy]="busy()" (accept)="acceptDraft()" (dismiss)="discardDraft()">
        <ng-container *ngIf="pv.payload.conflicts.length"> <p class="small" style="margin-top:6px"><strong>{{ pv.payload.conflicts.length }} conflict(s) self-reported.</strong> Accepting creates a <em>draft</em> only; you resolve these before publishing.</p> </ng-container>
      </rx-ai-card>
      <rx-roster-grid [label]="'AI draft roster preview for ' + month" [employees]="pvEmployees()" [days]="pvDays()" [entries]="pvEntries()" [shiftTypes]="pvShifts()" [conflicts]="pv.payload.conflicts" [editable]="false" [density]="theme.density()"/>
    </div>
  </ng-container></ng-container><ng-container *ngIf="!(loading()) && !(preview())"><ng-container *ngIf="g() as g">
    <div class="grid-2" style="grid-template-columns:minmax(0,1fr) 320px;align-items:start">
      <section aria-label="Roster grid" class="stack">
        <ng-container *ngIf="g.roster.status === 'Published'"> <div class="banner info"><rx-icon name="info"/><span>This roster is published. Changes notify the employee, and anything inside 24 hours of the shift is flagged to them as short notice.</span></div> </ng-container>
        <div class="legend" aria-label="Legend">
          <ng-container *ngFor="let s of projectShifts(); let i = index"> <span class="sw" [attr.data-sw]="i" draggable="true" (dragstart)="drag($event, 'shift:' + s.id)" [title]="s.name + ' ' + s.start + '–' + s.end + ' (drag onto cells, or select cells and press Enter)'" [style.background]="'var(--rx-shift-' + 'abcde'[i % 5] + ')'"><rx-icon name="grip" [size]="12"/>{{ s.code }} <span class="muted">{{ s.start }}–{{ s.end }}</span><ng-container *ngIf="s.eligible"> <span aria-label="allowance eligible">₹</span> </ng-container></span> </ng-container>
          <span class="sw" draggable="true" (dragstart)="drag($event, 'off')">OFF weekly off</span><span class="sw">LV leave</span><span class="sw">HOL holiday</span><span class="sw"><span style="color:var(--rx-danger)">!</span> blocking</span><span class="sw"><span style="color:var(--rx-warn)">▲</span> warning</span><span class="sw ai-cell">AI</span>
        </div>
        <ng-container *ngIf="g.canEdit"> <p class="small muted">Select cells (click, Shift+click, or Space), then press <kbd>Enter</kbd> to choose a shift. Arrow keys move. Dragging a chip onto cells is an optional shortcut.</p> </ng-container>
        <rx-roster-grid #grid [label]="'Roster for ' + g.roster.project.name + ', ' + month" [employees]="g.employees" [days]="g.days" [entries]="g.entries" [shiftTypes]="g.shiftTypes" [conflicts]="conflicts()" [editable]="g.canEdit"
          [density]="theme.density()" (activateCells)="openAssign($event)" (dropped)="onDrop($event)" (activeEmployee)="onActiveEmployee($event)"/>
      </section>

      <aside class="stack" aria-label="Roster checks">
        <section class="card" aria-labelledby="cf"><div class="card-h"><h2 id="cf" style="font-size:15px">Conflicts <span class="chip" [class]="conflicts().length ? 'chip danger' : 'chip success'">{{ conflicts().length }}</span></h2></div>
          <div class="card-b" style="max-height:340px;overflow:auto;padding:0">
            <ng-container *ngFor="let v of conflicts()">
              <div class="queue-item" style="grid-template-columns:1fr;gap:2px"><div><rx-status [value]="v.severity"/> <strong class="mono">{{ v.ruleCode }}</strong> · {{ v.employeeName }}</div><div class="small">{{ v.message }}</div><ng-container *ngIf="v.suggestedFix"> <div class="small muted">{{ v.suggestedFix }}</div> </ng-container>
                <div><button type="button" class="btn sm" (click)="goTo(v)">Go to cell</button></div></div>
            </ng-container><ng-container *ngIf="!(conflicts())?.length"> <div class="empty"><h3>No conflicts</h3><p class="small">This roster passes all rules.</p></div> </ng-container>
          </div></section>
        <ng-container *ngIf="g.validation.nearMisses.length">
          <section class="card" aria-labelledby="nm"><div class="card-h"><h2 id="nm" style="font-size:15px">Close to a limit <span class="ai-badge">AI</span></h2></div>
            <div class="card-b small stack"><ng-container *ngFor="let n of g.validation.nearMisses.slice(0, 5)"> <p>{{ n.message }}</p> </ng-container></div></section>
        </ng-container>
        <ng-container *ngIf="forecast() as f"> <rx-ai-card heading="Cost forecast" [summary]="f.summary" [impact]="f.impact" [confidence]="f.confidence" [reasons]="f.reasons">
          <div class="bar-chart ai" style="margin-top:6px"><ng-container *ngFor="let h of f.payload.history"> <div class="b ai"><span>{{ h.month | rxMonth }}</span><span class="track"><i [style.width.%]="(h.amount / maxAmt(f)) * 100"></i></span><span class="num">{{ h.amount | inr }}</span></div> </ng-container><div class="b ai"><span><strong>{{ month | rxMonth }}</strong></span><span class="track"><i [style.width.%]="(f.payload.forecast / maxAmt(f)) * 100"></i></span><span class="num">{{ f.payload.forecast | inr }}</span></div></div>
        </rx-ai-card> </ng-container>
        <ng-container *ngIf="pref() as p">
          <section class="ai-card" aria-label="Employee preference (AI)"><div class="ai-h"><span class="ai-badge">AI</span> {{ prefName() }} <span class="grow"></span><rx-confidence [value]="p.confidence"/></div>
            <p class="ai-sum">{{ p.summary }}</p>
            <ng-container *ngIf="p.payload.overrideShiftTypeId || p.payload.overrideOffDay"> <p class="small">Set on their record: {{ shiftName(p.payload.overrideShiftTypeId) }} {{ p.payload.overrideOffDay ? ', off ' + p.payload.overrideOffDay : '' }}. This takes priority when drafting.</p> </ng-container>
            <div class="row"><rx-why [reasons]="p.reasons"/><button type="button" class="btn sm" (click)="prefOpen.set(true)">Edit preference</button></div></section>
        </ng-container>
      </aside>
    </div>
  </ng-container></ng-container><ng-container *ngIf="!(loading()) && !(preview()) && !(g())">
    <div class="card"><rx-empty [title]="'No roster for ' + (month | rxMonth) + ' yet'" text="Start from a blank roster, or let Rosterix suggest a draft from the last three published months. You decide before anything is saved.">
      <div class="row" style="justify-content:center;margin-top:12px">
        <button type="button" class="btn" (click)="createBlank()" [disabled]="busy()">Create blank roster</button>
        <button type="button" class="btn ai" (click)="suggest()" [disabled]="busy()"><rx-icon name="spark"/>Suggest a draft</button></div></rx-empty></div>
    <ng-container *ngIf="loadingAi()"> <div style="margin-top:12px"><rx-ai-skeleton/></div> </ng-container>
  </ng-container>

  <!-- assign dialog: the non-drag path (WCAG 2.5.7) -->
  <ng-container *ngIf="assign() as a">
    <rx-dialog [title]="'Assign ' + a.cells.length + ' cell' + (a.cells.length === 1 ? '' : 's')" (close)="assign.set(null)">
      <p class="small muted">{{ a.cells.length === 1 ? cellLabel(a.cells[0]) : a.cells.length + ' cells across ' + empCount(a.cells) + ' people' }}</p>
      <fieldset><legend>Choose</legend>
        <div role="listbox" aria-label="Shift or day type" class="stack" style="gap:2px" (keydown)="listKey($event)">
          <ng-container *ngFor="let o of options(); let i = index">
            <label class="check" role="option" [attr.aria-selected]="a.choice === o.value" style="padding:2px 6px;border-radius:6px" [style.background]="a.choice === o.value ? 'var(--rx-action-subtle)' : ''">
              <input type="radio" name="choice" [value]="o.value" [checked]="a.choice === o.value" (change)="choose(o.value)" [attr.data-autofocus]="i === 0 ? '' : null"> <strong class="mono" style="min-width:64px;display:inline-block">{{ o.code }}</strong> {{ o.label }} <span class="muted small">{{ o.time }}</span><ng-container *ngIf="o.eligible"> <span class="chip success">₹ allowance</span> </ng-container>
            </label>
          </ng-container>
        </div></fieldset>
      <div aria-live="polite" style="margin-top:12px;min-height:44px">
        <ng-container *ngIf="a.checking"> <p class="muted small"><span class="ai-badge">AI</span> Checking the rules…</p> </ng-container><ng-container *ngIf="!(a.checking) && (a.result)">
          <ng-container *ngIf="!a.result.fresh.length"> <div class="banner success"><rx-icon name="check"/><span>No new rule conflicts.<ng-container *ngIf="a.result.existing"> <span class="muted"> {{ a.result.existing }} earlier conflict(s) remain on this roster.</span> </ng-container></span></div> </ng-container><ng-container *ngIf="!(!a.result.fresh.length)"> <div class="banner" [class]="a.result.block ? 'banner danger' : 'banner warn'"><rx-icon name="alert"/><div><strong>{{ a.result.block ? 'This would break a blocking rule, so it can’t be saved.' : 'This is allowed with your acknowledgement.' }}</strong>
            <ul style="margin:4px 0 0 18px;padding:0"><ng-container *ngFor="let v of a.result.fresh"> <li><span class="mono">{{ v.ruleCode }}</span> {{ v.message }} <span class="muted">{{ v.suggestedFix }}</span></li> </ng-container></ul></div></div> </ng-container>
          <ng-container *ngIf="a.result.warn && !a.result.block"> <label class="check"><input type="checkbox" [(ngModel)]="a.ack" name="ack"> I acknowledge the warning{{ a.result.fresh.length === 1 ? '' : 's' }}.</label> </ng-container>
        </ng-container>
        <ng-container *ngIf="a.error"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ a.error }}</span></div> </ng-container>
      </div>
      <label class="field" style="margin-top:8px">Reason <span class="hint">optional, kept in the change log</span><input type="text" [(ngModel)]="a.reason" name="reason" maxlength="200"></label>
      <div footer class="row"><button type="button" class="btn" (click)="assign.set(null)">Cancel</button><button type="button" class="btn primary" (click)="confirmAssign()" [disabled]="a.checking || !a.choice || a.result?.block || (a.result?.warn && !a.ack) || busy()">Confirm {{ a.cells.length === 1 ? 'assignment' : a.cells.length + ' assignments' }}</button></div>
    </rx-dialog>
  </ng-container>

  <ng-container *ngIf="publishOpen() as pb">
    <rx-dialog title="Publish this roster?" [wide]="true" (close)="publishOpen.set(null)">
      <p>Publishing tells <strong>{{ pb.affectedCount }} people</strong> their {{ month | rxMonth }} shifts are final. You can still edit afterwards, but each edit notifies the person.</p>
      <ng-container *ngIf="pb.leadTimeFlag"> <div class="banner warn"><rx-icon name="clock"/><span><strong>Short notice.</strong> The roster starts in under 24 hours, so notifications are marked as short notice.</span></div> </ng-container>
      <ng-container *ngIf="pb.blocking.length"> <div class="banner danger" style="margin-top:10px"><rx-icon name="ban"/><div><strong>{{ pb.blocking.length }} blocking conflict(s). Fix them first.</strong><ul style="margin:4px 0 0 18px;padding:0"><ng-container *ngFor="let v of pb.blocking.slice(0,5)"> <li>{{ v.message }}</li> </ng-container></ul></div></div> </ng-container>
      <ng-container *ngIf="pb.warnings.length"> <div class="banner warn" style="margin-top:10px"><rx-icon name="alert"/><div><strong>{{ pb.warnings.length }} warning(s)</strong><ul style="margin:4px 0 0 18px;padding:0"><ng-container *ngFor="let v of pb.warnings.slice(0,5)"> <li>{{ v.message }}</li> </ng-container></ul></div></div><label class="check" style="margin-top:6px"><input type="checkbox" [(ngModel)]="pubAck" name="pa"> I’ve reviewed the warnings.</label> </ng-container>
      <div class="table-wrap" style="max-height:260px;margin-top:12px"><table class="rx"><caption class="sr-only">People who will be notified</caption><thead><tr><th>Employee</th><th class="num">Shifts</th><th class="num">Weekly offs</th></tr></thead><tbody>
        <ng-container *ngFor="let p of pb.affected"> <tr><td>{{ p.employee.name }} <span class="muted xs mono">{{ p.employee.number }}</span></td><td class="num">{{ p.shifts }}</td><td class="num">{{ p.offs }}</td></tr> </ng-container></tbody></table></div>
      <div footer class="row"><button type="button" class="btn" (click)="publishOpen.set(null)">Not yet</button><button type="button" class="btn primary" [disabled]="pb.blocking.length || (pb.warnings.length && !pubAck) || busy()" (click)="publish()">Publish roster and notify {{ pb.affectedCount }} people</button></div>
    </rx-dialog>
  </ng-container>

  <ng-container *ngIf="uploadOpen()">
    <rx-dialog title="Upload roster CSV" [wide]="true" (close)="uploadOpen.set(false)">
      <p class="small muted">Columns: <code>employeeNumber,date,shift</code>. Shift is a code (for example EVE), or OFF, LEAVE, HOL. Rows are checked before anything is saved.</p>
      <div class="row"><input type="file" accept=".csv,text/csv" (change)="readFile($event)" aria-label="Choose CSV file"></div>
      <label class="field" style="margin-top:8px">CSV text<textarea [(ngModel)]="csv" name="csv" rows="5" class="mono"></textarea></label>
      <div class="row" style="margin-top:8px"><button type="button" class="btn" (click)="checkCsv()" [disabled]="!csv.trim()">Check rows</button></div>
      <ng-container *ngIf="csvRes() as r">
        <div class="banner" [class]="r.errorRows ? 'banner danger' : 'banner success'" style="margin-top:10px"><rx-icon [name]="r.errorRows ? 'alert' : 'check'"/><span>{{ r.validRows }} valid row(s), {{ r.errorRows }} with errors. <ng-container *ngIf="r.violations?.length"> {{ r.violations.length }} rule conflict(s) would result. </ng-container></span></div>
        <ng-container *ngIf="r.errorRows"> <ul class="small"><ng-container *ngFor="let x of r.rows"> <ng-container *ngIf="!x.ok"> <li>Row {{ x.row }}: {{ x.message }}</li> </ng-container> </ng-container></ul> </ng-container>
      </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="uploadOpen.set(false)">Cancel</button><button type="button" class="btn primary" (click)="commitCsv()" [disabled]="!csvRes() || csvRes().errorRows || !csvRes().validRows || busy()">Apply {{ csvRes()?.validRows || 0 }} rows</button></div>
    </rx-dialog>
  </ng-container>

  <ng-container *ngIf="prefOpen()">
    <rx-dialog title="Shift preference" (close)="prefOpen.set(false)">
      <p class="small muted">This is a hint used when drafting. It never changes a published roster.</p>
      <div class="form-grid">
        <label class="field">Preferred shift<select [(ngModel)]="pf.shift" name="ps"><option value="">No preference</option><ng-container *ngFor="let s of projectShifts()"> <option [value]="s.id">{{ s.name }}</option> </ng-container></select></label>
        <label class="field">Preferred weekly off<select [(ngModel)]="pf.off" name="po"><option value="">No preference</option><ng-container *ngFor="let d of dows"> <option [value]="d">{{ d }}</option> </ng-container></select></label>
      </div>
      <label class="field" style="margin-top:8px">Note<input type="text" [(ngModel)]="pf.note" name="pn" maxlength="300"></label>
      <div footer class="row"><button type="button" class="btn" (click)="prefOpen.set(false)">Cancel</button><button type="button" class="btn primary" (click)="savePref()">Save preference</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class RosterPage implements OnInit, OnDestroy {
  private api = inject(Api); private route = inject(ActivatedRoute); private router = inject(Router); private toast = inject(Toasts); private panel = inject(AiPanel); session = inject(Session); theme = inject(Theme);
  @ViewChild('grid') grid?: RxRosterGrid;
  projects = signal<J[]>([]); projectId = ''; month = ''; loading = signal(true); busy = signal(false); loadingAi = signal(false);
  g = signal<J | null>(null); preview = signal<J | null>(null); forecast = signal<J | null>(null); pref = signal<J | null>(null); prefName = signal(''); prefOpen = signal(false);
  assign = signal<J | null>(null); publishOpen = signal<J | null>(null); pubAck = false; uploadOpen = signal(false); csv = ''; csvRes = signal<J | null>(null);
  pf: J = { shift: '', off: '', note: '' }; dows = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']; private activeEmp = ''; private prefTick = new Subject<string>(); private check$ = new Subject<void>();
  conflicts = computed(() => this.g()?.validation.violations ?? []);
  projectShifts = computed(() => (this.g()?.shiftTypes ?? this.lookupShifts).filter((s: J) => s.projectShift));
  private lookupShifts: J[] = [];
  options = computed(() => {
    const st = (this.g()?.shiftTypes ?? []) as J[]; const mk = (s: J) => ({ value: 'shift:' + s.id, code: s.code, label: s.name, time: `${s.start}–${s.end}`, eligible: s.eligible });
    return [...st.filter(s => s.projectShift).map(mk), { value: 'off', code: 'OFF', label: 'Weekly off', time: '', eligible: false }, { value: 'leave', code: 'LV', label: 'Leave', time: '', eligible: false }, { value: 'hol', code: 'HOL', label: 'Holiday', time: '', eligible: false }, ...st.filter(s => !s.projectShift).map(mk)];
  });
  pvEmployees = computed(() => { const p = this.preview(); if (!p) return []; const ids = [...new Set((p.payload.entries as J[]).map(e => e.employeeId))]; return ids.map(id => this.names.get(id as string) ?? { id, name: String(id).slice(0, 8), number: '', grade: '' }); });
  pvDays = computed(() => { const p = this.preview(); if (!p) return []; const first = new Date(this.month + '-01T00:00:00'); const n = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate(); return Array.from({ length: n }, (_, i) => { const d = new Date(first.getFullYear(), first.getMonth(), i + 1); const iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(i + 1).padStart(2, '0')}`; return { date: iso, dow: d.toLocaleDateString('en-GB', { weekday: 'short' }) }; }); });
  pvEntries = computed(() => (this.preview()?.payload.entries ?? []).map((e: J) => ({ e: e.employeeId, d: e.workDate, s: e.shiftTypeId, t: e.entryType, ai: true })));
  pvShifts = computed(() => { const ids = new Set<string>((this.preview()?.payload.entries ?? []).map((e: J) => e.shiftTypeId).filter(Boolean)); return this.allShifts.map(s => ({ ...s, projectShift: ids.has(s.id) })); });
  private names = new Map<string, J>(); private allShifts: J[] = [];
  maxAmt = (f: J) => Math.max(f.payload.forecast, ...f.payload.history.map((h: J) => h.amount), 1);
  shiftName = (id: string | null) => this.allShifts.find(s => s.id === id)?.name ?? '—';

  ngOnInit() {
    this.prefTick.pipe(debounceTime(350), switchMap(id => (id ? this.api.get(`/api/ai/preferences/${id}`) : of(null)))).subscribe({ next: p => this.pref.set(p), error: () => this.pref.set(null) });
    this.api.get('/api/lookups').subscribe(l => {
      this.lookupShifts = l.shiftTypes; this.allShifts = l.shiftTypes; this.projects.set(l.projects.filter((p: J) => p.status === 'Active'));
      const q = this.route.snapshot.queryParamMap; this.projectId = q.get('project') ?? this.projects()[0]?.id ?? ''; this.month = q.get('month') ?? l.today.slice(0, 7); this.load();
    });
  }
  ngOnDestroy() { this.panel.clear('roster'); }
  load() {
    if (!this.projectId || !/^\d{4}-\d{2}$/.test(this.month)) return;
    this.loading.set(true); this.preview.set(null); this.forecast.set(null);
    this.api.get<J[]>('/api/rosters', { projectId: this.projectId, month: this.month }).subscribe({
      next: list => { if (!list.length) { this.g.set(null); this.loading.set(false); return; } this.loadGrid(list[0].id); },
      error: e => { this.loading.set(false); this.toast.error(e.title); },
    });
  }
  private loadGrid(id: string, quiet = false) {
    this.api.get(`/api/rosters/${id}`).subscribe({
      next: g => { this.g.set(g); this.loading.set(false); for (const e of g.employees) this.names.set(e.id, e); this.publishPanel(g); if (!quiet && this.session.has('PM', 'LM')) this.api.get('/api/ai/cost-forecast', { projectId: this.projectId, month: this.month }).subscribe({ next: f => this.forecast.set(f), error: () => {} }); },
      error: e => { this.loading.set(false); this.toast.error(e.title); },
    });
  }
  private publishPanel(g: J) {
    const n = g.validation.violations.length; this.panel.publish('roster', [{ key: 'val', kind: 'suggestion', title: 'Rule check (AI-UC-04)', summary: g.validation.summary, impact: g.validation.hasBlock ? 'Fix blocking conflicts before publishing.' : 'You can publish once warnings are acknowledged.', confidence: 0.97 }]);
    void n;
  }
  createBlank() { this.busy.set(true); this.api.post('/api/rosters', { projectId: this.projectId, month: this.month }).subscribe({ next: () => { this.busy.set(false); this.toast.success('Blank roster created.'); this.load(); }, error: e => { this.busy.set(false); this.toast.error(e.title); } }); }
  suggest() {
    this.loadingAi.set(true);
    this.api.get('/api/ai/roster-draft', { projectId: this.projectId, month: this.month }).subscribe({
      next: s => { this.preview.set(s); this.loadingAi.set(false); this.api.get<J[]>('/api/employees', { take: 200 }).subscribe(es => { for (const e of es) this.names.set(e.id, { id: e.id, name: e.name, number: e.number, grade: e.grade }); this.preview.set({ ...s }); }); },
      error: e => { this.loadingAi.set(false); this.toast.error(e.title); },
    });
  }
  acceptDraft() {
    const p = this.preview()!; this.busy.set(true);
    this.api.post('/api/rosters/accept-draft', { projectId: this.projectId, month: this.month, entries: p.payload.entries, confidence: p.confidence }).subscribe({
      next: () => { this.busy.set(false); this.preview.set(null); this.toast.success('Draft roster saved. It is not published; resolve the conflicts, then publish.'); this.load(); },
      error: e => { this.busy.set(false); this.toast.error(e.title); },
    });
  }
  discardDraft() { this.api.post('/api/ai/feedback', { useCaseCode: 'AI-UC-03', contextType: 'Roster', contextId: `${this.projectId}:${this.month}`, action: 'Dismiss', confidence: this.preview()!.confidence }).subscribe(); this.preview.set(null); this.toast.info('Suggestion discarded.'); }

  // ---- assignment (keyboard path + drag accelerator share it)
  openAssign(cells: Cell[], choice = '') {
    if (!cells.length) return; this.assign.set({ cells, choice: choice || this.defaultChoice(cells), ack: false, reason: '', result: null, checking: false, error: '' }); this.recheck();
  }
  private defaultChoice(cells: Cell[]) { const e = (this.g()!.entries as J[]).find(x => x.e === cells[0].e && x.d === cells[0].d); return e ? (e.t === 'Shift' ? 'shift:' + e.s : e.t === 'WeeklyOff' ? 'off' : e.t === 'Leave' ? 'leave' : 'hol') : this.options()[0]?.value; }
  choose(v: string) { this.assign.update(a => ({ ...a, choice: v, ack: false, error: '' })); this.recheck(); }
  listKey(e: KeyboardEvent) { if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return; const opts = this.options().map(o => o.value); const a = this.assign()!; const i = opts.indexOf(a.choice); const n = opts[(i + (e.key === 'ArrowDown' ? 1 : opts.length - 1)) % opts.length]; e.preventDefault(); this.choose(n); setTimeout(() => (document.querySelector('[role=listbox] input:checked') as HTMLElement)?.focus()); }
  private edits(a: J) { const c = a.choice as string; return a.cells.map((x: Cell) => ({ employeeId: x.e, workDate: x.d, shiftTypeId: c.startsWith('shift:') ? c.slice(6) : null, entryType: c.startsWith('shift:') ? 'Shift' : c === 'off' ? 'WeeklyOff' : c === 'leave' ? 'Leave' : 'Holiday' })); }
  private recheckSub?: any;
  recheck() {
    const a = this.assign(); if (!a) return; this.assign.set({ ...a, checking: true, result: null });
    this.recheckSub?.unsubscribe();
    // ≤200 ms debounce: coalesces rapid arrow-key changes in the picker
    this.recheckSub = of(0).pipe(debounceTime(200), switchMap(() => this.api.post('/api/ai/validate', { projectId: this.projectId, month: this.month, edits: this.edits(this.assign()!) }))).subscribe({
      next: v => {
        const have = new Set((this.g()!.validation.violations as J[]).map(x => `${x.employeeId}|${x.ruleCode}|${x.message}`));
        const fresh = (v.payload.violations as J[]).filter(x => !have.has(`${x.employeeId}|${x.ruleCode}|${x.message}`));
        this.assign.update(c => (c ? { ...c, checking: false, result: { fresh, block: fresh.some(f => f.severity === 'Block'), warn: fresh.some(f => f.severity === 'Warn'), existing: (v.payload.violations as J[]).length - fresh.length } } : c));
      },
      error: e => this.assign.update(c => (c ? { ...c, checking: false, error: e.title } : c)),
    });
  }
  confirmAssign() {
    const a = this.assign()!; this.busy.set(true);
    const ack = a.result?.warn ? [...new Set<string>((a.result.fresh as J[]).filter(f => f.severity === 'Warn').map(f => f.ruleCode))] : [];
    this.api.put(`/api/rosters/${this.g()!.roster.id}/entries`, { edits: this.edits(a), acknowledged: ack, reason: a.reason || null }).subscribe({
      next: r => { this.busy.set(false); const focus = a.cells[0]; this.assign.set(null); this.toast.success(r.applied ? `Saved ${r.applied} change${r.applied === 1 ? '' : 's'}.` : 'Nothing changed.'); this.grid?.clearSelection(); this.loadGrid(this.g()!.roster.id, true); setTimeout(() => this.grid?.focusCell(focus.e, focus.d), 350); },
      error: e => { this.busy.set(false); this.assign.update(c => (c ? { ...c, error: e.title } : c)); },
    });
  }
  onDrop(ev: DropEvent) { this.openAssign(ev.cells, ev.choice); }
  drag(e: DragEvent, choice: string) { e.dataTransfer?.setData('text/rx-choice', choice); e.dataTransfer!.effectAllowed = 'copy'; }
  cellLabel(c: Cell) { const g = this.g()!; return `${g.employees.find((e: J) => e.id === c.e)?.name}, ${new Date(c.d + 'T00:00:00').toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' })}`; }
  empCount(cells: Cell[]) { return new Set(cells.map(c => c.e)).size; }
  goTo(v: J) { const d = (v.affected as string[]).find(a => a.startsWith('date:'))?.slice(5) ?? ((v.affected as string[]).find(a => a.startsWith('week:'))?.slice(5)); if (d) this.grid?.focusCell(v.employeeId, d); }
  onActiveEmployee(id: string) { if (id && id !== this.activeEmp) { this.activeEmp = id; this.prefName.set(this.names.get(id)?.name ?? ''); this.prefTick.next(id); } }

  // ---- publish
  openPublish() { this.pubAck = false; this.api.get(`/api/rosters/${this.g()!.roster.id}/publish-preview`).subscribe({ next: p => this.publishOpen.set(p), error: e => this.toast.error(e.title) }); }
  publish() {
    this.busy.set(true); const warns = [...new Set<string>((this.publishOpen()!.warnings as J[]).map(w => w.ruleCode))];
    this.api.post(`/api/rosters/${this.g()!.roster.id}/publish`, { confirm: true, acknowledged: warns }).subscribe({ next: r => { this.busy.set(false); this.publishOpen.set(null); this.toast.success(`Published. ${r.notified} people were notified${r.leadTimeFlag ? ' (marked short notice)' : ''}.`); this.load(); }, error: e => { this.busy.set(false); this.toast.error(e.title); } });
  }

  // ---- CSV
  downloadCsv() { this.api.blob(`/api/rosters/${this.g()!.roster.id}/download`).subscribe(r => download(r.body!, `roster-${this.month}.csv`)); }
  readFile(e: Event) { const f = (e.target as HTMLInputElement).files?.[0]; if (f) f.text().then(t => (this.csv = t)); }
  checkCsv() { this.api.post(`/api/rosters/${this.g()!.roster.id}/upload`, { csv: this.csv }, { commit: false }).subscribe({ next: r => this.csvRes.set(r), error: e => this.toast.error(e.title) }); }
  commitCsv() { this.busy.set(true); this.api.post(`/api/rosters/${this.g()!.roster.id}/upload`, { csv: this.csv }, { commit: true }).subscribe({ next: r => { this.busy.set(false); this.uploadOpen.set(false); this.csvRes.set(null); this.csv = ''; this.toast.success(`Uploaded: ${r.applied} change(s) saved.`); this.loadGrid(this.g()!.roster.id, true); }, error: e => { this.busy.set(false); this.toast.error(e.title); } }); }

  // ---- preference (AI-UC-08 override)
  savePref() {
    const id = this.activeEmp; this.api.put(`/api/employees/${id}/preferences`, { preferredShiftTypeId: this.pf.shift || null, preferredOffDay: this.pf.off || null, note: this.pf.note || null }).subscribe({
      next: () => { this.prefOpen.set(false); this.toast.success('Preference saved. It is used the next time a draft is suggested.'); this.prefTick.next(id); }, error: e => this.toast.error(e.title) });
  }
}
