import { describe, expect, it } from "../../test/vitest-compat";
import { deliveryLabel, eventLabel } from "./notificationDelivery";

describe("deliveryLabel", () => {
  it("reports delivery only when the provider confirmed it", () => {
    expect(deliveryLabel("delivered")).toEqual({ text: "Delivered", tone: "good" });
  });

  it("does not present a provider hand-off as delivery", () => {
    const label = deliveryLabel("accepted");
    expect(label.tone).toBe("pending");
    expect(label.text).not.toMatch(/delivered/i);
  });

  it("marks terminal failures as failures", () => {
    expect(deliveryLabel("failed_permanent").tone).toBe("bad");
    expect(deliveryLabel("invalid_recipient").tone).toBe("bad");
  });

  it("keeps a transient failure out of the failed bucket, because it will be retried", () => {
    expect(deliveryLabel("failed_transient").tone).toBe("pending");
  });

  it("never turns a state it does not know into a success", () => {
    const label = deliveryLabel("some_future_state");
    expect(label.tone).toBe("muted");
    expect(label.text).toBe("some future state");
  });

  it("does not invent a state for a missing one", () => {
    expect(deliveryLabel(undefined)).toEqual({ text: "Unknown", tone: "muted" });
  });
});

describe("eventLabel", () => {
  it("names the confirmation", () => {
    expect(eventLabel("booking_confirmed")).toBe("Booking confirmation");
  });

  it("falls back to the raw event rather than a generic name", () => {
    expect(eventLabel("brand_new_event")).toBe("brand new event");
  });
});
