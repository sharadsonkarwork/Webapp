import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Api, J } from './api.service';
import { firstValueFrom } from 'rxjs';

const PERSONA_KEY = 'rx.persona';
const TOKEN_KEY = 'rx.token';
const PKCE_KEY = 'rx.pkce';

function read(k: string): string | null { try { return sessionStorage.getItem(k); } catch { return null; } }
function write(k: string, v: string | null): void { try { v === null ? sessionStorage.removeItem(k) : sessionStorage.setItem(k, v); } catch { /* storage unavailable: session lasts for this tab only */ } }

export interface AuthConfig { mode: 'dev' | 'oidc'; tenantId?: string; clientId?: string; scope?: string; }

/** Identity for the whole app: the dev role-switcher locally, Azure AD OIDC (auth-code + PKCE) elsewhere. */
@Injectable({ providedIn: 'root' })
export class Session {
  private api = inject(Api);
  private router = inject(Router);
  readonly persona = signal<string | null>(read(PERSONA_KEY));
  readonly token = signal<string | null>(read(TOKEN_KEY));
  readonly me = signal<J | null>(null);
  readonly config = signal<AuthConfig>({ mode: 'dev' });
  readonly roles = computed<string[]>(() => this.me()?.roles ?? []);
  readonly isSignedIn = computed(() => !!this.persona() || !!this.token());
  private memo?: Promise<J | null>;

  has(...r: string[]): boolean { const mine = this.roles(); return r.length === 0 || r.some(x => mine.includes(x)); }
  hasPolicy(p: string): boolean { return (this.me()?.policies ?? []).includes(p); }

  async init(): Promise<void> {
    try { const c = await fetch('assets/auth.json'); if (c.ok) this.config.set(await c.json()); } catch { /* defaults to dev */ }
    if (this.isSignedIn()) await this.refresh();
  }

  refresh(): Promise<J | null> {
    this.memo = firstValueFrom(this.api.get('/api/me')).then(m => { this.me.set(m); return m; }).catch(() => { this.me.set(null); return null; });
    return this.memo;
  }
  async ensureMe(): Promise<J | null> { return this.me() ?? (this.isSignedIn() ? (this.memo ?? this.refresh()) : null); }

  async signInAs(key: string): Promise<void> {
    this.persona.set(key); write(PERSONA_KEY, key); this.me.set(null);
    await this.refresh();
  }
  signOut(): void {
    this.persona.set(null); this.token.set(null); this.me.set(null); write(PERSONA_KEY, null); write(TOKEN_KEY, null);
    void this.router.navigateByUrl('/login');
  }

  // ---- OIDC authorization-code flow with PKCE (Azure AD). Exercised against a real tenant at deploy time; see DECISIONS D-15.
  async startOidc(): Promise<void> {
    const c = this.config(); if (c.mode !== 'oidc' || !c.tenantId || !c.clientId) return;
    const verifier = b64url(crypto.getRandomValues(new Uint8Array(48)));
    const challenge = b64url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
    const state = b64url(crypto.getRandomValues(new Uint8Array(16)));
    write(PKCE_KEY, JSON.stringify({ verifier, state }));
    const q = new URLSearchParams({ client_id: c.clientId, response_type: 'code', redirect_uri: `${location.origin}/auth/callback`, response_mode: 'query', scope: c.scope ?? `openid profile email api://${c.clientId}/access_as_user`, code_challenge: challenge, code_challenge_method: 'S256', state });
    location.assign(`https://login.microsoftonline.com/${c.tenantId}/oauth2/v2.0/authorize?${q}`);
  }
  async finishOidc(code: string, state: string): Promise<boolean> {
    const c = this.config(); const saved = JSON.parse(read(PKCE_KEY) ?? 'null'); write(PKCE_KEY, null);
    if (!saved || saved.state !== state || !c.tenantId || !c.clientId) return false;
    const body = new URLSearchParams({ client_id: c.clientId, grant_type: 'authorization_code', code, redirect_uri: `${location.origin}/auth/callback`, code_verifier: saved.verifier, scope: c.scope ?? `openid profile email api://${c.clientId}/access_as_user` });
    const r = await fetch(`https://login.microsoftonline.com/${c.tenantId}/oauth2/v2.0/token`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
    if (!r.ok) return false;
    const t = await r.json(); this.token.set(t.access_token); write(TOKEN_KEY, t.access_token);
    await this.refresh(); return !!this.me();
  }
}

function b64url(b: Uint8Array): string { let s = ''; b.forEach(x => (s += String.fromCharCode(x))); return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
