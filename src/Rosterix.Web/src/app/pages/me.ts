import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { Toasts } from '../core/ui.services';
import { DatePipeRx, DateTimePipeRx, InrPipe, MonthPipe } from '../shared/fmt';
import { RxAiCard, RxAiSkeleton, RxDialog, RxEmpty, RxIcon, RxPageHeader, RxStatus, RxConfidence, RxWhy } from '../shared/ui';

@Component({
  selector: 'rx-me', standalone: true, imports: [CommonModule, FormsModule, RxPageHeader, RxStatus, RxIcon, RxDialog, RxEmpty, RxAiCard, RxAiSkeleton, RxConfidence, RxWhy, DatePipeRx, DateTimePipeRx, InrPipe, MonthPipe],
  template: `
  <rx-page-header title="My work" sub="Your roster, how your allowance was calculated, and things to sign."/>
  <div class="tabs" role="tablist" aria-label="My work sections">
    <ng-container *ngFor="let t of tabs"> <button type="button" role="tab" [id]="'mt-' + t.k" [attr.aria-selected]="tab() === t.k" [attr.aria-controls]="'mp-' + t.k" [tabindex]="tab() === t.k ? 0 : -1" (click)="tab.set(t.k)" (keydown)="key($event)">{{ t.l }}<ng-container *ngIf="t.k === 'decl' && due()"> <span class="chip warn" style="margin-left:6px">{{ due() }}</span> </ng-container></button> </ng-container>
  </div>
  <div role="tabpanel" [id]="'mp-' + tab()" [attr.aria-labelledby]="'mt-' + tab()" tabindex="0" style="margin-top:14px">
  <ng-container [ngSwitch]="tab()"><ng-container *ngSwitchCase="'roster'">
      <div class="row" style="margin-bottom:10px"><label class="field" style="flex-direction:row;align-items:center;gap:6px">Month<input type="month" [(ngModel)]="month" name="m" (ngModelChange)="loadRoster()" aria-label="Month"></label>
        <ng-container *ngIf="roster() as r"> <span class="chip neutral">{{ r.shifts }} shifts</span><span class="chip neutral">{{ r.offs }} weekly offs</span> </ng-container></div>
      <ng-container *ngIf="roster() as r">
        <ng-container *ngIf="fellBackFrom()"> <div class="banner info" style="margin-bottom:10px"><rx-icon name="info"/><span>Your {{ fellBackFrom() | rxMonth }} roster isn’t published yet, so this shows {{ month | rxMonth }}. You’ll be notified when the new one is ready.</span></div> </ng-container>
        <ng-container *ngIf="r.draftPending && !fellBackFrom()"> <div class="banner info" style="margin-bottom:10px"><rx-icon name="clock"/><span>Your {{ month | rxMonth }} roster isn’t published yet. You’ll be notified when it is.</span></div> </ng-container>
        <ng-container *ngIf="r.entries.length">
          <div class="card"><div class="table-wrap"><table class="rx comfortable"><caption class="sr-only">My roster for {{ month }}</caption><thead><tr><th scope="col">Date</th><th scope="col">Shift</th><th scope="col">Time</th><th scope="col">Project</th></tr></thead><tbody>
            <ng-container *ngFor="let e of r.entries"> <tr><th scope="row" class="mono" style="white-space:nowrap;background:transparent;position:static;color:var(--rx-text);font-size:14px;font-weight:500">{{ e.date | rxDate:false }}</th><td><ng-container *ngIf="e.type === 'Shift'"> <strong>{{ e.shift }}</strong> </ng-container><ng-container *ngIf="!(e.type === 'Shift')"> <rx-status [value]="e.type === 'WeeklyOff' ? 'Closed' : e.type === 'Leave' ? 'Pending' : 'Active'" [label]="e.type === 'WeeklyOff' ? 'Weekly off' : e.type"/> </ng-container><ng-container *ngIf="e.holiday"> <span class="muted small"> · {{ e.holiday }}</span> </ng-container></td><td class="mono">{{ e.time || '—' }}</td><td>{{ e.project }}</td></tr> </ng-container></tbody></table></div></div>
        </ng-container><ng-container *ngIf="!(r.entries.length) && (!r.draftPending)"> <rx-empty title="No roster for this month" text="Nothing is published for you in this month."/> </ng-container>
        <section class="card" style="margin-top:16px" aria-labelledby="ch"><div class="card-h"><h2 id="ch" style="font-size:16px">Recent changes to my roster</h2></div><div class="card-b" style="padding:0">
          <ng-container *ngFor="let c of r.changes"> <div class="queue-item" style="grid-template-columns:1fr auto"><div><strong>{{ c.workDate | rxDate:false }}</strong>: {{ c.before }} → {{ c.after }}<div class="small muted">{{ c.changedAtUtc | rxDateTime }}</div></div><ng-container *ngIf="c.leadTimeFlag"> <span class="chip warn"><rx-icon name="clock" [size]="12"/>Short notice</span> </ng-container></div> </ng-container>
          <ng-container *ngIf="!r.changes.length"> <rx-empty title="No changes" text="Your roster hasn’t changed since it was published."/> </ng-container></div></section>
      </ng-container><ng-container *ngIf="!(roster())"> <p class="muted" role="status">Loading…</p> </ng-container>
    </ng-container><ng-container *ngSwitchCase="'allowance'">
      <div class="row" style="margin-bottom:10px"><label class="field" style="flex-direction:row;align-items:center;gap:6px">Month<input type="month" [(ngModel)]="amonth" name="am" (ngModelChange)="loadAllowance()" aria-label="Month"></label><button type="button" class="btn" (click)="openQuery()"><rx-icon name="info"/>Raise a query</button></div>
      <ng-container *ngIf="loadingAl()"> <rx-ai-skeleton/> </ng-container><ng-container *ngIf="!(loadingAl())"><ng-container *ngIf="al() as a">
        <rx-ai-card heading="Your allowance, explained" [summary]="a.summary" [impact]="a.impact" [confidence]="a.confidence" [reasons]="a.reasons"/>
        <div class="card" style="margin-top:12px"><div class="card-b" style="padding:0">
          <ng-container *ngFor="let l of a.payload.lines"> <details class="queue-item" style="display:block"><summary style="cursor:pointer;display:flex;gap:12px;align-items:center;min-height:32px"><span class="mono" style="min-width:90px">{{ l.date | rxDate:false }}</span><strong class="grow">{{ l.shift }}</strong><span class="num">{{ l.amount | inr }}</span></summary><p class="small" style="margin:8px 0 0">{{ l.trace }}</p></details> </ng-container>
          <ng-container *ngIf="!a.payload.lines.length"> <rx-empty title="No allowance-eligible shifts" text="Only some shift types earn an allowance."/> </ng-container>
          <ng-container *ngIf="a.payload.lines.length"> <div class="row between" style="padding:12px 14px"><strong>Total</strong><strong class="mono">{{ a.payload.total | inr }}</strong></div> </ng-container>
        </div></div>
      </ng-container></ng-container>
    </ng-container><ng-container *ngSwitchCase="'decl'">
      <div class="card"><div class="card-b" style="padding:0">
        <ng-container *ngFor="let d of decls()"> <div class="queue-item" style="grid-template-columns:1fr auto"><div><div class="t">{{ d.type }}</div><div class="small muted">{{ d.periodMonth | rxMonth }}<ng-container *ngIf="d.signedAtUtc"> · signed {{ d.signedAtUtc | rxDate }} </ng-container></div></div><div class="row"><rx-status [value]="d.status"/><ng-container *ngIf="d.status === 'Due'"> <button type="button" class="btn sm primary" (click)="signing.set(d)">Review and sign…</button> </ng-container></div></div> </ng-container>
        <ng-container *ngIf="!decls().length"> <rx-empty title="Nothing to sign"/> </ng-container></div></div>
    </ng-container><ng-container *ngSwitchCase="'pref'">
      <ng-container *ngIf="pref() as p">
        <rx-ai-card heading="What Rosterix has learned about your shifts" [summary]="p.summary" [impact]="p.impact" [confidence]="p.confidence" [reasons]="p.reasons">
          <ul class="small" style="margin:6px 0 0;padding-left:18px"><ng-container *ngFor="let a of p.payload.affinity.slice(0, 4)"> <li>{{ a.name }}: {{ (a.share * 100).toFixed(0) }}% of shifts</li> </ng-container></ul></rx-ai-card>
        <form class="card" style="margin-top:12px" (ngSubmit)="savePref()"><div class="card-h"><h2 style="font-size:16px">Tell Rosterix your preference</h2></div><div class="card-b stack"><p class="small muted">Your preference is a hint for the person building your roster. It never changes a published roster.</p>
          <div class="form-grid"><label class="field">Preferred shift<select [(ngModel)]="pf.shift" name="ps"><option value="">No preference</option><ng-container *ngFor="let s of shifts()"> <option [value]="s.id">{{ s.name }}</option> </ng-container></select></label>
            <label class="field">Preferred weekly off<select [(ngModel)]="pf.off" name="po"><option value="">No preference</option><ng-container *ngFor="let d of dows"> <option>{{ d }}</option> </ng-container></select></label></div>
          <label class="field">Note<input type="text" [(ngModel)]="pf.note" name="pn" maxlength="300"></label><div><button type="submit" class="btn primary">Save preference</button></div></div></form>
      </ng-container><ng-container *ngIf="!(pref())"> <rx-ai-skeleton/> </ng-container>
    </ng-container><ng-container *ngSwitchCase="'queries'">
      <div class="card"><div class="card-b" style="padding:0"><ng-container *ngFor="let q of queries()"> <div class="queue-item" style="grid-template-columns:1fr auto"><div><div class="t">{{ q.message }}</div><div class="small muted">{{ q.periodMonth | rxMonth }} · {{ q.createdAtUtc | rxDate }}</div></div><rx-status [value]="q.status"/></div> </ng-container> <ng-container *ngIf="!queries().length"> <rx-empty title="No queries" text="Use “Raise a query” on the allowance tab if something looks wrong."/> </ng-container></div></div>
    </ng-container></ng-container>
  </div>

  <ng-container *ngIf="signing() as d">
    <rx-dialog title="Sign declaration" (close)="signing.set(null)">
      <p><strong>{{ d.type }}</strong> for {{ d.periodMonth | rxMonth }}.</p><p>I confirm that the shifts and allowance shown for this period are a true record of the work I did.</p>
      <div footer class="row"><button type="button" class="btn" (click)="signing.set(null)">Cancel</button><button type="button" class="btn primary" (click)="sign()" [disabled]="busy()">I confirm and sign</button></div>
    </rx-dialog>
  </ng-container>
  <ng-container *ngIf="queryOpen()">
    <rx-dialog title="Raise an allowance query" (close)="queryOpen.set(false)">
      <p class="small muted">For {{ amonth | rxMonth }}. HRSS will reply by notification.</p>
      <label class="field">What looks wrong?<textarea [(ngModel)]="qmsg" name="qm" rows="4" maxlength="1000" data-autofocus [attr.aria-invalid]="qTouched() && !qmsg.trim()"></textarea></label>
      <ng-container *ngIf="qTouched() && !qmsg.trim()"> <p class="err" role="alert">Describe your question so we can look into it.</p> </ng-container>
      <div footer class="row"><button type="button" class="btn" (click)="queryOpen.set(false)">Cancel</button><button type="button" class="btn primary" (click)="sendQuery()">Send query</button></div>
    </rx-dialog>
  </ng-container>`,
})
export class MePage implements OnInit {
  private api = inject(Api); private toast = inject(Toasts); private session = inject(Session);
  tabs = [{ k: 'roster', l: 'My roster' }, { k: 'allowance', l: 'My allowance explained' }, { k: 'decl', l: 'Declarations' }, { k: 'pref', l: 'Preferences' }, { k: 'queries', l: 'My queries' }];
  tab = signal('roster'); month = ''; amonth = ''; roster = signal<J | null>(null); al = signal<J | null>(null); loadingAl = signal(false); decls = signal<J[]>([]); queries = signal<J[]>([]); pref = signal<J | null>(null); shifts = signal<J[]>([]);
  signing = signal<J | null>(null); busy = signal(false); queryOpen = signal(false); qTouched = signal(false); qmsg = ''; pf: J = { shift: '', off: '', note: '' }; dows = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
  due = computed(() => this.decls().filter(d => d.status === 'Due').length);
  ngOnInit() {
    const t = this.session.me()!.today as string; this.month = t.slice(0, 7); const p = new Date(t); p.setMonth(p.getMonth() - 1); this.amonth = `${p.getFullYear()}-${String(p.getMonth() + 1).padStart(2, '0')}`;
    this.loadRoster(true); this.loadAllowance(); this.loadDecls(); this.api.get<J[]>('/api/me/queries').subscribe(q => this.queries.set(q));
    this.api.get(`/api/ai/preferences/${this.session.me()!.id}`).subscribe(p => { this.pref.set(p); this.pf = { shift: p.payload.overrideShiftTypeId ?? '', off: p.payload.overrideOffDay ?? '', note: p.payload.overrideNote ?? '' }; });
    this.api.get('/api/lookups').subscribe(l => this.shifts.set(l.shiftTypes.filter((s: J) => s.isActive)));
  }
  key(e: KeyboardEvent) { const ks = this.tabs.map(t => t.k); const i = ks.indexOf(this.tab()); const n = e.key === 'ArrowRight' ? (i + 1) % ks.length : e.key === 'ArrowLeft' ? (i + ks.length - 1) % ks.length : -1; if (n >= 0) { this.tab.set(ks[n]); setTimeout(() => document.getElementById('mt-' + ks[n])?.focus()); } }
  fellBackFrom = signal('');
  loadRoster(allowFallback = false) {
    this.roster.set(null); this.fellBackFrom.set(allowFallback ? this.fellBackFrom() : '');
    this.api.get('/api/me/roster', { month: this.month }).subscribe(r => {
      // If this month isn't published yet, show the latest month that is (and say so), so the page is never empty for no reason.
      if (allowFallback && !r.entries.length) { const d = new Date(this.month + '-01T00:00:00'); d.setMonth(d.getMonth() - 1); this.fellBackFrom.set(this.month); this.month = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`; this.loadRoster(false); return; }
      this.roster.set(r);
    });
  }
  loadAllowance() { this.loadingAl.set(true); this.api.get('/api/me/allowance', { month: this.amonth }).subscribe({ next: a => { this.al.set(a); this.loadingAl.set(false); }, error: () => this.loadingAl.set(false) }); }
  loadDecls() { this.api.get<J[]>('/api/me/declarations').subscribe(d => this.decls.set(d)); }
  sign() { this.busy.set(true); this.api.post(`/api/me/declarations/${this.signing()!.id}/sign`).subscribe({ next: () => { this.busy.set(false); this.signing.set(null); this.toast.success('Signed. Thank you.'); this.loadDecls(); }, error: e => { this.busy.set(false); this.toast.error(e.title); } }); }
  openQuery() { this.qmsg = ''; this.qTouched.set(false); this.queryOpen.set(true); }
  sendQuery() { this.qTouched.set(true); if (!this.qmsg.trim()) return; this.api.post('/api/me/queries', { month: this.amonth, message: this.qmsg }).subscribe({ next: () => { this.queryOpen.set(false); this.toast.success('Query sent to HRSS.'); this.api.get<J[]>('/api/me/queries').subscribe(q => this.queries.set(q)); }, error: e => this.toast.error(e.title) }); }
  savePref() { this.api.put(`/api/employees/${this.session.me()!.id}/preferences`, { preferredShiftTypeId: this.pf.shift || null, preferredOffDay: this.pf.off || null, note: this.pf.note || null }).subscribe({ next: () => this.toast.success('Preference saved.'), error: e => this.toast.error(e.title) }); }
}

@Component({
  selector: 'rx-notifications', standalone: true, imports: [CommonModule, RouterLink, RxPageHeader, RxStatus, RxEmpty, DateTimePipeRx],
  template: `
  <rx-page-header title="Notifications" sub="Every notification links to the record it’s about."><button type="button" class="btn" (click)="readAll()" [disabled]="!unread()">Mark all as read</button></rx-page-header>
  <div class="card"><div class="card-b" style="padding:0">
    <ng-container *ngFor="let n of items()"> <a class="queue-item" [routerLink]="n.linkRoute" (click)="read(n)" style="grid-template-columns:auto 1fr auto;align-items:start"><span [attr.aria-label]="n.readAtUtc ? 'Read' : 'Unread'" style="width:10px;height:10px;border-radius:50%;margin-top:6px" [style.background]="n.readAtUtc ? 'transparent' : 'var(--rx-action)'" [style.border]="'2px solid var(--rx-action)'"></span><div><div class="t">{{ n.title }}</div><div class="small">{{ n.body }}</div><div class="xs muted">{{ n.sentAtUtc | rxDateTime }}</div></div><rx-status [value]="n.severity === 'Critical' ? 'High' : n.severity === 'Warning' ? 'Warn' : n.severity === 'Success' ? 'Approved' : 'Open'" [label]="n.severity"/></a> </ng-container>
    <ng-container *ngIf="!items().length"> <rx-empty title="No notifications"/> </ng-container>
  </div></div>`,
})
export class NotificationsPage implements OnInit {
  private api = inject(Api); private session = inject(Session); items = signal<J[]>([]); unread = computed(() => this.items().filter(i => !i.readAtUtc).length);
  ngOnInit() { this.api.get<J[]>('/api/notifications').subscribe(r => this.items.set(r)); }
  read(n: J) { if (!n.readAtUtc) this.api.post(`/api/notifications/${n.id}/read`).subscribe(() => { n.readAtUtc = new Date().toISOString(); void this.session.refresh(); }); }
  readAll() { this.api.post('/api/notifications/read-all').subscribe(() => { this.items.update(l => l.map(n => ({ ...n, readAtUtc: n.readAtUtc ?? new Date().toISOString() }))); void this.session.refresh(); }); }
}
