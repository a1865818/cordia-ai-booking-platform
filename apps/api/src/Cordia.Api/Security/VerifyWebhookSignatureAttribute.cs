using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cordia.Api.Security;

/// <summary>
/// Verifies <see cref="WebhookSignature"/> before model binding runs (#74).
///
/// <para><b>Why a resource filter.</b> The HMAC covers the raw body bytes, and <c>[FromBody]</c>
/// consumes the request stream. A resource filter runs before binding, so it can buffer the body,
/// verify it, rewind it, and let binding read it again — keeping the endpoint's documented JSON
/// contract, and refusing an unsigned request before any of its content is parsed.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class VerifyWebhookSignatureAttribute : Attribute, IAsyncResourceFilter
{
    /// <summary>A delivery receipt is three short fields; nothing legitimate comes near this.</summary>
    internal const int MaxBodyBytes = 64 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;

        // Spec 14 §24.4: the ISecretStore custody seam first, the legacy config key as the migration
        // fallback. Never logged or serialized.
        // Blank counts as unset, for the fallback AND the refusal: a set-but-empty environment
        // variable must neither hide the legacy key nor become an HMAC key anyone can compute.
        var storeSecret = services.GetRequiredService<ISecretStore>().GetSecret("NotificationsWebhookSecret");
        var secret = string.IsNullOrWhiteSpace(storeSecret)
            ? services.GetRequiredService<IConfiguration>()["Notifications:WebhookSecret"]
            : storeSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        if (http.Request.ContentLength > MaxBodyBytes)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
            return;
        }

        http.Request.EnableBuffering(bufferThreshold: MaxBodyBytes, bufferLimit: MaxBodyBytes);
        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            body = buffer.ToArray();
        }
        catch (IOException)
        {
            // Chunked bodies carry no Content-Length; the buffer limit is what stops those.
            context.Result = new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
            return;
        }
        http.Request.Body.Position = 0;

        // The tenant is the RAW X-Tenant-Id header, not the resolved tenant context. A provider
        // cannot present a signed tenant context, and outside Development the X-Tenant-Id shim is
        // off, so the resolved context would be empty and no genuine receipt could ever verify.
        // The signature is what makes this header trustworthy: only the secret holder can sign it.
        var signedTenant = http.Request.Headers["X-Tenant-Id"].ToString();

        var verified = WebhookSignature.Verify(
            secret,
            http.Request.Headers[WebhookSignature.TimestampHeader].ToString(),
            http.Request.Headers[WebhookSignature.SignatureHeader].ToString(),
            signedTenant,
            body,
            DateTimeOffset.UtcNow);

        if (!verified)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Establish the tenant the signature vouches for. Where a tenant was already resolved (the
        // shim, in Development) it must be the same one — SetTenant refuses a different tenant.
        if (!string.IsNullOrWhiteSpace(signedTenant))
        {
            try
            {
                services.GetRequiredService<TenantContext>().SetTenant(signedTenant);
            }
            catch (InvalidOperationException)
            {
                context.Result = new UnauthorizedResult();
                return;
            }
        }

        await next();
    }
}
