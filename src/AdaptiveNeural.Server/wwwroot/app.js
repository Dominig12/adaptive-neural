const $ = id => document.getElementById(id);
const netCanvas = $('neural'), worldCanvas = $('world'), chartCanvas = $('chart');
const ctx = netCanvas.getContext('2d'), worldCtx = worldCanvas.getContext('2d'), chartCtx = chartCanvas.getContext('2d');
const memoryChartCanvas = $('memory-chart'), memoryChartCtx = memoryChartCanvas.getContext('2d');
let memoryStudy = null;
let graph = {nodes:[], edges:[], activities:[], inputs:[], outputs:[], structures:[], version:-1}, world = null, paused = false, speed = 1;
let history = [], inflight = false, lastFrame = 0;
let view = {scale:1, x:0, y:0}, drag = null;
let lastWorld = {x:0, y:0}, trail = [];

function resizeCanvas(canvas, context) {
  const r = canvas.getBoundingClientRect(), dpr = Math.min(devicePixelRatio || 1, 2);
  const w = Math.max(1, Math.round(r.width*dpr)), h = Math.max(1, Math.round(r.height*dpr));
  if(canvas.width !== w || canvas.height !== h){ canvas.width=w;canvas.height=h; }
  context.setTransform(dpr,0,0,dpr,0,0);
  return {w:r.width,h:r.height};
}
function networkTransform(w,h) {
  const radius = Math.min(w/2.4,h/2.4)*view.scale;
  return {radius, ox:w/2 + view.x, oy:h/2 + view.y};
}
function drawGrid(context,w,h,spacing=34){
  context.strokeStyle='#172b3c';context.lineWidth=.6;
  context.beginPath();for(let x=0;x<w;x+=spacing){context.moveTo(x,0);context.lineTo(x,h)}
  for(let y=0;y<h;y+=spacing){context.moveTo(0,y);context.lineTo(w,y)} context.stroke();
}
function drawNetwork() {
  const {w,h}=resizeCanvas(netCanvas,ctx);ctx.clearRect(0,0,w,h);drawGrid(ctx,w,h,38);
  const {radius,ox,oy}=networkTransform(w,h), nodes=graph.nodes, acts=graph.activities;
  if(!nodes.length){ctx.fillStyle='#8197aa';ctx.font='13px system-ui';ctx.fillText('Получение топологии…',20,34);return;}
  // Newly differentiated functional structures are shown as translucent regions.
  for(const structure of graph.structures){
    if(structure.parentId===null) continue;
    const members=nodes.filter(n=>n.group===structure.id);
    if(!members.length)continue;
    let sx=0,sy=0;for(const n of members){sx+=n.x;sy+=n.y;}
    const cx=sx/members.length,cy=sy/members.length;
    let range=0.08;for(const n of members)range=Math.max(range,Math.hypot(n.x-cx,n.y-cy)+.04);
    const rx=ox+cx*radius,ry=oy+cy*radius;
    ctx.beginPath();ctx.arc(rx,ry,range*radius,0,Math.PI*2);
    ctx.fillStyle='rgba(171,123,235,.055)';ctx.fill();ctx.strokeStyle='rgba(180,142,240,.42)';ctx.lineWidth=1.2;ctx.stroke();
    ctx.font='10px system-ui';ctx.fillStyle='#c1a5fb';ctx.fillText(`Ансамбль ${structure.id}`,rx+5,ry-range*radius-5);
  }
  const maxEdges=5500, stride=Math.max(1,Math.ceil(graph.edges.length/maxEdges));
  ctx.lineWidth=.8;
  for(let i=0;i<graph.edges.length;i+=stride){
    const e=graph.edges[i],a=nodes[e.from],b=nodes[e.to];if(!a||!b)continue;
    const power=Math.min(1,(acts[e.from]||0)*2),strength=Math.min(1,Math.abs(e.weight)/1.3);
    ctx.strokeStyle=e.weight<0?`rgba(245,132,141,${.06+.25*power*strength})`:`rgba(97,183,198,${.08+.42*power*strength})`;
    ctx.beginPath();ctx.moveTo(ox+a.x*radius,oy+a.y*radius);ctx.lineTo(ox+b.x*radius,oy+b.y*radius);ctx.stroke();
  }
  for(let i=0;i<nodes.length;i++){
    const n=nodes[i],a=acts[i]||0,x=ox+n.x*radius,y=oy+n.y*radius,r=n.role===1?2.5:4.3;
    if(x<-10||x>w+10||y<-10||y>h+10)continue;
    if(a>.37){ctx.fillStyle=`rgba(85,238,191,${Math.min(.22,a*.19)})`;ctx.beginPath();ctx.arc(x,y,r+7*a,0,Math.PI*2);ctx.fill();}
    const color=n.role===0?'#82aeff':n.role===2?'#f0b674':'#59cbb2';
    ctx.globalAlpha=Math.max(.26,Math.min(1,.32+a*1.6));ctx.fillStyle=color;ctx.beginPath();ctx.arc(x,y,r+Math.min(2.6,a*2),0,Math.PI*2);ctx.fill();ctx.globalAlpha=1;
  }
  ctx.fillStyle='#8ca4b8';ctx.font='11px system-ui';ctx.fillText(`${nodes.length} узлов · ${graph.edges.length} связей`,16,21);
}
function drawWorld(){
  const {w,h}=resizeCanvas(worldCanvas,worldCtx),c=worldCtx;c.clearRect(0,0,w,h);drawGrid(c,w,h,34);
  if(!world)return;
  const square=Math.min(w-30,h-24),scale=square/2,cx=w/2,cy=h/2;
  const point=(x,y)=>[cx+x*scale,cy+y*scale];
  c.strokeStyle='#36536b';c.lineWidth=1;c.strokeRect(cx-scale,cy-scale,scale*2,scale*2);
  for(const object of world.objects){
    const [x,y]=point(object.x,object.y);c.beginPath();c.arc(x,y,object.hazard?7:5,0,Math.PI*2);
    if(object.hazard){c.fillStyle='rgba(231,106,132,.12)';c.arc(x,y,14,0,Math.PI*2);c.fill();c.beginPath();c.arc(x,y,6,0,Math.PI*2);c.fillStyle='#eb8297';}
    else{c.fillStyle='#64d7a4';}c.fill();
  }
  if(Math.hypot(world.x-lastWorld.x,world.y-lastWorld.y)>.003){trail.push({x:world.x,y:world.y});if(trail.length>130)trail.shift();lastWorld={x:world.x,y:world.y};}
  c.beginPath();trail.forEach((p,i)=>{const [x,y]=point(p.x,p.y);if(i===0)c.moveTo(x,y);else c.lineTo(x,y)});c.strokeStyle='rgba(95,178,219,.46)';c.lineWidth=1.4;c.stroke();
  const [x,y]=point(world.x,world.y),angle=world.heading;
  c.save();c.translate(x,y);c.rotate(angle);c.fillStyle='rgba(106,196,252,.15)';c.beginPath();c.arc(0,0,19,0,Math.PI*2);c.fill();
  c.beginPath();c.moveTo(14,0);c.lineTo(-10,-8);c.lineTo(-6,0);c.lineTo(-10,8);c.closePath();c.fillStyle='#8bd7ff';c.fill();c.restore();
  c.fillStyle='#8ca4b8';c.font='11px system-ui';c.fillText('Питание • зелёное     Опасность • красное',14,19);
}
function drawChart(){
  const {w,h}=resizeCanvas(chartCanvas,chartCtx),c=chartCtx;c.clearRect(0,0,w,h);const left=40,top=10,right=w-12,bottom=h-24;
  c.font='10px system-ui';c.strokeStyle='#2d4255';c.fillStyle='#7890a6';c.lineWidth=.7;
  for(let i=0;i<=4;i++){const y=top+(bottom-top)*i/4;c.beginPath();c.moveTo(left,y);c.lineTo(right,y);c.stroke();c.fillText((1-i/4).toFixed(2),5,y+3);}
  if(history.length<2)return;
  for(const [key,color] of [['activity','#65ddc2'],['stress','#f4b26c'],['memory','#bd96ed']]){
    c.beginPath();history.forEach((item,i)=>{const x=left+(right-left)*i/219,y=bottom-Math.min(1,item[key])*(bottom-top);if(!i)c.moveTo(x,y);else c.lineTo(x,y)});
    c.strokeStyle=color;c.lineWidth=2;c.stroke();
  }
  c.fillText('Последние ~22 с при 1×',left,bottom+17);
}
function draw(ts){requestAnimationFrame(draw);if(ts-lastFrame<33)return;lastFrame=ts;drawNetwork();drawWorld();drawChart();drawMemoryStudy();}
requestAnimationFrame(draw);

