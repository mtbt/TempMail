const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const net = require('node:net');
const assert = require('node:assert/strict');
const fs = require('node:fs');
async function smtp(address) {
  const socket = net.connect(Number(process.env.TEMPMAIL_TEST_SMTP_PORT), '127.0.0.1');
  let buffer = '', lines = [], waiting;
  socket.on('data', bytes => { buffer += bytes.toString(); let end; while ((end = buffer.indexOf('\r\n')) >= 0) { lines.push(buffer.slice(0, end)); buffer = buffer.slice(end + 2); } if (waiting) { waiting(); waiting = null; } });
  async function line() { while (!lines.length) await new Promise(resolve => waiting = resolve); return lines.shift(); }
  async function command(text, code) { socket.write(text + '\r\n'); assert.ok((await line()).startsWith(code)); }
  assert.ok((await line()).startsWith('220'));
  await command('HELO test.example', '250');
  return {
    async send(subject, contentType, body) {
      await command('MAIL FROM:<sender@example.com>', '250');
      await command(`RCPT TO:<${address}>`, '250');
      await command('DATA', '354');
      await command(`From: sender@example.com\r\nTo: ${address}\r\nSubject: ${subject}\r\nMIME-Version: 1.0\r\nContent-Type: ${contentType}\r\n\r\n${body}\r\n.`, '250');
    },
    async close() { await command('QUIT', '221'); socket.end(); }
  };
}
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage(); const errors = [], unexpectedHttp = []; let trackingRequests = 0;
    const expectedHttp = new Set(['404 /favicon.ico', '404 /api/mailboxes/current', '401 /api/admin/dashboard']);
    page.on('pageerror', error => { errors.push(error.message); console.error('PAGEERROR', error.message); });
    page.on('console', message => {
      if (message.type() !== 'error') return;
      const value = message.text();
      console.error('CONSOLE', value);
      // Chromium mirrors expected 404/401 fetches as generic console errors. HTTP status
      // validation below handles those precisely; retain all non-resource console errors
      // so Blazor circuit/WebSocket failures still fail the smoke test.
      if (!value.startsWith('Failed to load resource: the server responded with a status of')) errors.push('Console: ' + value);
    });
    page.on('response', response => {
      if (response.status() < 400) return;
      const entry = `${response.status()} ${new URL(response.url()).pathname}`;
      console.error('HTTP', entry);
      if (!expectedHttp.has(entry)) unexpectedHttp.push(entry);
    });
    page.on('request', req => { if (req.url().includes('tracking.invalid')) trackingRequests++; });
    page.on('dialog', dialog => { errors.push('Unexpected JS dialog'); dialog.dismiss(); });
    await page.goto(process.env.TEMPMAIL_TEST_URL);
    await page.getByRole('button', { name: 'Tạo email', exact: true }).waitFor().catch(async error => { console.error('PAGE', await page.locator('body').innerText()); throw error; });
    await page.locator('#local').fill('browser-test'); await page.getByRole('button', { name: 'Tạo email', exact: true }).click();
    await page.getByText('browser-test@mail.example.com', { exact: true }).waitFor();
    await page.getByText('Cập nhật trực tiếp', { exact: true }).waitFor();

    const mail = await smtp('browser-test@mail.example.com');
    await mail.send('Older message', 'text/plain; charset=utf-8', 'Old body');
    await page.getByText('Older message', { exact: true }).waitFor({ timeout: 10000 });
    await page.getByText('Older message', { exact: true }).click();
    await page.locator('section.card.mt-4 h2').filter({ hasText: 'Older message' }).waitFor();

    const largeBody = '<p>Safe email body</p><div>' + 'A'.repeat(12000) + '</div><script>alert(\'xss\')</script><img src="https://tracking.invalid/pixel">';
    await mail.send('Browser realtime proof', 'text/html; charset=utf-8', largeBody);
    await page.getByText('Browser realtime proof', { exact: true }).first().waitFor({ timeout: 10000 });
    await page.locator('section.card.mt-4 h2').filter({ hasText: 'Browser realtime proof' }).waitFor({ timeout: 10000 });
    const frame = page.frameLocator('iframe[title="Nội dung email đã lọc"]'); await frame.getByText('Safe email body').waitFor();
    assert.equal(await frame.locator('script').count(), 0); assert.equal(await frame.locator('img').count(), 0); assert.equal(trackingRequests, 0);
    await mail.close();
    assert.equal(await page.locator('iframe').getAttribute('sandbox'), '');
    fs.mkdirSync('artifacts', { recursive: true }); await page.screenshot({ path: 'artifacts/inbox-desktop.png', fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: 'artifacts/inbox-mobile.png', fullPage: true });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    const other = await browser.newContext({ ignoreHTTPSErrors: true });
    const list = await page.evaluate(() => tempMail.api('/api/messages')); const id = list.data[0].id;
    assert.equal((await other.request.get(`${process.env.TEMPMAIL_TEST_URL}/api/messages/${id}`)).status(), 404);
    assert.equal((await other.request.get(`${process.env.TEMPMAIL_TEST_URL}/api/messages/${id}/metadata`)).status(), 404);
    assert.equal((await other.request.get(`${process.env.TEMPMAIL_TEST_URL}/api/messages/${id}/text`)).status(), 404);
    await page.goto(process.env.TEMPMAIL_TEST_URL + '/admin');
    await page.locator('#admin-email').fill('admin@example.com'); await page.locator('#admin-password').fill(process.env.TEMPMAIL_TEST_ADMIN_PASSWORD);
    await page.getByRole('button', { name: 'Đăng nhập', exact: true }).click();
    await page.getByText('Domain nhận email', { exact: true }).waitFor().catch(async error => { console.error('ADMIN PAGE', await page.locator('body').innerText()); throw error; });
    await page.screenshot({ path: 'artifacts/admin-mobile.png', fullPage: true });
    assert.deepEqual(unexpectedHttp, []);
    assert.deepEqual(errors, []);
    console.log('PASS: Blazor create, SMTP -> DB -> SignalR -> auto-open newest message, sandbox HTML, mobile layout, anonymous isolation, Identity admin login.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exit(1); });
