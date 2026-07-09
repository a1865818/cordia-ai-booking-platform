using Cordia.Application.Handlers.Menu.IngestVendorMenu;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="IngestVendorMenuValidator"/> (spec 11 §5.2): structurally invalid
/// queue-driven commands are rejected before the inbox transaction opens.
/// </summary>
[Trait("Category", "Unit")]
public class IngestVendorMenuValidatorTests
{
    private readonly IngestVendorMenuValidator _validator = new();

    private static IngestVendorMenuCommand Valid()
        => new("tenant-abc", "cat-1", "s3://tenant/menu.pdf", "msg-1", "corr-1");

    [Fact]
    public async Task Valid_command_passes()
    {
        var result = await _validator.TestValidateAsync(
            Valid(), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Empty_tenant_id_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { TenantId = "" }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.TenantId);
    }

    [Fact]
    public async Task Empty_catalog_id_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { CatalogId = "" }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.CatalogId);
    }

    [Fact]
    public async Task Empty_source_artifact_ref_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { SourceArtifactRef = "" }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.SourceArtifactRef);
    }

    [Fact]
    public async Task Empty_message_id_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { MessageId = "" }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.MessageId);
    }
}
