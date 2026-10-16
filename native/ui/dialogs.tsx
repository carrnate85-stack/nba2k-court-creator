import { convertFileSrc } from '@tauri-apps/api/core';
import { useEffect, useMemo, useRef, useState } from 'react';
import { bitmap, call, errorText, type Floor, type ImporterInfo, type Palette, type Reply } from './api';
import { Icon } from './icons';

export function Dialog({ title, className, onClose, children, footer }: { title: string; className?: string; onClose(): void; children: React.ReactNode; footer?: React.ReactNode }) {
  useEffect(() => {
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } };
    window.addEventListener('keydown', key, true);
    return () => window.removeEventListener('keydown', key, true);
  }, [onClose]);
  return (
    <div className="backdrop" onMouseDown={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={`dialog ${className ?? ''}`} role="dialog" aria-label={title}>
        <div className="dialog-head"><h2>{title}</h2></div>
        <div className="dialog-body">{children}</div>
        {footer && <div className="dialog-foot">{footer}</div>}
      </div>
    </div>
  );
}

export function MessageDialog({ title, text, actions, onClose }: { title: string; text: string; actions: { label: string; primary?: boolean; run(): void }[]; onClose(): void }) {
  return (
    <Dialog title={title} className="message" onClose={onClose} footer={actions.map(a => <button key={a.label} className={a.primary ? 'action' : ''} onClick={a.run}>{a.label}</button>)}>
      <p>{text}</p>
    </Dialog>
  );
}

// --- Color utilities ---

export const normalizeHex = (value: string): string | null => {
  let hex = value.trim().replace(/^#/, '');
  if (/^[0-9a-f]{3}$/i.test(hex)) hex = hex.split('').map(c => c + c).join('');
  return /^[0-9a-f]{6}$/i.test(hex) ? '#' + hex.toUpperCase() : null;
};
const toRgb = (hex: string) => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
const toHex = (r: number, g: number, b: number) => '#' + [r, g, b].map(v => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, '0')).join('').toUpperCase();
function rgbToHsv(r: number, g: number, b: number) {
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = 0;
  if (d) h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return { h: ((h * 60) + 360) % 360, s: max ? d / max : 0, v: max / 255 };
}
function hsvToRgb(h: number, s: number, v: number) {
  const c = v * s, x = c * (1 - Math.abs(((h / 60) % 2) - 1)), m = v - c;
  const [r, g, b] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
  return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
}

const readPresets = (): string[] => { try { return JSON.parse(localStorage.getItem('court-presets') ?? '[]'); } catch { return []; } };
const writePresets = (list: string[]) => { try { localStorage.setItem('court-presets', JSON.stringify(list)); } catch { /* per-viewer convenience only */ } };

