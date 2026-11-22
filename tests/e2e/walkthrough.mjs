// Walks through the Salus web app the way a new person would, failing on any broken step, and
// saves screenshots of each screen at phone, iPad and desktop sizes.
//
//   node walkthrough.mjs <base url> <screenshot folder>
//   node walkthrough.mjs http://127.0.0.1:5080 shots
import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';

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
const browser = await chromium.launch();
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
    if (m.type() === 'error' && !m.text().includes('401')) errors.push(m.text());
  });
}

const email = `walkthrough-${Date.now()}@example.com`;
const password = 'correct horse battery';

try {
  const ctx = await browser.newContext({ viewport: sizes.phone, ...context });
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
    await ctx.grantPermissions(['notifications'], { origin: base });
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

  await step('sign out, and a signed-out visit to Today goes to sign-in', async () => {
    await page.goto(`${base}/insights`);
    await page.getByRole('button', { name: 'Sign out' }).last().click();
    await page.waitForURL('**/signin');
    await page.goto(`${base}/today`);
    await page.waitForURL('**/signin');
  });

  await step('sign back in goes straight to Today', async () => {
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.waitForURL('**/today');
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
