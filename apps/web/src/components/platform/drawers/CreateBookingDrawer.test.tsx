import { describe, expect, it, vi } from "../../../test/vitest-compat";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { CreateBookingDrawer } from "./CreateBookingDrawer";
import type { BookableService } from "../../../features/bookings/services";
import { buildWeek } from "../../../features/bookings/week";

vi.mock("../../../features/bookings/submit", () => ({ submitBooking: vi.fn() }));
vi.mock("../../../features/bookings/guestContact", () => ({ createGuestContact: vi.fn() }));

const WEEK = buildWeek(new Date("2026-08-03T00:00:00").toISOString());

const CATALOG: BookableService[] = [
  { id: "itm_gel", name: "Gel manicure", durationMinutes: 45 },
  { id: "itm_pedi", name: "Pedicure", durationMinutes: null },
];

/** The drawer polls the operation it submits, so it needs a client even before it submits. */
function Providers({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function open(props: Partial<Parameters<typeof CreateBookingDrawer>[0]> = {}) {
  render(
    <CreateBookingDrawer
      open
      device="desktop"
      week={WEEK}
      onClose={() => {}}
      onCreated={() => {}}
      services={CATALOG}
      {...props}
    />,
    { wrapper: Providers }
  );
}

describe("CreateBookingDrawer", () => {
  it("offers the venue's catalog items, identified by their real item_id", () => {
    // These ids are submitted verbatim as `service_id` and hashed by canon.v1. The drawer used
    // to offer `SERVICE_LIST` — five hand-written keys that exist on no server — so every
    // console-created booking named a service the venue did not have.
    open();
    const options = screen.getAllByRole("option") as HTMLOptionElement[];
    const values = options.map(o => o.value);
    expect(values).toContain("itm_gel");
    expect(values).toContain("itm_pedi");
    // And none of the mock catalogue's keys.
    expect(values).not.toContain("gel");
    expect(values).not.toContain("sns");
  });

  it("appends a duration only when the catalog states one", () => {
    open();
    expect(screen.getByRole("option", { name: "Gel manicure · 45m" })).toBeInTheDocument();
    // No "· 1h" invented for the item that carries no duration.
    expect(screen.getByRole("option", { name: "Pedicure" })).toBeInTheDocument();
  });

  it("never states a deposit", () => {
    // The platform defers payments (spec 09) and has never requested or held anything. The
    // confirmation panel already omitted this for that exact reason; the form did not.
    open();
    expect(screen.queryByText(/deposit/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Mirrors your deposit policy/)).not.toBeInTheDocument();
    expect(screen.queryByText(/A\$/)).not.toBeInTheDocument();
  });

  it("offers no staff picker when the roster has no named resources", () => {
    open();
    expect(screen.queryByText("With")).not.toBeInTheDocument();
  });

  it("offers a With picker and named staff when resources have names", () => {
    open({
      resources: [
        { id: "res_linh", name: "Linh Pham", role: "Technician" },
        { id: "res_trang", name: "Trang Vo", role: "Technician" },
      ],
    });
    expect(screen.getByText("With")).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Linh Pham · Technician" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Trang Vo · Technician" })).toBeInTheDocument();
  });

  it("tells 'we have not asked yet' apart from 'the venue has nothing bookable'", () => {
    // Rule 1: a successful-but-empty response is a real answer. Falling back to the mock
    // catalogue here is what put unbookable ids into durable intents in the first place.
    const { unmount } = render(
      <CreateBookingDrawer open device="desktop" week={WEEK} onClose={() => {}} onCreated={() => {}} />,
      { wrapper: Providers }
    );
    expect(screen.getByText("Loading services…")).toBeInTheDocument();
    unmount();

    open({ services: [] });
    expect(screen.getByText("No bookable services")).toBeInTheDocument();
    expect(screen.getByText(/catalog has no published items/)).toBeInTheDocument();
  });

  it("names a failed catalog read instead of showing an empty list", () => {
    open({ services: [], servicesFailed: true });
    expect(screen.getByText(/Couldn’t load this venue’s services/)).toBeInTheDocument();
    expect(screen.queryByText("No bookable services")).not.toBeInTheDocument();
  });

  it("cannot be submitted when there is nothing bookable", () => {
    open({ services: [] });
    expect(screen.queryByRole("button", { name: /confirm booking/i })).not.toBeInTheDocument();
  });

  it("reports a prefilled service the catalog does not have, rather than swapping it", () => {
    open({ prefill: { name: "Ana", serviceId: "itm_missing", day: 0, start: 600, partySize: 2 } });
    expect(screen.getByText(/is not in the service/)).toBeInTheDocument();
    expect(screen.getByText("itm_missing")).toBeInTheDocument();
  });

  it("does not reset the open form when the catalog refetches", async () => {
    // `services` is the live catalog now, so its array identity changes on every refetch — and
    // `useCatalogItems` inherits `refetchOnWindowFocus`. With `services` in the reset effect's
    // dependency array, alt-tabbing away and back wiped the typed guest name, the day, the time
    // and the party size, and re-minted `attemptKey` — throwing away the operation identity and
    // idempotency key the form was about to submit under (Inv-2/3).
    function Harness({ catalog }: { catalog: BookableService[] }) {
      return (
        <CreateBookingDrawer
          open device="desktop" week={WEEK}
          onClose={() => {}} onCreated={() => {}}
          services={catalog}
        />
      );
    }

    const { rerender } = render(<Harness catalog={CATALOG} />, { wrapper: Providers });

    const nameInput = screen.getByPlaceholderText("Search or add a customer");
    fireEvent.change(nameInput, { target: { value: "Priya" } });
    const partySize = screen.getByRole("spinbutton");
    fireEvent.change(partySize, { target: { value: "6" } });

    // A refetch returns an equal but NOT identical array — exactly what react-query hands back.
    rerender(<Harness catalog={CATALOG.map(s => ({ ...s }))} />);

    await waitFor(() => {
      expect(screen.getByPlaceholderText("Search or add a customer")).toHaveValue("Priya");
    });
    expect(screen.getByRole("spinbutton")).toHaveValue(6);
  });

  it("chooses a service when the catalog answers after the drawer opened", async () => {
    function Harness({ catalog }: { catalog?: BookableService[] }) {
      return (
        <CreateBookingDrawer
          open device="desktop" week={WEEK}
          onClose={() => {}} onCreated={() => {}}
          services={catalog}
        />
      );
    }
    const { rerender } = render(<Harness catalog={undefined} />, { wrapper: Providers });
    expect(screen.getByText("Loading services…")).toBeInTheDocument();

    rerender(<Harness catalog={CATALOG} />);
    await waitFor(() => {
      const select = screen.getAllByRole("combobox")[0] as HTMLSelectElement;
      expect(select.value).toBe("itm_gel");
    });
  });

  it("refuses to submit when the selected service leaves the catalog mid-edit", async () => {
    // The drawer submits `svc` but reads durationMinutes from the resolved `service`. It used to
    // fall back to serviceOptions[0] when `svc` was absent, so a refetch that retired the pick
    // (a publish landing, or refetchOnWindowFocus) would durably record a payload naming one
    // service and reserving ANOTHER's duration — with the confirm panel showing the substitute,
    // so the operator could not see it happen.
    const { rerender } = render(
      <CreateBookingDrawer
        open device="desktop" week={WEEK}
        onClose={() => {}} onCreated={() => {}}
        services={CATALOG}
      />,
      { wrapper: Providers }
    );

    fireEvent.change(screen.getByPlaceholderText("Search or add a customer"), {
      target: { value: "Sarah" },
    });

    // The catalog is republished without the selected item.
    rerender(
      <CreateBookingDrawer
        open device="desktop" week={WEEK}
        onClose={() => {}} onCreated={() => {}}
        services={[{ id: "itm_new", name: "Acrylics", durationMinutes: 90 }]}
      />
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(/no longer in the published catalog/i);
    expect(screen.getByRole("button", { name: /confirm booking/i })).toBeDisabled();
  });

  it("re-submits a retry with the SAME confirmed_at, not a fresh wall clock", async () => {
    // `AttemptIdentity.confirmedAt` is minted once with the attempt so every retry of that
    // attempt describes the same confirmation. This drawer omitted it from its SubmissionContext,
    // so `buildSubmission` fell back to `new Date().toISOString()` and each retry claimed the
    // operator had confirmed at the moment of the retry.
    //
    // Two reasons that is wrong, neither of which is "the server 409s" — `SubmitConfirmedIntent`
    // hashes only `revision.canonical_payload`, so this field is outside its idempotency hash:
    //   1. `confirmed_at` / `created_at` are the recorded time of CONFIRMATION. When the first
    //      attempt fails and a later one lands, the stored instant is the retry's, not the
    //      operator's — a durable record that says something untrue (Inv-16).
    //   2. The sibling doors DO hash the whole request body
    //      (`SubmitBookingCancellation`/`SubmitBookingModification` hash
    //      `SerializeToNode(request)`), where exactly this omission IS a hard 409. The console
    //      must not depend on which door happens to be forgiving.
    const { submitBooking } = await import("../../../features/bookings/submit");
    const submit = vi.mocked(submitBooking);
    submit.mockReset();
    submit.mockRejectedValue(new Error("network dropped"));

    open();
    fireEvent.change(screen.getByPlaceholderText("Search or add a customer"), {
      target: { value: "Priya" },
    });

    fireEvent.click(screen.getByRole("button", { name: /confirm booking/i }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));

    // The operator retries the identical booking — same attempt, same everything.
    fireEvent.click(screen.getByRole("button", { name: /confirm booking/i }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(2));

    const first = submit.mock.calls[0][0] as Record<string, any>;
    const second = submit.mock.calls[1][0] as Record<string, any>;

    // The attempt identity is reused, so the retry is a retry (Inv-2)...
    expect(second.idempotency_key).toBe(first.idempotency_key);
    expect(second.operation_id).toBe(first.operation_id);
    // ...and it describes the SAME confirmation rather than re-stamping the clock.
    expect(second.confirmation.confirmed_at).toBe(first.confirmation.confirmed_at);
    expect(second.revision.created_at).toBe(first.revision.created_at);
    // The payload hash was always stable here; assert it so a future change that folds a
    // timestamp into the canonical payload fails loudly instead of silently minting 409s.
    expect(second.revision.payload_hash).toBe(first.revision.payload_hash);
    expect(second.confirmation.intent_payload_hash).toBe(first.confirmation.intent_payload_hash);
  });

  it("says the customer NAME is a label, not a recorded guest identity", () => {
    // The name is still screen-only: it is deliberately kept out of the canonical payload,
    // which is immutable and outside the retention sweep. `confirmation.identity` carries the
    // operator.
    open();
    expect(screen.getByText(/Name is used on this screen only/)).toBeInTheDocument();
  });

  it("offers a guest phone, and says that one IS recorded", () => {
    // The phone is the half that reaches the platform, as `customer.phone_e164` — it is what
    // gives the booking a notification destination and makes it findable by phone.
    open();
    expect(screen.getByPlaceholderText("+61400111222")).toBeInTheDocument();
    expect(screen.getByText(/what the guest gets notified on/)).toBeInTheDocument();
  });

  it("warns when the phone is not E.164, without blocking submission", async () => {
    // The server matches the stored value against the query by exact string equality, so a
    // local-format number is silently unfindable later. Warn, never block — the platform
    // accepts it and the operator may have a reason.
    open();
    const phone = screen.getByPlaceholderText("+61400111222");
    fireEvent.change(phone, { target: { value: "0400 111 222" } });

    expect(await screen.findByText(/will not be found by a later phone search/)).toBeInTheDocument();

    fireEvent.change(phone, { target: { value: "+61400111222" } });
    await waitFor(() => {
      expect(screen.queryByText(/will not be found by a later phone search/)).not.toBeInTheDocument();
    });
  });

  describe("availability note", () => {
    // The drawer defaults `day` to `todayIndex` when the displayed week contains today, and to
    // index 0 otherwise — so the fixture has to follow that same rule rather than assuming a
    // weekday, or the slots land on a day nobody has selected.
    const selectedDay = WEEK.dates[WEEK.todayIndex ?? 0];
    const slotOn = (hour: number, free: number) => ({
      slotId: `slot_${hour}`,
      resourceId: null,
      startsAt: new Date(
        selectedDay.getFullYear(), selectedDay.getMonth(), selectedDay.getDate(), hour, 0, 0
      ).toISOString(),
      totalCapacity: 4,
      freeCapacity: free,
    });

    it("says nothing at all while the read has never loaded", () => {
      open({ availability: undefined });
      expect(screen.queryByText(/declared no capacity/)).not.toBeInTheDocument();
      expect(screen.queryByText(/Appears available/)).not.toBeInTheDocument();
    });

    it("reports an empty answer as 'nothing declared', never as fully booked", () => {
      // `capacity_slots` is filled only by the owner declaration surface, so a venue that has
      // never used it returns []. Reading that as "no availability" would be a confident
      // falsehood for every such tenant — the exact failure this endpoint was withheld over.
      open({ availability: [] });
      expect(screen.getByText(/declared no capacity/)).toBeInTheDocument();
      // And it says so explicitly, rather than leaving the operator to infer it.
      expect(
        screen.getByText(/not the same as being fully booked/)
      ).toBeInTheDocument();
    });

    it("uses 'appears available' wording, because the read holds nothing", () => {
      // Spec 07 §6.5: only a `held` result closes the race. This read is `informational`, so
      // anything stronger would promise a slot that can be taken before submission.
      open({ availability: [slotOn(9, 2)] });
      expect(screen.getByText(/Appears available/)).toBeInTheDocument();
      expect(screen.getByText(/not held/)).toBeInTheDocument();
    });

    it("never narrows the pickers — it informs the choice, it does not constrain it", () => {
      // Advisory means advisory. The read takes no hold and can be stale by submission time,
      // so filtering the time options by it would hide bookable times on the strength of a
      // number the platform never promised to honour.
      const withoutAvailability = render(
        <CreateBookingDrawer
          open device="desktop" week={WEEK}
          onClose={() => {}} onCreated={() => {}} services={CATALOG}
        />,
        { wrapper: Providers }
      );
      const optionCount = screen.getAllByRole("option").length;
      withoutAvailability.unmount();

      // A day whose every declared slot is full must still offer every time.
      open({ availability: [slotOn(9, 0)] });
      expect(screen.getByText(/appears full/)).toBeInTheDocument();
      expect(screen.getAllByRole("option")).toHaveLength(optionCount);
    });
  });

  // ── The submit gate must not pass a party size the API will refuse ─────────────────────────

  it.each(["0", "-1", "2.5", ""])(
    "refuses to submit party size %s — the input's min is never enforced by the browser",
    (value) => {
      // The confirm button is not a form submit, so `<input min={1}>` runs no constraint
      // validation at all, and the gate only asked whether the string was non-empty. `0`, `-1`
      // and `2.5` all reached the API through `Number(partySize)` — a durable booking for nobody.
      open();
      fireEvent.change(screen.getByPlaceholderText("Search or add a customer"), {
        target: { value: "Priya" },
      });
      fireEvent.change(screen.getByLabelText(/party size/i), { target: { value } });

      expect(screen.getByRole("button", { name: /confirm booking/i })).toBeDisabled();
    }
  );

  it.each(["1", "6", "51", "200"])("accepts party size %s — the API states no ceiling", (value) => {
    // `BookingRequest.party_size` is `{"type":"integer","exclusiveMinimum":0}` — a floor and
    // nothing else. A client-side maximum would refuse a booking the platform accepts and assert a
    // limit no spec has set, leaving a venue taking a function for sixty unable to submit at all.
    open();
    fireEvent.change(screen.getByPlaceholderText("Search or add a customer"), {
      target: { value: "Priya" },
    });
    fireEvent.change(screen.getByLabelText(/party size/i), { target: { value } });

    expect(screen.getByRole("button", { name: /confirm booking/i })).toBeEnabled();
  });

  // ── Idempotency: a retry must present the same payload under the same key ──────────────────

  it("reuses the minted guest-contact ref on retry, so the payload hash cannot drift", async () => {
    // `POST /api/guest-contacts` returns a NEW opaque ref on every call, and that ref is inside
    // the hashed canonical payload. Minting a fresh one per attempt changed `payload_hash` while
    // `attemptKey` — and therefore the idempotency key — stayed the same, so the server answered
    // 409 IdempotencyConflict and the booking could only be abandoned, never retried (Inv-2/3).
    const { submitBooking } = await import("../../../features/bookings/submit");
    const { createGuestContact } = await import("../../../features/bookings/guestContact");
    const submit = vi.mocked(submitBooking);
    const mint = vi.mocked(createGuestContact);
    submit.mockReset();
    mint.mockReset();
    submit.mockRejectedValue(new Error("network dropped"));
    let n = 0;
    mint.mockImplementation(async () => ({ contact_ref: `gc_${++n}` }) as never);

    open();
    fireEvent.change(screen.getByPlaceholderText("Search or add a customer"), {
      target: { value: "Priya" },
    });
    fireEvent.change(screen.getByPlaceholderText("+61400111222"), {
      target: { value: "+61400111222" },
    });

    fireEvent.click(screen.getByRole("button", { name: /confirm booking/i }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole("button", { name: /confirm booking/i }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(2));

    // Minted once for the attempt, not once per submit.
    expect(mint).toHaveBeenCalledTimes(1);

    const first = submit.mock.calls[0][0] as Record<string, any>;
    const second = submit.mock.calls[1][0] as Record<string, any>;
    expect(second.idempotency_key).toBe(first.idempotency_key);
    expect(second.revision.payload_hash).toBe(first.revision.payload_hash);
  });

  // ── The unmatched-prefill warning must stop claiming nothing was chosen ────────────────────

  it("drops the unmatched-prefill warning once the operator picks a service", () => {
    open({ prefill: { serviceId: "itm_missing", name: "Priya" } as never });

    expect(screen.getByText(/is not in the service/i)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/^Service/), { target: { value: "itm_gel" } });

    // The warning's own words are "nothing has been chosen for you" — which stops being true.
    expect(screen.queryByText(/is not in the service/i)).not.toBeInTheDocument();
  });

  // ── An accepted operation is described by what was SUBMITTED ───────────────────────────────

  it("keeps the outcome panel and its Done button when the catalog empties mid-operation", async () => {
    // A service unpublished from another tab empties the catalog. The `!hasServices` branch used to
    // outrank both phase branches, so the polling screen and the done screen were replaced by
    // "No bookable services" and the footer became `undefined` — no outcome, no retry, no Done,
    // only the close icon, while the operation settled and `onCreated` fired regardless.
    const { submitBooking } = await import("../../../features/bookings/submit");
    const submit = vi.mocked(submitBooking);
    submit.mockReset();
    submit.mockResolvedValue({ operation_id: "op_1" } as never);

    const { rerender } = render(
      <CreateBookingDrawer
        open device="desktop" week={WEEK}
        onClose={() => {}} onCreated={() => {}} services={CATALOG}
      />,
      { wrapper: Providers }
    );

    fireEvent.change(screen.getByPlaceholderText("Search or add a customer"), {
      target: { value: "Priya" },
    });
    fireEvent.click(screen.getByRole("button", { name: /confirm booking/i }));
    await waitFor(() => expect(submit).toHaveBeenCalled());

    // The catalog empties underneath the live operation.
    rerender(
      <CreateBookingDrawer
        open device="desktop" week={WEEK}
        onClose={() => {}} onCreated={() => {}} services={[]}
      />
    );

    // Still the operation's screen — not "No bookable services".
    expect(screen.queryByText(/No bookable services/i)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /submitting/i })).toBeInTheDocument();
  });

  it("does not apply an in-flight submission to a drawer that was closed and reopened", async () => {
    // The old promise still resolves. It used to call `setOperationId` unconditionally, so polling
    // restarted under the NEW form and a success appended a row built from whatever had since been
    // typed — a booking the operator never made.
    const { submitBooking } = await import("../../../features/bookings/submit");
    const submit = vi.mocked(submitBooking);
    submit.mockReset();

    let release!: (v: unknown) => void;
    submit.mockImplementation(() => new Promise(res => { release = res; }) as never);

    const onCreated = vi.fn();
    const { rerender } = render(
      <CreateBookingDrawer
        open device="desktop" week={WEEK}
        onClose={() => {}} onCreated={onCreated} services={CATALOG}
      />,
      { wrapper: Providers }
    );

    fireEvent.change(screen.getByPlaceholderText("Search or add a customer"), {
      target: { value: "Priya" },
    });
    fireEvent.click(screen.getByRole("button", { name: /confirm booking/i }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));

    // Closed, then reopened — a fresh form.
    rerender(
      <CreateBookingDrawer
        open={false} device="desktop" week={WEEK}
        onClose={() => {}} onCreated={onCreated} services={CATALOG}
      />
    );
    rerender(
      <CreateBookingDrawer
        open device="desktop" week={WEEK}
        onClose={() => {}} onCreated={onCreated} services={CATALOG}
      />
    );

    // The abandoned submission now resolves.
    release({ operation_id: "op_stale" });
    // Let the continuation run; the reopened form must be unaffected by it.
    await waitFor(() => expect(screen.getByPlaceholderText("Search or add a customer")).toHaveValue(""));

    // The reopened drawer is untouched: still the form, nothing appended.
    expect(onCreated).not.toHaveBeenCalled();
    expect(screen.queryByText(/Booking confirmed/i)).not.toBeInTheDocument();
  });
});
