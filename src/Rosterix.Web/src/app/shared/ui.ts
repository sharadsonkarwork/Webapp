import { CommonModule, NgTemplateOutlet } from '@angular/common';
import { AfterViewInit, Component, Directive, ElementRef, EventEmitter, HostListener, Input, OnDestroy, Output, TemplateRef, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { J } from '../core/api.service';
import { AiPanel } from '../core/ui.services';
import { InrPipe } from './fmt';

// ---------------------------------------------------------------- icons (stroke, currentColor; decorative unless labelled)
const P: Record<string, string> = {
  home: 'M3 11l9-8 9 8M5 10v10h5v-6h4v6h5V10', folder: 'M3 6a2 2 0 012-2h4l2 2h8a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2z',
  users: 'M9 11a3 3 0 100-6 3 3 0 000 6zM3 20v-1a5 5 0 015-5h2a5 5 0 015 5v1M17 11a3 3 0 000-6M21 20v-1a5 5 0 00-3-4.6', tag: 'M3 12V4h8l10 10-8 8L3 12zM7.5 7.5h.01',
  inbox: 'M4 4h16v16H4zM8 12l3 3 5-6', calendar: 'M4 6h16v14H4zM4 10h16M8 3v6M16 3v6', coin: 'M12 3a9 9 0 100 18 9 9 0 000-18zM9 9.5C9 8 10.3 7 12 7s3 1 3 2.5S13.5 12 12 12s-3 1.5-3 3 1.3 2 3 2 3-1 3-2',
  alert: 'M12 3l10 18H2zM12 10v5M12 18h.01', chart: 'M4 20V4M4 20h16M8 16v-5M12 16V8M16 16v-8', file: 'M6 3h9l4 4v14H6zM14 3v5h5M9 13h6M9 17h6',
  bank: 'M3 10l9-6 9 6M5 10v8M9 10v8M15 10v8M19 10v8M3 20h18', swap: 'M7 7h13M16 3l4 4-4 4M17 17H4M8 13l-4 4 4 4', delegate: 'M9 11a3 3 0 100-6 3 3 0 000 6zM3 20v-1a5 5 0 015-5h2a5 5 0 015 5v1M16 11l2 2 4-4',
  clock: 'M12 3a9 9 0 100 18 9 9 0 000-18zM12 7v5l3 2', exit: 'M9 4H5v16h4M15 8l5 4-5 4M20 12H9', person: 'M12 12a4 4 0 100-8 4 4 0 000 8zM4 21v-1a6 6 0 016-6h4a6 6 0 016 6v1',
  sliders: 'M4 6h16M4 12h16M4 18h16M9 4v4M15 10v4M8 16v4', shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z', bell: 'M6 16V11a6 6 0 1112 0v5l2 2H4zM10 21h4',
  menu: 'M4 6h16M4 12h16M4 18h16', spark: 'M12 3l2 6 6 2-6 2-2 6-2-6-6-2 6-2z', search: 'M11 4a7 7 0 100 14 7 7 0 000-14zM21 21l-5-5', x: 'M6 6l12 12M18 6L6 18',
  right: 'M9 6l6 6-6 6', down: 'M6 9l6 6 6-6', up: 'M6 15l6-6 6 6', download: 'M12 4v12M7 11l5 5 5-5M5 20h14', upload: 'M12 16V4M7 9l5-5 5 5M5 20h14', check: 'M5 12l5 5 9-10',
  info: 'M12 3a9 9 0 100 18 9 9 0 000-18zM12 11v6M12 7.5h.01', ban: 'M12 3a9 9 0 100 18 9 9 0 000-18zM6 6l12 12', moon: 'M20 14A8 8 0 1110 4a7 7 0 0010 10z', lock: 'M6 11h12v9H6zM8 11V8a4 4 0 018 0v3',
  plus: 'M12 5v14M5 12h14', refresh: 'M20 11a8 8 0 10-2 6M20 5v6h-6', minus: 'M5 12h14', sun: 'M12 8a4 4 0 100 8 4 4 0 000-8zM12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5L19 19M5 19l1.5-1.5M17.5 6.5L19 5',
  grip: 'M9 6h.01M9 12h.01M9 18h.01M15 6h.01M15 12h.01M15 18h.01', dot: 'M12 10a2 2 0 100 4 2 2 0 000-4z', edit: 'M4 20h4L19 9l-4-4L4 16zM13 7l4 4', trash: 'M5 7h14M10 7V4h4v3M7 7l1 13h8l1-13',
};

@Component({
  selector: 'rx-icon', standalone: true, imports: [CommonModule], template: `<svg [attr.width]="size" [attr.height]="size" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path [attr.d]="d"/></svg>`,
  styles: [':host{display:inline-flex;vertical-align:middle;flex:none}'],
})
export class RxIcon { @Input() name = 'dot'; @Input() size = 18; get d() { return P[this.name] ?? P['dot']; } }

// ---------------------------------------------------------------- focus trap (modal dialogs and popovers)
@Directive({ selector: '[rxFocusTrap]', standalone: true })
export class FocusTrap implements AfterViewInit, OnDestroy {
  @Output() escape = new EventEmitter<void>();
  private el = inject<ElementRef<HTMLElement>>(ElementRef);
  private prev: HTMLElement | null = document.activeElement as HTMLElement | null;
  private sel = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';
  ngAfterViewInit() { queueMicrotask(() => { const f = this.el.nativeElement.querySelector<HTMLElement>('[autofocus],[data-autofocus]') ?? this.el.nativeElement.querySelector<HTMLElement>(this.sel); (f ?? this.el.nativeElement).focus(); }); }
  ngOnDestroy() { this.prev?.focus?.(); }
  @HostListener('keydown', ['$event']) onKey(e: KeyboardEvent) {
    if (e.key === 'Escape') { e.stopPropagation(); this.escape.emit(); return; }
    if (e.key !== 'Tab') return;
    const items = Array.from(this.el.nativeElement.querySelectorAll<HTMLElement>(this.sel)).filter(i => i.offsetParent !== null);
    if (!items.length) { e.preventDefault(); return; }
    const first = items[0], last = items[items.length - 1], a = document.activeElement;
    if (e.shiftKey && (a === first || a === this.el.nativeElement)) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && a === last) { e.preventDefault(); first.focus(); }
  }
}

