import { CommonModule } from '@angular/common';
import { Component, ElementRef, EventEmitter, Input, OnChanges, Output, SimpleChanges, ViewChild, computed, signal } from '@angular/core';
import { J } from '../core/api.service';

export interface Cell { e: string; d: string; }
export interface DropEvent { cells: Cell[]; choice: string; }

/**
 * Roster grid (employees × days). WCAG 2.2 notes:
 *  2.1.1  fully keyboard operable: arrows/Home/End/PageUp/PageDown move, Space selects, Shift+arrows extends, Enter assigns, Esc clears.
 *  2.5.7  drag is only an accelerator; "select cell → Enter → pick → confirm" does everything drag does.
 *  2.4.11 focus is a 2px ring with offset on every cell; 2.5.8 cells are ≥ 24px (32 compact / 40 comfortable).
 *  1.4.1  no meaning by colour alone: every cell shows a text code and conflicts add a "!" glyph plus an accessible name.
 */
@Component({
  selector: 'rx-roster-grid', standalone: true, imports: [CommonModule], template: `
  <div class="grid-scroll" #scroll>
    <div class="rx-grid" role="grid" [attr.aria-label]="label" [attr.aria-rowcount]="employees.length + 1" [attr.aria-colcount]="days.length + 1" [attr.aria-multiselectable]="editable"
         [style.grid-template-columns]="'200px repeat(' + days.length + ', minmax(44px, 1fr))'" [style.grid-auto-rows]="'auto'"
         (keydown)="onKey($event)">
      <div role="row" aria-rowindex="1">
        <div class="corner" role="columnheader" aria-colindex="1" [style.height.px]="46">Employee</div>
        <ng-container *ngFor="let d of days; let ci = index">
          <div class="hd" role="columnheader" [attr.aria-colindex]="ci + 2" [class.wk]="d.dow === 'Sat' || d.dow === 'Sun'" [class.hol]="d.holiday" [style.height.px]="46"
               [attr.aria-label]="d.dow + ' ' + d.date.slice(8) + (d.holiday ? ', ' + d.holiday + (d.restricted ? ' (restricted holiday)' : '') : '')" [title]="d.holiday || ''">
            <span class="xs">{{ d.dow }}</span><span class="d">{{ d.date.slice(8) }}</span><ng-container *ngIf="d.holiday"> <span class="xs" aria-hidden="true">{{ d.restricted ? 'RH' : 'H' }}</span> </ng-container>
          </div>
        </ng-container>
      </div>
      <ng-container *ngFor="let row of matrix; let ri = index">
        <div role="row" [attr.aria-rowindex]="ri + 2">
          <div class="rh" role="rowheader" aria-colindex="1" [style.height.px]="rowH"><span class="nm" [title]="row.emp.name">{{ row.emp.name }}</span><span class="no">{{ row.emp.number }} · {{ row.emp.grade }}</span></div>
          <ng-container *ngFor="let c of row.cells; let ci = index">
            <div class="rx-grid-cell" role="gridcell" [attr.aria-colindex]="ci + 2" [attr.id]="'c-' + c.e + '-' + c.d"
                 [attr.tabindex]="isActive(ri, ci) ? 0 : -1" [attr.aria-selected]="editable ? isSel(c.e, c.d) : null" [attr.aria-readonly]="!editable"
                 [attr.data-t]="c.t" [attr.data-s]="c.si" [class.ai-cell]="c.ai" [class.conflict]="c.sev === 'Block'" [class.warnc]="c.sev === 'Warn'" [class.drop]="dropKey === c.e + '|' + c.d"
                 [style.height.px]="rowH" [attr.aria-label]="c.label"
                 (click)="onClick($event, ri, ci)" (dblclick)="activate()" (focus)="setActive(ri, ci, false)"
                 (dragover)="onDragOver($event, c.e, c.d)" (dragleave)="dropKey = ''" (drop)="onDrop($event, c.e, c.d)">
              {{ c.text }}
              <ng-container *ngIf="c.sev"> <span class="flag" [class.warn]="c.sev === 'Warn'" aria-hidden="true">{{ c.sev === 'Block' ? '!' : '▲' }}</span> </ng-container>
              <ng-container *ngIf="c.ai"> <span class="aitag" aria-hidden="true">AI</span> </ng-container>
            </div>
          </ng-container>
        </div>
      </ng-container>
    </div>
  </div>
  <p class="sr-only" aria-live="polite">{{ live() }}</p>`,
})
export class RxRosterGrid implements OnChanges {
  @Input() label = 'Roster'; @Input() employees: J[] = []; @Input() days: J[] = []; @Input() entries: J[] = []; @Input() shiftTypes: J[] = [];
  @Input() conflicts: J[] = []; @Input() editable = true; @Input() density: 'compact' | 'comfortable' = 'compact';
  @Output() activateCells = new EventEmitter<Cell[]>(); @Output() dropped = new EventEmitter<DropEvent>(); @Output() activeEmployee = new EventEmitter<string>();
  @ViewChild('scroll', { static: true }) scroll!: ElementRef<HTMLElement>;

