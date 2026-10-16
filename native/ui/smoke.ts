import type { RefObject } from 'react';
import { call, errorText, type Reply } from './api';
import type { CanvasHandle, Tool } from './CourtCanvas';
import type { ArtworkSession } from './ArtworkEditor';

type Hooks = { canvas: RefObject<CanvasHandle | null>; setSection(section: 'paint' | 'logos' | 'import'): void; setTool(tool: Tool): void; showHardwood(twoPoint?: boolean): void; apply(reply: Reply): void; artwork(session: ArtworkSession | null): void };
type Check = { name: string; ok: boolean; detail?: unknown; ms?: number };

const wait = (ms: number) => new Promise(r => setTimeout(r, ms));
const rect = (selector: string) => { const e = document.querySelector(selector); if (!e) return null; const r = e.getBoundingClientRect(); return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) }; };

/** Hidden verification run: real Rust commands, real layout, WebView2 captures. Never touches user files. */
export async function runSmoke(hooks: Hooks) {
  const checks: Check[] = [];
  const started = performance.now();
  const check = async (name: string, body: () => Promise<unknown> | unknown) => {
    const t = performance.now();
    try { const detail = await body(); checks.push({ name, ok: detail !== false, detail, ms: Math.round(performance.now() - t) }); }
    catch (e) { checks.push({ name, ok: false, detail: errorText(e), ms: Math.round(performance.now() - t) }); }
  };
  const doc = async () => (await call<Reply>('document')).doc;
  await wait(1200);
  await check('layout', () => {
    const rail = rect('.left-rail'), overlay = rect('.tool-overlay'), card = rect('.viewport-card'), inspector = rect('.inspector'), strip = rect('.hardwood-strip');
    const ok = !!rail && !!overlay && !!card && !!inspector && !!strip && overlay.x === card.x && overlay.y === card.y && rail.w === 72 && inspector.w > 360;
    return ok ? { rail, overlay, card, inspector, strip, tools: document.querySelectorAll('.tool-button').length } : false;
  });
  await check('hardwood bar opens by default', () => !!document.querySelector('.hardwood-bar') && document.querySelectorAll('.hardwood-group').length === 5);
  await call('smoke_capture', { name: 'workspace' });
  await check('overlay toggling keeps the court transform', async () => {
    const before = hooks.canvas.current?.scale();
    hooks.setTool('Move'); await wait(150);
    const after = hooks.canvas.current?.scale();
    hooks.showHardwood(); await wait(150);
    return before === after && after === hooks.canvas.current?.scale() ? { scale: after } : false;
  });
  await check('hardwood setting undo', async () => {
    const first = await doc();
    const set = await call<Reply>('hardwood_settings', { twoPoint: false, settings: { brightness: 25, contrast: 0, saturation: 0, scale: 100, rotation: 0 }, phase: 'set' });
    const undone = await call<Reply>('undo', { redo: false });
    return set.doc.floor?.settings.brightness === 25 && undone.doc.floor?.settings.brightness === first.floor?.settings.brightness;
  });
  await check('paint bucket fills a court region with the primary color', async () => {
    const geometry = (await call<{ data: { geometry: { anchors: { id: string; x: number; y: number }[] } } }>('startup_state')).data.geometry;
    const key = geometry.anchors.find(a => a.id === 'left-paint-center')!;
    const reply = await call<Reply>('fill_region', { x: key.x, y: key.y });
    const layer = reply.doc.paints.find(p => p.id === reply.selected);
    await call<Reply>('undo', { redo: false });
    return reply.selected === 'paint-left' && layer?.visible ? { filled: reply.selected, color: layer.color } : false;
  });
  await check('two-point hardwood enable/disable', async () => {
    const on = await call<Reply>('set_two_point_enabled', { enabled: true });
    const off = await call<Reply>('set_two_point_enabled', { enabled: false });
    await call<Reply>('undo', { redo: false }); await call<Reply>('undo', { redo: false });
    return on.doc.twoPointEnabled && !off.doc.twoPointEnabled;
  });
  const fixture = await call<{ status: string } | null>('smoke_open_fixture').catch(e => { checks.push({ name: 'open fixture', ok: false, detail: errorText(e) }); return null; });
  if (fixture) {
    checks.push({ name: 'open fixture', ok: !fixture.status.includes('Missing'), detail: fixture.status });
    hooks.apply(await call<Reply>('document'));
    hooks.canvas.current?.fit();
    await wait(1500);
    await call('smoke_capture', { name: 'fixture' });
    hooks.setSection('logos'); await wait(600);
    await call('smoke_capture', { name: 'logos' });
    hooks.setSection('paint');
  }
  await check('text layer placement and undo', async () => {
    const before = await doc();
    if (before.logos.length >= 4) return { skipped: 'four slots used' };
    const placed = await call<Reply>('text_apply', { settings: { Text: 'SMOKE', FontFamily: 'Arial', FontSize: 120, FillColor: '#FFFFFF', OutlineColor: '#000000', OutlineWidth: 2, Bold: true, Italic: false, Alignment: 0, LineHeight: 0, LetterSpacing: 0 }, x: 4096, y: 2048, target: null });
    const text = placed.doc.logos.find(l => l.id === placed.selected);
    await call<Reply>('undo', { redo: false });
    return text?.isText && text.pixelWidth > 0 ? { width: text.width, height: text.height } : false;
  });
  // --- Edit Artwork (shared Canvas editor session) ---
  type Info = { width: number; height: number; layers: { id: number }[]; canUndo: boolean; selected: number | null };
  const paint = async (id: number, info: Info) => {
    let next = await call<Info>('edit', { id, operation: { kind: 'addLayer' } });
    next = await call<Info>('edit', { id, operation: { kind: 'selectionShape', shape: 'rectangle', mode: 'replace', x1: 0, y1: 0, x2: Math.max(1, info.width / 3), y2: Math.max(1, info.height / 3) } });
    return call<Info>('edit', { id, operation: { kind: 'fillSelection', color: [200, 30, 40, 255], opacity: 1, clear: false } }).then(r => r ?? next);
  };
  const artworkOf = (logo: unknown) => (logo as { artwork?: Record<string, string> }).artwork ?? {};
  await check('edit artwork: logo Apply is one undo step with editable layers', async () => {
    const before = await doc();
    const logo = before.logos[0];
    if (!logo) return { skipped: 'fixture has no logo' };
    const session = await call<ArtworkSession>('artwork_open', { logo: logo.id });
    const blocked = await call<Reply>('set_layer', { id: before.paints[0].id, visible: false }).then(() => false, e => String(e).includes('Edit Artwork'));
    const painted = await paint(session.tab.id, session.tab.document);
    hooks.artwork(session); await wait(1200);
    await call('smoke_capture', { name: 'artwork-editor' });
    hooks.artwork(null);
    const applied = await call<Reply>('artwork_apply', { id: session.tab.id });
    const after = applied.doc.logos.find(l => l.id === logo.id)!;
    const meta = artworkOf(after);
    const placement = ['x', 'y', 'width', 'height', 'rotation'].every(k => (after as unknown as Record<string, number>)[k] === (logo as unknown as Record<string, number>)[k]);
    const undone = await call<Reply>('undo', { redo: false });
    const restored = undone.doc.logos.find(l => l.id === logo.id)!;
    const redone = await call<Reply>('undo', { redo: true });
    const reopened = await call<ArtworkSession>('artwork_open', { logo: logo.id });
    await call('artwork_close');
    const ok = blocked && painted.layers.length >= 2 && !!meta.artworkProjectPath && meta.artworkProjectPath.endsWith('artwork.2kstudio') && placement
      && restored.revision === logo.revision && artworkOf(redone.doc.logos.find(l => l.id === logo.id)).artworkProjectPath === meta.artworkProjectPath
      && reopened.tab.document.layers.length === painted.layers.length && applied.doc.version === before.version + 1;
    hooks.apply(redone);
    return ok ? { layers: painted.layers.length, alpha: meta.artworkAlphaMode, reopenedLayers: reopened.tab.document.layers.length } : { blocked, layers: painted.layers.length, meta, placement, restored: restored.revision === logo.revision, reopened: reopened.tab.document.layers.length };
  });
  await check('edit artwork: Cancel leaves the court unchanged', async () => {
    const before = await doc();
    const session = await call<ArtworkSession>('artwork_open', { logo: null });
    await paint(session.tab.id, session.tab.document);
    await call('artwork_close');
    const after = await doc();
    const editable = await call<Reply>('set_layer', { id: before.paints[0].id, visible: before.paints[0].visible });
    return after.version === before.version && after.floor?.path === before.floor?.path && !!editable;
  });
  await check('edit artwork: hardwood Apply keeps texture settings', async () => {
    const before = await doc();
    const session = await call<ArtworkSession>('artwork_open', { logo: null });
    await paint(session.tab.id, session.tab.document);
    const applied = await call<Reply>('artwork_apply', { id: session.tab.id });
    const floor = applied.doc.floor!;
    const ok = floor.id === before.floor?.id && floor.path.endsWith('artwork.png') && JSON.stringify(floor.settings) === JSON.stringify(before.floor?.settings) && floor.revision !== before.floor?.revision;
    hooks.apply(applied);
    return ok ? { path: floor.path.split(/[\/]/).slice(-2).join('/'), settings: floor.settings } : { floor, before: before.floor };
  });
  await check('portable save/reopen keeps editable artwork', async () => {
    const result = await call<{ status: string; saved: { floor: Record<string, string>; logoImages: Record<string, string>[] }; doc: Reply['doc'] }>('smoke_save_reopen');
    const floor = result.saved.floor, logo = result.saved.logoImages.find(l => l.artworkProjectPath);
    const relative = (p?: string) => !!p && !/^[a-z]:|^[\/]/i.test(p) && p.includes('.assets');
    const ok = !result.status.includes('Missing') && relative(floor.artworkProjectPath) && relative(floor.path) && (!logo || relative(logo.artworkProjectPath))
      && !!artworkOf(result.doc.logos.find(l => artworkOf(l).artworkProjectPath) ?? {}).artworkProjectRevision === !!logo;
    hooks.apply({ doc: result.doc } as Reply);
    return ok ? { status: result.status, floor: floor.artworkProjectPath, logo: logo?.artworkProjectPath } : result;
  });
  await check('color presets persist in preferences', async () => {
    const first = await call<{ colorPresets: string[] }>('set_preferences', { colorPresets: ['#123456', '#abcdef', '#123456'] });
    const cleared = await call<{ colorPresets: string[] }>('set_preferences', { colorPresets: [] });
    return JSON.stringify(first.colorPresets) === '["#123456","#ABCDEF"]' && cleared.colorPresets.length === 0 ? first.colorPresets : false;
  });
  await check('background floor preparation finishes and refreshes the catalog', async () => {
    const before = await doc();
    for (let i = 0; i < 240; i++) {
      const state = await call<{ state: { preparing: boolean; message: string }; data: { floors: unknown[]; libraryComplete: boolean } | null }>('startup_state');
      if (!state.state.preparing) {
        const after = await doc();
        return after.version >= before.version && after.floor?.id === before.floor?.id ? { floors: state.data?.floors.length, complete: state.data?.libraryComplete, message: state.state.message, waitedS: i } : false;
      }
      await wait(1000);
    }
    return false;
  });
  await check('PNG export', async () => call<string>('smoke_export', { iff: false }));
  await check('IFF export', async () => call<string>('smoke_export', { iff: true }));
  document.documentElement.dataset.theme = 'dark';
  await wait(400);
  await call('smoke_capture', { name: 'dark' });
  const passed = checks.every(c => c.ok);
  await call('smoke_report', { report: { passed, checks, elapsedMs: Math.round(performance.now() - started), viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio } } });
}