// ---------------------------------------------------------------- dialog
@Component({
  selector: 'rx-dialog', standalone: true, imports: [CommonModule, RxIcon, FocusTrap],
  template: `
  <div class="backdrop" (mousedown)="$event.target === $event.currentTarget && close.emit()">
    <div class="dialog" [class.wide]="wide" role="dialog" aria-modal="true" [attr.aria-labelledby]="id" rxFocusTrap (escape)="close.emit()" tabindex="-1">
      <header><h2 [id]="id" style="font-size:18px">{{ title }}</h2><button type="button" class="btn icon ghost" (click)="close.emit()" aria-label="Close dialog"><rx-icon name="x"/></button></header>
      <div class="body"><ng-content/></div>
      <footer><ng-content select="[footer]"/></footer>
    </div>
  </div>`,
})
export class RxDialog { @Input() title = ''; @Input() wide = false; @Output() close = new EventEmitter<void>(); id = 'dlg' + Math.random().toString(36).slice(2, 8); }

// ---------------------------------------------------------------- status chip: icon + text + colour
const TONES: Record<string, [string, string]> = {
  Active: ['success', 'check'], Approved: ['success', 'check'], Published: ['success', 'check'], Resolved: ['success', 'check'], Signed: ['success', 'check'], Cleared: ['success', 'check'], Committed: ['success', 'check'], Paid: ['success', 'check'], Acknowledged: ['info', 'check'], Submitted: ['info', 'check'], CompensationApproved: ['success', 'check'], Compliant: ['success', 'check'], Pass: ['success', 'check'], Succeeded: ['success', 'check'],
  Pending: ['warn', 'clock'], PendingApproval: ['warn', 'clock'], PendingCompensation: ['warn', 'clock'], PendingPublish: ['warn', 'clock'], Due: ['warn', 'clock'], Raised: ['warn', 'clock'], Staged: ['warn', 'clock'], InProgress: ['warn', 'clock'], Warn: ['warn', 'alert'], Medium: ['warn', 'alert'], 'Compliant with warnings': ['warn', 'alert'], Open: ['warn', 'alert'], Draft: ['neutral', 'edit'], Calculated: ['info', 'chart'], Low: ['neutral', 'minus'],
  Rejected: ['danger', 'x'], Failed: ['danger', 'x'], Fail: ['danger', 'x'], High: ['danger', 'alert'], Block: ['danger', 'ban'], 'Non-compliant': ['danger', 'ban'], Flagged: ['danger', 'alert'], Critical: ['danger', 'alert'], Cancelled: ['neutral', 'x'], Withdrawn: ['neutral', 'x'], Closed: ['neutral', 'lock'], Locked: ['neutral', 'lock'], Suspended: ['neutral', 'ban'], Backtagged: ['info', 'swap'], Dismissed: ['neutral', 'x'],
};
@Component({
  selector: 'rx-status', standalone: true, imports: [CommonModule, RxIcon],
  template: `<span class="chip" [class]="'chip ' + tone"><rx-icon [name]="icon" [size]="12"/>{{ label || value }}</span>`,
})
export class RxStatus { @Input() value = ''; @Input() label?: string; get tone() { return TONES[this.value]?.[0] ?? 'neutral'; } get icon() { return TONES[this.value]?.[1] ?? 'dot'; } }

