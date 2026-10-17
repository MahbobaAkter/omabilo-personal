import { type AuthConfig, useAuth } from '@ikonai/sdk-react-ui';
import { type FormEvent, useState } from 'react';
import { useI18n } from '../i18n/i18n';
import { formatAuthError } from './auth-guard';

type Step = 'email' | 'code';
type Status = 'idle' | 'sending' | 'verifying';

function extractErrorMessage(err: unknown): string | null {
  if (err instanceof Error && err.message) {
    return formatAuthError(err.message);
  }

  if (typeof err === 'string' && err) {
    return formatAuthError(err);
  }

  return null;
}

interface EmailLoginFormProps {
  config: AuthConfig;
  onAttempt?: () => void;
}

export function EmailLoginForm({ config, onAttempt }: EmailLoginFormProps) {
  const { t } = useI18n();
  const { requestEmailCode, submitEmailCode } = useAuth();

  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');
  const [step, setStep] = useState<Step>('email');
  const [status, setStatus] = useState<Status>('idle');
  const [errorMessage, setErrorMessage] = useState('');

  const handleSendCode = async (e: FormEvent) => {
    e.preventDefault();
    const trimmed = email.trim();

    if (!trimmed) {
      setErrorMessage(t('auth.email.error.empty'));
      return;
    }
    if (!config.spaceId) {
      setErrorMessage(t('auth.email.error.noSpaceId'));
      return;
    }

    setStatus('sending');
    setErrorMessage('');
    onAttempt?.();

    try {
      await requestEmailCode(trimmed);
      setEmail(trimmed);
      setCode('');
      setStep('code');
    } catch (err) {
      setErrorMessage(extractErrorMessage(err) ?? t('auth.email.error.sendFailed'));
    } finally {
      setStatus('idle');
    }
  };

  const handleSubmitCode = async (e: FormEvent) => {
    e.preventDefault();
    const trimmed = code.trim();
    if (!trimmed) {
      setErrorMessage(t('auth.email.error.codeEmpty'));
      return;
    }

    setStatus('verifying');
    setErrorMessage('');
    onAttempt?.();

    try {
      await submitEmailCode(email, trimmed);
    } catch (err) {
      setErrorMessage(extractErrorMessage(err) ?? t('auth.email.error.verifyFailed'));
    } finally {
      setStatus('idle');
    }
  };

  const backToEmail = () => {
    setStep('email');
    setCode('');
    setErrorMessage('');
  };

  if (step === 'code') {
    return (
      <form className="ikon-auth-email-form" onSubmit={handleSubmitCode}>
        {errorMessage && <div className="ikon-auth-error">{errorMessage}</div>}
        <p className="ikon-auth-email-code-hint">
          {t('auth.email.sent.message')} <strong>{email}</strong>
        </p>
        <input
          type="text"
          value={code}
          onChange={(event) => setCode(event.target.value.toUpperCase())}
          placeholder={t('auth.email.code.placeholder')}
          className="ikon-auth-code-input"
          disabled={status === 'verifying'}
          autoComplete="one-time-code"
          inputMode="text"
          autoFocus
        />
        <button type="submit" className="ikon-auth-email-button" disabled={status === 'verifying'}>
          {status === 'verifying' ? (
            <>
              <span className="ikon-auth-email-spinner" />
              {t('auth.email.code.submitting')}
            </>
          ) : (
            t('auth.email.code.submit')
          )}
        </button>
        <button type="button" className="ikon-auth-email-resend" onClick={backToEmail} disabled={status === 'verifying'}>
          {t('auth.email.code.back')}
        </button>
      </form>
    );
  }

  return (
    <form className="ikon-auth-email-form" onSubmit={handleSendCode}>
      {errorMessage && <div className="ikon-auth-error">{errorMessage}</div>}
      <input
        type="email"
        value={email}
        onChange={(event) => setEmail(event.target.value)}
        placeholder={t('auth.email.placeholder')}
        className="ikon-auth-email-input"
        disabled={status === 'sending'}
        autoComplete="email"
      />
      <button type="submit" className="ikon-auth-email-button" disabled={status === 'sending'}>
        {status === 'sending' ? (
          <>
            <span className="ikon-auth-email-spinner" />
            {t('auth.email.submitting')}
          </>
        ) : (
          t('auth.email.submit')
        )}
      </button>
    </form>
  );
}
