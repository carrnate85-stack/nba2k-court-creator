import { listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { useCallback, useEffect, useRef, useState } from 'react';
import { bitmap, call, errorText, type Doc, type Floor, type Inspection, type Layer, type Logo, type Prefs, type Regions, type Reply, type Settings, type StartupState, type StudioData } from './api';
import { CourtCanvas, type CanvasHandle, type LogoBitmap, type Pose, type Tool } from './CourtCanvas';
import { ArtworkEditor, type ArtworkSession } from './ArtworkEditor';
import { ColorPicker, FloorCatalog, LogoImporter, MessageDialog, TeamColors, TextDialog, normalizeHex } from './dialogs';
import { Icon, type IconName } from './icons';
import { runSmoke } from './smoke';

type Section = 'paint' | 'logos' | 'import';
type Modal =
  | { kind: 'catalog'; twoPoint: boolean }
  | { kind: 'picker'; title: string; color: string; apply(hex: string): void }
  | { kind: 'teams'; apply(hex: string): void }
  | { kind: 'importer' }
  | { kind: 'artwork'; session: ArtworkSession }
  | { kind: 'text'; initial: Record<string, unknown>; x: number; y: number; target: string | null }
  | { kind: 'message'; title: string; text: string; actions: { label: string; primary?: boolean; run(): void }[] };

const SLIDERS: { key: keyof Settings; label: string; min: number; max: number }[] = [
  { key: 'brightness', label: 'Brightness', min: -100, max: 100 }, { key: 'contrast', label: 'Contrast', min: -100, max: 100 },
  { key: 'saturation', label: 'Saturation', min: -100, max: 100 }, { key: 'scale', label: 'Grain scale', min: 50, max: 200 }, { key: 'rotation', label: 'Rotation', min: -180, max: 180 },
];
const DEFAULT_SETTINGS: Settings = { brightness: 0, contrast: 0, saturation: 0, scale: 100, rotation: 0 };
const smokeMode = new URLSearchParams(location.search).has('smoke');

function useOutside(open: boolean, close: () => void) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) close(); };
    window.addEventListener('mousedown', down);
    return () => window.removeEventListener('mousedown', down);
  }, [open, close]);
  return ref;
}

function Menu({ label, children, className, align }: { label: React.ReactNode; children: (close: () => void) => React.ReactNode; className?: string; align?: 'right' }) {
  const [open, setOpen] = useState(false);
  const close = useCallback(() => setOpen(false), []);
  const ref = useOutside(open, close);
  return (
    <div ref={ref} className={`menu ${open ? 'open' : ''}`}>
      <button className={className} onClick={() => setOpen(o => !o)}>{label}</button>
      {open && <div className={`popup ${align === 'right' ? 'right' : ''}`}>{children(close)}</div>}
    </div>
  );
}

function Item({ label, shortcut, onClick, disabled, title }: { label: string; shortcut?: string; onClick(): void; disabled?: boolean; title?: string }) {
  return <button disabled={disabled} title={title} onClick={onClick}><span>{label}</span>{shortcut && <span className="shortcut">{shortcut}</span>}</button>;
}

/** Brightness/contrast/saturation/grain scale/rotation slider with click-to-type exact values. */
function HardwoodSlider({ spec, value, disabled, onInput, onCommit, onTyped }: { spec: (typeof SLIDERS)[number]; value: number; disabled: boolean; onInput(v: number): void; onCommit(v: number): void; onTyped(v: number): boolean }) {
  const [editing, setEditing] = useState(false), [text, setText] = useState(''), [invalid, setInvalid] = useState(false);
  const group = useRef<HTMLDivElement>(null), [compact, setCompact] = useState(false);
  useEffect(() => {
    const observer = new ResizeObserver(() => setCompact((group.current?.clientWidth ?? 200) < 128));
    observer.observe(group.current!);
    return () => observer.disconnect();
  }, []);
  const commitText = () => {
    const n = Number.parseInt(text.trim().replace(/[%°]$/, ''), 10);
    if (!Number.isFinite(n) || n < spec.min || n > spec.max || !/^-?\d+[%°]?$/.test(text.trim())) { setInvalid(true); return false; }
    setInvalid(false); setEditing(false); return onTyped(n);
  };
  const fill = ((value - spec.min) / (spec.max - spec.min)) * 100;
  return (
    <div ref={group} className={`hardwood-group ${compact ? 'compact' : ''}`}>
      <span className="caption" title={spec.label}>{spec.label}</span>
      <input type="range" className="slider" aria-label={`Hardwood ${spec.key}`} min={spec.min} max={spec.max} step={1} value={value} disabled={disabled} style={{ ['--fill' as string]: `${fill}%` }}
        onChange={e => onInput(Number(e.target.value))} onPointerUp={e => onCommit(Number((e.target as HTMLInputElement).value))} onKeyUp={e => onCommit(Number((e.target as HTMLInputElement).value))} />
      {editing ? (
        <input className={`value-input ${invalid ? 'invalid' : ''}`} autoFocus value={text} aria-label={`Hardwood ${spec.key} exact value`} onChange={e => setText(e.target.value)} onFocus={e => e.target.select()}
          onBlur={() => { if (!commitText()) return; }} onKeyDown={e => { if (e.key === 'Enter') commitText(); else if (e.key === 'Escape') { setEditing(false); setInvalid(false); } }} />
      ) : (
        <button className="value-button" disabled={disabled} title={`Click to enter ${spec.key} (${spec.min} to ${spec.max})`} aria-label={`Enter hardwood ${spec.key}`} onClick={() => { setText(String(value)); setEditing(true); }}>{value}{spec.key === 'rotation' ? '°' : '%'}</button>
      )}
    </div>
  );
}

function Thumb({ path }: { path: string | undefined }) {
  const [url, setUrl] = useState<string>();
  useEffect(() => {
    let alive = true, created: string | undefined;
    if (!path) { setUrl(undefined); return; }
    bitmap('floor_thumbnail', { path }).then(async ({ data }) => {
      const c = new OffscreenCanvas(data.width, data.height); c.getContext('2d')!.putImageData(data, 0, 0);
      created = URL.createObjectURL(await c.convertToBlob());
      if (alive) setUrl(created);
    }).catch(() => alive && setUrl(undefined));
    return () => { alive = false; if (created) URL.revokeObjectURL(created); };
  }, [path]);
  return url ? <img className="thumb" src={url} alt="" /> : <span className="thumb" />;
}

function LogoThumb({ bitmapEntry }: { bitmapEntry?: LogoBitmap }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const c = ref.current; if (!c || !bitmapEntry) return;
    const scale = Math.min(38 / bitmapEntry.image.width, 38 / bitmapEntry.image.height);
    c.width = Math.max(1, Math.round(bitmapEntry.image.width * scale)); c.height = Math.max(1, Math.round(bitmapEntry.image.height * scale));
    c.getContext('2d')!.drawImage(bitmapEntry.image, 0, 0, c.width, c.height);
  }, [bitmapEntry]);
  return <div className="logo-thumb"><canvas ref={ref} /></div>;
}

