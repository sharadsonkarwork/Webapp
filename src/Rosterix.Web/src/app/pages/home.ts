import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { AiPanel } from '../core/ui.services';
import { InrPipe, DatePipeRx, MonthPipe } from '../shared/fmt';
import { RxAiCard, RxEmpty, RxIcon, RxPageHeader, RxStatus } from '../shared/ui';

@Component({
  selector: 'rx-home', standalone: true, imports: [CommonModule, RouterLink, RxPageHeader, RxIcon, RxStatus, RxAiCard, RxEmpty, InrPipe, DatePipeRx, MonthPipe],
  template: `
  <rx-page-header [title]="'Welcome, ' + first()" [sub]="subtitle()"/>
  <ng-container *ngIf="loading()"> <p class="muted" role="status">Loading your day…</p> </ng-container><ng-container *ngIf="!(loading())">
    <div class="grid-3" role="list" aria-label="Key numbers">
      <ng-container *ngFor="let k of kpis()"> <a class="card kpi" role="listitem" [routerLink]="k.link"><span class="v">{{ k.value }}</span><span class="l">{{ k.label }}</span><ng-container *ngIf="k.hint"> <span class="xs muted">{{ k.hint }}</span> </ng-container></a> </ng-container>
    </div>

    <div class="grid-2" style="margin-top:20px">
      <ng-container *ngIf="queue().length">
        <section class="card" aria-labelledby="q"><div class="card-h"><h2 id="q" style="font-size:16px">Needs your decision</h2><a routerLink="/approvals" class="btn sm">Open queue</a></div>
          <div class="card-b" style="padding:0">
            <ng-container *ngFor="let i of queue()">
              <a class="queue-item" [routerLink]="['/approvals', i.id]"><div><div class="t">{{ i.title }}</div><div class="small muted">{{ i.raisedBy.name }} · {{ i.ageDays }} d ago<ng-container *ngIf="i.risk"> · {{ i.risk.headline }} </ng-container></div></div>
                <ng-container *ngIf="i.risk"> <span class="risk" [attr.aria-label]="'Risk ' + i.risk.band"><rx-status [value]="i.risk.band"/> {{ i.risk.score.toFixed(2) }}</span> </ng-container></a>
            </ng-container>
          </div></section>
      </ng-container>
      <section class="card" aria-labelledby="t"><div class="card-h"><h2 id="t" style="font-size:16px">Today</h2></div>
        <div class="card-b stack">
          <ng-container *ngFor="let a of attention()"> <div class="banner" [class]="'banner ' + a.tone"><rx-icon [name]="a.icon"/><span>{{ a.text }} <ng-container *ngIf="a.link"> <a [routerLink]="a.link">{{ a.cta }}</a> </ng-container></span></div> </ng-container>
          <ng-container *ngIf="!attention().length"> <rx-empty title="You’re all caught up" text="Nothing needs attention right now."/> </ng-container>
        </div></section>
    </div>

    <ng-container *ngIf="next().length">
      <section class="card" style="margin-top:20px" aria-labelledby="n"><div class="card-h"><h2 id="n" style="font-size:16px">Your next shifts</h2><a class="btn sm" routerLink="/me">Full roster</a></div>
        <div class="card-b"><ul style="list-style:none;margin:0;padding:0;display:grid;gap:6px">
          <ng-container *ngFor="let s of next()"> <li class="row"><span class="mono" style="min-width:110px">{{ s.date | rxDate:false }}</span><strong>{{ s.shift }}</strong><span class="muted">{{ s.time }} · {{ s.project }}</span></li> </ng-container>
        </ul></div></section>
    </ng-container>
    <ng-container *ngIf="forecast() as f">
      <div style="margin-top:20px"><rx-ai-card heading="Cost forecast" [summary]="f.summary" [impact]="f.impact" [confidence]="f.confidence" [reasons]="f.reasons"/></div>
    </ng-container>
  </ng-container>`,
})
export class HomePage implements OnInit, OnDestroy {
  private api = inject(Api); session = inject(Session); private panel = inject(AiPanel);
  loading = signal(true); kpis = signal<J[]>([]); queue = signal<J[]>([]); attention = signal<J[]>([]); next = signal<J[]>([]); forecast = signal<J | null>(null);
  first = () => (this.session.me()?.name ?? '').split(' ')[0];
  subtitle = () => ({ PM: 'Your projects, rosters and allowances.', LM: 'Requests waiting for your decision.', HRBP: 'Approvals and exceptions for your business units.', RH: 'Final approvals and aged arrears.', HRSS: 'Foundation data, movements, anomalies and payroll.', EMP: 'Your roster, declarations and allowance.' } as J)[this.session.me()?.primaryRole] ?? '';

