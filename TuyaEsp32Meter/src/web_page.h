/* ============================================================
 *  web_page.h — pagina unica, self-contained (sem CDN: precisa
 *  funcionar no modo AP, sem internet). Fala so com /api/*.
 * ============================================================ */
#pragma once
#include <pgmspace.h>

static const char kWebPage[] PROGMEM = R"HTML(<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>TuyaMeter</title>
<style>
:root{
  --paper:#f3efe6;--card:#fbf9f4;--ink:#1b1a17;--muted:#6f6a5f;--rule:#d9d2c3;
  --signal:#d98a00;--signal-ink:#7a4d00;--ok:#2f7d4f;--bad:#b3372b;
  --a:#1f5f8b;--b:#8b3a62;
  --mono:ui-monospace,"Cascadia Mono","SF Mono",Consolas,monospace;
  --sans:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;
  --r:3px;
}
@media (prefers-color-scheme:dark){:root{
  --paper:#141412;--card:#1c1b18;--ink:#ece6d6;--muted:#9a9384;--rule:#34312b;
  --signal:#ffb020;--signal-ink:#ffcf70;--ok:#58c287;--bad:#ff6b5b;--a:#6fb3e0;--b:#e08ab3;
}}
*{box-sizing:border-box}
body{margin:0;background:var(--paper);color:var(--ink);font:15px/1.45 var(--sans);
  background-image:repeating-linear-gradient(0deg,transparent 0 23px,color-mix(in srgb,var(--rule) 35%,transparent) 23px 24px)}
header{display:flex;flex-wrap:wrap;gap:12px 24px;align-items:flex-end;justify-content:space-between;
  padding:28px clamp(16px,4vw,48px) 18px;border-bottom:2px solid var(--ink)}
.brand{font:700 13px/1 var(--mono);letter-spacing:.32em;text-transform:uppercase}
.brand b{display:block;font:800 clamp(30px,5vw,44px)/1 var(--sans);letter-spacing:-.03em;text-transform:none;margin-top:6px}
.brand b span{color:var(--signal)}
.chips{display:flex;flex-wrap:wrap;gap:8px}
.chip{font:12px/1 var(--mono);padding:7px 10px;border:1px solid var(--rule);border-radius:var(--r);background:var(--card);
  display:flex;gap:7px;align-items:center;white-space:nowrap}
.dot{width:8px;height:8px;border-radius:50%;background:var(--muted)}
.dot.on{background:var(--ok);box-shadow:0 0 0 3px color-mix(in srgb,var(--ok) 25%,transparent)}
.dot.off{background:var(--bad)}
main{padding:24px clamp(16px,4vw,48px) 64px;max-width:1280px}
.bar{display:flex;justify-content:space-between;align-items:center;gap:12px;margin:8px 0 18px}
.bar h2{font:600 12px/1 var(--mono);letter-spacing:.24em;text-transform:uppercase;color:var(--muted);margin:0}
button{font:600 13px/1 var(--sans);padding:10px 14px;border-radius:var(--r);border:1.5px solid var(--ink);
  background:var(--card);color:var(--ink);cursor:pointer;transition:transform .12s,box-shadow .12s,background .12s}
