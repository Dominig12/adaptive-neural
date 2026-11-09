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
    if(n.dormant){ctx.fillStyle='#4a5161';ctx.beginPath();ctx.arc(x,y,r,0,Math.PI*2);ctx.fill();continue;}
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
  c.fillStyle=`rgba(8,18,36,${Math.max(0,(1-world.daylight)*.43)})`;
  c.fillRect(cx-scale,cy-scale,scale*2,scale*2);
  c.strokeStyle='#36536b';c.lineWidth=1;c.strokeRect(cx-scale,cy-scale,scale*2,scale*2);
  const [agentX,agentY]=point(world.x,world.y),half=world.fieldOfViewDegrees*Math.PI/360;
  const radius=world.effectiveVisionRange*scale;
  c.beginPath();c.moveTo(agentX,agentY);c.arc(agentX,agentY,radius,world.heading-half,world.heading+half);c.closePath();
  c.fillStyle='rgba(106,194,229,.075)';c.fill();c.strokeStyle='rgba(124,203,248,.27)';c.lineWidth=1;c.stroke();
  for(const object of world.objects){
    const [x,y]=point(object.x,object.y),r=Math.max(4,object.radius*scale);
    c.save();c.globalAlpha=object.visible?1:.25;
    switch(object.kind){
      case 'rock':
        c.fillStyle='#7790a5';c.strokeStyle='#aec3d2';c.lineWidth=1;
        c.beginPath();for(let i=0;i<7;i++){const a=i*Math.PI*2/7,px=x+Math.cos(a)*(r+(i%2?1:-2)),py=y+Math.sin(a)*(r+(i%3?1:-2));if(!i)c.moveTo(px,py);else c.lineTo(px,py);}c.closePath();c.fill();c.stroke();break;
      case 'shelter':
        c.beginPath();c.arc(x,y,r+3,0,Math.PI*2);c.fillStyle='rgba(96,179,238,.13)';c.fill();
        c.strokeStyle='#86bfe7';c.lineWidth=2;c.beginPath();c.moveTo(x-r,y+r*.55);c.lineTo(x,y-r*.7);c.lineTo(x+r,y+r*.55);c.stroke();break;
      case 'hazard':
        c.beginPath();c.arc(x,y,r+3,0,Math.PI*2);c.fillStyle='rgba(231,106,132,.14)';c.fill();
        c.beginPath();c.arc(x,y,r*.55,0,Math.PI*2);c.fillStyle='#eb8297';c.fill();break;
      default:
        c.beginPath();c.arc(x,y,r*.6,0,Math.PI*2);c.fillStyle='#64d7a4';c.fill();break;
    }
    c.restore();
  }
  if(Math.hypot(world.x-lastWorld.x,world.y-lastWorld.y)>.003){trail.push({x:world.x,y:world.y});if(trail.length>130)trail.shift();lastWorld={x:world.x,y:world.y};}
  c.beginPath();trail.forEach((p,i)=>{const [x,y]=point(p.x,p.y);if(i===0)c.moveTo(x,y);else c.lineTo(x,y)});c.strokeStyle='rgba(95,178,219,.46)';c.lineWidth=1.4;c.stroke();
  c.save();c.translate(agentX,agentY);c.rotate(world.heading);c.fillStyle='rgba(106,196,252,.15)';c.beginPath();c.arc(0,0,16,0,Math.PI*2);c.fill();
  c.beginPath();c.moveTo(14,0);c.lineTo(-10,-8);c.lineTo(-6,0);c.lineTo(-10,8);c.closePath();c.fillStyle='#8bd7ff';c.fill();c.restore();
  c.fillStyle='#8ca4b8';c.font='11px system-ui';c.fillText('Пища · опасность · камни · укрытия  |  тусклые объекты не видны агенту',14,19);
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
    $('m-resource').textContent=`${n.metrics.resource.toFixed(1)} / ${n.metrics.resourceCapacity.toFixed(0)}`;
    $('m-resource-detail').textContent=`Содержание: ${n.metrics.maintenance.toFixed(3)}/тик · поступило: ${n.metrics.lastIntake.toFixed(3)}`;
    $('m-atrophy').textContent=String(n.metrics.atrophyEvents);
    $('m-atrophy-detail').textContent=`${n.metrics.dormantNeurons} спящих · голод: ${n.metrics.starvationTicks} тиков`;
    $('m-memory').textContent=n.metrics.stableConnections.toLocaleString('ru-RU');
    $('m-memory-strength').textContent=`${(n.metrics.meanConsolidation||0).toFixed(3)} средняя · ${n.metrics.memoryReactivations||0} эпизодов`;
    updateMemoryGroups(n.memoryGroups||[]);
    $('m-structures').textContent=String(n.metrics.structures);
    $('m-structure-births').textContent=`+${n.metrics.structureBirths} новых`;
    $('energy').textContent=`${Math.round(world.energy*100)}%`;$('energy-bar').style.width=`${world.energy*100}%`;$('food-collected').textContent=world.foodCollected;
    $('world-light').textContent=`${Math.round(world.daylight*100)}%`;
    $('world-temp').textContent=world.ambientTemperature.toFixed(2);
    $('world-visible').textContent=String(world.visibleObjects);
    $('world-collisions').textContent=String(world.collisions);
    $('world-hazards').textContent=String(world.hazardContacts);
    $('world-goal').textContent=world.course==='sandbox'?'—':world.goalDistance.toFixed(2);
    $('world-best').textContent=world.course==='sandbox'?'—':world.bestGoalDistance.toFixed(2);
    updateWorldSensors(world.sensors||[]);
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
$('courseMode').addEventListener('change',async e=>{
  await post('/api/control',{course:e.target.value});
  trail=[];lastWorld={x:0,y:0};poll();
});
function setting(id,key,digits){
  const el=$(id),out=$(id+'-v');el.addEventListener('input',()=>{out.value=Number(el.value).toFixed(digits)});
  el.addEventListener('change',()=>post('/api/control',{[key]:Number(el.value)}));
}
setting('resource-supply','resourceSupplyRate',3);
setting('reinforcement-rate','reinforcementRate',3);
$('metabolism-enabled').addEventListener('change',e=>post('/api/control',{metabolismEnabled:e.target.checked}));
$('reinforcement-enabled').addEventListener('change',e=>post('/api/control',{reinforcementEnabled:e.target.checked}));
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
  await post('/api/world/spawn',{x,y,kind:$('spawnMode').value});
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


