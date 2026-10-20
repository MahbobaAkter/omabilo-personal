import { Fragment, useId, useRef, useState, type ReactNode } from 'react';
import { IKON_PARAM_OAUTH_PROVIDER } from '@ikonai/sdk';
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

// A provider sign-in that fails comes back from the auth service as ?error=<code>&<provider param>.
// The auth provider removes both from the address bar on its first effect, so they are read here,
// when the module loads, to say on the recovery screen which sign-in failed and why.
interface CallbackFailure {
  code: string;
  provider: string | null;
}

const callbackFailure: CallbackFailure | null = (() => {
  try {
    const params = new URLSearchParams(window.location.search);
    const code = params.get('error');
    return code ? { code, provider: params.get(IKON_PARAM_OAUTH_PROVIDER) } : null;
  } catch {
    return null;
  }
})();

export function AuthGuard({ children, config }: AuthGuardProps) {
  const { isCheckingAuth, shouldRenderChildren, isLoginPrompt, dismissLoginPrompt, loginPromptReason } = useAuthGuard({
    config,
    guestUrlParam: 'guest',
  });
  const [errorScope, setErrorScope] = useState<ErrorScope | null>(null);
  const [recoveryOpen, setRecoveryOpen] = useState(callbackFailure !== null);
  const initialCheckDoneRef = useRef(false);

  if (!isCheckingAuth) {
    initialCheckDoneRef.current = true;
  }

  // Shown whether or not the app lets guests in: a guest app would otherwise drop the failure silently
  if (recoveryOpen && callbackFailure) {
    return <RecoveryScreen config={config} failure={callbackFailure} onClose={() => setRecoveryOpen(false)} />;
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
    <main className="ikon-surface ikon-auth-screen omabilo-auth">
      {/* Omabilo's brand panel beside the card on wide screens; presentation only, no controls */}
      <aside className="omabilo-auth-brand">
        <div className="omabilo-auth-wordmark">
          <BrandMark />
          <span>Omabilo</span>
        </div>
        <p className="omabilo-auth-eyebrow">Your financial companion</p>
        <p className="omabilo-auth-headline">
          A clearer way to manage <em>your money.</em>
        </p>
        <p className="omabilo-auth-lead">Understand your bills, plan around payday and move towards your goals, all in one place.</p>
        <ul className="omabilo-auth-points">
          <li>
            <strong>Understand your bills</strong>
            <span>Bills explained in plain language</span>
          </li>
          <li>
            <strong>Plan with confidence</strong>
            <span>See upcoming costs and reminders</span>
          </li>
          <li>
            <strong>Work towards your goals</strong>
            <span>Build better financial habits</span>
          </li>
        </ul>
      </aside>

      <section className="ikon-auth-container">
        <div className="omabilo-auth-card-brand">
          <BrandMark />
          <span>Omabilo</span>
        </div>
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

        <p className="omabilo-auth-note">{t('auth.note')}</p>

        {isLoginPrompt && (
          <button type="button" className="ikon-auth-dismiss" onClick={onDismiss}>
            {t('auth.dismiss')}
          </button>
        )}
      </section>
    </main>
  );
}

const OAUTH_PROVIDERS: Partial<Record<string, string>> = {
  google: 'Google',
  apple: 'Apple',
  facebook: 'Facebook',
  microsoft: 'Microsoft',
  signicat: 'eID',
};

// The SDK's own sentences for the codes the auth service sends back (@ikonai/sdk-react-ui)
const KNOWN_FAILURES: Partial<Record<string, string>> = {
  authentication_failed: 'Sign-in did not complete. Please try again.',
  invalid_redirect_url: 'Sign-in failed because the return address was not accepted.',
  login_method_not_allowed: 'This app does not offer that sign-in method. Please choose another way to sign in.',
  account_exists_unverified_email:
    'This email address already has an account on this app that uses a different sign-in method. For security, it can’t be connected automatically. Sign in the way this account was originally created.',
  account_holds_email_unverified:
    'An account already uses this email address, but the address has not been verified for that account. Sign in the way you did before, then verify your email address in that account.',
  sso_link_expired: 'The sign-in link expired. Please try again.',
};

