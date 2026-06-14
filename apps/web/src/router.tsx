import { createBrowserRouter } from "react-router";
import { RootLayout } from "./layouts/RootLayout";
import { HomePage } from "./pages/HomePage";
import { NotFoundPage } from "./pages/NotFoundPage";

// Derive the router basename from the Vite base path (import.meta.env.BASE_URL),
// so the SPA works when served under a prefix (VITE_BASE_PATH). Strip the
// trailing slash Vite includes, except for root ("/").
const basename = import.meta.env.BASE_URL.replace(/\/$/, "") || "/";

export const router = createBrowserRouter(
  [
    {
      path: "/",
      element: <RootLayout />,
      children: [
        { index: true, element: <HomePage /> },
        { path: "*", element: <NotFoundPage /> },
      ],
    },
  ],
  { basename }
);
