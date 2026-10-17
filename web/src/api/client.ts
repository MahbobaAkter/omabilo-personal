// One place for every call to the Omabilo API: the address, sign-in header, timeouts and
// error messages. A request only "succeeds" when the server answered with a success status;
// failures are never replaced with made-up data.

export type ErrorKind = 'config' | 'offline' | 'timeout' | 'unauthorized' | 'not-found' | 'validation' | 'conflict' | 'server';

export class ApiError extends Error {
  constructor(
    public readonly kind: ErrorKind,
    message: string,
    public readonly status?: number,
    public readonly fieldErrors?: Record<string, string[]>,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export interface ApiClientOptions {
  baseUrl: string | null;
  configProblem: string | null;
  /** The fictional demo user to act as, or null when demo sign-in is off. */
  demoUser: () => string | null;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

export type ApiClient = ReturnType<typeof createApiClient>;

export function createApiClient(options: ApiClientOptions) {
  const fetchImpl = options.fetchImpl ?? ((...args: Parameters<typeof fetch>) => fetch(...args));
  const timeoutMs = options.timeoutMs ?? 15000;

  async function send(method: string, path: string, body?: unknown, anonymous = false): Promise<Response> {
    if (!options.baseUrl) {
      throw new ApiError('config', options.configProblem ?? 'The API address is not configured.');
    }

    const headers: Record<string, string> = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const demoUser = anonymous ? null : options.demoUser();
    if (demoUser) headers['X-Demo-User'] = demoUser;

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    try {
      return await fetchImpl(`${options.baseUrl}${path}`, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller.signal,
      });
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        throw new ApiError('timeout', 'The server took too long to answer. Please try again.');
      }
      throw new ApiError('offline', "Can't reach the Omabilo server. Check your connection; the server may also be down.");
    } finally {
      clearTimeout(timer);
    }
  }

  async function failure(res: Response): Promise<ApiError> {
    let payload: { error?: string; title?: string; errors?: Record<string, string[]> } = {};
    try {
      payload = await res.json();
    } catch {
      // Not JSON: fall back to the status below
    }

    const firstField = payload.errors ? Object.values(payload.errors)[0]?.[0] : undefined;

    switch (res.status) {
      case 400:
        return new ApiError('validation', firstField ?? payload.error ?? 'Some details need checking.', 400, payload.errors);
      case 401:
        return new ApiError('unauthorized', 'You are not signed in to the Omabilo server.', 401);
      case 403:
        return new ApiError('unauthorized', "You don't have access to that.", 403);
      case 404:
        return new ApiError('not-found', "That item couldn't be found. It may have been removed.", 404);
      case 409:
        return new ApiError('conflict', payload.error ?? 'That change conflicts with the current data.', 409);
      default:
        return new ApiError('server', 'Something went wrong on the server. Please try again in a moment.', res.status);
    }
  }

  async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await send(method, path, body);
    if (!res.ok) throw await failure(res);
    if (res.status === 204) return undefined as T;
    return (await res.json()) as T;
  }

  return {
    get: <T>(path: string) => request<T>('GET', path),
    post: <T>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
    put: <T>(path: string, body: unknown) => request<T>('PUT', path, body),
    patch: <T>(path: string, body: unknown) => request<T>('PATCH', path, body),
    del: <T>(path: string) => request<T>('DELETE', path),

    /** Downloads a file response (an export) as a Blob. */
    async download(path: string): Promise<Blob> {
      const res = await send('GET', path);
      if (!res.ok) throw await failure(res);
      return res.blob();
    },

    /** Checks the server without signing in. */
    async health(): Promise<{ status: string; storage: string; auth: string }> {
      const res = await send('GET', '/health', undefined, true);
      if (!res.ok) throw await failure(res);
      return res.json();
    },
  };
}
