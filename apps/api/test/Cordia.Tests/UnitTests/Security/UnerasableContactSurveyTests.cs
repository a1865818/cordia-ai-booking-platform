using Cordia.Infrastructure.Services.Retention;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// Detecting guest contact data inside an immutable intent revision (docs §14 retention).
///
/// The payload is hashed and cannot be redacted without breaking the idempotency chain derived
/// from `payload_hash` (Inv-2/3), so retention cannot reach it. What it CAN do is count it — and
/// the count is only useful for the spec 12/14 ruling if it is neither inflated nor understated.
/// </summary>
[Trait("Category", "Unit")]
public class UnerasableContactSurveyTests
{
    [Theory]
    [InlineData("""{"operation":"create","customer":{"phone_e164":"+61400111222"}}""")]
    [InlineData("""{"operation":"create","customer":{"email":"guest@example.com"}}""")]
    [InlineData("""{"operation":"create","customer":{"display_name":"Ada Lovelace"}}""")]
    public void CountsAPayloadCarryingContact(string payload)
    {
        Assert.True(RetentionSweepHandler.CarriesContact(payload));
    }

    [Theory]
    // No customer node at all — the overwhelmingly common shape before guest identity existed.
    [InlineData("""{"operation":"create","service_id":"svc-1"}""")]
    // A customer node carrying only opaque ids is not contact data: an id cannot be dialled,
    // written to, or used to identify a person outside this system.
    [InlineData("""{"operation":"create","customer":{"customer_id":"cust_7"}}""")]
    // Present but empty is not contact data either.
    [InlineData("""{"operation":"create","customer":{"phone_e164":""}}""")]
    [InlineData("""{"operation":"create","customer":{"phone_e164":"   "}}""")]
    [InlineData("""{"operation":"create","customer":{}}""")]
    public void DoesNotCountAPayloadWithoutContact(string payload)
    {
        // Over-counting would make the number useless for the decision it exists to inform.
        Assert.False(RetentionSweepHandler.CarriesContact(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void AMalformedOrAbsentPayloadIsNotEvidenceOfContact(string? payload)
    {
        // And must not take the sweep down — a historical row can be any shape.
        Assert.False(RetentionSweepHandler.CarriesContact(payload));
    }

    [Fact]
    public void DoesNotCountAPhoneThatIsNotUnderTheCustomerNode()
    {
        // Only the `customer` node the submission contract defines (spec 04 §4.2) counts. A
        // venue's own phone in a business block is not guest contact data, and counting it would
        // inflate the exposure figure the ruling depends on.
        Assert.False(RetentionSweepHandler.CarriesContact(
            """{"operation":"create","business":{"phone":"+61399990000"}}"""));
    }
}