button:hover{box-shadow:3px 3px 0 var(--ink);transform:translate(-1px,-1px)}
button:active{box-shadow:none;transform:translate(1px,1px)}
button:focus-visible,input:focus-visible,select:focus-visible{outline:3px solid var(--signal);outline-offset:2px}
button.primary{background:var(--signal);border-color:var(--signal-ink);color:#1b1a17}
button.ghost{border-color:transparent;background:transparent;padding:8px 10px}
button.ghost:hover{box-shadow:none;background:color-mix(in srgb,var(--ink) 8%,transparent);transform:none}
button.danger{color:var(--bad);border-color:var(--bad)}
button:disabled{opacity:.5;cursor:wait}
.meter{background:var(--card);border:1.5px solid var(--ink);border-radius:var(--r);margin-bottom:22px;
  box-shadow:6px 6px 0 color-mix(in srgb,var(--ink) 14%,transparent);overflow:hidden}
.meter-head{display:flex;flex-wrap:wrap;gap:8px 16px;justify-content:space-between;align-items:center;
  padding:14px 18px;border-bottom:1px solid var(--rule)}
.meter-head h3{margin:0;font:700 18px/1.2 var(--sans)}
.meter-head .meta{font:12px/1.4 var(--mono);color:var(--muted);overflow-wrap:anywhere}
.meter-head>div:first-child,.meter-body>*{min-width:0}
.meter-body{display:grid;grid-template-columns:minmax(220px,1.1fr) repeat(var(--cols,2),1fr);gap:0}
.hero{padding:20px 18px;border-right:1px solid var(--rule);display:flex;flex-direction:column;gap:14px}
.hero .label{font:600 11px/1 var(--mono);letter-spacing:.2em;text-transform:uppercase;color:var(--muted)}
.big{font:700 clamp(44px,6vw,64px)/1 var(--mono);font-variant-numeric:tabular-nums;letter-spacing:-.04em}
.big small{font-size:.38em;color:var(--muted);margin-left:4px;letter-spacing:0}
.mini{display:grid;grid-template-columns:1fr 1fr;gap:10px 16px}
.ch{padding:16px 18px;border-right:1px solid var(--rule)}
.ch:last-child{border-right:0}
.ch h4{margin:0 0 10px;font:700 12px/1 var(--mono);letter-spacing:.2em;display:flex;align-items:center;gap:8px}
.ch h4::before{content:"";width:14px;height:4px;background:currentColor}
.ch.A h4{color:var(--a)}.ch.B h4{color:var(--b)}.ch.S h4{color:var(--signal-ink)}
.row{display:flex;justify-content:space-between;gap:10px;padding:6px 0;border-bottom:1px dashed var(--rule);font-size:13px}
.row:last-child{border-bottom:0}
.row span:first-child{color:var(--muted)}
.v{font:600 14px/1.3 var(--mono);font-variant-numeric:tabular-nums;text-align:right}
.v u{text-decoration:none;color:var(--muted);font-weight:400;margin-left:3px}
.err{margin:0;padding:10px 18px;font:12px/1.4 var(--mono);color:var(--bad);border-top:1px solid var(--rule);
  background:color-mix(in srgb,var(--bad) 7%,transparent)}
.empty{border:1.5px dashed var(--muted);border-radius:var(--r);padding:40px 24px;text-align:center;color:var(--muted)}
.empty strong{display:block;color:var(--ink);font-size:18px;margin-bottom:6px}
details.panel{margin-top:36px;border-top:2px solid var(--ink);padding-top:12px}
details.panel summary{cursor:pointer;font:600 12px/1 var(--mono);letter-spacing:.24em;text-transform:uppercase;color:var(--muted);padding:8px 0}
form .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:14px 18px;margin:14px 0}
label{display:flex;flex-direction:column;gap:6px;font:600 12px/1.2 var(--sans);color:var(--muted)}
label.check{flex-direction:row;align-items:center;gap:8px;color:var(--ink)}
input,select{font:14px/1.2 var(--mono);padding:9px 10px;border:1.5px solid var(--rule);border-radius:var(--r);
  background:var(--paper);color:var(--ink);min-width:0}
input:focus,select:focus{border-color:var(--ink)}
.hint{font:400 11px/1.3 var(--sans);color:var(--muted)}
.actions{display:flex;flex-wrap:wrap;gap:10px;align-items:center}
dialog{width:min(720px,calc(100vw - 32px));border:1.5px solid var(--ink);border-radius:var(--r);padding:0;
  background:var(--card);color:var(--ink);box-shadow:10px 10px 0 color-mix(in srgb,var(--ink) 25%,transparent)}
