/// <reference types="vite/client" />

declare global {
  interface Window {
    APP_CONFIG?: {
      API_URL?: string;
    };
  }
}

export {};
