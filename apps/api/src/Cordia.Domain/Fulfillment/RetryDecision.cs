using Cordia.Domain.Enums;

namespace Cordia.Domain.Fulfillment;

/// <summary>Combined classification and action for a failed provider call (spec 06 §15.1).</summary>
/// <param name="Classification">The retry classification category from the domain enum.</param>
/// <param name="Action">The policy action the caller should execute.</param>
public sealed record RetryDecision(RetryClassification Classification, RetryAction Action);
