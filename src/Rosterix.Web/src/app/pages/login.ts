import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Api, J } from '../core/api.service';
import { Session } from '../core/auth.service';
import { RxIcon } from '../shared/ui';

@Component({
  selector: 'rx-login', standalone: true, imports: [CommonModule, RxIcon],
  template: `
  <div class="login-wrap"><main id="main" class="stack-lg" style="width:min(760px,100%)">
    <div><h1 style="font-size:32px"><rx-icon name="calendar" [size]="30"/> Rosterix 2.0</h1>
      <p class="muted" style="max-width:60ch">Workforce, roster and allowance management. AI drafts; people commit. Every suggestion is explained, scored and reversible.</p></div>
    <ng-container *ngIf="session.config().mode === 'oidc'">
      <div class="card"><div class="card-b stack"><h2>Sign in</h2><p>Use your work account.</p>
        <button type="button" class="btn primary" (click)="session.startOidc()">Sign in with Microsoft</button></div></div>
    </ng-container><ng-container *ngIf="!(session.config().mode === 'oidc')">
      <section aria-labelledby="pick"><h2 id="pick" style="margin-bottom:4px">Choose who to sign in as</h2>
        <p class="muted small">Development role switcher. In staging and production this page uses Azure AD (auth-code + PKCE) instead.</p>
        <ng-container *ngIf="error()"> <div class="banner danger" role="alert"><rx-icon name="alert"/><span>{{ error() }}</span></div> </ng-container>
        <div class="grid-3" style="margin-top:12px">
          <ng-container *ngFor="let p of personas()">
            <button type="button" class="persona" (click)="pick(p.key)" [disabled]="busy()"><span class="n">{{ p.name }}</span><span class="r">{{ p.title }}</span><span class="small">{{ p.blurb }}</span></button>
          </ng-container><ng-container *ngIf="!(personas())?.length"> <p class="muted">Loading personas…</p> </ng-container>
        </div></section>
    </ng-container>
  </main></div>`,
})
export class LoginPage implements OnInit {
  session = inject(Session); private api = inject(Api); private router = inject(Router);
  personas = signal<J[]>([]); busy = signal(false); error = signal('');
  ngOnInit() { this.api.get<J[]>('/api/personas').subscribe({ next: p => this.personas.set(p), error: e => this.error.set(e.title) }); document.title = 'Sign in · Rosterix'; }
  async pick(key: string) { this.busy.set(true); await this.session.signInAs(key); this.busy.set(false); if (this.session.me()) void this.router.navigateByUrl('/home'); else this.error.set('That sign-in didn’t work. Try again.'); }
}

@Component({ selector: 'rx-callback', standalone: true, imports: [CommonModule], template: `<main id="main" class="login-wrap"><p role="status">{{ msg() }}</p></main>` })
export class CallbackPage implements OnInit {
  private route = inject(ActivatedRoute); private session = inject(Session); private router = inject(Router); msg = signal('Signing you in…');
  async ngOnInit() {
    const q = this.route.snapshot.queryParamMap; const code = q.get('code'); const state = q.get('state');
    if (code && state && (await this.session.finishOidc(code, state))) void this.router.navigateByUrl('/home');
    else { this.msg.set('We couldn’t complete the sign-in. Return to the sign-in page and try again.'); setTimeout(() => this.router.navigateByUrl('/login'), 2500); }
  }
}
