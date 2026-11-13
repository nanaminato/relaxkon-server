const assert = require('node:assert/strict');
const { chromium } = require('playwright');

async function verifyAdministratorLogin() {
  const browser = await chromium.launch({ channel: 'chrome' });
  try {
    const origin = process.env.SMOKE_ORIGIN;
    const page = await browser.newPage();
    page.setDefaultTimeout(20_000);

    const signInPage = await page.goto(origin + '/accounts/sign-in');
    assert.equal(signInPage.status(), 200, 'The sign-in page must load');
    await page.locator('#login-username').fill('admin');
    await page.locator('#login-password').fill(process.env.SMOKE_PASSWORD);
    const [loginResponse] = await Promise.all([
      page.waitForResponse(response =>
        response.url() === origin + '/api/auth/login' && response.request().method() === 'POST'),
      page.locator('form button[type=submit]').click(),
    ]);
    assert.equal(loginResponse.status(), 200, 'The administrator sign-in request must succeed');

    await page.waitForURL(url => url.pathname !== '/accounts/sign-in');
    await page.goto(origin + '/admin/accounts/account-management');
    const account = page.getByRole('cell', { name: 'admin@example.com', exact: true });
    await account.waitFor({ state: 'visible' });
    console.log('Chromium signed in and displayed the administrator account in account management.');
  } finally {
    await browser.close();
  }
}

verifyAdministratorLogin().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
