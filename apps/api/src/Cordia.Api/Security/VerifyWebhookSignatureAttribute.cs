using Cordia.Domain.Interfaces;
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
        var secret = services.GetRequiredService<ISecretStore>().GetSecret("NotificationsWebhookSecret")
            ?? services.GetRequiredService<IConfiguration>()["Notifications:WebhookSecret"];
        if (string.IsNullOrEmpty(secret))
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

        var verified = WebhookSignature.Verify(
            secret,
            http.Request.Headers[WebhookSignature.TimestampHeader].ToString(),
            http.Request.Headers[WebhookSignature.SignatureHeader].ToString(),
            services.GetRequiredService<ITenantContext>().TenantId,
            body,
            DateTimeOffset.UtcNow);

        if (!verified)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        await next();
    }
}