/** Shared picker: spectrum/hue, RGB/HSB, hex (selected first), Primary then Presets, one Team Colors action. */
export function ColorPicker({ title, color, primary, palettes, onApply, onClose }: { title: string; color: string; primary: string; palettes: Palette[]; onApply(hex: string): void; onClose(): void }) {
  const [hsv, setHsv] = useState(() => { const [r, g, b] = toRgb(color); return rgbToHsv(r, g, b); });
  const [hexText, setHexText] = useState(color);
  const [presets, setPresets] = useState(readPresets);
  const [teams, setTeams] = useState(false);
  const hexInput = useRef<HTMLInputElement>(null);
  const current = toHex(...(hsvToRgb(hsv.h, hsv.s, hsv.v) as [number, number, number]));
  useEffect(() => { hexInput.current?.focus(); hexInput.current?.select(); }, []);
  useEffect(() => { if (document.activeElement !== hexInput.current) setHexText(current); }, [current]);
  const set = (hex: string) => { const [r, g, b] = toRgb(hex); setHsv(rgbToHsv(r, g, b)); setHexText(hex); };
  const drag = (element: HTMLElement, update: (x: number, y: number) => void) => (event: React.PointerEvent) => {
    element.setPointerCapture(event.pointerId);
    const move = (e: PointerEvent | React.PointerEvent) => { const r = element.getBoundingClientRect(); update(Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)), Math.min(1, Math.max(0, (e.clientY - r.top) / r.height))); };
    move(event);
    const onMove = (e: PointerEvent) => move(e);
    const onUp = () => { element.removeEventListener('pointermove', onMove); element.removeEventListener('pointerup', onUp); };
    element.addEventListener('pointermove', onMove); element.addEventListener('pointerup', onUp);
  };
  const spectrum = useRef<HTMLDivElement>(null), hue = useRef<HTMLDivElement>(null);
  const [r, g, b] = toRgb(current);
  const field = (label: string, value: number, max: number, apply: (n: number) => void) => (
    <><span>{label}</span><input type="number" min={0} max={max} value={Math.round(value)} onChange={e => { const n = Number(e.target.value); if (Number.isFinite(n)) apply(Math.min(max, Math.max(0, n))); }} /></>
  );
  const apply = () => { const hex = normalizeHex(hexText) ?? current; onApply(hex); };
  if (teams) return <TeamColors palettes={palettes} onPick={hex => { setTeams(false); set(hex); }} onClose={() => setTeams(false)} />;
  return (
    <Dialog title={title} className="picker" onClose={onClose} footer={<>
      <button onClick={() => setTeams(true)}>Team Colors</button><span className="grow" />
      <button onClick={onClose}>Cancel</button><button className="action" onClick={apply}>Apply</button></>}>
      <div className="picker-body">
        <div ref={spectrum} className="spectrum-box" style={{ background: `hsl(${hsv.h} 100% 50%)` }} onPointerDown={e => drag(spectrum.current!, (x, y) => setHsv(s => ({ ...s, s: x, v: 1 - y })))(e)}>
          <div className="white" /><div className="black" />
          <div className="ring" style={{ left: `${hsv.s * 100}%`, top: `${(1 - hsv.v) * 100}%` }} />
        </div>
        <div ref={hue} className="hue-bar" onPointerDown={e => drag(hue.current!, (_, y) => setHsv(s => ({ ...s, h: Math.min(359.9, y * 360) })))(e)}>
          <div className="mark" style={{ top: `${(hsv.h / 360) * 100}%` }} />
        </div>
        <div>
          <div className="compare"><span style={{ background: current }} title="New" /><span style={{ background: color }} title="Current" onClick={() => set(color)} /></div>
          <div className="picker-fields">
            <span>Hex</span><input ref={hexInput} value={hexText} onChange={e => { setHexText(e.target.value); const hex = normalizeHex(e.target.value); if (hex) { const [rr, gg, bb] = toRgb(hex); setHsv(rgbToHsv(rr, gg, bb)); } }} onKeyDown={e => { if (e.key === 'Enter') apply(); }} />
            {field('R', r, 255, n => set(toHex(n, g, b)))}{field('G', g, 255, n => set(toHex(r, n, b)))}{field('B', b, 255, n => set(toHex(r, g, n)))}
            {field('H', hsv.h, 359, n => setHsv(s => ({ ...s, h: n })))}{field('S', hsv.s * 100, 100, n => setHsv(s => ({ ...s, s: n / 100 })))}{field('B (value)', hsv.v * 100, 100, n => setHsv(s => ({ ...s, v: n / 100 })))}
          </div>
        </div>
      </div>
      <div className="swatch-section">
        <div className="row"><span className="label">Primary</span><button className="chip" style={{ background: primary }} title={`Primary ${primary}`} onClick={() => set(primary)} /></div>
        <div className="row"><span className="label">Preset Colors</span>
          {presets.map(p => <button key={p} className="chip" style={{ background: p }} title={`${p} — right-click to remove preset`} onClick={() => set(p)} onContextMenu={e => { e.preventDefault(); const next = presets.filter(x => x !== p); setPresets(next); writePresets(next); }} />)}
          <button className="chip" title="Save the current color as a preset" disabled={presets.length >= 32} onClick={() => { const next = [...presets.filter(x => x !== current), current]; setPresets(next); writePresets(next); }}>+</button>
        </div>
      </div>
    </Dialog>
  );
}

