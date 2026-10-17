// Euros and dates in Finnish conventions: 1 234,56 € and 17.10.2026.

const eur = new Intl.NumberFormat('fi-FI', { style: 'currency', currency: 'EUR' });
const eur0 = new Intl.NumberFormat('fi-FI', { style: 'currency', currency: 'EUR', maximumFractionDigits: 0 });

export const money = (v: number) => eur.format(v);
export const money0 = (v: number) => eur0.format(v);
export const signedMoney = (v: number) => (v > 0 ? `+${eur.format(v)}` : eur.format(v));

const parse = (iso: string) => new Date(iso.length === 10 ? `${iso}T00:00:00Z` : iso);

/** 17.10.2026 */
export const dateFi = (iso: string) => {
  const d = parse(iso);
  return `${d.getUTCDate()}.${d.getUTCMonth() + 1}.${d.getUTCFullYear()}`;
};

/** Sat 17.10. */
export const dayFi = (iso: string) => {
  const d = parse(iso);
  const weekday = d.toLocaleDateString('en-GB', { weekday: 'short', timeZone: 'UTC' });
  return `${weekday} ${d.getUTCDate()}.${d.getUTCMonth() + 1}.`;
};

/** October 2026, from 2026-10 */
export const monthName = (yyyyMm: string) =>
  parse(`${yyyyMm}-01`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' });

export const shortMonth = (yyyyMm: string) =>
  parse(`${yyyyMm}-01`).toLocaleDateString('en-GB', { month: 'short', timeZone: 'UTC' });

/** yyyy-MM-dd for <input type="date"> */
export const isoDay = (iso: string) => iso.slice(0, 10);

export const pct = (v: number) => `${Math.round(v)} %`;

/** 29,9 */
export const numFi = (v: number) => v.toLocaleString('fi-FI', { maximumFractionDigits: 2 });