// ---------------------------------------------------------------- AI: confidence badge (never colour-only)
@Component({
  selector: 'rx-confidence', standalone: true, imports: [CommonModule], template: `<span class="conf" [attr.title]="'Confidence ' + (value * 100).toFixed(0) + ' percent'"><span class="bars" aria-hidden="true"><i [class.on]="level>=1" style="height:5px"></i><i [class.on]="level>=2" style="height:8px"></i><i [class.on]="level>=3" style="height:11px"></i></span><span class="xs" style="font-weight:500">Confidence</span> {{ band }} <span class="mono">{{ value.toFixed(2) }}</span></span>`,
})
export class RxConfidence { @Input() value = 0; get level() { return this.value >= 0.8 ? 3 : this.value >= 0.5 ? 2 : 1; } get band() { return this.value >= 0.8 ? 'High' : this.value >= 0.5 ? 'Medium' : 'Low'; } }

@Component({ selector: 'rx-ai-badge', standalone: true, imports: [CommonModule, RxIcon], template: `<span class="ai-badge"><rx-icon name="spark" [size]="11"/>AI</span>` })
export class RxAiBadge {}

// ---------------------------------------------------------------- AI: "Why this?" explainability popover
const EVIDENCE_ROUTE: Record<string, (id: string) => any[] | null> = {
  project: id => ['/projects', id], approval: id => ['/approvals', id], run: id => ['/allowance', id], arrear: () => ['/arrears'], roster: () => ['/roster'], foundation: () => ['/foundation'], rate: () => ['/foundation'],
};
@Component({
  selector: 'rx-why', standalone: true, imports: [CommonModule, RouterLink, RxIcon, FocusTrap],
  template: `
  <span style="position:relative;display:inline-block">
    <button type="button" class="btn sm ai" #trigger (click)="toggle()" [attr.aria-expanded]="open()" [attr.aria-controls]="id" aria-haspopup="dialog"><rx-icon name="info" [size]="14"/>Why this?</button>
    <ng-container *ngIf="open()">
      <div class="popover" [id]="id" role="dialog" aria-label="Why this suggestion" tabindex="-1" rxFocusTrap (escape)="open.set(false)" style="top:36px;left:0">
        <h3 style="font-size:14px;color:var(--rx-ai)">Why Rosterix suggests this</h3>
        <ol class="ai-reasons">
          <ng-container *ngFor="let r of reasons">
            <li>{{ r.text }}
              <ng-container *ngIf="link(r) as l"> <a [routerLink]="l" class="small">{{ r.evidenceLabel || 'See record' }}</a> </ng-container><ng-container *ngIf="!(link(r)) && (r.evidenceLabel)"> <span class="muted small">({{ r.evidenceLabel }})</span> </ng-container>
            </li>
          </ng-container>
        </ol>
        <p class="muted xs" style="margin-top:8px">Suggestions are advisory. You decide, and you can undo anything you accept.</p>
        <button type="button" class="btn sm" (click)="open.set(false)">Close</button>
      </div>
    </ng-container>
  </span>`,
})
export class RxWhy { @Input() reasons: J[] = []; open = signal(false); id = 'why' + Math.random().toString(36).slice(2, 8); toggle() { this.open.update(v => !v); } link(r: J) { return r.evidenceType && r.evidenceId ? EVIDENCE_ROUTE[r.evidenceType]?.(r.evidenceId) ?? null : null; } }

