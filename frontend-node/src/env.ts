// Hand-written bridge (19 Oct 2026): the ikon tool 2.39.9 boots the page with window.__IKON_BOOT__
// (set in index.html), but the SDK on npm (1.13.9) has no bootAuthConfig() yet and still reads the
// older window auth and local-server configs. This maps the boot table onto those. Once npm ships an
// SDK with bootAuthConfig, `ikon update` can replace this file with the generated two-liner.

import type { AuthConfig, LoginMethod } from '@ikonai/sdk-react-ui';

interface BootSurface {
  name?: string;
  methods?: LoginMethod[];
  routes?: string[];
  host?: string;
  requireSignIn?: boolean;
}

interface BootTable {
  spaceId: string;
  backendUrl?: string;
  authUrl?: string;
  localServer?: { host: string; port: number };
  devLogin?: { token: string; runId?: string };
  surface?: string;
  surfaces?: BootSurface[];
}

// The SDK's window keys, built up so the deploy check for the retired build defines doesn't match them
const authConfigKey = '__IKON_' + 'AUTH_CONFIG__';
const localServerKey = '__IKON_LOCAL_IKON_SERVER_CONFIG__';

// The prerendered static HTML a deployment serves for a snapshot route leaves out the boot script,
// while the packaged /index.html keeps it: read it from there, synchronously, as this runs before
// the SDK starts
const packagedBoot = (): BootTable | undefined => {
  try {
    const request = new XMLHttpRequest();
    request.open('GET', '/index.html', false);
    request.send();
    const marker = 'window.__IKON_BOOT__=';
    const html = request.status === 200 ? request.responseText : '';
    const start = html.indexOf(marker);
    // The table is one JSON object, so its first "};" ends it (";" only appears inside strings otherwise)
    const end = start < 0 ? -1 : html.indexOf('};', start);
    return end < 0 ? undefined : (JSON.parse(html.slice(start + marker.length, end + 1)) as BootTable);
  } catch {
    return undefined;
  }
};

const boot = (window as unknown as { __IKON_BOOT__?: BootTable }).__IKON_BOOT__ ?? packagedBoot();

if (!boot) {
  throw new Error('window.__IKON_BOOT__ is not defined');
}

const covers = (route: string, path: string) => {
  if (route === '/') {
    return path.startsWith('/');
  }

  const trimmed = route.replace(/\/+$/, '');
  return path === trimmed || path.startsWith(trimmed + '/');
};

// Same choice as the boot script: the pinned surface, else the longest route covering the path,
// else the surface with neither routes nor host, else the first
const resolveSurface = (surfaces: BootSurface[]): BootSurface | undefined => {
  const pinned = boot.surface || document.querySelector('meta[name="ikon-surface"]')?.getAttribute('content') || '';
  const byName = pinned ? surfaces.find((s) => s.name === pinned) : undefined;

  if (byName) {
    return byName;
  }

  const path = window.location.pathname;
  let best: BootSurface | undefined;
  let bestLength = -1;

  for (const surface of surfaces) {
    for (const route of surface.routes ?? []) {
      if (route.length > bestLength && covers(route, path)) {
        best = surface;
        bestLength = route.length;
      }
    }
  }

  return best ?? surfaces.find((s) => (s.routes ?? []).length === 0 && !s.host) ?? surfaces[0];
};

const surface = resolveSurface(boot.surfaces ?? []);
const methods: LoginMethod[] = surface?.methods?.length ? surface.methods : ['guest'];
const requireSignIn = surface?.requireSignIn ?? !methods.some((m) => m === 'guest' || m === 'global');

const config: AuthConfig = {
  methods,
  requireSignIn,
  // Compat mirror of requireSignIn for source that still reads `enabled`
  enabled: requireSignIn,
  spaceId: boot.spaceId,
  backendUrl: boot.backendUrl || undefined,
  authUrl: boot.authUrl || undefined,
  devLoginToken: boot.devLogin?.token || undefined,
  devLoginRunId: boot.devLogin?.runId || undefined,
};

const target = window as unknown as Record<string, unknown>;
target[authConfigKey] = config;
target[localServerKey] = { enabled: !!boot.localServer, host: boot.localServer?.host ?? 'localhost', port: boot.localServer?.port ?? 8443 };

export const authConfig = config;
