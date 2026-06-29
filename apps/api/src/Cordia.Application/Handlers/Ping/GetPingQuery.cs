using MediatR;

namespace Cordia.Application.Handlers.Ping;

/// <summary>Sample query demonstrating the Application (CQRS) layer.</summary>
public record GetPingQuery(string Name) : IRequest<PingResult>;

public record PingResult(string Message);

public class GetPingQueryHandler : IRequestHandler<GetPingQuery, PingResult>
{
    public Task<PingResult> Handle(GetPingQuery request, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "world" : request.Name;
        return Task.FromResult(new PingResult($"pong, {name}"));
    }
}
