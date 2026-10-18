import { describe, expect, it } from 'vitest';
import { isServerMessage } from '../src/game/protocol';

const player = {
  id: 'character-1', name: 'Mira', job: 'Mage', level: 1, experience: 0, mapId: 'lumen-town',
  x: 7, y: 8, hp: 100, mp: 40, gold: 0,
};

describe('server message validation', () => {
  it('accepts a versioned world snapshot with well-formed state', () => {
    expect(isServerMessage({
      type: 'world-state', mapId: 'lumen-town', players: [player], monsters: [], serverTime: 100,
    })).toBe(true);
  });

  it('rejects the wrong protocol version and malformed nested records', () => {
    expect(isServerMessage({ type: 'auth-ok', protocolVersion: 1, characters: [] })).toBe(false);
    expect(isServerMessage({ type: 'world-state', mapId: 'lumen-town', players: [null], monsters: [], serverTime: 100 })).toBe(false);
    expect(isServerMessage({ type: 'inventory', inventory: [{ itemKey: 'slime_essence', quantity: 'many' }] })).toBe(false);
  });
});
