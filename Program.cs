using StarlinkExporter;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StarlinkOptions>(builder.Configuration.GetSection("Starlink"));
builder.Services.AddSingleton<MetricsSnapshot>();
builder.Services.AddHostedService<StarlinkPoller>();

var app = builder.Build();

app.MapGet("/metrics", (MetricsSnapshot snapshot) =>
    Results.Text(snapshot.Text, "text/plain; version=0.0.4; charset=utf-8"));
app.MapGet("/", () => Results.Content(
    "<html><body><h1>Starlink exporter</h1><a href=\"/metrics\">/metrics</a></body></html>", "text/html"));

app.Run();
