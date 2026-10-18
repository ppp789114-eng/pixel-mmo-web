import Phaser from 'phaser';
import { clampStep, isWalkable, isoDistance, MAP_SIZE, TILE_HEIGHT, TILE_WIDTH, tileToWorld, type Point } from './math';
import type { EquipmentState, InventoryItem, MapId, MonsterState, PlayerState, QuestState, ServerMessage } from './protocol';

interface LocalPlayer extends PlayerState {
  targetX: number;
  targetY: number;
}

interface VisualEffect {
  x: number;
  y: number;
  life: number;
  duration: number;
  color: number;
  salt: number;
}

interface SceneCallbacks {
  move: (dx: number, dy: number) => void;
  attack: (targetId: string) => void;
  cast: (targetId: string) => void;
  changeMap: (mapId: MapId) => void;
  interact: (targetId: string) => void;
  equip: (itemKey: string) => void;
  unequip: (slot: 'weapon') => void;
}

const ORIGIN = { x: 480, y: 84 };
const PALETTE = {
  grass: 0x314e43,
  grassLight: 0x426653,
  dirt: 0x665344,
  dirtLight: 0x80674d,
  water: 0x274b61,
  waterLight: 0x4e94a1,
  gold: 0xf3ce83,
  mint: 0x9de1b8,
  rose: 0xff7898,
  town: 0x5b6254,
  townLight: 0x707562,
  stone: 0x786c58,
};

export class WorldScene extends Phaser.Scene {
  private terrain!: Phaser.GameObjects.Graphics;
  private effects!: Phaser.GameObjects.Graphics;
  private playerSprite!: Phaser.GameObjects.Container;
  private monsterSprite!: Phaser.GameObjects.Container;
  private landmarkSprites: Phaser.GameObjects.Container[] = [];
  private remoteSprites = new Map<string, Phaser.GameObjects.Container>();
  private mapId: MapId = 'lumen-town';
  private player: LocalPlayer = this.makePlayer('local', 'Wayfarer', 'Swordsman', 7, 8);
  private monsters: MonsterState[] = [this.makeSlime()];
  private remotePlayers: PlayerState[] = [];
  private inventory: InventoryItem[] = [];
  private equipment: EquipmentState[] = [];
  private quest: QuestState = { questKey: 'first-slime', status: 'inactive', progress: 0 };
  private effectsEnabled = true;
  private localDemo = false;
  private movementReadyAt = 0;
  private lastAttackAt = 0;
  private localChat: Array<{ name: string; text: string; system?: boolean }> = [];
  private visualEffects: VisualEffect[] = [];
  private callbacks: SceneCallbacks = { move: () => undefined, attack: () => undefined, cast: () => undefined, changeMap: () => undefined, interact: () => undefined, equip: () => undefined, unequip: () => undefined };
  private hudCallback: (state: Record<string, number | string>) => void = () => undefined;

  constructor() {
    super('WorldScene');
  }

  preload(): void {
    const assets = `${import.meta.env.BASE_URL}assets/`;
    this.load.svg('hero-pixel', `${assets}hero.svg`, { width: 32, height: 40 });
    this.load.svg('slime-pixel', `${assets}moss-slime.svg`, { width: 32, height: 24 });
    this.load.svg('elder-pixel', `${assets}elder-lyra.svg`, { width: 32, height: 40 });
  }

  create(): void {
    this.cameras.main.setRoundPixels(true);
    this.cameras.main.setZoom(1);
    this.terrain = this.add.graphics();
    this.effects = this.add.graphics().setDepth(100_000);
    this.drawWorld(this.mapId);
    this.playerSprite = this.makeHeroSprite();
    this.monsterSprite = this.makeSlimeSprite();
    this.input.keyboard?.on('keydown', this.onKeyDown, this);
    this.addAmbientDetails();
    this.syncSprites();
    this.emitHud();
  }

  bind(callbacks: SceneCallbacks, hudCallback: (state: Record<string, number | string>) => void): void {
    this.callbacks = callbacks;
    this.hudCallback = hudCallback;
  }

