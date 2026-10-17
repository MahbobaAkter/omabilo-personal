import { type AuthConfig, useSsoSignIn } from '@ikonai/sdk-react-ui';
import { type FormEvent, useState } from 'react';
import { useI18n } from '../i18n/i18n';

interface SsoSignInProps {
  config: AuthConfig;
  disabled?: boolean;
  onAttempt?: () => void;
}

const ORGANISATION_ICON = (
  <svg viewBox="0 0 24 24" width="20" height="20" fill="currentColor">
    <path d="M12 7V3H2v18h20V7H12zM6 19H4v-2h2v2zm0-4H4v-2h2v2zm0-4H4V9h2v2zm0-4H4V5h2v2zm4 12H8v-2h2v2zm0-4H8v-2h2v2zm0-4H8V9h2v2zm0-4H8V5h2v2zm10 12h-8v-2h2v-2h-2v-2h2v-2h-2V9h8v10zm-2-8h-2v2h2v-2zm0 4h-2v2h2v-2z" />
  </svg>
);

export function SsoSignIn({ config, disabled, onAttempt }: SsoSignInProps) {
  const { t } = useI18n();
  const { hostConnections, isDiscovering, discoverError, discover, signIn } = useSsoSignIn(config);
  const [email, setEmail] = useState('');

  const handleDiscover = async (event: FormEvent) => {
    event.preventDefault();

    if (!email.trim()) {
      return;
    }

    onAttempt?.();
    await discover(email);
  };

  return (
    <>
      {hostConnections.length > 0 && (
        <div className="ikon-auth-buttons">
          {hostConnections.map((connection) => (
            <button
              key={connection.connectionId}
              type="button"
              className="ikon-auth-login-button ikon-auth-login-button-sso"
              disabled={disabled}
              onClick={() => {
                onAttempt?.();
                void signIn(connection.connectionId);
              }}
            >
              {ORGANISATION_ICON}
              {t('auth.button.provider', { provider: connection.name })}
            </button>
          ))}
        </div>
      )}

      <form className="ikon-auth-email-form" onSubmit={handleDiscover}>
        {discoverError && <div className="ikon-auth-error">{discoverError}</div>}
        <input
          type="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          placeholder={t('auth.sso.placeholder')}
          className="ikon-auth-email-input"
          disabled={disabled || isDiscovering}
          autoComplete="email"
        />
        <button type="submit" className="ikon-auth-email-button" disabled={disabled || isDiscovering}>
          {isDiscovering ? (
            <>
              <span className="ikon-auth-email-spinner" />
              {t('auth.sso.submitting')}
            </>
          ) : (
            t('auth.sso.submit')
          )}
        </button>
      </form>
    </>
  );
}
