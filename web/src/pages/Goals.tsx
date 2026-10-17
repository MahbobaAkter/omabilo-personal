import { useState, type FormEvent } from 'react';
import { useAction, useQuery } from '../api/context';
import type { FinanceSettings, Goal } from '../api/types';
import { ActionNotice, Badge, Card, DemoLabel, EmptyState, Progress, QueryView, Stat } from '../components/ui';
import { dateFi, money, money0 } from '../format';

function monthlyNeeded(g: Goal): number | null {
  if (!g.targetDate || g.saved >= g.target) return null;
  const now = new Date();
  const target = new Date(g.targetDate);
  const months = Math.max(1, (target.getUTCFullYear() - now.getUTCFullYear()) * 12 + target.getUTCMonth() - now.getUTCMonth());
  return Math.ceil((g.target - g.saved) / months);
}

export default function Goals() {
  const goals = useQuery(c => c.get<Goal[]>('/api/finance/goals'));
  const settings = useQuery(c => c.get<FinanceSettings>('/api/finance/settings'));
  const action = useAction();
  const [creating, setCreating] = useState(false);
  const [moving, setMoving] = useState<{ id: string; direction: 1 | -1 } | null>(null);
  const [amount, setAmount] = useState('');

  return (
    <>
      <div className="page-head">
        <p>Give your savings a purpose. Adding money moves it out of your current account, so the payday plan stays accurate.</p>
        <div className="form-actions">
          {settings.data?.isDemoData && <DemoLabel />}
          <button className="btn btn-primary" onClick={() => setCreating(c => !c)} aria-expanded={creating}>{creating ? 'Close' : 'New goal'}</button>
        </div>
      </div>
      <ActionNotice error={action.error} success={action.success} onClose={action.clear} />

      {creating && <Card title="New goal"><GoalForm onDone={() => setCreating(false)} /></Card>}

      <QueryView query={goals} rows={4}>
        {list => list.length === 0 ? (
          <EmptyState title="No goals yet">An emergency fund covering three months of essentials is a good first goal.</EmptyState>
        ) : (
          <>
            <div className="grid grid-3">
              <Stat label="Saved in total" value={money0(list.reduce((s, g) => s + g.saved, 0))} hint={`of ${money0(list.reduce((s, g) => s + g.target, 0))}`} />
              <Stat label="Goals reached" value={`${list.filter(g => g.saved >= g.target).length} / ${list.length}`} />
              <Stat label="Needed per month" value={money0(list.reduce((s, g) => s + (monthlyNeeded(g) ?? 0), 0))} hint="To reach dated goals on time" />
            </div>
            <div className="grid grid-3">
              {list.map(g => {
                const need = monthlyNeeded(g);
                const done = g.saved >= g.target;
                return (
                  <Card key={g.id} title={g.name} subtitle={g.targetDate ? `Target ${money0(g.target)} by ${dateFi(g.targetDate)}` : `Target ${money0(g.target)}`}
                    actions={<>{g.isEmergencyFund && <Badge tone="blue">Safety net</Badge>}{done && <Badge tone="green">Reached</Badge>}</>}>
                    <p className="num"><strong style={{ fontSize: '1.35rem' }}>{money(g.saved)}</strong> <span className="muted">{Math.round((g.saved / g.target) * 100)} %</span></p>
                    <Progress value={g.saved} max={g.target} label={`${g.name} progress`} />
                    {need !== null && <p className="small muted">About {money0(need)} a month gets you there on time.</p>}
                    {moving?.id === g.id ? (
                      <form className="form-actions" onSubmit={async e => {
                        e.preventDefault();
                        const value = Number(amount.replace(',', '.')) * moving.direction;
                        const r = await action.run(c => c.post(`/api/finance/goals/${g.id}/contributions`, { amount: value }), moving.direction > 0 ? `Added to ${g.name}.` : `Withdrew from ${g.name}.`);
                        if (r) { setMoving(null); setAmount(''); }
                      }}>
                        <label className="field" style={{ flex: 1 }}>
                          <span className="sr-only">{moving.direction > 0 ? 'Amount to add' : 'Amount to withdraw'} in euros</span>
                          <input className="input" inputMode="decimal" value={amount} onChange={e => setAmount(e.target.value)} placeholder="50" autoFocus />
                        </label>
                        <button className="btn btn-primary btn-sm" disabled={action.pending}>{moving.direction > 0 ? 'Add' : 'Withdraw'}</button>
                        <button type="button" className="btn btn-secondary btn-sm" onClick={() => setMoving(null)}>Cancel</button>
                      </form>
                    ) : (
                      <div className="form-actions">
                        <button className="btn btn-secondary btn-sm" disabled={done} onClick={() => setMoving({ id: g.id, direction: 1 })}>Add money</button>
                        <button className="btn btn-ghost btn-sm" disabled={g.saved <= 0} onClick={() => setMoving({ id: g.id, direction: -1 })}>Withdraw</button>
                        <button className="btn btn-ghost btn-sm" onClick={() => action.run(c => c.del(`/api/finance/goals/${g.id}`), `Removed ${g.name}.`)}
                          title={g.saved > 0 ? 'Withdraw the savings first' : undefined}>Remove</button>
                      </div>
                    )}
                  </Card>
                );
              })}
            </div>
          </>
        )}
      </QueryView>
    </>
  );
}

function GoalForm({ onDone }: { onDone: () => void }) {
  const [name, setName] = useState('');
  const [target, setTarget] = useState('');
  const [date, setDate] = useState('');
  const action = useAction();

  async function submit(e: FormEvent) {
    e.preventDefault();
    const r = await action.run(c => c.post('/api/finance/goals', { name, target: Number(target.replace(',', '.')), targetDate: date || null }), 'Goal created.');
    if (r) onDone();
  }

  return (
    <form onSubmit={submit} noValidate>
      <div className="form-grid">
        <label className="field">Name<input className="input" value={name} onChange={e => setName(e.target.value)} maxLength={60} placeholder="e.g. Weekend in Tallinn" /></label>
        <label className="field">Target (€)<input className="input" inputMode="decimal" value={target} onChange={e => setTarget(e.target.value)} placeholder="400" /></label>
        <label className="field">Target date (optional)<input className="input" type="date" value={date} onChange={e => setDate(e.target.value)} /></label>
      </div>
      <div style={{ marginTop: 12 }}><ActionNotice error={action.error} success={null} /></div>
      <div className="form-actions" style={{ marginTop: 12 }}>
        <button className="btn btn-primary" disabled={action.pending}>{action.pending ? 'Saving…' : 'Create goal'}</button>
        <button type="button" className="btn btn-secondary" onClick={onDone}>Cancel</button>
      </div>
    </form>
  );
}
