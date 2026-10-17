import { useState } from 'react';
import { DEMO_USERS, useAction, useApi, useQuery } from '../api/context';
import type { FinanceSettings, SourceCatalogEntry } from '../api/types';
import { ActionNotice, Badge, Card, QueryView } from '../components/ui';
import { config } from '../config';

const availability = {
  Available: { label: 'Available', tone: 'green' as const },
  Simulated: { label: 'Simulated only', tone: 'blue' as const },
  NotConnected: { label: 'Not connected', tone: 'neutral' as const },
};

export default function Settings() {
  const { connection, connectionDetail, recheck, demoUser, setDemoUser } = useApi();
  const sources = useQuery(c => c.get<SourceCatalogEntry[]>('/api/invoice-sources'));
  const settings = useQuery(c => c.get<FinanceSettings>('/api/finance/settings'));
  const action = useAction();
  const [confirmReset, setConfirmReset] = useState(false);

  return (
    <>
      <ActionNotice error={action.error} success={action.success} onClose={action.clear} />
      <div className="grid grid-2">
        <Card title="Server connection" subtitle="Where this website gets its data">
          <dl className="kv">
            <div><dt>Status</dt><dd>{connection === 'online' ? 'Connected' : connection === 'checking' ? 'Checking…' : connection === 'offline' ? 'Unreachable' : 'Not configured'}</dd></div>
            <div><dt>API address</dt><dd className="small">{config.apiBaseUrl ?? 'Not set'}</dd></div>
          </dl>
          {connectionDetail && <p className="small muted">{connectionDetail}</p>}
          <div className="form-actions"><button className="btn btn-secondary btn-sm" onClick={recheck}>Check again</button></div>
        </Card>

        <Card title="Sign-in" subtitle={config.authMode === 'demo' ? 'Demo sign-in: fictional people, for trying the app' : 'Sign-in is not set up for this site yet'}>
          {config.authMode === 'demo' ? (
            <>
              <label className="field">
                Use the app as
                <select className="input" value={demoUser ?? ''} onChange={e => setDemoUser(e.target.value)}>
                  {DEMO_USERS.map(u => <option key={u.id} value={u.id}>{u.label}</option>)}
                </select>
              </label>
              <p className="small muted">Each demo person has separate data. Demo sign-in only works with a server running in Development mode; a public server needs a real sign-in provider.</p>
            </>
          ) : (
            <p className="small">The server needs a real sign-in provider before people can use this site. Nothing can be loaded until then.</p>
          )}
        </Card>
      </div>

      <Card title="Invoice sources" subtitle="How invoices can reach Omabilo. Only what's marked Available is real.">
        <QueryView query={sources}>
          {list => (
            <ul className="list">
              {list.map(s => (
                <li key={s.type} className="row">
                  <div className="row-main">
                    <span className="row-title">{s.name}</span>
                    <span className="row-meta">{s.status}</span>
                  </div>
                  <Badge tone={availability[s.availability].tone}>{availability[s.availability].label}</Badge>
                </li>
              ))}
            </ul>
          )}
        </QueryView>
      </Card>

      <Card title="Your finance details">
        <QueryView query={settings}>
          {s => <FinanceForm settings={s} />}
        </QueryView>
      </Card>

      <Card title="Demo data" subtitle="Start over with fresh fictional finances. Invoices are separate (delete them in the Financial Inbox).">
        {!confirmReset ? (
          <div className="form-actions"><button className="btn btn-danger" onClick={() => setConfirmReset(true)}>Reset finance data…</button></div>
        ) : (
          <div className="callout callout-warn">
            <p>This replaces your balance, transactions, loans, goals and settings with fresh demo data.</p>
            <div className="form-actions" style={{ marginTop: 10 }}>
              <button className="btn btn-danger" disabled={action.pending} onClick={async () => {
                const r = await action.run(c => c.post('/api/finance/reset-demo?confirm=reset-demo-data'), 'Finance data reset to fresh demo data.');
                if (r) setConfirmReset(false);
              }}>Reset now</button>
              <button className="btn btn-secondary" onClick={() => setConfirmReset(false)}>Cancel</button>
            </div>
          </div>
        )}
      </Card>
    </>
  );
}

function FinanceForm({ settings }: { settings: FinanceSettings }) {
  const [displayName, setName] = useState(settings.displayName);
  const [payDay, setPayDay] = useState(String(settings.payDay));
  const [netSalary, setSalary] = useState(String(settings.netSalary));
  const action = useAction();

  return (
    <form onSubmit={async e => {
      e.preventDefault();
      await action.run(c => c.put('/api/finance/settings', { displayName, payDay: Number(payDay), netSalary: Number(netSalary.replace(',', '.')) }), 'Details saved.');
    }}>
      <div className="form-grid">
        <label className="field">Name shown on the dashboard<input className="input" value={displayName} onChange={e => setName(e.target.value)} maxLength={40} /></label>
        <label className="field">Payday (day of month)
          <select className="input" value={payDay} onChange={e => setPayDay(e.target.value)}>
            {Array.from({ length: 28 }, (_, i) => i + 1).map(d => <option key={d} value={d}>{d}.</option>)}
          </select>
        </label>
        <label className="field">Net salary (€ a month)<input className="input" inputMode="decimal" value={netSalary} onChange={e => setSalary(e.target.value)} /></label>
      </div>
      <div style={{ marginTop: 12 }}><ActionNotice error={action.error} success={action.success} /></div>
      <div className="form-actions" style={{ marginTop: 12 }}>
        <button className="btn btn-primary" disabled={action.pending}>{action.pending ? 'Saving…' : 'Save'}</button>
      </div>
    </form>
  );
}