export function App() {
  const [startup, setStartup] = useState<StartupState>({ phase: 'loading', message: 'Starting studio...', preparing: false, retry: false });
  const [data, setData] = useState<StudioData | null>(null);
  const [doc, setDoc] = useState<Doc | null>(null);
  const [floors, setFloors] = useState<Floor[]>([]);
  const [prefs, setPrefs] = useState<Prefs>({ favorites: [], recent: [], dark: false, primaryColor: '#19583F', colorPresets: [] });
  const [status, setStatusText] = useState('Starting studio...');
  const [section, setSection] = useState<Section>('paint');
  const [tool, setTool] = useState<Tool>('Move');
  const [hardwoodTool, setHardwoodTool] = useState(true);
  const [editingTwoPoint, setEditingTwoPoint] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [colorLayer, setColorLayer] = useState('paint-left');
  const [livePose, setLivePose] = useState<Pose | null>(null);
  const [zoomText, setZoomText] = useState('Fit');
  const [modal, setModal] = useState<Modal | null>(null);
  const [regions, setRegions] = useState<Map<string, Regions>>(new Map());
  const [hardwood, setHardwood] = useState<{ main: ImageBitmap | null; two: ImageBitmap | null }>({ main: null, two: null });
  const [logoBitmaps, setLogoBitmaps] = useState<Map<string, LogoBitmap>>(new Map());
  const [draft, setDraft] = useState<{ twoPoint: boolean; settings: Settings } | null>(null);
  const [historyBusy, setHistoryBusy] = useState(false);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [importState, setImportState] = useState<{ inspection: Inspection; edges: string[]; image: ImageBitmap | null; previewed: string | null } | null>(null);
  const [opacityDraft, setOpacityDraft] = useState<number | null>(null);
  const [maximized, setMaximized] = useState(false);
  const canvas = useRef<CanvasHandle>(null);
  const docRef = useRef(doc); docRef.current = doc;

  const setStatus = useCallback((text: string) => setStatusText(text), []);
  const apply = useCallback((reply: Reply | null | undefined) => {
    if (!reply) return;
    setDoc(reply.doc);
    if (reply.status) setStatusText(reply.status);
    if (reply.selected !== undefined) setSelected(reply.selected ?? null);
  }, []);
  const showError = useCallback((error: unknown) => {
    const text = errorText(error);
    setStatusText(text);
    if (!smokeMode) setModal({ kind: 'message', title: 'Court Creator', text, actions: [{ label: 'OK', primary: true, run: () => setModal(null) }] });
  }, []);
  const run = useCallback(async (job: () => Promise<Reply | null | undefined | void>, quiet = false) => {
    try { const reply = await job(); if (reply && 'doc' in reply) apply(reply); }
    catch (e) { if (quiet) setStatusText(errorText(e)); else showError(e); }
  }, [apply, showError]);

  // --- Startup and background preparation ---
  const loadData = useCallback(async () => {
    const result = await call<{ state: StartupState; data: StudioData | null }>('startup_state');
    setStartup(result.state);
    if (result.data) {
      setData(result.data); setDoc(result.data.doc); setFloors(result.data.floors); setPrefs(result.data.prefs);
      // Presets saved by the first preview build lived in WebView storage; move them to preferences once.
      try { const old: unknown = JSON.parse(localStorage.getItem('court-presets') ?? '[]'); if (Array.isArray(old) && old.length && !smokeMode) { void call<Prefs>('set_preferences', { colorPresets: [...new Set([...result.data.prefs.colorPresets, ...old])].slice(0, 128) }).then(p => { setPrefs(p); localStorage.removeItem('court-presets'); }).catch(() => {}); } } catch { /* nothing to migrate */ }
      setStatusText(result.state.message);
    } else setStatusText(result.state.message);
  }, []);
  useEffect(() => {
    void loadData();
    const offs = [
      listen<StartupState>('startup', e => { setStartup(e.payload); if (e.payload.phase !== 'ready' || !e.payload.preparing) setStatusText(e.payload.message); }),
      listen('studio-ready', () => { void loadData(); }),
      listen<{ floors: Floor[] }>('catalog', e => { if (e.payload.floors) setFloors(e.payload.floors); }),
      listen<string>('status', e => setStatusText(e.payload)),
      listen('close-request', () => requestClose()),
    ];
    return () => { offs.forEach(p => p.then(off => off())); };
  }, [loadData]);
  useEffect(() => { document.documentElement.dataset.theme = prefs.dark || new URLSearchParams(location.search).has('dark') ? 'dark' : 'light'; }, [prefs.dark]);
  useEffect(() => {
    const win = getCurrentWindow();
    const update = () => win.isMaximized().then(setMaximized).catch(() => {});
    update();
    const off = win.onResized(update);
    return () => { off.then(f => f()); };
  }, []);

  // --- Derived court data ---
  const boundaryKey = doc?.boundary ?? 'none';
  useEffect(() => {
    if (!data || regions.has(boundaryKey)) return;
    call<Regions>('regions', { boundary: doc?.boundary ?? null }).then(r => setRegions(m => new Map(m).set(boundaryKey, r))).catch(e => setStatusText(errorText(e)));
  }, [data, boundaryKey, regions, doc?.boundary]);

  const mainKey = doc?.floor ? `${doc.floor.revision}|${JSON.stringify(draft && !draft.twoPoint ? draft.settings : doc.floor.settings)}` : '';
  const twoKey = doc?.twoPoint ? `${doc.twoPoint.revision}|${JSON.stringify(draft && draft.twoPoint ? draft.settings : doc.twoPoint.settings)}` : '';
  useEffect(() => {
    if (!mainKey) return;
    const timer = setTimeout(() => {
      const settings = draft && !draft.twoPoint ? draft.settings : undefined;
      bitmap('hardwood_preview', { twoPoint: false, settings }).then(({ image }) => setHardwood(h => ({ ...h, main: image }))).catch(e => setStatusText(errorText(e)));
    }, 80);
    return () => clearTimeout(timer);
  }, [mainKey]); // eslint-disable-line
  useEffect(() => {
    if (!twoKey) { setHardwood(h => ({ ...h, two: null })); return; }
    const timer = setTimeout(() => {
      const settings = draft && draft.twoPoint ? draft.settings : undefined;
      bitmap('hardwood_preview', { twoPoint: true, settings }).then(({ image }) => setHardwood(h => ({ ...h, two: image }))).catch(e => setStatusText(errorText(e)));
    }, 80);
    return () => clearTimeout(timer);
  }, [twoKey]); // eslint-disable-line
  useEffect(() => {
    if (!doc) return;
    for (const logo of doc.logos) {
      if (!logo.revision || logoBitmaps.has(logo.revision)) continue;
      const revision = logo.revision;
      bitmap('logo_pixels', { revision }).then(({ data: pixels, image }) => setLogoBitmaps(m => new Map(m).set(revision, { image, data: pixels }))).catch(() => {});
    }
  }, [doc, logoBitmaps]);

  const ready = !!(data && doc);
  const canEdit = ready && !historyBusy && !modal;
  const selectedLogo = doc?.logos.find(l => l.id === selected) ?? null;
  const hardwoodTarget = editingTwoPoint && doc?.twoPointEnabled ? doc?.twoPoint : doc?.floor;
  const hardwoodValues = draft && draft.twoPoint === (editingTwoPoint && !!doc?.twoPointEnabled) ? draft.settings : hardwoodTarget?.settings ?? DEFAULT_SETTINGS;

  // --- Commands ---
  const selectTool = (next: Tool) => {
    if (section === 'import' && ['Transform', 'Eyedropper', 'Bucket', 'Type'].includes(next)) return;
    void commitHardwood();
    setHardwoodTool(false);
    canvas.current?.cancel();
    if (next === 'Transform' && !selectedLogo) return;
    setTool(next); canvas.current?.focus();
  };
  const showHardwood = (twoPoint = false) => { void commitHardwood(); canvas.current?.cancel(); setTool('Move'); setHardwoodTool(true); setEditingTwoPoint(twoPoint); };
  const switchSection = (next: Section) => {
    void commitHardwood();
    canvas.current?.cancel(); setSection(next);
    if (next === 'import' && ['Transform', 'Eyedropper', 'Bucket', 'Type'].includes(tool)) setTool('Move');
  };
  const commitHardwood = async () => {
    const pending = draftRef.current;
    if (!pending) return;
    draftRef.current = null;
    try { apply(await call<Reply>('hardwood_settings', { twoPoint: pending.twoPoint, settings: pending.settings, phase: 'commit' })); }
    catch (e) { setStatusText(errorText(e)); }
    setDraft(null);
  };
  const draftRef = useRef<{ twoPoint: boolean; settings: Settings } | null>(null);
  const hardwoodInput = (key: keyof Settings, value: number) => {
    if (!hardwoodTarget) return;
    const twoPoint = editingTwoPoint && !!doc?.twoPointEnabled;
    const settings = { ...hardwoodValues, [key]: value };
    draftRef.current = { twoPoint, settings }; setDraft({ twoPoint, settings });
    void call('hardwood_settings', { twoPoint, settings, phase: 'preview' }).catch(() => {});
  };
  const hardwoodCommit = (key: keyof Settings, value: number) => { if (draftRef.current) { draftRef.current.settings = { ...draftRef.current.settings, [key]: value }; void commitHardwood(); } };
  const hardwoodTyped = (key: keyof Settings, value: number) => { hardwoodInput(key, value); void commitHardwood(); return true; };
  const resetHardwood = () => run(async () => { draftRef.current = null; setDraft(null); return call<Reply>('hardwood_settings', { twoPoint: editingTwoPoint && !!doc?.twoPointEnabled, settings: DEFAULT_SETTINGS, phase: 'set' }); });

  const setLayer = (id: string, visible?: boolean, color?: string) => run(() => call<Reply>('set_layer', { id, visible, color }));
  const undo = (redo: boolean) => run(async () => {
    await commitHardwood(); canvas.current?.cancel();
    setHistoryBusy(true);
    try { return await call<Reply>('undo', { redo }); } finally { setHistoryBusy(false); }
  });
  const savePrefs = async (patch: Record<string, unknown>) => { try { setPrefs(await call<Prefs>('set_preferences', patch)); } catch (e) { setStatusText(errorText(e)); } };
  const pickLayerColor = (layer: Layer, team: boolean) => {
    if (!layer.visible) return;
    setColorLayer(layer.id);
    const applyColor = (hex: string) => { setModal(null); void setLayer(layer.id, undefined, hex); };
    setModal(team ? { kind: 'teams', apply: applyColor } : { kind: 'picker', title: layer.name, color: layer.color, apply: applyColor });
  };
  const chooseFloor = (floor: Floor, twoPoint: boolean) => run(async () => { setModal(null); const reply = await call<Reply>('select_floor', { id: floor.id, twoPoint }); if (twoPoint) setEditingTwoPoint(true); return reply; });
  const transformLogo = (pose: Pose) => run(async () => { setLivePose(null); return call<Reply>('logo_transform', { ...pose }); }, true);
  const updateLogo = (id: string, patch: Record<string, unknown>) => run(() => call<Reply>('logo_update', { id, patch }), true);
  const confirmReplace = (then: () => void) => {
    if (!doc?.dirty) { then(); return; }
    setModal({ kind: 'message', title: 'Court Creator', text: 'The current court has unsaved changes. Continue? A recovery copy is kept locally.', actions: [
      { label: 'Yes', primary: true, run: () => { setModal(null); then(); } }, { label: 'No', run: () => setModal(null) }] });
  };
  const newCourt = () => confirmReplace(() => run(async () => { await commitHardwood(); setSection('paint'); const reply = await call<Reply>('new_project'); canvas.current?.fit(); setSelected(null); return reply; }));
  const openCourt = () => confirmReplace(() => run(async () => { await commitHardwood(); setStatusText('Opening project...'); const reply = await call<Reply | null>('open_project'); if (reply) setSelected(reply.doc.logos[0]?.id ?? null); return reply; }));
  const save = (saveAs: boolean) => run(async () => { await commitHardwood(); setStatusText('Saving project and portable assets...'); return call<Reply | null>('save_project', { saveAs }); });
  const exportCourt = (iff: boolean) => run(async () => {
    await commitHardwood();
    setStatusText(iff ? 'Building the single-texture NBA 2K27 IFF...' : 'Rendering the original assets at 8192 × 4096...');
    const result = await call<string | null>('export_court', { iff });
    if (result) setStatusText(result);
  });
  /** Edit > Edit Artwork: the selected logo on the Logos tab, otherwise the main hardwood. */
  const [cardMenu, setCardMenu] = useState<{ x: number; y: number } | null>(null);
  const editArtwork = (logo: boolean) => run(async () => {
    await commitHardwood(); canvas.current?.cancel();
    const target = logo && section === 'logos' ? selectedLogo : null;
    if (logo && section === 'logos' && !target) { setStatusText('Select a logo or graphic first.'); return; }
    setStatusText(`Opening ${target ? target.name : 'the court texture'} for editing...`);
    const session = await call<ArtworkSession>('artwork_open', { logo: target?.id ?? null });
    setModal({ kind: 'artwork', session }); setStatusText('Edit Artwork is open. Apply or Cancel to return to the court.');
  });
  const sendToCanvas = () => run(async () => { await commitHardwood(); const result = await call<string | null>('send_to_canvas'); if (result) setStatusText(result); });
  const prepareBase = () => run(async () => { setStatusText('Preparing the game export base...'); setStatusText(await call<string>('prepare_export_base')); });
  const toggleTheme = () => void savePrefs({ dark: !prefs.dark });
  const placeText = (x: number, y: number, hit: string | null) => {
    const target = hit ? doc?.logos.find(l => l.id === hit) : null;
    if (!target && (doc?.logos.length ?? 0) >= 4) { setStatusText('Text uses a court artwork slot. Delete a layer before adding text; four slots are supported.'); return; }
    if (target?.textSettings) { setModal({ kind: 'text', initial: target.textSettings, x, y, target: target.id }); return; }
    void call<Record<string, unknown>>('text_defaults').then(initial => setModal({ kind: 'text', initial, x, y, target: null }));
  };
  const editSelectedText = () => { if (selectedLogo?.textSettings) setModal({ kind: 'text', initial: selectedLogo.textSettings, x: selectedLogo.x + selectedLogo.width / 2, y: selectedLogo.y + selectedLogo.height / 2, target: selectedLogo.id }); };

  const requestClose = () => {
    const close = async (force: boolean) => {
      try { await call('app_exit', { force }); }
      catch (e) {
        setModal({ kind: 'message', title: 'Court Creator', text: 'The latest recovery or settings could not be saved. Close without them? Previous files remain available.\n\n' + errorText(e), actions: [
          { label: 'Yes', run: () => void close(true) }, { label: 'No', primary: true, run: () => setModal(null) }] });
      }
    };
    void commitHardwood();
    if (docRef.current?.dirty && !smokeMode) {
      setModal({ kind: 'message', title: 'Court Creator', text: 'The current court has unsaved changes. Close anyway? A recovery copy is kept locally.', actions: [
        { label: 'Close', primary: true, run: () => { setModal(null); void close(false); } }, { label: 'Cancel', run: () => setModal(null) }] });
    } else void close(false);
  };

  // --- Import older court ---
  const importOpen = (texture?: string) => run(async () => {
    setStatusText('Reading source court textures...');
    const inspection = await call<Inspection | null>('import_open', { path: texture ? importState?.inspection.path : null, texture: texture ?? null });
    if (!inspection) return;
    const edges = (inspection.sourceBounds ?? [0, 0, 0, 0]).map(String);
    setImportState({ inspection, edges, image: null, previewed: null });
    await importPreview(inspection, edges);
  });
  const importPreview = async (inspection: Inspection, edges: string[]) => {
    const bounds = edges.map(e => Number.parseInt(e, 10));
    if (bounds.some(b => !Number.isFinite(b))) { setStatusText('Enter whole-number court edges.'); return; }
    setStatusText('Aligning the imported court...');
    try {
      const { image } = await bitmap('import_preview', { bounds });
      setImportState(s => (s && s.inspection.path === inspection.path ? { ...s, image, previewed: edges.join(',') } : s));
      setStatusText('Imported court aligned to the 2K27 texture size.');
    } catch (e) { setImportState(s => (s ? { ...s, previewed: null } : s)); showError(e); }
  };
  const importExport = (iff: boolean) => run(async () => {
    if (!importState || importState.previewed !== importState.edges.join(',')) throw new Error('Refresh the conversion preview before exporting this court.');
    const result = await call<string | null>('import_export', { bounds: importState.edges.map(Number), iff });
    if (result) setStatusText(result);
  });

  // --- Keyboard shortcuts (outside text inputs) ---
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (!ready || modal || e.altKey) return;
      const ctrl = e.ctrlKey || e.metaKey;
      const k = e.key.toLowerCase();
      if (ctrl && k === 's') { e.preventDefault(); void save(e.shiftKey); return; }
      const typing = e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLSelectElement;
      if (typing) return;
      if (ctrl && k === 'z') { e.preventDefault(); void undo(e.shiftKey); }
      else if (ctrl && k === 'y') { e.preventDefault(); void undo(true); }
      else if (ctrl && k === 'n') { e.preventDefault(); newCourt(); }
      else if (ctrl && k === 'o') { e.preventDefault(); openCourt(); }
      else if (ctrl && k === '0') { e.preventDefault(); canvas.current?.fit(); }
      else if (ctrl && k === '1') { e.preventDefault(); canvas.current?.actualSize(); }
      else if (ctrl && (k === '=' || k === '+')) { e.preventDefault(); canvas.current?.zoom(1.25); }
      else if (ctrl && k === '-') { e.preventDefault(); canvas.current?.zoom(1 / 1.25); }
      else if (ctrl && k === 't' && selectedLogo) { e.preventDefault(); selectTool('Transform'); }
      else if (!ctrl && !e.shiftKey) {
        const map: Record<string, Tool> = { v: 'Move', h: 'Hand', z: 'Zoom', i: 'Eyedropper', g: 'Bucket', t: 'Type' };
        if (map[k]) { e.preventDefault(); selectTool(map[k]); }
      }
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  });

  useEffect(() => { if (smokeMode && ready) void runSmoke({ canvas, setSection, setTool: selectTool, showHardwood, apply: r => { apply(r); setSelected(r.doc.logos[0]?.id ?? null); }, artwork: session => setModal(session ? { kind: 'artwork', session } : null) }); }, [ready]); // eslint-disable-line

  // --- Rendering helpers ---
  const editingEnabled = section !== 'import' && ((!hardwoodTool && (tool === 'Move' || tool === 'Transform')) || section === 'logos');
  const showOptions = section !== 'import' && (tool === 'Move' || tool === 'Transform') && !!selectedLogo && !hardwoodTool;
  const showText = !hardwoodTool && tool === 'Type' && section !== 'import';
  const hint = tool === 'Bucket' ? 'Click a court region to fill it with the primary color. Undo restores its previous color or hardwood.'
    : tool === 'Type' ? 'Click to place text; click an existing text layer to edit. Move and Transform position it.'
    : tool === 'Transform' ? 'Drag corner handles to resize; top handle to rotate. Hold Shift to temporarily invert aspect lock.'
    : tool === 'Eyedropper' ? 'Click court artwork to store its color in the primary swatch.'
    : tool === 'Hand' ? 'Drag to pan. Scroll to zoom.' : tool === 'Zoom' ? 'Click to zoom in; Alt-click to zoom out.'
    : editingEnabled ? 'Drag a logo to move it. Corner handles resize; top handle rotates.' : 'Scroll to zoom. Space + drag to pan.';
  const lineOrder = (layers: Layer[]) => {
    const outer = layers.findIndex(l => l.id === 'line_center_circle_outer_lowShape');
    const rank = (l: Layer) => (l.id === 'NBA_line_three_point_lowShape' ? -30 : l.id === 'college-three' ? -20 : l.id === 'high-school-three' ? -10 : l.id === 'line_center_circle_inner_lowShape' ? outer : layers.indexOf(l));
    return [...layers].sort((a, b) => rank(a) - rank(b) || (a.id === 'line_center_circle_inner_lowShape' ? -1 : b.id === 'line_center_circle_inner_lowShape' ? 1 : 0));
  };
  const toolButton = (name: Tool, icon: IconName, tip: string, enabled = true) => (
    <button className={`tool-button ${!hardwoodTool && tool === name ? 'selected' : ''}`} title={tip} aria-label={tip.split('•')[0].trim()} disabled={!enabled || !ready} onClick={() => selectTool(name)}><Icon name={icon} size={22} /></button>
  );
  const pose = livePose && livePose.id === selected ? livePose : selectedLogo;

  const layerRow = (layer: Layer, index: number) => (
    <div key={layer.id} className={`layer-row ${index % 2 === 0 ? 'alternate' : ''} ${layer.id === colorLayer ? 'selected' : ''}`} onMouseDown={() => setColorLayer(layer.id)}>
      <span className="layer-name" title={layer.name} onDoubleClick={() => setLayer(layer.id, !layer.visible)}>{layer.name}</span>
      <button className={`toggle ${layer.visible ? 'on' : ''}`} title={`Toggle ${layer.name}`} aria-label={`Toggle ${layer.name}`} aria-pressed={layer.visible} disabled={!canEdit} onClick={() => setLayer(layer.id, !layer.visible)} />
      {layer.visible ? (
        <div className="layer-colors">
          <button className="swatch-button" title={`Pick ${layer.name} color`} aria-label={`Pick ${layer.name} color`} disabled={!canEdit} onClick={() => pickLayerColor(layer, false)}><span style={{ background: layer.color }} /></button>
          <HexField key={layer.id + layer.color} value={layer.color} disabled={!canEdit} onCommit={hex => setLayer(layer.id, undefined, hex)} onInvalid={() => setStatusText('Enter a valid three- or six-digit hex code.')} />
          <button className="team-button" title={`Choose ${layer.name} color from a team palette`} disabled={!canEdit} onClick={() => pickLayerColor(layer, true)}>Team Colors</button>
        </div>
      ) : <span />}
    </div>
  );

  if (!ready) {
    return (
      <div className="studio">
        <TopChrome maximized={maximized} onClose={requestClose} menus={null} actions={null} />
        <div className="document-chrome" />
        <div className="loading-screen"><div className="stack">
          {startup.phase === 'needs-game' || startup.phase === 'failed' ? null : <div className="spinner" />}
          <span>{startup.message}</span>
          {startup.phase === 'needs-game' && <button className="action" style={{ padding: '6px 16px' }} onClick={() => call('choose_game_folder').catch(showError)}>Choose NBA 2K27 folder…</button>}
          {startup.retry && <button onClick={() => call('retry_startup')}>Retry</button>}
        </div></div>
        <div className="status-bar"><span className="status-text">{status}</span><span>8192 x 4096 • NBA 2K27</span></div>
        {modal?.kind === 'message' && <MessageDialog title={modal.title} text={modal.text} actions={modal.actions} onClose={() => setModal(null)} />}
      </div>
    );
  }
  const d = doc!;
  const regionsNow = regions.get(boundaryKey) ?? null;
  const twoPointShown = d.twoPointEnabled;
  const index = selectedLogo ? d.logos.indexOf(selectedLogo) : -1;
  const slotsFull = d.logos.length >= 4;

  return (
    <div className="studio">
      <TopChrome maximized={maximized} onClose={requestClose} menus={<>
        <Menu label="File">{close => <>
          <Item label="New court" shortcut="Ctrl+N" onClick={() => { close(); newCourt(); }} />
          <Item label="Open project..." shortcut="Ctrl+O" onClick={() => { close(); openCourt(); }} />
          <Item label="Save project" shortcut="Ctrl+S" onClick={() => { close(); void save(false); }} />
          <Item label="Save project as..." shortcut="Ctrl+Shift+S" title="Save a separate project copy" onClick={() => { close(); void save(true); }} />
          <hr />
          <Item label="Import older court..." onClick={() => { close(); switchSection('import'); }} />
          <Item label="Refresh court" onClick={() => { close(); if (section === 'import' && importState) void importPreview(importState.inspection, importState.edges); else if (d.floor) void chooseFloor({ id: d.floor.id, name: d.floor.name, category: d.floor.category, previewPath: d.floor.previewPath }, false); }} />
          <Item label="Prepare game export base" onClick={() => { close(); void prepareBase(); }} />
        </>}</Menu>
        <Menu label="Edit">{close => <>
          <Item label="Undo" shortcut="Ctrl+Z" disabled={!d.canUndo} onClick={() => { close(); void undo(false); }} />
          <Item label="Redo" shortcut="Ctrl+Y" disabled={!d.canRedo} title="Redo (Ctrl+Y or Ctrl+Shift+Z)" onClick={() => { close(); void undo(true); }} />
          <Item label="Edit Artwork..." title="Edit the selected court texture, logo or graphic" disabled={!canEdit} onClick={() => { close(); void editArtwork(true); }} />
        </>}</Menu>
        <Menu label="View">{close => <>
          <Item label="Fit court" shortcut="Ctrl+0" onClick={() => { close(); canvas.current?.fit(); }} />
          <Item label="Switch light / dark appearance" onClick={() => { close(); toggleTheme(); }} />
        </>}</Menu>
      </>} actions={<>
        <button className="quiet" title="Save project (Ctrl+S)" onClick={() => void save(false)}>Save</button>
        <Menu align="right" className="action export-button" label={<><span>Export</span><Icon name="chevronDown" size={8} /></>}>{close => <>
          <Item label="Export Texture PNG..." onClick={() => { close(); void exportCourt(false); }} />
          <Item label="Build NBA 2K27 IFF..." onClick={() => { close(); void exportCourt(true); }} />
          <Item label="Send Texture to 2K Canvas" onClick={() => { close(); void sendToCanvas(); }} />
        </>}</Menu>
      </>} />
      <div className="document-chrome">
        <span />
        <div style={{ display: 'flex', alignItems: 'flex-end' }}>
          <div className="project-tab">
            {renaming === null ? (
              <button title={`${d.name} — click to rename`} onClick={() => setRenaming(d.name)}><Icon name="document" size={14} /><span>{d.name}</span></button>
            ) : (
              <input autoFocus maxLength={120} value={renaming} onChange={e => setRenaming(e.target.value)} onFocus={e => e.target.select()}
                onKeyDown={e => { if (e.key === 'Enter') { const name = renaming; setRenaming(null); if (name.trim() !== d.name) void run(() => call<Reply>('rename_project', { name })); } else if (e.key === 'Escape') setRenaming(null); }}
                onBlur={() => { const name = renaming; setRenaming(null); if (name.trim() && name.trim() !== d.name) void run(() => call<Reply>('rename_project', { name })); }} />
            )}
          </div>
        </div>
        <div className="nav-controls">
          <button className="canvas-action" title="Fit on screen (Ctrl+0)" aria-label="Fit on screen" onClick={() => canvas.current?.fit()}><Icon name="fit" size={17} /></button>
          <Menu className="zoom-button" label={<><span>{zoomText}</span><Icon name="chevronDown" size={8} /></>}>{close => <>
            <Item label="Fit on screen" shortcut="Ctrl+0" onClick={() => { close(); canvas.current?.fit(); }} />
            <Item label="100% — actual pixels" shortcut="Ctrl+1" onClick={() => { close(); canvas.current?.actualSize(); }} />
            <hr />
            <Item label="Zoom in" shortcut="Ctrl++" onClick={() => { close(); canvas.current?.zoom(1.25); }} />
            <Item label="Zoom out" shortcut="Ctrl+-" onClick={() => { close(); canvas.current?.zoom(1 / 1.25); }} />
            <hr />
            <Item label="Full Court" onClick={() => { close(); canvas.current?.setView('full'); }} />
            <Item label="Left Half" onClick={() => { close(); canvas.current?.setView('left'); }} />
            <Item label="Right Half" onClick={() => { close(); canvas.current?.setView('right'); }} />
          </>}</Menu>
          <button className="canvas-action" title="Hand tool (H) – hold Space or middle mouse to pan" onClick={() => selectTool('Hand')}><Icon name="Hand" size={19} /></button>
          <span className="divider-v" />
          <button className="canvas-action" title="Undo (Ctrl+Z)" aria-label="Undo" disabled={!d.canUndo || !canEdit} onClick={() => void undo(false)}><Icon name="undo" size={17} /></button>
          <button className="canvas-action" title="Redo (Ctrl+Y)" aria-label="Redo" disabled={!d.canRedo || !canEdit} onClick={() => void undo(true)}><Icon name="redo" size={17} /></button>
        </div>
      </div>
      <div className="workspace">
        <div className="left-rail">
          <div className="rail-tools">
            <button className={`tool-button ${hardwoodTool ? 'selected' : ''}`} title="Adjust hardwood texture" aria-label="Hardwood texture tool" onClick={() => showHardwood(false)}><Icon name="hardwood" size={22} /></button>
            {toolButton('Move', 'Move', 'Move tool • V\nSelect and drag a logo; corner handles resize, top handle rotates')}
            {toolButton('Transform', 'transform', 'Resize / rotate logo • Ctrl+T\nDrag corner handles to resize, top handle to rotate', !!selectedLogo && section !== 'import')}
            {toolButton('Eyedropper', 'Eyedropper', 'Eyedropper tool • I\nSample a primary color without changing the court', section !== 'import')}
            {toolButton('Bucket', 'Bucket', 'Paint tool • G\nFill a court region with the primary color', section !== 'import' && canEdit)}
            {toolButton('Type', 'Type', 'Text tool • T\nClick to place text or edit an existing text layer', section !== 'import' && canEdit)}
            <div className="rail-divider" />
            {toolButton('Hand', 'Hand', 'Hand tool • H • hold Space temporarily')}
            {toolButton('Zoom', 'Zoom', 'Zoom tool • Z (Alt-click zooms out)')}
          </div>
          <div className="pinned">
            <button className="primary-swatch" title={`Primary color ${prefs.primaryColor} · sample with Eyedropper or click to choose`} disabled={!canEdit}
              onClick={() => setModal({ kind: 'picker', title: 'Primary color', color: prefs.primaryColor, apply: hex => { setModal(null); void savePrefs({ primaryColor: hex }); } })}><span style={{ background: prefs.primaryColor }} /></button>
            <button className="canvas-action" title="Switch light / dark appearance" onClick={toggleTheme}><Icon name="moon" size={19} /></button>
          </div>
        </div>
        <div className="viewport-card">
          <CourtCanvas ref={canvas} geometry={data!.geometry} regions={regionsNow} doc={d} mainHardwood={hardwood.main} twoPointHardwood={twoPointShown ? hardwood.two : null}
            logos={logoBitmaps} importImage={section === 'import' ? importState?.image ?? null : null} tool={tool} editing={editingEnabled && canEdit} showArtwork={section !== 'import'} selected={selected}
            onSelect={setSelected} onCommit={transformLogo} onLivePose={setLivePose}
            onSample={hex => { void savePrefs({ primaryColor: hex }); setStatusText(`Primary color sampled: ${hex}. Choose Primary in a color picker to use it.`); }}
            onFill={(x, y) => { if (canEdit && section !== 'import') void run(() => call<Reply>('fill_region', { x, y }), true); }}
            onText={(x, y, hit) => { if (canEdit && section !== 'import') placeText(x, y, hit); }}
            onDelete={() => selected && run(() => call<Reply>('logo_delete', { id: selected }))}
            onZoom={scale => setZoomText(`${Math.round(scale * 100)}%`)} />
          <div className="tool-overlay">
            {hardwoodTool ? (
              <div className="hardwood-bar">
                <select aria-label="Hardwood adjustment target" title="Choose which hardwood the texture adjustments and reset edit" value={editingTwoPoint && d.twoPointEnabled ? 1 : 0} disabled={!canEdit}
                  onChange={e => { void commitHardwood(); setEditingTwoPoint(e.target.value === '1'); }}>
                  <option value={0}>Main hardwood</option><option value={1} disabled={!d.twoPointEnabled}>2-point hardwood</option>
                </select>
                <div className="hardwood-groups">
                  {SLIDERS.map(spec => <HardwoodSlider key={spec.key} spec={spec} value={hardwoodValues[spec.key]} disabled={!canEdit || !hardwoodTarget}
                    onInput={v => hardwoodInput(spec.key, v)} onCommit={v => hardwoodCommit(spec.key, v)} onTyped={v => hardwoodTyped(spec.key, v)} />)}
                </div>
                <button className="reset-button" title="Reset the selected hardwood texture" aria-label="Reset hardwood texture" disabled={!canEdit || !hardwoodTarget} onClick={() => void resetHardwood()}><Icon name="reset" size={17} /></button>
              </div>
            ) : showOptions && pose && selectedLogo ? (
              <OptionsBar key={selectedLogo.id} logo={selectedLogo} pose={pose} onCommit={(x, y, w, h, r) => run(() => call<Reply>('logo_transform', { id: selectedLogo.id, x, y, width: w, height: h, rotation: r }), true)}
                onLock={() => updateLogo(selectedLogo.id, { scaleLocked: !selectedLogo.scaleLocked })} />
            ) : showText ? (
              <div className="text-bar"><span>Text</span><button disabled={!selectedLogo?.isText} onClick={editSelectedText}>Edit selected text</button><span className="muted">Click the court to place text. Click text to edit it.</span></div>
            ) : (
              <div className="context-label">GAME UV</div>
            )}
          </div>
          <div className="canvas-hint">{hint}</div>
        </div>
        <div className="inspector-host">
          <div className="inspector">
            <div className="tabs">
              <button className={`tab ${section === 'paint' ? 'selected' : ''}`} title="Colors & Lines" onClick={() => switchSection('paint')}><Icon name="colors" size={18} />Colors &amp; Lines</button>
              <button className={`tab ${section === 'logos' ? 'selected' : ''}`} title="Logos" onClick={() => switchSection('logos')}><Icon name="logos" size={18} />Logos</button>
            </div>
            {section === 'paint' && (
              <div className="panel-scroll">
                {[['Colors', [...d.paints, d.outside]] as const, ['Lines', lineOrder(d.lines)] as const].map(([name, layers]) => (
                  <details key={name} className="accordion" open={!collapsed.has(name)} onToggle={e => { const open = (e.target as HTMLDetailsElement).open; setCollapsed(c => { const n = new Set(c); if (open) n.delete(name); else n.add(name); return n; }); }}>
                    <summary>{name}</summary>
                    {layers.map(layerRow)}
                  </details>
                ))}
              </div>
            )}
            {section === 'logos' && (
              <div className="panel-scroll">
                <div className="opacity-head"><strong>Opacity</strong><small>{Math.round(opacityDraft ?? selectedLogo?.opacity ?? 100)}%</small></div>
                <input type="range" className="slider" aria-label="Selected logo opacity" min={0} max={100} step={1} disabled={!selectedLogo || !canEdit} value={opacityDraft ?? selectedLogo?.opacity ?? 100}
                  style={{ ['--fill' as string]: `${opacityDraft ?? selectedLogo?.opacity ?? 100}%`, margin: '6px 0 4px' }}
                  onChange={e => { const v = Number(e.target.value); setOpacityDraft(v); if (selectedLogo) void call<Reply>('logo_opacity', { id: selectedLogo.id, opacity: v, commit: false }).then(apply).catch(() => {}); }}
                  onPointerUp={e => { const v = Number((e.target as HTMLInputElement).value); setOpacityDraft(null); if (selectedLogo) void run(() => call<Reply>('logo_opacity', { id: selectedLogo.id, opacity: v, commit: true }), true); }}
                  onKeyUp={e => { const v = Number((e.target as HTMLInputElement).value); setOpacityDraft(null); if (selectedLogo) void run(() => call<Reply>('logo_opacity', { id: selectedLogo.id, opacity: v, commit: true }), true); }} />
                <div className="section-head"><strong>Image Layers</strong><small>{d.logos.length === 1 ? '1 layer' : `${d.logos.length} layers`}</small></div>
                <div className="logo-list" role="listbox">
                  {d.logos.map(logo => (
                    <div key={logo.id} className={`logo-row ${logo.id === selected ? 'selected' : ''}`} onClick={() => setSelected(logo.id)} role="option" aria-selected={logo.id === selected}>
                      <button className={`eye ${logo.visible ? '' : 'off'}`} title="Show / hide layer" aria-label="Layer visibility" onClick={e => { e.stopPropagation(); canvas.current?.cancel(); void updateLogo(logo.id, { visible: !logo.visible }); }}><Icon name="eye" size={18} /></button>
                      <LogoThumb bitmapEntry={logo.revision ? logoBitmaps.get(logo.revision) : undefined} />
                      <LogoName logo={logo} disabled={!canEdit} onFocus={() => setSelected(logo.id)} onCommit={name => updateLogo(logo.id, { name })} onInvalid={() => setStatusText('Enter a logo name of 1–120 characters.')} />
                    </div>
                  ))}
                </div>
                <button className="action add-image" disabled={!canEdit || slotsFull} title="Import and clean up a logo image (four slots maximum)" onClick={() => setModal({ kind: 'importer' })}>+ Add Image</button>
                <div className="logo-actions">
                  <Menu className="logo-action" label={<>Flip<Icon name="chevronDown" size={6} /></>}>{close => <>
                    <Item label="Flip X" title="Flip artwork left/right without moving it" disabled={!selectedLogo} onClick={() => { close(); if (selectedLogo) void updateLogo(selectedLogo.id, { flipX: !selectedLogo.flipX }); }} />
                    <Item label="Flip Y" title="Flip artwork top/bottom without moving it" disabled={!selectedLogo} onClick={() => { close(); if (selectedLogo) void updateLogo(selectedLogo.id, { flipY: !selectedLogo.flipY }); }} />
                  </>}</Menu>
                  <Menu className="logo-action" label={<>Mirror<Icon name="chevronDown" size={6} /></>}>{close => <>
                    <Item label="Mirror X" title="Copy left/right across court center; keep artwork orientation" disabled={!selectedLogo || slotsFull} onClick={() => { close(); if (selectedLogo) void run(() => call<Reply>('logo_duplicate', { id: selectedLogo.id, mirror: 'x' })); }} />
                    <Item label="Mirror Y" title="Copy top/bottom across court center; keep artwork orientation" disabled={!selectedLogo || slotsFull} onClick={() => { close(); if (selectedLogo) void run(() => call<Reply>('logo_duplicate', { id: selectedLogo.id, mirror: 'y' })); }} />
                  </>}</Menu>
                  <button className="logo-action" title="Duplicate selected logo (four slots maximum)" disabled={!selectedLogo || slotsFull || !canEdit} onClick={() => selectedLogo && run(() => call<Reply>('logo_duplicate', { id: selectedLogo.id }))}><Icon name="copy" size={13} />Copy</button>
                  <button className="logo-action" title="Delete selected logo" disabled={!selectedLogo || !canEdit} onClick={() => selectedLogo && run(() => call<Reply>('logo_delete', { id: selectedLogo.id }))}><Icon name="trash" size={13} />Delete</button>
                  <button className="logo-action" style={{ padding: '2px 3px', fontSize: 18 }} title="Move layer up in list (toward back)" aria-label="Move logo layer up" disabled={index <= 0 || !canEdit} onClick={() => selectedLogo && run(() => call<Reply>('logo_reorder', { id: selectedLogo.id, direction: -1 }))}>↑</button>
                  <button className="logo-action" style={{ padding: '2px 3px', fontSize: 18 }} title="Move layer down in list (toward front)" aria-label="Move logo layer down" disabled={index < 0 || index >= d.logos.length - 1 || !canEdit} onClick={() => selectedLogo && run(() => call<Reply>('logo_reorder', { id: selectedLogo.id, direction: 1 }))}>↓</button>
                </div>
              </div>
            )}
            {section === 'import' && (
              <div className="panel-scroll import-panel stack">
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}><strong>Convert Court</strong><button onClick={() => void importOpen()}>Open IFF</button></div>
                <small style={{ fontSize: 12, color: 'var(--text)' }}>{importState ? importState.inspection.path.split(/[\\/]/).pop() : 'No source court selected'}</small>
                {importState && <>
                  <select value={importState.inspection.selected} onChange={e => void importOpen(e.target.value)}>{importState.inspection.textures.map(t => <option key={t.name} value={t.name}>{t.name}</option>)}</select>
                  <strong>Playable court edges</strong>
                  <div className="edge-grid">{['Left', 'Top', 'Right', 'Bottom'].map((label, i) => (
                    <label key={label}>{label}<input value={importState.edges[i]} onChange={e => setImportState(s => s && { ...s, edges: s.edges.map((v, j) => (j === i ? e.target.value : v)) })} onBlur={() => importState && void importPreview(importState.inspection, importState.edges)} /></label>
                  ))}</div>
                  <button onClick={() => { const edges = (importState.inspection.sourceBounds ?? [0, 0, 0, 0]).map(String); setImportState(s => s && { ...s, edges }); void importPreview(importState.inspection, edges); }}>Reset detected edges</button>
                  <button onClick={() => void importPreview(importState.inspection, importState.edges)}>Refresh conversion preview</button>
                  <button className="action" disabled={importState.previewed !== importState.edges.join(',')} onClick={() => void importExport(true)}>Build NBA 2K27 IFF</button>
                  <button disabled={importState.previewed !== importState.edges.join(',')} onClick={() => void importExport(false)}>Export converted PNG</button>
                  <small>{importState.inspection.textures.find(t => t.name === importState.inspection.selected)?.width} × {importState.inspection.textures.find(t => t.name === importState.inspection.selected)?.height} {importState.inspection.textures.find(t => t.name === importState.inspection.selected)?.format} · {importState.inspection.alignmentSource === 'recorded-game-uv' ? 'Recorded game-UV layout' : 'Detected court edges'}. Uses the selected main hardwood as background.</small>
                </>}
              </div>
            )}
          </div>
        </div>
        <div className="hardwood-strip">
          <button className={`court-card ${hardwoodTool && !editingTwoPoint ? 'active' : ''}`} title={`Main hardwood: ${d.floor?.name ?? 'Loading court library...'}\nBrowse and adjust this texture.`} aria-label="Main hardwood: open catalog" disabled={!canEdit}
            onClick={() => { showHardwood(false); setModal({ kind: 'catalog', twoPoint: false }); }}
            onContextMenu={e => { e.preventDefault(); if (canEdit) setCardMenu({ x: e.clientX, y: e.clientY }); }}>
            <Thumb path={d.floor?.previewPath} />
            <span className="text"><div className="label">MAIN HARDWOOD</div><div className="title">{d.floor?.name ?? 'Loading court library...'}</div></span>
            <Icon name="chevronRight" size={14} />
          </button>
          <span />
          {twoPointShown ? (
            <button className={`court-card ${hardwoodTool && editingTwoPoint ? 'active' : ''}`} title={`Left and right two-point areas: ${d.twoPoint?.name ?? 'Choose 2-point hardwood'}\nBrowse and adjust this texture.`} aria-label="Two-point hardwood: open catalog" disabled={!canEdit}
              onClick={() => { showHardwood(true); setModal({ kind: 'catalog', twoPoint: true }); }}>
              <Thumb path={d.twoPoint?.previewPath} />
              <span className="text"><div className="label">2-POINT HARDWOOD</div><div className="title">{d.twoPoint?.name ?? 'Choose 2-point hardwood'}</div></span>
              <Icon name="chevronRight" size={14} />
            </button>
          ) : (
            <label className="check" title="Add a second hardwood for the left and right two-point areas"><input type="checkbox" checked={false} disabled={!canEdit} aria-label="Enable 2-point hardwood"
              onChange={() => run(async () => { const reply = await call<Reply>('set_two_point_enabled', { enabled: true }); showHardwood(true); if (!reply.doc.twoPoint) setModal({ kind: 'catalog', twoPoint: true }); return reply; })} />2-point hardwood</label>
          )}
          {twoPointShown ? <button className="close-small" title="Turn off 2-point hardwood; its texture and adjustments are remembered" aria-label="Disable 2-point hardwood" disabled={!canEdit}
            onClick={() => run(async () => { const reply = await call<Reply>('set_two_point_enabled', { enabled: false }); showHardwood(false); return reply; })}><Icon name="closeSmall" size={14} /></button> : <span />}
        </div>
      </div>
      <div className="status-bar">
        <span className="status-text" title={status}>{status}</span>
        {startup.preparing && <span className="prep"><span className="spinner small" /><span>{startup.message}</span></span>}
        {startup.retry && startup.phase === 'ready' && <span className="prep"><span>{startup.message}</span><button onClick={() => call('retry_startup')}>Retry</button></span>}
        <span>8192 x 4096 • NBA 2K27</span>
      </div>
      {historyBusy && <div className="history-shield" />}
      {modal?.kind === 'catalog' && <FloorCatalog title={modal.twoPoint ? 'Choose two-point hardwood' : 'Hardwood Catalog'} floors={floors} selected={(modal.twoPoint ? d.twoPoint : d.floor)?.id ?? null} favorites={prefs.favorites} recent={d.recent}
        onFavorite={(id, on) => void savePrefs({ favorite: [id, on] })} onUse={floor => void chooseFloor(floor, modal.twoPoint)} onClose={() => setModal(null)}
        onAdd={() => run(async () => { const result = await call<{ id: string; floors: Floor[] } | null>('add_custom_floor'); if (!result) return; setFloors(result.floors); const floor = result.floors.find(f => f.id === result.id); if (floor) return chooseFloor(floor, modal.twoPoint); })} />}
      {modal?.kind === 'picker' && <ColorPicker title={modal.title} color={modal.color} primary={prefs.primaryColor} palettes={data!.palettes} presets={prefs.colorPresets} onPresets={colorPresets => void savePrefs({ colorPresets })} onApply={modal.apply} onClose={() => setModal(null)} />}
      {modal?.kind === 'teams' && <TeamColors palettes={data!.palettes} onPick={modal.apply} onClose={() => setModal(null)} />}
      {cardMenu && <div className="backdrop transparent" onMouseDown={() => setCardMenu(null)} onContextMenu={e => { e.preventDefault(); setCardMenu(null); }}>
        <div className="popup context-menu" style={{ left: cardMenu.x, top: cardMenu.y }} onMouseDown={e => e.stopPropagation()}>
          <button onClick={() => { setCardMenu(null); void editArtwork(false); }}><span>Edit Artwork...</span></button>
        </div>
      </div>}
      {modal?.kind === 'artwork' && <ArtworkEditor session={modal.session} primary={prefs.primaryColor} onClose={() => { setModal(null); setStatusText('Artwork editing cancelled; the court is unchanged.'); }}
        onApplied={reply => { setModal(null); apply(reply); }} />}
      {modal?.kind === 'importer' && <LogoImporter setStatus={setStatus} onClose={() => setModal(null)} onPlaced={reply => { setModal(null); apply(reply); setSection('logos'); setHardwoodTool(false); setTool('Move'); }} />}
      {modal?.kind === 'text' && <TextDialog initial={modal.initial} primary={prefs.primaryColor} palettes={data!.palettes} presets={prefs.colorPresets} onPresets={colorPresets => void savePrefs({ colorPresets })} onClose={() => setModal(null)}
        onApply={settings => { const m = modal; setModal(null); void run(() => call<Reply>('text_apply', { settings, x: m.x, y: m.y, target: m.target })); }} />}
      {modal?.kind === 'message' && <MessageDialog title={modal.title} text={modal.text} actions={modal.actions} onClose={() => setModal(null)} />}
    </div>
  );
}

