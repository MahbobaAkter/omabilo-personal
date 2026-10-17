import { I18nProvider as BaseI18nProvider, useI18n as useBaseI18n, type I18nContextValue } from '@ikonai/sdk-react-ui';
import type { ReactNode } from 'react';

export interface Translations {
  'connection.connecting': string;
  'connection.reconnecting': string;
  'connection.offline.title': string;
  'connection.offline.message': string;
  'connection.offline.action': string;
  'connection.serverFull.title': string;
  'connection.serverFull.message': string;
  'connection.serverFull.action': string;
  'connection.sessionExpired.title': string;
  'connection.sessionExpired.message': string;
  'connection.sessionExpired.action': string;
  'connection.startupFailed.title': string;
  'connection.startupFailed.message': string;
  'connection.startupFailed.action': string;
  'auth.welcome.title': string;
  'auth.welcome.subtitle': string;
  'auth.divider': string;
  'auth.dismiss': string;
  'auth.email.placeholder': string;
  'auth.email.submit': string;
  'auth.email.submitting': string;
  'auth.email.sent.message': string;
  'auth.email.code.placeholder': string;
  'auth.email.code.submit': string;
  'auth.email.code.submitting': string;
  'auth.email.code.back': string;
  'auth.email.error.empty': string;
  'auth.email.error.noSpaceId': string;
  'auth.email.error.sendFailed': string;
  'auth.email.error.codeEmpty': string;
  'auth.email.error.verifyFailed': string;
  'connection.accessDenied.title': string;
  'connection.accessDenied.message': string;
  'connection.accessDenied.backToLogin': string;
  'auth.button.guest': string;
  'auth.button.provider': string;
  'auth.button.registerPasskey': string;
  'auth.sso.placeholder': string;
  'auth.sso.submit': string;
  'auth.sso.submitting': string;
}

export type TranslationKey = keyof Translations;

export interface I18nProviderProps {
  children: ReactNode;
  translations: Record<string, Translations>;
  defaultLanguage?: string;
  detectLanguage?: boolean;
}

export function I18nProvider({ children, translations, defaultLanguage, detectLanguage }: I18nProviderProps) {
  return (
    <BaseI18nProvider translations={translations} defaultLanguage={defaultLanguage} detectLanguage={detectLanguage}>
      {children}
    </BaseI18nProvider>
  );
}

export function useI18n(): I18nContextValue<Translations> {
  return useBaseI18n<Translations>();
}
