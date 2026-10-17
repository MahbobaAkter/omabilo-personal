/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_LOCAL_IKON_SERVER_ENABLED?: string;
  readonly VITE_LOCAL_IKON_SERVER_HOST?: string;
  readonly VITE_LOCAL_IKON_SERVER_PORT?: string;

  readonly VITE_SERVER_PORT?: string;
  readonly VITE_SERVER_FRONTEND_HOST?: string;
  readonly VITE_SERVER_FRONTEND_PORT?: string;
  readonly VITE_SERVER_FRONTEND_URL?: string;
  readonly VITE_SERVER_OPEN_BROWSER?: string;
  readonly VITE_SERVER_TUNNELED?: string;

  readonly VITE_IKON_AUTH_METHODS?: string;
  readonly VITE_IKON_AUTH_REQUIRE_SIGN_IN?: string;
  readonly VITE_IKON_AUTH_SPACE_ID?: string;

  readonly VITE_WAIT_FOR_EXTERNAL_CONNECT_URL?: string;

  readonly VITE_IKON_BACKEND_URL?: string;
  readonly VITE_IKON_AUTH_URL?: string;
  readonly VITE_IKON_BACKEND_TYPE?: string;

  readonly VITE_IKON_SERVER_CERT_PATH?: string;
  readonly VITE_IKON_SERVER_KEY_PATH?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