  startLocalDemo(name = 'Wayfarer', job: 'Swordsman' | 'Mage' = 'Swordsman'): void {
    this.localDemo = true;
    this.mapId = 'verdant-field';
    this.player = this.makePlayer('local', name, job, 7, 8);
    this.monsters = [this.makeSlime()];
    this.remotePlayers = [];
    this.inventory = [];
    this.equipment = [];
    this.quest = { questKey: 'first-slime', status: 'inactive', progress: 0 };
    this.localChat = [{ name: 'SYSTEM', text: 'The glade is quiet. Your story begins here.', system: true }];
    this.drawWorld(this.mapId);
    this.addAmbientDetails();
    this.game.events.emit('map-changed', this.mapId);
    this.syncSprites();
    this.emitHud();
  }

  enterWorld(character: PlayerState, inventory: InventoryItem[], equipment: EquipmentState[], quest: QuestState, players: PlayerState[], monsters: MonsterState[]): void {
    this.localDemo = false;
    this.mapId = character.mapId;
    this.player = { ...character, targetX: character.x, targetY: character.y };
    this.inventory = inventory;
    this.equipment = equipment;
    this.quest = quest;
    this.drawWorld(this.mapId);
    this.addAmbientDetails();
    this.remotePlayers = players.filter((player) => player.id !== character.id);
    this.monsters = monsters;
    this.game.events.emit('map-changed', this.mapId);
    this.syncSprites();
    this.emitHud();
  }

  applyServerMessage(message: ServerMessage): void {
    if (message.type === 'entered-world') {
      this.enterWorld(message.character, message.inventory, message.equipment, message.quest, message.players, message.monsters);
    } else if (message.type === 'map-changed') {
      this.mapId = message.mapId;
      this.player = { ...message.character, targetX: message.character.x, targetY: message.character.y };
      this.remotePlayers = message.players.filter((player) => player.id !== this.player.id);
      this.monsters = message.monsters;
      this.drawWorld(this.mapId);
      this.addAmbientDetails();
      this.game.events.emit('map-changed', this.mapId);
      this.syncSprites();
      this.emitHud();
    } else if (message.type === 'world-state') {
      if (message.mapId !== this.mapId) return;
      const current = message.players.find((player) => player.id === this.player.id);
      if (current) this.player = { ...current, targetX: current.x, targetY: current.y };
      this.remotePlayers = message.players.filter((player) => player.id !== this.player.id);
      this.monsters = message.monsters;
      this.syncSprites();
      this.emitHud();
    } else if (message.type === 'inventory') {
      this.inventory = message.inventory;
      this.emitHud();
    } else if (message.type === 'equipment') {
      this.inventory = message.inventory;
      this.equipment = message.equipment;
      this.emitHud();
    } else if (message.type === 'quest') {
      this.quest = message.quest;
      this.emitHud();
    } else if (message.type === 'quest-reward') {
      if (message.applied) this.addChat('SYSTEM', `Quest reward · +${message.experience} XP · Moonstone ×1`, true);
    } else if (message.type === 'chat') {
      this.addChat(message.name, message.text, message.system);
    }
  }

  setEffectsEnabled(enabled: boolean): void {
    this.effectsEnabled = enabled;
    if (!enabled) {
      this.visualEffects = [];
      this.effects.clear();
    }
  }

  sendChat(name: string, text: string): void {
    this.addChat(name, text);
  }

  getChat(): Array<{ name: string; text: string; system?: boolean }> {
    return [...this.localChat];
  }

  equipStarter(): void {
    const weapon = this.inventory.find((item) => (item.itemKey === 'iron_sword' || item.itemKey === 'oak_staff') && item.quantity > 0);
    if (weapon) this.callbacks.equip(weapon.itemKey);
    else if (this.equipment.some((item) => item.slot === 'weapon')) this.callbacks.unequip('weapon');
    else this.addChat('SYSTEM', 'No weapon is available in your bag.', true);
  }

  private makePlayer(id: string, name: string, job: 'Swordsman' | 'Mage', x: number, y: number): LocalPlayer {
    return {
      id, name, job, level: 1, experience: 0, mapId: this.mapId, x, y, targetX: x, targetY: y,
      hp: 100, mp: 40, gold: 0,
    };
  }

  private makeSlime(): MonsterState {
    return { id: 'moss-slime-01', kind: 'Moss Slime', x: 9, y: 8, hp: 24, maxHp: 24, alive: true };
  }