interface RecoveryScreenProps {
  config: AuthConfig;
  failure: CallbackFailure;
  onClose: () => void;
}

// After a provider sign-in fails: says what the auth service reported and offers the way back in
function RecoveryScreen({ config, failure, onClose }: RecoveryScreenProps) {
  const { t } = useI18n();
  const { state, login } = useAuth();
  const [helpOpen, setHelpOpen] = useState(false);
  const [busy, setBusy] = useState<'return' | 'retry' | null>(null);
  const helpId = useId();

  const providerName = failure.provider ? OAUTH_PROVIDERS[failure.provider] : undefined;
  // Retry goes straight back to the same provider only when this app still offers it
  const retryProvider = providerName && config.methods.includes(failure.provider as LoginMethod) ? (failure.provider as LoginMethod) : null;
  // The SDK turns the code into a sentence; for a code it does not know it passes the code through.
  // A guest sign-in that follows can clear that sentence first, so the SDK's wording is kept here too.
  const reported = state.error ? formatAuthError(state.error) : null;
  const explanation = (reported && reported !== failure.code ? reported : null) ?? KNOWN_FAILURES[failure.code] ?? null;

  const handleReturn = () => {
    setBusy('return');
    // The auth provider has already taken the error out of the address, so this is the app's own page
    window.location.assign(window.location.origin + window.location.pathname);
  };

  const handleRetry = async () => {
    if (!retryProvider) {
      onClose();
      return;
    }

    setBusy('retry');

    try {
      await login(retryProvider);
    } finally {
      // A redirect leaves the page before this runs; a popup, or a sign-in that did not start, lands here
      setBusy(null);
    }
  };

  return (
    <main className="ikon-surface omabilo-auth omabilo-recovery">
      <div className="omabilo-recovery-leaves" aria-hidden="true">
        {[0, 1, 2, 3].map((i) => (
          <svg key={i} className={`omabilo-recovery-leaf omabilo-recovery-leaf-${i}`} viewBox="0 0 24 24" width="24" height="24">
            <path d="M4 20C4 10 10 4 20 4c0 10-6 16-16 16z" fill="currentColor" />
            <path d="M4 20 15 9" stroke="rgba(2, 44, 34, 0.35)" strokeWidth="1.2" strokeLinecap="round" fill="none" />
          </svg>
        ))}
      </div>

      <section className="omabilo-recovery-card" aria-labelledby={`${helpId}-title`}>
        <SproutMascot />

        <h1 id={`${helpId}-title`} className="omabilo-recovery-title">
          {t('auth.recovery.title')}
        </h1>
        <p className="omabilo-recovery-message">{providerName ? t('auth.recovery.messageProvider', { provider: providerName }) : t('auth.recovery.message')}</p>

        <div className="omabilo-recovery-actions">
          <button type="button" className="omabilo-recovery-primary" onClick={handleReturn} disabled={busy !== null} aria-busy={busy === 'return'}>
            {busy === 'return' ? <Spinner /> : null}
            <span>{busy === 'return' ? t('auth.recovery.returning') : t('auth.recovery.return')}</span>
            {busy === 'return' ? null : <ArrowIcon />}
          </button>
          <button type="button" className="omabilo-recovery-secondary" onClick={() => void handleRetry()} disabled={busy !== null} aria-busy={busy === 'retry'}>
            {busy === 'retry' ? <Spinner /> : <RetryIcon />}
            <span>{busy === 'retry' ? t('auth.recovery.retrying') : retryProvider ? t('auth.recovery.retryProvider', { provider: providerName! }) : t('auth.recovery.retry')}</span>
          </button>
        </div>

        <div className="omabilo-recovery-help">
          <button
            type="button"
            className="omabilo-recovery-help-toggle"
            aria-expanded={helpOpen}
            aria-controls={`${helpId}-panel`}
            onClick={() => setHelpOpen((open) => !open)}
          >
            <span>{t('auth.recovery.help')}</span>
            <svg className="omabilo-recovery-chevron" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
              <path d="m6 9 6 6 6-6" />
            </svg>
          </button>

          <div id={`${helpId}-panel`} className="omabilo-recovery-panel" data-open={helpOpen} aria-hidden={!helpOpen}>
            <div className="omabilo-recovery-panel-inner">
              <div className="omabilo-recovery-details">
                <h2>{t('auth.recovery.whatHappened')}</h2>
                {explanation ? <p>{explanation}</p> : null}
                <p className="omabilo-recovery-code">
                  {t('auth.recovery.code')}: <code>{failure.code}</code>
                </p>
                <h2>{t('auth.recovery.nextSteps')}</h2>
                <ul>
                  <li>{t('auth.recovery.stepRetry')}</li>
                  <li>{t('auth.recovery.stepOther')}</li>
                  <li>{t('auth.recovery.stepSupport')}</li>
                </ul>
              </div>
            </div>
          </div>
        </div>
      </section>
    </main>
  );
}

