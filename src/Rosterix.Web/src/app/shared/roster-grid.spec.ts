import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Cell, RxRosterGrid } from './roster-grid';

const emps = [{ id: 'e1', name: 'Ravi Kumar', number: 'E1', grade: 'G3' }, { id: 'e2', name: 'Isha Rao', number: 'E2', grade: 'G3' }];
const days = ['2026-10-01', '2026-10-02', '2026-10-03', '2026-10-04'].map((d, i) => ({ date: d, dow: ['Thu', 'Fri', 'Sat', 'Sun'][i], holiday: i === 1 ? 'Gandhi Jayanti' : null, restricted: i === 1 }));
const shifts = [{ id: 's1', code: 'EVE', name: 'Evening', projectShift: true }, { id: 's2', code: 'GEN', name: 'General', projectShift: true }];
const entries = [{ e: 'e1', d: '2026-10-01', s: 's1', t: 'Shift', ai: false }, { e: 'e1', d: '2026-10-02', s: null, t: 'Holiday', ai: false }, { e: 'e2', d: '2026-10-01', s: 's2', t: 'Shift', ai: true }, { e: 'e1', d: '2026-10-04', s: null, t: 'WeeklyOff', ai: false }];
const conflicts = [{ employeeId: 'e1', severity: 'Block', ruleCode: 'BR-02', affected: ['date:2026-10-03'] }, { employeeId: 'e2', severity: 'Warn', ruleCode: 'BR-07', affected: ['week:2026-09-28'] }];

function setup(editable = true): ComponentFixture<RxRosterGrid> {
  const f = TestBed.createComponent(RxRosterGrid); const c = f.componentInstance;
  c.employees = emps; c.days = days; c.entries = entries; c.shiftTypes = shifts; c.conflicts = conflicts; c.editable = editable;
  c.ngOnChanges({ employees: {} as never, days: {} as never, entries: {} as never, shiftTypes: {} as never, conflicts: {} as never }); f.detectChanges(); return f;
}
const cell = (f: ComponentFixture<RxRosterGrid>, e: string, d: string) => (f.nativeElement as HTMLElement).querySelector<HTMLElement>(`[id="c-${e}-${d}"]`)!;
const key = (f: ComponentFixture<RxRosterGrid>, el: HTMLElement, k: string, o: KeyboardEventInit = {}) => { el.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, ...o })); f.detectChanges(); };

