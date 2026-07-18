using System.Text.Json.Nodes;
using Cordia.Infrastructure.Services;

namespace Cordia.Tests.UnitTests.Idempotency;

/// <summary>
/// Spec 11 §4.3 / Inv-2: the <c>PreorderReference</c> embedded in the canonical payload is folded
/// into the canon.v1 request hash, so changing any pre-order line changes the hash (forcing a new
/// revision) while a semantically identical pre-order — regardless of line/modifier ordering —
/// hashes equal. A booking with no pre-order hashes identically whether the field is absent or
/// explicit null/empty (the hasher omits null + empty collections), so this is backward-compatible.
/// </summary>
[Trait("Category", "Unit")]
public class CanonicalHashIncludesPreorderTests
{
    private readonly CanonicalRequestHasher _hasher = new();

    private static string Booking(string preorderJson) => $$"""
        {
          "operation": "create",
          "service_id": "svc_1",
          "starts_at": "2026-07-01T18:00:00Z",
          "party_size": 2,
          "preorder": {{preorderJson}}
        }
        """;

    [Fact]
    public void DifferentPreorderLines_ProduceDifferentHash()
    {
        var twoBurgers = Booking("""
            { "catalog_version": "menu_1", "confirmation_id": "conf_1",
              "lines": [ { "item_id": "burger", "modifier_ids": ["cheese"], "quantity": 2,
                           "captured_price_amount_minor": 1500, "captured_price_currency": "AUD" } ] }
            """);
        var oneBurger = Booking("""
            { "catalog_version": "menu_1", "confirmation_id": "conf_1",
              "lines": [ { "item_id": "burger", "modifier_ids": ["cheese"], "quantity": 1,
                           "captured_price_amount_minor": 1500, "captured_price_currency": "AUD" } ] }
            """);

        Assert.NotEqual(
            _hasher.Hash(JsonNode.Parse(twoBurgers)).Value,
            _hasher.Hash(JsonNode.Parse(oneBurger)).Value);
    }

    [Fact]
    public void SamePreorder_ReorderedLinesAndModifiers_ProduceEqualHash()
    {
        var orderA = Booking("""
            { "catalog_version": "menu_1", "confirmation_id": "conf_1",
              "lines": [
                { "item_id": "burger", "modifier_ids": ["cheese","bacon"], "quantity": 1,
                  "captured_price_amount_minor": 1500, "captured_price_currency": "AUD" },
                { "item_id": "fries", "modifier_ids": [], "quantity": 2,
                  "captured_price_amount_minor": 500, "captured_price_currency": "AUD" } ] }
            """);
        var orderB = Booking("""
            { "confirmation_id": "conf_1", "catalog_version": "menu_1",
              "lines": [
                { "item_id": "fries", "quantity": 2, "modifier_ids": [],
                  "captured_price_currency": "AUD", "captured_price_amount_minor": 500 },
                { "item_id": "burger", "modifier_ids": ["bacon","cheese"], "quantity": 1,
                  "captured_price_amount_minor": 1500, "captured_price_currency": "AUD" } ] }
            """);

        Assert.Equal(
            _hasher.Hash(JsonNode.Parse(orderA)).Value,
            _hasher.Hash(JsonNode.Parse(orderB)).Value);
    }

    [Fact]
    public void PreorderPresenceVsAbsence_ProduceDifferentHash()
    {
        var withPreorder = Booking("""
            { "catalog_version": "menu_1", "confirmation_id": "conf_1",
              "lines": [ { "item_id": "burger", "modifier_ids": [], "quantity": 1,
                           "captured_price_amount_minor": 1500, "captured_price_currency": "AUD" } ] }
            """);
        var noPreorder = """
            { "operation": "create", "service_id": "svc_1",
              "starts_at": "2026-07-01T18:00:00Z", "party_size": 2 }
            """;

        Assert.NotEqual(
            _hasher.Hash(JsonNode.Parse(withPreorder)).Value,
            _hasher.Hash(JsonNode.Parse(noPreorder)).Value);
    }

    [Fact]
    public void NoPreorder_NullVsAbsent_ProduceEqualHash()
    {
        var nullPreorder = Booking("null");
        var absentPreorder = """
            { "operation": "create", "service_id": "svc_1",
              "starts_at": "2026-07-01T18:00:00Z", "party_size": 2 }
            """;

        Assert.Equal(
            _hasher.Hash(JsonNode.Parse(nullPreorder)).Value,
            _hasher.Hash(JsonNode.Parse(absentPreorder)).Value);
    }
}
