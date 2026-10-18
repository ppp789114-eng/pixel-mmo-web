import Phaser from 'phaser';
import { currentSession, signInWithGoogle, supabase } from './auth';
import { GameSocket, type SocketStatus } from './network/game-socket';
import { WorldScene } from './game/world-scene';
import type { CharacterSummary, ServerMessage } from './game/protocol';
import './styles.css';

const byId = <T extends HTMLElement>(id: string): T => {
  const element = document.getElementById(id);
  if (!element) throw new Error(`Missing page element #${id}`);
  return element as T;
};

const overlay = byId('overlay');
const authNote = byId('auth-note');
const characterForm = byId('character-form');
const characterList = byId('character-list');
const gameServerUrl = import.meta.env.VITE_GAME_SERVER_URL?.trim() || '';
let gameSocket: GameSocket | undefined;
let currentAccessToken = '';
let enteredWorld = false;

function updateHud(state: Record<string, number | string>): void {
  byId('character-name').textContent = String(state.name);
  byId('character-job').textContent = String(state.job);
  byId('hp-value').textContent = `${state.hp} / ${state.maxHp}`;
  byId('mp-value').textContent = `${state.mp} / ${state.maxMp}`;
  byId('xp-value').textContent = `Lv. ${state.level} · ${state.xpPercent}%`;
  byId('hp-bar').style.width = `${Math.max(0, Math.min(100, Number(state.hp) / Number(state.maxHp) * 100))}%`;
  byId('mp-bar').style.width = `${Math.max(0, Math.min(100, Number(state.mp) / Number(state.maxMp) * 100))}%`;
  byId('xp-bar').style.width = `${Math.max(0, Math.min(100, Number(state.xpPercent)))}%`;
  byId('essence-count').textContent = String(state.essence);
  byId('inventory-label').textContent = String(state.items);
  byId('quest-count').textContent = `${state.questProgress} / 1`;
  const questText: Record<string, string> = {
    inactive: 'Speak with Elder Lyra in town.',
    active: 'Find a Moss Slime in the field.',
    complete: 'Return to Elder Lyra for your reward.',
    claimed: 'Quest complete. Your first tale is told.',
  };
  document.querySelector<HTMLElement>('.quest-card > span')!.textContent = questText[String(state.questStatus)] ?? '';
  document.querySelector<HTMLElement>('.quest-progress i')!.style.width = `${Number(state.questProgress) * 100}%`;
  byId('weapon-label').textContent = state.weapon === 'iron_sword' ? 'Sword' : state.weapon === 'oak_staff' ? 'Staff' : 'Equip';
  byId('online-count').textContent = `${state.online} adventurer${Number(state.online) === 1 ? '' : 's'} nearby`;
}

function showOverlay(show: boolean): void {
  overlay.classList.toggle('hidden', !show);
}

function showCharacterCreation(show: boolean): void {
  characterForm.classList.toggle('hidden', !show);
  characterList.classList.add('hidden');
}

function renderChat(messages: Array<{ name: string; text: string; system?: boolean }>): void {
  const log = byId('chat-log');
  log.replaceChildren();
  for (const message of messages.slice(-8)) {
    const row = document.createElement('p');
    const name = document.createElement('b');
    name.className = message.system ? 'system-name' : 'player-name';
    name.textContent = message.system ? 'SYSTEM' : message.name;
    row.append(name, document.createTextNode(` ${message.text}`));
    log.append(row);
  }
  log.scrollTop = log.scrollHeight;
}

function setConnectionStatus(status: SocketStatus): void {
  const label = byId('connection-label');
  const dot = document.querySelector('.status-dot');
  label.textContent = status === 'connected' ? 'CONNECTED' : status === 'connecting' ? 'CONNECTING' : 'RECONNECTING';
  dot?.classList.toggle('connected', status === 'connected');
}

function showCharacterChoices(characters: CharacterSummary[]): void {
  characterForm.classList.add('hidden');
  characterList.replaceChildren();
  characterList.classList.remove('hidden');
  const title = document.createElement('p');
  title.className = 'eyebrow';
  title.textContent = 'CHOOSE YOUR WAYFARER';
  characterList.append(title);
  for (const character of characters) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'character-choice';
    button.innerHTML = `<span class="choice-sigil">✦</span><span><strong></strong><small></small></span><span class="choice-arrow">↗</span>`;
    button.querySelector('strong')!.textContent = character.name;
    button.querySelector('small')!.textContent = `${character.job} · Level ${character.level}`;
    button.addEventListener('click', () => gameSocket?.selectCharacter(character.id));
    characterList.append(button);
  }
  if (characters.length < 3) {
    const create = document.createElement('button');
    create.type = 'button';
    create.className = 'secondary-button compact-button';
    create.textContent = 'Create a new character';
    create.addEventListener('click', () => showCharacterCreation(true));
    characterList.append(create);
  }
}

