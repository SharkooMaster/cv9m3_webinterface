using Microsoft.AspNetCore.Server.Kestrel.Core;
using WebInterface.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<LocalCompressionService>();
builder.Services.AddSingleton<DynamicResourceManager>();
builder.Services.AddSingleton<CompressionService>();
builder.Services.AddSingleton<StatisticsService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<ChunkStorageService>();

// Configure Kestrel
builder.WebHost.ConfigureKestrel(options =>
{
    // Default ports match docker/k8s manifests (5000/5001).
    // Override for local multi-process dev via env vars:
    // - WEBINTERFACE_PORT_HTTP1=8080
    // - WEBINTERFACE_PORT_HTTP2=8081
    int http1Port = int.TryParse(Environment.GetEnvironmentVariable("WEBINTERFACE_PORT_HTTP1"), out var p1) ? p1 : 5000;
    int http2Port = int.TryParse(Environment.GetEnvironmentVariable("WEBINTERFACE_PORT_HTTP2"), out var p2) ? p2 : 5001;

    options.ListenAnyIP(http1Port, o => o.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(http2Port, o => o.Protocols = HttpProtocols.Http2);
    options.Limits.MaxRequestBodySize = 1024 * 1024 * 1024; // 1GB
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Disable response buffering middleware for SSE endpoints
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/directory"))
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        if (feature != null)
        {
            feature.DisableBuffering();
        }
    }
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();