  private drawWorld(mapId: MapId): void {
    this.terrain.clear();
    const halfW = TILE_WIDTH / 2;
    const halfH = TILE_HEIGHT / 2;
    const town = mapId === 'lumen-town';
    for (let y = 0; y < MAP_SIZE; y += 1) {
      for (let x = 0; x < MAP_SIZE; x += 1) {
        const point = tileToWorld(x, y, ORIGIN);
        const water = !town && x <= 2 && y <= 3;
        const path = town ? (Math.abs(x - 8) <= 2 && y > 1 && y < 15) || (Math.abs(y - 8) <= 1 && x > 2 && x < 14) : Math.abs(x - 8) <= 1 && y > 2 && y < 15;
        const color = water ? PALETTE.water : path ? (town ? PALETTE.stone : PALETTE.dirt) : ((x * 11 + y * 7) % 5 === 0 ? (town ? PALETTE.townLight : PALETTE.grassLight) : (town ? PALETTE.town : PALETTE.grass));
        const bottom = new Phaser.Math.Vector2(point.x, point.y + halfH);
        const left = new Phaser.Math.Vector2(point.x - halfW, point.y);
        const top = new Phaser.Math.Vector2(point.x, point.y - halfH);
        const right = new Phaser.Math.Vector2(point.x + halfW, point.y);
        this.terrain.fillStyle(color, 1).fillPoints([top, right, bottom, left], true, true);
        this.terrain.lineStyle(1, water ? 0x38657a : town ? 0x77705b : 0x587363, 0.48).strokePoints([top, right, bottom, left], true, true);
        if (water && (x + y) % 3 === 0) {
          this.terrain.fillStyle(PALETTE.waterLight, 0.7).fillRect(point.x - 5 + ((x * 3) % 7), point.y - 1, 4, 1);
        } else if (!town && !path && (x * 3 + y) % 4 === 0) {
          this.terrain.fillStyle(PALETTE.mint, 0.7).fillRect(point.x + ((x + y) % 9) - 4, point.y + ((x * 2) % 5) - 2, 2, 2);
        } else if (path && (x + y) % 4 === 0) {
          this.terrain.fillStyle(PALETTE.dirtLight, 0.65).fillRect(point.x - 3, point.y + 1, 3, 1);
        }
      }
    }
    if (town) {
      this.terrain.fillStyle(0x312e2b, 1).fillRect(440, 96, 76, 33).fillRect(448, 80, 60, 18);
      this.terrain.fillStyle(0x755c44, 1).fillRect(448, 104, 60, 25);
      this.terrain.fillStyle(0xb8a477, 1).fillRect(445, 99, 66, 4).fillRect(453, 87, 50, 4);
      this.terrain.fillStyle(0x433d32, 1).fillRect(470, 111, 15, 18);
    }
    this.terrain.fillStyle(0x101820, 0.25).fillRect(0, 458, 960, 90);
  }

  private addAmbientDetails(): void {
    this.landmarkSprites.forEach((sprite) => sprite.destroy());
    this.landmarkSprites = [];
    const trees: Point[] = this.mapId === 'lumen-town'
      ? [{ x: 3, y: 3 }, { x: 3, y: 13 }, { x: 12, y: 3 }, { x: 12, y: 13 }]
      : [{ x: 4, y: 3 }, { x: 5, y: 11 }, { x: 12, y: 4 }, { x: 12, y: 12 }, { x: 2, y: 9 }, { x: 10, y: 14 }];
    for (const tile of trees) {
      if (!isWalkable(tile.x, tile.y)) continue;
      const position = tileToWorld(tile.x, tile.y, ORIGIN);
      const tree = this.add.container(position.x, position.y);
      const graphics = this.add.graphics();
      graphics.fillStyle(0x09191b, 0.35).fillEllipse(0, 0, 34, 10);
      graphics.fillStyle(0x654a3f).fillRect(-3, -25, 6, 24);
      graphics.fillStyle(0x244b45).fillRect(-11, -42, 22, 18);
      graphics.fillStyle(0x386854).fillRect(-15, -37, 30, 12);
      graphics.fillStyle(0x4c815d).fillRect(-9, -46, 7, 5);
      graphics.fillStyle(0x152d2c).fillRect(8, -34, 5, 7);
      tree.add(graphics);
      tree.setDepth(position.y - 3);
      this.landmarkSprites.push(tree);
    }
    if (this.mapId === 'lumen-town') {
      const elderPosition = tileToWorld(5, 8, ORIGIN);
      const elder = this.makeNpcSprite();
      elder.setPosition(elderPosition.x, elderPosition.y);
      elder.setDepth(elderPosition.y + 3);
      this.landmarkSprites.push(elder);
    }
    const gate = this.add.container(0, 0);
    const gateTile = this.mapId === 'lumen-town' ? { x: 14, y: 7 } : { x: 0, y: 9 };
    const gatePosition = tileToWorld(gateTile.x, gateTile.y, ORIGIN);
    gate.setPosition(gatePosition.x, gatePosition.y);
    const gateGraphics = this.add.graphics();
    gateGraphics.fillStyle(0x18201d, 0.32).fillEllipse(0, 0, 28, 10);
    gateGraphics.lineStyle(2, PALETTE.gold, 0.8).strokeCircle(0, -4, 10);
    gateGraphics.fillStyle(PALETTE.mint, 0.75).fillRect(-2, -10, 4, 12);
    gate.add(gateGraphics);
    gate.setDepth(gatePosition.y + 1);
    this.landmarkSprites.push(gate);
    for (let i = 0; i < 9; i += 1) {
      this.visualEffects.push({
        x: 120 + ((i * 137) % 720), y: 95 + ((i * 71) % 300),
        life: (i * 0.37) % 3, duration: 3 + (i % 3),
        color: i % 2 ? PALETTE.gold : PALETTE.mint, salt: i * 19,
      });
    }
  }