// Omabilo's sprout as a soft, shaded character; bobs a few times, then rests
function SproutMascot() {
  return (
    <div className="omabilo-recovery-mascot" aria-hidden="true">
      <svg viewBox="0 0 160 150" width="150" height="140">
        <defs>
          <radialGradient id="omabilo-sprout-body" cx="38%" cy="32%" r="75%">
            <stop offset="0%" stopColor="#c6f6a6" />
            <stop offset="55%" stopColor="#7fd65a" />
            <stop offset="100%" stopColor="#3f9a35" />
          </radialGradient>
          <linearGradient id="omabilo-sprout-leaf" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stopColor="#c9f7a8" />
            <stop offset="100%" stopColor="#5cbf45" />
          </linearGradient>
        </defs>
        <ellipse cx="80" cy="140" rx="44" ry="6" fill="rgba(0, 0, 0, 0.28)" />
        <path d="M80 70c-2-14 0-26 6-36" stroke="#5cbf45" strokeWidth="5" strokeLinecap="round" fill="none" />
        <path d="M86 36c6-20 26-30 44-26-2 20-20 34-44 26z" fill="url(#omabilo-sprout-leaf)" />
        <path d="M84 40c-12-14-32-16-46-6 8 16 28 20 46 6z" fill="url(#omabilo-sprout-leaf)" />
        <circle cx="80" cy="100" r="40" fill="url(#omabilo-sprout-body)" />
        <ellipse cx="66" cy="80" rx="12" ry="7" fill="rgba(255, 255, 255, 0.35)" />
        <ellipse cx="66" cy="102" rx="4.5" ry="5.5" fill="#123d1c" />
        <ellipse cx="94" cy="102" rx="4.5" ry="5.5" fill="#123d1c" />
        <circle cx="67.5" cy="100" r="1.5" fill="#ffffff" />
        <circle cx="95.5" cy="100" r="1.5" fill="#ffffff" />
        <path d="M60 92l9-3M100 92l-9-3" stroke="#123d1c" strokeWidth="2" strokeLinecap="round" />
        <path d="M74 118c4-4 8-4 12 0" stroke="#123d1c" strokeWidth="2.4" strokeLinecap="round" fill="none" />
        <ellipse cx="57" cy="112" rx="5" ry="3" fill="rgba(244, 114, 182, 0.35)" />
        <ellipse cx="103" cy="112" rx="5" ry="3" fill="rgba(244, 114, 182, 0.35)" />
      </svg>
    </div>
  );
}

function Spinner() {
  return <span className="omabilo-recovery-spinner" aria-hidden="true" />;
}

function ArrowIcon() {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M5 12h14M13 6l6 6-6 6" />
    </svg>
  );
}

function RetryIcon() {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4" />
    </svg>
  );
}

// The sprout in a mint tile, as on the landing page
function BrandMark() {
  return (
    <span className="omabilo-auth-mark" aria-hidden="true">
      <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M7 20h10" />
        <path d="M10 20c5.5-2.5.8-6.4 3-10" />
        <path d="M9.5 9.4c1.1.8 1.8 2.2 2.3 3.7-2 .4-3.5.4-4.8-.3-1.2-.6-2.3-1.9-3-4.2 2.8-.5 4.4 0 5.5.8z" />
        <path d="M14.1 6a7 7 0 0 0-1.1 4c1.9-.1 3.3-.6 4.3-1.4 1-1 1.6-2.3 1.7-4.6-2.7.1-4 1-4.9 2z" />
      </svg>
    </span>
  );
}
