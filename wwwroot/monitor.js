(()=> {
const grid=document.getElementById("monitorGrid");
let signature="",players=[],busy=false;
function clear(){for(const p of players)p.destroy();players=[];grid.replaceChildren();}
function build(config){
 const cams=config.cameras||[config];
 const rows=config.rows||1,cols=config.columns||1;
 clear();grid.style.setProperty("--rows",rows);grid.style.setProperty("--cols",cols);
 for(let i=0;i<rows*cols;i++){
  const c=cams[i],tile=document.createElement("div");tile.className="tile";grid.append(tile);
  if(!c){tile.classList.add("empty");tile.textContent="Sem camera";continue;}
  const v=document.createElement("video");v.autoplay=true;v.muted=true;v.playsInline=true;tile.append(v);
  const label=document.createElement("span");label.className="label";label.textContent=c.name;tile.append(label);
  const status=document.createElement("span");status.className="status";status.textContent="Conectando…";tile.append(status);
  v.addEventListener("playing",()=>status.hidden=true);v.addEventListener("stalled",()=>{status.hidden=false;status.textContent="Aguardando video…";});
  v.addEventListener("dblclick",()=>v.requestFullscreen?.());
  const url="/monitor/hls/"+encodeURIComponent(c.id)+"/index.m3u8";
  if(window.Hls&&Hls.isSupported()){
   const h=new Hls({maxBufferLength:8,liveSyncDurationCount:3});players.push(h);h.loadSource(url);h.attachMedia(v);
   h.on(Hls.Events.MANIFEST_PARSED,()=>v.play().catch(()=>{}));
   h.on(Hls.Events.ERROR,(_,d)=>{if(!d.fatal)return;status.hidden=false;status.textContent="Reconectando…";setTimeout(()=>{if(!players.includes(h))return;if(d.type===Hls.ErrorTypes.MEDIA_ERROR)h.recoverMediaError();else h.startLoad();},3000);});
  }else if(v.canPlayType("application/vnd.apple.mpegurl")){v.src=url;}else{status.textContent="HLS indisponivel";}
 }
}
async function sync(){if(busy)return;busy=true;try{const r=await fetch("/monitor/config",{cache:"no-store"});if(!r.ok)throw Error();const c=await r.json();const s=JSON.stringify(c);if(s!==signature){build(c);signature=s;}}catch(_){if(!signature){grid.textContent="Aguardando NVR…";}}finally{busy=false;}}
sync();setInterval(sync,5000);
})();