  private makeHeroSprite(): Phaser.GameObjects.Container {
    const container = this.add.container(0, 0);
    if (this.textures.exists('hero-pixel')) {
      container.add(this.add.image(0, -20, 'hero-pixel'));
      return container;
    }
    const g = this.add.graphics();
    g.fillStyle(0x071114, 0.28).fillEllipse(0, 0, 20, 7);
    g.fillStyle(0x25212d).fillRect(-5, -7, 4, 6).fillRect(1, -7, 4, 6);
    g.fillStyle(0xc17b63).fillRect(-6, -16, 12, 10);
    g.fillStyle(0x324f71).fillRect(-7, -14, 14, 9);
    g.fillStyle(0x597da0).fillRect(-7, -14, 3, 7).fillRect(4, -14, 3, 7);
    g.fillStyle(0xc88b67).fillRect(-5, -24, 10, 9);
    g.fillStyle(0x302939).fillRect(-6, -26, 12, 5).fillRect(-4, -29, 8, 4);
    g.fillStyle(PALETTE.gold).fillRect(-5, -25, 10, 2);
    g.fillStyle(0x282636).fillRect(-3, -21, 2, 2).fillRect(3, -21, 2, 2);
    g.fillStyle(0xe7c581).fillRect(7, -16, 2, 11).fillRect(5, -18, 6, 2);
    container.add(g);
    return container;
  }

  private makeSlimeSprite(): Phaser.GameObjects.Container {
    const container = this.add.container(0, 0);
    if (this.textures.exists('slime-pixel')) {
      container.add(this.add.image(0, -12, 'slime-pixel'));
      return container;
    }
    const g = this.add.graphics();
    g.fillStyle(0x09191b, 0.35).fillEllipse(0, 0, 28, 9);
    g.fillStyle(0x277f72).fillRect(-11, -10, 22, 9).fillRect(-8, -15, 16, 7);
    g.fillStyle(0x50ba91).fillRect(-7, -14, 12, 4).fillRect(-10, -9, 6, 2);
    g.fillStyle(0xb8f1b6).fillRect(-5, -11, 2, 2).fillRect(4, -11, 2, 2);
    g.fillStyle(0x205b58).fillRect(-4, -6, 2, 2).fillRect(3, -6, 2, 2);
    container.add(g);
    return container;
  }

  private makeNpcSprite(): Phaser.GameObjects.Container {
    const container = this.add.container(0, 0);
    if (this.textures.exists('elder-pixel')) {
      container.add(this.add.image(0, -20, 'elder-pixel'));
      return container;
    }
    const g = this.add.graphics();
    g.fillStyle(0x071114, 0.28).fillEllipse(0, 0, 20, 7);
    g.fillStyle(0x493a42).fillRect(-5, -7, 4, 6).fillRect(1, -7, 4, 6);
    g.fillStyle(0x785f78).fillRect(-7, -16, 14, 10);
    g.fillStyle(0xd8bd89).fillRect(-5, -25, 10, 9);
    g.fillStyle(0xc6c1b1).fillRect(-7, -28, 14, 5).fillRect(-4, -31, 8, 4);
    g.fillStyle(PALETTE.gold).fillRect(-5, -25, 10, 2);
    container.add(g);
    return container;
  }