const categoryOrder = (c: string) => ({ NBA: 0, WNBA: 1, EuroLeague: 2, 'NCAA D1': 3, D1: 3 } as Record<string, number>)[c] ?? 4;
const categoryLabel = (c: string) => (c === 'NCAA D1' ? 'D1' : c);

/** Team palettes: collapsed league headings, then teams; search reveals matches across groups. */
export function TeamColors({ palettes, onPick, onClose }: { palettes: Palette[]; onPick(hex: string): void; onClose(): void }) {
  const [search, setSearch] = useState(''), [query, setQuery] = useState(''), [league, setLeague] = useState('All Categories');
  const [expanded, setExpanded] = useState<Set<string>>(new Set()), [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  useEffect(() => { const t = setTimeout(() => { setQuery(search); setCollapsed(new Set()); }, 90); return () => clearTimeout(t); }, [search]);
  const teams = useMemo(() => palettes.map((p, i) => {
    const seen = new Set<string>();
    const colors = p.colors.map(c => ({ hex: normalizeHex(c.hex), name: c.name ?? 'Color' })).filter((c): c is { hex: string; name: string } => !!c.hex && !seen.has(c.hex) && !!seen.add(c.hex));
    return { key: 'team:' + i, league: p.league ?? 'College', name: p.team ?? 'Team', evidence: p.paletteNote ?? 'Existing team palette', colors };
  }).filter(t => t.colors.length), [palettes]);
  const leagues = useMemo(() => ['All Categories', ...[...new Set(teams.map(t => t.league))].sort((a, b) => categoryOrder(a) - categoryOrder(b) || a.localeCompare(b)).map(categoryLabel)], [teams]);
  const words = query.split(' ').filter(Boolean).map(w => w.toLowerCase());
  const toggle = (key: string) => {
    const searching = words.length > 0;
    const set = new Set(searching ? collapsed : expanded);
    if (!set.delete(key)) set.add(key);
    (searching ? setCollapsed : setExpanded)(set);
  };
  const isOpen = (key: string) => (words.length ? !collapsed.has(key) : expanded.has(key));
  const groups = [...new Set(teams.map(t => t.league))].sort((a, b) => categoryOrder(a) - categoryOrder(b) || a.localeCompare(b)).filter(l => league === 'All Categories' || categoryLabel(l) === league);
  let matches = 0;
  const rows: React.ReactNode[] = [];
  for (const group of groups) {
    const matching = teams.filter(t => t.league === group).map(t => ({ team: t, colors: t.colors.filter(c => words.every(w => `${t.name} ${t.league} ${categoryLabel(t.league)} ${c.name} ${c.hex}`.toLowerCase().includes(w))) })).filter(t => t.colors.length);
    if (!matching.length) continue;
    matches += matching.length;
    const key = 'league:' + group;
    rows.push(<button key={key} className={`team-heading category ${isOpen(key) ? 'open' : ''}`} onClick={() => toggle(key)}><Icon name="chevronRight" className="caret" size={12} /><span>{categoryLabel(group)}</span><small>{matching.length} teams</small></button>);
    if (!isOpen(key)) continue;
    for (const { team, colors } of matching.sort((a, b) => a.team.name.localeCompare(b.team.name))) {
      rows.push(<button key={team.key} className={`team-heading ${isOpen(team.key) ? 'open' : ''}`} style={{ paddingLeft: 24 }} onClick={() => toggle(team.key)}><Icon name="chevronRight" className="caret" size={12} /><span>{team.name}</span><small>{colors.length} colors</small></button>);
      if (isOpen(team.key)) rows.push(<div key={team.key + ':s'} className="team-swatches">{colors.map(c => <button key={c.hex} className="team-color" title={`${c.name} · ${c.hex}\n${team.evidence}`} onClick={() => onPick(c.hex)}><span style={{ background: c.hex }} />{c.hex}</button>)}<small style={{ width: '100%' }}>{team.evidence}</small></div>);
    }
  }
  return (
    <Dialog title="Team Colors" className="teams" onClose={onClose} footer={<small style={{ flex: 1 }}>{rows.length === 0 ? 'No matching team colors.' : words.length ? `${matches} matching teams. Clear search to restore your expanded categories.` : 'Expand a category, then a team. Click a swatch to apply it.'}</small>}>
      <div className="catalog-filters" style={{ gridTemplateColumns: 'minmax(0,1fr) 150px' }}>
        <input autoFocus placeholder="Search teams, colors or hex values" value={search} onChange={e => setSearch(e.target.value)} />
        <select value={league} onChange={e => setLeague(e.target.value)}>{leagues.map(l => <option key={l}>{l}</option>)}</select>
      </div>
      <div className="team-list">{rows}</div>
    </Dialog>
  );
}

/** Hardwood catalog with search, categories, favorites/recent, sort and lazy thumbnails. */
export function FloorCatalog({ title, floors, selected, favorites, recent, onFavorite, onUse, onAdd, onClose }: {
  title: string; floors: Floor[]; selected: string | null; favorites: string[]; recent: string[];
  onFavorite(id: string, on: boolean): void; onUse(floor: Floor): void; onAdd(): void; onClose(): void;
}) {
  const [search, setSearch] = useState(''), [query, setQuery] = useState(''), [category, setCategory] = useState('All'), [sort, setSort] = useState('A - Z');
  const [current, setCurrent] = useState(selected);
  useEffect(() => { const t = setTimeout(() => setQuery(search), 150); return () => clearTimeout(t); }, [search]);
  const categories = useMemo(() => ['All', 'Favorites', 'Recent', ...[...new Set(floors.map(f => f.category))].sort((a, b) => (a === 'Unknown' ? 'zzzz' : a).localeCompare(b === 'Unknown' ? 'zzzz' : b))], [floors]);
  const words = query.split(' ').filter(Boolean).map(w => w.toLowerCase());
  const rows = floors
    .filter(f => words.every(w => `${f.name} ${f.category} ${f.id}`.toLowerCase().includes(w)))
    .filter(f => category === 'All' || f.category === category || (category === 'Favorites' && favorites.includes(f.id)) || (category === 'Recent' && recent.includes(f.id)))
    .sort((a, b) => (sort === 'Z - A' ? -1 : 1) * a.name.localeCompare(b.name));
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => { list.current?.querySelector('.selected')?.scrollIntoView({ block: 'center' }); }, []);
  const chosen = rows.find(r => r.id === current) ?? null;
  return (
    <Dialog title={title} className="catalog" onClose={onClose} footer={<><small className="grow">{rows.length} {rows.length === 1 ? 'court' : 'courts'}</small><button className="action" disabled={!chosen} onClick={() => chosen && onUse(chosen)}>Use Selected Court</button></>}>
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: -40, marginBottom: 12 }}><button onClick={onAdd}>Add Custom Floor</button></div>
      <div className="catalog-filters">
        <input autoFocus placeholder="Search hardwood" aria-label="Search hardwood" value={search} onChange={e => setSearch(e.target.value)} />
        <select value={category} onChange={e => setCategory(e.target.value)} aria-label="Court category">{categories.map(c => <option key={c}>{c}</option>)}</select>
        <select value={sort} onChange={e => setSort(e.target.value)} aria-label="Court sort order"><option>A - Z</option><option>Z - A</option></select>
      </div>
      {rows.length === 0 ? <div className="empty">No courts found</div> : (
        <div ref={list} className="catalog-list" onKeyDown={e => { if (e.key === 'Enter' && chosen) onUse(chosen); }} tabIndex={0}>
          {rows.map(floor => (
            <div key={floor.id} className={`catalog-row ${floor.id === current ? 'selected' : ''}`} title={floor.name} onClick={() => setCurrent(floor.id)} onDoubleClick={() => onUse(floor)}>
              <div className="catalog-thumb"><img loading="lazy" alt="" src={convertFileSrc(floor.previewPath)} onError={e => { (e.target as HTMLImageElement).style.display = 'none'; (e.target as HTMLImageElement).parentElement!.title = 'Preview unavailable'; }} /></div>
              <div className="text"><div className="name">{floor.name}</div><div className="category">{floor.category}</div></div>
              <button className={`star ${favorites.includes(floor.id) ? 'on' : ''}`} title="Toggle favorite" aria-label="Toggle favorite" onClick={e => { e.stopPropagation(); onFavorite(floor.id, !favorites.includes(floor.id)); }}>{favorites.includes(floor.id) ? '★' : '☆'}</button>
            </div>
          ))}
        </div>
      )}
    </Dialog>
  );
}