dialog::backdrop{background:color-mix(in srgb,#000 45%,transparent)}
dialog header{padding:16px 20px;border-bottom:1px solid var(--rule)}
dialog form{padding:4px 20px 20px}
.probe{margin-top:14px;border:1px solid var(--rule);border-radius:var(--r);padding:12px;font:12px/1.5 var(--mono);
  max-height:260px;overflow:auto;background:var(--paper)}
.probe.ok{border-color:var(--ok)}.probe.fail{border-color:var(--bad);color:var(--bad)}
.probe table{border-collapse:collapse;width:100%}
.probe td{padding:3px 6px;border-bottom:1px dashed var(--rule)}
.probe td:last-child{text-align:right}
.toast{position:fixed;right:16px;bottom:16px;max-width:calc(100vw - 32px);background:var(--ink);color:var(--paper);
  font:13px/1.4 var(--sans);padding:12px 16px;border-radius:var(--r);transform:translateY(140%);transition:transform .25s}
.toast.show{transform:none}
@media (max-width:860px){.meter-body{grid-template-columns:1fr 1fr}.hero{grid-column:1/-1;border-right:0;border-bottom:1px solid var(--rule)}}
@media (max-width:520px){.meter-body{grid-template-columns:1fr}.ch{border-right:0;border-bottom:1px solid var(--rule)}}
@media (prefers-reduced-motion:reduce){*{transition:none!important}}
</style>
</head>
<body>
<header>
  <div class="brand">Tuya LAN → MQTT<b>Tuya<span>Meter</span></b></div>
  <div class="chips" id="chips"></div>
</header>
<main>
  <div class="bar"><h2>Devices</h2><button class="primary" id="add">+ Adicionar device</button></div>
  <section id="devices" aria-live="polite"></section>

  <details class="panel" id="settingsPanel">
    <summary>Configurações</summary>
    <form id="settings">
      <div class="grid">
        <label>Wi-Fi SSID<input name="wifiSsid" maxlength="32"></label>
        <label>Senha do Wi-Fi<input name="wifiPass" type="password" maxlength="63" placeholder="(mantém a atual)"></label>
        <label>Broker MQTT<input name="mqttHost" placeholder="192.168.0.10"></label>
        <label>Porta<input name="mqttPort" type="number" min="1" max="65535"></label>
        <label>Usuário MQTT<input name="mqttUser"></label>
        <label>Senha MQTT<input name="mqttPass" type="password" placeholder="(mantém a atual)"></label>
        <label>Tópico base<input name="baseTopic" pattern="[A-Za-z0-9_\-]{1,32}"></label>
        <label>Prefixo discovery<input name="discoveryPrefix" pattern="[A-Za-z0-9_\-]{1,32}"></label>
        <label>Leitura a cada (s)<input name="pollSeconds" type="number" min="5" max="3600"></label>
        <label>Senha da página<input name="adminPass" type="password" placeholder="(mantém a atual)">
          <span class="hint">Usuário <b>admin</b>. Vazia = sem senha.</span></label>
        <label>Senha do AP de resgate<input name="apPass" readonly>
          <span class="hint">Anote: o AP abre se o Wi-Fi cair por 20s.</span></label>
      </div>
      <div class="actions">
        <button class="primary" type="submit">Salvar e reiniciar</button>
        <label class="check"><input type="checkbox" name="adminPassClear"> remover senha da página</label>
        <span style="flex:1"></span>
        <button type="button" id="restart">Reiniciar</button>
        <button type="button" class="danger" id="reset">Apagar configurações</button>
      </div>
      <div class="actions" style="margin-top:18px">
        <label>Firmware (.bin)<input type="file" id="fwFile" accept=".bin"></label>
        <button type="button" id="fwSend">Atualizar firmware</button>
      </div>
    </form>
  </details>
</main>

<dialog id="dlg" aria-labelledby="dlgTitle">
  <header><div class="brand" id="dlgTitle">Device</div></header>
  <form id="devForm" method="dialog">
    <input type="hidden" name="originalId">
    <div class="grid">
      <label>Nome<input name="name" required maxlength="48" placeholder="Medidor do quadro"></label>
      <label>Perfil<select name="profile" id="profileSel"></select></label>
      <label>Device ID<input name="id" required maxlength="32" pattern="[A-Za-z0-9]+" placeholder="ebd4a0…"></label>
      <label>IP<input name="ip" required placeholder="192.168.0.50" inputmode="decimal"></label>
      <label>Local key<input name="localKey" type="password" maxlength="16" autocomplete="off">
        <span class="hint" id="keyHint">16 caracteres, do Tuya IoT / tinytuya wizard.</span></label>
      <label>Protocolo<select name="protocol"><option>3.5</option><option>3.4</option><option>3.3</option></select></label>
    </div>
    <label class="check"><input type="checkbox" name="enabled" checked> Ler este device</label>
    <div class="probe" id="probe" hidden></div>
    <div class="actions" style="margin-top:16px">
      <button type="button" id="test">Testar conexão</button>
      <span style="flex:1"></span>
      <button type="button" class="ghost" id="cancel">Cancelar</button>
      <button type="submit" class="primary" id="save">Salvar</button>
    </div>
  </form>
</dialog>
<div class="toast" id="toast" role="status"></div>

<script>
const $=s=>document.querySelector(s);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let last=null, timer=null;

function toast(msg){const t=$('#toast');t.textContent=msg;t.classList.add('show');clearTimeout(t._h);t._h=setTimeout(()=>t.classList.remove('show'),3500)}

async function api(path,opts={}){
  const r=await fetch(path,{headers:{'Content-Type':'application/json','X-Requested-With':'1'},...opts});
  const j=await r.json().catch(()=>({ok:false,error:'resposta inválida ('+r.status+')'}));
  if(!r.ok&&j.ok===undefined)j.ok=false;
  return j;
}

function ago(s){if(s==null)return'nunca';if(s<60)return s+'s';if(s<3600)return Math.floor(s/60)+'min';return Math.floor(s/3600)+'h'}
function up(s){const d=Math.floor(s/86400),h=Math.floor(s%86400/3600),m=Math.floor(s%3600/60);return(d?d+'d ':'')+h+'h '+m+'m'}

function chips(d){
  const w=d.wifi,m=d.mqtt;
  $('#chips').innerHTML=
    `<span class="chip"><i class="dot ${w.connected?'on':'off'}"></i>${w.connected?esc(w.ssid)+' · '+esc(w.ip)+' · '+w.rssi+'dBm':'Wi-Fi desconectado'}</span>`+
    (w.apActive?`<span class="chip"><i class="dot on"></i>AP ${esc(w.apName)} · ${esc(w.apIp)}</span>`:'')+
    `<span class="chip"><i class="dot ${m.connected?'on':'off'}"></i>MQTT ${esc(m.host||'—')}${m.connected?'':' (state '+m.state+')'}</span>`+
    `<span class="chip">↑ ${m.published} msgs</span>`+
    `<span class="chip">up ${up(d.uptimeSec)} · ${Math.round(d.freeHeap/1024)}KB · v${esc(d.fw)}</span>`;
}

const GROUP_TITLES={A:'CANAL A',B:'CANAL B',S:'ESTADO'};
function shown(ent,v){return ent.component==='binary_sensor'?(v?'ON':'OFF'):v}
function fmt(ent,state){
  const v=state?.[ent.code];
  if(v==null)return'<span class="v">—</span>';
  return`<span class="v">${esc(shown(ent,v))}${ent.unit?`<u>${esc(ent.unit)}</u>`:''}</span>`;
}

function meterCard(dev,prof){
  const st=dev.status||{},s=st.state||{},ents=prof?.entities||[];
  const g=k=>ents.filter(e=>e.group===k);
  const hero=g('T'),heroMain=hero[0];
  const online=st.online,never=st.lastOkAgoSec==null;
  const statusTxt=!dev.enabled?'desativado':online?'online':never?'aguardando':'offline';
  const rows=list=>list.map(e=>`<div class="row"><span>${esc(e.name.replace(/ [AB]$/,''))}</span>${fmt(e,s)}</div>`).join('');
  const cols=[...new Set(ents.map(e=>e.group))].filter(k=>k!=='T');
  return`<article class="meter">
    <div class="meter-head">
      <div><h3>${esc(dev.name)}</h3><div class="meta">${esc(dev.id)} · ${esc(dev.ip)} · v${esc(dev.protocol)} · ${esc(prof?.label||dev.profile)}</div></div>
      <div class="actions">
        <span class="chip"><i class="dot ${online&&dev.enabled?'on':never&&dev.enabled?'':'off'}"></i>${statusTxt} · leitura há ${ago(st.lastOkAgoSec)}</span>
        <button class="ghost" data-edit="${esc(dev.id)}">Editar</button>
        <button class="ghost" data-del="${esc(dev.id)}">Remover</button>
      </div>
    </div>
    <div class="meter-body" style="--cols:${cols.length}">
      <div class="hero">
        <div><div class="label">${esc(heroMain?.name||'')}</div>
          <div class="big">${esc(s[heroMain?.code]??'—')}<small>${esc(heroMain?.unit||'')}</small></div></div>
        <div class="mini">${hero.slice(1).map(e=>`<div><div class="label">${esc(e.name)}</div>${fmt(e,s)}</div>`).join('')}</div>
      </div>
      ${cols.map(k=>`<div class="ch ${esc(k)}"><h4>${esc(GROUP_TITLES[k]||k)}</h4>${rows(g(k))}</div>`).join('')}
    </div>
    ${st.lastError&&!online?`<p class="err">${esc(st.lastError)}${st.failures?' · '+st.failures+' falha(s) seguida(s)':''}</p>`:''}
  </article>`;
}

function render(d){
  last=d;chips(d);
  const box=$('#devices');
  if(!d.devices.length){
    box.innerHTML=`<div class="empty"><strong>Nenhum device cadastrado</strong>Use “Adicionar device” com o ID, IP e local key do medidor.</div>`;
    return;
  }
  box.innerHTML=d.devices.map(dev=>meterCard(dev,d.profiles.find(p=>p.id===dev.profile))).join('');
}

async function refresh(){
  try{render(await api('/api/status'))}catch(e){$('#chips').innerHTML='<span class="chip"><i class="dot off"></i>sem contato com o ESP32</span>'}
}

/* ---------- dialog de device ---------- */
const form=$('#devForm');
function formData(){
  const f=Object.fromEntries(new FormData(form));
  f.enabled=form.enabled.checked;
  return f;
}
function openDialog(dev){
  form.reset();$('#probe').hidden=true;
  $('#profileSel').innerHTML=last.profiles.map(p=>`<option value="${esc(p.id)}">${esc(p.label)}</option>`).join('');
  $('#dlgTitle').textContent=dev?'Editar device':'Novo device';
  form.originalId.value=dev?.id||'';
  if(dev){for(const k of['name','id','ip','profile','protocol'])form[k].value=dev[k];form.enabled.checked=dev.enabled}
  else form.protocol.value=last.profiles[0]?.defaultProtocol||'3.5';
  form.localKey.required=!dev;
  form.localKey.placeholder=dev?.hasKey?'(mantém a atual)':'';
  $('#dlg').showModal();
}
function busy(btn,on,txt){btn.disabled=on;if(txt)btn.textContent=txt}

$('#add').onclick=()=>openDialog(null);
$('#cancel').onclick=()=>$('#dlg').close();
$('#devices').onclick=async e=>{
  const ed=e.target.dataset.edit,del=e.target.dataset.del;
  if(ed)openDialog(last.devices.find(d=>d.id===ed));
  if(del&&confirm(`Remover ${del}? As entidades somem do Home Assistant.`)){
    const r=await api('/api/devices?id='+encodeURIComponent(del),{method:'DELETE'});
    toast(r.ok?'Device removido':r.error);refresh();
  }
};

$('#test').onclick=async()=>{
  const b=$('#test'),p=$('#probe');
  busy(b,true,'Testando…');p.hidden=false;p.className='probe';p.textContent='Conectando ao device (até ~15s)…';
  const r=await api('/api/devices/test',{method:'POST',body:JSON.stringify(formData())});
  busy(b,false,'Testar conexão');
  if(!r.ok){p.className='probe fail';p.textContent='✗ '+(r.error||'falhou');return}
  const prof=last.profiles.find(x=>x.id===form.profile.value);
  const rows=(prof?.entities||[]).map(e=>`<tr><td>${esc(e.name)}</td><td>dp ${e.dp}</td><td>${esc(r.state?.[e.code]==null?'—':shown(e,r.state[e.code]))} ${esc(e.unit||'')}</td></tr>`).join('');
  p.className='probe ok';
  p.innerHTML=`✓ respondeu em ${r.durationMs}ms · ${Object.keys(r.dps||{}).length} DPs<table>${rows}</table>`;
};

form.onsubmit=async e=>{
  e.preventDefault();
  const b=$('#save');busy(b,true,'Salvando…');
  const r=await api('/api/devices',{method:'POST',body:JSON.stringify(formData())});
  busy(b,false,'Salvar');
  if(!r.ok){toast(r.error);return}
  $('#dlg').close();toast('Device salvo — primeira leitura em instantes');refresh();
};

/* ---------- settings ---------- */
const sf=$('#settings');
$('#settingsPanel').ontoggle=async e=>{
  if(!e.target.open)return;
  const s=await api('/api/settings');
  for(const k of['wifiSsid','mqttHost','mqttPort','mqttUser','baseTopic','discoveryPrefix','pollSeconds','apPass'])sf[k].value=s[k]??'';
  sf.adminPassClear.checked=false;
};
sf.onsubmit=async e=>{
  e.preventDefault();
  const f=Object.fromEntries(new FormData(sf));delete f.apPass;
  f.mqttPort=+f.mqttPort;f.pollSeconds=+f.pollSeconds;f.adminPassClear=sf.adminPassClear.checked;
  const r=await api('/api/settings',{method:'POST',body:JSON.stringify(f)});
  toast(r.ok?'Salvo. Reiniciando — se mudou a rede, procure o novo IP.':r.error);
};
$('#fwSend').onclick=async()=>{
  const f=$('#fwFile').files[0],b=$('#fwSend');
  if(!f){toast('Escolha o firmware.bin (em .pio/build/esp32dev/)');return}
  if(!confirm(`Gravar ${f.name} (${Math.round(f.size/1024)} KB) e reiniciar?`))return;
  const fd=new FormData();fd.append('firmware',f,f.name);
  busy(b,true,'Enviando…');
  const r=await fetch('/api/firmware',{method:'POST',headers:{'X-Requested-With':'1'},body:fd})
    .then(x=>x.json()).catch(()=>({ok:false,error:'falha no envio'}));
  busy(b,false,'Atualizar firmware');
  toast(r.ok?'Firmware gravado. Reiniciando…':r.error);
};
$('#restart').onclick=async()=>{await api('/api/restart',{method:'POST'});toast('Reiniciando…')};
$('#reset').onclick=async()=>{
  if(!confirm('Apagar Wi-Fi, MQTT e TODOS os devices? O ESP32 volta aos valores de fábrica.'))return;
  await api('/api/factory-reset',{method:'POST'});toast('Configurações apagadas. Reiniciando…');
};

refresh();timer=setInterval(()=>{if(!$('#dlg').open)refresh()},3000);
</script>
</body>
</html>)HTML";