// ---------------------------------------------------------------- AI: suggestion card (Accept / Modify / Dismiss)
@Component({
  selector: 'rx-ai-card', standalone: true, imports: [CommonModule, RxIcon, RxConfidence, RxWhy, RxAiBadge],
  template: `
  <section class="ai-card" [attr.aria-label]="'AI suggestion: ' + (heading || summary)">
    <div class="ai-h"><rx-ai-badge/> <span>{{ heading || 'Suggestion' }}</span><span class="grow"></span> <rx-confidence [value]="confidence"/></div>
    <p class="ai-sum">{{ summary }}</p>
    <ng-container *ngIf="impact"> <p class="ai-impact">{{ impact }}</p> </ng-container>
    <ng-content/>
    <div class="row" style="margin-top:8px">
      <ng-container *ngIf="acceptLabel"> <button type="button" class="btn sm primary" (click)="accept.emit()" [disabled]="busy"><rx-icon name="check" [size]="14"/>{{ acceptLabel }}</button> </ng-container>
      <ng-container *ngIf="modifyLabel"> <button type="button" class="btn sm" (click)="modify.emit()">{{ modifyLabel }}</button> </ng-container>
      <ng-container *ngIf="dismissLabel"> <button type="button" class="btn sm ghost" (click)="dismiss.emit()"><rx-icon name="x" [size]="14"/>{{ dismissLabel }}</button> </ng-container>
      <rx-why [reasons]="reasons"/>
    </div>
  </section>`,
})
export class RxAiCard {
  @Input() heading = ''; @Input() summary = ''; @Input() impact = ''; @Input() confidence = 0; @Input() reasons: J[] = [];
  @Input() acceptLabel = ''; @Input() modifyLabel = ''; @Input() dismissLabel = 'Dismiss'; @Input() busy = false;
  @Output() accept = new EventEmitter<void>(); @Output() modify = new EventEmitter<void>(); @Output() dismiss = new EventEmitter<void>();
}

@Component({ selector: 'rx-ai-skeleton', standalone: true, imports: [CommonModule], template: `<div class="ai-card" role="status" aria-live="polite"><div class="ai-h">AI is thinking…</div><div class="ai-skel" style="width:80%;margin:10px 0 6px"></div><div class="ai-skel" style="width:55%"></div><span class="sr-only">Loading suggestion</span></div>` })
export class RxAiSkeleton {}

// ---------------------------------------------------------------- AI: right-hand assistant panel (collapsed by default)
@Component({
  selector: 'rx-ai-panel', standalone: true, imports: [CommonModule, RxIcon, RxConfidence, RxWhy, RouterLink, RxAiBadge],
  template: `
  <button type="button" class="ai-toggle" (click)="panel.toggle()" [attr.aria-expanded]="panel.open()" aria-controls="ai-panel"><rx-icon name="spark" [size]="16"/>{{ panel.open() ? 'Hide assistant' : 'AI assistant' }}<ng-container *ngIf="!panel.open() && panel.items().length"> <span class="mono">({{ panel.items().length }})</span> </ng-container></button>
  <aside id="ai-panel" class="aipanel" [attr.data-open]="panel.open()" aria-label="AI assistant" [attr.aria-hidden]="!panel.open()" [attr.inert]="panel.open() ? null : ''">
    <div class="inner">
      <div class="row between"><h2 style="font-size:16px;color:var(--rx-ai)"><rx-ai-badge/> Assistant</h2></div>
      <p class="muted small">Advisory only. Nothing here changes a record until you choose to apply it.</p>
      <ng-container *ngFor="let i of panel.items()">
        <section class="ai-card" [attr.aria-label]="i.title">
          <div class="ai-h"><span>{{ i.title }}</span><span class="grow"></span><rx-confidence [value]="i.confidence"/></div>
          <p class="ai-sum">{{ i.summary }}</p>
          <ng-container *ngIf="i.impact"> <p class="ai-impact">{{ i.impact }}</p> </ng-container>
          <div class="row" style="margin-top:8px">
            <ng-container *ngIf="i.action"> <button type="button" class="btn sm primary" (click)="i.action!()">{{ i.actionLabel || 'Apply' }}</button> </ng-container>
            <ng-container *ngIf="i.link"> <a class="btn sm" [routerLink]="i.link">Open</a> </ng-container>
            <ng-container *ngIf="i.reasons?.length"> <rx-why [reasons]="i.reasons!"/> </ng-container>
          </div>
        </section>
      </ng-container><ng-container *ngIf="!(panel.items())?.length"> <div class="empty"><h3>Nothing to suggest here</h3><p class="small">Suggestions for the page you’re on show up in this panel.</p></div> </ng-container>
    </div>
  </aside>`,
})
export class RxAiPanel { panel = inject(AiPanel); }

