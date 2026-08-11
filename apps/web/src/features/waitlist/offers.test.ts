/**
 * Which waitlist offers can actually be answered, and what the row says about the rest.
 *
 * `acceptOffer`/`declineOffer` existed from the start and were called from nowhere: the
 * enrollment projection reported that an offer existed but not its id, so there was nothing
 * to address. With `latest_offer_id` exposed, the remaining question is which offers are
 * genuinely actionable — offering a button that is certain to 409 is worse than offering none.
 */

import { describe, expect, it } from "vitest";
import { isOfferActionable, offerExpiryLabel, toWaitlistItem } from "./adapters";
import type { WaitlistEnrollmentSummary } from "./api";

const NOW = new Date("2026-08-03T12:00:00Z").getTime();

function enrollment(overrides: Partial<WaitlistEnrollmentSummary> = {}): WaitlistEnrollmentSummary {
  return {
    enrollment_id: "enr_1",
    customer_ref: "cust_1",
    service_id: "gel",
    location_id: "loc_1",
    party_size: 2,
    acceptable_from: "2026-08-06T09:00:00Z",
    acceptable_to: "2026-08-06T13:00:00Z",
    auto_book_authorized: false,
    cancelled: false,
    created_at: "2026-08-01T09:00:00Z",
    latest_offer_id: null,
    latest_offer_status: null,
    latest_offer_expires_at: null,
    ...overrides,
  } as WaitlistEnrollmentSummary;
}

describe("isOfferActionable", () => {
  it("accepts only a live, unexpired offer", () => {
    expect(isOfferActionable("offered", "2026-08-03T12:10:00Z", NOW)).toBe(true);
  });

  it("refuses an offer that has not reached the guest yet", () => {
    expect(isOfferActionable("pending", null, NOW)).toBe(false);
    expect(isOfferActionable("matched", null, NOW)).toBe(false);
  });

  it("refuses a terminal offer", () => {
    expect(isOfferActionable("accepted", null, NOW)).toBe(false);
    expect(isOfferActionable("declined", null, NOW)).toBe(false);
    expect(isOfferActionable("expired", null, NOW)).toBe(false);
  });

  it("refuses an offer whose expiry has passed even if the row still says offered", () => {
    // The sweeper may not have run. Rendering Accept here guarantees a 409.
    expect(isOfferActionable("offered", "2026-08-03T11:59:00Z", NOW)).toBe(false);
  });

  it("treats an offer with no expiry as live", () => {
    expect(isOfferActionable("offered", null, NOW)).toBe(true);
  });

  it("refuses nothing at all", () => {
    expect(isOfferActionable(null, null, NOW)).toBe(false);
  });
});

describe("offerExpiryLabel", () => {
  it("counts down in minutes, then hours", () => {
    expect(offerExpiryLabel("2026-08-03T12:07:00Z", NOW)).toBe("expires in 7 min");
    expect(offerExpiryLabel("2026-08-03T14:00:00Z", NOW)).toBe("expires in 2 hr");
    expect(offerExpiryLabel("2026-08-03T12:00:30Z", NOW)).toBe("expires in under a minute");
  });

  it("says expired rather than counting backwards", () => {
    expect(offerExpiryLabel("2026-08-03T11:00:00Z", NOW)).toBe("expired");
  });

  it("is empty when there is no offer", () => {
    expect(offerExpiryLabel(null, NOW)).toBe("");
    expect(offerExpiryLabel("nonsense", NOW)).toBe("");
  });
});

describe("toWaitlistItem offer fields", () => {
  it("carries the offer RAW, leaving actionability to the screen's ticking clock", () => {
    // Deciding it here froze it at fetch time: a row kept a live Accept button and a stale
    // countdown for as long as the list went un-refetched, and the click 409'd.
    const live = toWaitlistItem(
      enrollment({
        latest_offer_id: "off_1",
        latest_offer_status: "offered",
        latest_offer_expires_at: "2026-08-03T12:10:00Z",
      })
    );

    expect(live.offerId).toBe("off_1");
    expect(live.offerStatus).toBe("offered");
    expect(live.offerExpiresAt).toBe("2026-08-03T12:10:00Z");
  });

  it("carries a terminal offer's id too — the screen decides it cannot be answered", () => {
    const answered = toWaitlistItem(
      enrollment({ latest_offer_id: "off_1", latest_offer_status: "accepted" })
    );

    expect(answered.offerStatus).toBe("accepted");
    expect(isOfferActionable(answered.offerStatus, answered.offerExpiresAt, NOW)).toBe(false);
  });

  it("carries the raw enrollment fields so Book now can prefill", () => {
    const row = toWaitlistItem(enrollment());

    expect(row.serviceId).toBe("gel");
    expect(row.partySize).toBe(2);
    expect(row.acceptableFrom).toBe("2026-08-06T09:00:00Z");
  });
});

describe("the label and the guard agree", () => {
  it("never says expired while the offer is still answerable", () => {
    // Rounding to the nearest minute made them disagree: an offer 25 seconds out was actionable
    // but already labelled "expired", so Accept rendered beside the word expired.
    const expiresAt = "2026-08-03T12:00:25Z";

    expect(isOfferActionable("offered", expiresAt, NOW)).toBe(true);
    expect(offerExpiryLabel(expiresAt, NOW)).toBe("expires in under a minute");
  });

  it("never offers an action once the label says expired", () => {
    const expiresAt = "2026-08-03T11:59:59Z";

    expect(isOfferActionable("offered", expiresAt, NOW)).toBe(false);
    expect(offerExpiryLabel(expiresAt, NOW)).toBe("expired");
  });

  it("understates rather than overstates the time remaining", () => {
    // 90 minutes rounded UP to "2 hr", which promises time the guest does not have.
    expect(offerExpiryLabel("2026-08-03T13:30:00Z", NOW)).toBe("expires in 1 hr");
  });
});
