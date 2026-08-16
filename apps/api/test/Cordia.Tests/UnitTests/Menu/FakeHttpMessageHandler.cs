using System.Net;
using System.Text;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Menu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// The repo's first <see cref="HttpMessageHandler"/> test double. Deliberately hand-rolled rather
/// than mocked: <c>SendAsync</c> is <c>protected</c>, so a Moq setup would need
/// <c>Protected().Setup&lt;Task&lt;HttpResponseMessage&gt;&gt;("SendAsync", …)</c> — a
/// stringly-typed override that compiles happily after a rename — and none of the tests here need
/// anything Moq provides that thirty lines do not.
///
/// <para>Snapshots what the client actually put on the wire <b>during</b> the send:
/// <see cref="HttpRequestMessage"/> is disposed by the caller on the way out, which disposes its
/// content, so a test that read <c>LastRequest.Content</c> afterwards would fail on a disposed
/// stream rather than on the assertion it meant to make.</para>
/// </summary>
internal sealed class FakeHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    /// <summary>Number of times the client dialled out. Pins "the request was never sent".</summary>
    public int CallCount { get; private set; }

    public HttpMethod? LastMethod { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    public string? LastBody { get; private set; }

    /// <summary>Media type only, parameters (e.g. <c>; charset=utf-8</c>) stripped.</summary>
    public string? LastContentType { get; private set; }

    /// <summary>Every request header, snapshotted while the message was still alive.</summary>
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Answers every request with a fixed status and body.</summary>
    public static FakeHttpMessageHandler Responding(
        HttpStatusCode status, string body, string mediaType = "application/json")
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        }));

    /// <summary>Answers <c>200 application/json</c> with <paramref name="json"/>.</summary>
    public static FakeHttpMessageHandler RespondingJson(string json)
        => Responding(HttpStatusCode.OK, json);

    /// <summary>Never answers until the token it is given is cancelled (a hung agent).</summary>
    public static FakeHttpMessageHandler Hanging()
        => new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastMethod = request.Method;
        LastRequestUri = request.RequestUri;

        LastHeaders.Clear();
        foreach (var header in request.Headers)
            LastHeaders[header.Key] = string.Join(",", header.Value);

        if (request.Content is not null)
        {
            LastContentType = request.Content.Headers.ContentType?.MediaType;
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return await responder(request, cancellationToken);
    }
}

/// <summary>
/// Builds <see cref="LangGraphMenuExtractorClient"/> instances over a fake handler and owns their
/// lifetimes. Shared by the client tests and the wire-contract parity test so neither has to
/// reach into the other's helpers — and so the <see cref="HttpClient"/>s actually get disposed
/// (they were leaking one client plus one handler per test).
/// </summary>
internal sealed class MenuExtractorHarness : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public LangGraphMenuExtractorClient Create(
        FakeHttpMessageHandler handler,
        string? secret = null,
        int timeoutSeconds = 30,
        IMenuArtifactTextResolver? resolver = null,
        string correlationId = "")
    {
        var options = new LangGraphOptions
        {
            Host = "http://localhost:2024",
            ExtractTimeoutSeconds = timeoutSeconds,
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.Host),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        _disposables.Add(http);
        _disposables.Add(handler);

        var secrets = new Mock<ISecretStore>();
        secrets
            .Setup(s => s.GetSecret(LangGraphMenuExtractorClient.InternalKeySecretName))
            .Returns(secret);

        return new LangGraphMenuExtractorClient(
            http,
            resolver ?? new InlineMenuArtifactTextResolver(),
            secrets.Object,
            new StubCorrelationContext(correlationId),
            Options.Create(options),
            NullLogger<LangGraphMenuExtractorClient>.Instance);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    /// <summary>
    /// A local stub rather than the real <c>CorrelationContextHolder</c>, whose backing
    /// <c>AsyncLocal</c> is <b>static</b> — setting it in one test would bleed into whatever ran
    /// next in the same async flow.
    /// </summary>
    private sealed class StubCorrelationContext(string correlationId) : ICorrelationContext
    {
        public AmbientCorrelationContext Current { get; } =
            new(TenantId: "tenant_test", CorrelationId: correlationId);

        public void Set(AmbientCorrelationContext context) => throw new NotSupportedException();
    }
}
