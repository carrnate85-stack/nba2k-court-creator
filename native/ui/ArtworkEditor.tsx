import { invoke } from '@tauri-apps/api/core';
import { useEffect, useRef, useState } from 'react';
import { CanvasViewport } from '@canvas/CanvasViewport';
import { LayerThumbnail } from '@canvas/LayerThumbnail';
import { HexColorInput } from '@canvas/HexColorInput';
import { TextCard, TextLayerDialog, TextOverlay, inlineDefaults, textLayerAt, useTextDraft, withTextDraft, type TextSettings } from '@canvas/TextEditor';
import { frameOf, framePoint } from '@canvas/transform-frame';
import type { DocumentInfo, Layer, Operation, Point, SelectionMode, Tab } from '@canvas/types';
import '@canvas/editor.css?scoped';
import { errorText, type Reply } from './api';
import { Icon, type IconName } from './icons';

export type ArtworkSession = { tab: Tab; name: string; target: 'logo' | 'floor'; dark: boolean };

/** The compact host configuration's tools (`StudioArtworkEditorWindow`), with Canvas shortcuts. */
const TOOLS: [IconName, string, string][] = [
  ['Move', 'Move', 'V'], ['RectSelect', 'Rectangular Marquee', 'M'], ['Lasso', 'Lasso', 'L'], ['PolygonLasso', 'Polygonal Lasso', 'Shift+L'],
  ['MagicWand', 'Magic Wand', 'W'], ['ColorRange', 'Color Range', ''], ['Brush', 'Brush', 'B'], ['Eraser', 'Eraser', 'E'],
  ['Bucket', 'Paint Bucket', 'G'], ['Eyedropper', 'Eyedropper', 'I'], ['Type', 'Type', 'T'], ['Rectangle', 'Rectangle', 'U'],
  ['Hand', 'Hand', 'H'], ['Zoom', 'Zoom', 'Z'],
];
const KEYS: Record<string, string> = { v: 'Move', m: 'RectSelect', l: 'Lasso', L: 'PolygonLasso', w: 'MagicWand', b: 'Brush', e: 'Eraser', g: 'Bucket', i: 'Eyedropper', t: 'Type', u: 'Rectangle', h: 'Hand', z: 'Zoom' };
/** Court color adjustment presets: selective recolor of white toward court green, markings and graphics. */
const PRESETS: [string, string][] = [['Court green', '#19583F'], ['White court markings', '#FFFFFF'], ['Black court graphics', '#000000']];
const rgb = (hex: string) => ({ r: parseInt(hex.slice(1, 3), 16), g: parseInt(hex.slice(3, 5), 16), b: parseInt(hex.slice(5, 7), 16) });
const rgba = (hex: string, alpha: number): [number, number, number, number] => { const c = rgb(hex); return [c.r, c.g, c.b, alpha]; };
const MODES: SelectionMode[] = ['replace', 'add', 'subtract', 'intersect'];