  private onKeyDown(event: KeyboardEvent): void {
    const now = performance.now();
    const directions: Record<string, Point> = {
      w: { x: -1, y: -1 }, ArrowUp: { x: -1, y: -1 },
      d: { x: 1, y: -1 }, ArrowRight: { x: 1, y: -1 },
      s: { x: 1, y: 1 }, ArrowDown: { x: 1, y: 1 },
      a: { x: -1, y: 1 }, ArrowLeft: { x: -1, y: 1 },
    };
    const direction = directions[event.key] ?? directions[event.key.toLowerCase()];
    if (direction) {
      event.preventDefault();
      if (now < this.movementReadyAt) return;
      this.movementReadyAt = now + 145;
      if (this.localDemo) {
        const position = clampStep(this.player, direction.x, direction.y);
        this.player.targetX = position.x;
        this.player.targetY = position.y;
        this.player.x = position.x;
        this.player.y = position.y;
      } else {
        this.callbacks.move(direction.x, direction.y);
      }
      return;
    }
    if (event.repeat) return;
    if (event.code === 'Space') {
      event.preventDefault();
      this.performAction(false);
    } else if (event.key === '1') {
      this.performAction(true);
    } else if (event.key.toLowerCase() === 'i') {
      this.equipStarter();
    } else if (event.key.toLowerCase() === 'e') {
      event.preventDefault();
      if (this.mapId === 'lumen-town' && isoDistance(this.player, { x: 5, y: 8 }) <= 1) this.callbacks.interact('elder-lyra');
      else if (this.mapId === 'lumen-town' && this.player.x === 14 && this.player.y === 7) this.callbacks.changeMap('verdant-field');
      else if (this.mapId === 'verdant-field' && this.player.x === 0 && this.player.y === 9) this.callbacks.changeMap('lumen-town');
      else this.addChat('SYSTEM', 'Move beside Elder Lyra or stand on the glowing gate, then press E.', true);
    }
  }

  private performAction(useSkill: boolean): void {
    if (this.mapId !== 'verdant-field') {
      this.addChat('SYSTEM', 'Travel through the eastern gate to find creatures.', true);
      return;
    }
    const now = performance.now();
    if (now - this.lastAttackAt < (useSkill ? 550 : 350)) return;
    const target = this.monsters
      .filter((monster) => monster.alive)
      .sort((a, b) => isoDistance(this.player, a) - isoDistance(this.player, b))[0];
    const range = useSkill ? 5 : 2;
    if (!target || isoDistance(this.player, target) > range) {
      this.addChat('SYSTEM', useSkill ? 'No creature is close enough for Starbolt.' : 'Move closer to a creature first.', true);
      return;
    }
    if (useSkill && this.localDemo && this.player.mp < 6) {
      this.addChat('SYSTEM', 'Not enough mana for Starbolt.', true);
      return;
    }
    this.lastAttackAt = now;
    const point = tileToWorld(target.x, target.y, ORIGIN);
    this.spawnImpact(point.x, point.y - 7, useSkill ? PALETTE.rose : PALETTE.gold);
    if (!this.localDemo) {
      if (useSkill) this.callbacks.cast(target.id);
      else this.callbacks.attack(target.id);
      return;
    }
    if (useSkill) this.player.mp -= 6;
    target.hp = Math.max(0, target.hp - (useSkill ? 13 : 8));
    if (target.hp === 0) {
      target.alive = false;
      this.player.experience += 20;
      const essence = this.inventory.find((item) => item.itemKey === 'slime_essence');
      if (essence) essence.quantity += 1;
      else this.inventory.push({ itemKey: 'slime_essence', quantity: 1 });
      this.addChat('SYSTEM', 'Moss Slime defeated · +20 XP · Moss essence ×1', true);
      this.player.level = Math.min(99, 1 + Math.floor(this.player.experience / 100));
      window.setTimeout(() => {
        target.hp = target.maxHp;
        target.alive = true;
        this.syncSprites();
      }, 4_000);
    }
    this.syncSprites();
    this.emitHud();
  }

