using System.Security.Cryptography;
using System.Text;
using Grpc.Net.Client;
using System.Net.Http;

namespace WebInterface.Services;

/// <summary>
/// Service for detecting local mode and optimizing gRPC calls when services are on the same network.
/// In Docker, services on the same network can use optimized gRPC settings.
/// </summary>
public class LocalCompressionService
{
    private readonly ILogger<LocalCompressionService>? _logger;
    private static readonly bool _isLocalMode;

    static LocalCompressionService()
    {
        // Detect if we're in Docker/local environment
        _isLocalMode = IsRunningInDocker() || IsLocalhostEnvironment();
    }

    public LocalCompressionService(ILogger<LocalCompressionService>? logger = null)
    {
        _logger = logger;
        if (_isLocalMode)
        {
            _logger?.LogInformation("Local mode detected - gRPC calls will be optimized for localhost");
        }
    }

    public static bool IsLocalMode => _isLocalMode;

    private static bool IsRunningInDocker()
    {
        // Check for Docker environment indicators
        if (File.Exists("/.dockerenv"))
            return true;

        // Check for Docker container in cgroup
        if (File.Exists("/proc/self/cgroup"))
        {
            try
            {
                var cgroup = File.ReadAllText("/proc/self/cgroup");
                if (cgroup.Contains("docker") || cgroup.Contains("containerd"))
                    return true;
            }
            catch { }
        }

        return false;
    }

    private static bool IsLocalhostEnvironment()
    {
        // Check if we're likely running locally (not in a distributed environment)
        var gatewayUrl = Environment.GetEnvironmentVariable("CrossService__Url") 
                        ?? Environment.GetEnvironmentVariable("CROSS_SERVICE_URL");
        
        if (string.IsNullOrEmpty(gatewayUrl))
            return true; // Default to local if not specified

        // If URL is localhost, 127.0.0.1, or a Docker service name, consider it local
        return gatewayUrl.Contains("localhost") 
            || gatewayUrl.Contains("127.0.0.1") 
            || gatewayUrl.Contains("cross") // Docker service name
            || !gatewayUrl.Contains("http"); // No protocol = likely local
    }

    /// <summary>
    /// Gets optimized gRPC channel options for local mode (reduced overhead).
    /// </summary>
    public GrpcChannelOptions GetOptimizedChannelOptions()
    {
        if (!_isLocalMode)
        {
            return new GrpcChannelOptions(); // Use defaults for remote
        }

        // Optimized settings for localhost/Docker network
        return new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 1000 * 1024 * 1024,
            MaxSendMessageSize = 1000 * 1024 * 1024,
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(10), // More frequent for local
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
            }
        };
    }
}