/** Edit Artwork: the shared Canvas editing surface for one court asset, with Apply/Cancel. */
export function ArtworkEditor({ session, primary, onApplied, onClose }: { session: ArtworkSession; primary: string; onApplied(reply: Reply): void; onClose(): void }) {
  const [tab, setTab] = useState<Tab>(session.tab);
  const [tool, setTool] = useState('Brush'), [color, setColor] = useState(primary.toUpperCase()), [alpha, setAlpha] = useState(255);
  const [size, setSize] = useState(24), [opacity, setOpacity] = useState(100), [hardness, setHardness] = useState(100);
  const [tolerance, setTolerance] = useState(24), [contiguous, setContiguous] = useState(true), [mode, setMode] = useState<SelectionMode>('replace');
  const [zoom, setZoom] = useState<number | null>(null), [panKey, setPanKey] = useState(false), [gesture, setGesture] = useState(false);
  const [busy, setBusy] = useState(false), [applying, setApplying] = useState(false), [status, setStatus] = useState('Edits stay private until Apply.');
  const [textDialog, setTextDialog] = useState<TextSettings>();
  const doc = tab.document, id = tab.id;
  const text = useTextDraft((_, document) => setTab(t => ({ ...t, document })), setStatus);
  const closing = useRef(false);
  const update = (document: DocumentInfo) => setTab(t => ({ ...t, document }));

  const edit = async (operation: Operation) => {
    try { update(await invoke<DocumentInfo>('edit', { id, operation })); }
    catch (e) { setStatus(errorText(e)); throw e; }
  };
  const action = (job: () => Promise<unknown>) => { if (busy || applying) return; setBusy(true); void job().catch(e => setStatus(errorText(e))).finally(() => setBusy(false)); };
  const cancel = () => {
    if (closing.current) return;
    closing.current = true;
    if (text.draft) text.cancel();
    void invoke('artwork_close').catch(() => {}).finally(onClose);
  };
  const apply = async () => {
    if (applying || text.draft) { if (text.draft) setStatus('Apply or cancel the text draft first.'); return; }
    setApplying(true); setStatus('Preparing the edited artwork...');
    try { onApplied(await invoke<Reply>('artwork_apply', { id })); }
    catch (e) { if (!closing.current) { setStatus(errorText(e)); setApplying(false); } }
  };
  const beginText = (point: Point) => {
    if (busy || text.draft) return;
    const target = textLayerAt(doc, point);
    if (target?.locked) { setStatus('Unlock this text layer to edit it'); return; }
    action(async () => { if (target && doc.selected !== target.id) await edit({ kind: 'select', layer: target.id }); await text.begin(id, target?.id ?? null, point, inlineDefaults(color)); });
  };
  const shape = (_kind: string, start: Point, end: Point) => action(() => edit({ kind: 'rectangle', x1: start.x, y1: start.y, x2: end.x, y2: end.y, fill: rgba(color, alpha), stroke: null, width: 0, opacity: opacity / 100 } as unknown as Operation));
  const preset = (target: string) => action(() => edit({ kind: 'addAdjustment', key: 'colorAdjustment', value: { kind: 2, recolor: { source: rgb('#FFFFFF'), target: rgb(target), tolerance: 32, feather: 16, preserveShading: true, strength: 100 } } } as unknown as Operation));

  useEffect(() => {
    const down = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLSelectElement;
      if (e.key === 'Escape') { e.preventDefault(); e.stopImmediatePropagation(); if (textDialog) setTextDialog(undefined); else if (text.draft) text.cancel(); else cancel(); return; }
      if (text.draft || typing || applying) return;
      if (e.key === ' ') { setPanKey(true); e.preventDefault(); return; }
      const ctrl = e.ctrlKey || e.metaKey, k = e.key.toLowerCase();
      if (ctrl && (k === 'z' || k === 'y')) { e.preventDefault(); action(() => edit({ kind: k === 'y' || e.shiftKey ? 'redo' : 'undo' })); return; }
      if (ctrl && k === 'a') { e.preventDefault(); action(() => edit({ kind: 'selectAll' })); return; }
      if (ctrl && k === 'd') { e.preventDefault(); action(() => edit({ kind: 'deselect' })); return; }
      if (ctrl && e.shiftKey && k === 'i') { e.preventDefault(); action(() => edit({ kind: 'invertSelection' })); return; }
      if (e.key === 'Delete' && doc.selection) { e.preventDefault(); action(() => edit({ kind: 'fillSelection', color: [0, 0, 0, 0], opacity: 1, clear: true } as unknown as Operation)); return; }
      if (ctrl || e.altKey) return;
      if (e.key === '[' || e.key === ']') { setSize(s => Math.max(1, Math.min(500, Math.round(s * (e.key === ']' ? 1.2 : 1 / 1.2))))); return; }
      const next = KEYS[e.shiftKey && k === 'l' ? 'L' : k];
      if (next) setTool(next);
    };
    const up = (e: KeyboardEvent) => { if (e.key === ' ') setPanKey(false); };
    window.addEventListener('keydown', down, true); window.addEventListener('keyup', up, true);
    return () => { window.removeEventListener('keydown', down, true); window.removeEventListener('keyup', up, true); };
  });

  const selected = doc.layers.find(l => l.id === doc.selected);
  const painting = ['Brush', 'Eraser'].includes(tool), selecting = ['RectSelect', 'Lasso', 'PolygonLasso', 'MagicWand', 'ColorRange'].includes(tool);
  const layers = [...doc.layers].reverse();
  const shownTab = text.draft?.id === id ? { ...tab, document: withTextDraft(doc, text.draft.info) } : tab;
  const locked = busy || applying;
  return (
    <div className="backdrop artwork-backdrop">
      <div className={`artwork-editor ${session.dark ? 'dark' : ''}`} role="dialog" aria-label={`Edit Artwork - ${session.name}`}>
        <div className="artwork-head"><strong>Edit Artwork - {session.name}</strong><span className="muted">{doc.width} × {doc.height}{doc.alphaIsData ? ' · alpha is game data' : ''}</span></div>
        <div className="artwork-options" aria-label="Tool options">
          <span className="artwork-tool-name">{TOOLS.find(t => t[0] === tool)?.[1]}</span>
          {(painting || tool === 'Rectangle' || tool === 'Bucket') && <label>Opacity <input type="number" min={1} max={100} value={opacity} onChange={e => setOpacity(Math.max(1, Math.min(100, Number(e.target.value) || 1)))} />%</label>}
          {painting && <><label>Size <input type="number" min={1} max={500} value={size} onChange={e => setSize(Math.max(1, Math.min(500, Number(e.target.value) || 1)))} />px</label>
            <label>Hardness <input type="number" min={0} max={100} value={hardness} onChange={e => setHardness(Math.max(0, Math.min(100, Number(e.target.value) || 0)))} />%</label></>}
          {(tool === 'MagicWand' || tool === 'ColorRange' || tool === 'Bucket') && <label>Tolerance <input type="number" min={0} max={255} value={tolerance} onChange={e => setTolerance(Math.max(0, Math.min(255, Number(e.target.value) || 0)))} /></label>}
          {(tool === 'MagicWand' || tool === 'Bucket') && <label className="check"><input type="checkbox" checked={contiguous} onChange={e => setContiguous(e.target.checked)} /> Contiguous</label>}
          {selecting && <span className="selection-modes" role="group" aria-label="Selection mode">{MODES.map(m => <button key={m} className={mode === m ? 'active' : ''} aria-pressed={mode === m} onClick={() => setMode(m)}>{m[0].toUpperCase() + m.slice(1)}</button>)}</span>}
          <span className="spacer" />
          <button title="Fit (Ctrl+0)" onClick={() => setZoom(null)}>Fit</button>
          <button title="Undo (Ctrl+Z)" aria-label="Undo artwork edit" disabled={!doc.canUndo || locked} onClick={() => action(() => edit({ kind: 'undo' }))}><Icon name="undo" size={15} /></button>
          <button title="Redo (Ctrl+Y)" aria-label="Redo artwork edit" disabled={!doc.canRedo || locked} onClick={() => action(() => edit({ kind: 'redo' }))}><Icon name="redo" size={15} /></button>
        </div>
        <div className="artwork-body">
          <nav className="artwork-tools" aria-label="Artwork tools">
            {TOOLS.map(([name, label, key]) => <button key={name} className={tool === name ? 'active' : ''} aria-pressed={tool === name} title={key ? `${label} • ${key}` : label} aria-label={label} onClick={() => setTool(name)}><Icon name={name} size={20} /></button>)}
          </nav>
          <div className="artwork-canvas">
            <CanvasViewport tab={shownTab} tool={tool} size={size} opacity={opacity} hardness={hardness} flow={100} spacing={18} smoothing={0} editingMask={false} paintTarget={5}
              colorAlpha={alpha} viewChannel={0} color={color} zoom={zoom} busy={locked || !!text.draft} edit={edit} pick={(hex, a) => { setColor(hex.toUpperCase()); if (a !== undefined) setAlpha(a); setStatus(`Color ${hex.toUpperCase()} sampled`); }}
              status={setStatus} panKey={panKey} onGestureChange={setGesture} selectionMode={mode} tolerance={tolerance} contiguous={contiguous} onZoom={setZoom} onShape={shape}
              onTextClick={beginText} textOverlay={text.draft?.id === id ? <TextOverlay state={text} width={doc.width} height={doc.height} /> : undefined}
              transform={selected ? { reference: framePoint(frameOf(selected), .5, .5), moveReference: () => {} } : undefined} />
          </div>
          <aside className="artwork-inspector">
            {text.draft?.id === id ? <TextCard state={text} advanced={() => setTextDialog({ ...(text.requested.current ?? text.draft!.info.settings), text: text.text })} /> : <>
              <section><h3>Color</h3>
                <div className="artwork-color"><input type="color" aria-label="Paint color" value={color} onChange={e => setColor(e.target.value.toUpperCase())} /><HexColorInput color={color} alpha={alpha} onChange={(hex, a) => { setColor(hex.toUpperCase()); setAlpha(a); }} />
                  <button title="Use the court's primary color" onClick={() => { setColor(primary.toUpperCase()); setAlpha(255); }}>Primary</button></div>
              </section>
              <section><h3>Court presets</h3>
                <div className="artwork-presets">{PRESETS.map(([label, hex]) => <button key={label} title={`Add a selective recolor layer: white → ${hex}`} disabled={locked} onClick={() => preset(hex)}><span className="chip" style={{ background: hex }} />{label}</button>)}</div>
              </section>
              <section className="artwork-layers"><h3>Layers</h3>
                <div className="artwork-layer-actions">
                  <button title="New layer" disabled={locked} onClick={() => action(() => edit({ kind: 'addLayer' }))}>New</button>
                  <button title="Duplicate layer" disabled={locked || !selected} onClick={() => action(() => edit({ kind: 'duplicateLayer' }))}>Copy</button>
                  <button title="Delete layer" disabled={locked || !selected || selected.isBaseLayer} onClick={() => action(() => edit({ kind: 'deleteLayer' }))}>Delete</button>
                  <button title="Move layer up" disabled={locked || !selected} onClick={() => action(() => edit({ kind: 'reorder', direction: 1 }))}>↑</button>
                  <button title="Move layer down" disabled={locked || !selected} onClick={() => action(() => edit({ kind: 'reorder', direction: -1 }))}>↓</button>
                </div>
                <ul>{layers.map((layer: Layer) => <li key={layer.id} className={layer.id === doc.selected ? 'selected' : ''} onClick={() => layer.id !== doc.selected && action(() => edit({ kind: 'select', layer: layer.id }))}>
                  <input type="checkbox" aria-label={`Show ${layer.name}`} checked={layer.visible} disabled={locked} onClick={e => e.stopPropagation()} onChange={e => action(() => edit({ kind: 'visible', layer: layer.id, visible: e.target.checked }))} />
                  <LayerThumbnail tabId={id} layer={layer} /><span className="name" title={layer.name}>{layer.name}</span>{layer.locked && <Icon name="lock" size={13} />}
                </li>)}</ul>
                {selected && <label className="artwork-opacity">Layer opacity <input type="number" min={0} max={100} value={Math.round(selected.opacity * 100)} disabled={locked} onChange={e => { const v = Math.max(0, Math.min(100, Number(e.target.value) || 0)); action(() => edit({ kind: 'opacity', opacity: v / 100 })); }} />%</label>}
              </section>
            </>}
          </aside>
        </div>
        <div className="artwork-foot">
          <span className="artwork-status" aria-live="polite">{gesture ? 'Editing…' : status}</span>
          <button onClick={cancel}>Cancel</button>
          <button className="action" disabled={applying || !!text.draft} onClick={() => void apply()}>{applying ? 'Applying…' : 'Apply'}</button>
        </div>
        {textDialog && <TextLayerDialog initial={textDialog} existing submit={settings => { text.replace(settings); setTextDialog(undefined); }} cancel={() => setTextDialog(undefined)} />}
      </div>
    </div>
  );
}