  private spawnImpact(x: number, y: number, color: number): void {
    if (!this.effectsEnabled) return;
    for (let index = 0; index < 10; index += 1) {
      this.visualEffects.push({ x, y, life: 0, duration: 0.45 + (index % 3) * 0.12, color, salt: index * 41 + Math.random() * 12 });
    }
  }

  private syncSprites(): void {
    if (!this.playerSprite) return;
    const playerPoint = tileToWorld(this.player.targetX, this.player.targetY, ORIGIN);
    this.playerSprite.setPosition(Math.round(playerPoint.x), Math.round(playerPoint.y));
    this.playerSprite.setDepth(playerPoint.y + 4);
    const slime = this.monsters[0];
    if (slime) {
      const point = tileToWorld(slime.x, slime.y, ORIGIN);
      this.monsterSprite.setPosition(Math.round(point.x), Math.round(point.y));
      this.monsterSprite.setDepth(point.y + 2);
      this.monsterSprite.setVisible(this.mapId === 'verdant-field' && slime.alive);
    }
    const presentIds = new Set(this.remotePlayers.map((player) => player.id));
    for (const [id, sprite] of this.remoteSprites) {
      if (!presentIds.has(id)) {
        sprite.destroy();
        this.remoteSprites.delete(id);
      }
    }
    for (const remote of this.remotePlayers) {
      let sprite = this.remoteSprites.get(remote.id);
      if (!sprite) {
        sprite = this.makeHeroSprite();
        this.add.existing(sprite);
        this.remoteSprites.set(remote.id, sprite);
      }
      const position = tileToWorld(remote.x, remote.y, ORIGIN);
      sprite.setPosition(Math.round(position.x), Math.round(position.y));
      sprite.setDepth(position.y + 3);
    }
  }

  private addChat(name: string, text: string, system = false): void {
    this.localChat.push({ name, text, system });
    this.localChat = this.localChat.slice(-30);
    this.game.events.emit('chat-updated', this.getChat());
  }

  private emitHud(): void {
    const state = {
      name: this.player.name,
      job: this.player.job,
      level: this.player.level,
      hp: this.player.hp,
      maxHp: 100,
      mp: this.player.mp,
      maxMp: 40,
      xpPercent: this.player.experience % 100,
      essence: this.inventory.find((item) => item.itemKey === 'slime_essence')?.quantity ?? 0,
      online: 1 + this.remotePlayers.length,
      questProgress: this.quest.progress,
      questStatus: this.quest.status,
      mapId: this.mapId,
      weapon: this.equipment.find((item) => item.slot === 'weapon')?.itemKey ?? 'None',
      items: this.inventory.filter((item) => item.quantity > 0).map((item) => `${item.itemKey.replaceAll('_', ' ')} ×${item.quantity}`).join(' · ') || 'Bag empty',
    };
    this.hudCallback(state);
    this.game.events.emit('hud-updated', state);
  }

  update(_time: number, delta: number): void {
    if (this.playerSprite) {
      const position = tileToWorld(this.player.targetX, this.player.targetY, ORIGIN);
      const amount = Math.min(1, delta / 90);
      this.playerSprite.x = Phaser.Math.Linear(this.playerSprite.x, Math.round(position.x), amount);
      this.playerSprite.y = Phaser.Math.Linear(this.playerSprite.y, Math.round(position.y), amount);
      this.playerSprite.setDepth(this.playerSprite.y + 4);
    }
    this.drawEffects(delta / 1_000);
  }

  private drawEffects(elapsed: number): void {
    this.effects.clear();
    const stillAlive: VisualEffect[] = [];
    for (const particle of this.visualEffects) {
      particle.life += elapsed;
      if (particle.life >= particle.duration) continue;
      stillAlive.push(particle);
      const t = particle.life / particle.duration;
      const drift = Math.sin(particle.salt + particle.life * 9) * 12;
      const rise = t * (18 + (particle.salt % 22));
      const alpha = Math.max(0, 1 - t);
      this.effects.fillStyle(particle.color, alpha * 0.14).fillCircle(particle.x + drift, particle.y - rise, 9 * (1 - t) + 2);
      this.effects.fillStyle(particle.color, alpha).fillRect(Math.round(particle.x + drift), Math.round(particle.y - rise), 2 + (particle.salt % 2), 2 + (particle.salt % 2));
    }
    this.visualEffects = stillAlive;
  }
}
