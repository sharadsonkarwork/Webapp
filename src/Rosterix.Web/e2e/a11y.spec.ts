import { expect, test } from '@playwright/test';
import { PERSONAS, Persona, api, axe, ready, signIn } from './helpers';

// Routes and the roles that may open them (mirrors app.routes.ts).
const ROUTES: [string, string[]][] = [
  ['/home', ['*']], ['/projects', ['priya', 'arjun', 'meera', 'karthik', 'fathima']], ['/projects/new', ['priya']], ['/tagging', ['priya', 'arjun', 'meera', 'fathima']], ['/tagging/new', ['priya', 'arjun']],
  ['/approvals', ['priya', 'arjun', 'meera', 'karthik', 'fathima']], ['/roster', ['priya', 'arjun', 'meera', 'karthik', 'fathima']], ['/allowance', ['priya', 'arjun', 'meera', 'fathima']],
  ['/exceptions', ['priya', 'arjun', 'meera', 'fathima']], ['/anomalies', ['priya', 'arjun', 'meera', 'fathima']], ['/reports', ['*']], ['/payroll', ['meera', 'karthik', 'fathima']],
  ['/movements', ['fathima']], ['/delegation', ['priya', 'arjun', 'fathima']], ['/arrears', ['priya', 'arjun', 'meera', 'karthik', 'fathima']], ['/settlement', ['fathima']],
  ['/me', ['*']], ['/foundation', ['fathima']], ['/notifications', ['*']], ['/audit', ['fathima']],
];

for (const persona of PERSONAS) {
  test.describe(`axe: ${persona}`, () => {
    for (const [route, roles] of ROUTES) {
      if (!roles.includes('*') && !roles.includes(persona)) continue;
      test(`${route} has no serious or critical WCAG 2.2 AA violations`, async ({ page }) => {
        await signIn(page, persona);
        await page.goto(route);
        await ready(page);
        await axe(page, `${persona} ${route}`);
      });
    }
    test('detail routes: project, allowance run, approval request', async ({ page }) => {
      const a = await api(persona); const ids: string[] = [];
      const p = await a.get('/api/projects'); if (p.ok()) { const j = await p.json(); if (j[0]) ids.push('/projects/' + j[0].id); }
      const r = await a.get('/api/allowance-runs'); if (r.ok()) { const j = await r.json(); if (j[0]) ids.push('/allowance/' + j[0].id); }
      const q = await a.get('/api/approvals/pending'); if (q.ok()) { const j = await q.json(); if (j.items[0]) ids.push('/approvals/' + j.items[0].id); }
      await signIn(page, persona);
      for (const route of ids) { await page.goto(route); await ready(page); await axe(page, `${persona} ${route}`); }
    });
  });
}

test.describe('axe: dark theme and phone width', () => {
  for (const [persona, route] of [['priya', '/roster'], ['arjun', '/approvals'], ['fathima', '/anomalies'], ['ravi', '/me'], ['priya', '/home']] as [Persona, string][]) {
    test(`dark: ${persona} ${route}`, async ({ page }) => { await signIn(page, persona, 'dark'); await page.goto(route); await ready(page); await axe(page, `dark ${persona} ${route}`); });
    test(`390px: ${persona} ${route}`, async ({ page }) => { await page.setViewportSize({ width: 390, height: 844 }); await signIn(page, persona); await page.goto(route); await ready(page); await axe(page, `390 ${persona} ${route}`);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth); expect(overflow, 'no horizontal page scroll').toBeLessThanOrEqual(1); });
  }
  test('login page', async ({ page }) => { await page.goto('/login'); await ready(page); await axe(page, 'login'); });
});
