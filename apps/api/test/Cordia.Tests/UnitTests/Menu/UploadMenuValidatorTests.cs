using System.Text;
using Cordia.Application.Handlers.Menu.UploadMenu;
using Cordia.Infrastructure.Menu;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="UploadMenuValidator"/>.
///
/// <para>The interesting rule is the length bound on <c>SourceArtifactRef</c> (issue #95). Before
/// it, the validator was <c>NotEmpty()</c> and nothing more, so an upload anywhere between the
/// resolver's 256 KiB cap and Kestrel's ~28.6 MB default answered <c>202 Accepted</c>, committed a
/// catalog row, an outbox message and an audit event, burned a publisher claim and an inbox
/// transaction, and only then failed with a generic reason the owner could not act on.</para>
///
/// <para>Two properties matter and they pull in opposite directions: the bound must reject the
/// definitely-unresolvable, and it must NEVER reject something the resolver would have accepted —
/// a validator that fails a processable upload is worse than the bug it replaced.</para>
/// </summary>
[Trait("Category", "Unit")]
public class UploadMenuValidatorTests
{
    private readonly UploadMenuValidator _validator = new();

    private static UploadMenuCommand Valid(string? artifactRef = null)
        => new(
            "tenant-abc",
            "owner-1",
            artifactRef ?? "inline:Grilled Salmon - $32.00",
            "v1",
            "menu_refresh",
            "owner");

    [Fact]
    public async Task Valid_command_passes()
    {
        var result = await _validator.TestValidateAsync(
            Valid(), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Empty_artifact_ref_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { SourceArtifactRef = "" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.SourceArtifactRef);
    }

    /// <summary>
    /// The gap the bound closes: 1 MiB + 1 characters used to be a <c>202 Accepted</c> that failed
    /// asynchronously, and is now a 400 before the transaction opens.
    /// </summary>
    [Fact]
    public async Task Artifact_ref_over_the_bound_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid(new string('x', UploadMenuValidator.MaxSourceArtifactRefChars + 1)),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.SourceArtifactRef);
    }

    [Fact]
    public async Task Artifact_ref_at_the_bound_passes()
    {
        var result = await _validator.TestValidateAsync(
            Valid(new string('x', UploadMenuValidator.MaxSourceArtifactRefChars)),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>
    /// The load-bearing direction. The bound is in CHARACTERS and the resolver's cap is in UTF-8
    /// BYTES of decoded text, so the two only agree if the bound clears the largest ref that can
    /// still resolve. That is the percent-encoded <c>data:</c> form, where every byte of an at-cap
    /// document becomes three characters (<c>%XX</c>) — and the bound has to cover that, or a
    /// legitimate upload the pipeline could have processed is refused at ingress.
    /// </summary>
    [Fact]
    public void The_bound_admits_every_ref_the_resolver_can_still_resolve()
    {
        // 3 chars per byte is the percent-encoded worst case; the `data:text/plain;base64,` style
        // header is the longest prefix any accepted form carries.
        const int longestPrefix = 40;
        var worstCaseResolvableRef =
            (3 * InlineMenuArtifactTextResolver.MaxMenuTextBytes) + longestPrefix;

        Assert.True(
            UploadMenuValidator.MaxSourceArtifactRefChars >= worstCaseResolvableRef,
            $"the validator bound ({UploadMenuValidator.MaxSourceArtifactRefChars}) must not reject a "
            + $"ref the resolver would accept (worst case {worstCaseResolvableRef} chars)");
    }

    /// <summary>
    /// And the canonical <c>inline:</c> form at the resolver's exact cap must pass, measured through
    /// the resolver's own constant rather than a copied number. A character is never fewer than one
    /// UTF-8 byte, so an at-cap ASCII document is the largest <c>inline:</c> ref there is.
    /// </summary>
    [Fact]
    public async Task An_at_cap_inline_document_passes()
    {
        var atCap = "inline:" + new string('x', InlineMenuArtifactTextResolver.MaxMenuTextBytes);
        Assert.Equal(
            InlineMenuArtifactTextResolver.MaxMenuTextBytes + "inline:".Length,
            Encoding.UTF8.GetByteCount(atCap));

        var result = await _validator.TestValidateAsync(
            Valid(atCap), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
