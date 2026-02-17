using CrossService;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Google.Protobuf;
using System.Net.Http;
using System.Buffers;
using System.Security.Cryptography;
using Grpc.Core;
using System.Net;
using System.Collections.Concurrent;

namespace WebInterface.Services;

public class CompressionService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CompressionService> _logger;
    private readonly LocalCompressionService _localCompressionService;
    private readonly bool _useLocal;
    private readonly Cross.Services.Cross.CrossService? _localCrossService;
    private static readonly ConcurrentDictionary<string, GrpcChannel> _channelByEndpoint = new();
    private static readonly object _crossResolveLock = new object();
    private static DateTime _crossResolveAt = DateTime.MinValue;
    private static string[] _resolvedCrossIps = Array.Empty<string>();
    private static int _crossRoundRobinCounter = -1;

    public CompressionService(
        IConfiguration configuration, 
        ILogger<CompressionService> logger,
        LocalCompressionService localCompressionService)
    {
        _configuration = configuration;
        _logger = logger;
        _localCompressionService = localCompressionService;
        var url = _configuration["CrossService:Url"] ?? "http://cross:5000";
        _useLocal = url.Equals("local", StringComparison.OrdinalIgnoreCase);

        if (_useLocal)
        {
            _logger.LogInformation("CompressionService running in LOCAL in-process mode (no gRPC to Cross).");
            _localCrossService = new Cross.Services.Cross.CrossService();
        }
    }

    private FileService.FileServiceClient GetClient()
    {
        // Enable h2c for local (unencrypted) gRPC endpoints.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var url = _configuration["CrossService:Url"] ?? "http://cross:5000";

        // Friendly local-dev fallback: if you're not running in Docker, "http://cross:5000"
        // won't resolve. Prefer localhost in that case.
        if (!File.Exists("/.dockerenv") && url.Contains("://cross:", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://localhost:5000";
        }

        string endpoint = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && uri.Host != "127.0.0.1")
        {
            // Explicit per-request endpoint selection to avoid sticky single-pod behavior.
            // Resolve the headless service to pod IPs and round-robin across them.
            var now = DateTime.UtcNow;
            lock (_crossResolveLock)
            {
                if (_resolvedCrossIps.Length == 0 || now - _crossResolveAt > TimeSpan.FromSeconds(15))
                {
                    try
                    {
                        _resolvedCrossIps = Dns.GetHostAddresses(uri.Host)
                            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .Select(ip => ip.ToString())
                            .Distinct()
                            .ToArray();
                        _crossResolveAt = now;
                        _logger.LogInformation("Resolved Cross targets {Host} -> {Count} IPs", uri.Host, _resolvedCrossIps.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to resolve Cross host {Host}; falling back to service URL", uri.Host);
                        _crossResolveAt = now;
                    }
                }
            }

            if (_resolvedCrossIps.Length > 0)
            {
                int idx = Math.Abs(Interlocked.Increment(ref _crossRoundRobinCounter)) % _resolvedCrossIps.Length;
                endpoint = $"http://{_resolvedCrossIps[idx]}:{uri.Port}";
            }
        }

        var channel = _channelByEndpoint.GetOrAdd(endpoint, ep =>
        {
            // For direct IP endpoints, no DNS LB policy is needed.
            // For service fallback, keep DNS round-robin enabled.
            var opts = _localCompressionService.GetOptimizedChannelOptions();
            if (Uri.TryCreate(ep, UriKind.Absolute, out var epu)
                && !IPAddress.TryParse(epu.Host, out _)
                && !epu.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && epu.Host != "127.0.0.1")
            {
                opts.ServiceConfig = new ServiceConfig
                {
                    LoadBalancingConfigs = { new RoundRobinConfig() },
                    MethodConfigs =
                    {
                        new MethodConfig
                        {
                            Names = { MethodName.Default },
                            RetryPolicy = new RetryPolicy
                            {
                                MaxAttempts = 3,
                                InitialBackoff = TimeSpan.FromMilliseconds(150),
                                MaxBackoff = TimeSpan.FromSeconds(1),
                                BackoffMultiplier = 2,
                                RetryableStatusCodes = { StatusCode.Unavailable, StatusCode.ResourceExhausted }
                            }
                        }
                    }
                };
            }
            return GrpcChannel.ForAddress(ep, opts);
        });

        return new FileService.FileServiceClient(channel);
    }

    public async Task<byte[]> CompressFileAsync(byte[] fileBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_useLocal && _localCrossService != null)
            {
                // In-process call: skip gRPC entirely for local mode
                return await _localCrossService.CompressFile(fileBytes);
            }

            // Use optimized gRPC (automatically optimized for local mode)
            var client = GetClient();
            var request = new FileRequest
            {
                FileContent = ByteString.CopyFrom(fileBytes)
            };

            var response = await client.ProcessFileAsync(request, cancellationToken: cancellationToken);
            return response.FileContent.ToByteArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compressing file");
            throw;
        }
    }

    public async Task<CompressionStats> CompressFileToStreamWithStatsAsync(
        Stream input,
        string fileName,
        long fileLength,
        Stream output,
        Action<CompressionStats>? onStats = null,
        CancellationToken cancellationToken = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));

        // Local in-process mode: we have to materialize bytes for the current CrossService API.
        if (_useLocal && _localCrossService != null)
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, cancellationToken);
            var fileBytes = ms.ToArray();

            var (compressedBytes, referencesFound, totalChunks) =
                await _localCrossService.CompressFileWithStats(fileBytes);

            byte[] compressedHashBytes;
            using (var sha = SHA256.Create())
                compressedHashBytes = sha.ComputeHash(compressedBytes);

            var localStats = new CompressionStats
            {
                OriginalSize = (ulong)fileBytes.Length,
                CompressedSize = (ulong)compressedBytes.Length,
                ReferencesFound = (uint)Math.Max(0, referencesFound),
                TotalChunks = (uint)Math.Max(0, totalChunks),
                CompressedSha256 = ByteString.CopyFrom(compressedHashBytes)
            };

            onStats?.Invoke(localStats);
            await output.WriteAsync(compressedBytes, cancellationToken);
            await output.FlushAsync(cancellationToken);
            return localStats;
        }

        var client = GetClient();
        using var call = client.ProcessFileStream(cancellationToken: cancellationToken);

        const int chunkSize = 1024 * 1024; // 1 MiB

        // Upload in parallel while we read the server stream.
        var uploadTask = Task.Run(async () =>
        {
            await call.RequestStream.WriteAsync(new FileUploadRequest
            {
                Metadata = new FileMetadata
                {
                    FileName = fileName ?? string.Empty,
                    OriginalSize = fileLength > 0 ? (ulong)fileLength : 0,
                    ChunkSize = chunkSize,
                    Sha256 = ByteString.Empty // optional; not computing upfront to keep single-pass streaming
                }
            });

            byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                uint seq = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken);
                    if (read <= 0)
                        break;

                    await call.RequestStream.WriteAsync(new FileUploadRequest
                    {
                        Chunk = new FileChunk
                        {
                            Seq = seq++,
                            Data = ByteString.CopyFrom(buffer, 0, read),
                            Eof = false
                        }
                    });
                }

                await call.RequestStream.WriteAsync(new FileUploadRequest
                {
                    Chunk = new FileChunk
                    {
                        Seq = 0,
                        Data = ByteString.Empty,
                        Eof = true
                    }
                });

                await call.RequestStream.CompleteAsync();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, cancellationToken);

        CompressionStats? stats = null;
        using var compressedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                var msg = call.ResponseStream.Current;
                switch (msg.PayloadCase)
                {
                    case FileUploadResponse.PayloadOneofCase.Stats:
                        stats = msg.Stats;
                        onStats?.Invoke(stats);
                        break;

                    case FileUploadResponse.PayloadOneofCase.Chunk:
                        var chunk = msg.Chunk;
                        if (chunk.Data != null && chunk.Data.Length > 0)
                        {
                            // Avoid extra copies when possible
                            await output.WriteAsync(chunk.Data.Memory, cancellationToken);
                            compressedHash.AppendData(chunk.Data.Span);
                        }

                        if (chunk.Eof)
                        {
                            await output.FlushAsync(cancellationToken);
                            goto Done;
                        }
                        break;

                    case FileUploadResponse.PayloadOneofCase.Error:
                        throw new InvalidOperationException(msg.Error?.Message ?? "Cross stream error");

                    default:
                        break;
                }
            }

        Done:
            await uploadTask; // ensure upload completed successfully

            stats ??= new CompressionStats();

            if (stats.CompressedSha256 != null && stats.CompressedSha256.Length == 32)
            {
                var computed = compressedHash.GetHashAndReset();
                if (!stats.CompressedSha256.Span.SequenceEqual(computed))
                    throw new InvalidOperationException("Compressed SHA-256 mismatch while streaming response.");
            }

            return stats;
        }
        catch
        {
            // If the server fails, the upload task might still be running.
            try { await uploadTask; } catch { /* ignore secondary */ }
            throw;
        }
    }

    public async Task<(byte[] CompressedBytes, int ReferencesFound, int TotalChunks)> CompressFileWithStatsAsync(
        byte[] fileBytes,
        CancellationToken cancellationToken = default)
    {
        if (_useLocal && _localCrossService != null)
        {
            // In-process call: we can return real stats
            return await _localCrossService.CompressFileWithStats(fileBytes);
        }

        // gRPC mode: compressed bytes only (stats not available without changing proto)
        var bytes = await CompressFileAsync(fileBytes, cancellationToken);
        return (bytes, 0, 0);
    }

    public async Task<byte[]> DecompressFileAsync(byte[] compressedBytes, CancellationToken cancellationToken = default)
    {
        if (_useLocal && _localCrossService != null)
        {
            return await _localCrossService.DecompressFile(compressedBytes);
        }

        var client = GetClient();
        var response = await client.DecompressFileAsync(
            new FileRequest { FileContent = ByteString.CopyFrom(compressedBytes) },
            cancellationToken: cancellationToken);
        return response.FileContent.ToByteArray();
    }
}

