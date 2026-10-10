import { expect, test } from '@playwright/test';
import { api, ready, signIn, axe } from './helpers';

test.describe('persona journeys', () => {
  test('PM → LM → HRBP → RH: shift tagging completes end to end', async ({ browser }) => {
    const page = await (await browser.newContext()).newPage();
    await signIn(page, 'priya');
    const pid = (await (await (await api('priya')).get('/api/projects')).json()).find((p: any) => p.code === 'P-RET-04').id;
    await page.goto('/projects/' + pid); await ready(page);
    await page.getByRole('button', { name: 'Tag as shift project' }).click();
    await expect(page.getByText(/Sent for approval/)).toBeVisible();
    for (const who of ['arjun', 'meera', 'karthik'] as const) {
      await signIn(page, who); await page.goto('/approvals'); await ready(page);
      await page.getByRole('link', { name: /Shift-tag Warehouse returns desk/ }).click(); await ready(page);
      await expect(page.getByRole('heading', { name: /Shift-tag Warehouse returns desk/ })).toBeVisible();
      await page.getByRole('button', { name: /^Approve/ }).click();
      await expect(page.getByText(/Approved\./)).toBeVisible();
    }
    await signIn(page, 'priya'); await page.goto('/projects/' + pid); await ready(page);
    await expect(page.locator('rx-status', { hasText: 'Active' }).first()).toBeVisible();
  });

  test('LM approves from a phone, and rejection requires a reason', async ({ browser }) => {
    const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true }); const page = await ctx.newPage();
    await signIn(page, 'arjun'); await page.goto('/approvals'); await ready(page);
    await page.getByRole('link', { name: /Tag 4 people to Store operations from 15 Aug/ }).click(); await ready(page);
    await expect(page.getByText('Risk assessment')).toBeVisible();
    const approve = page.getByRole('button', { name: /^Approve/ }); const box = await approve.boundingBox(); expect(box!.height).toBeGreaterThanOrEqual(36);
    await page.getByRole('button', { name: /Reject/ }).click();
    await page.getByRole('button', { name: 'Reject request' }).click();
    await expect(page.getByText('Enter a reason so the requester knows what to change.')).toBeVisible();
    await page.getByLabel(/Reason \(required\)/).fill('Start date predates the project plan');
    await page.getByRole('button', { name: 'Reject request' }).click();
    await expect(page.getByText(/Rejected\./)).toBeVisible();
  });

  test('roster: keyboard-only edit, focus returns, and drag is only an accelerator', async ({ page }) => {
    await signIn(page, 'priya'); await page.goto('/roster?project=' + (await (await (await api('priya')).get('/api/projects')).json()).find((p: any) => p.code === 'P-RET-02').id + '&month=2026-10'); await ready(page);
    const grid = page.getByRole('grid');
    await expect(grid).toHaveAttribute('aria-rowcount', /\d+/); await expect(grid).toHaveAttribute('aria-colcount', '32');
    // reach the grid by keyboard alone: the roving-tabindex cell is the single tab stop
    const first = page.locator('[role=gridcell][tabindex="0"]'); await first.focus();
    await page.keyboard.press('ArrowRight'); await page.keyboard.press('ArrowRight'); await page.keyboard.press('ArrowDown');
    const active = page.locator('[role=gridcell]:focus'); await expect(active).toHaveCount(1);
    const label = (await active.getAttribute('aria-label'))!;
    await page.keyboard.press('Space'); await expect(active).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Enter');
    const dlg = page.getByRole('dialog', { name: /Assign 1 cell/ }); await expect(dlg).toBeVisible();
    await expect(dlg.getByText(/No new rule conflicts|Checking the rules|would break|allowed with/)).toBeVisible();
    await page.keyboard.press('Escape'); await expect(dlg).toBeHidden();
    await expect(page.locator('[role=gridcell]:focus')).toHaveAttribute('aria-label', label); // focus is restored to the cell
    // choose "Weekly off" with the keyboard and confirm
    await page.keyboard.press('Enter'); await expect(dlg).toBeVisible();
    await dlg.getByRole('radio', { name: /OFF/ }).check(); await expect(dlg.getByText(/No new rule conflicts/)).toBeVisible();
    await dlg.getByRole('button', { name: /Confirm assignment/ }).click();
    await expect(page.getByText(/Saved 1 change/)).toBeVisible();
    await expect(page.locator('[role=gridcell]:focus')).toHaveAttribute('aria-label', /Weekly off/);
    // the same job through drag: it opens the same confirm dialog with the choice preselected
    const chip = page.locator('.legend .sw[draggable=true]').first(); const target = page.locator('[role=gridcell]').nth(40);
    await chip.dragTo(target); await expect(page.getByRole('dialog', { name: /Assign 1 cell/ })).toBeVisible(); await page.keyboard.press('Escape');
  });

  test('roster: a blocking change is explained and cannot be confirmed', async ({ page }) => {
    await signIn(page, 'priya'); const ret1 = (await (await (await api('priya')).get('/api/projects')).json()).find((p: any) => p.code === 'P-RET-01').id;
    await page.goto(`/roster?project=${ret1}&month=2026-10`); await ready(page);
    await expect(page.getByRole('heading', { name: /Conflicts/ })).toBeVisible();
    await expect(page.getByText('BR-02', { exact: false }).first()).toBeVisible(); // planted: 7 days in a row
    await page.getByRole('button', { name: 'Go to cell' }).first().click(); await expect(page.locator('[role=gridcell]:focus')).toHaveCount(1);
    await expect(page.getByRole('button', { name: /Publish roster/ })).toBeVisible(); await page.getByRole('button', { name: /Publish roster/ }).click();
    const dlg = page.getByRole('dialog', { name: 'Publish this roster?' }); await expect(dlg).toBeVisible(); await expect(dlg.getByText(/blocking conflict/)).toBeVisible();
    await expect(dlg.getByRole('button', { name: /Publish roster and notify/ })).toBeDisabled();
  });

  test('AI draft is only a suggestion: accepting creates a draft, never a published roster', async ({ page }) => {
    await signIn(page, 'priya'); const ret1 = (await (await (await api('priya')).get('/api/projects')).json()).find((p: any) => p.code === 'P-RET-01').id;
    await page.goto(`/roster?project=${ret1}&month=2026-11`); await ready(page);
    await expect(page.getByText('No roster for November 2026 yet')).toBeVisible();
    await page.getByRole('button', { name: 'Suggest a draft' }).first().click();
    await expect(page.getByText(/Draft roster for 2026-11/)).toBeVisible(); await expect(page.getByText('Confidence').first()).toBeVisible();
    await page.getByRole('button', { name: 'Why this?' }).first().click(); await expect(page.getByRole('dialog', { name: 'Why this suggestion' })).toBeVisible(); await page.keyboard.press('Escape');
    const before = await (await api('priya')).get(`/api/rosters?projectId=${ret1}&month=2026-11`); expect(await before.json()).toHaveLength(0); // suggesting persisted nothing
    await page.getByRole('button', { name: 'Accept as draft roster' }).click();
    await expect(page.getByText(/Draft roster saved\. It is not published/)).toBeVisible();
    const after = await (await (await api('priya')).get(`/api/rosters?projectId=${ret1}&month=2026-11`)).json(); expect(after[0].status).toBe('Draft'); expect(after[0].source).toBe('AiDraft');
    await expect(page.locator('.ai-cell').first()).toBeVisible();
  });

  test('allowance: PM can send a run for approval but cannot approve compensation; HRSS can', async ({ page }) => {
    const runs = await (await (await api('fathima')).get('/api/allowance-runs')).json(); const pending = runs.find((r: any) => r.status === 'PendingCompensation');
    await signIn(page, 'priya'); await page.goto('/allowance/' + pending.id); await ready(page);
    await expect(page.getByRole('button', { name: /Approve compensation/ })).toHaveCount(0);
    await expect(page.getByText(/Only HRSS can approve compensation|Waiting for HRSS/)).toBeVisible();
    await signIn(page, 'fathima'); await page.goto('/allowance/' + pending.id); await ready(page);
    await page.getByRole('button', { name: /Approve compensation/ }).click(); await page.getByRole('dialog').getByRole('button', { name: 'Approve compensation' }).click();
    await expect(page.getByText('Compensation approved.')).toBeVisible();
    await page.getByRole('button', { name: /See breakdown for/ }).first().click();
    await expect(page.getByRole('dialog').getByText(/allowance-eligible/).first()).toBeVisible();
  });

  test('HRSS: anomaly dismissal needs a reason, and the payroll checklist gates submission', async ({ page }) => {
    await signIn(page, 'fathima'); await page.goto('/anomalies'); await ready(page);
    await page.getByRole('button', { name: /Dismiss with reason/ }).first().click();
    const dlg = page.getByRole('dialog', { name: 'Dismiss this anomaly' });
    await dlg.getByRole('button', { name: 'Dismiss', exact: true }).click(); await expect(page.getByText('A reason is required to dismiss.')).toBeVisible();
    await page.getByLabel('Why is this normal?').fill('Verified with the roster desk'); await dlg.getByRole('button', { name: 'Dismiss', exact: true }).click(); await expect(page.getByText(/Dismissed\. Your reason was recorded/)).toBeVisible();
    await page.goto('/payroll'); await ready(page); await expect(page.getByText('Pre-submission checklist')).toBeVisible();
  });

  test('HRSS: movement shows a ripple before commit, and backdated needs approval first', async ({ page }) => {
    await signIn(page, 'fathima'); await page.goto('/movements'); await ready(page);
    await expect(page.getByRole('button', { name: /Commit…/ })).toBeDisabled();
    await expect(page.getByText('A backdated movement can be committed only after its approval completes.')).toBeVisible();
    await page.getByLabel('Find employee').fill('Isha'); await page.getByRole('button', { name: /Isha/ }).first().click();
    const to = page.getByLabel('Move to'); await to.selectOption({ label: 'P-RET-02 · Festive fulfilment' }); await page.getByLabel('Effective from').fill('2026-11-02'); await page.getByLabel('Reason').fill('Seasonal move');
    await page.getByRole('button', { name: /Preview the ripple/ }).click(); await expect(page.getByText(/downstream record group/)).toBeVisible(); await expect(page.getByRole('table', { name: /What changes/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Stage this movement/ })).toBeVisible();
  });

  test('delegation: ineligible people are explained, and a same-level peer works', async ({ page }) => {
    await signIn(page, 'arjun'); await page.goto('/delegation'); await ready(page);
    await expect(page.getByText(/different grade|same role|reports to you|your own grade/i).first()).toBeVisible();
    await page.getByLabel('Delegate to').selectOption({ label: 'Nikhil Rao · G5 · Retail Ops' });
    await expect(page.getByText(/Eligible: same role and grade/)).toBeVisible();
    await page.getByLabel('What to delegate').selectOption('ExceptionReview'); await page.getByLabel('From').fill('2026-10-20'); await page.getByLabel('To', { exact: true }).fill('2026-10-25'); await page.getByLabel('Reason').fill('Conference');
    await page.getByRole('button', { name: 'Create delegation' }).click(); await expect(page.getByText(/Delegation created/)).toBeVisible();
  });

  test('employee: roster, allowance explained, declaration and query', async ({ page }) => {
    await signIn(page, 'ravi'); await page.goto('/me'); await ready(page);
    await expect(page.getByRole('heading', { name: 'Recent changes to my roster' })).toBeVisible();
    await page.getByRole('tab', { name: 'My allowance explained' }).click(); await expect(page.getByText('Your allowance, explained')).toBeVisible();
    await page.locator('details summary').first().click(); await expect(page.getByText(/The rate in effect on that date/).first()).toBeVisible();
    await page.getByRole('button', { name: 'Raise a query' }).click(); await page.getByRole('button', { name: 'Send query' }).click(); await expect(page.getByText('Describe your question so we can look into it.')).toBeVisible();
    await page.getByLabel('What looks wrong?').fill('Why is 12 Sep ₹450?'); await page.getByRole('button', { name: 'Send query' }).click(); await expect(page.getByText('Query sent to HRSS.')).toBeVisible();
    await page.getByRole('tab', { name: /Declarations/ }).click(); await page.getByRole('button', { name: /Review and sign/ }).first().click(); await page.getByRole('button', { name: 'I confirm and sign' }).click(); await expect(page.getByText('Signed. Thank you.')).toBeVisible();
  });

  test('role guard: an employee is sent home with an explanation', async ({ page }) => {
    await signIn(page, 'ravi'); await page.goto('/foundation'); await ready(page);
    await expect(page).toHaveURL(/\/home$/); await expect(page.getByText(/role doesn’t have access/)).toBeVisible();
    await expect(page.getByRole('link', { name: 'Foundation data' })).toHaveCount(0);
  });
});

