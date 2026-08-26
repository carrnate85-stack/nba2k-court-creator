from __future__ import annotations

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
from urllib.parse import unquote, urlparse


HTML = r"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>NBA 2K Court Logo Editor</title>
  <style>
    :root{color-scheme:dark;font-family:"Segoe UI",Arial,sans-serif;--bg:#171a20;--work:#11141a;--surface:#20242b;--header:#222833;--inspector:#1d222c;--panel:#202632;--button:#303746;--border:#343b49;--strong:#475064;--text:#edf1f7;--heading:#f8fafc;--muted:#aab3c2;--subtle:#99a5b8;--primary:#f0b429;--primaryText:#171a20;--return:#168579;--danger:#6a2f35}
    *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);overflow:hidden}
    header{height:48px;display:flex;align-items:center;gap:8px;padding:0 14px;background:var(--header);border-bottom:1px solid var(--border)}
    header strong{color:var(--heading);font-size:16px}.spacer{flex:1}.hint{color:var(--muted);font-size:12px}
    button,input{font:inherit}button{border:1px solid transparent;border-radius:6px;padding:7px 10px;background:var(--button);color:var(--text);font-weight:600;cursor:pointer}button:hover{border-color:var(--strong)}button.primary{background:var(--primary);color:var(--primaryText)}button.return{background:var(--return);color:#fff}button.danger{background:var(--danger);color:#fff}
    #layout{height:calc(100vh - 48px);display:grid;grid-template-columns:minmax(0,1fr) 330px}#stage{position:relative;min-width:0;min-height:0;background:var(--work)}canvas{width:100%;height:100%;display:block;outline:none}
    aside{background:var(--inspector);border-left:1px solid var(--border);padding:12px;overflow:auto}h2{font-size:13px;margin:0 0 8px;color:var(--heading)}.section{border:1px solid var(--border);background:var(--panel);border-radius:6px;padding:10px;margin-bottom:10px}
    #layers{display:flex;flex-direction:column;gap:6px;max-height:270px;overflow:auto}.layer{width:100%;display:grid;grid-template-columns:22px minmax(0,1fr);gap:8px;text-align:left;align-items:center;border-color:var(--border);background:#252b37}.layer.active{border-color:var(--primary);background:#343b49}.layer .eye{color:var(--muted);font-weight:700}.layer b,.layer span{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.layer span{font-size:11px;color:var(--muted);font-weight:400}
    .grid{display:grid;grid-template-columns:1fr 1fr;gap:8px}.row{display:flex;gap:8px;align-items:center;margin-top:8px}.row>*{flex:1}label{display:block;color:var(--muted);font-size:12px;margin-bottom:4px}input[type=number]{width:100%;border:1px solid var(--strong);border-radius:6px;background:#11161d;color:var(--text);padding:7px}input[type=range]{width:100%;accent-color:var(--primary)}.empty{padding:10px 2px;color:var(--muted);font-size:12px;line-height:1.35}.status{min-height:34px;color:var(--muted);font-size:12px;line-height:1.35}
    @media(max-width:900px){#layout{grid-template-columns:minmax(0,1fr) 300px}.hint{display:none}}
  </style>
</head>
<body>
  <header>
    <strong>Court Logo Editor</strong>
    <button id="fit">Fit</button><button id="zoomOut">Zoom -</button><button id="zoomIn">Zoom +</button>
    <span class="hint">Drag logos. Pull corner handles to resize. Wheel zooms. Shift-drag pans.</span>
    <span class="spacer"></span><button id="done" class="return">Done - Return to App</button>
  </header>
  <div id="layout">
    <main id="stage"><canvas id="canvas" tabindex="0"></canvas></main>
    <aside>
      <section class="section"><h2>Logos</h2><div id="layers"></div></section>
      <section class="section" id="inspector" hidden>
        <h2>Selected</h2>
        <div class="grid">
          <div><label>Left</label><input id="x" type="number" step="1"></div>
          <div><label>Top</label><input id="y" type="number" step="1"></div>
          <div><label>Width</label><input id="w" type="number" step="1" min="1"></div>
          <div><label>Height</label><input id="h" type="number" step="1" min="1"></div>
          <div><label>Turn</label><input id="r" type="number" step="1"></div>
          <div><label>Opacity</label><input id="o" type="number" step="1" min="0" max="100"></div>
        </div>
        <div class="row"><button id="visible">Hide</button><button id="flipX">Flip H</button><button id="flipY">Flip V</button></div>
        <div class="row"><button id="front">Move Up</button><button id="back">Move Down</button></div>
        <div class="row"><button id="remove" class="danger">Remove</button></div>
      </section>
      <section class="section"><h2>Canvas</h2><div class="status" id="status">Loading editor...</div></section>
    </aside>
  </div>
  <script>
    const $=id=>document.getElementById(id),canvas=$('canvas'),ctx=canvas.getContext('2d'),stage=$('stage');
    let state,project,items=[],selectedId=null,bg=new Image(),images=new Map(),scale=1,minScale=1,panX=0,panY=0,drag=null,dirty=false,saving=false;
    const alphaCanvas=document.createElement('canvas'),alphaCtx=alphaCanvas.getContext('2d',{willReadFrequently:true});alphaCanvas.width=1;alphaCanvas.height=1;
    async function api(path,payload){const options=payload===undefined?{cache:'no-store'}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)};const res=await fetch(path,options);const data=await res.json();if(!res.ok)throw new Error(data.error||`Request failed ${res.status}`);return data}
    function status(text){$('status').textContent=text}
    async function load(){state=await api('/api/project');project=state.project;items=project.items||[];selectedId=project.selectedId||items[0]?.id||null;bg.onload=()=>{loadLogoImages();fit();status(items.length?'Select, drag, and resize logos on the court.':'Import logos in the desktop app, then reopen this editor.');};bg.src='/api/court?v='+state.revision;renderLayers();renderInspector();resize()}
    function loadLogoImages(){images.clear();for(const item of items){const image=new Image();image.onload=draw;image.src=`/api/logo/${encodeURIComponent(item.id)}?v=${state.revision}`;images.set(item.id,image)}draw()}
    function selected(){return items.find(x=>x.id===selectedId)||null}
    function renderLayers(){const host=$('layers');host.innerHTML='';if(!items.length){host.innerHTML='<div class="empty">No logos yet. Use Import Logo in the desktop app first.</div>';return}items.slice().reverse().forEach((item,index)=>{const b=document.createElement('button');b.className='layer'+(item.id===selectedId?' active':'');b.innerHTML=`<span class="eye">${item.visible?'On':'Off'}</span><div><b>${item.name||'Logo'}</b><span>${items.length-index} of ${items.length}</span></div>`;b.onclick=()=>{selectedId=item.id;renderLayers();renderInspector();draw();};host.append(b)})}
    function renderInspector(){const item=selected(),show=!!item;$('inspector').hidden=!show;if(!show)return;$('x').value=Math.round(item.x||0);$('y').value=Math.round(item.y||0);$('w').value=Math.round(item.width||1);$('h').value=Math.round(item.height||1);$('r').value=Math.round(item.rotation||0);$('o').value=Math.round(item.opacity??100);$('visible').textContent=item.visible?'Hide':'Show'}
    function resize(){const ratio=devicePixelRatio||1;canvas.width=Math.max(1,stage.clientWidth*ratio);canvas.height=Math.max(1,stage.clientHeight*ratio);ctx.setTransform(ratio,0,0,ratio,0,0);draw()}
    function fit(){minScale=Math.max(.03,Math.min(1,(stage.clientWidth-40)/project.width,(stage.clientHeight-40)/project.height));scale=minScale;panX=(stage.clientWidth-project.width*scale)/2;panY=(stage.clientHeight-project.height*scale)/2;draw()}
    function schedule(){if(dirty)return;dirty=true;requestAnimationFrame(()=>{dirty=false;draw()})}
    function toScreen(x,y){return{x:panX+x*scale,y:panY+y*scale}}function toDoc(x,y){return{x:(x-panX)/scale,y:(y-panY)/scale}}
    function corners(item){const cx=item.x+item.width/2,cy=item.y+item.height/2,rad=(item.rotation||0)*Math.PI/180,cs=Math.cos(rad),sn=Math.sin(rad),pts=[[-.5,-.5],[.5,-.5],[.5,.5],[-.5,.5]];return pts.map(([px,py])=>({x:cx+(px*item.width)*cs-(py*item.height)*sn,y:cy+(px*item.width)*sn+(py*item.height)*cs}))}
    function itemPath(item){const pts=corners(item).map(p=>toScreen(p.x,p.y));ctx.beginPath();pts.forEach((p,i)=>i?ctx.lineTo(p.x,p.y):ctx.moveTo(p.x,p.y));ctx.closePath();return pts}
    function drawItem(item,outline=false){const image=images.get(item.id);if(!image||!image.complete||!item.visible)return;const center=toScreen((item.x||0)+(item.width||1)/2,(item.y||0)+(item.height||1)/2);ctx.save();ctx.translate(center.x,center.y);ctx.rotate((item.rotation||0)*Math.PI/180);ctx.globalAlpha=Math.max(0,Math.min(1,(item.opacity??100)/100));ctx.scale(item.flipX?-1:1,item.flipY?-1:1);ctx.drawImage(image,-item.width*scale/2,-item.height*scale/2,item.width*scale,item.height*scale);ctx.restore();if(outline)drawSelection(item)}
    function drawSelection(item){const pts=itemPath(item);ctx.strokeStyle='#f0b429';ctx.lineWidth=2;ctx.stroke();ctx.fillStyle='#f0b429';for(const p of pts){ctx.fillRect(p.x-5,p.y-5,10,10)}}
    function draw(){ctx.clearRect(0,0,stage.clientWidth,stage.clientHeight);ctx.fillStyle='#11141a';ctx.fillRect(0,0,stage.clientWidth,stage.clientHeight);if(project&&bg.complete)ctx.drawImage(bg,panX,panY,project.width*scale,project.height*scale);for(const item of items)drawItem(item,false);const item=selected();if(item)drawSelection(item)}
    function canvasPoint(e){const r=canvas.getBoundingClientRect();return{x:e.clientX-r.left,y:e.clientY-r.top}}
    function alphaHit(item,screenPoint){const image=images.get(item.id);if(!image||!image.complete)return true;const doc=toDoc(screenPoint.x,screenPoint.y),cx=item.x+item.width/2,cy=item.y+item.height/2,rad=-(item.rotation||0)*Math.PI/180,cs=Math.cos(rad),sn=Math.sin(rad),dx=doc.x-cx,dy=doc.y-cy;let lx=dx*cs-dy*sn+item.width/2,ly=dx*sn+dy*cs+item.height/2;if(item.flipX)lx=item.width-lx;if(item.flipY)ly=item.height-ly;if(lx<0||ly<0||lx>item.width||ly>item.height)return false;const sx=Math.max(0,Math.min(image.naturalWidth-1,Math.floor(lx/item.width*image.naturalWidth))),sy=Math.max(0,Math.min(image.naturalHeight-1,Math.floor(ly/item.height*image.naturalHeight)));alphaCtx.clearRect(0,0,1,1);alphaCtx.drawImage(image,sx,sy,1,1,0,0,1,1);return alphaCtx.getImageData(0,0,1,1).data[3]>12}
    function hit(e){const p=canvasPoint(e);const item=selected();if(item){const pts=corners(item).map(v=>toScreen(v.x,v.y));for(let i=pts.length-1;i>=0;i--){if(Math.hypot(p.x-pts[i].x,p.y-pts[i].y)<12)return{item,handle:i,p}}}for(let i=items.length-1;i>=0;i--){const candidate=items[i];if(!candidate.visible)continue;ctx.save();itemPath(candidate);const inside=ctx.isPointInPath(p.x,p.y);ctx.restore();if(inside&&alphaHit(candidate,p))return{item:candidate,handle:null,p}}return{item:null,handle:null,p}}
    function zoom(factor,center){const before=toDoc(center.x,center.y);scale=Math.max(minScale*.5,Math.min(12,scale*factor));panX=center.x-before.x*scale;panY=center.y-before.y*scale;schedule()}
    canvas.onpointerdown=e=>{canvas.focus();const p=canvasPoint(e);if(e.shiftKey||e.button===1){drag={mode:'pan',p,panX,panY};return}const found=hit(e);if(!found.item){selectedId=null;renderLayers();renderInspector();draw();return}selectedId=found.item.id;renderLayers();renderInspector();drag={mode:found.handle===null?'move':'resize',handle:found.handle,start:p,item:{...found.item}};schedule()};
    canvas.onpointermove=e=>{if(!drag)return;const p=canvasPoint(e),item=selected();if(drag.mode==='pan'){panX=drag.panX+p.x-drag.p.x;panY=drag.panY+p.y-drag.p.y;schedule();return}if(!item)return;const dx=(p.x-drag.start.x)/scale,dy=(p.y-drag.start.y)/scale;if(drag.mode==='move'){item.x=drag.item.x+dx;item.y=drag.item.y+dy}else{const local=toLocalDelta(dx,dy,drag.item.rotation||0);const signX=drag.handle===0||drag.handle===3?-1:1,signY=drag.handle<2?-1:1;item.width=Math.max(8,drag.item.width+local.x*signX);item.height=Math.max(8,drag.item.height+local.y*signY);if(signX<0)item.x=drag.item.x+drag.item.width-item.width;if(signY<0)item.y=drag.item.y+drag.item.height-item.height}renderInspector();schedule()};
    canvas.onpointerup=async()=>{if(!drag)return;drag=null;await save()};
    canvas.onwheel=e=>{e.preventDefault();zoom(e.deltaY<0?1.15:1/1.15,canvasPoint(e))};canvas.oncontextmenu=e=>e.preventDefault();
    function toLocalDelta(dx,dy,deg){const r=-deg*Math.PI/180,cs=Math.cos(r),sn=Math.sin(r);return{x:dx*cs-dy*sn,y:dx*sn+dy*cs}}
    async function save(){if(saving)return;saving=true;try{state=await api('/api/save',{selectedId,items});status('Saved to desktop preview.')}catch(e){status(e.message)}finally{saving=false}}
    function updateFields(){const item=selected();if(!item)return;item.x=+$('x').value||0;item.y=+$('y').value||0;item.width=Math.max(1,+$('w').value||1);item.height=Math.max(1,+$('h').value||1);item.rotation=+$('r').value||0;item.opacity=Math.max(0,Math.min(100,+$('o').value||100));draw();save()}
    for(const id of ['x','y','w','h','r','o'])$(id).onchange=updateFields;
    $('visible').onclick=()=>{const item=selected();if(!item)return;item.visible=!item.visible;renderLayers();renderInspector();draw();save()};$('flipX').onclick=()=>{const item=selected();if(!item)return;item.flipX=!item.flipX;draw();save()};$('flipY').onclick=()=>{const item=selected();if(!item)return;item.flipY=!item.flipY;draw();save()};
    $('front').onclick=()=>{const i=items.findIndex(x=>x.id===selectedId);if(i>=0&&i<items.length-1){[items[i],items[i+1]]=[items[i+1],items[i]];renderLayers();draw();save()}};$('back').onclick=()=>{const i=items.findIndex(x=>x.id===selectedId);if(i>0){[items[i],items[i-1]]=[items[i-1],items[i]];renderLayers();draw();save()}};
    $('remove').onclick=()=>{const i=items.findIndex(x=>x.id===selectedId);if(i<0)return;items.splice(i,1);selectedId=items.at(-1)?.id||null;renderLayers();renderInspector();draw();save()};
    canvas.onkeydown=e=>{const item=selected();if(!item)return;const amount=e.shiftKey?10:1;if(e.key==='ArrowLeft')item.x-=amount;else if(e.key==='ArrowRight')item.x+=amount;else if(e.key==='ArrowUp')item.y-=amount;else if(e.key==='ArrowDown')item.y+=amount;else return;e.preventDefault();renderInspector();draw();save()};
    $('fit').onclick=fit;$('zoomIn').onclick=()=>zoom(1.25,{x:stage.clientWidth/2,y:stage.clientHeight/2});$('zoomOut').onclick=()=>zoom(1/1.25,{x:stage.clientWidth/2,y:stage.clientHeight/2});$('done').onclick=async()=>{await api('/api/return',{});$('done').textContent='App Ready - Close This Tab';status('Returned changes to the desktop app.')};
    window.onresize=resize;load().catch(e=>status(e.message));
  </script>
</body>
</html>"""


def read_state(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def write_state(path: Path, value: dict) -> dict:
    value["revision"] = int(value.get("revision") or 0) + 1
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    temporary.replace(path)
    return value


def logo_path(project_root: Path, item: dict) -> Path:
    raw = Path(str(item.get("path") or ""))
    resolved = raw if raw.is_absolute() else project_root / raw
    resolved = resolved.resolve()
    if not str(resolved).lower().startswith(str(project_root.resolve()).lower()):
        raise ValueError("That logo is outside this project.")
    return resolved


def handler_class(state_path: Path):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, _format, *args):
            return

        def do_GET(self):  # noqa: N802
            try:
                path = urlparse(self.path).path
                state = read_state(state_path)
                project = state["project"]
                if path == "/":
                    self._send(HTML.encode("utf-8"), "text/html; charset=utf-8")
                elif path == "/api/project":
                    self._json(state)
                elif path == "/api/court":
                    self._file(Path(project["backgroundPath"]))
                elif path.startswith("/api/logo/"):
                    item_id = unquote(path.rsplit("/", 1)[1])
                    item = next(item for item in project.get("items", []) if item.get("id") == item_id)
                    self._file(logo_path(Path(project["projectRoot"]), item))
                else:
                    self.send_error(404)
            except Exception as exc:  # noqa: BLE001
                self._json({"error": str(exc)}, 500)

        def do_POST(self):  # noqa: N802
            try:
                length = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(length) or b"{}")
                path = urlparse(self.path).path
                state = read_state(state_path)
                if path == "/api/save":
                    state["returnRequested"] = False
                    state["project"]["selectedId"] = payload.get("selectedId")
                    state["project"]["items"] = clean_items(payload.get("items", []))
                    self._json(write_state(state_path, state))
                elif path == "/api/return":
                    state["returnRequested"] = True
                    self._json(write_state(state_path, state))
                else:
                    self.send_error(404)
            except Exception as exc:  # noqa: BLE001
                self._json({"error": str(exc)}, 400)

        def _json(self, value: dict, status: int = 200):
            self._send(json.dumps(value).encode("utf-8"), "application/json", status)

        def _file(self, path: Path):
            content_type = "image/png" if path.suffix.lower() == ".png" else "image/jpeg"
            self._send(path.read_bytes(), content_type)

        def _send(self, data: bytes, content_type: str, status: int = 200):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

    return Handler


def clean_items(raw_items) -> list[dict]:
    items = []
    for item in raw_items:
        try:
            items.append(
                {
                    "id": str(item.get("id") or ""),
                    "name": str(item.get("name") or "Logo"),
                    "path": str(item.get("path") or ""),
                    "visible": bool(item.get("visible", True)),
                    "x": float(item.get("x", 0)),
                    "y": float(item.get("y", 0)),
                    "width": max(1.0, float(item.get("width", 1))),
                    "height": max(1.0, float(item.get("height", 1))),
                    "rotation": float(item.get("rotation", 0)),
                    "opacity": max(0.0, min(100.0, float(item.get("opacity", 100)))),
                    "flipX": bool(item.get("flipX", False)),
                    "flipY": bool(item.get("flipY", False)),
                }
            )
        except (TypeError, ValueError):
            continue
    return [item for item in items if item["id"] and item["path"]]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", required=True)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", 0), handler_class(Path(args.state)))
    print(json.dumps({"url": f"http://127.0.0.1:{server.server_port}/"}), flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