const sensorLabels=['Пища слева','Пища спереди','Пища справа','Опасн. слева','Опасн. спереди','Опасн. справа','Укрытие слева','Укрытие спереди','Укрытие справа','Камень спереди','Нехватка энергии','Холод / жара'];
const sensorMeters=sensorLabels.map(label=>{
  const element=document.createElement('div');element.className='sensor-sample';
  const name=document.createElement('span');name.textContent=label;
  const bar=document.createElement('div');bar.className='sensor-meter';
  const fill=document.createElement('i');bar.append(fill);element.append(name,bar);
  $('world-sensors').append(element);return fill;
});
function updateWorldSensors(values){values.forEach((v,i)=>{if(sensorMeters[i])sensorMeters[i].style.width=`${Math.min(100,Math.max(0,v/3*100))}%`;});}
for(const [id,key,format] of [['vision-angle','fieldOfViewDegrees',v=>`${v}°`],['vision-range','visionRange',v=>Number(v).toFixed(2)]]){
  const slider=$(id),out=$(id+'-v');
  slider.addEventListener('input',()=>out.value=format(slider.value));
  slider.addEventListener('change',()=>post('/api/control',{[key]:Number(slider.value)}));
}
$('run-retention-experiment').addEventListener('click',async()=>{
  const button=$('run-retention-experiment'),status=$('retention-status'),target=$('retention-results');
  button.disabled=true;status.textContent='Выполняется независимый опыт. Текущий мир и сеть не затрагиваются…';
  try{
    const response=await fetch('/api/memory/retention',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({seed:1337,episodes:Number($('retention-episodes').value),delaySteps:Number($('retention-delay').value)})});
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    const data=await response.json();
    status.textContent=`Seed ${data.seed}, ${data.episodes} эпизодов, ${data.delaySteps} тиков отсрочки.`;
    const table=document.createElement('table');
    const row=document.createElement('tr');
    for(const label of ['Состояние','Знакомый отклик','Новый отклик','Различимость','Сдвиг от базовой реакции','Стабильные связи']){const th=document.createElement('th');th.textContent=label;row.append(th);}
    const head=document.createElement('thead');head.append(row);table.append(head);
    const body=document.createElement('tbody');
    for(const p of data.results){const tr=document.createElement('tr');for(const value of [p.phase,p.familiarResponse.toFixed(4),p.novelResponse.toFixed(4),p.patternSeparation.toFixed(4),p.familiarDriftFromBaseline.toFixed(4),String(p.stableConnections)]){const td=document.createElement('td');td.textContent=value;tr.append(td);}body.append(tr);}
    table.append(body);target.replaceChildren(table);
  }catch(error){status.textContent=`Ошибка опыта: ${error.message}`;}finally{button.disabled=false;}
});


