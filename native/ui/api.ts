import { invoke } from '@tauri-apps/api/core';

export type Point = [number, number];
export type Polygon = Point[];
export type Settings = { brightness: number; contrast: number; saturation: number; scale: number; rotation: number };
export type Layer = { id: string; name: string; visible: boolean; color: string; defaultVisible: boolean; defaultColor: string };
export type Hardwood = { id: string; name: string; category: string; path: string; previewPath: string; settings: Settings; revision: string | null };
export type TextSettings = Record<string, unknown>;
export type Logo = {
  id: string; name: string; path: string; x: number; y: number; width: number; height: number; rotation: number; opacity: number;
  visible: boolean; scaleLocked: boolean; flipX: boolean; flipY: boolean; revision: string | null; pixelWidth: number; pixelHeight: number;
  isText: boolean; textSettings: TextSettings | null; missing: boolean; artwork: Record<string, unknown>;
};
export type Doc = {
  version: number; name: string; path: string | null; dirty: boolean; canUndo: boolean; canRedo: boolean;
  paints: Layer[]; lines: Layer[]; outside: Layer; floor: Hardwood | null; twoPoint: Hardwood | null; twoPointEnabled: boolean;
  logos: Logo[]; boundary: string | null; recent: string[];
};
export type Anchor = { id: string; name: string; x: number; y: number };
export type Geometry = { revision: string; lines: { id: string; polygons: Polygon[] }[]; paints: { id: string; polygons: Polygon[] }[]; surface: Polygon[]; hardwoodBounds: [number, number, number, number]; anchors: Anchor[] };
export type Regions = { boundary: string | null; enclosures: Polygon[]; paints: Record<string, Polygon[]> };
export type Floor = { id: string; name: string; category: string; previewPath: string };
export type Palette = { league?: string; team?: string; source?: string; paletteNote?: string; colors: { hex: string; name?: string }[] };
export type Prefs = { favorites: string[]; recent: string[]; dark: boolean; primaryColor: string; colorPresets: string[] };
export type Reply = { doc: Doc; status?: string; selected?: string };
export type StartupState = { phase: string; message: string; preparing: boolean; retry: boolean };
export type StudioData = { geometry: Geometry; floors: Floor[]; defaultFloor: string | null; palettes: Palette[]; prefs: Prefs; doc: Doc; libraryName: string; libraryComplete: boolean };
export type Texture = { name: string; width: number; height: number; format: string; mipmaps: number; bytes: number };
export type Inspection = { path: string; textures: Texture[]; selected: string; sourceBounds?: [number, number, number, number]; sourceRevision?: string; alignmentSource?: string };
export type ImporterInfo = { session: number; name: string; width: number; height: number; background: string; canUndo: boolean; hasChanges: boolean; removed: number };

export const call = <T>(command: string, args?: Record<string, unknown>) => invoke<T>(command, args);

/** Binary pixel replies: 8-byte header (width, height) followed by straight RGBA. */
export async function pixels(command: string, args?: Record<string, unknown>): Promise<ImageData> {
  const buffer = await invoke<ArrayBuffer>(command, args);
  const view = new DataView(buffer);
  const width = view.getUint32(0, true), height = view.getUint32(4, true);
  return new ImageData(new Uint8ClampedArray(buffer, 8, width * height * 4), width, height);
}

export async function bitmap(command: string, args?: Record<string, unknown>) {
  const data = await pixels(command, args);
  return { data, image: await createImageBitmap(data) };
}

export const errorText = (error: unknown) => (typeof error === 'string' ? error : error instanceof Error ? error.message : String(error));
