import type { CharacterSummary, ClientMessage, MapId, ServerMessage } from '../game/protocol';
import { isServerMessage } from '../game/protocol';

export type SocketStatus = 'connecting' | 'connected' | 'disconnected';

export class GameSocket {
  private socket: WebSocket | undefined;
  private closedByClient = false;
  private reconnectAttempt = 0;
  private reconnectTimer: number | undefined;
  private sequence = 0;
  private selectedCharacterId = '';

  constructor(
    private readonly url: string,
    private readonly accessToken: string,
    private readonly onMessage: (message: ServerMessage) => void,
    private readonly onStatus: (status: SocketStatus) => void,
  ) {}

  connect(): void {
    this.closedByClient = false;
    this.openSocket();
  }

  close(): void {
    this.closedByClient = true;
    if (this.reconnectTimer !== undefined) window.clearTimeout(this.reconnectTimer);
    this.socket?.close(1000, 'client closed');
  }

  createCharacter(name: string, job: CharacterSummary['job']): void {
    this.send({ type: 'create-character', name, job });
  }

  selectCharacter(characterId: string): void {
    this.selectedCharacterId = characterId;
    this.send({ type: 'select-character', characterId });
  }

  changeMap(mapId: MapId): void {
    this.send({ type: 'change-map', mapId });
  }

  interact(targetId: string): void {
    this.send({ type: 'interact', targetId });
  }

  equip(itemKey: string): void {
    this.send({ type: 'equip', itemKey });
  }

  unequipWeapon(): void {
    this.send({ type: 'unequip', slot: 'weapon' });
  }

  move(dx: number, dy: number): void {
    this.send({ type: 'move', dx, dy, sequence: ++this.sequence });
  }

  attack(targetId: string): void {
    this.send({ type: 'attack', targetId, commandId: crypto.randomUUID() });
  }

  castStarbolt(targetId: string): void {
    this.send({ type: 'cast', skillKey: 'starbolt', targetId, commandId: crypto.randomUUID() });
  }

  chat(text: string): void {
    this.send({ type: 'chat', text });
  }

  private openSocket(): void {
    if (this.closedByClient) return;
    this.onStatus('connecting');
    const socket = new WebSocket(this.url);
    this.socket = socket;

    socket.addEventListener('open', () => {
      this.onStatus('connected');
      // Authenticate in the first frame; do not put bearer tokens in the URL.
      socket.send(JSON.stringify({ type: 'auth', version: 2, accessToken: this.accessToken } satisfies ClientMessage));
    });

    socket.addEventListener('message', (event) => {
      try {
        const message: unknown = JSON.parse(String(event.data));
        if (isServerMessage(message)) {
          if (message.type === 'auth-ok') {
            this.reconnectAttempt = 0;
            if (this.selectedCharacterId) this.selectCharacter(this.selectedCharacterId);
          }
          this.onMessage(message);
        }
      } catch {
        this.onMessage({ type: 'error', code: 'invalid-server-message', message: 'The game server sent an unreadable message.' });
      }
    });

    socket.addEventListener('close', () => {
      this.onStatus('disconnected');
      if (!this.closedByClient) this.scheduleReconnect();
    });

    socket.addEventListener('error', () => {
      // The close event handles status and reconnect scheduling.
      socket.close();
    });
  }

  private send(message: ClientMessage): void {
    if (this.socket?.readyState !== WebSocket.OPEN) return;
    this.socket.send(JSON.stringify(message));
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempt >= 5 || this.reconnectTimer !== undefined) return;
    const delay = Math.min(30_000, 1_000 * 2 ** this.reconnectAttempt);
    this.reconnectAttempt += 1;
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = undefined;
      this.openSocket();
    }, delay);
  }
}
