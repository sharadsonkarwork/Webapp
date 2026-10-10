import { CommonModule } from '@angular/common';
import { Component, ElementRef, HostListener, OnInit, ViewChild, computed, effect, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { Api, J } from './core/api.service';
import { Session } from './core/auth.service';
import { AiPanel, Theme, Toasts } from './core/ui.services';
import { RxAiPanel, RxIcon } from './shared/ui';
import { dateFmt } from './shared/fmt';

interface NavItem { label: string; icon: string; route: string; roles?: string[]; badge?: () => number; }
interface NavGroup { title: string; items: NavItem[]; }

@Component({
  selector: 'rx-shell', standalone: true, imports: [CommonModule, RouterOutlet, RouterLink, RouterLinkActive, RxIcon, RxAiPanel],
  template: `
  <a class="skip-link" href="#main" (click)="focusMain($event)">Skip to main content</a>
  <div class="app">
    <header class="topbar">
      <button type="button" class="btn icon menu-btn" (click)="navOpen.set(!navOpen())" [attr.aria-expanded]="navOpen()" aria-controls="sidenav" aria-label="Menu"><rx-icon name="menu"/></button>
      <a class="brand" routerLink="/home" aria-label="Rosterix home"><rx-icon name="calendar" [size]="20"/> <span class="brand-t">Rosterix</span></a>
      <span class="grow"></span>
      <ng-container *ngIf="personas().length">
        <label class="row" style="gap:6px"><span class="xs" style="opacity:.9">Act as</span>
          <select (change)="switchTo($any($event.target).value)" aria-label="Switch demo persona (development only)" style="min-height:32px;background:transparent;color:inherit;border-color:rgb(255 255 255 / 45%)">
            <ng-container *ngFor="let p of personas()"> <option [value]="p.key" [selected]="p.key === session.persona()" style="color:#1B2733">{{ p.name }} · {{ p.title }}</option> </ng-container>
          </select></label>
      </ng-container>
      <div class="who"><strong>{{ session.me()?.name }}</strong><small>{{ session.me()?.roles?.join(' · ') || 'Employee' }}</small></div>
      <button type="button" class="btn icon" (click)="theme.cycle()" [attr.aria-label]="'Theme: ' + theme.mode() + '. Switch theme'" [title]="'Theme: ' + theme.mode()"><rx-icon [name]="theme.mode()==='dark' ? 'moon' : 'sun'"/></button>
      <a class="btn icon" routerLink="/notifications" style="position:relative" [attr.aria-label]="'Notifications' + (unread() ? ', ' + unread() + ' unread' : '')"><rx-icon name="bell"/><ng-container *ngIf="unread()"> <span class="badge-dot" aria-hidden="true">{{ unread() }}</span> </ng-container></a>
      <button type="button" class="btn" (click)="session.signOut()" aria-label="Sign out"><span class="so-text">Sign out</span><rx-icon class="so-ic" name="exit"/></button>
    </header>

    <nav id="sidenav" class="sidenav" [attr.data-open]="navOpen()" aria-label="Main">
      <ng-container *ngFor="let g of groups()">
        <h2>{{ g.title }}</h2>
        <ng-container *ngFor="let i of g.items">
          <a [routerLink]="i.route" routerLinkActive #rla="routerLinkActive" [attr.aria-current]="rla.isActive ? 'page' : null" (click)="navOpen.set(false)"><rx-icon [name]="i.icon"/>{{ i.label }}<ng-container *ngIf="i.badge && i.badge()"> <span class="count" [attr.aria-label]="i.badge() + ' waiting'">{{ i.badge() }}</span> </ng-container></a>
        </ng-container>
      </ng-container>
    </nav>

    <main id="main" class="main" tabindex="-1" #main>
      <ng-container *ngFor="let b of banners()"> <div class="delegation-banner" role="status"><rx-icon name="delegate"/><span [innerHTML]="b"></span></div> </ng-container>
      <router-outlet/>
    </main>
    <rx-ai-panel/>
  </div>
  <div class="toasts" aria-live="polite" aria-atomic="false">
    <ng-container *ngFor="let t of toasts.items()">
      <div class="toast" [class]="'toast ' + t.kind" [attr.role]="t.kind === 'error' ? 'alert' : 'status'"><span class="grow">{{ t.text }}</span><button type="button" class="btn sm ghost icon" (click)="toasts.dismiss(t.id)" aria-label="Dismiss message"><rx-icon name="x" [size]="14"/></button></div>
    </ng-container>
  </div>
  <div class="sr-only" aria-live="polite" role="status">{{ announce() }}</div>`,
})
export class Shell implements OnInit {
  session = inject(Session); theme = inject(Theme); toasts = inject(Toasts); private router = inject(Router); private api = inject(Api); private ai = inject(AiPanel);
  @ViewChild('main', { static: true }) mainEl!: ElementRef<HTMLElement>;
  navOpen = signal(false); announce = signal(''); personas = signal<J[]>([]); unread = computed(() => this.session.me()?.unreadNotifications ?? 0);

  private all: NavGroup[] = [
    { title: 'Work', items: [
      { label: 'Home', icon: 'home', route: '/home' },
      { label: 'Approvals', icon: 'inbox', route: '/approvals', roles: ['PM', 'LM', 'HRBP', 'RH', 'HRSS'], badge: () => this.session.me()?.pendingApprovals ?? 0 },
      { label: 'Projects', icon: 'folder', route: '/projects', roles: ['PM', 'LM', 'HRBP', 'RH', 'HRSS'] },
      { label: 'Employee tagging', icon: 'tag', route: '/tagging', roles: ['PM', 'LM', 'HRBP', 'HRSS'] },
      { label: 'Roster', icon: 'calendar', route: '/roster', roles: ['PM', 'LM', 'HRSS', 'HRBP', 'RH'] },
    ] },
    { title: 'Money', items: [
      { label: 'Allowance', icon: 'coin', route: '/allowance', roles: ['PM', 'LM', 'HRSS', 'HRBP'] },
      { label: 'Exceptions', icon: 'alert', route: '/exceptions', roles: ['PM', 'LM', 'HRBP', 'HRSS'] },
      { label: 'Anomalies', icon: 'spark', route: '/anomalies', roles: ['PM', 'LM', 'HRBP', 'HRSS'] },
      { label: 'Arrears', icon: 'clock', route: '/arrears', roles: ['PM', 'LM', 'HRBP', 'RH', 'HRSS'] },
      { label: 'Payroll', icon: 'bank', route: '/payroll', roles: ['HRSS', 'HRBP', 'RH'] },
      { label: 'Reports', icon: 'chart', route: '/reports' },
    ] },
    { title: 'People', items: [
      { label: 'My work', icon: 'person', route: '/me' },
      { label: 'Delegation', icon: 'delegate', route: '/delegation', roles: ['PM', 'LM', 'HRSS'] },
      { label: 'Movements', icon: 'swap', route: '/movements', roles: ['HRSS'] },
      { label: 'Settlement', icon: 'exit', route: '/settlement', roles: ['HRSS'] },
    ] },
    { title: 'Admin', items: [
      { label: 'Foundation data', icon: 'sliders', route: '/foundation', roles: ['HRSS'] },
      { label: 'Audit trail', icon: 'shield', route: '/audit', roles: ['HRSS'] },
    ] },
  ];
  groups = computed(() => { this.session.me(); return this.all.map(g => ({ ...g, items: g.items.filter(i => !i.roles || this.session.has(...i.roles)) })).filter(g => g.items.length); });
  banners = computed(() => {
    const m = this.session.me(); const out: string[] = [];
    for (const d of m?.delegationsReceived ?? []) out.push(`<strong>Acting under a delegation.</strong> You can decide ${d.scope.replace(/([A-Z])/g, ' $1').toLowerCase().trim()} requests for ${d.from.name} until ${dateFmt(d.validTo)} (${d.daysLeft} day${d.daysLeft === 1 ? '' : 's'} left).`);
    for (const d of m?.delegationsGiven ?? []) out.push(`<strong>You’ve delegated</strong> ${d.scope.replace(/([A-Z])/g, ' $1').toLowerCase().trim()} to ${d.to.name} until ${dateFmt(d.validTo)}${d.daysLeft <= 3 ? ` — <strong>expires in ${d.daysLeft} day${d.daysLeft === 1 ? '' : 's'}</strong>` : ''}.`);
    return out;
  });

  constructor() {
    effect(() => { const id = this.session.me()?.id; if (id) this.ai.setUser(id); }, { allowSignalWrites: true });
    let navs = 0;
    this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe(() => {
      const first = ++navs === 1; // a fresh page load keeps focus at the top so the skip link is the first Tab stop
      queueMicrotask(() => {
        const t = this.leaf()?.data?.['title'] as string | undefined;
        this.announce.set(t ? `${t} page loaded` : '');
        if (first) return;
        const h1 = this.mainEl.nativeElement.querySelector<HTMLElement>('h1'); (h1 ?? this.mainEl.nativeElement).focus({ preventScroll: true });
      });
      this.navOpen.set(false);
    });
  }
  private leaf() { let r = this.router.routerState.snapshot.root; while (r.firstChild) r = r.firstChild; return r; }
  ngOnInit() { this.api.get<J[]>('/api/personas').subscribe({ next: p => this.personas.set(p), error: () => this.personas.set([]) }); }
  focusMain(e: Event) { e.preventDefault(); this.mainEl.nativeElement.focus(); }
  async switchTo(key: string) { await this.session.signInAs(key); await this.router.navigateByUrl('/home', { onSameUrlNavigation: 'reload' }); this.toasts.info(`Now acting as ${this.session.me()?.name}.`); }
  @HostListener('document:keydown.escape') onEsc() { this.navOpen.set(false); }
}
