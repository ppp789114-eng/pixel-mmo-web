export interface Point {
  x: number;
  y: number;
}

export const MAP_SIZE = 16;
export const TILE_WIDTH = 48;
export const TILE_HEIGHT = 24;

export function tileToWorld(
  tileX: number,
  tileY: number,
  origin: Point,
  tileWidth = TILE_WIDTH,
  tileHeight = TILE_HEIGHT,
): Point {
  return {
    x: origin.x + ((tileX - tileY) * tileWidth) / 2,
    y: origin.y + ((tileX + tileY) * tileHeight) / 2,
  };
}

export function worldToTile(
  point: Point,
  origin: Point,
  tileWidth = TILE_WIDTH,
  tileHeight = TILE_HEIGHT,
): Point {
  const projectedX = ((point.x - origin.x) * 2) / tileWidth;
  const projectedY = ((point.y - origin.y) * 2) / tileHeight;
  return {
    x: Math.round((projectedX + projectedY) / 2),
    y: Math.round((projectedY - projectedX) / 2),
  };
}

export function isWalkable(x: number, y: number): boolean {
  if (x < 0 || y < 0 || x >= MAP_SIZE || y >= MAP_SIZE) return false;
  // A small pond in the northwest corner; the server applies the same boundary rule.
  return !(x <= 2 && y <= 3);
}

export function clampStep(current: Point, dx: number, dy: number): Point {
  const safeDx = Math.max(-1, Math.min(1, Math.trunc(dx)));
  const safeDy = Math.max(-1, Math.min(1, Math.trunc(dy)));
  // The four screen-cardinal directions on an isometric grid are diagonal in tile space.
  if (Math.abs(safeDx) !== 1 || Math.abs(safeDy) !== 1) return current;
  const candidate = { x: current.x + safeDx, y: current.y + safeDy };
  return isWalkable(candidate.x, candidate.y) ? candidate : current;
}

export function isoDistance(a: Point, b: Point): number {
  return Math.max(Math.abs(a.x - b.x), Math.abs(a.y - b.y));
}
