// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, login, logout, resetCsrf, send } from './api';
import { ErrorBox } from './components/ui';
import { cleanup, render, screen } from '@testing-library/react';
import React from 'react';

beforeEach(resetCsrf);
afterEach(() => { vi.unstubAllGlobals(); cleanup(); });
describe('API client CSRF lifecycle', () => {
  it('uses a fresh authenticated token after login, instead of reusing the anonymous token', async () => {
    let signedIn = false;
    vi.stubGlobal('fetch', vi.fn(async (url: string, options?: RequestInit) => {
      if (url.endsWith('/csrf')) return Response.json({ token: signedIn ? 'user-token-B' : 'anonymous-token-A' });
      const expected = signedIn ? 'user-token-B' : 'anonymous-token-A';
      if (new Headers(options?.headers).get('X-CSRF-TOKEN') !== expected || options?.credentials !== 'same-origin') return Response.json({ title: 'CSRF rejected' }, { status: 403 });
      if (url.endsWith('/login')) { signedIn = true; return Response.json({ id: 'user', fullName: 'Test', email: 'test@example.org', role: 'Student' }); }
      return new Response(null, { status: 204 });
    }));
    await login('test@example.org', 'FictionalPassword!2026');
    await expect(api('/students/507f1f77bcf86cd799439011', send('PUT', { address: 'New address' }))).resolves.toBeUndefined();
  });
  it('clears the authenticated CSRF token after logout so another account can log in', async () => {
    let signedIn = false;
    vi.stubGlobal('fetch', vi.fn(async (url: string, options?: RequestInit) => {
      if (url.endsWith('/csrf')) return Response.json({ token: signedIn ? 'user-B' : 'anonymous-A' });
      if (new Headers(options?.headers).get('X-CSRF-TOKEN') !== (signedIn ? 'user-B' : 'anonymous-A')) return Response.json({ title: 'CSRF rejected' }, { status: 403 });
      if (url.endsWith('/login')) { signedIn = true; return Response.json({ id: 'user', role: 'Student' }); }
      if (url.endsWith('/logout')) signedIn = false;
      return new Response(null, { status: 204 });
    }));
    await login('one@example.org', 'FictionalPassword!2026'); await logout();
    await expect(login('two@example.org', 'FictionalPassword!2026')).resolves.toMatchObject({ id: 'user' });
  });
  it('notifies the application when the server revokes an authenticated session', async () => {
    const expired = vi.fn(); window.addEventListener('session-expired', expired);
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ title: 'Login required' }, { status: 401 })));
    try { await expect(api('/students')).rejects.toMatchObject({ status: 401 }); expect(expired).toHaveBeenCalledTimes(1); }
    finally { window.removeEventListener('session-expired', expired); }
  });
  it('sanitizes a fetch transport error instead of exposing its connection details', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('Connection failure: private-password at internal-host'); }));
    try { await api('/students'); throw new Error('Expected rejection'); } catch (error) { expect(error).toBeInstanceOf(ApiError); expect((error as Error).message).not.toContain('private-password'); expect((error as Error).message).not.toContain('internal-host'); }
  });
});
describe('server error rendering', () => {
  it('renders HTML-shaped error text as text rather than executable markup', () => {
    const text = '<img src=x onerror="alert(1)">';
    const { container } = render(React.createElement(ErrorBox, { error: new ApiError(text, 400) }));
    expect(screen.getByRole('alert').textContent).toContain(text); expect(container.querySelector('img,script')).toBeNull();
  });
});
