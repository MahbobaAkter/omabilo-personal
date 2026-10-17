import { useState } from 'react';
import { useAction, useQuery } from '../api/context';
import type { ExpectedIncome, FinanceSettings, PaydayPlan, PlanLine } from '../api/types';
import { ActionNotice, Badge, Card, CertaintyBadge, DemoLabel, QueryView, Stat } from '../components/ui';
import { dateFi, dayFi, isoDay, money } from '../format';

const GROUPS: { key: PlanLine['group']; label: string }[] = [
  { key: 'income', label: 'Money coming in' },
  { key: 'bills', label: 'Bills' },
  { key: 'loans', label: 'Loan payments' },
  { key: 'savings', label: 'Savings' },
  { key: 'discretionary', label: 'Everyday spending' },
];

export default function Payday() {
  const [until, setUntil] = useState<string>('');
  const plan = useQuery(c => c.get<PaydayPlan>(`/api/finance/payday-plan${until ? `?until=${until}` : ''}`), [until]);
  const income = useQuery(c => c.get<ExpectedIncome[]>('/api/finance/expected-income'));
  const settings = useQuery(c => c.get<FinanceSettings>('/api/finance/settings'));
  const action = useAction();
  const [allowance, setAllowance] = useState<string | null>(null);

  return (
    <>
      <div className="page-head">
        <p>What's left until payday, worked out step by step. Every figure says how certain it is.</p>
        {settings.data?.isDemoData && <DemoLabel />}
      </div>
      <ActionNotice error={action.error} success={action.success} onClose={action.clear} />

      <QueryView query={plan} rows={6}>
        {p => (
          <>
            <div className="grid grid-3">
              <div className="stat stat-hero">
                <span className="stat-label">Safe to spend per day</span>
                <span className="stat-value">{money(p.safeToSpendPerDay)}</span>
                <span className="stat-hint">For {p.days} days, {dayFi(p.today)} to {dayFi(p.planUntil)}, before everyday spending</span>
              </div>
              <Stat label={`Projected balance on ${dayFi(p.planUntil)}`} value={money(p.projectedBalance)} tone={p.projectedBalance < 0 ? 'bad' : 'good'} hint="After everything below, including your everyday spending plan" />
              <Stat label="Next payday" value={dateFi(p.nextPayday)} hint="Salary isn't counted until it arrives" />
            </div>

            {p.warnings.map(w => <div key={w} className="notice notice-error" role="alert">{w}</div>)}

            <Card title="How it's worked out" actions={
              <label className="field" style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
                <span className="small" style={{ fontWeight: 600 }}>Plan until</span>
                <input className="input" type="date" style={{ width: 170 }} value={until || isoDay(p.planUntil)}
                  min={isoDay(p.today)} max={isoDay(new Date(new Date(p.nextPayday).getTime() - 864e5).toISOString())}
                  onChange={e => setUntil(e.target.value)} aria-label="Plan until date" />
              </label>
            }>
              <div className="table-wrap">
                <table className="table">
                  <tbody>
                    <tr>
                      <td><strong>Current account today</strong><div className="small muted">From recorded transactions</div></td>
                      <td><CertaintyBadge certainty="Confirmed" /></td>
                      <td className="amount"><strong>{money(p.startingBalance)}</strong></td>
                    </tr>
                    {GROUPS.map(g => {
                      const lines = p.lines.filter(l => l.group === g.key);
                      if (lines.length === 0) return null;
                      return lines.map((l, i) => (
                        <tr key={`${g.key}-${i}`}>
                          <td>
                            {i === 0 && <div className="small muted" style={{ fontWeight: 600 }}>{g.label}</div>}
                            {l.label}
                            <div className="small muted">{l.date ? dayFi(l.date) : `Every day until ${dayFi(p.planUntil)}`}{l.note ? ` · ${l.note}` : ''}</div>
                          </td>
                          <td><CertaintyBadge certainty={l.certainty} /></td>
                          <td className={`amount ${l.amount > 0 ? 'pos' : ''}`}>{l.amount > 0 ? '+' : ''}{money(l.amount)}</td>
                        </tr>
                      ));
                    })}
                    <tr>
                      <td><strong>Projected balance on {dayFi(p.planUntil)}</strong></td>
                      <td />
                      <td className="amount"><strong>{money(p.projectedBalance)}</strong></td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </Card>

            {p.excluded.length > 0 && (
              <Card title="Not counted" subtitle="Shown so nothing is hidden: these are left out to avoid double counting or guessing.">
                <ul className="list">
                  {p.excluded.map((x, i) => (
                    <li key={i} className="row">
                      <div className="row-main">
                        <span className="row-title">{x.label}</span>
                        <span className="row-meta">{x.date ? `${dayFi(x.date)} · ` : ''}{x.reason}</span>
                      </div>
                      <span className="row-amount muted">{money(Math.abs(x.amount))}</span>
                    </li>
                  ))}
                </ul>
              </Card>
            )}
          </>
        )}
      </QueryView>

      <div className="grid grid-2">
        <Card title="Expected income" subtitle="Only money you've confirmed counts in the plan.">
          <QueryView query={income}>
            {list => (
              <ul className="list">
                {list.map(i => (
                  <li key={i.id} className="row">
                    <div className="row-main">
                      <span className="row-title">{i.description}</span>
                      <span className="row-meta">{dayFi(i.date)} · {i.confirmed ? <Badge tone="green">Confirmed</Badge> : <Badge>Not confirmed</Badge>}</span>
                    </div>
                    <span className="row-amount">{money(i.amount)}</span>
                    <button className="btn btn-secondary btn-sm" disabled={action.pending}
                      onClick={() => action.run(c => c.patch(`/api/finance/expected-income/${i.id}`, { confirmed: !i.confirmed }), i.confirmed ? 'Marked as not confirmed.' : 'Confirmed. It now counts in your plan.')}>
                      {i.confirmed ? 'Unconfirm' : 'Confirm'}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </QueryView>
        </Card>

        <Card title="Your everyday spending plan" subtitle="Groceries, coffee, small purchases: a figure you choose.">
          <QueryView query={settings}>
            {s => (
              <form className="form-actions" style={{ alignItems: 'flex-end' }} onSubmit={async e => {
                e.preventDefault();
                const value = Number((allowance ?? String(s.discretionaryPerDay)).replace(',', '.'));
                const r = await action.run(c => c.put('/api/finance/settings', { discretionaryPerDay: value }), 'Everyday spending plan updated.');
                if (r) setAllowance(null);
              }}>
                <label className="field" style={{ flex: 1 }}>
                  Euros per day
                  <input className="input" inputMode="decimal" value={allowance ?? String(s.discretionaryPerDay)} onChange={e => setAllowance(e.target.value)} />
                </label>
                <button className="btn btn-primary" disabled={action.pending}>Save</button>
              </form>
            )}
          </QueryView>
        </Card>
      </div>
    </>
  );
}
