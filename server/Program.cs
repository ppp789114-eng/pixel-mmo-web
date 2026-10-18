using PixelMmo.Server.Game;
using PixelMmo.Server.Persistence;
using PixelMmo.Server.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<SupabaseAuthService>();
builder.Services.AddSingleton<GameRepository>();
builder.Services.AddSingleton<GameWorld>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<GameWorld>());
builder.Services.AddSingleton<GameSocketHandler>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(25),
    ReceiveBufferSize = 8 * 1024,
});

app.MapGet("/healthz", (GameRepository repository) => Results.Ok(new
{
    status = "ok",
    persistence = repository.IsPersistent ? "supabase-postgres" : "development-memory",
    protocolVersion = GameRules.ProtocolVersion,
}));

app.Map("/ws/game", async (HttpContext context, GameSocketHandler handler) => await handler.HandleAsync(context));

app.Run();

public partial class Program;