function TopChrome({ maximized, onClose, menus, actions }: { maximized: boolean; onClose(): void; menus: React.ReactNode; actions: React.ReactNode }) {
  const win = getCurrentWindow();
  return (
    <div className="top-chrome" onMouseDown={e => {
      if ((e.target as HTMLElement).closest('button,input,select,.popup')) return;
      if (e.detail === 2) void win.toggleMaximize(); else if (e.button === 0) void win.startDragging();
    }}>
      <div className="brand"><span className="brand-mark">2K</span><strong>Court Creator</strong></div>
      <div className="menubar">{menus}</div>
      <div className="chrome-actions">
        {actions}
        <button className="window-button" title="Minimize" onClick={() => void win.minimize()}><Icon name="minimize" size={18} /></button>
        <button className="window-button" title={maximized ? 'Restore' : 'Maximize'} aria-label={maximized ? 'Restore' : 'Maximize'} onClick={() => void win.toggleMaximize()}><Icon name={maximized ? 'restore' : 'maximize'} size={18} /></button>
        <button className="window-button close" title="Close" onClick={onClose}><Icon name="close" size={18} /></button>
      </div>
    </div>
  );
}

function HexField({ value, disabled, onCommit, onInvalid }: { value: string; disabled: boolean; onCommit(hex: string): void; onInvalid(): void }) {
  const [text, setText] = useState(value);
  const commit = () => {
    const hex = normalizeHex(text);
    if (!hex) { setText(value); onInvalid(); return; }
    setText(hex);
    if (hex !== value.toUpperCase()) onCommit(hex);
  };
  return <input className="hex-input" title="Hex color" aria-label="Hex color" value={text} disabled={disabled} onChange={e => setText(e.target.value)} onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') commit(); }} />;
}