  matrix: { emp: J; cells: J[] }[] = [];
  private map = new Map<string, J>(); private cmap = new Map<string, string>(); private shiftIdx = new Map<string, number>(); private shiftCode = new Map<string, string>();
  active = signal({ r: 0, c: 0 }); sel = signal<Set<string>>(new Set()); anchor: { r: number; c: number } | null = null; dropKey = ''; live = signal('');
  get rowH() { return this.density === 'compact' ? 32 : 40; }

  ngOnChanges(ch: SimpleChanges) {
    if (ch['shiftTypes']) { this.shiftIdx.clear(); this.shiftCode.clear(); this.shiftTypes.filter(s => s.projectShift).forEach((s, i) => this.shiftIdx.set(s.id, i % 5)); this.shiftTypes.forEach(s => this.shiftCode.set(s.id, s.code)); }
    if (ch['entries'] || ch['shiftTypes']) { this.map.clear(); for (const e of this.entries) this.map.set(e.e + '|' + e.d, e); }
    if (ch['conflicts'] || ch['employees'] || ch['days']) this.buildConflicts();
    this.matrix = this.employees.map(emp => ({ emp, cells: this.days.map(d => this.build(emp, d)) }));
  }
  private build(emp: J, d: J): J { const c = this.cell(emp.id, d.date); const sev = this.sev(emp.id, d.date); return { ...c, e: emp.id, d: d.date, sev, label: this.aria(emp, d, c) }; }
  private buildConflicts() {
    this.cmap.clear();
    for (const v of this.conflicts) for (const a of (v.affected ?? []) as string[]) {
      let dates: string[] = [];
      if (a.startsWith('date:')) dates = [a.slice(5)];
      else if (a.startsWith('week:')) { const s = new Date(a.slice(5) + 'T00:00:00'); dates = Array.from({ length: 7 }, (_, i) => { const d = new Date(s); d.setDate(d.getDate() + i); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }); }
      for (const d of dates) { const k = v.employeeId + '|' + d; if (this.cmap.get(k) !== 'Block') this.cmap.set(k, v.severity); }
    }
  }
  cell(e: string, d: string): J {
    const x = this.map.get(e + '|' + d);
    if (!x) return { t: 'Empty', text: '·', si: -1, ai: false, name: 'Not scheduled' };
    if (x.t === 'Shift') return { t: 'Shift', text: this.shiftCode.get(x.s)?.slice(0, 5) ?? '?', si: this.shiftIdx.get(x.s) ?? 0, ai: x.ai, name: this.shiftTypes.find(s => s.id === x.s)?.name ?? 'Shift' };
    const map: J = { WeeklyOff: ['OFF', 'Weekly off'], Leave: ['LV', 'Leave'], Holiday: ['HOL', 'Holiday'] };
    return { t: x.t, text: map[x.t][0], si: -1, ai: x.ai, name: map[x.t][1] };
  }
  sev(e: string, d: string): string | undefined { return this.cmap.get(e + '|' + d); }
  aria(emp: J, d: J, c: J): string { const s = this.sev(emp.id, d.date); return `${emp.name}, ${d.dow} ${d.date.slice(8)}: ${c.name}${c.ai ? ', suggested by AI' : ''}${s ? (s === 'Block' ? '. Blocking rule conflict' : '. Rule warning') : ''}`; }
  isActive(r: number, c: number) { const a = this.active(); return a.r === r && a.c === c; }
  isSel(e: string, d: string) { return this.sel().has(e + '|' + d); }
  selectedCells(): Cell[] { const out = [...this.sel()].map(k => { const [e, d] = k.split('|'); return { e, d }; }); if (out.length) return out; const a = this.active(); return this.employees[a.r] && this.days[a.c] ? [{ e: this.employees[a.r].id, d: this.days[a.c].date }] : []; }