  ngOnInit() {
    const m = this.session.me()!; const has = (...r: string[]) => this.session.has(...r); const month = m.today.slice(0, 7); const prev = new Date(m.today); prev.setMonth(prev.getMonth() - 1);
    const lastMonth = `${prev.getFullYear()}-${String(prev.getMonth() + 1).padStart(2, '0')}`;
    const g = (u: string) => this.api.get(u).pipe(catchError(() => of(null)));
    forkJoin({
      pending: has('LM', 'HRBP', 'RH', 'HRSS') ? g('/api/approvals/pending') : of(null),
      mine: has('PM') ? g('/api/approvals/mine') : of(null),
      projects: has('PM', 'LM', 'HRBP', 'RH', 'HRSS') ? g('/api/projects') : of(null),
      exceptions: has('PM', 'LM', 'HRBP', 'HRSS') ? g('/api/exceptions?status=Open') : of(null),
      anomalies: has('HRSS') ? g('/api/ai/anomalies') : of(null),
      runs: has('PM', 'HRSS') ? g('/api/allowance-runs') : of(null),
      movements: has('HRSS') ? g('/api/movements') : of(null),
      arrears: has('RH', 'HRBP', 'HRSS') ? g('/api/arrears') : of(null),
      roster: g('/api/me/roster?month=' + month), decl: g('/api/me/declarations'), allowance: g('/api/me/allowance?month=' + lastMonth),
    }).subscribe(d => {
      const k: J[] = []; const att: J[] = [];
      if (d.pending) { const n = d.pending.total; k.push({ value: n, label: 'Waiting for your decision', link: '/approvals', hint: n ? `${d.pending.items.filter((i: J) => i.risk?.band === 'High').length} high risk` : '' }); this.queue.set(d.pending.items.slice(0, 3)); if (n) att.push({ tone: 'info', icon: 'inbox', text: `${n} request${n === 1 ? '' : 's'} need your decision.`, link: '/approvals', cta: 'Review', }); }
      if (d.mine) { const p = d.mine.filter((r: J) => r.status === 'Pending').length; k.push({ value: p, label: 'Requests I raised, in progress', link: '/approvals' }); }
      if (d.projects) k.push({ value: d.projects.filter((p: J) => p.status === 'Active').length, label: 'Active projects', link: '/projects', hint: `${d.projects.length} in scope` });
      if (d.exceptions) { const hi = d.exceptions.filter((e: J) => e.severity === 'High').length; k.push({ value: d.exceptions.length, label: 'Open exceptions', link: '/exceptions', hint: hi ? `${hi} high severity` : '' }); if (hi) att.push({ tone: 'warn', icon: 'alert', text: `${hi} high-severity exception${hi === 1 ? ' is' : 's are'} open.`, link: '/exceptions', cta: 'Review' }); }
      if (d.anomalies) { const items = d.anomalies.payload.items; k.push({ value: items.length, label: 'Anomalies found by AI', link: '/anomalies', hint: `${d.anomalies.payload.outlierCount} outliers` }); if (items.length) att.push({ tone: 'warn', icon: 'spark', text: `The anomaly scan found ${items.length} item${items.length === 1 ? '' : 's'}.`, link: '/anomalies', cta: 'Open dashboard' }); }
      if (d.runs) { const w = d.runs.filter((r: J) => r.status === 'PendingCompensation').length; k.push({ value: w, label: has('HRSS') ? 'Runs awaiting compensation approval' : 'Runs with HRSS', link: '/allowance' }); }
      if (d.movements) { const s = d.movements.filter((x: J) => x.status === 'Staged').length; if (s) att.push({ tone: 'info', icon: 'swap', text: `${s} movement${s === 1 ? ' is' : 's are'} staged and not committed.`, link: '/movements', cta: 'Open' }); }
      if (d.arrears && has('RH', 'HRBP')) { const o = d.arrears.filter((a: J) => a.bucket === '>6' && a.status !== 'Paid').length; k.push({ value: o, label: 'Arrears older than 6 months', link: '/arrears' }); }
      if (d.decl?.length) { const due = d.decl.filter((x: J) => x.status === 'Due').length; if (due) att.push({ tone: 'warn', icon: 'file', text: `You have ${due} declaration${due === 1 ? '' : 's'} to sign.`, link: '/me', cta: 'Sign now' }); k.push({ value: due, label: 'Declarations to sign', link: '/me' }); }
      if (d.roster && (d.roster.shifts > 0 || d.roster.entries.length > 0)) { k.push({ value: d.roster.shifts, label: `My shifts this month`, link: '/me', hint: `${d.roster.offs} weekly offs` }); const t = m.today; this.next.set((d.roster.entries as J[]).filter(e => e.type === 'Shift' && e.date >= t).slice(0, 4)); if (d.roster.changes?.some((c: J) => c.leadTimeFlag)) att.push({ tone: 'warn', icon: 'clock', text: 'A shift changed less than 24 hours before it starts.', link: '/me', cta: 'See changes' }); }
      for (const dl of m.delegationsGiven ?? []) if (dl.daysLeft <= 3) att.push({ tone: 'warn', icon: 'delegate', text: `Your delegation to ${dl.to.name} ends in ${dl.daysLeft} day${dl.daysLeft === 1 ? '' : 's'}.`, link: '/delegation', cta: 'Review' });
      this.kpis.set(k.slice(0, 6)); this.attention.set(att);
      // Advisory cards for the assistant panel
      const items = [];
      if (d.pending?.items?.length) items.push({ key: 'risk', kind: 'suggestion' as const, title: 'Approval risk (AI-UC-07)', summary: `${d.pending.items.filter((i: J) => i.risk?.band === 'High').length} high-risk and ${d.pending.items.filter((i: J) => i.risk?.band === 'Medium').length} medium-risk request(s) are listed first in your queue.`, impact: 'Changes order only; nothing is blocked.', confidence: 0.8, link: '/approvals' });
      if (d.anomalies) items.push({ key: 'anom', kind: 'suggestion' as const, title: 'Anomaly scan (AI-UC-09)', summary: d.anomalies.summary, impact: d.anomalies.impact, confidence: d.anomalies.confidence, reasons: d.anomalies.reasons, link: '/anomalies' });
      if (d.allowance?.payload?.lines?.length) items.push({ key: 'my', kind: 'suggestion' as const, title: 'Your allowance explained (AI-UC-05)', summary: d.allowance.summary, impact: d.allowance.impact, confidence: d.allowance.confidence, reasons: d.allowance.reasons, link: '/me' });
      this.panel.publish('home', items);
      this.loading.set(false);
      const proj = (d.projects as J[] | null)?.find(p => p.status === 'Active' && p.ownerId === m.id) && has('PM') ? (d.projects as J[]).find(p => p.status === 'Active' && p.ownerId === m.id) : null;
      if (proj) this.api.get(`/api/ai/cost-forecast`, { projectId: proj.id, month }).subscribe({ next: f => this.forecast.set(f), error: () => {} });
    });
  }
  ngOnDestroy() { this.panel.clear('home'); }
}

@Component({ selector: 'rx-nf', standalone: true, imports: [CommonModule, RouterLink, RxPageHeader], template: `<rx-page-header title="We can’t find that page" sub="The link may be old, or you may not have access to it."><a class="btn primary" routerLink="/home">Go to home</a></rx-page-header>` })
export class NotFoundPage {}