async function post(url,data){
  const response=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
  if(!response.ok)throw new Error(`${url}: HTTP ${response.status}`);
}
async function poll(){
  if(inflight)return;inflight=true;
  try{
    const response=await fetch(`/api/snapshot?version=${graph.version}`,{cache:'no-store'});if(!response.ok)throw new Error(`HTTP ${response.status}`);
    const state=await response.json(),n=state.network;
    if(n.nodes){graph.nodes=n.nodes;graph.edges=n.edges||[];graph.inputs=n.inputs||[];graph.outputs=n.outputs||[];graph.structures=n.structures||[];graph.version=n.topologyVersion;updateEvolutionInspector();}
    graph.activities=n.activities;world=state.world;paused=state.paused;speed=state.speed;
    $('play').textContent=paused?'Пуск':'Пауза';$('speed').value=String(speed);
    $('m-neurons').textContent=n.metrics.neurons.toLocaleString('ru-RU');$('m-born').textContent=`+${n.metrics.births} новых`;
    $('m-connections').textContent=n.metrics.connections.toLocaleString('ru-RU');$('m-pruned').textContent=`${n.metrics.prunedConnections} удалено`;
    $('m-activity').textContent=n.metrics.meanActivity.toFixed(3);$('m-stress').textContent=n.metrics.meanStress.toFixed(3);$('m-steps').textContent=n.metrics.steps.toLocaleString('ru-RU');
    $('m-ports').textContent=`${n.metrics.inputPorts} / ${n.metrics.outputPorts}`;
    $('m-port-births').textContent=`+${n.metrics.inputBirths} / +${n.metrics.outputBirths} · ${n.metrics.retiredPorts} отозвано`;
    $('m-memory').textContent=n.metrics.stableConnections.toLocaleString('ru-RU');
    $('m-memory-strength').textContent=`${(n.metrics.meanConsolidation||0).toFixed(3)} средняя · ${n.metrics.memoryReactivations||0} эпизодов`;
    updateMemoryGroups(n.memoryGroups||[]);
    $('m-structures').textContent=String(n.metrics.structures);
    $('m-structure-births').textContent=`+${n.metrics.structureBirths} новых`;
    $('energy').textContent=`${Math.round(world.energy*100)}%`;$('energy-bar').style.width=`${world.energy*100}%`;$('food-collected').textContent=world.foodCollected;
    if(!paused&&(!history.length||n.metrics.steps!==history[history.length-1].steps)){
      history.push({activity:n.metrics.meanActivity,stress:n.metrics.meanStress,memory:n.metrics.meanConsolidation||0,steps:n.metrics.steps});if(history.length>220)history.shift();
    }
    $('connection').className='connection connected';$('connection').innerHTML='<span class="dot"></span>Локальный сервер';
  }catch(error){$('connection').className='connection error';$('connection').innerHTML='<span class="dot"></span>Нет соединения';console.warn(error);}
  finally{inflight=false;}
}
setInterval(poll,110);poll();
$('play').addEventListener('click',async()=>{await post('/api/control',{action:paused?'play':'pause'});poll();});
$('step').addEventListener('click',async()=>{await post('/api/control',{action:'step'});poll();});
$('reset').addEventListener('click',async()=>{await post('/api/control',{action:'reset'});graph.version=-1;history=[];trail=[];poll();});
$('speed').addEventListener('change',e=>post('/api/control',{speed:Number(e.target.value)}));
function setting(id,key,digits){
  const el=$(id),out=$(id+'-v');el.addEventListener('input',()=>{out.value=Number(el.value).toFixed(digits)});
  el.addEventListener('change',()=>post('/api/control',{[key]:Number(el.value)}));
}
setting('target','targetActivity',3);setting('learning','learningRate',4);setting('growth','growthThreshold',3);
setting('interface-growth','interfaceGrowthThreshold',3);setting('structure-growth','structureGrowthThreshold',3);
setting('memory-threshold','memoryActivationThreshold',3);
$('structural').addEventListener('change',e=>post('/api/control',{structuralPlasticity:e.target.checked}));
$('adaptive-interfaces').addEventListener('change',e=>post('/api/control',{adaptiveInterfaces:e.target.checked}));
$('adaptive-structures').addEventListener('change',e=>post('/api/control',{adaptiveStructures:e.target.checked}));
$('memory-consolidation').addEventListener('change',e=>post('/api/control',{memoryConsolidation:e.target.checked}));