// Spatial readout uses independent network instances; maps and traces are drawn
// from returned data rather than inferred from training success.
function drawSpatialTrial(canvas, trial) {
  const context=canvas.getContext('2d'), {w,h}=resizeCanvas(canvas,context);
  context.clearRect(0,0,w,h);
  drawGrid(context,w,h,28);
  const size=Math.min(w-16,h-18)/2, cx=w/2,cy=h/2;
  const pos=(x,y)=>[cx+x*size,cy+y*size];
  context.strokeStyle='#55708a';context.strokeRect(cx-size,cy-size,size*2,size*2);
  for(const object of trial.objects){
    const [x,y]=pos(object.x,object.y),r=Math.max(2,object.radius*size);
    context.beginPath();context.arc(x,y,r,0,2*Math.PI);
    context.fillStyle=object.kind==='rock'?'#768a9b':object.kind==='food'?'#63d8a9':'#6e9acc';context.fill();
  }
  context.beginPath();
  trial.trajectory.forEach((point,i)=>{
    const [x,y]=pos(point.x,point.y);
    if(i===0)context.moveTo(x,y);else context.lineTo(x,y);
  });
  context.strokeStyle='#f4b26c';context.lineWidth=2.4;context.stroke();
  if(trial.trajectory.length){
    const start=trial.trajectory[0],end=trial.trajectory.at(-1);
    for(const [point,color] of [[start,'#70d5e0'],[end,'#f4b26c']]){
      const [x,y]=pos(point.x,point.y);
      context.beginPath();context.arc(x,y,4,0,2*Math.PI);context.fillStyle=color;context.fill();
    }
  }
  context.font='10px system-ui';context.fillStyle='#9cb0c6';
  context.fillText(trial.course==='upper'?'Проход сверху':'Проход снизу',9,15);
}
$('run-spatial-experiment').addEventListener('click', async()=>{
  const button=$('run-spatial-experiment');button.disabled=true;
  $('spatial-status').textContent='Изолированный эксперимент выполняется. Живая симуляция продолжает работать…';
  try {
    const response=await fetch('/api/spatial/experiment',{
      method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
        seed:1337,
        episodes:Number($('spatial-episodes').value),
        episodeSteps:Number($('spatial-steps').value),
        evaluationSteps:Number($('spatial-evaluation').value)
      })
    });
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    const result=await response.json();
    const cards=[];
    for(const trial of result.trials){
      const card=document.createElement('article');card.className='spatial-card';
      const title=document.createElement('h3');title.textContent=trial.phase;
      const canvas=document.createElement('canvas');canvas.className='spatial-map';
      const info=document.createElement('div');info.className='spatial-numbers';
      info.textContent=`Пища: ${trial.foodCollected} · ближайшее расстояние: ${trial.closestGoalDistance.toFixed(2)} · столкновения: ${trial.collisions} · путь: ${trial.distanceTravelled.toFixed(2)} · энергия: ${(trial.energy*100).toFixed(0)}%`;
      card.append(title,canvas,info);cards.push(card);
    }
    $('spatial-results').replaceChildren(...cards);
    result.trials.forEach((trial,i)=>drawSpatialTrial(cards[i].querySelector('canvas'),trial));
    const best=result.trainingHistory.map(x=>x.closestGoalDistance.toFixed(2)).join(' / ');
    $('spatial-history').textContent=`Минимальное расстояние до цели по ${result.episodes} обучающим попыткам: ${best}. Меньшее значение — ближе к цели.`;
    $('spatial-status').textContent=`Seed ${result.seed} · ${result.episodes} обучающих попыток × ${result.episodeSteps} шагов · ${result.evaluationSteps} шагов проверки`;
  } catch(error){$('spatial-status').textContent=`Ошибка: ${error.message}`;}
  finally {button.disabled=false;}
});


// Bounded outer-loop genetic trial. This does not mutate the live network.
$('run-genetic-experiment').addEventListener('click', async () => {
  const button=$('run-genetic-experiment'), status=$('genetic-status'), output=$('genetic-results');
  button.disabled=true;status.textContent='Выполняется изолированный генетический отбор…';
  try {
    const response=await fetch('/api/genetic/experiment',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
      seed:1337,population:Number($('genetic-population').value),
      generations:Number($('genetic-generations').value),stepsPerTrial:Number($('genetic-steps').value),worldTrials:3
    })});
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    const experiment=await response.json();
    const lines=experiment.history.map(g=>`Поколение ${g.generation + 1}: максимум ${g.bestFitness.toFixed(3)}, среднее ${g.meanFitness.toFixed(3)}`);
    lines.push(`Лучший результат: ${experiment.winnerFitness.toFixed(3)} · исходный геном: ${experiment.baselineFitness.toFixed(3)}`);
    lines.push(`Параметры победителя: ${JSON.stringify(experiment.winner)}`);
    output.textContent=lines.join(' | ');
    status.textContent=experiment.interpretation;
  }catch(error){status.textContent=`Ошибка генетического опыта: ${error.message}`;}
  finally{button.disabled=false;}
});
