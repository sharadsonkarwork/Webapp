import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RxAiCard, RxConfidence, RxImpact, RxStatus, RxTable, RxWhy } from './ui';

const el = (f: ComponentFixture<unknown>) => f.nativeElement as HTMLElement;

describe('rx-confidence', () => {
  const make = (v: number) => { const f = TestBed.createComponent(RxConfidence); f.componentInstance.value = v; f.detectChanges(); return f; };
  it('never relies on colour: shows a band word, the number, and the word Confidence', () => {
    const t = el(make(0.82)).textContent!; expect(t).toContain('High'); expect(t).toContain('0.82'); expect(t).toContain('Confidence');
  });
  it('uses the specified bands', () => { expect(el(make(0.49)).textContent).toContain('Low'); expect(el(make(0.5)).textContent).toContain('Medium'); expect(el(make(0.79)).textContent).toContain('Medium'); expect(el(make(0.8)).textContent).toContain('High'); });
});

describe('rx-status', () => {
  it('pairs every status with an icon and text', () => {
    const f = TestBed.createComponent(RxStatus); f.componentInstance.value = 'Rejected'; f.detectChanges();
    expect(el(f).querySelector('svg')).toBeTruthy(); expect(el(f).textContent).toContain('Rejected'); expect(el(f).querySelector('.chip.danger')).toBeTruthy();
  });
  it('treats unknown values as neutral rather than failing', () => { const f = TestBed.createComponent(RxStatus); f.componentInstance.value = 'Whatever'; f.detectChanges(); expect(el(f).querySelector('.chip.neutral')).toBeTruthy(); });
});

describe('rx-impact', () => {
  it('renders a real table (no stray ARIA table roles) with before and after columns', () => {
    const f = TestBed.createComponent(RxImpact); f.componentInstance.rows = [{ entityType: 'Tag', label: 'Festive tag', before: 'open', after: 'ends 31 Aug', change: 'Changed' }]; f.detectChanges();
    const t = el(f).querySelector('table.diff')!; expect(t).toBeTruthy(); expect(t.querySelectorAll('th[scope=col]').length).toBe(2); expect(el(f).querySelector('[role=table],[role=cell]')).toBeNull();
    expect(t.textContent).toContain('ends 31 Aug');
  });
});

describe('rx-why', () => {
  it('opens a labelled dialog of reasons, and Escape closes it', async () => {
    await TestBed.configureTestingModule({ providers: [provideRouter([])] }).compileComponents();
    const f = TestBed.createComponent(RxWhy); f.componentInstance.reasons = [{ text: 'Because history', evidenceType: 'project', evidenceId: 'p1', evidenceLabel: 'Store ops' }, { text: 'Because fit' }]; f.detectChanges();
    const btn = el(f).querySelector('button')!; expect(btn.getAttribute('aria-expanded')).toBe('false');
    btn.click(); f.detectChanges(); const pop = el(f).querySelector('[role=dialog]')!; expect(pop.textContent).toContain('Because history'); expect(pop.querySelector('a')!.getAttribute('href')).toBe('/projects/p1'); expect(btn.getAttribute('aria-expanded')).toBe('true');
    pop.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); f.detectChanges(); expect(el(f).querySelector('[role=dialog]')).toBeNull();
  });
});

describe('rx-ai-card', () => {
  it('offers Accept, Dismiss and Why with ≥32px targets (via .btn.sm min-height) and emits outputs', async () => {
    await TestBed.configureTestingModule({ providers: [provideRouter([])] }).compileComponents();
    const f = TestBed.createComponent(RxAiCard); const c = f.componentInstance; c.summary = 'Do X'; c.confidence = 0.6; c.acceptLabel = 'Apply'; c.dismissLabel = 'Dismiss'; c.reasons = [{ text: 'r' }];
    let accepted = 0, dismissed = 0; c.accept.subscribe(() => accepted++); c.dismiss.subscribe(() => dismissed++); f.detectChanges();
    const labels = Array.from(el(f).querySelectorAll('button')).map(b => b.textContent!.trim()); expect(labels).toEqual(expect.arrayContaining(['Apply', 'Dismiss', 'Why this?']));
    (Array.from(el(f).querySelectorAll('button')).find(b => b.textContent!.includes('Apply')) as HTMLButtonElement).click(); (Array.from(el(f).querySelectorAll('button')).find(b => b.textContent!.includes('Dismiss')) as HTMLButtonElement).click();
    expect([accepted, dismissed]).toEqual([1, 1]); expect(el(f).textContent).toContain('AI');
  });
});

@Component({ standalone: true, imports: [RxTable], template: `<rx-table [rows]="rows" [columns]="cols" [pageSize]="2" [selectable]="true" (selectionChange)="sel = $event"/>` })
class Host { rows = [{ id: '1', name: 'Charu', n: 3 }, { id: '2', name: 'Aarav', n: 10 }, { id: '3', name: 'Bela', n: 2 }]; cols = [{ key: 'name', label: 'Name' }, { key: 'n', label: 'N', type: 'num' as const }]; sel: unknown[] = []; }

describe('rx-table', () => {
  it('sorts numerically and by text, announces sort state, pages and selects', () => {
    const f = TestBed.createComponent(Host); f.detectChanges(); const root = el(f);
    const names = () => Array.from(root.querySelectorAll('tbody tr td:nth-child(2)')).map(td => td.textContent!.trim());
    expect(names().length).toBe(2); // paged at 2
    (root.querySelector('th:nth-child(3) button') as HTMLButtonElement).click(); f.detectChanges();
    expect(root.querySelector('th:nth-child(3)')!.getAttribute('aria-sort')).toBe('ascending'); expect(names()).toEqual(['Bela', 'Charu']); // numeric: 2, 3, 10
    (root.querySelector('th:nth-child(3) button') as HTMLButtonElement).click(); f.detectChanges(); expect(names()).toEqual(['Aarav', 'Charu']);
    (root.querySelector('tbody input[type=checkbox]') as HTMLInputElement).click(); f.detectChanges(); expect((f.componentInstance.sel as unknown[]).length).toBe(1);
    expect(root.querySelector('.pager')!.textContent).toContain('1–2 of 3');
    (Array.from(root.querySelectorAll('.pager button')).find(b => b.textContent === 'Next') as HTMLButtonElement).click(); f.detectChanges(); expect(names()).toEqual(['Bela']);
  });
  it('shows an empty state instead of an empty table body', () => {
    const f = TestBed.createComponent(RxTable); f.componentInstance.columns = [{ key: 'a', label: 'A' }]; f.componentInstance.rows = []; f.componentInstance.emptyTitle = 'Nothing'; f.detectChanges(); expect(el(f).textContent).toContain('Nothing');
  });
});
