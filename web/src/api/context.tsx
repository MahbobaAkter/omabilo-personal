import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { config } from '../config';
import { ApiError, createApiClient, type ApiClient } from './client';

export type Connection = 'checking' | 'online' | 'offline' | 'misconfigured';

/** Fictional demo users. These ids only work with a backend running in Development. */
export const DEMO_USERS = [
  { id: 'demo-maija', label: 'Maija (demo)' },
  { id: 'demo-ville', label: 'Ville (demo)' },
];

interface ApiState {
  client: ApiClient;
  connection: Connection;
  connectionDetail: string | null;
  recheck: () => void;
  demoUser: string | null;
  setDemoUser: (id: string) => void;
  /** Bumped after a change so every screen reloads its data. */
  version: number;
  changed: () => void;
}

const Ctx = createContext<ApiState | null>(null);
const STORAGE_KEY = 'omabilo.demoUser';

function storedDemoUser(): string {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved && DEMO_USERS.some(u => u.id === saved)) return saved;
  } catch {
    // Storage blocked (private mode): use the default
  }
  return DEMO_USERS[0].id;
}

export function ApiProvider({ children }: { children: ReactNode }) {
  const [demoUser, setDemoUserState] = useState<string | null>(config.authMode === 'demo' ? storedDemoUser() : null);
  const demoRef = useRef(demoUser);
  demoRef.current = demoUser;

  const client = useMemo(
    () => createApiClient({ baseUrl: config.apiBaseUrl, configProblem: config.problem, demoUser: () => demoRef.current }),
    [],
  );

  const [connection, setConnection] = useState<Connection>(config.problem ? 'misconfigured' : 'checking');
  const [connectionDetail, setConnectionDetail] = useState<string | null>(config.problem);
  const [version, setVersion] = useState(0);

  const recheck = useCallback(() => {
    if (config.problem) return;
    if (typeof navigator !== 'undefined' && navigator.onLine === false) {
      setConnection('offline');
      setConnectionDetail('This device is offline.');
      return;
    }
    client.health().then(
      h => {
        setConnection('online');
        setConnectionDetail(`Server OK · ${h.storage} storage · ${h.auth} sign-in`);
      },
      (e: unknown) => {
        setConnection('offline');
        setConnectionDetail(e instanceof ApiError ? e.message : 'The server did not answer.');
      },
    );
  }, [client]);

  useEffect(() => {
    recheck();
    const timer = setInterval(recheck, 30000);
    window.addEventListener('online', recheck);
    window.addEventListener('offline', recheck);
    return () => {
      clearInterval(timer);
      window.removeEventListener('online', recheck);
      window.removeEventListener('offline', recheck);
    };
  }, [recheck]);

  const setDemoUser = useCallback((id: string) => {
    try {
      localStorage.setItem(STORAGE_KEY, id);
    } catch {
      // Not remembered across visits; still works for this one
    }
    setDemoUserState(id);
    setVersion(v => v + 1);
  }, []);

  const changed = useCallback(() => setVersion(v => v + 1), []);

  return (
    <Ctx.Provider value={{ client, connection, connectionDetail, recheck, demoUser, setDemoUser, version, changed }}>
      {children}
    </Ctx.Provider>
  );
}

export function useApi(): ApiState {
  const v = useContext(Ctx);
  if (!v) throw new Error('useApi must be used inside ApiProvider');
  return v;
}

/** Loads data for a screen. Reloads when the user changes, after any change, or on retry. */
export function useQuery<T>(load: (c: ApiClient) => Promise<T>, deps: unknown[] = []) {
  const { client, version } = useApi();
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    load(client).then(
      d => {
        if (!cancelled) {
          setData(d);
          setLoading(false);
        }
      },
      (e: unknown) => {
        if (!cancelled) {
          // Keep no stale data from before: an error screen, not old numbers that look current
          setData(null);
          setError(e instanceof ApiError ? e : new ApiError('server', 'Something unexpected went wrong.'));
          setLoading(false);
        }
      },
    );
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, version, attempt, ...deps]);

  return { data, error, loading, retry: () => setAttempt(a => a + 1) };
}

/** Runs a change (a POST, PUT...) and reports success only when the server confirmed it. */
export function useAction() {
  const { client, changed } = useApi();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  const run = useCallback(
    async <T,>(action: (c: ApiClient) => Promise<T>, successMessage?: string | ((r: T) => string)): Promise<T | null> => {
      setPending(true);
      setError(null);
      setSuccess(null);
      try {
        const result = await action(client);
        if (successMessage) setSuccess(typeof successMessage === 'function' ? successMessage(result) : successMessage);
        changed();
        return result;
      } catch (e) {
        setError(e instanceof ApiError ? e : new ApiError('server', 'Something unexpected went wrong.'));
        return null;
      } finally {
        setPending(false);
      }
    },
    [client, changed],
  );

  return { run, pending, error, success, clear: () => { setError(null); setSuccess(null); } };
}
