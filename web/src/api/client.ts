
export type ErrorKind =
  | 'config'
  | 'offline'
  | 'timeout'
  | 'unauthorized'
  | 'not-found'
  | 'validation'
  | 'conflict'
  | 'restricted'
  | 'server';

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
  demoUser: () => string | null;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

export type ApiClient = ReturnType<typeof createApiClient>;

interface ApiErrorPayload {
  error?: string;
  message?: string;
  title?: string;
  errors?: Record<string, string[]>;
}

export function createApiClient(options: ApiClientOptions) {
  const fetchImpl =
    options.fetchImpl ??
    ((...args: Parameters<typeof fetch>) => fetch(...args));

  const timeoutMs = options.timeoutMs ?? 15000;

  async function send(
    method: string,
    path: string,
    body?: unknown,
    anonymous = false,
  ): Promise<Response> {
    if (!options.baseUrl) {
      throw new ApiError(
        'config',
        options.configProblem ??
          'The Omabilo API address is not configured.',
      );
    }

    const headers: Record<string, string> = {
      Accept: 'application/json',
    };

    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }

    const demoUser = anonymous ? null : options.demoUser();

    if (demoUser) {
      headers['X-Demo-User'] = demoUser;
    }

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    try {
      return await fetchImpl(
        `${options.baseUrl.replace(/\/+$/, '')}${path}`,
        {
          method,
          headers,
          body: body === undefined ? undefined : JSON.stringify(body),
          signal: controller.signal,
        },
      );
    } catch (error) {
      if (
        error instanceof DOMException &&
        error.name === 'AbortError'
      ) {
        throw new ApiError(
          'timeout',
          'The Omabilo server took too long to respond. Please try again.',
        );
      }

      throw new ApiError(
        'offline',
        'The browser could not reach the Omabilo API. Check the API URL, CORS settings, and server availability.',
      );
    } finally {
      clearTimeout(timer);
    }
  }

  async function failure(res: Response): Promise<ApiError> {
    let payload: ApiErrorPayload = {};

    try {
      payload = (await res.json()) as ApiErrorPayload;
    } catch {
      // Use the HTTP status if the response is not JSON.
    }

    const firstField = payload.errors
      ? Object.values(payload.errors)[0]?.[0]
      : undefined;

    if (
      res.status === 503 &&
      payload.error === 'staging_restricted'
    ) {
      return new ApiError(
        'restricted',
        'The API is online, but finance requests are disabled because authentication has not been configured. The server administrator must finish authentication setup.',
        503,
      );
    }

    switch (res.status) {
      case 400:
        return new ApiError(
          'validation',
          firstField ??
            payload.message ??
            payload.error ??
            'Please check the information you entered.',
          400,
          payload.errors,
        );

      case 401:
        return new ApiError(
          'unauthorized',
          'You need to sign in before using this feature.',
          401,
        );

      case 403:
        return new ApiError(
          'unauthorized',
          "You don't have permission to access this information.",
          403,
        );

      case 404:
        return new ApiError(
          'not-found',
          "The requested item or API endpoint couldn't be found.",
          404,
        );

      case 409:
        return new ApiError(
          'conflict',
          payload.message ??
            payload.error ??
            'This change conflicts with the current data.',
          409,
        );

      default:
        return new ApiError(
          'server',
          payload.message ??
            payload.title ??
            `The Omabilo API returned HTTP ${res.status}. Please try again later.`,
          res.status,
        );
    }
  }

  async function request<T>(
    method: string,
    path: string,
    body?: unknown,
  ): Promise<T> {
    const res = await send(method, path, body);

    if (!res.ok) {
      throw await failure(res);
    }

    if (res.status === 204) {
      return undefined as T;
    }

    return (await res.json()) as T;
  }

  return {
    get: <T>(path: string) => request<T>('GET', path),

    post: <T>(path: string, body?: unknown) =>
      request<T>('POST', path, body ?? {}),

    put: <T>(path: string, body: unknown) =>
      request<T>('PUT', path, body),

    patch: <T>(path: string, body: unknown) =>
      request<T>('PATCH', path, body),

    del: <T>(path: string) =>
      request<T>('DELETE', path),

    async download(path: string): Promise<Blob> {
      const res = await send('GET', path);

      if (!res.ok) {
        throw await failure(res);
      }

      return res.blob();
    },

    async health(): Promise<{
      status: string;
      storage: string;
      auth: string;
    }> {
      const res = await send(
        'GET',
        '/health',
        undefined,
        true,
      );

      if (!res.ok) {
        throw await failure(res);
      }

      return (await res.json()) as {
        status: string;
        storage: string;
        auth: string;
      };
    },
  };
}
