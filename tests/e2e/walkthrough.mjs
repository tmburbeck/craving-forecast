// Walks through the Salus web app the way a new person would, failing on any broken step, and
// saves screenshots of each screen at phone, iPad and desktop sizes.
//
//   node walkthrough.mjs <base url> <screenshot folder>
//   node walkthrough.mjs http://127.0.0.1:5080 shots
import { chromium } from 'playwright';
import { mkdir, readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';

const [base = 'http://127.0.0.1:5080', out = 'shots'] = process.argv.slice(2);
const sizes = {
  phone: { width: 390, height: 844 },
  ipad: { width: 820, height: 1180 },
  desktop: { width: 1440, height: 900 },
};
// Screenshots are taken at 2 PM in Chicago on today's date, so they stay comparable from run to run
// (the server decides what "today" is, so the date itself must stay real).
const zone = 'America/Chicago';
const context = { timezoneId: zone, locale: 'en-US' };
const today = new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
const offset = new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'longOffset' })
  .formatToParts(new Date()).find(p => p.type === 'timeZoneName').value.replace('GMT', '') || '+00:00';
const clock = new Date(`${today}T14:00:00${offset}`);

await mkdir(out, { recursive: true });
// Full Chromium (not the lighter headless shell, which always denies notifications), still headless.
const browser = await chromium.launch({ channel: 'chromium' });
const errors = [];
let failed = false;

async function step(name, fn) {
  const started = Date.now();
  try {
    await fn();
    console.log(`ok    ${name} (${Date.now() - started} ms)`);
  } catch (e) {
    failed = true;
    console.log(`FAIL  ${name}\n      ${String(e.message).split('\n')[0]}`);
    throw e;
  }
}

function watch(page) {
  page.on('pageerror', e => errors.push(String(e)));
  page.on('console', m => {
    // The app asks "is anyone signed in?" before sign-up; the browser logs that expected 401.
    // The delete step tries a wrong password first on purpose; the browser logs that expected 400.
    const expected400 = m.text().includes('400') && (m.location()?.url ?? '').includes('/api/me/delete');
    if (m.type() === 'error' && !m.text().includes('401') && !expected400) errors.push(m.text());
  });
}

const email = `walkthrough-${Date.now()}@example.com`;
let password = 'correct horse battery';
// Where the server writes emails instead of sending them (its Email:PickupDirectory). Unset: the reset step is skipped.
const mailDir = process.env.MAIL_DIR;

