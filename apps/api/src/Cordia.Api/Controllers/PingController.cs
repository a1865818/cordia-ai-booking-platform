using Cordia.Application.Handlers.Ping;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PingController : ControllerBase
{
    private readonly IMediator _mediator;

    public PingController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Sample endpoint that flows through the Application layer.</summary>
    [HttpGet]
    public async Task<ActionResult<PingResult>> Get([FromQuery] string name = "world")
    {
        var result = await _mediator.Send(new GetPingQuery(name));
        return Ok(result);
    }
}