// ---------------------------------------------------------------- before/after impact preview (AI-UC-06 and any wide change)
@Component({
  selector: 'rx-impact', standalone: true, imports: [CommonModule, RxStatus],
  template: `
  <table class="diff"><caption class="sr-only">What changes: before and after</caption>
    <thead><tr><th scope="col">Before</th><th scope="col">After</th></tr></thead>
    <tbody *ngFor="let r of rows">
      <tr><td class="lbl" colspan="2">{{ r.entityType || r.field }}: {{ r.label || '' }} <rx-status *ngIf="r.change" [value]="r.change"/></td></tr>
      <tr><td class="before">{{ r.before }}</td><td class="after">{{ r.after }}</td></tr>
    </tbody>
  </table>`,
})
export class RxImpact { @Input() rows: J[] = []; }

// ---------------------------------------------------------------- empty state, header
@Component({ selector: 'rx-empty', standalone: true, imports: [CommonModule], template: `<div class="empty"><h3>{{ title }}</h3><p>{{ text }}</p><ng-content/></div>` })
export class RxEmpty { @Input() title = 'Nothing here yet'; @Input() text = ''; }

@Component({
  selector: 'rx-page-header', standalone: true, imports: [CommonModule], template: `<div class="page-h"><div><h1 #h tabindex="-1">{{ title }}</h1><ng-container *ngIf="sub"> <p>{{ sub }}</p> </ng-container></div><div class="row"><ng-content/></div></div>`,
})
export class RxPageHeader { @Input() title = ''; @Input() sub = ''; }

// ---------------------------------------------------------------- data table: sorting, paging, column chooser hooks
export interface Column { key: string; label: string; type?: 'text' | 'num' | 'money' | 'date' | 'chip' | 'mono'; wrap?: boolean; hidden?: boolean; }