function updateEvolutionInspector(){
  $('input-ports-count').textContent=String(graph.inputs.length);
  $('output-ports-count').textContent=String(graph.outputs.length);
  function drawPorts(id,ports,motor){
    const container=$(id);const rows=[];
    for(const port of ports){
      const row=document.createElement('div');row.className='port-row'+(port.primary?'':' new-port');
      const name=document.createElement('strong');name.textContent=`N${port.neuronId}`;
      const mapping=document.createElement('span');
      mapping.textContent=motor?`→ действие ${port.physicalChannel}`:
        (port.secondaryChannel===null?`← сенсор ${port.physicalChannel}`:`← ${port.physicalChannel} + ${port.secondaryChannel}`);
      row.append(name,mapping);rows.push(row);
    }
    container.replaceChildren(...rows);
  }
  drawPorts('ports-in',graph.inputs,false);drawPorts('ports-out',graph.outputs,true);
  const list=$('structures-list'),items=[];
  for(const group of graph.structures){
    const item=document.createElement('div');item.className='structure-item'+(group.parentId===null?'':' new-structure');
    const title=document.createElement('strong');title.textContent=`Группа ${group.id}`;
    const note=document.createElement('div');note.textContent=`${group.members} нейронов · ${group.parentId===null?'исходная':`из ${group.parentId} · шаг ${group.createdAt}`}`;
    item.append(title,note);items.push(item);
  }
  list.replaceChildren(...items);
}


