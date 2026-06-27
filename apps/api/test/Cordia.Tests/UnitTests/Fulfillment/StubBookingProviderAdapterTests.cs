#pragma warning disable xUnit1051 // adapter methods accept ct but tests use default(ct) deliberately
using Cordia.Domain.Fulfillment;
using Cordia.Infrastructure.Providers;

namespace Cordia.Tests.UnitTests.Fulfillment;

[Trait("Category", "Unit")]
public class StubBookingProviderAdapterTests
{
    private const string Account = "acct_1";
    private const string IdempotencyKey = "idem_key_abc";

    private static ProviderBookingRequest MakeRequest(string? key = null) =>
        new(
            TenantId: "tenant_1",
            OperationId: "op_1",
            ProviderAccount: Account,
            ProviderIdempotencyKey: key ?? IdempotencyKey,
            RequestHash: "hash_abc",
            CanonicalPayloadJson: "{}");

    [Fact]
    public async Task CreateBooking_FirstCall_ReturnsCreatedWithReference()
    {
        var adapter = new StubBookingProviderAdapter();

        var result = await adapter.CreateBookingAsync(MakeRequest());

        Assert.Equal(ProviderResultKind.Created, result.Kind);
        Assert.NotNull(result.ProviderReference);
    }

    [Fact]
    public async Task CreateBooking_SameKey_ReturnsAlreadyExistsWithSameReference()
    {
        var adapter = new StubBookingProviderAdapter();

        var first = await adapter.CreateBookingAsync(MakeRequest());
        var second = await adapter.CreateBookingAsync(MakeRequest());

        Assert.Equal(ProviderResultKind.Created, first.Kind);
        Assert.Equal(ProviderResultKind.AlreadyExists, second.Kind);
        Assert.Equal(first.ProviderReference, second.ProviderReference);
    }

    [Fact]
    public async Task LookupByIdempotencyKey_KnownKey_ReturnsAlreadyExistsWithReference()
    {
        var adapter = new StubBookingProviderAdapter();
        var created = await adapter.CreateBookingAsync(MakeRequest());

        var lookup = await adapter.LookupByIdempotencyKeyAsync(Account, IdempotencyKey);

        Assert.Equal(ProviderResultKind.AlreadyExists, lookup.Kind);
        Assert.Equal(created.ProviderReference, lookup.ProviderReference);
    }

    [Fact]
    public async Task LookupByIdempotencyKey_UnknownKey_ReturnsNotFound()
    {
        var adapter = new StubBookingProviderAdapter();

        var result = await adapter.LookupByIdempotencyKeyAsync(Account, "unknown_key");

        Assert.Equal(ProviderResultKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task LookupByReference_KnownReference_ReturnsAlreadyExistsWithReference()
    {
        var adapter = new StubBookingProviderAdapter();
        var created = await adapter.CreateBookingAsync(MakeRequest());

        var lookup = await adapter.LookupByReferenceAsync(Account, created.ProviderReference!);

        Assert.Equal(ProviderResultKind.AlreadyExists, lookup.Kind);
        Assert.Equal(created.ProviderReference, lookup.ProviderReference);
    }

    [Fact]
    public async Task LookupByReference_UnknownReference_ReturnsNotFound()
    {
        var adapter = new StubBookingProviderAdapter();

        var result = await adapter.LookupByReferenceAsync(Account, "ext_unknown");

        Assert.Equal(ProviderResultKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task GetStatus_KnownReference_ReturnsAlreadyExistsWithReference()
    {
        var adapter = new StubBookingProviderAdapter();
        var created = await adapter.CreateBookingAsync(MakeRequest());

        var status = await adapter.GetStatusAsync(Account, created.ProviderReference!);

        Assert.Equal(ProviderResultKind.AlreadyExists, status.Kind);
        Assert.Equal(created.ProviderReference, status.ProviderReference);
    }

    [Fact]
    public async Task GetStatus_UnknownReference_ReturnsNotFound()
    {
        var adapter = new StubBookingProviderAdapter();

        var result = await adapter.GetStatusAsync(Account, "ext_unknown");

        Assert.Equal(ProviderResultKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task Cancel_KnownReference_ReturnsCreated_ThenLookupReturnsNotFound()
    {
        var adapter = new StubBookingProviderAdapter();
        var created = await adapter.CreateBookingAsync(MakeRequest());
        var reference = created.ProviderReference!;

        var cancel = await adapter.CancelBookingAsync(Account, reference);
        Assert.Equal(ProviderResultKind.Created, cancel.Kind);

        // After cancel, lookup by reference should return NotFound.
        var lookup = await adapter.LookupByReferenceAsync(Account, reference);
        Assert.Equal(ProviderResultKind.NotFound, lookup.Kind);
    }

    [Fact]
    public async Task Cancel_UnknownReference_ReturnsNotFound()
    {
        var adapter = new StubBookingProviderAdapter();

        var result = await adapter.CancelBookingAsync(Account, "ext_unknown");

        Assert.Equal(ProviderResultKind.NotFound, result.Kind);
    }
}
