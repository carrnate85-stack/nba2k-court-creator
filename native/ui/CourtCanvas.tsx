import { forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef } from 'react';
import type { Doc, Geometry, Logo, Polygon, Regions } from './api';

export type Tool = 'Move' | 'Transform' | 'Hand' | 'Zoom' | 'Eyedropper' | 'Bucket' | 'Type';
export type Pose = { id: string; x: number; y: number; width: number; height: number; rotation: number };
export type LogoBitmap = { image: ImageBitmap; data: ImageData };
type Rect = { x: number; y: number; w: number; h: number };

export type CanvasHandle = {
  fit(): void;
  actualSize(): void;
  zoom(factor: number): void;
  setView(view: 'full' | 'left' | 'right'): void;
  cancel(): void;
  focus(): void;
  scale(): number;
};

type Props = {
  geometry: Geometry;
  regions: Regions | null;
  doc: Doc;
  mainHardwood: ImageBitmap | null;
  twoPointHardwood: ImageBitmap | null;
  logos: Map<string, LogoBitmap>;
  importImage: ImageBitmap | null;
  tool: Tool;
  editing: boolean;
  showArtwork: boolean;
  selected: string | null;
  onSelect(id: string | null): void;
  onCommit(pose: Pose): void;
  onLivePose(pose: Pose | null): void;
  onSample(hex: string): void;
  onFill(x: number, y: number): void;
  onText(x: number, y: number, hit: string | null): void;
  onDelete(): void;
  onZoom(scale: number): void;
};

const DOC = { w: 8192, h: 4096 };
const FIT_OFFSET = 24;
const HANDLE = 10;
const DRAG = 4;

function pathOf(polygons: Polygon[]) {
  const path = new Path2D();
  for (const polygon of polygons) {
    if (polygon.length < 3) continue;
    path.moveTo(polygon[0][0], polygon[0][1]);
    for (let i = 1; i < polygon.length; i++) path.lineTo(polygon[i][0], polygon[i][1]);
    path.closePath();
  }
  return path;
}

const rotate = (x: number, y: number, degrees: number): [number, number] => {
  const r = (degrees * Math.PI) / 180;
  return [x * Math.cos(r) - y * Math.sin(r), x * Math.sin(r) + y * Math.cos(r)];
};

/** TransformGeometry.ConstrainScale / ScaleDimensions. */
function constrain(width: number, height: number, scale: number) {
  const s = Math.min(Math.max(scale, Math.max(1 / width, 1 / height)), Math.min(32768 / width, 32768 / height));
  return { width: Math.min(Math.max(width * s, 1), 32768), height: Math.min(Math.max(height * s, 1), 32768) };
}

/** Opposite-corner anchored resize with continuous aspect-lock changes (ArtworkResizeGesture). */
class ResizeGesture {
  private segment: Pose; private pointer: [number, number]; private sx: number; private sy: number; private locked: boolean;
  constructor(state: Pose, pointer: [number, number], corner: number, locked: boolean) {
    this.segment = state; this.pointer = pointer; this.locked = locked;
    this.sx = corner === 0 || corner === 3 ? -1 : 1; this.sy = corner === 0 || corner === 1 ? -1 : 1;
  }
  update(pointer: [number, number], locked: boolean, current: Pose): Pose {
    if (locked !== this.locked) { this.segment = current; this.pointer = pointer; this.locked = locked; return current; }
    const s = this.segment;
    const [dx, dy] = rotate(pointer[0] - this.pointer[0], pointer[1] - this.pointer[1], -s.rotation);
    let width = Math.min(Math.max(s.width + dx * this.sx, 1), 32768);
    let height = Math.min(Math.max(s.height + dy * this.sy, 1), 32768);
    if (locked) {
      const ratio = Math.abs(dx / s.width) >= Math.abs(dy / s.height) ? width / s.width : height / s.height;
      ({ width, height } = constrain(s.width, s.height, ratio));
    }
    const cx = s.x + s.width / 2, cy = s.y + s.height / 2;
    const [ax, ay] = rotate((-this.sx * s.width) / 2, (-this.sy * s.height) / 2, s.rotation);
    const [bx, by] = rotate((this.sx * width) / 2, (this.sy * height) / 2, s.rotation);
    const centerX = cx + ax + bx, centerY = cy + ay + by;
    return { ...s, x: centerX - width / 2, y: centerY - height / 2, width, height };
  }
}

