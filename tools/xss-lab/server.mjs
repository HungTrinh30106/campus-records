import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--port')) throw new Error('Use --port NUMBER only.');
const port = Number(args[1] || 5180);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Port must be 1024..65535.');
const html = await readFile(new URL('./index.html', import.meta.url));
const server = createServer((request, response) => {
  response.setHeader('X-Content-Type-Options', 'nosniff');
  response.setHeader('Cache-Control', 'no-store');
  response.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self'; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'");
  if (request.method === 'GET' && request.url === '/') { response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }); response.end(html); }
  else { response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' }); response.end('Not found'); }
});
server.on('error', error => { console.error(`XSS lab could not listen (${error.code}).`); process.exitCode = 1; });
server.listen(port, '127.0.0.1', () => console.log(`Standalone fictional-data XSS lab: http://127.0.0.1:${port}/`));
process.on('SIGINT', () => server.close());