/** Local logo cleanup before placement: sample/wand modes, tolerance, edge background removal, undo/reset. */
export function LogoImporter({ onPlaced, onClose, setStatus }: { onPlaced(reply: Reply): void; onClose(): void; setStatus(text: string): void }) {
  const [info, setInfo] = useState<ImporterInfo | null>(null);
  const [wand, setWand] = useState(false), [tolerance, setTolerance] = useState(20), [all, setAll] = useState(false);
  const [message, setMessage] = useState('Existing transparency is preserved.'), [busy, setBusy] = useState(false);
  const canvas = useRef<HTMLCanvasElement>(null);
  const refresh = async (next: ImporterInfo) => {
    setInfo(next);
    const { data } = await bitmap('importer_pixels', { session: next.session });
    const c = canvas.current!; c.width = data.width; c.height = data.height; c.getContext('2d')!.putImageData(data, 0, 0);
  };
  const choose = async () => {
    try { setBusy(true); const next = await call<ImporterInfo | null>('importer_open'); if (next) { setWand(false); setAll(false); setMessage('Existing transparency is preserved.'); await refresh(next); } }
    catch (e) { setMessage(errorText(e)); } finally { setBusy(false); }
  };
  useEffect(() => { void choose(); }, []);
  const act = async (action: string, extra: Record<string, unknown> = {}, text?: (n: ImporterInfo) => string) => {
    if (!info) return;
    try { setBusy(true); const next = await call<ImporterInfo>('importer_action', { session: info.session, action, tolerance, all, ...extra }); await refresh(next); if (text) setMessage(text(next)); }
    catch (e) { setMessage(errorText(e)); } finally { setBusy(false); }
  };
  const click = (event: React.MouseEvent<HTMLCanvasElement>) => {
    if (!info || busy) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const x = Math.floor(((event.clientX - rect.left) / rect.width) * info.width), y = Math.floor(((event.clientY - rect.top) / rect.height) * info.height);
    if (wand) void act('wand', { x, y }, n => `Removed ${n.removed.toLocaleString()} pixels.`);
    else void act('sample', { x, y });
  };
  const close = () => { if (info) void call('importer_close', { session: info.session }); onClose(); };
  const place = async () => {
    if (!info) return;
    try { setBusy(true); const reply = await call<Reply>('importer_place', { session: info.session }); onPlaced(reply); if (reply.status) setStatus(reply.status); }
    catch (e) { setMessage(errorText(e)); setBusy(false); }
  };
  return (
    <Dialog title="Import Logo" className="importer" onClose={close} footer={<><small className="grow">Move, resize and rotate after placement.</small><button onClick={close}>Cancel</button><button className="action" disabled={!info || busy} onClick={place}>Place on Court</button></>}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: -6, marginBottom: 12 }}>
        <small>{info ? `${info.name}  /  ${info.width} x ${info.height}` : 'Choose an image to preview and clean before placement.'}</small>
        <button disabled={busy} onClick={choose}>Choose Image...</button>
      </div>
      <div className="importer-body">
        <div className="importer-preview">{!info && <small style={{ position: 'absolute' }}>PNG, JPEG, BMP, GIF, TIFF, WebP or DDS</small>}<canvas ref={canvas} onClick={click} style={{ display: info ? 'block' : 'none' }} /></div>
        <div className="importer-side stack" style={{ opacity: info ? 1 : 0.45, pointerEvents: info && !busy ? 'auto' : 'none' }}>
          <h3>Local logo cleanup</h3>
          <small>Remove solid colors. Original file stays unchanged.</small>
          <div className="mode-buttons">
            <button className={!wand ? 'selected' : ''} title="Eyedropper: click to sample a background color" onClick={() => setWand(false)}><Icon name="Eyedropper" size={20} /></button>
            <button className={wand ? 'selected' : ''} title="Magic Wand: click to remove a connected color area, including enclosed holes" onClick={() => setWand(true)}><Icon name="MagicWand" size={20} /></button>
            <small style={{ marginLeft: 6 }}>{wand ? 'Remove color' : 'Sample color'}</small>
          </div>
          <small>{wand ? 'Click a region to remove it. Click enclosed holes separately.' : 'Click the preview to sample a color.'}</small>
          <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11 }}><span>Tolerance</span><span>{tolerance}</span></div>
          <input type="range" className="slider" min={0} max={255} value={tolerance} style={{ ['--fill' as string]: `${(tolerance / 255) * 100}%` }} onChange={e => setTolerance(Number(e.target.value))} />
          <small>0: exact color · 255: all opaque colors</small>
          <label className="check" style={{ margin: 0 }} title="Explicitly remove the clicked color everywhere, including separate logo elements"><input type="checkbox" checked={all} onChange={e => setAll(e.target.checked)} />Remove all matching colors</label>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}><span style={{ width: 22, height: 22, borderRadius: 3, border: '1px solid var(--strong-border)', background: info?.background }} /><small>{info?.background}</small></div>
          <button title="Remove edge-connected color. Use the wand for enclosed holes." onClick={() => act('background', { color: info?.background }, n => `Removed ${n.removed.toLocaleString()} edge pixels.`)}>Remove edge background</button>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 4 }}>
            <button disabled={!info?.canUndo} onClick={() => act('undo', {}, () => 'Cleanup undone.')}>Undo</button>
            <button disabled={!info?.hasChanges} onClick={() => act('reset', {}, () => 'Original pixels restored. Reset can also be undone.')}>Reset</button>
          </div>
          <small>{message}</small>
        </div>
      </div>
    </Dialog>
  );
}

