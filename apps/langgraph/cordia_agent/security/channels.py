"""Prompt channel separation — spec 14 §8.11 (design §24.2), Inv-12.

Runtime enforcement that UNTRUSTED content can never be assembled into a
privileged prompt channel. ``PromptChannels`` is the ONLY sanctioned shape for
prompt assembly: three trust-homogeneous channels whose membership is validated
at construction, so a customer utterance (or menu text, or retrieved document)
placed into the system channel is a hard :class:`ChannelSeparationError`, not a
silent prompt-injection vector.

Future-LLM mandate: any code path that sends content to a language model MUST
assemble its input through ``PromptChannels`` (or an equivalent that preserves
these invariants) — never by concatenating raw strings across trust levels.
Today's adopters: the isolated menu-extraction agent (empty system channel,
one validated untrusted span). The main booking agent adopts this at the point
an LLM call is introduced on its hot path.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from cordia_agent.safety.injection_harness import LabeledContent, TrustLevel


if TYPE_CHECKING:
    from collections.abc import Iterable


class ChannelSeparationError(ValueError):
    """Raised when content is placed in a channel its trust level forbids."""


def _require_trust(
    channel: str, items: Iterable[LabeledContent], expected: TrustLevel
) -> tuple[LabeledContent, ...]:
    validated = tuple(items)
    for item in validated:
        if item.trust is not expected:
            raise ChannelSeparationError(
                f"{channel} channel only accepts {expected.value} content, "
                f"got {item.trust.value}"
            )
    return validated


class PromptChannels:
    """Trust-homogeneous prompt channels (spec 14 §8.11).

    ``system`` carries only SYSTEM-trust content (this process's own prompt
    text and policy rules), ``tool_results`` only TOOL_RESULT content, and
    ``untrusted`` only UNTRUSTED content (customer speech, documents, menus).
    Cross-channel placement raises :class:`ChannelSeparationError` at
    construction — the violation can never reach a model call. Channels are
    stored as tuples and there is no mutation API (mirrors ``ToolAllowlist``).
    """

    __slots__ = ("_system", "_tool_results", "_untrusted")

    def __init__(
        self,
        *,
        system: Iterable[LabeledContent] = (),
        tool_results: Iterable[LabeledContent] = (),
        untrusted: Iterable[LabeledContent] = (),
    ) -> None:
        self._system = _require_trust("system", system, TrustLevel.SYSTEM)
        self._tool_results = _require_trust(
            "tool_results", tool_results, TrustLevel.TOOL_RESULT
        )
        self._untrusted = _require_trust("untrusted", untrusted, TrustLevel.UNTRUSTED)

    @property
    def system(self) -> tuple[LabeledContent, ...]:
        """SYSTEM-trust content only (prompts, policy rules)."""
        return self._system

    @property
    def tool_results(self) -> tuple[LabeledContent, ...]:
        """TOOL_RESULT-trust content only (verified tool outputs)."""
        return self._tool_results

    @property
    def untrusted(self) -> tuple[LabeledContent, ...]:
        """UNTRUSTED content only (customer text, documents, menus)."""
        return self._untrusted
