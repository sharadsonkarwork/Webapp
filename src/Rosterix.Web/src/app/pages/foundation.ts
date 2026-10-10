import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, J } from '../core/api.service';
import { Toasts } from '../core/ui.services';
import { DatePipeRx, InrPipe } from '../shared/fmt';
import { Column, RxDialog, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxTable } from '../shared/ui';

@Component({
  selector: 'rx-foundation', standalone: true, imports: [CommonModule, FormsModule, RxPageHeader, RxTable, RxStatus, RxIcon, RxDialog, RxEmpty, DatePipeRx, InrPipe],
  template: `
  <rx-page-header title="Foundation data" sub="Shift types, rule limits, allowance rates and holidays. Every change is effective-dated and audited; changing a rate never rewrites a submitted payroll batch."/>
  <div class="tabs" role="tablist" aria-label="Foundation sections"><ng-container *ngFor="let t of tabs"> <button type="button" role="tab" [id]="'ft-' + t.k" [attr.aria-selected]="tab() === t.k" [attr.aria-controls]="'fp-' + t.k" [tabindex]="tab() === t.k ? 0 : -1" (click)="tab.set(t.k)" (keydown)="key($event)">{{ t.l }}</button> </ng-container></div>
  <div role="tabpanel" [id]="'fp-' + tab()" [attr.aria-labelledby]="'ft-' + tab()" tabindex="0" style="margin-top:14px" class="stack">
  <ng-container [ngSwitch]="tab()"><ng-container *ngSwitchCase="'shifts'">
      <div class="row between"><p class="muted small">Allowance applies only to shift types marked eligible.</p><button type="button" class="btn primary" (click)="editShift({})"><rx-icon name="plus"/>New shift type</button></div>
      <div class="card"><div class="card-b" style="padding:0"><rx-table [rows]="shifts()" [columns]="shiftCols" [rowTpl]="sAct" caption="Shift types" [paged]="false"/><ng-template #sAct let-r><button type="button" class="btn sm" (click)="editShift(r)" [attr.aria-label]="'Edit ' + r.name">Edit</button></ng-template></div></div>
    </ng-container><ng-container *ngSwitchCase="'rules'">
      <div class="banner info"><rx-icon name="info"/><span>Limits live here, not in code. Saving creates a new version from the date you choose; the previous version stays on record.</span></div>
      <div class="card"><div class="card-b" style="padding:0"><rx-table [rows]="rules()" [columns]="ruleCols" [rowTpl]="rAct" caption="Shift rules" [paged]="false"/><ng-template #rAct let-r><ng-container *ngIf="r.current"> <button type="button" class="btn sm" (click)="editRule(r)" [attr.aria-label]="'Edit ' + r.code">New version…</button> </ng-container></ng-template></div></div>
    </ng-container><ng-container *ngSwitchCase="'rates'">
      <div class="row between"><label class="field" style="flex-direction:row;align-items:center;gap:6px">Shift<select [(ngModel)]="rateShift" name="rs" aria-label="Filter rates by shift"><option value="">All</option><ng-container *ngFor="let s of shifts()"> <ng-container *ngIf="s.isAllowanceEligible"> <option [value]="s.code">{{ s.code }}</option> </ng-container> </ng-container></select></label>
        <button type="button" class="btn primary" (click)="editRate()"><rx-icon name="plus"/>New rate or revision</button></div>
      <div class="card"><div class="card-b" style="padding:0"><rx-table [rows]="rateRows()" [columns]="rateCols" caption="Allowance rates" [pageSize]="30" emptyTitle="No rates"/></div></div>
    </ng-container><ng-container *ngSwitchCase="'holidays'">
      <div class="row between"><label class="field" style="flex-direction:row;align-items:center;gap:6px">Year<input type="number" [(ngModel)]="year" name="y" (ngModelChange)="loadHolidays()" min="2024" max="2035" style="width:90px" aria-label="Year"></label><button type="button" class="btn primary" (click)="holiday.set({ regionId: regions()[0]?.id, date: '', name: '', isRestricted: false })"><rx-icon name="plus"/>Add holiday</button></div>
      <div class="card"><div class="card-b" style="padding:0"><rx-table [rows]="holidays()" [columns]="holCols" [rowTpl]="hAct" caption="Holiday calendar" [pageSize]="30"/><ng-template #hAct let-r><button type="button" class="btn sm" (click)="toggleHoliday(r)">{{ r.isRestricted ? 'Make unrestricted' : 'Make restricted' }}</button></ng-template></div></div>
    </ng-container></ng-container>
  </div>

  <ng-container *ngIf="sh() as s">
    <rx-dialog [title]="s.id ? 'Edit shift type' : 'New shift type'" (close)="sh.set(null)">
      <div class="form-grid"><label class="field">Code<input type="text" [(ngModel)]="s.code" name="c" [disabled]="!!s.id" maxlength="20" data-autofocus></label><label class="field">Name<input type="text" [(ngModel)]="s.name" name="n"></label>
        <label class="field">Start<input type="time" [(ngModel)]="s.start" name="st"></label><label class="field">End<input type="time" [(ngModel)]="s.end" name="en"></label></div>
      <label class="check"><input type="checkbox" [(ngModel)]="s.isAllowanceEligible" name="e"> Allowance-eligible</label> <ng-container *ngIf="s.id"> <label class="check"><input type="checkbox" [(ngModel)]="s.isActive" name="a"> Active</label> </ng-container>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="sh.set(null)">Cancel</button><button type="button" class="btn primary" (click)="saveShift()">Save</button></div>
    </rx-dialog>
  </ng-container>
  <ng-container *ngIf="ru() as r">
    <rx-dialog [title]="'New version of ' + r.code" (close)="ru.set(null)">
      <p class="small muted">{{ r.name }}. Current: <span class="mono">{{ r.parameters }}</span>, {{ r.severity }}.</p>
      <div class="form-grid"><ng-container *ngIf="r.key"> <label class="field">{{ r.label }}<input type="number" [(ngModel)]="r.value" name="v" min="1" step="0.5" data-autofocus></label> </ng-container>
        <label class="field">Severity<select [(ngModel)]="r.severity" name="sv"><option>Block</option><option>Warn</option></select></label><label class="field">Effective from<input type="date" [(ngModel)]="r.from" name="ef"></label></div>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="ru.set(null)">Cancel</button><button type="button" class="btn primary" (click)="saveRule()">Save new version</button></div>
    </rx-dialog>
  </ng-container>
  <ng-container *ngIf="ra() as r">
    <rx-dialog title="New rate or revision" (close)="ra.set(null)">
      <p class="small muted">Adding a rate that starts on a future date closes the current one the day before. Existing allowance lines keep the rate they were calculated with.</p>
      <div class="form-grid"><label class="field">Shift type<select [(ngModel)]="r.shiftTypeId" name="sh"><option value="">Choose</option><ng-container *ngFor="let s of shifts()"> <ng-container *ngIf="s.isAllowanceEligible"> <option [value]="s.id">{{ s.code }} · {{ s.name }}</option> </ng-container> </ng-container></select></label>
        <label class="field">Business unit<select [(ngModel)]="r.businessUnitId" name="bu"><option [ngValue]="null">All</option><ng-container *ngFor="let b of bus()"> <option [value]="b.id">{{ b.name }}</option> </ng-container></select></label>
        <label class="field">Grade<select [(ngModel)]="r.grade" name="gr"><option value="">All</option><ng-container *ngFor="let g of grades"> <option>{{ g }}</option> </ng-container></select></label>
        <label class="field">Amount (₹)<input type="number" [(ngModel)]="r.amount" name="am" min="1"></label><label class="field">Effective from<input type="date" [(ngModel)]="r.effectiveFrom" name="ef"></label></div>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="ra.set(null)">Cancel</button><button type="button" class="btn primary" (click)="saveRate()">Save rate</button></div>
    </rx-dialog>
  </ng-container>
  <ng-container *ngIf="holiday() as h">
    <rx-dialog title="Add holiday" (close)="holiday.set(null)">
      <div class="form-grid"><label class="field">Region<select [(ngModel)]="h.regionId" name="rg"><ng-container *ngFor="let r of regions()"> <option [value]="r.id">{{ r.name }}</option> </ng-container></select></label><label class="field">Date<input type="date" [(ngModel)]="h.date" name="d" data-autofocus></label><label class="field">Name<input type="text" [(ngModel)]="h.name" name="n"></label></div>
      <label class="check"><input type="checkbox" [(ngModel)]="h.isRestricted" name="r"> Restricted (rostering a shift needs acknowledgement)</label>
      <ng-container *ngIf="err()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ err() }}</span></div> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="holiday.set(null)">Cancel</button><button type="button" class="btn primary" (click)="saveHoliday()">Add</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class FoundationPage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts);
  tabs = [{ k: 'shifts', l: 'Shift types' }, { k: 'rules', l: 'Rules' }, { k: 'rates', l: 'Allowance rates' }, { k: 'holidays', l: 'Holidays' }]; tab = signal('shifts');
  shifts = signal<J[]>([]); rules = signal<J[]>([]); rates = signal<J[]>([]); holidays = signal<J[]>([]); bus = signal<J[]>([]); regions = signal<J[]>([]); rateShift = ''; year = 2026; err = signal('');
  sh = signal<J | null>(null); ru = signal<J | null>(null); ra = signal<J | null>(null); holiday = signal<J | null>(null); grades = ['G1', 'G2', 'G3', 'G4', 'G5', 'G6', 'G7'];
  shiftCols: Column[] = [{ key: 'code', label: 'Code', type: 'mono' }, { key: 'name', label: 'Name' }, { key: 'start', label: 'Start', type: 'mono' }, { key: 'end', label: 'End', type: 'mono' }, { key: 'hours', label: 'Hours', type: 'num' }, { key: 'elig', label: 'Allowance' }, { key: 'act', label: 'Active' }];
  ruleCols: Column[] = [{ key: 'code', label: 'Rule', type: 'mono' }, { key: 'name', label: 'Name' }, { key: 'parameters', label: 'Parameters', type: 'mono' }, { key: 'severity', label: 'Severity', type: 'chip' }, { key: 'effectiveFrom', label: 'From', type: 'date' }, { key: 'effectiveTo', label: 'To', type: 'date' }, { key: 'cur', label: 'Version' }];
  rateCols: Column[] = [{ key: 'code', label: 'Shift', type: 'mono' }, { key: 'businessUnit', label: 'Business unit' }, { key: 'grade', label: 'Grade' }, { key: 'amount', label: 'Amount', type: 'money' }, { key: 'effectiveFrom', label: 'From', type: 'date' }, { key: 'effectiveTo', label: 'To', type: 'date' }, { key: 'cur', label: 'Status' }];
  holCols: Column[] = [{ key: 'date', label: 'Date', type: 'date' }, { key: 'name', label: 'Holiday' }, { key: 'region', label: 'Region' }, { key: 'rest', label: 'Restricted' }];
  rateRows = computed(() => this.rates().filter(r => !this.rateShift || r.code === this.rateShift).map(r => ({ ...r, cur: r.current ? 'Current' : r.effectiveTo ? 'Ended' : 'Future' })));
  ngOnInit() { this.loadShifts(); this.loadRules(); this.loadRates(); this.loadHolidays(); this.api.get('/api/lookups').subscribe(l => { this.bus.set(l.businessUnits); this.regions.set([...new Map((l.businessUnits as J[]).map(b => [b.regionId, { id: b.regionId, name: '' }])).values()]); this.api.get<J[]>('/api/foundation/holidays', { year: 2026 }).subscribe(h => { const m = new Map<string, string>(); h.forEach(x => m.set(x.regionId, x.region)); this.regions.set([...m].map(([id, name]) => ({ id, name }))); }); }); }
  key(e: KeyboardEvent) { const ks = this.tabs.map(t => t.k); const i = ks.indexOf(this.tab()); const n = e.key === 'ArrowRight' ? (i + 1) % ks.length : e.key === 'ArrowLeft' ? (i + ks.length - 1) % ks.length : -1; if (n >= 0) { this.tab.set(ks[n]); setTimeout(() => document.getElementById('ft-' + ks[n])?.focus()); } }
  loadShifts() { this.api.get<J[]>('/api/foundation/shift-types').subscribe(r => this.shifts.set(r.map(s => ({ ...s, elig: s.isAllowanceEligible ? 'Yes' : 'No', act: s.isActive ? 'Yes' : 'No' })))); }
  loadRules() { this.api.get<J[]>('/api/foundation/shift-rules').subscribe(r => this.rules.set(r.map(x => ({ ...x, cur: x.current ? 'Current' : 'Superseded' })))); }
  loadRates() { this.api.get<J[]>('/api/foundation/rates').subscribe(r => this.rates.set(r)); }
  loadHolidays() { this.api.get<J[]>('/api/foundation/holidays', { year: this.year }).subscribe(r => this.holidays.set(r.map(h => ({ ...h, rest: h.isRestricted ? 'Restricted' : 'Optional' })))); }
  editShift(s: J) { this.err.set(''); this.sh.set(s.id ? { ...s } : { code: '', name: '', start: '09:00', end: '17:00', isAllowanceEligible: false, isActive: true }); }
  saveShift() { const s = this.sh()!; const req = s.id ? this.api.put(`/api/foundation/shift-types/${s.id}`, s) : this.api.post('/api/foundation/shift-types', s); req.subscribe({ next: () => { this.sh.set(null); this.toast.success('Shift type saved.'); this.loadShifts(); }, error: e => this.err.set(e.title) }); }
  editRule(r: J) { this.err.set(''); const p = JSON.parse(r.parameters || '{}'); const key = Object.keys(p)[0]; const labels: J = { max: 'Maximum days', hours: 'Hours', min: 'Minimum' }; const next = new Date(); next.setDate(next.getDate() + 1); this.ru.set({ ...r, key, label: labels[key] ?? key, value: key ? p[key] : null, from: new Date(Date.now() + 86400000 * 30).toISOString().slice(0, 10) }); void next; }
  saveRule() { const r = this.ru()!; const params = r.key ? JSON.stringify({ [r.key]: Number(r.value) }) : '{}'; this.api.post('/api/foundation/shift-rules', { code: r.code, parameters: params, severity: r.severity, effectiveFrom: r.from }).subscribe({ next: () => { this.ru.set(null); this.toast.success('New rule version saved. It takes effect on the date you chose.'); this.loadRules(); }, error: e => this.err.set(e.title) }); }
  editRate() { this.err.set(''); this.ra.set({ shiftTypeId: '', businessUnitId: null, grade: '', amount: null, effectiveFrom: '' }); }
  saveRate() { const r = this.ra()!; this.api.post('/api/foundation/rates', { shiftTypeId: r.shiftTypeId, businessUnitId: r.businessUnitId, grade: r.grade || null, amount: r.amount, effectiveFrom: r.effectiveFrom }).subscribe({ next: () => { this.ra.set(null); this.toast.success('Rate saved. Past calculations are unchanged.'); this.loadRates(); }, error: e => this.err.set(e.title) }); }
  saveHoliday() { const h = this.holiday()!; this.err.set(''); this.api.post('/api/foundation/holidays', h).subscribe({ next: () => { this.holiday.set(null); this.toast.success('Holiday added.'); this.loadHolidays(); }, error: e => this.err.set(e.title) }); }
  toggleHoliday(h: J) { this.api.put(`/api/foundation/holidays/${h.id}`, { regionId: h.regionId, date: h.date, name: h.name, isRestricted: !h.isRestricted }).subscribe({ next: () => { this.toast.success('Updated.'); this.loadHolidays(); }, error: e => this.toast.error(e.title) }); }
}

@Component({
  selector: 'rx-audit', standalone: true, imports: [CommonModule, FormsModule, RxPageHeader, RxStatus, RxEmpty, DatePipeRx],
  template: `
  <rx-page-header title="Audit trail" sub="Every state-changing action: who, when, from where, and whether it succeeded. Sensitive values are masked."/>
  <div class="card"><div class="card-h"><label class="field"><span class="sr-only">Search</span><input type="search" [(ngModel)]="q" name="q" placeholder="Search event or endpoint" aria-label="Search audit trail" (keydown.enter)="page = 1; load()" (change)="page = 1; load()"></label><span class="muted small" aria-live="polite">{{ total }} entries</span></div>
    <div class="table-wrap"><table class="rx"><caption class="sr-only">Audit trail</caption><thead><tr><th>When (UTC)</th><th>Actor</th><th>Event</th><th>Result</th><th>Endpoint</th><th>Origin</th></tr></thead><tbody>
      <ng-container *ngFor="let r of rows()"> <tr><td class="mono" style="white-space:nowrap">{{ r.occurredAtUtc.replace('T',' ').slice(0,19) }}</td><td>{{ r.actor || '—' }}</td><td>{{ r.eventType }}</td><td><rx-status [value]="r.succeeded ? 'Succeeded' : 'Failed'"/></td><td class="mono">{{ r.endpoint }}</td><td class="mono">{{ r.originIp }}</td></tr> </ng-container>
      <ng-container *ngIf="!rows().length"> <tr><td colspan="6"><rx-empty title="No audit entries yet"/></td></tr> </ng-container></tbody></table></div>
    <div class="pager"><button type="button" class="btn sm" [disabled]="page === 1" (click)="page = page - 1; load()">Previous</button><button type="button" class="btn sm" [disabled]="page * 50 >= total" (click)="page = page + 1; load()">Next</button></div></div>`,
})
export class AuditPage implements OnInit {
  private api = inject(Api); rows = signal<J[]>([]); total = 0; page = 1; q = '';
  ngOnInit() { this.load(); }
  load() { this.api.get('/api/audit', { page: this.page, pageSize: 50, q: this.q }).subscribe(r => { this.rows.set(r.rows); this.total = r.total; }); }
}