@Component({
  selector: 'rx-table', standalone: true, imports: [CommonModule, NgTemplateOutlet, RxIcon, RxStatus, InrPipe],
  template: `
  <div class="table-wrap" tabindex="0" role="region" aria-label="Scrollable table">
  <table class="rx" [class.comfortable]="comfortable">
    <ng-container *ngIf="caption"> <caption class="sr-only">{{ caption }}</caption> </ng-container>
    <thead><tr>
      <ng-container *ngIf="selectable"> <th style="width:36px"><input type="checkbox" [checked]="allSelected()" (change)="toggleAll($any($event.target).checked)" [attr.aria-label]="'Select all rows'"></th> </ng-container>
      <ng-container *ngFor="let c of visibleCols()">
        <th [class.num]="c.type==='num'||c.type==='money'" [attr.aria-sort]="sortKey()===c.key ? (sortDir()==='asc'?'ascending':'descending') : 'none'" scope="col">
          <ng-container *ngIf="sortable"> <button type="button" class="sort" (click)="sortBy(c.key)">{{ c.label }} <rx-icon [name]="sortKey()===c.key ? (sortDir()==='asc'?'up':'down') : 'swap'" [size]="12"/></button> </ng-container><ng-container *ngIf="!(sortable)"> {{ c.label }} </ng-container>
        </th>
      </ng-container>
      <ng-container *ngIf="rowTpl"> <th scope="col"><span class="sr-only">Actions</span></th> </ng-container>
    </tr></thead>
    <tbody>
      <ng-container *ngFor="let r of pageRows()">
        <tr [class.selected]="selectable && selected.has(rowId(r))">
          <ng-container *ngIf="selectable"> <td><input type="checkbox" [checked]="selected.has(rowId(r))" (change)="toggle(r)" [attr.aria-label]="'Select ' + (r[labelKey] ?? 'row')"></td> </ng-container>
          <ng-container *ngFor="let c of visibleCols()">
            <td [class.num]="c.type==='num'||c.type==='money'" [class.wrap]="c.wrap" [class.mono]="c.type==='mono'">
              <ng-container [ngSwitch]="c.type"><ng-container *ngSwitchCase="'chip'"> <rx-status [value]="r[c.key]"/> </ng-container><ng-container *ngSwitchCase="'money'"> {{ r[c.key] | inr }} </ng-container><ng-container *ngSwitchCase="'date'"> {{ r[c.key] || '—' }} </ng-container><ng-container *ngSwitchDefault> {{ r[c.key] ?? '—' }} </ng-container></ng-container>
            </td>
          </ng-container>
          <ng-container *ngIf="rowTpl"> <td><ng-container [ngTemplateOutlet]="rowTpl" [ngTemplateOutletContext]="{ $implicit: r }"/></td> </ng-container>
        </tr>
      </ng-container><ng-container *ngIf="!(pageRows())?.length"> <tr><td [attr.colspan]="visibleCols().length + (selectable?1:0) + (rowTpl?1:0)"><div class="empty"><h3>{{ emptyTitle }}</h3><p>{{ emptyText }}</p></div></td></tr> </ng-container>
    </tbody>
  </table></div>
  <ng-container *ngIf="paged && total() > pageSize">
    <div class="pager"><span class="muted small" aria-live="polite">{{ from() }}–{{ to() }} of {{ total() }}</span>
      <button type="button" class="btn sm" (click)="page.set(page()-1)" [disabled]="page()===1">Previous</button>
      <button type="button" class="btn sm" (click)="page.set(page()+1)" [disabled]="to()>=total()">Next</button></div>
  </ng-container>`,
})
export class RxTable {
  @Input() set rows(v: J[]) { this._rows.set(v ?? []); this.page.set(1); }
  @Input() set columns(v: Column[]) { this._cols.set(v); }
  @Input() caption = ''; @Input() sortable = true; @Input() paged = true; @Input() pageSize = 25; @Input() comfortable = false;
  @Input() selectable = false; @Input() idKey = 'id'; @Input() labelKey = 'name'; @Input() rowTpl?: TemplateRef<J>;
  @Input() emptyTitle = 'No results'; @Input() emptyText = 'Try changing the filters.'; @Input() serverSide = false;
  @Input() hiddenKeys: string[] = [];
  @Output() selectionChange = new EventEmitter<J[]>(); @Output() sortChange = new EventEmitter<{ key: string; dir: 'asc' | 'desc' }>();
  _rows = signal<J[]>([]); _cols = signal<Column[]>([]); sortKey = signal(''); sortDir = signal<'asc' | 'desc'>('asc'); page = signal(1); selected = new Set<string>();
  visibleCols = computed(() => this._cols().filter(c => !c.hidden && !this.hiddenKeys.includes(c.key)));
  sorted = computed(() => {
    const k = this.sortKey(); const r = [...this._rows()]; if (!k || this.serverSide) return r;
    const d = this.sortDir() === 'asc' ? 1 : -1;
    return r.sort((a, b) => { const x = a[k], y = b[k]; if (x === y) return 0; if (x === null || x === undefined) return 1; if (y === null || y === undefined) return -1; return (typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), undefined, { numeric: true })) * d; });
  });
  total = computed(() => this.sorted().length);
  from = computed(() => (this.page() - 1) * this.pageSize + 1); to = computed(() => Math.min(this.total(), this.page() * this.pageSize));
  pageRows = computed(() => (this.paged ? this.sorted().slice((this.page() - 1) * this.pageSize, this.page() * this.pageSize) : this.sorted()));
  rowId(r: J): string { return String(r[this.idKey]); }
  sortBy(k: string) { if (this.sortKey() === k) this.sortDir.update(d => (d === 'asc' ? 'desc' : 'asc')); else { this.sortKey.set(k); this.sortDir.set('asc'); } this.sortChange.emit({ key: k, dir: this.sortDir() }); }
  allSelected() { const p = this.pageRows(); return p.length > 0 && p.every(r => this.selected.has(this.rowId(r))); }
  toggle(r: J) { const id = this.rowId(r); this.selected.has(id) ? this.selected.delete(id) : this.selected.add(id); this.emit(); }
  toggleAll(on: boolean) { for (const r of this.pageRows()) on ? this.selected.add(this.rowId(r)) : this.selected.delete(this.rowId(r)); this.emit(); }
  clearSelection() { this.selected.clear(); this.emit(); }
  private emit() { this.selectionChange.emit(this._rows().filter(r => this.selected.has(this.rowId(r)))); }
}

export const UI = [RxIcon, RxDialog, RxStatus, RxConfidence, RxAiBadge, RxWhy, RxAiCard, RxAiSkeleton, RxPageHeader, RxEmpty, RxTable, RxImpact] as const;
