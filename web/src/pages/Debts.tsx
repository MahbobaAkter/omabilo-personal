import { useState } from 'react';
import { CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useAction, useQuery } from '../api/context';
import type { DebtPlan, Loan, Overview } from '../api/types';
import { ActionNotice, Badge, Card, DemoLabel, EmptyState, Progress, QueryView, Stat } from '../components/ui';
import { money, money0, numFi } from '../format';

export default function Debts() {
  const [strategy, setStrategy] = useState<'avalanche' | 'snowball'>('avalanche');
  const [extra, setExtra] = useState(100);
  const loans = useQuery(c => c.get<Loan[]>('/api/finance/debts'));
  const plan = useQuery(c => c.get<DebtPlan>(`/api/finance/debts/plan?strategy=${strategy}&extra=${extra}`), [strategy, extra]);
  const overview = useQuery(c => c.get<Overview>('/api/finance/overview'));
  const [paying, setPaying] = useState<string | null>(null);
  const [amount, setAmount] = useState('');
  const action = useAction();

  const monthLabel = (offset: number) => {
    const d = new Date();
    d.setUTCDate(1);
    d.setUTCMonth(d.getUTCMonth() + offset);
    return d.toLocaleDateString('en-GB', { month: 'short', year: '2-digit', timeZone: 'UTC' });
  };

  return (
    <>
      <div className="page-head">
        <p>Every balance in one place, and a plan for becoming debt-free. Minimum payments are always paid first.</p>
        {overview.data?.isDemoData && <DemoLabel>Fictional loans</DemoLabel>}
      </div>
      <ActionNotice error={action.error} success={action.success} onClose={action.clear} />

      <QueryView query={loans} rows={4}>
        {list => list.length === 0 ? <EmptyState title="No debts recorded" /> : (
          <>
            <div className="grid grid-3">
              <Stat label="Total owed" value={money0(list.reduce((s, l) => s + l.balance, 0))} hint={`${list.filter(l => l.balance > 0).length} active`} />
              <Stat label="Minimum payments" value={money0(list.filter(l => l.balance > 0).reduce((s, l) => s + l.minPayment, 0))} hint="Every month" />
              <Stat label="Interest this month (approx.)" value={money(list.reduce((s, l) => s + (l.balance * l.apr) / 100 / 12, 0))} hint="Balance × rate ÷ 12" />
            </div>

            <div className="grid grid-3">
              {list.map(l => (
                <Card key={l.id} title={l.name} subtitle={`${l.kind} · ${l.lender}`}>
                  <p className="num" style={{ fontSize: '1.4rem', fontWeight: 700 }}>{l.balance > 0 ? money(l.balance) : 'Paid off'}</p>
                  <Progress value={l.startingBalance - l.balance} max={l.startingBalance} label={`${l.name} paid off`} />
                  <p className="row-meta">
                    <Badge tone={l.apr >= 10 ? 'amber' : 'neutral'}>{numFi(l.apr)} % APR</Badge>
                    <Badge>Min {money0(l.minPayment)}/mo</Badge>
                    <Badge>Due on the {l.dueDay}.</Badge>
                  </p>
                  {paying === l.id ? (
                    <form className="form-actions" onSubmit={async e => {
                      e.preventDefault();
                      const r = await action.run(c => c.post<Loan>(`/api/finance/debts/${l.id}/payments`, { amount: Number(amount.replace(',', '.')) }), `Payment recorded for ${l.name}.`);
                      if (r) { setPaying(null); setAmount(''); }
                    }}>
                      <label className="field" style={{ flex: 1 }}>
                        <span className="sr-only">Payment amount in euros</span>
                        <input className="input" inputMode="decimal" placeholder={`e.g. ${l.minPayment}`} value={amount} onChange={e => setAmount(e.target.value)} autoFocus />
                      </label>
                      <button className="btn btn-primary btn-sm" disabled={action.pending}>Save</button>
                      <button type="button" className="btn btn-secondary btn-sm" onClick={() => setPaying(null)}>Cancel</button>
                    </form>
                  ) : (
                    <button className="btn btn-secondary btn-sm" disabled={l.balance <= 0} onClick={() => { setPaying(l.id); setAmount(String(l.minPayment)); }}>Log a payment</button>
                  )}
                </Card>
              ))}
            </div>
          </>
        )}
      </QueryView>

      <Card title="Payoff plan" subtitle="A projection using today's balances and rates. Real interest may differ slightly.">
        <div className="form-grid">
          <div className="field">
            <span className="small" style={{ fontWeight: 600 }}>Strategy</span>
            <div className="tabs" role="group" aria-label="Strategy">
              <button className="tab" aria-pressed={strategy === 'avalanche'} onClick={() => setStrategy('avalanche')}>Highest interest first</button>
              <button className="tab" aria-pressed={strategy === 'snowball'} onClick={() => setStrategy('snowball')}>Smallest balance first</button>
            </div>
          </div>
          <label className="field">
            Extra each month: <strong>{money0(extra)}</strong>
            <input type="range" min={0} max={600} step={10} value={extra} onChange={e => setExtra(Number(e.target.value))} aria-label="Extra payment each month in euros" />
          </label>
        </div>
        <QueryView query={plan}>
          {p => (
            <>
              <div className="grid grid-3">
                <Stat label="Debt-free in" value={p.neverPaysOff ? 'Not with these payments' : `${p.months} months`} hint={p.neverPaysOff ? 'Payments do not cover the interest' : `around ${monthLabel(p.months)}`} tone={p.neverPaysOff ? 'bad' : 'default'} />
                <Stat label="Total interest" value={money0(p.totalInterest)} />
                <Stat label="Saved vs minimums only" value={p.interestSavedVsMinimums === null ? '—' : money0(p.interestSavedVsMinimums)} tone="good" />
              </div>
              <div className="chart" role="img" aria-label="Remaining debt over time, with your plan and with minimum payments only">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={p.minimumsOnlyByMonth.map((m, i) => ({ month: monthLabel(i), plan: p.remainingByMonth[i] ?? 0, minimums: m }))}>
                    <CartesianGrid stroke="#ece6dc" vertical={false} />
                    <XAxis dataKey="month" tickLine={false} axisLine={false} minTickGap={24} />
                    <YAxis tickLine={false} axisLine={false} width={56} tickFormatter={v => `${Math.round(Number(v) / 1000)}k €`} />
                    <Tooltip formatter={v => money0(Number(v))} />
                    <Legend />
                    <Line type="monotone" dataKey="plan" name="With your plan" stroke="#0f766e" strokeWidth={2.5} dot={false} />
                    <Line type="monotone" dataKey="minimums" name="Minimums only" stroke="#9aa3b2" strokeWidth={2} dot={false} strokeDasharray="5 4" />
                  </LineChart>
                </ResponsiveContainer>
              </div>
              {p.payoffOrder.length > 0 && (
                <ol className="small" style={{ margin: 0, paddingLeft: 20 }}>
                  {p.payoffOrder.map(o => <li key={o.debt}>{o.debt}: paid off around {monthLabel(o.month)}</li>)}
                </ol>
              )}
            </>
          )}
        </QueryView>
      </Card>
    </>
  );
}
