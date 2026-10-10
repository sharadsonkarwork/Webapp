import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { Theme } from '../core/ui.services';
import { RxRosterGrid } from '../shared/roster-grid';
import { Column, RxAiCard, RxAiSkeleton, RxConfidence, RxDialog, RxIcon, RxImpact, RxPageHeader, RxStatus, RxTable, RxWhy } from '../shared/ui';

interface Pair { name: string; fg: string; bg: string; need: number; }

function lum(hex: string): number {
  const c = hex.replace('#', '').match(/.{2}/g)!.map(x => parseInt(x, 16) / 255).map(v => (v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4)));
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}
export const contrast = (a: string, b: string): number => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
/** Resolves a token to a #rrggbb string by letting the browser compute it (handles theme and color-mix-free tokens). */
function token(name: string): string {
  const probe = document.createElement('span'); probe.style.color = `var(${name})`; document.body.appendChild(probe);
  const rgb = getComputedStyle(probe).color; probe.remove(); const m = rgb.match(/\d+/g)!.map(Number);
  return '#' + m.slice(0, 3).map(n => n.toString(16).padStart(2, '0')).join('');
}

@Component({
  selector: 'rx-gallery', standalone: true, imports: [CommonModule, RxPageHeader, RxIcon, RxStatus, RxConfidence, RxAiCard, RxAiSkeleton, RxWhy, RxTable, RxImpact, RxDialog, RxRosterGrid],
  template: `
  <rx-page-header title="Design system" sub="Tokens, primitives and the shared AI components. Colours and contrast below are computed live from the active theme."><button type="button" class="btn" (click)="theme.cycle()">Theme: {{ theme.mode() }}</button></rx-page-header>
  <div class="stack-lg">
    <section class="card" aria-labelledby="g1"><div class="card-h"><h2 id="g1" style="font-size:16px">Colour and contrast (WCAG 2.2: text 4.5:1, UI 3:1)</h2></div><div class="card-b" style="padding:0">
      <table class="rx"><caption class="sr-only">Contrast ratios for token pairs</caption><thead><tr><th>Pair</th><th>Foreground</th><th>Background</th><th class="num">Ratio</th><th>Needs</th><th>Result</th></tr></thead><tbody>
        <ng-container *ngFor="let p of pairs()"> <tr><td>{{ p.name }}</td><td><span class="mono">{{ p.fg }}</span></td><td><span class="mono">{{ p.bg }}</span></td><td class="num">{{ ratio(p).toFixed(2) }}:1</td><td class="num">{{ p.need }}:1</td><td><rx-status [value]="ratio(p) >= p.need ? 'Pass' : 'Fail'"/></td></tr> </ng-container>
      </tbody></table></div></section>

    <section class="card" aria-labelledby="g2"><div class="card-h"><h2 id="g2" style="font-size:16px">Buttons (min 32px; icon-only targets ≥ 32×32)</h2></div><div class="card-b row">
      <button type="button" class="btn primary">Publish roster</button><button type="button" class="btn">Secondary</button><button type="button" class="btn danger">Reject request</button><button type="button" class="btn danger-outline">Remove</button><button type="button" class="btn ghost">Ghost</button><button type="button" class="btn ai"><rx-icon name="spark"/>Suggest a draft</button>
      <button type="button" class="btn primary" disabled>Disabled</button><button type="button" class="btn icon" aria-label="Download"><rx-icon name="download"/></button><button type="button" class="btn sm">Small (28px)</button></div></section>

    <section class="card" aria-labelledby="g3"><div class="card-h"><h2 id="g3" style="font-size:16px">Form fields</h2></div><div class="card-b form-grid">
      <label class="field">Project name<input type="text" value="Store operations"><span class="hint">Up to 120 characters.</span></label>
      <label class="field">Cost centre<input type="text" aria-invalid="true" aria-describedby="e1" value=""><span id="e1" class="err">Enter a cost centre, for example CC-4101.</span></label>
      <label class="field">Status<select><option>Active</option><option>Draft</option></select></label>
      <label class="check"><input type="checkbox" checked> Allowance-eligible</label></div></section>

    <section class="card" aria-labelledby="g4"><div class="card-h"><h2 id="g4" style="font-size:16px">Status chips and banners: never colour alone</h2></div><div class="card-b stack">
      <div class="row"><ng-container *ngFor="let s of statuses"> <rx-status [value]="s"/> </ng-container></div>
      <div class="banner info"><rx-icon name="info"/><span>Information: this roster is published.</span></div><div class="banner warn"><rx-icon name="alert"/><span>Warning: restricted holiday.</span></div>
      <div class="banner danger"><rx-icon name="ban"/><span>Blocking: this breaks BR-02. Make Sunday a weekly off.</span></div><div class="banner success"><rx-icon name="check"/><span>Saved.</span></div></div></section>

    <section class="card" aria-labelledby="g5"><div class="card-h"><h2 id="g5" style="font-size:16px">AI layer: reserved purple, always labelled</h2></div><div class="card-b stack">
      <div class="row"><rx-confidence [value]="0.35"/><rx-confidence [value]="0.62"/><rx-confidence [value]="0.91"/></div>
      <rx-ai-card heading="Draft roster" summary="Draft roster for November: 30 people, 734 shifts, 0 conflicts to review." impact="Creates a Draft roster only when you accept it. It is never published automatically." [confidence]="0.9"
        [reasons]="[{ text: 'Each person’s most common shift was taken from the last 3 published rosters.' }, { text: 'The rule engine checked 30 people; 0 conflicts remain.' }]" acceptLabel="Accept as draft" modifyLabel="Modify" dismissLabel="Dismiss"/>
      <rx-ai-skeleton/><div class="row"><span class="rx-grid-cell ai-cell" style="width:64px;height:32px;display:inline-flex">EVE<span class="aitag">AI</span></span><span class="small muted">An AI-suggested roster cell: dotted purple border and an “AI” tag.</span></div></div></section>

    <section class="card" aria-labelledby="g6"><div class="card-h"><h2 id="g6" style="font-size:16px">Impact preview (before and after)</h2></div><div class="card-b"><rx-impact [rows]="impact"/></div></section>
    <section class="card" aria-labelledby="g7"><div class="card-h"><h2 id="g7" style="font-size:16px">Table</h2></div><div class="card-b" style="padding:0"><rx-table [rows]="rows" [columns]="cols" caption="Sample table" [pageSize]="3"/></div></section>
    <section class="card" aria-labelledby="g8"><div class="card-h"><h2 id="g8" style="font-size:16px">Roster grid (read-only sample)</h2></div><div class="card-b"><rx-roster-grid label="Sample roster" [employees]="emps" [days]="days" [entries]="entries" [shiftTypes]="shifts" [conflicts]="[]" [editable]="false"/></div></section>
    <section class="card" aria-labelledby="g9"><div class="card-h"><h2 id="g9" style="font-size:16px">Dialog</h2></div><div class="card-b"><button type="button" class="btn" (click)="dlg.set(true)">Open dialog</button></div></section>
  </div>
  <ng-container *ngIf="dlg()"> <rx-dialog title="Example dialog" (close)="dlg.set(false)"><p>Focus is trapped here; Escape closes it and returns focus to the button.</p><div footer class="row"><button type="button" class="btn" (click)="dlg.set(false)">Close</button></div></rx-dialog> </ng-container>`,
})
export class GalleryPage {
  theme = inject(Theme); private tick = signal(0);
  statuses = ['Active', 'Approved', 'Pending', 'Draft', 'Warn', 'Rejected', 'High', 'Block', 'Closed', 'Flagged', 'Backtagged'];
  cols: Column[] = [{ key: 'name', label: 'Employee' }, { key: 'shifts', label: 'Shifts', type: 'num' }, { key: 'amount', label: 'Amount', type: 'money' }, { key: 'status', label: 'Status', type: 'chip' }];
  rows = [{ id: 1, name: 'Ravi Kumar', shifts: 23, amount: 12000, status: 'Approved' }, { id: 2, name: 'Isha Rao', shifts: 18, amount: 9450, status: 'Pending' }, { id: 3, name: 'Karan Gupta', shifts: 26, amount: 13650, status: 'Flagged' }, { id: 4, name: 'Dev Tiwari', shifts: 24, amount: 12600, status: 'Approved' }];
  impact = [{ entityType: 'Tag', label: 'Festive fulfilment', before: '1 Mar 2026 – open', after: '1 Mar 2026 – 31 Aug 2026', change: 'Changed' }, { entityType: 'Allowance lines', label: 'September run', before: '25 lines, ₹3,000', after: 'Recovery or arrear of ₹3,000 needed', change: 'Arrear' }];
  dlg = signal(false);
  emps = [{ id: 'a', name: 'Ravi Kumar', number: 'E1001', grade: 'G3' }, { id: 'b', name: 'Isha Rao', number: 'E1002', grade: 'G3' }];
  days = ['2026-10-01', '2026-10-02', '2026-10-03', '2026-10-04', '2026-10-05'].map((d, i) => ({ date: d, dow: ['Thu', 'Fri', 'Sat', 'Sun', 'Mon'][i], holiday: i === 1 ? 'Gandhi Jayanti' : null, restricted: i === 1 }));
  shifts = [{ id: 's1', code: 'EVE', name: 'Evening', projectShift: true }, { id: 's2', code: 'NIGHT', name: 'Night', projectShift: true }];
  entries = [{ e: 'a', d: '2026-10-01', s: 's1', t: 'Shift', ai: false }, { e: 'a', d: '2026-10-02', s: null, t: 'Holiday', ai: false }, { e: 'a', d: '2026-10-03', s: 's2', t: 'Shift', ai: true }, { e: 'a', d: '2026-10-04', s: null, t: 'WeeklyOff', ai: false }, { e: 'b', d: '2026-10-01', s: 's2', t: 'Shift', ai: false }, { e: 'b', d: '2026-10-02', s: null, t: 'Leave', ai: false }];
  constructor() { effect(() => { this.theme.mode(); setTimeout(() => this.tick.update(n => n + 1)); }, { allowSignalWrites: true }); }
  pairs = computed<Pair[]>(() => {
    this.tick(); const t = (n: string) => token(n);
    const P = (name: string, fg: string, bg: string, need = 4.5): Pair => ({ name, fg: t(fg), bg: t(bg), need });
    return [P('Text on surface', '--rx-text', '--rx-surface'), P('Muted text on surface', '--rx-text-muted', '--rx-surface'), P('Muted text on canvas', '--rx-text-muted', '--rx-canvas'), P('Muted text on sunk surface', '--rx-text-muted', '--rx-surface-sunk'),
      P('Action text on surface', '--rx-action', '--rx-surface'), P('Primary button label', '--rx-on-action', '--rx-action'), P('AI text on surface', '--rx-ai', '--rx-surface'), P('AI text on AI tint', '--rx-ai', '--rx-ai-subtle'),
      P('Danger on danger tint', '--rx-danger', '--rx-danger-subtle'), P('Warning on warning tint', '--rx-warn', '--rx-warn-subtle'), P('Success on success tint', '--rx-success', '--rx-success-subtle'), P('Info on info tint', '--rx-info', '--rx-info-subtle'),
      P('Focus ring on surface (UI, 3:1)', '--rx-focus', '--rx-surface', 3), P('Focus ring on canvas (UI, 3:1)', '--rx-focus', '--rx-canvas', 3), P('Control border on surface (UI, 3:1)', '--rx-control-border', '--rx-surface', 3), P('Control border on canvas (UI, 3:1)', '--rx-control-border', '--rx-canvas', 3), P('Danger button label', '--rx-on-danger', '--rx-danger'), P('AI badge label', '--rx-on-ai', '--rx-ai')];
  });
  ratio = (p: Pair) => contrast(p.fg, p.bg);
}
