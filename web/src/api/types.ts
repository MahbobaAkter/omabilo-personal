// Shapes of the API's JSON responses (see backend/README.md for the routes).

export type Certainty = 'Confirmed' | 'Scheduled' | 'Estimate' | 'Planned';
export type InvoiceState = 'NeedsReview' | 'Ready' | 'Rejected' | 'Merged';
export type PaymentStatus = 'Unpaid' | 'Paid' | 'Cancelled';
export type SourceType = 'Demo' | 'Manual' | 'ForwardedEmail' | 'Document' | 'Gmail' | 'Outlook' | 'FinnishProvider';
export type IngestOutcome = 'Created' | 'LinkedToExisting' | 'PossibleDuplicate' | 'AlreadyIngested' | 'Invalid';

export interface UpcomingItem {
  label: string;
  amount: number;
  date: string;
  type: 'invoice' | 'recurring' | 'loan';
  certainty: Certainty;
  invoiceId: string | null;
  overdue: boolean;
}

export interface MonthTotals {
  month: string;
  income: number;
  spending: number;
  saved: number;
}

export interface Overview {
  isDemoData: boolean;
  displayName: string;
  today: string;
  balance: number;
  nextPayday: string;
  daysToPayday: number;
  plan: { projectedBalance: number; safeToSpendPerDay: number; planUntil: string };
  invoices: { toReview: number; unpaidCount: number; unpaidTotal: number; overdueCount: number };
  upcoming: UpcomingItem[];
  spentThisMonth: number;
  incomeLastMonth: number;
  debtTotal: number;
  debtMinimums: number;
  goals: { saved: number; target: number; count: number };
  cashflow: MonthTotals[];
}

export interface PlanLine {
  label: string;
  amount: number;
  group: 'income' | 'bills' | 'loans' | 'savings' | 'discretionary';
  certainty: Certainty;
  date: string | null;
  source: string;
  note: string | null;
}

export interface PaydayPlan {
  today: string;
  planUntil: string;
  nextPayday: string;
  days: number;
  startingBalance: number;
  confirmedIncome: number;
  bills: number;
  loanPayments: number;
  savings: number;
  discretionary: number;
  projectedBalance: number;
  safeToSpendPerDay: number;
  lines: PlanLine[];
  excluded: { label: string; amount: number; date: string | null; reason: string }[];
  warnings: string[];
}

export interface ExpectedIncome {
  id: string;
  description: string;
  date: string;
  amount: number;
  confirmed: boolean;
}

export interface FinanceSettings {
  displayName: string;
  payDay: number;
  netSalary: number;
  employer: string;
  discretionaryPerDay: number;
  isDemoData: boolean;
}

export interface Loan {
  id: string;
  name: string;
  lender: string;
  kind: string;
  balance: number;
  startingBalance: number;
  apr: number;
  minPayment: number;
  dueDay: number;
}

export interface DebtPlan {
  strategy: 'avalanche' | 'snowball';
  extraPerMonth: number;
  months: number;
  neverPaysOff: boolean;
  totalInterest: number;
  interestSavedVsMinimums: number | null;
  payoffOrder: { debt: string; month: number }[];
  remainingByMonth: number[];
  minimumsOnlyByMonth: number[];
}

export interface Goal {
  id: string;
  name: string;
  target: number;
  saved: number;
  targetDate: string | null;
  isEmergencyFund: boolean;
}

export interface MonthReport {
  month: string;
  periodStart: string;
  periodEnd: string;
  isPartialMonth: boolean;
  income: number;
  spending: number;
  saved: number;
  keptPercent: number;
  previousSpending: number;
  byCategory: { category: string; amount: number }[];
  topPayees: { category: string; amount: number }[];
  transactionCount: number;
}

export interface Transaction {
  id: string;
  date: string;
  description: string;
  category: string;
  amount: number;
}

export interface Invoice {
  id: string;
  ownerId: string;
  provider: string;
  invoiceNumber: string | null;
  reference: string | null;
  invoiceDate: string | null;
  dueDate: string;
  amount: number;
  currency: string;
  paymentStatus: PaymentStatus;
  verification: 'Unverified' | 'Verified' | 'Rejected';
  state: InvoiceState;
  confidence: number;
  primarySource: SourceType;
  sourceRecordIds: string[];
  createdAt: string;
  updatedAt: string;
  possibleDuplicateOfId: string | null;
  mergedIntoId: string | null;
  paidAt: string | null;
  countsInTotals: boolean;
}

export interface InvoiceSource {
  id: string;
  type: SourceType;
  sourceRef: string;
  idempotencyKey: string;
  isSimulated: boolean;
  confidence: number;
  receivedAt: string;
  sourceLabel: string | null;
  fields: { fieldConfidence: Record<string, number> | null };
}

export interface AuditEvent {
  id: string;
  action: string;
  actor: string;
  at: string;
  detail: string;
}

export interface InvoiceDetail {
  invoice: Invoice;
  sources: InvoiceSource[];
  history: AuditEvent[];
  referenceValid: boolean;
}

export interface IngestResult {
  outcome: IngestOutcome;
  invoiceId: string | null;
  message: string;
}

export interface SourceCatalogEntry {
  type: SourceType;
  name: string;
  availability: 'Available' | 'Simulated' | 'NotConnected';
  status: string;
}
