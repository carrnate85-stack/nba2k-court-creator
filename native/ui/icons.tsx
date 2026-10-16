import toolIcons from '@canvas/icons.generated.json';

/** Shared 2K Canvas tool glyphs (24 x 24) plus the Court Creator studio's own path icons. */
const studio: Record<string, [string, number]> = {
  hardwood: ['M3,3 H21 V21 H3 Z M3,9 H21 M3,15 H21 M9,3 V9 M15,9 V15 M8,15 V21', 24],
  transform: ['M5,5 H19 V19 H5 Z M3,3 H7 V7 H3 Z M17,3 H21 V7 H17 Z M3,17 H7 V21 H3 Z M17,17 H21 V21 H17 Z', 24],
  fit: ['M5,1 H1 V5 M11,1 H15 V5 M1,11 V15 H5 M15,11 V15 H11 M4,4 H12 V12 H4 Z', 16],
  undo: ['M17,14 C17,8 13,5 8,5 H3 M3,5 L7,1 M3,5 L7,9', 18],
  redo: ['M3,14 C3,8 7,5 12,5 H17 M17,5 L13,1 M17,5 L13,9', 18],
  moon: ['M14,2 A9,9 0 1 0 22,14 A8,8 0 0 1 14,2 Z', 24],
  reset: ['M3,11 A9,9 0 1 1 12,20 M3,4 V11 H10', 24],
  chevronDown: ['M0,0 L4,4 L8,0', 8],
  chevronRight: ['M4,2 L10,8 L4,14', 16],
  close: ['M3,3 L15,15 M15,3 L3,15', 18],
  closeSmall: ['M2,2 L12,12 M12,2 L2,12', 14],
  minimize: ['M3,14 H15', 18],
  maximize: ['M3,3 H15 V15 H3 Z', 18],
  restore: ['M6,3 H17 V14 H14 M3,6 H14 V17 H3 Z', 18],
  document: ['M2,1 H8 L12,5 V15 H2 Z M8,1 V5 H12', 16],
  colors: ['M5,4 H15 L20,14 L13,21 L3,11 Z M5,4 L13,14 H20 M7,7 L11,11', 24],
  logos: ['M4,4 H20 V20 H4 Z M7,16 L11,11 L14,14 L17,8 M8,8 H8.1', 24],
  eye: ['M1,8 C5,1 13,1 17,8 C13,15 5,15 1,8 Z M6,8 A3,3 0 1 0 12,8 A3,3 0 1 0 6,8', 18],
  lock: ['M4,10 H18 V21 H4 Z M7,10 V6 A4,4 0 0 1 15,6 V10', 22],
  unlock: ['M4,10 H18 V21 H4 Z M7,10 V6 A4,4 0 0 1 15,6 V7', 22],
  copy: ['M8,8 H20 V20 H8 Z M4,16 V4 H16', 24],
  trash: ['M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17', 24],
  search: ['M10,3 A7,7 0 1 0 10,17 A7,7 0 1 0 10,3 M15,15 L21,21', 24],
};

export type IconName = keyof typeof studio | keyof typeof toolIcons;

export function Icon({ name, size = 18, className }: { name: IconName; size?: number; className?: string }) {
  const tool = (toolIcons as Record<string, string>)[name];
  const [path, box] = tool ? [tool, 24] : studio[name] ?? ['', 24];
  return (
    <svg className={`icon ${className ?? ''}`} viewBox={`0 0 ${box} ${box}`} style={{ width: size, height: size }} aria-hidden="true">
      <path d={path} />
    </svg>
  );
}
