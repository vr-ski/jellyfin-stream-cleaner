using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamCleaner.Models;
using Jellyfin.Plugin.StreamCleaner.Services;
using Jellyfin.Plugin.StreamCleaner.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StreamCleaner.Controllers;

/// <summary>Administrator API for Stream Cleaner.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("StreamCleaner")]
public sealed class StreamCleanerController : ControllerBase
{
    private readonly StreamCleanerService _service;
    private readonly ITaskManager _taskManager;

    public StreamCleanerController(StreamCleanerService service, ITaskManager taskManager)
    {
        _service = service;
        _taskManager = taskManager;
    }

    [HttpGet("Status")]
    public async Task<ActionResult<JobState?>> GetStatus(CancellationToken ct)
        => Ok(await _service.GetStateAsync(ct).ConfigureAwait(false));

    [HttpPost("Probe")]
    public async Task<ActionResult<ProbeResult>> Probe([FromBody] ProbeRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest("Path is required.");
        return Ok(await _service.ProbePathAsync(req.Path, ct).ConfigureAwait(false));
    }

    [HttpPost("Submit")]
    public async Task<ActionResult<JobState>> Submit([FromBody] JobRequest? request, CancellationToken ct)
    {
        if (request is null) return BadRequest("Request body is required.");
        if (string.IsNullOrWhiteSpace(request.InputPath)) return BadRequest("InputPath is required.");
        if (request.Selection is null) return BadRequest("Selection is required.");
        try
        {
            var state = await _service.SubmitAsync(request, ct).ConfigureAwait(false);
            _taskManager.QueueScheduledTask<StreamCleanerTask>();
            return Ok(state);
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPost("Cancel")]
    public ActionResult Cancel()
    {
        try { _taskManager.CancelIfRunning<StreamCleanerTask>(); } catch { }
        return Ok();
    }
}