netCanvas.addEventListener('pointerdown',e=>{netCanvas.setPointerCapture(e.pointerId);drag={x:e.clientX,y:e.clientY,ox:view.x,oy:view.y,moved:false};});
netCanvas.addEventListener('pointermove',e=>{if(!drag)return;const dx=e.clientX-drag.x,dy=e.clientY-drag.y;if(Math.abs(dx)+Math.abs(dy)>5)drag.moved=true;if(drag.moved){view.x=drag.ox+dx;view.y=drag.oy+dy;}});
netCanvas.addEventListener('pointerup',async e=>{if(!drag)return;const moved=drag.moved;drag=null;if(moved)return;
  const rect=netCanvas.getBoundingClientRect(),{radius,ox,oy}=networkTransform(rect.width,rect.height);
  const x=(e.clientX-rect.left-ox)/radius,y=(e.clientY-rect.top-oy)/radius;
  await post('/api/stimulus',{x,y,strength:3.5});
});
netCanvas.addEventListener('wheel',e=>{e.preventDefault();const factor=Math.exp(-e.deltaY*.001);view.scale=Math.max(.5,Math.min(5,view.scale*factor));$('graph-zoom').textContent=`${Math.round(view.scale*100)}%`;},{passive:false});
worldCanvas.addEventListener('click',async e=>{
  const r=worldCanvas.getBoundingClientRect(),square=Math.min(r.width-30,r.height-24),scale=square/2;
  const x=(e.clientX-r.left-r.width/2)/scale,y=(e.clientY-r.top-r.height/2)/scale;
  if(Math.abs(x)>1||Math.abs(y)>1)return;
  await post('/api/world/spawn',{x,y,hazard:$('spawnMode').value==='hazard'});
});


