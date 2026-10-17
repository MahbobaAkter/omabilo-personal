import { describe, expect, it, vi } from 'vitest';
import { ApiError, createApiClient } from './client';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function client(fetchImpl: typeof fetch, extra: Partial<Parameters<typeof createApiClient>[0]> = {}) {
  return createApiClient({ baseUrl: 'https://api.test', configProblem: null, demoUser: () => 'demo-maija', fetchImpl, ...extra });
}

async function errorOf(p: Promise<unknown>): Promise<ApiError> {
  try {
    await p;
  } catch (e) {
    return e as ApiError;
  }
  throw new Error('Expected the request to fail');
}

describe('API client', () => {
  it('returns data only for a successful response, sending the demo user', async () => {
    const fetchImpl = vi.fn(async () => json(200, { ok: 1 }));
    const result = await client(fetchImpl).get<{ ok: number }>('/api/x');
    expect(result).toEqual({ ok: 1 });
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('https://api.test/api/x');
    expect((init.headers as Record<string, string>)['X-Demo-User']).toBe('demo-maija');
  });

  it('turns a network failure into an offline error, never fake data', async () => {
    const e = await errorOf(client(async () => { throw new TypeError('Failed to fetch'); }).get('/api/x'));
    expect(e.kind).toBe('offline');
    expect(e.message).toMatch(/Can't reach/);
  });

  it('times out a request that never answers', async () => {
    const hanging: typeof fetch = (_url, init) =>
      new Promise((_resolve, reject) => init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError'))));
    const e = await errorOf(client(hanging, { timeoutMs: 20 }).get('/api/x'));
    expect(e.kind).toBe('timeout');
  });

  it('maps server statuses to clear errors', async () => {
    expect((await errorOf(client(async () => json(401, {})).get('/x'))).kind).toBe('unauthorized');
    expect((await errorOf(client(async () => json(404, {})).get('/x'))).kind).toBe('not-found');
    expect((await errorOf(client(async () => json(409, { error: 'Withdraw first' })).get('/x'))).message).toBe('Withdraw first');
    expect((await errorOf(client(async () => json(500, {})).get('/x'))).kind).toBe('server');
    expect((await errorOf(client(async () => new Response('oops', { status: 502 })).get('/x'))).kind).toBe('server');
  });

  it('surfaces the first field error from a validation response', async () => {
    const e = await errorOf(client(async () => json(400, { errors: { amount: ['Enter an amount above zero.'] } })).post('/x', {}));
    expect(e.kind).toBe('validation');
    expect(e.message).toBe('Enter an amount above zero.');
    expect(e.fieldErrors?.amount).toBeDefined();
  });

  it('does not call the network when the API address is not configured', async () => {
    const fetchImpl = vi.fn();
    const e = await errorOf(createApiClient({ baseUrl: null, configProblem: 'Not set', demoUser: () => null, fetchImpl }).get('/x'));
    expect(e.kind).toBe('config');
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('calls the exact https API address and returns its data', async () => {
    const fetchImpl = vi.fn(async () => json(200, { balance: 812.4 }));
    const api = createApiClient({ baseUrl: 'https://omabilo-api.example.test', configProblem: null, demoUser: () => null, fetchImpl });

    expect(await api.get<{ balance: number }>('/api/finance/overview')).toEqual({ balance: 812.4 });
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('https://omabilo-api.example.test/api/finance/overview');
    expect((init.headers as Record<string, string>)['X-Demo-User']).toBeUndefined();
  });

  it('treats an empty 204 answer as success with no data', async () => {
    const api = client(async () => new Response(null, { status: 204 }));
    expect(await api.del('/api/finance/goals/g1')).toBeUndefined();
  });

  it('never treats a redirect as success', async () => {
    // A misconfigured proxy that redirects instead of serving the API
    const e = await errorOf(client(async () => new Response(null, { status: 307, headers: { Location: 'https://elsewhere.test/' } })).get('/api/x'));
    expect(e.kind).toBe('server');
  });

  it('sends a JSON body only when there is one', async () => {
    const fetchImpl = vi.fn(async () => json(200, {}));
    await client(fetchImpl).post('/api/x', { amount: 5 });
    await client(fetchImpl).get('/api/y');
    const [, post] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    const [, get] = fetchImpl.mock.calls[1] as unknown as [string, RequestInit];
    expect(post.body).toBe('{"amount":5}');
    expect((post.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(get.body).toBeUndefined();
  });

  it('checks health without a sign-in header', async () => {
    const fetchImpl = vi.fn(async () => json(200, { status: 'ok', storage: 'Sqlite', auth: 'Demo' }));
    await client(fetchImpl).health();
    const [, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect((init.headers as Record<string, string>)['X-Demo-User']).toBeUndefined();
  });
});