test.describe('WCAG 2.2 mechanics', () => {
  test('skip link is first and visible on focus, with a 2px focus ring', async ({ page }) => {
    await signIn(page, 'priya'); await page.goto('/home'); await ready(page); await page.reload(); await ready(page);
    await page.keyboard.press('Tab'); const skip = page.getByRole('link', { name: 'Skip to main content' }); await expect(skip).toBeFocused(); await expect(skip).toBeInViewport();
    await page.keyboard.press('Enter'); await expect(page.locator('main#main')).toBeFocused();
    await page.keyboard.press('Tab');
    const ring = await page.evaluate(() => { const s = getComputedStyle(document.activeElement!); return { w: s.outlineWidth, st: s.outlineStyle, off: s.outlineOffset }; });
    expect(ring.st).toBe('solid'); expect(parseFloat(ring.w)).toBeGreaterThanOrEqual(2); expect(parseFloat(ring.off)).toBeGreaterThanOrEqual(1);
  });

  for (const [persona, route] of [['priya', '/roster'], ['arjun', '/approvals'], ['fathima', '/anomalies'], ['priya', '/projects'], ['fathima', '/foundation']] as const) {
    test(`target size ≥ 24×24 on ${persona} ${route}`, async ({ page }) => {
      await signIn(page, persona); await page.goto(route); await ready(page);
      const small = await page.evaluate(() => {
        const out: string[] = [];
        for (const el of Array.from(document.querySelectorAll<HTMLElement>('button, select, input:not([type=checkbox]):not([type=radio]):not([type=file]), [role=tab], [role=gridcell], summary, a.btn'))) {
          const r = el.getBoundingClientRect(); const cs = getComputedStyle(el);
          if (r.width === 0 || r.height === 0 || cs.visibility === 'hidden' || el.closest('[inert],[hidden],.sr-only')) continue;
          if (r.width < 24 || r.height < 24) out.push(`${el.tagName.toLowerCase()}.${el.className} ${Math.round(r.width)}×${Math.round(r.height)} "${(el.textContent || el.getAttribute('aria-label') || '').trim().slice(0, 30)}"`);
        }
        return out;
      });
      expect(small).toEqual([]);
    });
  }

  test('reduced motion is respected', async ({ page }) => {
    await signIn(page, 'priya'); await page.goto('/home'); await ready(page);
    const d = await page.evaluate(() => getComputedStyle(document.querySelector('.aipanel')!).transitionDuration); expect(parseFloat(d)).toBeLessThan(0.01);
  });

  test('AI assistant panel is collapsed by default, toggles, and remembers per user', async ({ page }) => {
    await signIn(page, 'arjun'); await page.goto('/approvals'); await ready(page);
    const toggle = page.getByRole('button', { name: /AI assistant/ }); await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click(); await expect(page.getByRole('complementary', { name: 'AI assistant' })).toBeVisible(); await expect(page.getByText('Advisory only. Nothing here changes a record until you choose to apply it.')).toBeVisible();
    await page.reload(); await ready(page); await expect(page.getByRole('button', { name: /Hide assistant/ })).toHaveAttribute('aria-expanded', 'true');
    await axe(page, 'arjun approvals with AI panel open');
  });
});
