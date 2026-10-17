import { Fragment, useRef, useState, type ReactNode } from 'react';
import { useAuth, useAuthGuard, type AuthConfig, type LoginMethod } from '@ikonai/sdk-react-ui';
import { useI18n } from '../i18n/i18n';
import './auth.css';
import { EmailLoginForm } from './email-login-form';
import { LoginButton, RegisterPasskeyButton } from './login-button';
import { SsoSignIn } from './sso-sign-in';

type ErrorScope = 'primary' | 'passkey' | 'email' | 'guest';

export interface AuthGuardProps {
  children: ReactNode;
  config: AuthConfig;
}

export function formatAuthError(error: string): string {
  const trimmed = error.trim();

  try {
    const parsed = JSON.parse(trimmed) as { message?: unknown };

    if (typeof parsed.message === 'string' && parsed.message.length > 0) {
      return parsed.message;
    }
  } catch {
    /* not JSON */
  }

  return trimmed;
}

export function AuthGuard({ children, config }: AuthGuardProps) {
  const { isCheckingAuth, shouldRenderChildren, isLoginPrompt, dismissLoginPrompt, loginPromptReason } = useAuthGuard({
    config,
    guestUrlParam: 'guest',
  });
  const [errorScope, setErrorScope] = useState<ErrorScope | null>(null);
  const initialCheckDoneRef = useRef(false);

  if (!isCheckingAuth) {
    initialCheckDoneRef.current = true;
  }

  if (isCheckingAuth && !initialCheckDoneRef.current) {
    return null;
  }

  if (!shouldRenderChildren) {
    return (
      <AuthScreen
        config={config}
        errorScope={errorScope}
        setErrorScope={setErrorScope}
        isLoginPrompt={isLoginPrompt}
        onDismiss={dismissLoginPrompt}
        loginPromptReason={loginPromptReason}
      />
    );
  }

  return <>{children}</>;
}

interface AuthScreenProps {
  config: AuthConfig;
  errorScope: ErrorScope | null;
  setErrorScope: (scope: ErrorScope) => void;
  isLoginPrompt: boolean;
  onDismiss: () => void;
  loginPromptReason: string | null;
}

function AuthScreen({ config, errorScope, setErrorScope, isLoginPrompt, onDismiss, loginPromptReason }: AuthScreenProps) {
  const { t } = useI18n();
  const { state } = useAuth();

  const primaryMethods = config.methods.filter(
    (m): m is Exclude<LoginMethod, 'email' | 'guest' | 'global' | 'passkey'> => m !== 'email' && m !== 'guest' && m !== 'global' && m !== 'passkey' && m !== 'sso',
  );
  const hasSso = config.methods.includes('sso');
  const hasPasskey = config.methods.includes('passkey');
  const hasEmail = config.methods.includes('email');
  const guestProvider = isLoginPrompt ? null : config.methods.includes('global') ? ('global' as const) : config.methods.includes('guest') ? ('guest' as const) : null;

  const errorFor = (scope: ErrorScope) =>
    state.error && (errorScope === scope || (errorScope === null && scope === 'primary')) ? (
      <div className="ikon-auth-error">{formatAuthError(state.error)}</div>
    ) : null;

  const sections: { key: string; content: ReactNode }[] = [];

  if (hasPasskey) {
    sections.push({
      key: 'passkey',
      content: (
        <>
          {errorFor('passkey')}
          <div className="ikon-auth-buttons">
            <LoginButton provider="passkey" disabled={state.isLoading} onAttempt={() => setErrorScope('passkey')} />
            <RegisterPasskeyButton disabled={state.isLoading} onAttempt={() => setErrorScope('passkey')} />
          </div>
        </>
      ),
    });
  }

  if (primaryMethods.length > 0) {
    sections.push({
      key: 'providers',
      content: (
        <div className="ikon-auth-buttons">
          {primaryMethods.map((method) => (
            <LoginButton key={method} provider={method} disabled={state.isLoading} onAttempt={() => setErrorScope('primary')} />
          ))}
        </div>
      ),
    });
  }

  if (hasSso) {
    sections.push({ key: 'sso', content: <SsoSignIn config={config} disabled={state.isLoading} onAttempt={() => setErrorScope('primary')} /> });
  }

  if (hasEmail) {
    sections.push({ key: 'email', content: <EmailLoginForm config={config} onAttempt={() => setErrorScope('email')} /> });
  }

  if (guestProvider) {
    sections.push({
      key: 'guest',
      content: (
        <>
          {errorFor('guest')}
          <LoginButton provider={guestProvider} disabled={state.isLoading} onAttempt={() => setErrorScope('guest')} />
        </>
      ),
    });
  }

  return (
    <main className="ikon-surface ikon-auth-screen">
      <section className="ikon-auth-container">
        <h1 className="ikon-auth-title">{t('auth.welcome.title')}</h1>
        {/* Whoever raised the prompt said why; that beats the generic welcome, which tells someone
            sent here by another application nothing about what they are about to agree to. */}
        <p className="ikon-auth-subtitle">{loginPromptReason ?? t('auth.welcome.subtitle')}</p>

        {errorFor('primary')}

        {sections.map((section, index) => (
          <Fragment key={section.key}>
            {index > 0 && (
              <div className="ikon-auth-divider">
                <span>{t('auth.divider')}</span>
              </div>
            )}
            {section.content}
          </Fragment>
        ))}

        {isLoginPrompt && (
          <button type="button" className="ikon-auth-dismiss" onClick={onDismiss}>
            {t('auth.dismiss')}
          </button>
        )}
      </section>
    </main>
  );
}
