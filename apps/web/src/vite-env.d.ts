/// <reference types="vite/client" />

// NOTE: this file must stay a *global script*, not a module. A top-level `import` or `export`
// — including a bare `export {}` — scopes every interface below to the module, so the
// `ImportMetaEnv`/`ImportMeta` declarations stop augmenting the globals and silently do
// nothing: every `import.meta.env.VITE_*` read falls back to `vite/client`'s
// `[key: string]: any`. `Window` is declared directly for the same reason (`declare global` is
// only needed from inside a module).

interface ImportMetaEnv {
  /** Dev tenant sent as `X-Tenant-Id` (spec-15 signed context replaces this later). */
  readonly VITE_DEV_TENANT_ID?: string;
  /** Dev operator identity sent as `X-Actor` on staff writes. */
  readonly VITE_DEV_ACTOR?: string;
  /** Dev operator role sent as `X-Actor-Role` alongside `X-Actor`. */
  readonly VITE_DEV_ACTOR_ROLE?: string;
  /** Dev-server proxy target for `/api` (see vite.config.ts). */
  readonly VITE_API_PROXY_TARGET?: string;
  /** Base path when the SPA is served under a prefix. */
  readonly VITE_BASE_PATH?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

interface Window {
  APP_CONFIG?: {
    API_URL?: string;
    TENANT_ID?: string;
    ACTOR?: string;
    ACTOR_ROLE?: string;
  };
}
