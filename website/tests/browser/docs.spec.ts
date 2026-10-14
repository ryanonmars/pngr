import { test, expect } from '@playwright/test';
import { mkdir } from 'node:fs/promises';

const captureDir = 'docs/validation';
test('docs home, full-text search, keyboard navigation and article layout', async ({ page }, testInfo) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('requestfailed', request => {
    if (request.failure()?.errorText !== 'net::ERR_ABORTED') errors.push(`${request.url()}: ${request.failure()?.errorText}`);
  });
  await mkdir(captureDir, { recursive: true });
  await page.goto('/docs/');
  await expect(page.getByRole('heading', { name: 'Pngr documentation', exact: true })).toBeVisible();
  await expect(page.locator('html')).toHaveClass(/dark/);
  await expect(page.getByRole('img', { name: 'Pngr', exact: true }).first()).toBeVisible();
  await expect(page.getByRole('img', { name: /monitoring multiple destinations/ })).toBeVisible();
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `${captureDir}/docs-home-${testInfo.project.name}.png`, fullPage: true });

  await page.waitForLoadState('networkidle');
  expect(errors).toEqual([]);
  if (testInfo.project.name === 'mobile') await page.getByRole('banner').first().getByRole('button', { name: 'Open Search', exact: true }).click();
  else await page.keyboard.press('Control+k');
  const search = page.getByRole('dialog');
  await expect(search).toBeVisible();
  for (const [query, result] of [['Probing profiles', 'Probing profiles'], ['Secrets and testing', 'Secrets and testing'], ['Transmission', 'Recorded transmission classification']]) {
    await search.getByRole('combobox').fill(query);
    await expect(search.getByRole('option').filter({ hasText: result }).first()).toBeVisible();
  }
  await expect(search.getByRole('option').filter({ hasText: 'CSV export' }).first()).toBeVisible();
  await page.screenshot({ path: `${captureDir}/search-${testInfo.project.name}.png` });
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(search).not.toBeVisible();
  await expect(page).toHaveURL(/\/docs\//);

  await page.goto('/docs/network-monitoring/probe-configuration/');
  await expect(page.locator('h1')).toBeVisible();
  await expect(page.locator('table').first()).toBeVisible();
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `${captureDir}/probe-configuration-${testInfo.project.name}.png`, fullPage: true });

  await page.goto('/docs/advanced/local-api/');
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.getByRole('button', { name: 'Copy Text', exact: true }).first().click();
  await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toContain('curl');
  await expect(page.locator('pre').first()).toBeVisible();
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `${captureDir}/local-api-${testInfo.project.name}.png`, fullPage: true });
  await page.goto('/docs/advanced/alert-actions/');
  await page.getByRole('button', { name: 'Does Pngr retry a failed delivery?' }).click();
  await expect(page.getByText('There is no delivery retry setting.', { exact: false })).toBeVisible();
  expect(errors).toEqual([]);
});

test('sidebar and section navigation', async ({ page }, testInfo) => {
  await page.goto('/docs/getting-started/quick-start/');
  await page.waitForLoadState('networkidle');
  if (testInfo.project.name === 'mobile') await page.getByRole('button', { name: /open sidebar/i }).click();
  const sidebar = page.getByRole('complementary');
  await expect(sidebar).toBeVisible();
  await expect(sidebar.getByRole('link', { name: 'Quick start', exact: true })).toHaveAttribute('data-active', 'true');
  const section = sidebar.getByRole('button', { name: 'Getting Started', exact: true });
  await section.click();
  await expect(sidebar.getByRole('link', { name: 'Quick start', exact: true })).not.toBeVisible();
  await section.click();
  await sidebar.getByRole('link', { name: /macOS installation/i }).click();
  await expect(page).toHaveURL(/macos-installation/);
  await expect(page.locator('h1')).toContainText('macOS');
});

test('existing landing page navigation and screenshot dialog', async ({ page }, testInfo) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'See your network clearly.' })).toBeVisible();
  await expect(page.locator('.hero-copy')).toHaveCSS('opacity', '1');
  await page.screenshot({ path: `${captureDir}/landing-${testInfo.project.name}.png` });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.getByRole('button', { name: 'Enlarge Pngr live network diagnostics screenshot' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Close screenshot' }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible();
  await page.getByRole('link', { name: 'Documentation', exact: true }).click();
  await expect(page.locator('h1')).toHaveText('Pngr documentation');
  await page.getByRole('link', { name: 'Pngr', exact: true }).first().click();
  await expect(page.getByRole('heading', { name: 'See your network clearly.' })).toBeVisible();
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(16, 18, 20)');
});


test('narrow phones preserve branding and avoid horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 740 });
  for (const path of ['/', '/docs/', '/docs/advanced/local-api/']) {
    await page.goto(path);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    if (path === '/') await expect(page.locator('.header .nav-logo')).toHaveCSS('width', '78px');
  }
});
