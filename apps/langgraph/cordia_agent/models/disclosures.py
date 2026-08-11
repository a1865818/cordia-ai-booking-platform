"""The disclosure vocabulary recorded on ``AgentState.disclosures_presented``.

``await_confirmation`` refuses to record a ``Confirmation`` while
``disclosures_presented`` is empty (§5.5 / §14), and until now **nothing in
production ever populated it** — the text runner injected values through
``aupdate_state``.  That is a workaround a script can perform and a phone call
cannot: a voice caller has no driver to reach in and patch state, so the gate
was a hard stop on the voice channel.

The fix is to have the nodes that genuinely present a disclosure record that
they did.  Two do:

- ``consent_gate`` plays the recording notice, when the resolved jurisdiction
  policy says one is required.  That is :data:`RECORDING_NOTICE`.
- ``read_back`` reads the booking's material terms back to the customer before
  asking them to confirm.  That is :data:`BOOKING_TERMS`, plus
  :data:`AVAILABILITY_ADVISORY` when the availability behind those terms is
  informational rather than held — Inv-16 wording that must not be presented
  as a guarantee.

Nothing else may append here.  A disclosure id asserts "the customer was shown
this", and a node that did not show it must not claim otherwise; spec 14 owns
the eventual required-disclosure SET, and this module owns only the names.
"""

from __future__ import annotations


#: The recording/transcription notice presented by the consent gate.
RECORDING_NOTICE = "recording_notice"

#: The material booking terms presented by the read-back.
BOOKING_TERMS = "booking_terms"

#: Presented alongside the read-back when availability is informational — the
#: slot is NOT held, and Inv-16 forbids implying otherwise.
AVAILABILITY_ADVISORY = "availability_advisory"


def merge_disclosures(existing: list[str], *presented: str) -> list[str]:
    """Return *existing* plus any of *presented* not already recorded.

    Order is preserved and duplicates are dropped: the list is a set of facts
    about what the customer has been shown this session, and a node that runs
    twice has not disclosed twice.

    Args:
        existing: The disclosures already recorded on state.
        *presented: Disclosure ids the calling node just presented.

    Returns:
        A new list; *existing* is never mutated.
    """
    merged = list(existing)
    for disclosure in presented:
        if disclosure not in merged:
            merged.append(disclosure)
    return merged
