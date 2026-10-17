import { Link } from 'react-router-dom';
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useQuery } from '../api/context';
import type { Overview } from '../api/types';
import { Badge, Card, CertaintyBadge, DemoLabel, EmptyState, Progress, QueryView, Stat } from '../components/ui';
import { dateFi, dayFi, money, money0, shortMonth } from '../format';

export default function Dashboard() {
  const overview = useQuery(c => c.get<Overview>('/api/finance/overview'));

  return (
    <QueryView query={overview} rows={6}>
      {o => (
        <>
          <div className="page-head">
            <div>
              <h2 style={{ fontSize: '1.35rem' }}>Hello, {o.displayName}</h2>
              <p>
                Today is {dateFi(o.today)}. Payday is in {o.daysToPayday} {o.daysToPayday === 1 ? 'day' : 'days'} ({dayFi(o.nextPayday)}).
              </p>
            </div>
            {o.isDemoData && <DemoLabel />}
          </div>

          <div className="grid grid-4">
            <div className="stat stat-hero">
              <span className="stat-label">Safe to spend per day until payday</span>
              <span className="stat-value">{money(o.plan.safeToSpendPerDay)}</span>
              <span className="stat-hint">
                After bills, loans and savings due by {dayFi(o.plan.planUntil)} <Link to="/payday" style={{ color: '#fff' }}>See how</Link>
              </span>
            </div>
            <Stat label="Current account" value={money(o.balance)} hint="Balance from recorded transactions" />
            <Stat
              label="Unpaid invoices"
              value={money(o.invoices.unpaidTotal)}
              hint={`${o.invoices.unpaidCount} confirmed${o.invoices.toReview > 0 ? ` · ${o.invoices.toReview} to review` : ''}${o.invoices.overdueCount > 0 ? ` · ${o.invoices.overdueCount} overdue` : ''}`}
              tone={o.invoices.overdueCount > 0 ? 'bad' : 'default'}
            />
            <Stat label="Total debt" value={money0(o.debtTotal)} hint={`Minimum payments ${money0(o.debtMinimums)} a month`} />
          </div>

          <div className="grid grid-2">
            <Card title="Coming up in the next 14 days" subtitle="Confirmed invoices, plus regular bills and loan payments not yet paid" actions={<Link className="btn btn-ghost btn-sm" to="/inbox">Open inbox</Link>}>
              {o.upcoming.length === 0 ? (
                <EmptyState title="Nothing due in the next two weeks" />
              ) : (
                <ul className="list">
                  {o.upcoming.map((u, i) => (
                    <li key={`${u.label}-${u.date}-${i}`} className="row">
                      <div className="row-main">
                        <span className="row-title">{u.label}</span>
                        <span className="row-meta">
                          {dayFi(u.date)}
                          {u.overdue ? <Badge tone="red">Overdue</Badge> : <CertaintyBadge certainty={u.certainty} />}
                          {u.type === 'invoice' && <Badge tone="teal">Invoice</Badge>}
                        </span>
                      </div>
                      <span className="row-amount">{money(u.amount)}</span>
                    </li>
                  ))}
                </ul>
              )}
            </Card>

            <Card title="Money in and out" subtitle="Full months only, from your recorded transactions" actions={<Link className="btn btn-ghost btn-sm" to="/reports">Reports</Link>}>
              {o.cashflow.length === 0 ? (
                <EmptyState title="No full months recorded yet" />
              ) : (
                <div className="chart" role="img" aria-label={`Income and spending for ${o.cashflow.length} months`}>
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart data={o.cashflow.map(m => ({ month: shortMonth(m.month), In: m.income, Out: m.spending }))} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
                      <CartesianGrid stroke="#ece6dc" vertical={false} />
                      <XAxis dataKey="month" tickLine={false} axisLine={false} />
                      <YAxis tickLine={false} axisLine={false} width={56} tickFormatter={v => `${Math.round(Number(v) / 100) / 10}k €`} />
                      <Tooltip formatter={v => money(Number(v))} />
                      <Legend />
                      <Bar dataKey="In" fill="#0f766e" radius={[6, 6, 0, 0]} />
                      <Bar dataKey="Out" fill="#c8a46a" radius={[6, 6, 0, 0]} />
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              )}
            </Card>
          </div>

          <div className="grid grid-3">
            <Stat label="Spent so far this month" value={money0(o.spentThisMonth)} hint="Excluding transfers to savings" />
            <Stat label="Income last month" value={money0(o.incomeLastMonth)} />
            <Card title="Savings goals" actions={<Link className="btn btn-ghost btn-sm" to="/goals">Goals</Link>}>
              <p className="num">
                <strong>{money0(o.goals.saved)}</strong> <span className="muted">of {money0(o.goals.target)} across {o.goals.count} goals</span>
              </p>
              <Progress value={o.goals.saved} max={o.goals.target} label="Saved towards all goals" />
            </Card>
          </div>
        </>
      )}
    </QueryView>
  );
}
