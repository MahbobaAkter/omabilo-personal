import { lazy, Suspense } from 'react';
import { Route, Routes } from 'react-router-dom';
import { Layout } from './components/Layout';
import { EmptyState, Loading } from './components/ui';

// Each screen loads when first opened, so the first visit downloads less
const Dashboard = lazy(() => import('./pages/Dashboard'));
const Inbox = lazy(() => import('./pages/Inbox'));
const Debts = lazy(() => import('./pages/Debts'));
const Payday = lazy(() => import('./pages/Payday'));
const Goals = lazy(() => import('./pages/Goals'));
const Reports = lazy(() => import('./pages/Reports'));
const Settings = lazy(() => import('./pages/Settings'));

export default function App() {
  return (
    <Layout>
      <Suspense fallback={<Loading rows={5} />}>
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/inbox" element={<Inbox />} />
          <Route path="/debts" element={<Debts />} />
          <Route path="/payday" element={<Payday />} />
          <Route path="/goals" element={<Goals />} />
          <Route path="/reports" element={<Reports />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="*" element={<EmptyState title="Page not found">Use the menu to find your way back.</EmptyState>} />
        </Routes>
      </Suspense>
    </Layout>
  );
}
