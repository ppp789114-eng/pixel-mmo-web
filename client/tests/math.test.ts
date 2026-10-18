import { describe, expect, it } from 'vitest';
import { clampStep, isWalkable, isoDistance, tileToWorld, worldToTile } from '../src/game/math';

describe('isometric coordinates and movement rules', () => {
  it('round-trips tile centers through the isometric projection', () => {
    for (const tile of [{ x: 0, y: 0 }, { x: 4, y: 9 }, { x: 15, y: 15 }]) {
      const world = tileToWorld(tile.x, tile.y, { x: 420, y: 80 });
      expect(worldToTile(world, { x: 420, y: 80 })).toEqual(tile);
    }
  });

  it('keeps movement to one isometric screen direction and blocks map edges and pond tiles', () => {
    expect(clampStep({ x: 7, y: 8 }, 1, 1)).toEqual({ x: 8, y: 9 });
    expect(clampStep({ x: 7, y: 8 }, 1, 0)).toEqual({ x: 7, y: 8 });
    expect(clampStep({ x: 0, y: 0 }, -1, 0)).toEqual({ x: 0, y: 0 });
    expect(isWalkable(1, 1)).toBe(false);
  });

  it('uses isometric tile distance for close combat range', () => {
    expect(isoDistance({ x: 7, y: 8 }, { x: 9, y: 8 })).toBe(2);
    expect(isoDistance({ x: 7, y: 8 }, { x: 8, y: 9 })).toBe(1);
  });
});
