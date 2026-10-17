import { useEffect, useState } from 'react';
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useQuery } from '../api/context';
import type { MonthReport, Transaction } from '../api/types';
import { Card, EmptyState, QueryView, Stat } from '../components/ui';
import { dateFi, dayFi, money, money0, monthName, numFi } from '../format';

export default function Reports() {
  const months = useQuery(c => c.get<string[]>('/api/finance/reports'));
  const [month, setMonth] = useState<string | null>(null);

  // Open on the last complete month once the list arrives
  useEffect(() => {
    if (!month && months.data && months.data.length > 0) setMonth(months.data[Math.min(1, months.data.length - 1)]);
  }, [month, months.data]);

  const report = useQuery(c => (month ? c.get<MonthReport>(`/api/finance/reports/${month}`) : Promise.resolve(null)), [month]);
  const txns = useQuery(c => (month ? c.get<Transaction[]>(`/api/finance/transactions?month=${month}`) : Promise.resolve([] as Transaction[])), [month]);

  return (
    <QueryView query={months}>
      {list => list.length === 0 ? <EmptyState title="No transactions recorded yet" /> : (
        <>
          <div className="page-head">
            <label className="field" style={{ maxWidth: 260 }}>
              Month
              <select className="input" value={month ?? ''} onChange={e => setMonth(e.target.value)}>
                {list.map(m => <option key={m} value={m}>{monthName(m)}</option>)}
              </select>
            </label>
          </div>

          <QueryView query={report} rows={5}>
            {r => r && (
              <>
                <p className="callout">
                  Period: {dateFi(r.periodStart)} to {dateFi(r.periodEnd)}
                  {r.isPartialMonth ? ' (this month so far; compared with the same days of last month)' : ' (full month)'}. From {r.transactionCount} recorded transactions.
                </p>
                <div className="grid grid-4">
                  <Stat label="Money in" value={money0(r.income)} />
                  <Stat label="Money out" value={money0(r.spending)} hint={r.previousSpending > 0 ? `${r.spending > r.previousSpending ? '▲' : '▼'} ${money0(Math.abs(r.spending - r.previousSpending))} vs last month` : undefined} />
                  <Stat label="Moved to savings" value={money0(r.saved)} />
                  <Stat label="Kept" value={r.income > 0 ? `${numFi(r.keptPercent)} %` : '—'} hint={r.income > 0 ? 'Of money in, not spent' : 'No income in this period'} />
                </div>
                <div className="grid grid-2">
                  <Card title="Where it went">
                    {r.byCategory.length === 0 ? <EmptyState title="No spending in this period" /> : (
                      <div className="chart" style={{ height: Math.max(200, r.byCategory.length * 38) }} role="img" aria-label="Spending by category">
                        <ResponsiveContainer width="100%" height="100%">
                          <BarChart data={r.byCategory} layout="vertical" margin={{ left: 8, right: 16 }}>
                            <CartesianGrid stroke="#ece6dc" horizontal={false} />
                            <XAxis type="number" tickLine={false} axisLine={false} tickFormatter={v => `${v} €`} />
                            <YAxis type="category" dataKey="category" width={110} tickLine={false} axisLine={false} />
                            <Tooltip formatter={v => money(Number(v))} />
                            <Bar dataKey="amount" name="Spent" fill="#0f766e" radius={[0, 6, 6, 0]} />
                          </BarChart>
                        </ResponsiveContainer>
                      </div>
                    )}
                  </Card>
                  <Card title="Biggest payees">
                    <ul className="list">
                      {r.topPayees.map(p => (
                        <li key={p.category} className="row">
                          <span className="row-main row-title">{p.category}</span>
                          <span className="row-amount">{money(p.amount)}</span>
                        </li>
                      ))}
                    </ul>
                  </Card>
                </div>
              </>
            )}
          </QueryView>

          <Card title="Transactions" subtitle={month ? monthName(month) : undefined}>
            <QueryView query={txns}>
              {t => t.length === 0 ? <EmptyState title="No transactions in this month" /> : (
                <div className="table-wrap">
                  <table className="table">
                    <thead><tr><th>Date</th><th>Description</th><th>Category</th><th className="amount">Amount</th></tr></thead>
                    <tbody>
                      {t.map(x => (
                        <tr key={x.id}>
                          <td style={{ whiteSpace: 'nowrap' }}>{dayFi(x.date)}</td>
                          <td>{x.description}</td>
                          <td className="muted">{x.category}</td>
                          <td className={`amount ${x.amount > 0 ? 'pos' : ''}`}>{x.amount > 0 ? '+' : ''}{money(x.amount)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </QueryView>
          </Card>
        </>
      )}
    </QueryView>
  );
}
