import { useEffect, useState } from "react";
import { AvailabilityMode } from "@cordia/shared-schemas";
import { fetchApi, isApiError } from "../api/client";

interface HealthResponse {
  status: string;
}

export function HomePage() {
  const [health, setHealth] = useState<string>("checking…");

  useEffect(() => {
    fetchApi<HealthResponse>("/api/health")
      .then((res) => setHealth(res.status))
      .catch((err) => setHealth(isApiError(err) ? `error ${err.status}` : "unreachable"));
  }, []);

  return (
    <section>
      <p>API health: {health}</p>
      {/* AvailabilityMode is imported from the generated shared-schemas package,
          proving the Pydantic -> JSON Schema -> TypeScript pipeline works. */}
      <p>Default availability mode: {AvailabilityMode.Confirmed}</p>
    </section>
  );
}
