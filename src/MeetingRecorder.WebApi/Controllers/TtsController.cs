using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingRecorder.WebApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TtsController : ApiControllerBase
{
    private readonly ISarvamApiService _sarvamApiService;

    public TtsController(ISarvamApiService sarvamApiService)
    {
        _sarvamApiService = sarvamApiService;
    }

    /// <summary>
    /// Convert transcript text to speech audio (base64 string).
    /// </summary>
    [HttpPost("synthesize")]
    [ProducesResponseType(typeof(ApiResponse<SynthesizeTtsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<SynthesizeTtsResponse>>> Synthesize(
        [FromBody] SynthesizeTtsRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);

        var audioBase64 = await _sarvamApiService.SynthesizeTextToSpeechAsync(request.Text, request.LanguageCode, request.ApiKey, ct);
        var response = new SynthesizeTtsResponse(audioBase64);

        return Envelope(response, "TTS audio synthesized successfully.");
    }
}
