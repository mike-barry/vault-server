var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5111");

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true);

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
