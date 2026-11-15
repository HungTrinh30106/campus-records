import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { request } from 'node:http';
import { fileURLToPath } from 'node:url';

test('standalone XSS lab serves its demonstration only and blocks arbitrary files/network connections', async () => {
  const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
  const port = reservation.address().port; await new Promise(resolve => reservation.close(resolve));
  const child = spawn(process.execPath, [fileURLToPath(new URL('./server.mjs', import.meta.url)), '--port', String(port)], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  try {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Lab did not become ready')), 8000);
      child.once('exit', code => { clearTimeout(timer); reject(new Error(`Lab exited before readiness (${code})`)); });
      child.stdout.once('data', () => { clearTimeout(timer); resolve(); });
    });
    const response = await fetch(`http://127.0.0.1:${port}/`);
    assert.equal(response.status, 200); assert.match(response.headers.get('content-type'), /^text\/html/);
    assert.match(response.headers.get('content-security-policy'), /connect-src 'none'/);
    assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
    assert.equal((await fetch(`http://127.0.0.1:${port}/backend/appsettings.json`)).status, 404);
    assert.equal((await fetch(`http://127.0.0.1:${port}/%2e%2e/README.md`)).status, 404);
    assert.equal((await fetch(`http://127.0.0.1:${port}/missing-image`)).status, 404);
    const malformedStatus = await new Promise((resolve, reject) => {
      const req = request({ hostname: '127.0.0.1', port, path: '//[', method: 'GET' }, res => { res.resume(); resolve(res.statusCode); });
      req.on('error', reject); req.end();
    });
    assert.equal(malformedStatus, 404);
    assert.equal((await fetch(`http://127.0.0.1:${port}/`)).status, 200);
  } finally { if (child.exitCode === null) { child.kill(); await once(child, 'exit').catch(() => {}); } }
});
