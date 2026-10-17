import { useState, type FormEvent, type ReactElement } from 'react';
import { useAction, useQuery } from '../api/context';
import type { IngestResult, Invoice, InvoiceDetail } from '../api/types';
import { ActionNotice, Badge, Card, EmptyState, ErrorState, Loading, QueryView, type BadgeTone } from '../components/ui';
import { dateFi, isoDay, money } from '../format';

const FILTERS = [
  { key: 'all', label: 'All' },
  { key: 'unpaid', label: 'Unpaid' },
  { key: 'review', label: 'To review' },
  { key: 'paid', label: 'Paid' },
  { key: 'closed', label: 'Rejected & merged' },
] as const;

// The API's fixed FICTIONAL samples (backend/README.md), read by the real extraction code
const SAMPLE_EMAILS = [
  { key: 'email-energy', label: 'Energy invoice email' },
  { key: 'email-water', label: 'Water bill email (Finnish)' },
  { key: 'email-energy-again', label: 'Same energy invoice, forwarded again' },
  { key: 'email-insurance', label: 'Insurance renewal email' },
];
const SAMPLE_DOCS = [
  { key: 'doc-dental', label: 'Clear scan: dental invoice' },
  { key: 'doc-poor-scan', label: 'Poor scan: bike repair' },
];

export function statusOf(i: Invoice): { label: string; tone: BadgeTone } {
  if (i.state === 'NeedsReview') return { label: i.possibleDuplicateOfId ? 'Possible duplicate' : 'To review', tone: 'amber' };
  if (i.state === 'Rejected') return { label: 'Rejected', tone: 'neutral' };
  if (i.state === 'Merged') return { label: 'Merged', tone: 'neutral' };
  if (i.paymentStatus === 'Paid') return { label: 'Paid', tone: 'green' };
  if (new Date(i.dueDate) < new Date(new Date().toISOString().slice(0, 10))) return { label: 'Overdue', tone: 'red' };
  return { label: 'Unpaid', tone: 'blue' };
}

const ingestTone: Record<IngestResult['outcome'], 'success' | 'info' | 'warn' | 'error'> = {
  Created: 'success',
  LinkedToExisting: 'info',
  AlreadyIngested: 'info',
  PossibleDuplicate: 'warn',
  Invalid: 'error',
};

