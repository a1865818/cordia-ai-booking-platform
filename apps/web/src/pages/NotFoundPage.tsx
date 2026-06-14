import { useLocation } from "react-router";

export function NotFoundPage() {
  const location = useLocation();
  return (
    <section>
      <h2>404</h2>
      <p>No route matches {location.pathname}</p>
    </section>
  );
}
