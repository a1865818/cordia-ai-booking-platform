using FluentValidation;

namespace Cordia.Application.Handlers.Menu.UploadMenu;

/// <summary>Validates <see cref="UploadMenuCommand"/> before the upload transaction opens.</summary>
public sealed class UploadMenuValidator : AbstractValidator<UploadMenuCommand>
{
    /// <summary>
    /// Ceiling on <c>SourceArtifactRef</c>, in CHARACTERS. 1 MiB.
    ///
    /// <para><b>Why any bound at all.</b> The rule below used to be <c>NotEmpty()</c> and nothing
    /// else, so the only cap anywhere on the path was <c>InlineMenuArtifactTextResolver</c>'s
    /// 256 KiB — enforced asynchronously, at extraction time, on the queue lane. Everything between
    /// 256 KiB and Kestrel's ~28.6 MB default therefore returned <c>202 Accepted</c>, was durably
    /// committed, burned a publisher claim and an inbox transaction, and only then failed with a
    /// generic reason. For an <c>inline:</c> ref it also stored the whole document in
    /// <c>outbox_messages.payload</c> unmasked (<b>#95</b> — the underlying problem, whose real fix
    /// is #84 moving artifacts into object storage; this bound is the mitigation, not the cure).</para>
    ///
    /// <para><b>Why this number.</b> Three quantities have to line up, and they are measured in
    /// different units:</para>
    /// <list type="number">
    ///   <item>The resolver's authoritative cap is <b>256 KiB of UTF-8 bytes of DECODED text</b>
    ///     (<c>MAX_MENU_TEXT_BYTES</c> on the Python side too).</item>
    ///   <item>FluentValidation's <c>MaximumLength</c> counts <b>UTF-16 characters of the encoded
    ///     ref</b>, which is neither the same unit nor the same string.</item>
    ///   <item>The ref carries a prefix — <c>inline:</c> (7) or a <c>data:text/plain;…;base64,</c>
    ///     header (up to ~40).</item>
    /// </list>
    ///
    /// <para>The bound has to be loose enough that <b>nothing the resolver would accept is rejected
    /// here</b> — a validator that fails an upload the pipeline could have processed is a worse bug
    /// than the one being fixed. The largest resolvable ref is the percent-encoded <c>data:</c>
    /// form: every byte may encode as <c>%XX</c>, three characters, so an at-cap document is
    /// <c>3 × 262144 = 786,432</c> characters plus its header. (<c>inline:</c> tops out at 262,151
    /// — a character is never fewer than one UTF-8 byte — and <c>;base64</c> at ~349,568.)</para>
    ///
    /// <para>1 MiB is the next clean number above that worst case, and it is not an arbitrary one:
    /// it is <c>4 × MAX_MENU_TEXT_BYTES</c>, the same multiplier the extraction route's
    /// <c>MAX_REQUEST_BODY_BYTES</c> already uses, chosen there for the same reason — "one at-cap
    /// document, encoded". So anything over this bound is <b>provably</b> unresolvable whichever of
    /// the three encodings it uses, and is refused with a 400 at ingress instead of a durable commit
    /// and a terminal <c>ExtractFailed</c>.</para>
    ///
    /// <para>The reverse direction is deliberately NOT claimed: a 262,144-character CJK
    /// <c>inline:</c> ref is 786,432 UTF-8 bytes, passes this rule and is still refused by the
    /// resolver. Characters cannot express a byte budget, and the resolver stays the authority.
    /// This is a cheap pre-filter for the definitely-doomed, not a second contract.</para>
    /// </summary>
    internal const int MaxSourceArtifactRefChars = 4 * 256 * 1024;

    public UploadMenuValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty();
        RuleFor(x => x.SourceArtifactRef).NotEmpty().MaximumLength(MaxSourceArtifactRefChars);
        RuleFor(x => x.VersionLabel).NotEmpty();
        RuleFor(x => x.ReasonCode).NotEmpty();
        RuleFor(x => x.ActorRole).NotEmpty();
    }
}