export default function Inbox() {
  const [filter, setFilter] = useState<(typeof FILTERS)[number]['key']>('all');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [importResult, setImportResult] = useState<IngestResult | null>(null);
  const invoices = useQuery(c => c.get<Invoice[]>('/api/invoices'));
  const action = useAction();

  async function runImport(path: string) {
    setImportResult(null);
    const r = await action.run(c => c.post<IngestResult | IngestResult[]>(path));
    if (r) setImportResult(Array.isArray(r) ? r[r.length - 1] : r);
  }

  async function exportData() {
    await action.run(async c => {
      const blob = await c.download('/api/invoices/export');
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `omabilo-invoices-${new Date().toISOString().slice(0, 10)}.json`;
      a.click();
      URL.revokeObjectURL(url);
    }, 'Your invoice data was downloaded.');
  }

  return (
    <>
      <div className="page-head">
        <p>Bills from every source in one place. The same invoice arriving twice is matched, never counted twice.</p>
        <div className="form-actions">
          <button className="btn btn-primary" onClick={() => setAdding(a => !a)} aria-expanded={adding}>
            {adding ? 'Close form' : 'Add invoice'}
          </button>
          <button className="btn btn-secondary" onClick={exportData} disabled={action.pending}>Export data</button>
        </div>
      </div>

      <ActionNotice error={action.error} success={action.success} onClose={action.clear} />

      {adding && (
        <Card title="Add an invoice" subtitle="Typed-in invoices are confirmed straight away.">
          <InvoiceForm onDone={() => setAdding(false)} />
        </Card>
      )}

      <Card title={<>Import invoices <Badge tone="blue">Simulated</Badge></>} subtitle="Fictional sample emails and scans, read by the real extraction and matching code. No mailbox is connected.">
        <div className="form-actions">
          {SAMPLE_EMAILS.map(s => (
            <button key={s.key} className="btn btn-secondary btn-sm" disabled={action.pending} onClick={() => runImport(`/api/invoices/simulate/email/${s.key}`)}>
              ✉ {s.label}
            </button>
          ))}
        </div>
        <div className="form-actions">
          {SAMPLE_DOCS.map(s => (
            <button key={s.key} className="btn btn-secondary btn-sm" disabled={action.pending} onClick={() => runImport(`/api/invoices/simulate/document/${s.key}`)}>
              ⎙ {s.label}
            </button>
          ))}
          <button className="btn btn-ghost btn-sm" disabled={action.pending} onClick={() => runImport('/api/invoices/demo')}>
            Load demo invoices
          </button>
        </div>
        {importResult && (
          <div className={`notice ${ingestTone[importResult.outcome] === 'error' ? 'notice-error' : ingestTone[importResult.outcome] === 'success' ? 'notice-success' : 'callout'}`} role="status">
            <span>
              {importResult.message}
              {importResult.invoiceId && (
                <>
                  {' '}
                  <button className="btn btn-ghost btn-sm" onClick={() => setSelected(importResult.invoiceId)}>Open</button>
                </>
              )}
            </span>
          </div>
        )}
      </Card>

      <Card title="Invoices">
        <div className="card-head">
          <div className="tabs" role="group" aria-label="Filter invoices">
            {FILTERS.map(f => (
              <button key={f.key} className="tab" aria-pressed={filter === f.key} onClick={() => setFilter(f.key)}>
                {f.label}
                {invoices.data && <span className="muted"> {invoices.data.filter(i => matches(i, f.key)).length}</span>}
              </button>
            ))}
          </div>
          <input className="input" style={{ maxWidth: 280 }} type="search" placeholder="Search provider, number or reference" aria-label="Search invoices" value={search} onChange={e => setSearch(e.target.value)} />
        </div>

        <QueryView query={invoices}>
          {list => {
            const q = search.trim().toLowerCase();
            const rows = list
              .filter(i => matches(i, filter))
              .filter(i => !q || [i.provider, i.invoiceNumber, i.reference].some(v => v?.toLowerCase().replace(/\s/g, '').includes(q.replace(/\s/g, ''))));

            if (list.length === 0) {
              return <EmptyState title="No invoices yet">Add one, or try a simulated import above.</EmptyState>;
            }
            if (rows.length === 0) {
              return (
                <EmptyState title="No invoices match" action={<button className="btn btn-secondary btn-sm" onClick={() => { setFilter('all'); setSearch(''); }}>Show all</button>}>
                  Nothing is hidden: clear the filter or search to see all {list.length}.
                </EmptyState>
              );
            }
            return (
              <ul className="list">
                {rows.map(i => {
                  const s = statusOf(i);
                  return (
                    <li key={i.id} className="row">
                      <button className="row-button" onClick={() => setSelected(i.id)} aria-label={`Open ${i.provider} invoice, ${money(i.amount)}, ${s.label}`}>
                        <div className="row-main">
                          <span className="row-title">{i.provider}</span>
                          <span className="row-meta">
                            {i.invoiceNumber ?? 'No number'} · due {dateFi(i.dueDate)}
                            {i.sourceRecordIds.length > 1 && <Badge tone="teal">{i.sourceRecordIds.length} sources</Badge>}
                          </span>
                        </div>
                        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 4 }}>
                          <span className="row-amount" style={i.countsInTotals ? undefined : { textDecoration: 'line-through', opacity: 0.6 }}>{money(i.amount)}</span>
                          <Badge tone={s.tone}>{s.label}</Badge>
                        </div>
                      </button>
                    </li>
                  );
                })}
              </ul>
            );
          }}
        </QueryView>
      </Card>

      <Card title="Your invoice data" subtitle="Stored per signed-in user. History records what changed, never amounts or document text.">
        {!confirmDelete ? (
          <div className="form-actions">
            <button className="btn btn-danger" onClick={() => setConfirmDelete(true)}>Delete all invoice data…</button>
          </div>
        ) : (
          <div className="callout callout-warn">
            <p><strong>This permanently deletes every invoice, its sources and its history.</strong> It can't be undone. Export first if you want a copy.</p>
            <div className="form-actions" style={{ marginTop: 10 }}>
              <button className="btn btn-danger" disabled={action.pending} onClick={async () => {
                const r = await action.run(c => c.del<{ deletedInvoices: number }>('/api/invoices?confirm=delete-all-invoice-data'), x => `Deleted ${x.deletedInvoices} invoices.`);
                if (r) setConfirmDelete(false);
              }}>Delete permanently</button>
              <button className="btn btn-secondary" onClick={() => setConfirmDelete(false)}>Cancel</button>
            </div>
          </div>
        )}
      </Card>

      {selected && <InvoiceDrawer id={selected} onClose={() => setSelected(null)} />}
    </>
  );
}

function matches(i: Invoice, filter: string) {
  switch (filter) {
    case 'unpaid': return i.countsInTotals && i.paymentStatus === 'Unpaid';
    case 'review': return i.state === 'NeedsReview';
    case 'paid': return i.paymentStatus === 'Paid' && i.state !== 'Merged';
    case 'closed': return i.state === 'Rejected' || i.state === 'Merged';
    default: return true;
  }
}