describe('rx-roster-grid (WCAG 2.2: keyboard, grid semantics, no colour-only meaning)', () => {
  it('exposes ARIA grid structure with row and column counts and indexes', () => {
    const f = setup(); const g = (f.nativeElement as HTMLElement).querySelector('[role=grid]')!;
    expect(g.getAttribute('aria-rowcount')).toBe('3'); expect(g.getAttribute('aria-colcount')).toBe('5');
    expect(g.querySelectorAll('[role=row]').length).toBe(3); expect(g.querySelectorAll('[role=columnheader]').length).toBe(5); expect(g.querySelectorAll('[role=rowheader]').length).toBe(2);
    expect(cell(f, 'e1', '2026-10-01').getAttribute('aria-colindex')).toBe('2'); expect(cell(f, 'e2', '2026-10-01').closest('[role=row]')!.getAttribute('aria-rowindex')).toBe('3');
  });
  it('shows text codes, never colour alone, and an accessible name per cell', () => {
    const f = setup(); expect(cell(f, 'e1', '2026-10-01').textContent).toContain('EVE'); expect(cell(f, 'e1', '2026-10-04').textContent).toContain('OFF'); expect(cell(f, 'e1', '2026-10-02').textContent).toContain('HOL');
    expect(cell(f, 'e1', '2026-10-01').getAttribute('aria-label')).toBe('Ravi Kumar, Thu 01: Evening');
    expect(cell(f, 'e2', '2026-10-01').getAttribute('aria-label')).toContain('suggested by AI'); expect(cell(f, 'e2', '2026-10-01').classList).toContain('ai-cell');
  });
  it('marks blocking and warning conflicts with a glyph, a class and the accessible name', () => {
    const f = setup(); const b = cell(f, 'e1', '2026-10-03'); expect(b.classList).toContain('conflict'); expect(b.textContent).toContain('!'); expect(b.getAttribute('aria-label')).toContain('Blocking rule conflict');
    const w = cell(f, 'e2', '2026-10-01'); expect(w.classList).toContain('warnc'); expect(w.getAttribute('aria-label')).toContain('Rule warning'); // week: marker expands to the whole week
  });
  it('uses a roving tabindex: exactly one cell is a tab stop', () => { const f = setup(); expect((f.nativeElement as HTMLElement).querySelectorAll('[role=gridcell][tabindex="0"]').length).toBe(1); });
  it('moves with arrow keys, Home/End and PageDown, never beyond the edges', () => {
    const f = setup(); const g = (f.nativeElement as HTMLElement).querySelector<HTMLElement>('[role=grid]')!;
    key(f, g, 'ArrowRight'); expect(f.componentInstance.active()).toEqual({ r: 0, c: 1 }); key(f, g, 'ArrowDown'); expect(f.componentInstance.active()).toEqual({ r: 1, c: 1 });
    key(f, g, 'ArrowDown'); expect(f.componentInstance.active().r).toBe(1); key(f, g, 'End'); expect(f.componentInstance.active().c).toBe(3); key(f, g, 'Home'); expect(f.componentInstance.active().c).toBe(0);
    key(f, g, 'PageUp'); expect(f.componentInstance.active().r).toBe(0);
  });
  it('Space selects, Shift+Arrow extends a range, Escape clears, Enter hands the cells to the assign flow (the non-drag path)', () => {
    const f = setup(); const g = (f.nativeElement as HTMLElement).querySelector<HTMLElement>('[role=grid]')!; let got: Cell[] = []; f.componentInstance.activateCells.subscribe(c => (got = c));
    key(f, g, ' '); expect(cell(f, 'e1', '2026-10-01').getAttribute('aria-selected')).toBe('true');
    key(f, g, 'ArrowRight', { shiftKey: true }); key(f, g, 'ArrowRight', { shiftKey: true }); expect(f.componentInstance.selectedCells().length).toBe(3);
    key(f, g, 'Enter'); expect(got.map(c => c.d)).toEqual(['2026-10-01', '2026-10-02', '2026-10-03']);
    key(f, g, 'Escape'); expect(f.componentInstance.sel().size).toBe(0);
  });
  it('Enter on an unselected cell assigns just that cell', () => { const f = setup(); const g = (f.nativeElement as HTMLElement).querySelector<HTMLElement>('[role=grid]')!; let got: Cell[] = []; f.componentInstance.activateCells.subscribe(c => (got = c)); key(f, g, 'Enter'); expect(got).toEqual([{ e: 'e1', d: '2026-10-01' }]); });
  it('drag and drop is only an accelerator: a drop emits the same cells + choice the keyboard path would produce', () => {
    const f = setup(); let drop: unknown; f.componentInstance.dropped.subscribe(d => (drop = d));
    const dt = { getData: (t: string) => (t === 'text/rx-choice' ? 'shift:s1' : ''), dropEffect: 'copy' };
    const ev = new Event('drop', { bubbles: true, cancelable: true }) as Event & { dataTransfer: unknown }; ev.dataTransfer = dt; cell(f, 'e2', '2026-10-03').dispatchEvent(ev);
    expect(drop).toEqual({ cells: [{ e: 'e2', d: '2026-10-03' }], choice: 'shift:s1' });
  });
  it('a read-only grid (AI preview) cannot be selected, assigned or dropped on', () => {
    const f = setup(false); const g = (f.nativeElement as HTMLElement).querySelector<HTMLElement>('[role=grid]')!; let n = 0; f.componentInstance.activateCells.subscribe(() => n++); f.componentInstance.dropped.subscribe(() => n++);
    key(f, g, 'Enter'); key(f, g, ' '); expect(n).toBe(0); expect(cell(f, 'e1', '2026-10-01').getAttribute('aria-selected')).toBeNull(); expect(cell(f, 'e1', '2026-10-01').getAttribute('aria-readonly')).toBe('true');
  });
  it('focusCell moves the roving tab stop (used by "Go to cell" in the conflicts list)', () => { const f = setup(); f.componentInstance.focusCell('e2', '2026-10-03'); f.detectChanges(); expect(f.componentInstance.active()).toEqual({ r: 1, c: 2 }); expect(cell(f, 'e2', '2026-10-03').getAttribute('tabindex')).toBe('0'); });
});