try {
  // Headless Chromium reports notifications as blocked unless they're granted up front, and the app
  // (correctly) hides its offer for blocked browsers, so grant them as a person saying yes would.
  const ctx = await browser.newContext({ viewport: sizes.phone, ...context, permissions: ['notifications'] });
  const page = await ctx.newPage();
  await page.clock.setFixedTime(clock);
  watch(page);

  await step('first visit shows the welcome slides', async () => {
    await page.goto(`${base}/`);
    await page.waitForURL('**/welcome');
    await page.getByRole('heading', { name: 'Know your cravings before they arrive' }).waitFor();
    await page.screenshot({ path: `${out}/welcome-phone.png` });
  });

  await step('create an account', async () => {
    await page.getByRole('link', { name: 'Skip' }).click();
    await page.waitForURL('**/signup');
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(password);
    await page.screenshot({ path: `${out}/signup-phone.png` });
    await page.getByRole('button', { name: 'Create account' }).click();
    await page.waitForURL('**/quiz');
  });

  await step('take the quiz', async () => {
    await page.getByRole('heading', { name: 'When do you usually wake up?' }).waitFor();
    await page.screenshot({ path: `${out}/quiz-phone.png` });
    const pick = label => page.getByRole('button', { name: label, exact: true }).click();
    for (const answer of ['7 AM', '11 PM', '9–5, on-site', 'Dinner', 'Boredom', 'Breakfast']) await pick(answer);
    await page.getByRole('heading', { name: 'When do cravings usually get you?' }).waitFor();
    await pick('Afternoon energy slump');
    await pick('Watching TV / scrolling');
    await page.getByRole('button', { name: 'Next' }).click();
    await pick('Sweet');
    await pick('A mix of both');
    await page.getByRole('button', { name: 'See my forecast' }).click();
    await page.waitForURL('**/today');
    await page.getByRole('heading', { level: 1 }).waitFor();
  });

  await step('answer "How did you sleep?"', async () => {
    await page.getByRole('button', { name: 'Okay', exact: true }).click();
    await page.getByText('Thanks. Forecast updated.').waitFor();
  });

  await step('check in', async () => {
    await page.getByRole('link', { name: 'Check in' }).first().click();
    await page.waitForURL('**/checkin');
    await page.getByRole('button', { name: 'Level 7' }).click();
    await page.getByRole('button', { name: 'Stressed after work' }).click();
    await page.screenshot({ path: `${out}/checkin-phone.png` });
    await page.getByRole('button', { name: 'Log it' }).click();
    await page.waitForURL('**/today');
    await page.getByText('Logged. Your forecast just updated.').waitFor();
  });

  await step('notifications are offered, and the worker shows a pushed nudge', async () => {
    await page.getByRole('button', { name: 'Turn on notifications' }).waitFor();
    // Deliver a push straight to the service worker, the way a push service would, and check it's shown.
    const cdp = await ctx.newCDPSession(page);
    const registered = new Promise(resolve => cdp.on('ServiceWorker.workerRegistrationUpdated', e => {
      const r = e.registrations.find(x => x.scopeURL.startsWith(base) && !x.isDeleted);
      if (r) resolve(r.registrationId);
    }));
    await cdp.send('ServiceWorker.enable');
    await page.evaluate(() => navigator.serviceWorker.ready);
    const registrationId = await registered;
    await cdp.send('ServiceWorker.deliverPushMessage', {
      origin: new URL(base).origin, registrationId,
      data: JSON.stringify({ title: 'Afternoon slump in 30 minutes', body: 'Greek yogurt with berries now.', url: '/today', tag: 'walkthrough' })
    });
    await page.waitForFunction(async () =>
      (await (await navigator.serviceWorker.ready).getNotifications()).some(n => n.title === 'Afternoon slump in 30 minutes'), null, { timeout: 10000 });
    await cdp.detach();
  });

  await step('Today and Insights at phone, iPad and desktop sizes', async () => {
    const state = await ctx.storageState();
    for (const [size, viewport] of Object.entries(sizes)) {
      const c = await browser.newContext({ viewport, storageState: state, ...context });
      const p = await c.newPage();
      await p.clock.setFixedTime(clock);
      watch(p);
      for (const route of ['today', 'insights']) {
        await p.goto(`${base}/${route}`);
        await p.getByRole('heading', { level: 1 }).waitFor();
        await p.screenshot({ path: `${out}/${route}-${size}.png` });
      }
      await c.close();
    }
  });

  await step('Help & FAQ opens from Insights and answers expand', async () => {
    await page.goto(`${base}/insights`);
    await page.getByRole('link', { name: 'Help & FAQ' }).last().click();
    await page.waitForURL('**/help');
    await page.getByRole('heading', { name: 'Help & FAQ' }).waitFor();
    const answer = page.getByText('Apple only allows them once Salus is on your Home Screen');
    if (await answer.isVisible()) throw new Error('answers should start closed');
    await page.getByText('How do I get notifications on an iPhone or iPad?').click();
    await answer.waitFor();
    await page.screenshot({ path: `${out}/help-phone.png`, fullPage: true });
  });

  await step('sign out, and a signed-out visit to Today goes to sign-in', async () => {
    await page.goto(`${base}/insights`);
    await page.getByRole('button', { name: 'Sign out' }).last().click();
    await page.waitForURL('**/signin');
    await page.goto(`${base}/today`);
    await page.waitForURL('**/signin');
  });

  await step('switch between sign-in and create-account', async () => {
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.getByRole('button', { name: 'New here? Create an account' }).click();
    await page.getByRole('heading', { name: 'Create your account' }).waitFor();
    await page.waitForURL('**/signup');
    await page.getByRole('button', { name: 'Already have an account? Sign in' }).click();
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.waitForURL('**/signin');
  });

  await step('a wrong password is refused with a clear message', async () => {
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill('not the right password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.getByText("That email and password don't match.").waitFor();
  });

  await step('forgot password: the emailed link sets a new password', async () => {
    if (!mailDir) { console.log('      (skipped: MAIL_DIR not set)'); return; }
    await page.getByRole('link', { name: 'Forgot your password?' }).click();
    await page.waitForURL('**/forgot');
    await page.getByLabel('Email').fill(email);
    await page.getByRole('button', { name: 'Send reset link' }).click();
    await page.getByRole('heading', { name: 'Check your email' }).waitFor();

    let link;
    for (const f of (await readdir(mailDir)).filter(f => f.endsWith('.json')).sort().reverse()) {
      const mail = JSON.parse(await readFile(join(mailDir, f), 'utf8'));
      if (mail.To === email) { link = mail.Text.split('\n').map(s => s.trim()).find(s => s.includes('/reset-password?')); break; }
    }
    if (!link) throw new Error('no password-reset email was written');
    const target = new URL(link); // the link names the real site; open the same page on the site under test
    await page.goto(`${base}${target.pathname}${target.search}`);
    await page.getByRole('heading', { name: 'Choose a new password' }).waitFor();
    password = 'a brand new passphrase';
    await page.getByLabel('New password').fill(password);
    await page.getByRole('button', { name: 'Save new password' }).click();
    await page.getByRole('heading', { name: 'Password changed' }).waitFor();
    await page.getByRole('link', { name: 'Sign in' }).click();
    await page.waitForURL('**/signin');
  });

  await step('sign back in goes straight to Today', async () => {
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.waitForURL('**/today');
  });

  await step('delete my account: needs the password, then erases it for good', async () => {
    await page.goto(`${base}/insights`);
    await page.getByRole('link', { name: 'Delete my account' }).click();
    await page.waitForURL('**/delete-account');
    await page.getByRole('heading', { name: 'Delete your account?' }).waitFor();
    await page.getByLabel('Enter your password to confirm').fill('not my password');
    await page.getByRole('button', { name: 'Delete my account' }).click();
    await page.getByText("That password isn't right.").waitFor();
    await page.screenshot({ path: join(out, 'delete-phone.png'), fullPage: true });
    await page.getByLabel('Enter your password to confirm').fill(password);
    await page.getByRole('button', { name: 'Delete my account' }).click();
    await page.getByRole('heading', { name: 'Your account is deleted' }).waitFor();

    await page.goto(`${base}/today`);
    await page.waitForURL('**/signin');
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.getByText("That email and password don't match.").waitFor();
  });

  if (errors.length) {
    failed = true;
    console.log(`FAIL  errors in the browser:\n      ${errors.join('\n      ')}`);
  }
} catch {
  // already reported by step()
} finally {
  await browser.close();
}

process.exit(failed ? 1 : 0);