function InvoiceForm({ invoice, onDone }: { invoice?: Invoice; onDone: () => void }) {
  const [provider, setProvider] = useState(invoice?.provider ?? '');
  const [invoiceNumber, setNumber] = useState(invoice?.invoiceNumber ?? '');
  const [reference, setReference] = useState(invoice?.reference ?? '');
  const [amount, setAmount] = useState(invoice ? String(invoice.amount) : '');
  const [dueDate, setDue] = useState(invoice ? isoDay(invoice.dueDate) : new Date(Date.now() + 14 * 864e5).toISOString().slice(0, 10));
  const [invoiceDate, setDate] = useState(invoice?.invoiceDate ? isoDay(invoice.invoiceDate) : '');
  const action = useAction();

  async function submit(e: FormEvent) {
    e.preventDefault();
    const body = {
      provider,
      invoiceNumber: invoiceNumber || null,
      reference: reference || null,
      amount: Number(amount.replace(',', '.')),
      dueDate,
      invoiceDate: invoiceDate || null,
    };
    const r = invoice
      ? await action.run(c => c.patch(`/api/invoices/${invoice.id}`, body), 'Invoice updated.')
      : await action.run(c => c.post<IngestResult>('/api/invoices', body), x => x.message);
    if (r) onDone();
  }

  const fe = action.error?.fieldErrors;
  return (
    <form onSubmit={submit} noValidate>
      <div className="form-grid">
        <Field label="Provider" error={fe?.provider?.[0]}>
          <input className="input" value={provider} onChange={e => setProvider(e.target.value)} required maxLength={80} />
        </Field>
        <Field label="Amount (€)" error={fe?.amount?.[0]}>
          <input className="input" inputMode="decimal" value={amount} onChange={e => setAmount(e.target.value)} required placeholder="58,20" />
        </Field>
        <Field label="Due date" error={fe?.dueDate?.[0]}>
          <input className="input" type="date" value={dueDate} onChange={e => setDue(e.target.value)} required />
        </Field>
        <Field label="Invoice date (optional)">
          <input className="input" type="date" value={invoiceDate} onChange={e => setDate(e.target.value)} />
        </Field>
        <Field label="Invoice number (optional)" error={fe?.invoiceNumber?.[0]}>
          <input className="input" value={invoiceNumber} onChange={e => setNumber(e.target.value)} maxLength={40} />
        </Field>
        <Field label="Payment reference (optional)" hint="Finnish viitenumero or RF reference" error={fe?.reference?.[0]}>
          <input className="input" value={reference} onChange={e => setReference(e.target.value)} maxLength={40} />
        </Field>
      </div>
      {action.error && !fe && <div style={{ marginTop: 12 }}><ActionNotice error={action.error} success={null} /></div>}
      <div className="form-actions" style={{ marginTop: 14 }}>
        <button className="btn btn-primary" type="submit" disabled={action.pending}>{action.pending ? 'Saving…' : invoice ? 'Save changes' : 'Add invoice'}</button>
        <button className="btn btn-secondary" type="button" onClick={onDone}>Cancel</button>
      </div>
    </form>
  );
}

function Field({ label, hint, error, children }: { label: string; hint?: string; error?: string; children: ReactElement }) {
  return (
    <div className="field">
      <label>
        {label}
        {children}
      </label>
      {hint && <span className="hint">{hint}</span>}
      {error && <span className="small" style={{ color: 'var(--red)' }} role="alert">{error}</span>}
    </div>
  );
}

function InvoiceDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const detail = useQuery(c => c.get<InvoiceDetail>(`/api/invoices/${id}`), [id]);
  const action = useAction();
  const [editing, setEditing] = useState(false);

  const act = (path: string, message: string, body?: unknown) => action.run(c => c.post(`/api/invoices/${id}/${path}`, body), message);

  return (
    <div className="drawer-backdrop" onClick={onClose}>
      <div className="drawer" role="dialog" aria-modal="true" aria-label="Invoice details" onClick={e => e.stopPropagation()}>
        <div className="card-head">
          <h2>Invoice</h2>
          <button className="btn btn-secondary btn-sm" onClick={onClose} autoFocus>Close</button>
        </div>
        <ActionNotice error={action.error} success={action.success} onClose={action.clear} />
        {detail.error && <ErrorState error={detail.error} onRetry={detail.retry} />}
        {detail.loading && !detail.data && <Loading />}
        {detail.data && (() => {
          const { invoice: inv, sources, history, referenceValid } = detail.data;
          const s = statusOf(inv);
          return (
            <>
              <div>
                <h3 style={{ fontSize: '1.25rem' }}>{inv.provider}</h3>
                <p className="row-meta" style={{ marginTop: 4 }}>
                  <Badge tone={s.tone}>{s.label}</Badge>
                  <Badge tone={inv.verification === 'Verified' ? 'green' : 'neutral'}>{inv.verification}</Badge>
                  Read with {Math.round(inv.confidence * 100)} % confidence
                </p>
              </div>
              <dl className="kv">
                <div><dt>Amount</dt><dd className="num">{money(inv.amount)} {inv.currency !== 'EUR' && inv.currency}</dd></div>
                <div><dt>Due date</dt><dd>{dateFi(inv.dueDate)}</dd></div>
                <div><dt>Invoice number</dt><dd>{inv.invoiceNumber ?? '—'}</dd></div>
                <div><dt>Reference</dt><dd>{inv.reference ?? '—'}</dd></div>
                <div><dt>Invoice date</dt><dd>{inv.invoiceDate ? dateFi(inv.invoiceDate) : '—'}</dd></div>
                <div><dt>Paid</dt><dd>{inv.paidAt ? dateFi(inv.paidAt) : 'Not yet'}</dd></div>
              </dl>
              {inv.reference && (
                <p className={`callout ${referenceValid ? '' : 'callout-warn'}`}>
                  {referenceValid ? '✓ The payment reference check digit is valid.' : "⚠ The payment reference check digit doesn't match. Check it before paying."}
                </p>
              )}
              {inv.possibleDuplicateOfId && (
                <div className="callout callout-warn">
                  <p><strong>This looks like an invoice you already have.</strong> Merge it so it's counted once, or keep both if they are separate bills. Nothing is merged automatically.</p>
                  <div className="form-actions" style={{ marginTop: 10 }}>
                    <button className="btn btn-secondary btn-sm" disabled={action.pending} onClick={() => act('merge', 'Merged into the existing invoice.', { targetInvoiceId: inv.possibleDuplicateOfId })}>Merge</button>
                    <button className="btn btn-secondary btn-sm" disabled={action.pending} onClick={() => act('keep-separate', 'Kept as a separate invoice.')}>Keep both</button>
                  </div>
                </div>
              )}

              {editing ? (
                <Card title="Correct the details"><InvoiceForm invoice={inv} onDone={() => setEditing(false)} /></Card>
              ) : (
                <div className="form-actions">
                  {inv.state === 'NeedsReview' && (
                    <button className="btn btn-primary" disabled={action.pending || !!inv.possibleDuplicateOfId} title={inv.possibleDuplicateOfId ? 'Decide on the possible duplicate first' : undefined}
                      onClick={() => act('approve', 'Approved. It now counts in your bills.')}>Approve</button>
                  )}
                  {inv.countsInTotals && inv.paymentStatus === 'Unpaid' && (
                    <button className="btn btn-primary" disabled={action.pending} onClick={() => act('pay', 'Marked as paid and recorded in your account.')}>Mark paid</button>
                  )}
                  {inv.paymentStatus === 'Paid' && (
                    <button className="btn btn-secondary" disabled={action.pending} onClick={() => act('unpay', 'Marked unpaid; the payment was removed from your account.')}>Mark unpaid</button>
                  )}
                  {inv.state !== 'Merged' && inv.state !== 'Rejected' && (
                    <>
                      <button className="btn btn-secondary" onClick={() => setEditing(true)}>Edit</button>
                      <button className="btn btn-danger" disabled={action.pending} onClick={() => act('reject', 'Rejected. It no longer counts.')}>Reject</button>
                    </>
                  )}
                </div>
              )}

              <Card title={`Sources (${sources.length})`}>
                <ul className="list">
                  {sources.map(src => (
                    <li key={src.id} className="row">
                      <div className="row-main">
                        <span className="row-title">{src.sourceLabel ?? src.type}</span>
                        <span className="row-meta">{src.type} · received {dateFi(src.receivedAt)} · key {src.idempotencyKey.slice(0, 8)}</span>
                      </div>
                      {src.isSimulated && <Badge tone="blue">Simulated</Badge>}
                    </li>
                  ))}
                </ul>
              </Card>
              <Card title="History">
                <ul className="list">
                  {history.map(h => (
                    <li key={h.id} className="row">
                      <div className="row-main">
                        <span>{h.detail}</span>
                        <span className="row-meta">{dateFi(h.at)} · {h.actor === 'user' ? 'you' : 'Omabilo'}</span>
                      </div>
                    </li>
                  ))}
                </ul>
              </Card>
            </>
          );
        })()}
      </div>
    </div>
  );
}
