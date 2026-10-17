// Reads the website's settings from VITE_ environment variables at build time.
// These values are public (they end up in the JavaScript every visitor downloads), so they
// only ever hold the API address and the sign-in mode: never secrets.

export type AuthMode = 'demo' | 'none';

export interface AppConfig {
  apiBaseUrl: string | null;
  authMode: AuthMode;
  /** Why the site can't talk to the API, shown to the person instead of failing silently. */
  problem: string | null;
}

interface EnvLike {
  VITE_API_BASE_URL?: string;
  VITE_AUTH_MODE?: string;
  PROD?: boolean;
}

export function readConfig(env: EnvLike = import.meta.env as EnvLike): AppConfig {
  const authMode: AuthMode = env.VITE_AUTH_MODE === 'none' ? 'none' : 'demo';
  const raw = env.VITE_API_BASE_URL?.trim();

  if (!raw) {
    return { apiBaseUrl: null, authMode, problem: 'The API address (VITE_API_BASE_URL) is not set for this site.' };
  }

  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return { apiBaseUrl: null, authMode, problem: `The API address "${raw}" is not a valid web address.` };
  }

  const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  if (env.PROD && local) {
    return { apiBaseUrl: null, authMode, problem: 'This published site points to a localhost API address, which visitors cannot reach.' };
  }

  if (env.PROD && url.protocol !== 'https:') {
    return { apiBaseUrl: null, authMode, problem: 'The API address must use https:// on a published site.' };
  }

  return { apiBaseUrl: raw.replace(/\/+$/, ''), authMode, problem: null };
}

export const config = readConfig();
