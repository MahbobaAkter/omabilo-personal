import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { useApi, type Connection } from '../api/context';

export const NAV = [
  { to: '/', label: 'Dashboard', icon: '◧' },
  { to: '/inbox', label: 'Financial Inbox', icon: '✉' },
  { to: '/debts', label: 'Debt Tracker', icon: '↘' },
  { to: '/payday', label: 'Payday Planner', icon: '◷' },
  { to: '/goals', label: 'Savings Goals', icon: '◎' },
  { to: '/reports', label: 'Reports', icon: '▤' },
  { to: '/settings', label: 'Settings & Connections', icon: '⚙' },
];

const connectionText: Record<Connection, string> = {
  checking: 'Checking server…',
  online: 'Server connected',
  offline: 'Server unreachable',
  misconfigured: 'Server not configured',
};

export function ConnectionPill() {
  const { connection, connectionDetail, recheck } = useApi();
  return (
    <button className={`conn conn-${connection}`} onClick={recheck} title={`${connectionDetail ?? ''} (tap to check again)`} aria-live="polite">
      <span className="conn-dot" aria-hidden="true" />
      <span>{connectionText[connection]}</span>
    </button>
  );
}

function Brand() {
  return (
    <div className="brand">
      <div className="brand-mark" aria-hidden="true">
        <svg viewBox="0 0 32 32" width="22" height="22">
          <path d="M9 21c0-7 5-12 13-12-1 7-5 12-13 12z" fill="currentColor" />
        </svg>
      </div>
      <div>
        <div className="brand-name">Omabilo Personal</div>
        <div className="brand-tag">Your money. Your way.</div>
      </div>
    </div>
  );
}

export function Layout({ children }: { children: ReactNode }) {
  const [menuOpen, setMenuOpen] = useState(false);
  const location = useLocation();
  const { connection, connectionDetail } = useApi();
  const current = NAV.find(n => (n.to === '/' ? location.pathname === '/' : location.pathname.startsWith(n.to)));

  useEffect(() => setMenuOpen(false), [location.pathname]);

  const links = (
    <nav aria-label="Main">
      <ul className="nav-list">
        {NAV.map(n => (
          <li key={n.to}>
            <NavLink to={n.to} end={n.to === '/'} className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
              <span className="nav-icon" aria-hidden="true">{n.icon}</span>
              {n.label}
            </NavLink>
          </li>
        ))}
      </ul>
    </nav>
  );

  return (
    <div className="shell">
      <a className="skip" href="#main">Skip to content</a>
      <aside className="sidebar">
        <Brand />
        {links}
        <div className="sidebar-foot">
          <ConnectionPill />
          <p className="small muted">All figures are fictional demo data. No bank or mailbox is connected.</p>
        </div>
      </aside>

      <div className="content">
        <header className="topbar">
          <button className="menu-btn" aria-expanded={menuOpen} aria-controls="mobile-menu" onClick={() => setMenuOpen(o => !o)}>
            <span aria-hidden="true">☰</span>
            <span className="sr-only">Menu</span>
          </button>
          <div className="topbar-title">
            <span className="topbar-brand">Omabilo</span>
            <h1>{current?.label ?? 'Omabilo'}</h1>
          </div>
          <ConnectionPill />
        </header>

        {menuOpen && (
          <div id="mobile-menu" className="mobile-menu">
            <Brand />
            {links}
          </div>
        )}

        {connection === 'misconfigured' && (
          <div className="banner banner-error" role="alert">
            <strong>This website isn't connected to a server.</strong> {connectionDetail}
          </div>
        )}
        {connection === 'offline' && (
          <div className="banner banner-warn" role="status">
            <strong>Can't reach the server.</strong> Nothing shown here is being updated, and changes won't be saved until it's back. {connectionDetail}
          </div>
        )}

        <main id="main" className="main" tabIndex={-1}>
          {children}
        </main>
      </div>
    </div>
  );
}
