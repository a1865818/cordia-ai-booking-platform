using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    /// <summary>Liveness probe.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new HealthResponse("ok"));
}

public record HealthResponse(string Status);