type TextValues = { Text: string; FontFamily: string; FontSize: number; FillColor: string; OutlineColor: string; OutlineWidth: number; Bold: boolean; Italic: boolean; Alignment: number; LineHeight: number; LetterSpacing: number };

/** Text settings (font, size, fill, outline, bold/italic, alignment, line height, letter spacing). */
export function TextDialog({ initial, primary, palettes, onApply, onClose }: { initial: Record<string, unknown>; primary: string; palettes: Palette[]; onApply(settings: TextValues): void; onClose(): void }) {
  const pick = <T,>(pascal: string, fallback: T): T => (initial[pascal] ?? initial[pascal[0].toLowerCase() + pascal.slice(1)] ?? fallback) as T;
  const [values, setValues] = useState<TextValues>({
    Text: pick('Text', 'TEXT'), FontFamily: pick('FontFamily', 'Arial'), FontSize: pick('FontSize', 180), FillColor: pick('FillColor', primary), OutlineColor: pick('OutlineColor', '#000000'),
    OutlineWidth: pick('OutlineWidth', 0), Bold: pick('Bold', true), Italic: pick('Italic', false), Alignment: pick('Alignment', 0), LineHeight: pick('LineHeight', 0), LetterSpacing: pick('LetterSpacing', 0),
  });
  const [fonts, setFonts] = useState<string[]>([]), [error, setError] = useState(''), [picker, setPicker] = useState<null | 'FillColor' | 'OutlineColor'>(null);
  const preview = useRef<HTMLCanvasElement>(null);
  useEffect(() => { call<string[]>('text_fonts').then(setFonts).catch(() => setFonts(['Arial'])); }, []);
  useEffect(() => {
    const timer = setTimeout(async () => {
      try {
        const { data } = await bitmap('text_preview', { settings: values });
        const c = preview.current!; c.width = data.width; c.height = data.height; c.getContext('2d')!.putImageData(data, 0, 0); setError('');
      } catch (e) { setError(errorText(e)); }
    }, 120);
    return () => clearTimeout(timer);
  }, [values]);
  const set = <K extends keyof TextValues>(key: K, value: TextValues[K]) => setValues(v => ({ ...v, [key]: value }));
  const number = (key: keyof TextValues, min: number, max: number, step = 1) => <input type="number" min={min} max={max} step={step} value={values[key] as number} onChange={e => set(key, Number(e.target.value) as never)} />;
  const valid = values.Text.trim().length > 0 && values.FontSize >= 6 && values.FontSize <= 600 && values.OutlineWidth >= 0 && values.OutlineWidth <= 40 && values.LineHeight >= 0 && values.LineHeight <= 3000 && values.LetterSpacing >= 0 && values.LetterSpacing <= 200;
  if (picker) return <ColorPicker title={picker === 'FillColor' ? 'Text fill' : 'Text outline'} color={normalizeHex(values[picker]) ?? '#FFFFFF'} primary={primary} palettes={palettes} onClose={() => setPicker(null)} onApply={hex => { set(picker, hex); setPicker(null); }} />;
  return (
    <Dialog title="Text" className="text-dialog" onClose={onClose} footer={<><small className="grow" style={{ color: 'var(--warning-text)' }}>{error}</small><button onClick={onClose}>Cancel</button><button className="action" disabled={!valid} onClick={() => onApply(values)}>Apply</button></>}>
      <div className="text-grid">
        <textarea autoFocus value={values.Text} onChange={e => set('Text', e.target.value)} aria-label="Text" />
        <span>Font</span><select value={values.FontFamily} onChange={e => set('FontFamily', e.target.value)}>{(fonts.includes(values.FontFamily) ? fonts : [values.FontFamily, ...fonts]).map(f => <option key={f}>{f}</option>)}</select>
        <span>Size</span>{number('FontSize', 6, 600)}
        <span>Fill</span><button className="swatch-button" style={{ width: 60 }} onClick={() => setPicker('FillColor')}><span style={{ background: values.FillColor, width: 48 }} /></button>
        <span>Outline</span><div style={{ display: 'flex', gap: 6 }}><button className="swatch-button" style={{ width: 60 }} onClick={() => setPicker('OutlineColor')}><span style={{ background: values.OutlineColor, width: 48 }} /></button>{number('OutlineWidth', 0, 40, 0.5)}</div>
        <span>Style</span><div style={{ display: 'flex', gap: 10 }}><label className="check" style={{ margin: 0 }}><input type="checkbox" checked={values.Bold} onChange={e => set('Bold', e.target.checked)} />Bold</label><label className="check" style={{ margin: 0 }}><input type="checkbox" checked={values.Italic} onChange={e => set('Italic', e.target.checked)} />Italic</label></div>
        <span>Alignment</span><select value={values.Alignment} onChange={e => set('Alignment', Number(e.target.value))}><option value={0}>Left</option><option value={1}>Center</option><option value={2}>Right</option></select>
        <span>Line height</span>{number('LineHeight', 0, 3000)}
        <span>Letter spacing</span>{number('LetterSpacing', 0, 200, 0.5)}
      </div>
      <div className="text-preview"><canvas ref={preview} /></div>
    </Dialog>
  );
}
