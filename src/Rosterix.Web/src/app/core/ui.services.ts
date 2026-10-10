import { Injectable, effect, signal } from '@angular/core';
import { J } from './api.service';

function get(k: string): string | null { try { return localStorage.getItem(k); } catch { return null; } }
function set(k: string, v: string): void { try { localStorage.setItem(k, v); } catch { /* per-viewer convenience only */ } }

export interface Toast { id: number; kind: 'success' | 'error' | 'info'; text: string; }

/** Toasts render inside an aria-live region so screen readers hear every result. */
@Injectable({ providedIn: 'root' })
export class Toasts {
  readonly items = signal<Toast[]>([]);
  private n = 0;
  show(kind: Toast['kind'], text: string, ms = 6000): void {
    const t = { id: ++this.n, kind, text }; this.items.update(x => [...x, t]);
    if (kind !== 'error') setTimeout(() => this.dismiss(t.id), ms);
  }
  success(t: string) { this.show('success', t); } error(t: string) { this.show('error', t); } info(t: string) { this.show('info', t); }
  dismiss(id: number) { this.items.update(x => x.filter(i => i.id !== id)); }
}

@Injectable({ providedIn: 'root' })
export class Theme {
  readonly mode = signal<'auto' | 'light' | 'dark'>((get('rx.theme') as J) ?? 'auto');
  readonly density = signal<'compact' | 'comfortable'>((get('rx.density') as J) ?? 'compact');
  constructor() {
    effect(() => {
      const m = this.mode(); const el = document.documentElement;
      if (m === 'auto') el.removeAttribute('data-theme'); else el.setAttribute('data-theme', m);
      set('rx.theme', m);
    });
    effect(() => set('rx.density', this.density()));
  }
  cycle(): void { this.mode.update(m => (m === 'auto' ? 'light' : m === 'light' ? 'dark' : 'auto')); }
}

export interface PanelItem { key: string; kind: 'suggestion'; title: string; summary: string; impact?: string; confidence: number; reasons?: J[]; actionLabel?: string; action?: () => void; dismissLabel?: string; link?: string; }

/** Pages publish advisory cards here; the right-hand assistant panel lists them. Collapsed by default, state persists per user. */
@Injectable({ providedIn: 'root' })
export class AiPanel {
  readonly open = signal(false);
  readonly items = signal<PanelItem[]>([]);
  private userKey = 'anon';
  private owners = new Map<string, PanelItem[]>();
  setUser(id: string) { this.userKey = id; this.open.set(get('rx.ai.open.' + id) === '1'); }
  toggle() { this.open.update(v => !v); set('rx.ai.open.' + this.userKey, this.open() ? '1' : '0'); }
  publish(owner: string, items: PanelItem[]) { this.owners.set(owner, items); this.flush(); }
  clear(owner: string) { this.owners.delete(owner); this.flush(); }
  private flush() { this.items.set([...this.owners.values()].flat()); }
}
