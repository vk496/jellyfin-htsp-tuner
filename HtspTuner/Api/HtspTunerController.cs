using System.ComponentModel.DataAnnotations;
using HtspTuner.Configuration;
using HtspTuner.Htsp;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HtspTuner.Api;

/// <summary>The body of a connection test: the fields needed to reach a Tvheadend server.</summary>
public class TestConnectionRequest
{
    /// <summary>Gets or sets the host name or IP.</summary>
    [Required]
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTSP port.</summary>
    public int HtspPort { get; set; } = 9982;

    /// <summary>Gets or sets the user name (empty for anonymous).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the password.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>The result of a connection test.</summary>
public class TestConnectionResult
{
    /// <summary>Gets or sets a value indicating whether the connection succeeded.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets a human-readable message describing the outcome.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Plugin REST API: a connection test the config page can call, so failures show in the UI instead of
/// the server log.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("HtspTuner")]
public class HtspTunerController : ControllerBase
{
    private readonly ILogger<HtspTunerController> _logger;

    /// <summary>Initializes a new instance of the <see cref="HtspTunerController"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public HtspTunerController(ILogger<HtspTunerController> logger)
    {
        _logger = logger;
    }

    /// <summary>Tests a Tvheadend connection with the given credentials.</summary>
    /// <param name="request">The connection details.</param>
    /// <returns>Whether it connected, and a message.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestConnectionResult>> TestConnection([FromBody] TestConnectionRequest request)
    {
        var cfg = Plugin.Instance!.Configuration;
        var options = new HtspClientOptions
        {
            Host = request.Host,
            Port = request.HtspPort,
            Username = request.Username,
            Password = request.Password,
            HttpPort = cfg.HttpPort,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var client = new HtspClient(options, _logger);
        try
        {
            await client.ConnectOnlyAsync(cts.Token).ConfigureAwait(false);
            return new TestConnectionResult { Ok = true, Message = $"Connected to {request.Host}:{request.HtspPort}." };
        }
        catch (OperationCanceledException)
        {
            return new TestConnectionResult { Ok = false, Message = $"Timed out reaching {request.Host}:{request.HtspPort}." };
        }
        catch (Exception ex)
        {
            return new TestConnectionResult { Ok = false, Message = ex.Message };
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