function LogoName({ logo, disabled, onFocus, onCommit, onInvalid }: { logo: Logo; disabled: boolean; onFocus(): void; onCommit(name: string): void; onInvalid(): void }) {
  const [text, setText] = useState(logo.name);
  useEffect(() => setText(logo.name), [logo.name]);
  const commit = () => {
    const value = text.trim();
    if (value === logo.name) return;
    if (!value || value.length > 120 || /[\u0000-\u001f]/.test(value)) { setText(logo.name); onInvalid(); return; }
    onCommit(value);
  };
  return <input className="logo-name" value={text} disabled={disabled} title="Rename logo (Enter applies, Escape cancels)" onClick={e => e.stopPropagation()} onFocus={e => { onFocus(); e.target.select(); }} onChange={e => setText(e.target.value)} onBlur={commit}
    onKeyDown={e => { if (e.key === 'Enter') { commit(); (e.target as HTMLInputElement).blur(); } else if (e.key === 'Escape') { setText(logo.name); (e.target as HTMLInputElement).blur(); } }} />;
}

function OptionsBar({ logo, pose, onCommit, onLock }: { logo: Logo; pose: { x: number; y: number; width: number; height: number; rotation: number }; onCommit(x: number, y: number, w: number, h: number, r: number): void; onLock(): void }) {
  const format = (v: number) => String(Math.round(v * 100) / 100);
  const field = (label: string, key: 'x' | 'y' | 'width' | 'height' | 'rotation', tip: string) => <NumberField key={key + format(pose[key])} label={label} tip={tip} value={pose[key]} display={format(pose[key])} onCommit={n => {
    let { x, y, width, height, rotation } = logo;
    if (key === 'x') { if (Math.abs(n) > 1_000_000) return; x = n; }
    else if (key === 'y') { if (Math.abs(n) > 1_000_000) return; y = n; }
    else if (key === 'rotation') rotation = n;
    else if (!logo.scaleLocked) { if (key === 'width') width = n; else height = n; }
    else {
      const requested = n / (key === 'width' ? logo.width : logo.height);
      const scale = Math.min(Math.max(requested, Math.max(1 / logo.width, 1 / logo.height)), Math.min(32768 / logo.width, 32768 / logo.height));
      width = key === 'width' && scale === requested ? n : Math.min(Math.max(logo.width * scale, 1), 32768);
      height = key === 'height' && scale === requested ? n : Math.min(Math.max(logo.height * scale, 1), 32768);
    }
    onCommit(x, y, width, height, rotation);
  }} />;
  return (
    <div className="options-bar">
      {field('X', 'x', 'X in texture pixels')}{field('Y', 'y', 'Y in texture pixels')}{field('W', 'width', 'Width in texture pixels')}
      <button className={`lock ${logo.scaleLocked ? 'on' : ''}`} title={logo.scaleLocked ? 'Aspect ratio locked; click to unlock' : 'Aspect ratio unlocked; click to lock'} aria-label="Lock aspect ratio" onClick={onLock}><Icon name={logo.scaleLocked ? 'lock' : 'unlock'} size={16} /></button>
      {field('H', 'height', 'Height in texture pixels')}{field('°', 'rotation', 'Rotation in degrees')}
    </div>
  );
}

function NumberField({ label, tip, display, onCommit }: { label: string; tip: string; value: number; display: string; onCommit(n: number): void }) {
  const [text, setText] = useState(display);
  const commit = () => {
    if (text === display) return;
    const n = Number(text);
    if (!text.trim() || !Number.isFinite(n)) { setText(display); return; }
    onCommit(n);
  };
  return <label title={tip}>{label}<input value={text} aria-label={label} onChange={e => setText(e.target.value)} onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') commit(); else if (e.key === 'Escape') setText(display); }} /></label>;
}
