import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

// Each test sets its own configuration, so a developer's .env.local can't change the result
const mockConfig = vi.hoisted(() => ({ value: { apiBaseUrl: null as string | null, authMode: 'demo', problem: null as string | null } }));
vi.mock('../config', () => ({
  get config() {
    return mockConfig.value;
  },
}));

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.resetModules();
});

async function renderDashboard() {
  const { ApiProvider } = await import('../api/context');
  const { default: Dashboard } = await import('./Dashboard');
  return render(
    <MemoryRouter>
      <ApiProvider>
        <Dashboard />
      </ApiProvider>
    </MemoryRouter>,
  );
}

describe('Dashboard when the server is not usable', () => {
  it('explains a missing API address and shows no made-up figures', async () => {
    mockConfig.value = { apiBaseUrl: null, authMode: 'demo', problem: 'The API address (VITE_API_BASE_URL) is not set for this site.' };
    const { container } = await renderDashboard();

    expect(await screen.findByText(/not connected to a server/i)).toBeTruthy();
    expect(screen.queryByText(/Try again/)).toBeNull();
    expect(container.textContent).not.toMatch(/€/);
  });

  it('says the server is unreachable, offers a retry, and shows no figures', async () => {
    mockConfig.value = { apiBaseUrl: 'https://api.test', authMode: 'demo', problem: null };
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch'); }));
    const { container } = await renderDashboard();

    expect((await screen.findAllByText(/Can't reach the Omabilo server/i)).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
    expect(container.textContent).not.toMatch(/€/);
  });
});
