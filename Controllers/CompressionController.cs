using CrossService;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using WebInterface.Services;
using WebInterface.Models;

namespace WebInterface.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CompressionController : ControllerBase
{
    private readonly CompressionService _compressionService;
    private readonly StatisticsService _statisticsService;
    private readonly ILogger<CompressionController> _logger;

    public CompressionController(
        CompressionService compressionService,
        StatisticsService statisticsService,
        ILogger<CompressionController> logger)
    {
        _compressionService = compressionService;
        _statisticsService = statisticsService;
        _logger = logger;
    }

    [HttpPost("compress")]
    public async Task<IActionResult> CompressFile(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        try
        {
            var originalSize = (int)Math.Min(int.MaxValue, file.Length);
            var startTime = DateTime.UtcNow;

            // Compress via streaming (avoids buffering the input file in memory).
            using var compressedStream = new MemoryStream();
            using var input = file.OpenReadStream();
            CompressionStats grpcStats = await _compressionService.CompressFileToStreamWithStatsAsync(
                input,
                file.FileName,
                file.Length,
                compressedStream,
                onStats: null,
                cancellationToken: cancellationToken);

            var endTime = DateTime.UtcNow;
            var duration = (endTime - startTime).TotalMilliseconds;
            var compressedBytes = compressedStream.ToArray();
            var compressedSize = compressedBytes.Length;
            var originalSizeForRatio = grpcStats.OriginalSize > 0 ? (double)grpcStats.OriginalSize : (double)Math.Max(0, originalSize);
            var compressionRatio = originalSizeForRatio > 0 ? (double)compressedSize / originalSizeForRatio : 0;
            var savings = originalSize - compressedSize;
            var savingsPercent = originalSize > 0 ? (double)savings / originalSize * 100 : 0;

            // Save statistics
            var statEntity = new CompressionStatistic
            {
                Id = Guid.NewGuid(),
                FileName = file.FileName,
                OriginalSize = originalSize,
                CompressedSize = compressedSize,
                CompressionRatio = compressionRatio,
                SavingsBytes = savings,
                SavingsPercent = savingsPercent,
                DurationMs = duration,
                ReferencesFound = (int)grpcStats.ReferencesFound,
                TotalChunks = (int)grpcStats.TotalChunks,
                Timestamp = DateTime.UtcNow,
                Status = "Success"
            };
            await _statisticsService.SaveStatisticsAsync(statEntity);

            return Ok(new CompressionResponse
            {
                Success = true,
                OriginalSize = originalSize,
                CompressedSize = compressedSize,
                CompressionRatio = compressionRatio,
                SavingsBytes = savings,
                SavingsPercent = savingsPercent,
                DurationMs = duration,
                ReferencesFound = (int)grpcStats.ReferencesFound,
                TotalChunks = (int)grpcStats.TotalChunks,
                CompressedData = Convert.ToBase64String(compressedBytes),
                StatisticId = statEntity.Id
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compression failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("compress-binary")]
    public async Task<IActionResult> CompressFileBinary(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        try
        {
            // Delay writing body until we get stats, so we can still set headers.
            bool headersSet = false;

            string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(file.FileName) ? "file" : file.FileName);
            string downloadName = safeName + ".ccf";

            using var input = file.OpenReadStream();
            await _compressionService.CompressFileToStreamWithStatsAsync(
                input,
                safeName,
                file.Length,
                Response.Body,
                onStats: s =>
                {
                    if (headersSet) return;
                    headersSet = true;

                    Response.ContentType = "application/octet-stream";
                    Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment")
                    {
                        FileNameStar = downloadName
                    }.ToString();

                    Response.Headers["X-Cross-Original-Size"] = s.OriginalSize.ToString();
                    Response.Headers["X-Cross-Compressed-Size"] = s.CompressedSize.ToString();
                    Response.Headers["X-Cross-References-Found"] = s.ReferencesFound.ToString();
                    Response.Headers["X-Cross-Total-Chunks"] = s.TotalChunks.ToString();
                    Response.Headers["X-Cross-Compressed-Sha256"] = Convert.ToHexString(s.CompressedSha256.ToByteArray());
                },
                cancellationToken: cancellationToken);

            // Response body already written.
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Binary compression failed");
            if (Response.HasStarted)
            {
                // Best effort: connection will likely be seen as failed by client.
                return new EmptyResult();
            }
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("decompress")]
    public async Task<IActionResult> DecompressFile([FromBody] DecompressRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(request.CompressedData))
        {
            return BadRequest("No compressed data provided");
        }

        try
        {
            var compressedBytes = Convert.FromBase64String(request.CompressedData);
            var startTime = DateTime.UtcNow;

            // Decompress
            var decompressedBytes = await _compressionService.DecompressFileAsync(compressedBytes, cancellationToken);

            var endTime = DateTime.UtcNow;
            var duration = (endTime - startTime).TotalMilliseconds;

            return Ok(new DecompressResponse
            {
                Success = true,
                DecompressedSize = decompressedBytes.Length,
                DurationMs = duration,
                DecompressedData = Convert.ToBase64String(decompressedBytes)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decompression failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("statistics")]
    public async Task<IActionResult> GetStatistics([FromQuery] int limit = 100)
    {
        var stats = await _statisticsService.GetStatisticsAsync(limit);
        return Ok(stats);
    }

    [HttpGet("statistics/{id}")]
    public async Task<IActionResult> GetStatistic(Guid id)
    {
        var stat = await _statisticsService.GetStatisticAsync(id);
        if (stat == null)
        {
            return NotFound();
        }
        return Ok(stat);
    }

    [HttpGet("statistics/summary")]
    public async Task<IActionResult> GetSummary()
    {
        var summary = await _statisticsService.GetSummaryAsync();
        return Ok(summary);
    }
}






