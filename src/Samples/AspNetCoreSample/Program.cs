var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => new { application = "OpenDevelop ASP.NET Core sample", status = "running" });
app.MapGet("/health", () => "healthy");

app.Run();