function updateMemoryGroups(groups){
  const entries=[];
  for(const group of groups){
    const row=document.createElement('div');row.className='memory-group';
    const label=document.createElement('strong');label.textContent=`Ансамбль ${group.groupId}`;
    const track=document.createElement('div');track.className='memory-progress';
    const progress=document.createElement('div');progress.style.width=`${Math.min(100,Math.max(0,group.meanStability*100))}%`;
    track.append(progress);
    const note=document.createElement('small');note.textContent=`${group.stableConnections}/${group.connections}`;
    row.title=`Корреляция: сенсор ${group.preferredSensor>=0?group.preferredSensor:'—'}, избирательность ${(group.selectivity*100).toFixed(0)}%`;
    row.append(label,track,note);entries.push(row);
  }
  $('memory-groups').replaceChildren(...entries);
}

function drawMemoryStudy(){
  const {w,h}=resizeCanvas(memoryChartCanvas,memoryChartCtx),c=memoryChartCtx;
  c.clearRect(0,0,w,h);drawGrid(c,w,h,38);
  if(!memoryStudy?.series?.length){c.fillStyle='#8197aa';c.font='12px system-ui';c.fillText('Результаты ещё не получены',18,26);return;}
  const items=memoryStudy.series,plot={l:38,r:w-12,t:14,b:h-32};
  const maximum=Math.max(1,...items.flatMap(x=>[x.repeatedStable,x.shuffledStable]));
  c.font='10px system-ui';c.fillStyle='#8198b0';
  for(let k=0;k<=4;k++){const y=plot.t+(plot.b-plot.t)*k/4;c.fillText(String(Math.round(maximum*(1-k/4))),5,y+3);}
  for(const [key,color] of [['repeatedStable','#bd96ed'],['shuffledStable','#67baca']]){
    c.beginPath();items.forEach((d,i)=>{
      const x=plot.l+(plot.r-plot.l)*(items.length===1?0:i/(items.length-1));
      const y=plot.b-(plot.b-plot.t)*d[key]/maximum;
      if(i===0)c.moveTo(x,y);else c.lineTo(x,y);
    });c.strokeStyle=color;c.lineWidth=2.1;c.stroke();
  }
  c.fillText('Повторяющаяся пара',plot.l, h-8);c.fillStyle='#bd96ed';c.fillRect(plot.l+112,h-15,13,3);
  c.fillStyle='#8198b0';c.fillText('Перемешанные пары',plot.l+140,h-8);c.fillStyle='#67baca';c.fillRect(plot.l+263,h-15,13,3);
}

$('run-memory-experiment').addEventListener('click', async()=>{
  const button=$('run-memory-experiment');button.disabled=true;
  $('memory-experiment-status').textContent='Выполняется отдельный эксперимент; рабочая симуляция не затрагивается…';
  try{
    const response=await fetch('/api/memory/experiment',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({seed:1337,episodes:24})});
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    memoryStudy=await response.json();
    const end=memoryStudy.series.at(-1);
    $('memory-experiment-status').textContent=`${memoryStudy.episodes} эпизодов · одинаковый seed ${memoryStudy.seed} · независимые сети`;
    $('memory-experiment-summary').textContent=`Закреплённые связи: ${end.repeatedStable} (повторение) / ${end.shuffledStable} (перемешивание). Отклик на знакомую пару: ${memoryStudy.repeatedProbeResponse.toFixed(4)} / ${memoryStudy.shuffledProbeResponse.toFixed(4)}.`;
  }catch(error){$('memory-experiment-status').textContent=`Ошибка исследования: ${error.message}`;}
  finally{button.disabled=false;}
});
