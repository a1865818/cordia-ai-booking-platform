/**
 * ErrorState — the shared API-failure surface.
 *
 * What matters here is that the heading never tells an operator to do something the error does
 * not call for. The detail line is the server's own words; the heading is ours, so it is the
 * part that can lie.
 */

import { describe, expect, it, vi } from "../../test/vitest-compat";
import { fireEvent, render, screen } from "@testing-library/react";
import { ErrorState } from "./ErrorState";
import { ApiError, ApiValidationError, ApiVerificationRequiredError } from "../../api/problem";

describe("the 403 heading", () => {
  it("says verification only when the error IS a verification requirement", () => {
    // `toApiError` mints this for a 403 carrying `status: requires_verification` or
    // `code: verification_required` — a guest whose identity has not been established.
    render(
      <ErrorState
        error={new ApiVerificationRequiredError(403, "verification required", { action: "modify" })}
      />
    );

    expect(screen.getByText("Verification needed")).toBeInTheDocument();
  });

  it("does NOT tell an operator to verify when the 403 is a permission refusal", () => {
    // A role/authorization 403 stays a plain ApiError, and it is the console's most common 403
    // by far — every menu and waitlist action refuses a session without the role. Sending that
    // operator to a verification flow points them at something that cannot resolve their
    // problem, and nothing on the screen would correct the impression.
    render(<ErrorState error={new ApiError(403, "Authorization failed")} />);

    expect(screen.queryByText("Verification needed")).not.toBeInTheDocument();
    expect(screen.getByText("This session isn't allowed to do that")).toBeInTheDocument();
  });

  it("still shows the server's own explanation under either heading", () => {
    render(<ErrorState error={new ApiError(403, "menu management needs the owner role")} />);

    expect(screen.getByText("menu management needs the owner role")).toBeInTheDocument();
  });
});

describe("the rest of the taxonomy", () => {
  it.each([
    [undefined, "Couldn't reach the server"],
    [400, "That request wasn't valid"],
    [401, "Your session needs to be re-established"],
    [404, "Not found"],
    [409, "This changed while you were looking"],
    [422, "The server couldn't accept that"],
    [500, "The server had a problem"],
  ])("maps %s to its heading", (status, heading) => {
    render(
      <ErrorState error={status === undefined ? new Error("offline") : new ApiError(status, "x")} />
    );

    expect(screen.getByText(heading)).toBeInTheDocument();
  });

  it("lets a caller override the heading entirely", () => {
    render(<ErrorState error={new ApiError(403, "nope")} title="Could not load the catalog list" />);

    expect(screen.getByText("Could not load the catalog list")).toBeInTheDocument();
    expect(screen.queryByText("This session isn't allowed to do that")).not.toBeInTheDocument();
  });

  it("lists per-field messages for a validation problem", () => {
    render(
      <ErrorState
        error={new ApiValidationError(400, "invalid", { partySize: ["Party size must be at least 1"] })}
      />
    );

    expect(screen.getByText(/Party size must be at least 1/)).toBeInTheDocument();
  });
});

describe("the retry control", () => {
  it("is absent unless a retry is offered", () => {
    render(<ErrorState error={new ApiError(500, "boom")} />);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("names itself 'Try again' by default and runs the handler", () => {
    const onRetry = vi.fn();
    render(<ErrorState error={new ApiError(500, "boom")} onRetry={onRetry} />);

    fireEvent.click(screen.getByRole("button", { name: /Try again/ }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it("takes a distinct label, so two ErrorStates on one screen stay tellable apart", () => {
    render(<ErrorState error={new ApiError(500, "boom")} onRetry={() => {}} retryLabel="Reload list" />);

    expect(screen.getByRole("button", { name: "Reload list" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Try again/ })).not.toBeInTheDocument();
  });
});

describe("a retry that is temporarily unavailable", () => {
  it("stays in the DOM disabled, rather than vanishing", () => {
    // Withdrawing `onRetry` removed the button entirely, which drops keyboard focus to <body>
    // mid-tab and makes the control reappear elsewhere in the tab order when the block lifts.
    const onRetry = vi.fn();
    render(
      <ErrorState
        error={new ApiError(500, "boom")}
        onRetry={onRetry}
        retryDisabled
        retryTitle="Another action is still running."
      />
    );

    const button = screen.getByRole("button", { name: /Try again/ });
    expect(button).toBeInTheDocument();
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("title", "Another action is still running.");

    fireEvent.click(button);
    expect(onRetry).not.toHaveBeenCalled();
  });

  it("carries no stale tooltip once it is available again", () => {
    render(
      <ErrorState error={new ApiError(500, "boom")} onRetry={() => {}} retryTitle="not now" />
    );

    expect(screen.getByRole("button", { name: /Try again/ })).not.toHaveAttribute("title");
  });
});