const poseOf = (logo: Logo): Pose => ({ id: logo.id, x: logo.x, y: logo.y, width: logo.width, height: logo.height, rotation: logo.rotation });

export const CourtCanvas = forwardRef<CanvasHandle, Props>(function CourtCanvas(props, ref) {
  const host = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const view = useRef({ zoom: 1, panX: 0, panY: 0, viewport: null as Rect | null, width: 1, height: 1 });
  const gesture = useRef<null | { kind: 'pan' | 'move' | 'resize' | 'rotate'; button: number; start: [number, number]; startDoc: [number, number]; panStart: [number, number]; original: Pose; resize?: ResizeGesture; dragged: boolean; pointer: number }>(null);
  const pose = useRef<Pose | null>(null);
  const space = useRef(false);
  const frame = useRef(0);
  const latest = useRef(props);
  latest.current = props;

  const paths = useMemo(() => {
    const lines = new Map(props.geometry.lines.map(l => [l.id, pathOf(l.polygons)]));
    return { lines, surface: pathOf(props.geometry.surface) };
  }, [props.geometry]);
  const regionPaths = useMemo(() => {
    if (!props.regions) return null;
    return { paints: new Map(Object.entries(props.regions.paints).map(([id, p]) => [id, pathOf(p)])), enclosures: pathOf(props.regions.enclosures) };
  }, [props.regions]);

  const metrics = useCallback(() => {
    const v = view.current;
    const r = v.viewport ?? { x: 0, y: 0, w: DOC.w, h: DOC.h };
    const base = Math.min(Math.max(1, v.width - 24) / r.w, Math.max(1, v.height - 24) / r.h);
    const scale = Math.max(0.001, base * v.zoom);
    const available = Math.max(0, (v.height - r.h * base) / 2 - 12);
    const offset = Math.min(Math.max(FIT_OFFSET, -available), available);
    const ox = (v.width - r.w * scale) / 2 - r.x * scale + v.panX;
    const oy = (v.height - r.h * scale) / 2 - r.y * scale + offset + v.panY;
    return { scale, ox, oy, view: r, base };
  }, []);
  const toDoc = useCallback((x: number, y: number): [number, number] => { const m = metrics(); return [(x - m.ox) / m.scale, (y - m.oy) / m.scale]; }, [metrics]);

  const drawScene = useCallback((ctx: CanvasRenderingContext2D, includeLogos: boolean, override: Pose | null) => {
    const p = latest.current;
    if (p.importImage) {
      ctx.drawImage(p.importImage, 0, 0, DOC.w, DOC.h);
    } else {
      const doc = p.doc;
      if (doc.outside.visible) { ctx.fillStyle = doc.outside.color; ctx.fillRect(0, 0, DOC.w, DOC.h); }
      const [bx, by, bw, bh] = p.geometry.hardwoodBounds;
      if (p.mainHardwood) {
        ctx.save(); ctx.clip(paths.surface, 'nonzero');
        ctx.drawImage(p.mainHardwood, bx, by, bw, bh); ctx.restore();
      }
      if (doc.twoPointEnabled && p.twoPointHardwood && regionPaths && doc.boundary) {
        ctx.save(); ctx.clip(paths.surface, 'nonzero'); ctx.clip(regionPaths.enclosures, 'nonzero');
        ctx.drawImage(p.twoPointHardwood, bx, by, bw, bh); ctx.restore();
      }
      if (regionPaths) {
        // Equal-color paint regions fill together so shared edges stay seamless.
        const groups = new Map<string, Path2D>();
        for (const layer of doc.paints) {
          if (!layer.visible) continue;
          const region = regionPaths.paints.get(layer.id);
          if (!region) continue;
          const key = layer.color.toUpperCase();
          const group = groups.get(key) ?? new Path2D();
          group.addPath(region); groups.set(key, group);
        }
        for (const [color, path] of groups) { ctx.fillStyle = color; ctx.fill(path, 'nonzero'); }
      }
      for (const layer of doc.lines) {
        const path = paths.lines.get(layer.id);
        if (layer.visible && path) { ctx.fillStyle = layer.color; ctx.fill(path, 'nonzero'); }
      }
    }
    if (!includeLogos) return;
    for (const logo of p.doc.logos) {
      const bitmap = logo.revision ? p.logos.get(logo.revision) : undefined;
      if (!logo.visible || logo.opacity <= 0 || !bitmap) continue;
      const q = override && override.id === logo.id ? override : poseOf(logo);
      ctx.save();
      ctx.globalAlpha = logo.opacity / 100;
      ctx.translate(q.x + q.width / 2, q.y + q.height / 2);
      ctx.rotate((q.rotation * Math.PI) / 180);
      ctx.scale(logo.flipX ? -1 : 1, logo.flipY ? -1 : 1);
      ctx.drawImage(bitmap.image, -q.width / 2, -q.height / 2, q.width, q.height);
      ctx.restore();
    }
  }, [paths, regionPaths]);

  const draw = useCallback(() => {
    frame.current = 0;
    const element = canvas.current; if (!element) return;
    const ctx = element.getContext('2d'); if (!ctx) return;
    const p = latest.current;
    const dpr = window.devicePixelRatio || 1;
    const v = view.current;
    const style = getComputedStyle(element);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = style.getPropertyValue('--workspace') || '#E1E3E7';
    ctx.fillRect(0, 0, v.width, v.height);
    const m = metrics();
    ctx.save();
    ctx.translate(m.ox, m.oy); ctx.scale(m.scale, m.scale);
    ctx.beginPath(); ctx.rect(m.view.x, m.view.y, m.view.w, m.view.h); ctx.clip();
    // Checkerboard for transparent texture areas.
    const tile = 24 / m.scale;
    ctx.fillStyle = style.getPropertyValue('--checker-base') || '#FAFAFB'; ctx.fillRect(0, 0, DOC.w, DOC.h);
    ctx.fillStyle = style.getPropertyValue('--checker-tile') || '#E4E6EB';
    if (tile > 2) {
      const x0 = Math.max(0, Math.floor(((0 - m.ox) / m.scale) / tile) * tile), y0 = Math.max(0, Math.floor(((0 - m.oy) / m.scale) / tile) * tile);
      const x1 = Math.min(DOC.w, (v.width - m.ox) / m.scale), y1 = Math.min(DOC.h, (v.height - m.oy) / m.scale);
      for (let y = y0; y < y1; y += tile) for (let x = x0 + (((y / tile) % 2) * tile); x < x1; x += tile * 2) ctx.fillRect(x, y, tile, tile);
    }
    ctx.imageSmoothingQuality = 'high';
    drawScene(ctx, p.showArtwork, pose.current);
    ctx.restore();
    const selected = p.doc.logos.find(l => l.id === p.selected);
    if (p.editing && p.showArtwork && (p.tool === 'Move' || p.tool === 'Transform') && selected && selected.visible && selected.opacity > 0 && !selected.missing) {
      const q = pose.current && pose.current.id === selected.id ? pose.current : poseOf(selected);
      const accent = style.getPropertyValue('--accent-bright') || '#4D72AA';
      ctx.save();
      ctx.translate(m.ox + (q.x + q.width / 2) * m.scale, m.oy + (q.y + q.height / 2) * m.scale);
      ctx.rotate((q.rotation * Math.PI) / 180);
      const w = q.width * m.scale, h = q.height * m.scale;
      ctx.strokeStyle = accent; ctx.lineWidth = 1.5; ctx.fillStyle = '#fff';
      ctx.strokeRect(-w / 2, -h / 2, w, h);
      for (const [hx, hy] of [[-w / 2, -h / 2], [w / 2, -h / 2], [w / 2, h / 2], [-w / 2, h / 2]]) { ctx.fillRect(hx - 4, hy - 4, 8, 8); ctx.strokeRect(hx - 4, hy - 4, 8, 8); }
      ctx.beginPath(); ctx.moveTo(0, -h / 2); ctx.lineTo(0, -h / 2 - 28); ctx.stroke();
      ctx.beginPath(); ctx.arc(0, -h / 2 - 28, 5, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
      ctx.restore();
    }
  }, [drawScene, metrics]);

  const invalidate = useCallback(() => { if (!frame.current) frame.current = requestAnimationFrame(draw); }, [draw]);
  useEffect(invalidate, [props, invalidate]);

  useEffect(() => {
    const element = host.current!;
    const observer = new ResizeObserver(() => {
      const dpr = window.devicePixelRatio || 1;
      view.current.width = element.clientWidth; view.current.height = element.clientHeight;
      const c = canvas.current!; c.width = Math.max(1, Math.round(element.clientWidth * dpr)); c.height = Math.max(1, Math.round(element.clientHeight * dpr));
      cancel(); draw(); latest.current.onZoom(metrics().scale);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [draw, metrics]);

  const changeZoom = useCallback((factor: number, at?: [number, number]) => {
    if (!isFinite(factor) || factor <= 0 || factor === 1) return;
    cancel();
    const v = view.current, before = metrics();
    const target = at ?? [v.width / 2, v.height / 2 + FIT_OFFSET];
    const docPoint = toDoc(target[0], target[1]);
    const basis = before.scale / v.zoom;
    v.zoom = Math.min(Math.max(v.zoom * factor, 0.05 / basis), 32 / basis);
    const after = metrics();
    v.panX += target[0] - (docPoint[0] * after.scale + after.ox);
    v.panY += target[1] - (docPoint[1] * after.scale + after.oy);
    invalidate(); latest.current.onZoom(metrics().scale);
  }, [invalidate, metrics, toDoc]);

  function cancel() {
    const g = gesture.current;
    gesture.current = null;
    if (pose.current) { pose.current = null; latest.current.onLivePose(null); }
    if (g && canvas.current?.hasPointerCapture(g.pointer)) canvas.current.releasePointerCapture(g.pointer);
    invalidate();
  }

  useImperativeHandle(ref, () => ({
    fit() { cancel(); Object.assign(view.current, { zoom: 1, panX: 0, panY: 0 }); invalidate(); latest.current.onZoom(metrics().scale); },
    actualSize() { changeZoom(1 / metrics().scale); },
    zoom: (factor: number) => changeZoom(factor),
    setView(kind) { cancel(); view.current.viewport = kind === 'left' ? { x: 0, y: 0, w: 4096, h: 4096 } : kind === 'right' ? { x: 4096, y: 0, w: 4096, h: 4096 } : null; Object.assign(view.current, { zoom: 1, panX: 0, panY: 0 }); invalidate(); latest.current.onZoom(metrics().scale); },
    cancel,
    focus: () => host.current?.focus(),
    scale: () => metrics().scale,
  }));

  const hitLogo = (x: number, y: number): Logo | null => {
    const p = latest.current;
    if (!p.showArtwork) return null;
    for (const logo of [...p.doc.logos].reverse()) {
      const bitmap = logo.revision ? p.logos.get(logo.revision) : undefined;
      if (!logo.visible || logo.opacity <= 0 || !bitmap) continue;
      const [lx, ly] = rotate(x - (logo.x + logo.width / 2), y - (logo.y + logo.height / 2), -logo.rotation);
      let u = lx / logo.width + 0.5, v = ly / logo.height + 0.5;
      if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
      if (logo.flipX) u = 1 - u; if (logo.flipY) v = 1 - v;
      const px = Math.min(bitmap.data.width - 1, Math.floor(u * bitmap.data.width)), py = Math.min(bitmap.data.height - 1, Math.floor(v * bitmap.data.height));
      if ((bitmap.data.data[(py * bitmap.data.width + px) * 4 + 3] * logo.opacity) / 100 < 12) continue;
      return logo;
    }
    return null;
  };

  /** Samples the composited artwork at document resolution (excluding handles), like SampleArtwork. */
  const sample = (x: number, y: number): string | null => {
    if (x < 0 || y < 0 || x >= DOC.w || y >= DOC.h) return null;
    const probe = new OffscreenCanvas(1, 1).getContext('2d')!;
    probe.translate(-Math.floor(x), -Math.floor(y));
    drawScene(probe as unknown as CanvasRenderingContext2D, latest.current.showArtwork, null);
    const [r, g, b, a] = probe.getImageData(0, 0, 1, 1).data;
    if (a === 0) return null;
    return '#' + [r, g, b].map(c => c.toString(16).padStart(2, '0')).join('').toUpperCase();
  };

  const onPointerDown = (event: React.PointerEvent) => {
    host.current?.focus();
    const p = latest.current;
    const rect = host.current!.getBoundingClientRect();
    const screen: [number, number] = [event.clientX - rect.left, event.clientY - rect.top];
    const docPoint = toDoc(screen[0], screen[1]);
    if (event.button === 1 || (event.button === 0 && (p.tool === 'Hand' || space.current))) {
      cancel();
      gesture.current = { kind: 'pan', button: event.button, start: screen, startDoc: docPoint, panStart: [view.current.panX, view.current.panY], original: { id: '', x: 0, y: 0, width: 1, height: 1, rotation: 0 }, dragged: false, pointer: event.pointerId };
      canvas.current!.setPointerCapture(event.pointerId); event.preventDefault(); return;
    }
    if (event.button !== 0) return;
    if (p.tool === 'Zoom') { changeZoom(event.altKey ? 1 / 1.25 : 1.25, screen); return; }
    if (p.tool === 'Eyedropper') { const hex = sample(docPoint[0], docPoint[1]); if (hex) p.onSample(hex); return; }
    if (p.tool === 'Bucket') { p.onFill(docPoint[0], docPoint[1]); return; }
    if (p.tool === 'Type') { const hit = hitLogo(docPoint[0], docPoint[1]); p.onText(docPoint[0], docPoint[1], hit?.isText ? hit.id : null); return; }
    if (!p.editing || !p.showArtwork) return;
    cancel();
    let kind: 'move' | 'resize' | 'rotate' | null = null; let corner = -1;
    let target = p.doc.logos.find(l => l.id === p.selected && l.visible && l.opacity > 0 && !l.missing) ?? null;
    const scale = metrics().scale;
    if (target) {
      const c = [target.x + target.width / 2, target.y + target.height / 2];
      const [lx, ly] = rotate(docPoint[0] - c[0], docPoint[1] - c[1], -target.rotation);
      const ux = c[0] + lx, uy = c[1] + ly;
      if (Math.hypot(ux - c[0], uy - (target.y - 28 / scale)) * scale <= HANDLE) kind = 'rotate';
      else {
        const handles = [[target.x, target.y], [target.x + target.width, target.y], [target.x + target.width, target.y + target.height], [target.x, target.y + target.height]];
        corner = handles.findIndex(h => Math.hypot(ux - h[0], uy - h[1]) * scale <= HANDLE);
        if (corner >= 0) kind = 'resize';
      }
      if (!kind) {
        const hit = hitLogo(docPoint[0], docPoint[1]);
        if (hit && p.doc.logos.indexOf(hit) > p.doc.logos.indexOf(target)) { target = hit; p.onSelect(hit.id); kind = 'move'; }
        else if (Math.abs(lx) <= target.width / 2 && Math.abs(ly) <= target.height / 2) kind = 'move';
      }
    }
    if (!kind) { const hit = hitLogo(docPoint[0], docPoint[1]); p.onSelect(hit?.id ?? null); target = hit; if (hit) kind = 'move'; }
    if (!kind || !target) return;
    const original = poseOf(target);
    gesture.current = { kind, button: 0, start: screen, startDoc: docPoint, panStart: [0, 0], original, dragged: false, pointer: event.pointerId,
      resize: kind === 'resize' ? new ResizeGesture(original, docPoint, corner, target.scaleLocked !== event.shiftKey) : undefined };
    canvas.current!.setPointerCapture(event.pointerId);
  };

  const onPointerMove = (event: React.PointerEvent) => {
    const g = gesture.current; if (!g) return;
    const rect = host.current!.getBoundingClientRect();
    const screen: [number, number] = [event.clientX - rect.left, event.clientY - rect.top];
    if (g.kind === 'pan') { view.current.panX = g.panStart[0] + screen[0] - g.start[0]; view.current.panY = g.panStart[1] + screen[1] - g.start[1]; invalidate(); return; }
    if (!g.dragged && Math.abs(screen[0] - g.start[0]) < DRAG && Math.abs(screen[1] - g.start[1]) < DRAG) return;
    g.dragged = true;
    const d = toDoc(screen[0], screen[1]);
    const o = g.original;
    const logo = latest.current.doc.logos.find(l => l.id === o.id);
    let next: Pose = pose.current ?? o;
    if (g.kind === 'move') next = { ...o, x: o.x + d[0] - g.startDoc[0], y: o.y + d[1] - g.startDoc[1] };
    else if (g.kind === 'resize' && g.resize) next = g.resize.update(d, (logo?.scaleLocked ?? true) !== event.shiftKey, pose.current ?? o);
    else if (g.kind === 'rotate') {
      const cx = o.x + o.width / 2, cy = o.y + o.height / 2;
      const angle = (Math.atan2(d[1] - cy, d[0] - cx) * 180) / Math.PI + 90;
      next = { ...o, rotation: (event.ctrlKey ? Math.round(angle / 15) * 15 : angle) % 360 };
    }
    pose.current = next; latest.current.onLivePose(next); invalidate();
  };

  const onPointerUp = (event: React.PointerEvent) => {
    const g = gesture.current; if (!g || event.button !== g.button) return;
    gesture.current = null;
    if (canvas.current?.hasPointerCapture(event.pointerId)) canvas.current.releasePointerCapture(event.pointerId);
    if (g.kind === 'pan') return;
    const final = pose.current;
    pose.current = null;
    if (final && g.dragged) {
      const o = g.original;
      if (final.x !== o.x || final.y !== o.y || final.width !== o.width || final.height !== o.height || final.rotation !== o.rotation) latest.current.onCommit(final);
      else latest.current.onLivePose(null);
    } else latest.current.onLivePose(null);
    invalidate();
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === ' ') { space.current = true; return; }
    if (event.key === 'Escape') { cancel(); return; }
    const p = latest.current;
    const logo = p.doc.logos.find(l => l.id === p.selected);
    if (!p.editing || !p.showArtwork || !logo || !logo.visible || logo.opacity <= 0 || logo.missing) return;
    if (event.key === 'Delete') { cancel(); p.onDelete(); event.preventDefault(); return; }
    const step = event.shiftKey ? 10 : 1;
    const delta = ({ ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] } as Record<string, [number, number]>)[event.key];
    if (!delta) return;
    cancel(); event.preventDefault();
    p.onCommit({ ...poseOf(logo), x: logo.x + delta[0], y: logo.y + delta[1] });
  };

  useEffect(() => {
    const up = (e: KeyboardEvent) => { if (e.key === ' ') space.current = false; };
    window.addEventListener('keyup', up);
    return () => window.removeEventListener('keyup', up);
  }, []);

  const cursor = props.tool === 'Hand' ? 'grab' : props.tool === 'Type' ? 'text' : ['Zoom', 'Eyedropper', 'Bucket'].includes(props.tool) ? 'crosshair' : 'default';
  return (
    <div ref={host} className="court-canvas" tabIndex={0} style={{ cursor }} onKeyDown={onKeyDown}
      onWheel={event => { const rect = host.current!.getBoundingClientRect(); changeZoom(Math.pow(1.2, -event.deltaY / 100), [event.clientX - rect.left, event.clientY - rect.top]); }}>
      <canvas ref={canvas} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onLostPointerCapture={() => { if (gesture.current && gesture.current.kind !== 'pan') cancel(); }} onContextMenu={e => e.preventDefault()} />
    </div>
  );
});
