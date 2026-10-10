import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { toApiError } from './api.service';
import { Session } from './auth.service';
import { AiPanel, Toasts } from './ui.services';

describe('toApiError', () => {
  it('explains a network failure in plain language', () => expect(toApiError(new HttpErrorResponse({ status: 0 })).title).toMatch(/can’t reach the server/));
  it('uses the server title and extra payload', () => {
    const e = toApiError(new HttpErrorResponse({ status: 400, error: { title: 'Fix the blocking conflicts.', extra: { violations: [1] } } }));
    expect(e).toEqual({ status: 400, title: 'Fix the blocking conflicts.', extra: { violations: [1] } });
  });
  it('falls back to a status message', () => expect(toApiError(new HttpErrorResponse({ status: 500 })).title).toContain('500'));
});

describe('Session', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideRouter([])] }));
  it('answers role and policy questions from /api/me', () => {
    const s = TestBed.inject(Session);
    s.me.set({ roles: ['PM', 'LM'], policies: ['CanBuildRoster'] });
    expect(s.has('PM')).toBe(true); expect(s.has('HRSS')).toBe(false); expect(s.has('HRSS', 'LM')).toBe(true); expect(s.has()).toBe(true);
    expect(s.hasPolicy('CanBuildRoster')).toBe(true); expect(s.hasPolicy('CanApproveCompensation')).toBe(false);
  });
  it('is signed out until a persona or token exists', () => { sessionStorage.clear(); expect(TestBed.inject(Session).isSignedIn()).toBe(false); });
});

describe('AiPanel', () => {
  beforeEach(() => { localStorage.clear(); TestBed.configureTestingModule({}); });
  it('is collapsed by default and remembers its state per user', () => {
    const p = TestBed.inject(AiPanel); p.setUser('u1'); expect(p.open()).toBe(false);
    p.toggle(); expect(p.open()).toBe(true); p.setUser('u2'); expect(p.open()).toBe(false); p.setUser('u1'); expect(p.open()).toBe(true);
  });
  it('lets pages publish and clear their own suggestions', () => {
    const p = TestBed.inject(AiPanel); const item = { key: 'a', kind: 'suggestion' as const, title: 't', summary: 's', confidence: 0.7 };
    p.publish('page1', [item]); p.publish('page2', [{ ...item, key: 'b' }]); expect(p.items().length).toBe(2);
    p.clear('page1'); expect(p.items().map(i => i.key)).toEqual(['b']);
  });
});

describe('Toasts', () => {
  beforeEach(() => TestBed.configureTestingModule({}));
  it('keeps errors until dismissed and auto-clears the rest', () => {
    jest.useFakeTimers(); const t = TestBed.inject(Toasts);
    t.success('Saved'); t.error('Failed'); expect(t.items().length).toBe(2);
    jest.advanceTimersByTime(7000); expect(t.items().map(i => i.kind)).toEqual(['error']);
    t.dismiss(t.items()[0].id); expect(t.items().length).toBe(0); jest.useRealTimers();
  });
});
