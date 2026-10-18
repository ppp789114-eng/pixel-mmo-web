using System.Net.WebSockets;

namespace PixelMmo.Server.Game;

public sealed class GameSession(WebSocket socket, Guid ownerId, GameCharacter character)
{
    public WebSocket Socket { get; } = socket;
    public Guid OwnerId { get; } = ownerId;
    public GameCharacter Character { get; set; } = character;
    public SemaphoreSlim SendGate { get; } = new(1, 1);
    public DateTime LastMoveAt { get; set; } = DateTime.MinValue;
    public int LastMoveSequence { get; set; }
    public DateTime LastAttackAt { get; set; } = DateTime.MinValue;
    public DateTime LastChatAt { get; set; } = DateTime.MinValue;
    public HashSet<string> ProcessedCommands { get; } = new(StringComparer.Ordinal);
    public Queue<string> ProcessedCommandOrder { get; } = new();
}
