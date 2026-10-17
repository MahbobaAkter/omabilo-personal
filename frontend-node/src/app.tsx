import './app.css';

import { AuthProvider, IkonApp, getConnectionRecovery, useAuthOptional, useIkonApp } from '@ikonai/sdk-react-ui';
import { registerStandardUiModule, registerLucideIconsModule } from '@ikonai/sdk-react-ui-standard';
import { AuthGuard } from './auth/auth-guard';
import { authConfig } from './env';
import { I18nProvider, useI18n } from './i18n/i18n';
import { en } from './i18n/en';

function App() {
  return (
    <I18nProvider translations={{ en }}>
      <AuthProvider config={authConfig}>
        <AuthGuard config={authConfig}>
          <AuthorizedApp />
        </AuthGuard>
      </AuthProvider>
    </I18nProvider>
  );
}

function AuthorizedApp() {
  const app = useIkonApp({
    modules: [registerStandardUiModule, registerLucideIconsModule],
  });

  return (
    <IkonApp
      {...app}
      connectingOverlay={(isSlow) => (isSlow ? <ConnectingOverlay /> : null)}
      reconnectingOverlay={<ReconnectingOverlay />}
      offlineOverlay={<OfflineOverlay isServerFull={app.isServerFull} isSessionExpired={app.isSessionExpired} isStartupFailed={app.isStartupFailed} />}
      accessDeniedScreen={(reason) => <AccessDeniedScreen reason={reason} />}
    />
  );
}

function ConnectingOverlay() {
  const { t } = useI18n();
  return (
    <div className="ikon-connecting-overlay">
      <div className="ikon-connecting-chip">
        <div className="ikon-connecting-spinner" />
        <span>{t('connection.connecting')}</span>
      </div>
    </div>
  );
}

function ReconnectingOverlay() {
  const { t } = useI18n();
  return (
    <div className="ikon-reconnecting-overlay">
      <div className="ikon-reconnecting-chip">
        <div className="ikon-reconnecting-spinner" />
        <span>{t('connection.reconnecting')}</span>
      </div>
    </div>
  );
}

function OfflineOverlay({ isServerFull, isSessionExpired, isStartupFailed }: { isServerFull: boolean; isSessionExpired: boolean; isStartupFailed: boolean }) {
  const { t } = useI18n();

  const scope = isServerFull ? 'serverFull' : isSessionExpired ? 'sessionExpired' : isStartupFailed ? 'startupFailed' : 'offline';
  const message = t(`connection.${scope}.message`);
  const recovery = getConnectionRecovery({ isSessionExpired });
  return (
    <div className="ikon-offline-overlay">
      <div className="ikon-offline-chip">
        <span className="ikon-offline-title">{t(`connection.${scope}.title`)}</span>
        {message && <span className="ikon-offline-message">{message}</span>}
        <button type="button" className="ikon-offline-action" onClick={recovery.run}>
          <span className="ikon-offline-action-label">{t(`connection.${scope}.action`)}</span>
        </button>
      </div>
    </div>
  );
}

function AccessDeniedScreen({ reason }: { reason: string }) {
  const { t } = useI18n();
  const auth = useAuthOptional();
  const message = t('connection.accessDenied.message');
  return (
    <main className="ikon-surface ikon-auth-screen">
      <section className="ikon-auth-container">
        <h1 className="ikon-auth-title">{t('connection.accessDenied.title')}</h1>
        {message && <p className="ikon-auth-subtitle">{message}</p>}
        <div className="ikon-auth-error">{reason}</div>
        {auth && (
          <button type="button" className="ikon-auth-email-button" onClick={auth.logout}>
            {t('connection.accessDenied.backToLogin')}
          </button>
        )}
      </section>
    </main>
  );
}

export default App;
