import { APIRequestContext, Page, expect, request } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

export const PERSONAS = ['priya', 'arjun', 'meera', 'karthik', 'fathima', 'ravi'] as const;
export type Persona = (typeof PERSONAS)[number];

/** Signs in through the dev role switcher by seeding the session, exactly as the login page does. */
export async function signIn(page: Page, persona: Persona, theme: 'light' | 'dark' = 'light') {
  await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' });
  await page.goto('/login');
  await page.evaluate(k => sessionStorage.setItem('rx.persona', k), persona);
}

export async function api(persona: Persona): Promise<APIRequestContext> {
  return request.newContext({ baseURL: 'http://localhost:5199', extraHTTPHeaders: { 'X-Dev-Persona': persona } });
}

export async function axe(page: Page, label: string) {
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
  expect(bad.map(v => `${label}: ${v.id} (${v.impact}) ${v.nodes.slice(0, 2).map(n => n.target.join(' ')).join(' | ')}`), `${label} has serious or critical accessibility violations`).toEqual([]);
}

export async function ready(page: Page) {
  await page.waitForLoadState('networkidle');
  await expect(page.locator('main#main')).toBeVisible();
  await page.waitForTimeout(300);
}