function handleServerMessage(message: ServerMessage, scene: WorldScene): void {
  if (message.type === 'auth-ok') {
    if (message.characters.length === 0) showCharacterCreation(true);
    else showCharacterChoices(message.characters);
    return;
  }
  if (message.type === 'entered-world') {
    enteredWorld = true;
    scene.applyServerMessage(message);
    showOverlay(false);
    byId('connection-label').textContent = 'ONLINE';
    byId('world-toast').textContent = 'Move · E interact / gate · Space strike · 1 Starbolt';
    renderChat(scene.getChat());
    return;
  }
  if (message.type === 'world-state' || message.type === 'map-changed' || message.type === 'chat' || message.type === 'inventory' || message.type === 'equipment' || message.type === 'quest' || message.type === 'quest-reward') {
    scene.applyServerMessage(message);
    if (message.type === 'chat' || message.type === 'quest-reward') renderChat(scene.getChat());
    return;
  }
  if (message.type === 'error') {
    if (enteredWorld) {
      byId('world-toast').textContent = message.message;
      return;
    }
    authNote.textContent = message.message;
    authNote.classList.add('error-note');
    showOverlay(true);
  }
}

const worldScene = new WorldScene();
worldScene.bind({
  move: (dx, dy) => gameSocket?.move(dx, dy),
  attack: (targetId) => gameSocket?.attack(targetId),
  cast: (targetId) => gameSocket?.castStarbolt(targetId),
  changeMap: (mapId) => gameSocket?.changeMap(mapId),
  interact: (targetId) => gameSocket?.interact(targetId),
  equip: (itemKey) => gameSocket?.equip(itemKey),
  unequip: () => gameSocket?.unequipWeapon(),
}, updateHud);

const game = new Phaser.Game({
  type: Phaser.AUTO,
  parent: 'game',
  width: 960,
  height: 540,
  backgroundColor: '#111a21',
  pixelArt: true,
  roundPixels: true,
  antialias: false,
  scale: {
    mode: Phaser.Scale.FIT,
    autoCenter: Phaser.Scale.CENTER_BOTH,
    width: 960,
    height: 540,
  },
  scene: [worldScene],
});

game.events.on('chat-updated', renderChat);
game.events.on('map-changed', (mapId: 'lumen-town' | 'verdant-field') => {
  const town = mapId === 'lumen-town';
  byId('map-label').textContent = town ? 'Lumen Town' : 'Verdant Field';
  byId('minimap-title').textContent = town ? 'LUMEN\nTOWN' : 'VERDANT\nFIELD';
  byId('minimap-coords').textContent = town ? 'EASTERN GATE' : 'TOWN GATE';
});

async function connectToGame(token: string): Promise<void> {
  if (!gameServerUrl) {
    authNote.textContent = 'Set VITE_GAME_SERVER_URL to connect to online play. You can still play the local demo.';
    authNote.classList.add('error-note');
    showOverlay(true);
    return;
  }
  currentAccessToken = token;
  gameSocket?.close();
  gameSocket = new GameSocket(
    gameServerUrl,
    token,
    (message) => handleServerMessage(message, worldScene),
    setConnectionStatus,
  );
  gameSocket.connect();
  authNote.classList.remove('error-note');
  authNote.textContent = 'Connected to the realm gateway. Choose a character to enter.';
}

byId<HTMLButtonElement>('google-login').addEventListener('click', async () => {
  try {
    if (!supabase) throw new Error('Online login is not configured yet. Add Supabase URL and publishable key.');
    await signInWithGoogle();
  } catch (error) {
    authNote.textContent = error instanceof Error ? error.message : 'Could not start Google sign-in.';
    authNote.classList.add('error-note');
  }
});

byId<HTMLButtonElement>('demo-login').addEventListener('click', () => {
  gameSocket?.close();
  gameSocket = undefined;
  worldScene.startLocalDemo();
  showOverlay(false);
  byId('connection-label').textContent = 'LOCAL PREVIEW';
  byId('world-toast').textContent = 'Move with WASD or arrow keys · Attack with Space · Cast with 1';
  renderChat(worldScene.getChat());
});

byId<HTMLButtonElement>('create-character').addEventListener('click', () => {
  const name = byId<HTMLInputElement>('character-input').value.trim();
  const job = byId<HTMLSelectElement>('job-select').value as CharacterSummary['job'];
  if (name.length < 3 || name.length > 18) {
    authNote.textContent = 'Choose a name between 3 and 18 characters.';
    authNote.classList.add('error-note');
    return;
  }
  gameSocket?.createCharacter(name, job);
});

byId<HTMLButtonElement>('effects-toggle').addEventListener('click', (event) => {
  const button = event.currentTarget as HTMLButtonElement;
  const enabled = button.getAttribute('aria-pressed') !== 'true';
  button.setAttribute('aria-pressed', String(enabled));
  button.textContent = enabled ? '✧ Effects: On' : '✧ Effects: Low';
  worldScene.setEffectsEnabled(enabled);
});

byId<HTMLButtonElement>('equip-button').addEventListener('click', () => worldScene.equipStarter());

byId<HTMLFormElement>('chat-form').addEventListener('submit', (event) => {
  event.preventDefault();
  const input = byId<HTMLInputElement>('chat-input');
  const text = input.value.trim().slice(0, 160);
  if (!text) return;
  if (gameSocket && currentAccessToken) gameSocket.chat(text);
  else worldScene.sendChat('Wayfarer', text);
  input.value = '';
});

window.addEventListener('beforeunload', () => gameSocket?.close());

async function restoreLogin(): Promise<void> {
  if (!supabase) return;
  try {
    const session = await currentSession();
    if (session?.access_token) await connectToGame(session.access_token);
  } catch (error) {
    authNote.textContent = error instanceof Error ? error.message : 'Could not restore your login session.';
    authNote.classList.add('error-note');
  }
}

void restoreLogin();
