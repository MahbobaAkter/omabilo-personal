import type { ReactNode } from 'react';
import type { ApiError } from '../api/client';
import type { Certainty } from '../api/types';

export function Card({ title, subtitle, actions, children, className = '' }: {
  title?: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={`card ${className}`}>
      {(title || actions) && (
        <header className="card-head">
          <div>
            {title && <h2 className="card-title">{title}</h2>}
            {subtitle && <p className="muted small">{subtitle}</p>}
          </div>
          {actions && <div className="card-actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}

export function Stat({ label, value, hint, tone = 'default' }: { label: string; value: ReactNode; hint?: ReactNode; tone?: 'default' | 'good' | 'warn' | 'bad' }) {
  return (
    <div className={`stat stat-${tone}`}>
      <span className="stat-label">{label}</span>
      <span className="stat-value">{value}</span>
      {hint && <span className="stat-hint">{hint}</span>}
    </div>
  );
}

export type BadgeTone = 'neutral' | 'teal' | 'green' | 'amber' | 'red' | 'blue';

export function Badge({ tone = 'neutral', children }: { tone?: BadgeTone; children: ReactNode }) {
  return <span className={`badge badge-${tone}`}>{children}</span>;
}

const certaintyText: Record<Certainty, { label: string; tone: BadgeTone; help: string }> = {
  Confirmed: { label: 'Confirmed', tone: 'green', help: 'Recorded or confirmed by you' },
  Scheduled: { label: 'Scheduled', tone: 'blue', help: 'A fixed amount that has not happened yet' },
  Estimate: { label: 'Estimate', tone: 'amber', help: 'A typical amount; the real one may differ' },
  Planned: { label: 'Your plan', tone: 'teal', help: 'A figure you chose for planning' },
};

export function CertaintyBadge({ certainty }: { certainty: Certainty }) {
  const c = certaintyText[certainty];
  return (
    <span className={`badge badge-${c.tone}`} title={c.help}>
      {c.label}
    </span>
  );
}

export function DemoLabel({ children = 'Fictional demo data' }: { children?: ReactNode }) {
  return <span className="badge badge-demo" title="Made-up figures for trying the app. No real accounts are connected.">{children}</span>;
}

export function Loading({ label = 'Loading…', rows = 3 }: { label?: string; rows?: number }) {
  return (
    <div className="loading" role="status" aria-live="polite">
      <span className="sr-only">{label}</span>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="skeleton" style={{ width: `${90 - i * 15}%` }} />
      ))}
    </div>
  );
}

export function ErrorState({ error, onRetry }: { error: ApiError; onRetry?: () => void }) {
  const title = {
    config: 'The website is not connected to a server',
    offline: "Can't reach the server",
    timeout: 'The server is slow to answer',
    unauthorized: 'Not signed in',
    'not-found': 'Not found',
    validation: 'Please check the details',
    conflict: "That change couldn't be made",
    server: 'Something went wrong',
  }[error.kind];

  return (
    <div className="state state-error" role="alert">
      <strong>{title}</strong>
      <p>{error.message}</p>
      {error.kind === 'unauthorized' && <p className="small">Sign-in is set in Settings. Demo sign-in only works with a backend running in Development mode.</p>}
      {onRetry && error.kind !== 'config' && (
        <button className="btn btn-secondary" onClick={onRetry}>
          Try again
        </button>
      )}
    </div>
  );
}

export function EmptyState({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="state state-empty">
      <strong>{title}</strong>
      {children && <p>{children}</p>}
      {action}
    </div>
  );
}

/** The outcome of a change: green only when the server confirmed it. */
export function ActionNotice({ error, success, onClose }: { error: ApiError | null; success: string | null; onClose?: () => void }) {
  if (!error && !success) return null;
  return (
    <div className={`notice ${error ? 'notice-error' : 'notice-success'}`} role={error ? 'alert' : 'status'}>
      <span>{error ? error.message : success}</span>
      {onClose && (
        <button className="notice-close" onClick={onClose} aria-label="Dismiss message">
          ×
        </button>
      )}
    </div>
  );
}

export function Progress({ value, max, label }: { value: number; max: number; label: string }) {
  const pctValue = max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0;
  return (
    <div className="progress" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(pctValue)}>
      <div className="progress-fill" style={{ width: `${pctValue}%` }} />
    </div>
  );
}

/** Wraps a query's three outcomes so every screen handles loading, error and data the same way. */
export function QueryView<T>({ query, children, rows }: {
  query: { data: T | null; error: ApiError | null; loading: boolean; retry: () => void };
  children: (data: T) => ReactNode;
  rows?: number;
}) {
  if (query.error) return <ErrorState error={query.error} onRetry={query.retry} />;
  if (query.loading && query.data === null) return <Loading rows={rows} />;
  if (query.data === null) return null;
  return <>{children(query.data)}</>;
}