  setActive(r: number, c: number, focus = true) {
    this.active.set({ r, c }); this.activeEmployee.emit(this.employees[r]?.id);
    if (focus) queueMicrotask(() => this.focusEl(r, c));
  }
  private focusEl(r: number, c: number) { const el = this.scroll.nativeElement.querySelector<HTMLElement>(`#${CSS.escape('c-' + this.employees[r]?.id + '-' + this.days[c]?.date)}`); el?.focus({ preventScroll: false }); el?.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }
  focusCell(emp: string, date: string) { const r = this.employees.findIndex(e => e.id === emp); const c = this.days.findIndex(d => d.date === date); if (r >= 0 && c >= 0) { this.sel.set(new Set()); this.setActive(r, c); } }
  clearSelection() { this.sel.set(new Set()); this.anchor = null; }

  private rect(r1: number, c1: number, r2: number, c2: number) {
    const s = new Set<string>();
    for (let r = Math.min(r1, r2); r <= Math.max(r1, r2); r++) for (let c = Math.min(c1, c2); c <= Math.max(c1, c2); c++) s.add(this.employees[r].id + '|' + this.days[c].date);
    return s;
  }
  onClick(ev: MouseEvent, r: number, c: number) {
    if (!this.editable) { this.setActive(r, c, false); return; }
    const key = this.employees[r].id + '|' + this.days[c].date;
    if (ev.shiftKey && this.anchor) this.sel.set(this.rect(this.anchor.r, this.anchor.c, r, c));
    else if (ev.ctrlKey || ev.metaKey) { const s = new Set(this.sel()); s.has(key) ? s.delete(key) : s.add(key); this.sel.set(s); this.anchor = { r, c }; }
    else { this.sel.set(new Set([key])); this.anchor = { r, c }; }
    this.setActive(r, c, false); this.live.set(`${this.sel().size} cell${this.sel().size === 1 ? '' : 's'} selected`);
  }
  activate() { if (this.editable) this.activateCells.emit(this.selectedCells()); }

  onKey(e: KeyboardEvent) {
    const a = this.active(); const R = this.employees.length - 1, C = this.days.length - 1; let { r, c } = a; let moved = true;
    switch (e.key) {
      case 'ArrowRight': c = Math.min(C, c + 1); break; case 'ArrowLeft': c = Math.max(0, c - 1); break; case 'ArrowDown': r = Math.min(R, r + 1); break; case 'ArrowUp': r = Math.max(0, r - 1); break;
      case 'Home': c = e.ctrlKey ? 0 : 0; if (e.ctrlKey) r = 0; break; case 'End': c = C; if (e.ctrlKey) r = R; break;
      case 'PageDown': r = Math.min(R, r + 7); break; case 'PageUp': r = Math.max(0, r - 7); break;
      case ' ': if (this.editable) { const k = this.employees[r].id + '|' + this.days[c].date; const s = new Set(this.sel()); s.has(k) ? s.delete(k) : s.add(k); this.sel.set(s); this.anchor = { r, c }; this.live.set(`${s.size} selected`); } e.preventDefault(); return;
      case 'Enter': if (this.editable) { e.preventDefault(); this.activateCells.emit(this.selectedCells()); } return;
      case 'Escape': if (this.sel().size) { this.clearSelection(); this.live.set('Selection cleared'); e.stopPropagation(); } return;
      case 'a': case 'A': if ((e.ctrlKey || e.metaKey) && this.editable) { e.preventDefault(); this.sel.set(this.rect(0, 0, R, C)); this.live.set('All cells selected'); } return;
      default: moved = false;
    }
    if (!moved) return;
    e.preventDefault();
    if (e.shiftKey && this.editable) { if (!this.anchor) this.anchor = { r: a.r, c: a.c }; this.sel.set(this.rect(this.anchor.r, this.anchor.c, r, c)); }
    this.setActive(r, c);
  }

  // ---- drag is an accelerator only; the keyboard path above does the same job
  onDragOver(e: DragEvent, emp: string, date: string) { if (!this.editable) return; e.preventDefault(); this.dropKey = emp + '|' + date; }
  onDrop(e: DragEvent, emp: string, date: string) {
    this.dropKey = ''; if (!this.editable) return; e.preventDefault();
    const choice = e.dataTransfer?.getData('text/rx-choice'); if (!choice) return;
    const cells = this.isSel(emp, date) ? this.selectedCells() : [{ e: emp, d: date }];
    this.dropped.emit({ cells, choice });
  }
}
