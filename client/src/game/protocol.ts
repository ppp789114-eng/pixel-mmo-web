export interface CharacterSummary {
  id: string;
  name: string;
  job: 'Swordsman' | 'Mage';
  level: number;
  experience: number;
  mapId: MapId;
  x: number;
  y: number;
  hp: number;
  mp: number;
  gold: number;
}

export type MapId = 'lumen-town' | 'verdant-field';

export interface InventoryItem {
  itemKey: string;
  quantity: number;
}

export interface PlayerState extends CharacterSummary {
  ownerId?: string;
}

export interface MonsterState {
  id: string;
  kind: string;
  x: number;
  y: number;
  hp: number;
  maxHp: number;
  alive: boolean;
}

export interface EquipmentState {
  slot: 'weapon';
  itemKey: string;
}

export interface QuestState {
  questKey: 'first-slime';
  status: 'inactive' | 'active' | 'complete' | 'claimed';
  progress: number;
}

export type ClientMessage =
  | { type: 'auth'; version: 2; accessToken: string }
  | { type: 'create-character'; name: string; job: 'Swordsman' | 'Mage' }
  | { type: 'select-character'; characterId: string }
  | { type: 'move'; dx: number; dy: number; sequence: number }
  | { type: 'attack'; targetId: string; commandId: string }
  | { type: 'cast'; skillKey: 'starbolt'; targetId: string; commandId: string }
  | { type: 'chat'; text: string }
  | { type: 'change-map'; mapId: MapId }
  | { type: 'interact'; targetId: string }
  | { type: 'equip'; itemKey: string }
  | { type: 'unequip'; slot: 'weapon' };

export type ServerMessage =
  | { type: 'auth-ok'; protocolVersion: 2; characters: CharacterSummary[] }
  | { type: 'entered-world'; protocolVersion: 2; mapId: MapId; character: CharacterSummary; inventory: InventoryItem[]; equipment: EquipmentState[]; quest: QuestState; players: PlayerState[]; monsters: MonsterState[] }
  | { type: 'map-changed'; mapId: MapId; character: PlayerState; players: PlayerState[]; monsters: MonsterState[] }
  | { type: 'world-state'; mapId: MapId; players: PlayerState[]; monsters: MonsterState[]; serverTime: number }
  | { type: 'inventory'; inventory: InventoryItem[] }
  | { type: 'equipment'; inventory: InventoryItem[]; equipment: EquipmentState[] }
  | { type: 'quest'; quest: QuestState }
  | { type: 'quest-reward'; experience: number; applied: boolean }
  | { type: 'chat'; name: string; text: string; system?: boolean }
  | { type: 'error'; code: string; message: string }
  | { type: 'pong'; serverTime: number };

export function isServerMessage(value: unknown): value is ServerMessage {
  if (!value || typeof value !== 'object' || !('type' in value)) return false;
  const message = value as Record<string, unknown>;
  switch (message.type) {
    case 'auth-ok': return message.protocolVersion === 2 && Array.isArray(message.characters) && message.characters.every(isCharacter);
    case 'entered-world': return message.protocolVersion === 2 && isMapId(message.mapId) && isCharacter(message.character) && isInventory(message.inventory) && isEquipment(message.equipment) && isQuest(message.quest) && isPlayers(message.players) && isMonsters(message.monsters);
    case 'map-changed': return isMapId(message.mapId) && isCharacter(message.character) && isPlayers(message.players) && isMonsters(message.monsters);
    case 'world-state': return isMapId(message.mapId) && isPlayers(message.players) && isMonsters(message.monsters) && typeof message.serverTime === 'number';
    case 'inventory': return isInventory(message.inventory);
    case 'equipment': return isInventory(message.inventory) && isEquipment(message.equipment);
    case 'quest': return isQuest(message.quest);
    case 'quest-reward': return typeof message.experience === 'number' && typeof message.applied === 'boolean';
    case 'chat': return typeof message.name === 'string' && typeof message.text === 'string' && (message.system === undefined || typeof message.system === 'boolean');
    case 'error': return typeof message.code === 'string' && typeof message.message === 'string';
    case 'pong': return typeof message.serverTime === 'number';
    default: return false;
  }
}

function isMapId(value: unknown): value is MapId {
  return value === 'lumen-town' || value === 'verdant-field';
}

function isCharacter(value: unknown): value is CharacterSummary {
  if (!value || typeof value !== 'object') return false;
  const character = value as Record<string, unknown>;
  return typeof character.id === 'string' && typeof character.name === 'string' &&
    (character.job === 'Swordsman' || character.job === 'Mage') && typeof character.level === 'number' &&
    typeof character.experience === 'number' && isMapId(character.mapId) && typeof character.x === 'number' &&
    typeof character.y === 'number' && typeof character.hp === 'number' && typeof character.mp === 'number' && typeof character.gold === 'number';
}

function isQuest(value: unknown): value is QuestState {
  if (!value || typeof value !== 'object') return false;
  const quest = value as Record<string, unknown>;
  return quest.questKey === 'first-slime' && ['inactive', 'active', 'complete', 'claimed'].includes(String(quest.status)) && typeof quest.progress === 'number';
}

function isInventory(value: unknown): value is InventoryItem[] {
  return Array.isArray(value) && value.every((item) => item && typeof item === 'object' && typeof item.itemKey === 'string' && Number.isSafeInteger(item.quantity) && item.quantity >= 0);
}

function isEquipment(value: unknown): value is EquipmentState[] {
  return Array.isArray(value) && value.every((item) => item && typeof item === 'object' && item.slot === 'weapon' && typeof item.itemKey === 'string');
}

function isPlayers(value: unknown): value is PlayerState[] {
  return Array.isArray(value) && value.every(isCharacter);
}

function isMonsters(value: unknown): value is MonsterState[] {
  return Array.isArray(value) && value.every((monster) => monster && typeof monster === 'object' &&
    typeof monster.id === 'string' && typeof monster.kind === 'string' && Number.isSafeInteger(monster.x) &&
    Number.isSafeInteger(monster.y) && Number.isSafeInteger(monster.hp) && Number.isSafeInteger(monster.maxHp) && typeof monster.alive === 'boolean');
}
