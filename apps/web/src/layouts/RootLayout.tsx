import { Outlet } from "react-router";

export function RootLayout() {
  return (
    <div>
      <header>
        <h1>Cordia</h1>
      </header>
      <main>
        <Outlet />
      </main>
    </div>
  );
}
