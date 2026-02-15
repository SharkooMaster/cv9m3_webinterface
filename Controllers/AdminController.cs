using Microsoft.AspNetCore.Mvc;
using WebInterface.Services;

namespace WebInterface.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly DatabaseService _databaseService;
    private readonly StatisticsService _statisticsService;
    private readonly ChunkStorageService _chunkStorageService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        DatabaseService databaseService,
        StatisticsService statisticsService,
        ChunkStorageService chunkStorageService,
        ILogger<AdminController> logger)
    {
        _databaseService = databaseService;
        _statisticsService = statisticsService;
        _chunkStorageService = chunkStorageService;
        _logger = logger;
    }

    [HttpPost("clear-database")]
    public async Task<IActionResult> ClearDatabase()
    {
        try
        {
            // Clear database tables
            var dbSuccess = await _databaseService.ClearAllDataAsync();
            if (!dbSuccess)
            {
                return StatusCode(500, new { success = false, message = "Failed to clear database tables. Check logs for details." });
            }

            // Clear chunk files
            var chunksSuccess = _chunkStorageService.ClearAllChunks();
            if (!chunksSuccess)
            {
                _logger.LogWarning("Database cleared but failed to clear chunk files");
                return StatusCode(500, new { success = false, message = "Database cleared but failed to clear chunk files. Check logs for details." });
            }

            _logger.LogInformation("Database and chunk files cleared successfully");
            return Ok(new { success = true, message = "Database and chunk files cleared successfully. All vectors, buckets, and chunks have been removed." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing database and chunks");
            return StatusCode(500, new { success = false, message = $"Error: {ex.Message}" });
        }
    }

           [HttpGet("database-stats")]
           public async Task<IActionResult> GetDatabaseStats()
           {
               try
               {
                   var counts = await _databaseService.GetTableCountsAsync();
                   return Ok(counts);
               }
               catch (Exception ex)
               {
                   _logger.LogError(ex, "Error getting database stats");
                   return StatusCode(500, new { error = ex.Message });
               }
           }

    [HttpPost("clear-statistics")]
    public async Task<IActionResult> ClearStatistics()
    {
        try
        {
            var success = await _statisticsService.ClearStatisticsAsync();
            if (!success)
            {
                return StatusCode(500, new { success = false, message = "Failed to clear statistics. Check logs for details." });
            }

            _logger.LogInformation("Statistics cleared successfully");
            return Ok(new { success = true, message = "Statistics cleared successfully. All compression statistics have been removed." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing statistics");
            return StatusCode(500, new { success = false, message = $"Error: {ex.Message}" });
        }
    }

           [HttpGet("logs")]
           public async Task<IActionResult> GetLogs([FromQuery] string service = "agent-1", [FromQuery] int lines = 100)
           {
               try
               {
                   // Use Docker API or shell command to get logs
                   var processStartInfo = new System.Diagnostics.ProcessStartInfo
                   {
                       FileName = "docker",
                       Arguments = $"logs --tail {lines} crossv9-{service} 2>&1",
                       RedirectStandardOutput = true,
                       RedirectStandardError = true,
                       UseShellExecute = false,
                       CreateNoWindow = true
                   };

                   using var process = System.Diagnostics.Process.Start(processStartInfo);
                   if (process == null)
                   {
                       return StatusCode(500, new { error = "Failed to start docker process" });
                   }

                   var output = await process.StandardOutput.ReadToEndAsync();
                   var error = await process.StandardError.ReadToEndAsync();
                   await process.WaitForExitAsync();

                   return Ok(new
                   {
                       service = service,
                       lines = lines,
                       logs = output + error,
                       timestamp = DateTime.UtcNow
                   });
               }
               catch (Exception ex)
               {
                   _logger.LogError(ex, "Error getting logs");
                   return StatusCode(500, new { error = ex.Message });
               }
           }
}

