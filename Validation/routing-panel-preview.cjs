'use strict';
// Serves only the embedded UI and an entirely synthetic MCP Apps parent.
// It never starts Codex/MCP, reads account files, stores credentials or calls a real tool.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const vm = require('node:vm');

const panelPath = path.resolve(__dirname, '..', 'Plugin', 'ui', 'accounts.html');
const translations=JSON.parse(fs.readFileSync(path.join(path.dirname(panelPath),'translations.json'),'utf8'));
const panel = fs.readFileSync(panelPath, 'utf8').replace('/*__ACCOUNT_TRANSLATIONS__*/{}',JSON.stringify(translations).replace(/</g,'\\u003c'));
const host = `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-src 'self'; connect-src 'none'; img-src data:; object-src 'none'; base-uri 'none'; form-action 'none'">
<title>合成订阅面板 · 无真实账号</title><style>
body{margin:0;background:#e8eee9;color:#18382a;font:14px 'Segoe UI','Microsoft YaHei',sans-serif}
.fixture{max-width:1020px;margin:auto;padding:18px 20px 32px}.banner{background:#fff2cc;border:1px solid #dcc77e;border-radius:12px;padding:14px 18px;line-height:1.65}
.banner strong{font-size:17px}.banner p{margin:4px 0 0}.controls{display:flex;gap:9px;margin:12px 0}
.controls{flex-wrap:wrap}.controls button{border:1px solid #b8cabf;background:white;color:#214b39;border-radius:8px;padding:8px 12px;cursor:pointer}
iframe{width:100%;height:840px;border:1px solid #cedad1;border-radius:15px;background:white;display:block}
.events{margin-top:12px;padding:13px 16px;border:1px solid #c9d8ce;border-radius:12px;background:#f9fcfa}.events h2{font-size:13px;margin:0 0 8px}
pre{font:12px/1.6 'Segoe UI','Microsoft YaHei',sans-serif;white-space:pre-wrap;word-break:break-word;margin:0}
</style></head><body><main class="fixture">
<div class="banner"><strong>合成数据预览 · 无真实账号</strong><p>仅验证面板展示与点击回调。没有连接 Codex、账号库或真实订阅；点击不会执行登录、账号切换或模型请求。</p></div>
<div class="controls"><button id="fixture-idle">合成状态：已接入</button><button id="fixture-native">原生输入已接入（合成）</button><button id="fixture-rejected">合成状态：账号被拒绝</button><button id="fixture-missing">合成状态：未接入</button><button id="fixture-starting">合成状态：启动中</button><button id="fixture-failed">合成状态：启动失败</button><button id="fixture-busy">合成状态：忙碌</button><button id="fixture-dark">合成主题：深色</button><button id="fixture-light">合成主题：浅色</button></div>
<iframe id="panel" title="合成 MCP Apps 订阅账号面板" src="/panel"></iframe>
<section class="events"><h2>合成宿主回调记录（仅内存）</h2><pre id="event-log">等待面板握手</pre></section>
</main><script>
(() => {
  const frame = document.getElementById('panel');
  const log = document.getElementById('event-log');
  const accounts = [
    {id:'a'.repeat(64),label:'合成 · 工作订阅',maskedEmail:'wo***@example.test',plan:'Pro'},
    {id:'b'.repeat(64),label:'合成 · 个人订阅',maskedEmail:'pe***@example.test',plan:'Plus'},
    {id:'c'.repeat(64),label:'合成 · 备用订阅',maskedEmail:'ba***@example.test',plan:'Pro'}
  ];
  const calls = [];
  let selected = accounts[0].id, busy = false, theme = 'light', routing = true, startupState = '', routingFailure = null;
  let syntheticResetsUsed = 0;
  let syntheticRemoved=null,syntheticRemoval=null;
  let syntheticAutoRotation=false, syntheticDesktopSwitch=null,syntheticNativeInput=null;
  let syntheticLanguage=null;
  const syntheticCards = [{id:'synthetic-reset-1',status:'available',resetType:'codex_rate_limits',expiresAtUtc:'2026-11-22T10:00:00Z',expiryKnown:true},
    {id:'synthetic-reset-2',status:'available',resetType:'codex_rate_limits',expiresAtUtc:null,expiryKnown:true}];
  function view() {
    const current = accounts.find(account => account.id === selected);
    return {
      subscriptionRouting:routing,
      languagePreference:syntheticLanguage,
      desktopSwitchSupported:true, desktopSwitch:syntheticDesktopSwitch,
      startupIntegration:{state:'active',message:'Synthetic installed startup component.'},
      routingStartup:startupState?{stage:'synthetic-preview',state:startupState,message:startupState==='failed'?'合成启动失败说明；没有启动任何真实程序。':'合成启动过程；没有连接真实服务。',updatedAtUtc:'2026-10-12T01:30:00Z'}:null,
      accounts:accounts.map((account,index) => ({...account,planSource:'server',isCurrent:account.id===selected,isDesktop:account.id==='a'.repeat(64),
        resetCredits:{status:index===2?'unavailable':'ok',stale:index===2,availableCount:index===0?Math.max(0,2-syntheticResetsUsed):index===1?0:null,
          cards:index===0?syntheticCards.slice(syntheticResetsUsed):index===1?[]:null,updatedAtUtc:'2026-10-22T02:20:00Z',message:'合成数据：查询暂不可用。'},
        usage:{status:index===2?'reauth-required':'ok',stale:index===2,
          updatedAtUtc:'2026-10-22T02:20:00Z',message:index===2?'登录需要更新；下列为上次查询数据。':'官方用量已更新。',
          primary:{usedPercent:index===0?24:index===1?83:null,limitWindowSeconds:18000,resetsAtUtc:'2026-10-22T06:00:00Z'},
          secondary:{usedPercent:index===0?42:index===1?100:null,limitWindowSeconds:604800,resetsAtUtc:'2026-10-26T06:00:00Z'}}})),
      current:{maskedEmail:current.maskedEmail,plan:current.plan,planSource:'server'},
      lastRemoved:syntheticRemoval,
      desktop:{maskedEmail:'wo***@example.test',plan:'Pro'},
      pending:routing?null:{id:'synthetic-previous-request',state:'failed',label:'合成 · 个人订阅',createdAtUtc:'2026-10-11T03:15:00Z',message:'合成旧记录：后台等待程序已退出，未能确认自动切换。'},
      login:routing?null:{id:'synthetic-login',state:'completed',message:'合成账号保存记录，不包含真实凭据。'},
      live:{enabled:routing,connected:routing,busy,nativeConnectionObserved:routing,failure:routingFailure,verificationState:'unverified',lastCompletedAccountId:null,message:'合成连接：没有真实模型请求。',removalSupported:true,
        autoRotationSupported:true,autoRotationEnabled:syntheticAutoRotation,nativeInput:syntheticNativeInput},
      note:'合成预览：已选择仅表示内存标签改变；没有验证任何真实订阅。'
    };
  }
  function writeLog() {
    log.textContent = '合成状态：' + (busy?'忙碌':'空闲') + '\\n已选择：' + accounts.find(account=>account.id===selected).label +
      '\\n桌面身份仍为：wo***@example.test（合成）\\n' +
      (calls.length?calls.map(call=>'回调：'+call.name+' · '+(call.accountLabel||'只读/备注操作')).join('\\n'):'尚未点击账号操作');
    log.dataset.selectCalls = String(calls.filter(call=>call.name==='accounts_select_subscription').length);
  }
  function send(message) { frame.contentWindow.postMessage({jsonrpc:'2.0',...message},'*'); }
  function result() { return {content:[{type:'text',text:'合成结果，无真实账号操作。'}],structuredContent:view()}; }
  function publish() { send({method:'ui/notifications/tool-result',params:result()});writeLog(); }
  window.addEventListener('message',event=>{
    if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;
    const packet=event.data;
    if(packet.method==='ui/initialize'){
      send({id:packet.id,result:{protocolVersion:'2026-01-26',hostInfo:{name:'synthetic-only-parent',version:'1'},hostCapabilities:{serverTools:{}},hostContext:{theme,displayMode:'fullscreen'}}});
      writeLog();return;
    }
    if(packet.method==='ui/notifications/size-changed'){
      const height=packet.params?.height;if(Number.isFinite(height)&&height>0&&height<6000)frame.style.height=(height+8)+'px';return;
    }
    if(packet.method!=='tools/call')return;
    const name=packet.params?.name,args=packet.params?.arguments||{};
    const account=accounts.find(account=>account.id===args.account_id);
    calls.push({name,accountLabel:account?.label||null});writeLog();
    if(name==='accounts_set_language'&&['zh-CN','zh-TW','en','ja','ko','es','fr','de','pt-BR','ru'].includes(args.language)){
      syntheticLanguage=args.language;send({id:packet.id,result:{structuredContent:{languagePreference:syntheticLanguage}}});return;
    }
    if(name==='accounts_list'||name==='accounts_panel'||name==='accounts_routing_status'||name==='accounts_live_status'||name==='accounts_refresh_usage'){
      send({id:packet.id,result:result()});return;
    }
    if(name==='accounts_profile_details'){
      send({id:packet.id,result:{structuredContent:{profiles:accounts.slice(0,2).map((account,index)=>({accountId:account.id,displayName:index===0?'合成 · 开发者':'合成 · 个人用户',username:index===0?'synthetic-developer':null,source:'login-cache'}))}}});return;
    }
    if(name==='accounts_request_desktop_switch' && account && args.confirmed===true){
      syntheticDesktopSwitch={id:'3'.repeat(32),accountId:account.id,label:account.label,state:'queued',message:'合成登记，不执行任何真实切换。',expiresAtUtc:'2026-12-01T00:00:00Z'};
      send({id:packet.id,result:result()});return;
    }
    if(name==='accounts_cancel_desktop_switch' && args.request_id===syntheticDesktopSwitch?.id){
      syntheticDesktopSwitch={...syntheticDesktopSwitch,state:'cancelled'};send({id:packet.id,result:result()});return;
    }
    if(name==='accounts_set_auto_rotation' && typeof args.enabled==='boolean'){
      syntheticAutoRotation=args.enabled;send({id:packet.id,result:result()});return;
    }
    if(name==='accounts_prepare_reset' && account && syntheticCards.some(card=>card.id===args.credit_id)){
      send({id:packet.id,result:{isError:false,structuredContent:{resetPreview:{accountId:account.id,confirmationId:'1'.repeat(32),
        card:syntheticCards.find(card=>card.id===args.credit_id),confirmBeforeUtc:'2026-12-01T00:00:00Z',recovering:false,
        message:'合成确认：示范使用一张重置卡。没有连接真实账号，不会消耗真实卡片。'}}}});return;
    }
    if(name==='accounts_use_reset' && account && args.confirmation_id==='1'.repeat(32) && args.confirmed===true){
      syntheticResetsUsed++;const answer=result();answer.structuredContent.resetResult={code:'reset',confirmed:true,message:'合成使用完成；没有消耗真实重置卡。'};
      send({id:packet.id,result:answer});return;
    }
    if(name==='accounts_select_subscription'){
      if(!routing||busy||!account||Object.keys(args).some(key=>key!=='account_id')){
        send({id:packet.id,result:{isError:true,content:[{type:'text',text:'合成拒绝：忙碌或参数不符；没有真实操作。'}]}});return;
      }
      selected=account.id;send({id:packet.id,result:result()});writeLog();return;
    }
    if(name==='accounts_remove' && account && account.id!==selected && !busy && args.confirmed===true){
      syntheticRemoved=account;accounts.splice(accounts.indexOf(account),1);syntheticRemoval={removalId:'2'.repeat(32),label:account.label};
      send({id:packet.id,result:result()});return;
    }
    if(name==='accounts_restore_removed' && syntheticRemoved && args.removal_id===syntheticRemoval?.removalId){
      accounts.push(syntheticRemoved);syntheticRemoved=null;syntheticRemoval=null;send({id:packet.id,result:result()});return;
    }
    if(name==='accounts_rename'&&account&&typeof args.label==='string'){
      account.label=args.label.slice(0,60);send({id:packet.id,result:result()});writeLog();return;
    }
    send({id:packet.id,result:{isError:true,content:[{type:'text',text:'合成宿主禁止登录、保存凭据或真实切换。'}]}});
  });
  document.getElementById('fixture-idle').addEventListener('click',()=>{routing=true;busy=false;startupState='';routingFailure=null;publish();});
  document.getElementById('fixture-native').addEventListener('click',()=>{routing=true;busy=false;startupState='';syntheticNativeInput={supported:true,connected:true,frontendAdapted:true,initialQuotaAdapted:true,workQuotaAdapted:true,quotaAvailable:true,state:'adapted',updatedAtUtc:new Date().toISOString()};publish();});
  document.getElementById('fixture-rejected').addEventListener('click',()=>{routing=true;busy=false;startupState='';routingFailure={code:'selected-rejected',message:'所选账号被官方认证拒绝。连接空闲后可选择另一个账号，或重新通过官方登录添加该账号。桌面登录未改动。',httpStatus:401};publish();});
  document.getElementById('fixture-missing').addEventListener('click',()=>{routing=false;busy=false;startupState='';publish();});
  document.getElementById('fixture-starting').addEventListener('click',()=>{routing=false;busy=false;startupState='starting';publish();});
  document.getElementById('fixture-failed').addEventListener('click',()=>{routing=false;busy=false;startupState='failed';publish();});
  document.getElementById('fixture-busy').addEventListener('click',()=>{busy=true;publish();});
  function setTheme(next){theme=next;send({method:'ui/notifications/host-context-changed',params:{theme}});}
  document.getElementById('fixture-dark').addEventListener('click',()=>setTheme('dark'));
  document.getElementById('fixture-light').addEventListener('click',()=>setTheme('light'));
  writeLog();
})();
</script></body></html>`;

async function checkIsolatedDom() {
  const checks = [];
  const assert = (ok, label) => {
    if (!ok) throw new Error('Synthetic UI assertion failed: ' + label);
    checks.push(label);
  };
  class Element {
    constructor(id = '') {
      this.id = id; this.children = []; this.handlers = new Map(); this.disabled = false;
      this.hidden = false; this.checked = false; this.textContent = ''; this.value = ''; this.className = '';
      this.parentElement = {hidden:false}; this.dataset = {}; this.style = {setProperty(){}};
      this.classes = new Set();
      this.classList = {toggle:(name,enabled)=>enabled?this.classes.add(name):this.classes.delete(name)};
    }
    addEventListener(type, handler) { this.handlers.set(type, handler); }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute() {}
    focus() {}
    getBoundingClientRect() { return {height:800}; }
    click() { return this.disabled ? undefined : this.handlers.get('click')?.({preventDefault(){}}); }
  }
  const elements = new Map();
  const get = id => {
    if (!elements.has(id)) elements.set(id, new Element(id));
    return elements.get(id);
  };
  const accounts = [
    {id:'a'.repeat(64),label:'Synthetic desktop A',maskedEmail:'a***@example.test',plan:'Pro'},
    {id:'b'.repeat(64),label:'Synthetic subscription B',maskedEmail:'b***@example.test',plan:'Plus'}
  ];
  let selected = accounts[0].id, backendBusy = false, subscriptionRouting = true;
  let routingEnabled = true, routingConnected = true, routingStartup = null, pending = null, login = null;
  let nativeConnectionObserved = true, verificationState = 'unverified', lastCompletedAccountId = null, routingFailure = null;
  let usageView = null, usageError = false, resetView = null, resetPending = null, resetUnknown = false;
  let removalSupported=true,lastRemoved=null,removedAccount=null;
  let autoRotationSupported=true,autoRotationEnabled=false,lastAutoRotation=null;
  let desktopQuota=null,desktopSwitch=null,desktopSwitchSupported=true,markDesktop=false,profileDetails=[],nativeInput=null,languagePreference=null;
  const resetConfirmation='1'.repeat(32);
  const messageHandlers = new Map(), outbound = [];
  const view = () => ({
    subscriptionRouting,
    languagePreference,
    desktopSwitchSupported,desktopSwitch,
    routingStartup,
    accounts:accounts.map(account=>({...account,plan:usageView?.plan||account.plan,
      planSource:usageView?'server':'cached',usage:usageView,resetCredits:resetView,resetPending,isCurrent:account.id===selected,isDesktop:account.isDesktop===true||markDesktop&&account.id==='a'.repeat(64)})),
    current:{maskedEmail:accounts.find(account=>account.id===selected).maskedEmail,
      plan:accounts.find(account=>account.id===selected).plan},
    desktop:{maskedEmail:accounts[0].maskedEmail,plan:accounts[0].plan},pending,login,
    live:{enabled:routingEnabled,connected:routingConnected,busy:backendBusy,nativeConnectionObserved,verificationState,lastCompletedAccountId,failure:routingFailure,message:'Synthetic only',removalSupported,
      autoRotationSupported,autoRotationEnabled,lastAutoRotation,nativeInput},
    lastRemoved,desktopQuota,
    note:'Synthetic only: no real account or model request.'
  });
  const parent = {postMessage(packet) {
    outbound.push(packet);
    if (packet.id === undefined) return;
    queueMicrotask(() => {
      let result;
      if (packet.method === 'ui/initialize') {
        result = {protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{}},hostContext:{theme:'light'}};
      } else if (packet.method === 'tools/call') {
        const name = packet.params.name, args = packet.params.arguments;
        if(name==='accounts_profile_details')result={structuredContent:{profiles:profileDetails}};
        else if(name==='accounts_set_language'){
          languagePreference=args.language;result={structuredContent:{languagePreference}};
        }
        else if(name==='accounts_request_desktop_switch' && desktopSwitchSupported && args.confirmed===true){
          desktopSwitch={id:'3'.repeat(32),accountId:args.account_id,label:accounts.find(account=>account.id===args.account_id).label,state:'queued',message:'Synthetic queued only.',expiresAtUtc:'2027-01-01T00:00:00Z'};
        }
        else if(name==='accounts_cancel_desktop_switch' && args.request_id===desktopSwitch?.id)desktopSwitch={...desktopSwitch,state:'cancelled'};
        else if (name === 'accounts_select_subscription' && subscriptionRouting && routingEnabled && routingConnected && nativeConnectionObserved && !backendBusy &&
            accounts.some(account=>account.id===args.account_id) &&
            Object.keys(args).length === 1) selected = args.account_id;
        else if (name === 'accounts_refresh_usage' && usageError) {
          result = {isError:true,content:[{type:'text',text:'Synthetic usage temporarily unavailable.'}]};
        }
        else if (name === 'accounts_prepare_reset') {
          const card=resetView?.cards?.find(card=>card.id===args.credit_id);
          result={structuredContent:{resetPreview:{accountId:args.account_id,confirmationId:resetConfirmation,card,
            recovering:!!resetPending,confirmBeforeUtc:'2027-01-01T00:00:00Z',message:'Synthetic reset preview only.'}}};
        }
        else if (name === 'accounts_use_reset') {
          if(resetUnknown)resetPending={accountId:args.account_id,confirmationId:resetConfirmation,card:resetView.cards[0],recovering:true,message:'Synthetic pending.'};
          else resetPending=null;
          result={structuredContent:{...view(),resetResult:{code:resetUnknown?'unknown':'reset',confirmed:!resetUnknown,message:resetUnknown?'Synthetic result unknown.':'Synthetic reset complete.'}}};
        }
        else if(name==='accounts_remove' && args.confirmed===true && args.account_id!==selected && !backendBusy && removalSupported){
          removedAccount=accounts.find(account=>account.id===args.account_id);
          accounts.splice(accounts.findIndex(account=>account.id===args.account_id),1);
          lastRemoved={removalId:'2'.repeat(32),label:removedAccount.label};
        }
        else if(name==='accounts_restore_removed' && args.removal_id===lastRemoved?.removalId && removedAccount){
          accounts.push(removedAccount);lastRemoved=null;removedAccount=null;
        }
        else if(name==='accounts_set_auto_rotation' && typeof args.enabled==='boolean' && autoRotationSupported){
          autoRotationEnabled=args.enabled;
        }
        else if (!['accounts_list','accounts_refresh_usage'].includes(name)) {
          result = {isError:true,content:[{type:'text',text:'Synthetic operation rejected.'}]};
        }
        result ??= {structuredContent:view(),content:[{type:'text',text:'Synthetic result only.'}]};
      } else return;
      messageHandlers.get('message')?.({source:parent,data:{jsonrpc:'2.0',id:packet.id,result}});
    });
  }};
  const window = {parent,addEventListener(type,handler){messageHandlers.set(type,handler);}};
  const document = {getElementById:get,createElement:()=>new Element(),hidden:false,
    documentElement:{dataset:{},style:{setProperty(){}},clientWidth:980}};
  const script = panel.match(/<script>([\s\S]*?)<\/script>/)?.[1];
  assert(typeof script === 'string', 'actual embedded panel script found');
  const intervals=[];
  new vm.Script(script, {filename:'synthetic-accounts-panel'}).runInNewContext({
    window,document,setTimeout,clearTimeout,URL,
    setInterval(callback,delay){const timer=setInterval(callback,delay);intervals.push({callback,delay});return timer;},clearInterval
  });
  try {
    await new Promise(setImmediate);
    assert(outbound.some(packet=>packet.method==='ui/initialize'), 'standard MCP Apps initialize handshake');
    assert(outbound.filter(packet=>packet.method==='tools/call').every(packet=>['accounts_list','accounts_refresh_usage','accounts_profile_details'].includes(packet.params.name)),
      'loading issues only read-only list and official usage requests');
    assert(get('current-heading').textContent === '下一次模型请求使用的订阅', 'subscription mode heading');
    assert(get('current-caption').textContent.includes(accounts[0].maskedEmail), 'desktop A caption is explicit');
    assert(get('current-caption').textContent.includes('仅已选择，尚未确认请求成功'), 'local selection never claims a verified model request');
    assert(get('capture-current').textContent === '保存桌面账号', 'capture label describes desktop account');
    assert(get('reopen').parentElement.hidden, 'no exit/reopen checkbox in subscription routing');
    assert(get('routing-notice').hidden, 'ready subscription connection has no disconnected banner');
    function subscriptionButton(label) {
      const card = get('accounts').children.find(item=>
        item.children[1]?.children[0]?.children.some(child=>child.textContent===label));
      if (!card) throw new Error('Synthetic account card missing.');
      return card.children[2].children.find(child=>child.className.includes('switch'));
    }
    const switchToB = subscriptionButton(accounts[1].label);
    assert(switchToB.textContent === '切换订阅' && !switchToB.disabled, 'noncurrent B subscription button is enabled');
    await switchToB.click();
    const mutations = outbound.filter(packet=>packet.method==='tools/call'&&!['accounts_list','accounts_refresh_usage','accounts_profile_details'].includes(packet.params.name));
    assert(mutations.length === 1 && mutations[0].params.name === 'accounts_select_subscription',
      'click calls subscription selection, never old live/disk switching');
    assert(mutations[0].params.arguments.account_id === accounts[1].id &&
      Object.keys(mutations[0].params.arguments).length === 1, 'selection uses exact B profile id and schema');
    assert(get('current-name').textContent === accounts[1].maskedEmail, 'selected inference subscription becomes B');
    assert(get('current-caption').textContent.includes(accounts[0].maskedEmail) &&
      !get('current-caption').textContent.includes(accounts[1].maskedEmail), 'desktop A and selected B remain separate');
    const publish = () => messageHandlers.get('message')({source:parent,data:{jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:view()}}});
    assert(get('current-caption').textContent.includes('仅已选择，尚未确认请求成功'), 'select callback remains unverified until completion proof');
    verificationState = 'accepted'; lastCompletedAccountId = accounts[1].id;
    publish();
    assert(!get('current-caption').textContent.includes('请求已使用所选账号完成'), 'accepted response headers alone never claim completion');
    verificationState = 'completed'; lastCompletedAccountId = accounts[0].id;
    publish();
    assert(get('current-caption').textContent.includes('仅已选择，尚未确认请求成功'), 'completion for desktop A cannot prove selected subscription B');
    lastCompletedAccountId = accounts[1].id;
    publish();
    assert(get('current-caption').textContent.includes('最近一次模型请求已使用所选账号完成'), 'matching completed profile proof is displayed');
    verificationState = 'unverified';
    publish();
    assert(get('current-caption').textContent.includes('仅已选择，尚未确认请求成功'), 'old completed profile cannot override an unverified current request state');
    assert(subscriptionButton(accounts[1].label).disabled &&
      subscriptionButton(accounts[1].label).textContent === '已选择', 'selected B cannot be submitted twice');
    backendBusy = true;
    messageHandlers.get('message')({source:parent,data:{jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:view()}}});
    const switchToA = subscriptionButton(accounts[0].label), beforeBusyClick = outbound.length;
    assert(switchToA.disabled, 'busy notification disables subscription switch');
    await switchToA.click();
    assert(outbound.length === beforeBusyClick, 'disabled busy control emits no mutation');
    backendBusy = false;
    messageHandlers.get('message')({source:parent,data:{jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:view()}}});
    assert(!subscriptionButton(accounts[0].label).disabled, 'idle notification restores available action');
    const beforeUnavailable = outbound.length;
    const previouslyEnabledButton = subscriptionButton(accounts[0].label);
    subscriptionRouting = false; routingEnabled = false; routingConnected = false;
    pending = {id:'synthetic-previous-request',label:'Synthetic previous B',state:'failed',createdAtUtc:'2026-10-11T03:15:00Z',message:'Synthetic old exit switch failed.'};
    login = {id:'synthetic-completed-login',state:'completed',message:'Synthetic saved only.'};
    publish();
    assert(!get('routing-notice').hidden && get('routing-title').textContent === '未接入订阅切换',
      'unavailable mode has a clear subscription-routing banner');
    assert(get('routing-message').textContent.includes('正常退出 Codex') &&
      get('routing-message').textContent.includes('常用入口'), 'unavailable mode uses the integrated ordinary startup guidance');
    assert(subscriptionButton(accounts[0].label).disabled && subscriptionButton(accounts[1].label).disabled &&
      subscriptionButton(accounts[0].label).textContent === '尚未接入', 'all primary subscription actions remain disabled without routing');
    await subscriptionButton(accounts[0].label).click();
    await previouslyEnabledButton.handlers.get('click')();
    assert(outbound.length === beforeUnavailable, 'even a stale enabled click never falls back to a disk or live switch');
    assert(get('pending-notice').hidden, 'historical exit-switch failure is not shown as a persistent current warning');
    assert(get('login-notice').hidden, 'completed account-save record is not shown as a persistent current warning');
    publish();
    assert(get('pending-notice').hidden && get('login-notice').hidden, 'refresh cannot resurrect the two historical notices');
    pending={...pending,state:'waiting'};publish();
    assert(!get('pending-notice').hidden && !get('cancel-switch').disabled, 'active exit-switch progress and cancellation remain available');
    pending={...pending,state:'failed'};login={...login,state:'waiting'};publish();
    assert(get('pending-notice').hidden && !get('login-notice').hidden && !get('cancel-login').disabled,
      'active login progress remains visible while old switch failure stays hidden');
    login={...login,state:'completed'};publish();
    routingStartup = {stage:'service-starting',state:'starting',message:'Synthetic service starting.',updatedAtUtc:'2026-10-12T01:30:00Z'};
    publish();
    assert(get('routing-title').textContent === '订阅切换正在启动' && get('routing-message').textContent.includes('稍后点击刷新'),
      'service startup has a separate disabled waiting state');
    routingStartup = {stage:'desktop-launch',state:'failed',message:'Synthetic desktop launch failed.',updatedAtUtc:'2026-10-12T01:32:00Z'};
    publish();
    assert(get('routing-title').textContent === '订阅切换启动失败' && get('routing-notice').classes.has('error') &&
      get('routing-message').textContent.includes('Synthetic desktop launch failed.') && get('routing-message').textContent.includes('状态时间：'),
      'fixed safe startup failure and update time are shown separately from old exit failure');
    routingEnabled = true; routingConnected = true; routingStartup = null;
    publish();
    assert(subscriptionButton(accounts[0].label).disabled, 'legacy live transport cannot enable the subscription action');
    subscriptionRouting = true; routingConnected = false; pending = null; login = null;
    publish();
    assert(get('routing-title').textContent === '订阅切换正在启动' && subscriptionButton(accounts[0].label).disabled,
      'routing selection cannot be treated as ready while service is disconnected');
    routingConnected = true;
    nativeConnectionObserved = false; verificationState = 'completed'; lastCompletedAccountId = accounts[1].id;
    publish();
    assert(get('routing-title').textContent === '订阅切换正在启动' && subscriptionButton(accounts[0].label).disabled,
      'service ready alone cannot enable switch before native desktop binding is observed');
    assert(get('current-caption').textContent.includes('仅已选择，尚未确认请求成功'),
      'native binding absent cannot display request completion as current route proof');
    nativeConnectionObserved = true;
    publish();
    assert(get('routing-notice').hidden && !subscriptionButton(accounts[0].label).disabled,
      'verified ready mode restores only the subscription-selection action');
    routingFailure = {code:'selected-rejected',message:'Synthetic selected subscription was rejected.',httpStatus:401};
    publish();
    assert(!get('routing-notice').hidden && get('routing-title').textContent === '最近一次请求未完成' &&
      get('routing-message').textContent.includes('HTTP 401') && get('routing-message').textContent.includes('selected-rejected'),
      'upstream authentication failure shows its own stage and original status');
    assert(!subscriptionButton(accounts[0].label).disabled, 'upstream failure leaves alternate subscription selectable after native binding');
    routingConnected = false; nativeConnectionObserved = false;
    routingFailure = {code:'native-caller',message:'Synthetic native binding refused.',httpStatus:403};
    publish();
    assert(get('routing-message').textContent.includes('native-caller') &&
      get('routing-message').textContent.includes('桌面连接尚未通过校验') && subscriptionButton(accounts[0].label).disabled,
      'local native binding refusal remains distinct and cannot enable a subscription switch');
    routingConnected = true; nativeConnectionObserved = true; routingFailure = null;
    publish();
    assert(outbound.filter(packet=>packet.method==='tools/call').every(packet=>
      ['accounts_list','accounts_select_subscription','accounts_refresh_usage','accounts_profile_details'].includes(packet.params.name)), 'panel never sends accounts_request_switch or accounts_switch_live');
    await get('refresh').click();
    const refreshCall=outbound.filter(packet=>packet.params?.name==='accounts_refresh_usage').at(-1);
    assert(refreshCall?.params.arguments.force === true,
      'manual refresh requests current quotas without switching');
    const contents = element => element.textContent + element.children.map(contents).join(' ');
    usageView={status:'ok',plan:'Free',stale:false,updatedAtUtc:'2026-10-22T02:20:00Z',
      primary:{usedPercent:0,limitWindowSeconds:18000,resetsAtUtc:'2026-10-22T06:00:00Z'},
      secondary:{usedPercent:null,limitWindowSeconds:604800,resetsAtUtc:null}};
    publish();
    const quotaText=contents(get('accounts'));
    assert(quotaText.includes('Free') && !quotaText.includes('缓存套餐：Pro'), 'official current plan replaces stale cached Pro label');
    assert(quotaText.includes('剩余 100%') && quotaText.includes('用量未提供') && quotaText.includes('重置时间未提供'),
      'zero usage and unavailable usage or reset timestamps remain distinct');
    assert(quotaText.includes('UTC+8') && quotaText.includes('上次更新：'), 'quota reset and update timestamps have explicit time zone');
    usageView={...usageView,status:'unavailable',stale:true,message:'Synthetic last value retained.'};
    publish();
    assert(contents(get('accounts')).includes('数据待更新') && contents(get('accounts')).includes('Synthetic last value retained.'),
      'failed refresh clearly marks retained quota values stale');
    usageError=true;
    await get('refresh').click();
    assert(!subscriptionButton(accounts[0].label).disabled, 'quota service failure never locks an otherwise ready account switch');
    assert(get('usage-caption').textContent.includes('每分钟自动更新'), 'automatic quota refresh interval is visible');
    const automatic=intervals.find(interval=>interval.delay===60000);
    assert(!!automatic, 'quota refresh is actually scheduled every minute');
    const beforeHidden=outbound.length;document.hidden=true;
    await automatic.callback();
    assert(outbound.length===beforeHidden, 'hidden panels pause automatic quota queries');
    document.hidden=false;usageError=false;
    await automatic.callback();
    assert(outbound.filter(packet=>packet.params?.name==='accounts_refresh_usage').at(-1)?.params.arguments.force===false,
      'automatic refresh respects backend cache interval');
    const firstReset={id:'synthetic-card-1',status:'available',resetType:'codex_rate_limits',expiresAtUtc:'2027-01-22T00:00:00Z',expiryKnown:true};
    resetView={status:'ok',stale:false,availableCount:5,cards:[firstReset],updatedAtUtc:'2026-10-22T02:20:00Z'};
    publish();
    assert(contents(get('accounts')).includes('可用 5 张') && contents(get('accounts')).includes('官方仅返回部分卡片详情'),
      'banked reset total comes from backend count, not returned row count');
    const findButton=(element,label)=>element.textContent===label?element:element.children.map(child=>findButton(child,label)).find(Boolean);
    const bCard=()=>get('accounts').children.find(card=>contents(card).includes(accounts[1].label));
    const beforePreview=outbound.filter(packet=>packet.params?.name==='accounts_use_reset').length;
    await findButton(bCard(),'使用此卡').click();
    assert(!get('reset-confirm').hidden && get('reset-description').textContent.includes(accounts[1].label) && get('reset-description').textContent.includes('UTC+8'),
      'reset confirmation shows exact target account and real expiration time zone');
    assert(outbound.at(-1).params.name==='accounts_prepare_reset' && outbound.at(-1).params.arguments.account_id===accounts[1].id &&
      outbound.filter(packet=>packet.params?.name==='accounts_use_reset').length===beforePreview,'opening confirmation never redeems a card');
    await get('reset-cancel').click();
    assert(get('reset-confirm').hidden && outbound.filter(packet=>packet.params?.name==='accounts_use_reset').length===beforePreview,'cancel never consumes a reset');
    await findButton(bCard(),'使用此卡').click();await get('reset-submit').click();
    const redemption=outbound.filter(packet=>packet.params?.name==='accounts_use_reset').at(-1);
    assert(redemption.params.arguments.account_id===accounts[1].id && redemption.params.arguments.confirmation_id===resetConfirmation &&
      redemption.params.arguments.confirmed===true && get('reset-confirm').hidden,'only explicit confirmation sends exact account and idempotent confirmation');
    resetUnknown=true;
    await findButton(bCard(),'使用此卡').click();await get('reset-submit').click();
    assert(contents(get('accounts')).includes('核对上次重置') && get('status-message').textContent.includes('Synthetic result unknown.'),'uncertain redemption persists a separate recovery control');
    const beforeAuto=outbound.filter(packet=>packet.params?.name==='accounts_use_reset').length;
    await automatic.callback();
    assert(outbound.filter(packet=>packet.params?.name==='accounts_use_reset').length===beforeAuto,'automatic refresh never retries redemption');
    await findButton(bCard(),'核对上次重置').click();
    assert(get('reset-submit').textContent==='核对同一次操作','uncertain result requires another explicit same-operation confirmation');
    await get('reset-cancel').click(); resetPending=null;
    resetView={status:'unavailable',stale:true,availableCount:null,cards:null};publish();
    assert(contents(get('accounts')).includes('数量未知') && !contents(get('accounts')).includes('可用 0 张'),'missing reset data remains unknown, not zero');
    resetView={status:'ok',stale:false,availableCount:0,cards:[]};publish();
    assert(contents(get('accounts')).includes('可用 0 张') && contents(get('accounts')).includes('暂无可用重置卡'),'official zero is explicitly displayed');
    resetView={status:'ok',stale:false,availableCount:1,cards:[{...firstReset,expiryKnown:false,expiresAtUtc:null}]};publish();
    assert(findButton(bCard(),'使用此卡').disabled && contents(get('accounts')).includes('到期时间未提供'),'unknown expiry cannot be redeemed');
    resetView={status:'ok',stale:false,availableCount:1,cards:[{...firstReset,expiresAtUtc:'2020-01-01T00:00:00Z'}]};publish();
    assert(findButton(bCard(),'使用此卡').disabled && contents(get('accounts')).includes('已过期'),'expired cards cannot be redeemed');
    resetView={status:'ok',stale:false,availableCount:1,cards:[{...firstReset,expiresAtUtc:null}]};publish();
    assert(!findButton(bCard(),'使用此卡').disabled && contents(get('accounts')).includes('官方标记无到期时间'),'explicit non-expiring card differs from missing expiration');
    const inactiveId=accounts[0].id,inactiveLabel=accounts[0].label;
    const inactiveCard=()=>get('accounts').children.find(card=>contents(card).includes(inactiveLabel));
    assert(findButton(bCard(),'移除').disabled,'selected subscription cannot be removed');
    removalSupported=false;publish();
    assert(findButton(inactiveCard(),'移除').disabled,'old hosts cannot enable removal without atomic selection protection');
    removalSupported=true;backendBusy=true;publish();
    assert(findButton(inactiveCard(),'移除').disabled,'busy host cannot enable account removal');
    backendBusy=false;publish();
    accounts[0].isDesktop=true;publish();
    assert(findButton(inactiveCard(),'移除').disabled,'native desktop account cannot be removed while another subscription is selected');
    accounts[0].isDesktop=false;publish();
    const beforeRemove=outbound.filter(packet=>packet.params?.name==='accounts_remove').length;
    await findButton(inactiveCard(),'移除').click();
    assert(!get('remove-confirm').hidden && get('remove-description').textContent.includes(inactiveLabel) &&
      outbound.filter(packet=>packet.params?.name==='accounts_remove').length===beforeRemove,'removal first shows exact account confirmation without a mutation');
    await get('remove-cancel').click();
    assert(get('remove-confirm').hidden && accounts.length===2,'cancel keeps the saved account');
    await findButton(inactiveCard(),'移除').click();await get('remove-submit').click();
    const removeCall=outbound.filter(packet=>packet.params?.name==='accounts_remove').at(-1);
    assert(removeCall.params.arguments.account_id===inactiveId && removeCall.params.arguments.confirmed===true && accounts.length===1 &&
      !get('undo-remove').hidden,'explicit removal uses exact account and exposes undo');
    const selectionBeforeUndo=selected;
    await get('undo-remove').click();
    assert(accounts.length===2 && selected===selectionBeforeUndo && get('undo-remove').hidden,'undo restores the record without changing the selected subscription');
    assert(!get('auto-rotation').checked && !outbound.some(packet=>packet.params?.name==='accounts_set_auto_rotation'),
      'automatic rotation is off by default and loading never opts in');
    get('auto-rotation').checked=true;await get('auto-rotation').handlers.get('change')();
    assert(outbound.at(-1).params.name==='accounts_set_auto_rotation' && outbound.at(-1).params.arguments.enabled===true &&
      get('auto-rotation').checked && get('rotation-caption').textContent.includes('同一请求'),
      'user switch enables request-level seamless continuation without selecting an account');
    backendBusy=true;publish();
    assert(!get('auto-rotation').disabled,'user can disable future rotation while model response is active');
    get('auto-rotation').checked=false;await get('auto-rotation').handlers.get('change')();
    assert(!get('auto-rotation').checked && outbound.at(-1).params.arguments.enabled===false,'user switch turns off automatic rotation');
    backendBusy=false;autoRotationSupported=false;publish();
    assert(get('auto-rotation').disabled && get('rotation-caption').textContent.includes('正常重开'),'old hosts cannot silently enable an unsupported rotation setting');
    autoRotationSupported=true;lastAutoRotation={fromAccountId:accounts[0].id,toAccountId:accounts[1].id,state:'completed',occurredAtUtc:'2026-10-22T02:20:00Z'};publish();
    assert(!get('rotation-last').hidden && get('rotation-last').textContent.includes(accounts[0].label) &&
      get('rotation-last').textContent.includes('响应已完成'),'rotation record identifies target and observed completion state');
    const beforePreflight=outbound.length;
    desktopQuota={state:'desktop-exhausted',message:'Synthetic native login quota exhausted; inference selection does not unlock native send.',updatedAtUtc:'2026-10-23T00:00:00Z'};
    publish();
    assert(!get('desktop-quota-notice').hidden && get('desktop-quota-message').textContent.includes('native login quota exhausted') &&
      get('desktop-quota-updated').textContent.includes('UTC+8'),'native login exhaustion has its own current quota warning');
    assert(outbound.length===beforePreflight,'native exhaustion diagnosis never logs out or changes desktop credentials automatically');
    desktopQuota={state:'stale',message:'Synthetic old quota snapshot.',updatedAtUtc:'2026-10-22T00:00:00Z'};publish();
    assert(get('desktop-quota-notice').hidden,'old exhausted snapshot is not displayed as a current native-send block');
    desktopQuota={state:'allowed',message:'Synthetic current official quota allowed.',updatedAtUtc:'2026-10-23T00:00:01Z'};publish();
    assert(get('desktop-quota-notice').hidden,'official current allowed snapshot clears only the local diagnosis, without claiming native send was tested');
    const beforeModes=outbound.length;markDesktop=true;publish();
    const nativeAccount=accounts.find(account=>account.id==='a'.repeat(64)),nativeTarget=accounts.find(account=>account.id==='b'.repeat(64));
    await get('mode-desktop').handlers.get('change')();
    assert(outbound.length===beforeModes && get('mode-desktop').checked,'mode choice alone sends no account mutation');
    assert(subscriptionButton(nativeAccount.label).disabled && subscriptionButton(nativeAccount.label).textContent==='桌面当前登录','native login status is distinct from selected inference account');
    await subscriptionButton(nativeTarget.label).click();
    assert(!get('desktop-switch-confirm').hidden && get('desktop-switch-description').textContent.includes(nativeTarget.maskedEmail) &&
      !outbound.some(packet=>packet.params?.name==='accounts_request_desktop_switch'),'exact target and scope shown before scheduling desktop sync');
    await get('desktop-switch-cancel').click();
    assert(get('desktop-switch-confirm').hidden && outbound.length===beforeModes,'cancelled confirmation does not register or change credentials');
    await subscriptionButton(nativeTarget.label).click();await get('desktop-switch-submit').click();
    const sync=outbound.filter(packet=>packet.params?.name==='accounts_request_desktop_switch').at(-1);
    assert(sync?.params.arguments.account_id===nativeTarget.id && sync.params.arguments.confirmed===true &&
      !get('desktop-switch-notice').hidden && get('pending-notice').hidden,'confirmed sync schedules exact target and shows current intent without reviving old failure');
    assert(get('mode-subscription').disabled && !get('cancel-desktop-switch').disabled,'pending sync can be cancelled while conflicting mode changes are disabled');
    await get('cancel-desktop-switch').click();
    assert(get('desktop-switch-notice').hidden && outbound.at(-1).params.arguments.request_id==='3'.repeat(32),'cancel binds the pending intent id');
    await get('mode-subscription').handlers.get('change')();
    assert(get('mode-caption').textContent.includes('未加载'),'inference-only mode never claims native-send adaptation before the new host loads');
    desktopSwitchSupported=false;publish();
    assert(get('mode-desktop').disabled,'older metadata cannot enable desktop sync without backend support');
    profileDetails=[{accountId:nativeTarget.id,displayName:'Synthetic actual display',username:'synthetic-handle',source:'login-cache',avatarUrl:'https://cdn.openai.com/synthetic.png'},
      {accountId:'f'.repeat(64),displayName:'Wrong account',source:'login-cache'}];
    await get('refresh').click();
    assert(contents(get('accounts')).includes('名称：Synthetic actual display') && contents(get('accounts')).includes('用户名：synthetic-handle') &&
      !contents(get('accounts')).includes('Wrong account') && get('profile-caption').hidden,'returned profile fields remain while the redundant profile explanation stays hidden');
    profileDetails=[];await get('refresh').click();
    assert(get('profile-caption').hidden && !contents(get('accounts')).includes('名称：') && !contents(get('accounts')).includes('用户名：'),
      'unavailable official login fields are hidden entirely without invented profile placeholders');
    assert(get('accounts').children.every(card=>card.children[0].hidden),'missing official avatars have no letter-photo placeholders');
    nativeInput={supported:true,connected:true,frontendAdapted:true,initialQuotaAdapted:true,workQuotaAdapted:true,quotaAvailable:true,state:'adapted',updatedAtUtc:new Date().toISOString()};
    desktopQuota={state:'desktop-exhausted',message:'Synthetic original A exhausted.',updatedAtUtc:new Date().toISOString()};publish();
    assert(get('desktop-quota-notice').hidden && get('mode-caption').textContent.includes('发送已接入'),
      'only fresh native-renderer adaptation and usable inference quota clear the local block diagnosis');
    nativeInput={...nativeInput,quotaAvailable:false};publish();
    assert(!get('desktop-quota-notice').hidden && get('desktop-quota-message').textContent.includes('请选择有额度'),
      'native frontend adaptation cannot invent a usable inference quota');
    nativeInput={...nativeInput,quotaAvailable:true,updatedAtUtc:'2020-01-01T00:00:00Z'};publish();
    assert(!get('desktop-quota-notice').hidden && !get('mode-caption').textContent.includes('发送已接入'),
      'old native-adapter heartbeat never claims the send integration remains active');
    nativeInput={supported:true,connected:true,frontendAdapted:false,state:'incompatible',updatedAtUtc:new Date().toISOString()};publish();
    assert(get('mode-caption').textContent.includes('版本尚未通过适配核验'),'unrecognized official frontend remains visibly unadapted');
    nativeInput={supported:true,connected:true,frontendAdapted:true,quotaAvailable:true,state:'adapted',updatedAtUtc:new Date().toISOString()};publish();
    assert(!get('desktop-quota-notice').hidden&&get('mode-caption').textContent.includes('只覆盖了部分'),
      '0.11 marker alone cannot claim the second native work-quota gate is covered');
    nativeInput={...nativeInput,frontendAdapted:false,initialQuotaAdapted:true,workQuotaAdapted:false,state:'partial'};publish();
    assert(!get('desktop-quota-notice').hidden&&get('mode-caption').textContent.includes('只覆盖了部分'),
      'a partially loaded new host remains visibly incomplete');
    nativeInput=null;desktopQuota=null;desktopSwitchSupported=true;usageView={status:'ok',stale:false,updatedAtUtc:new Date().toISOString(),primary:{usedPercent:25,limitWindowSeconds:18000,resetsAtUtc:'2027-01-01T06:00:00Z'}};
    autoRotationEnabled=true;publish();
    const originalLabels=accounts.map(account=>account.label);
    await get('add-account').click();get('account-label').value='Do not lose this draft · 名称';
    const beforeLanguage=outbound.length;
    for(const code of ['en','zh-TW','ja','ko','es','fr','de','pt-BR','ru','zh-CN']){
      get('language-select').value=code;
      await get('language-select').handlers.get('change')();
      assert(document.documentElement.lang===code&&get('current-heading').textContent===translations['下一次模型请求使用的订阅'][code],`language_${code}_updates_dynamic_interface`);
      assert(get('account-label').value==='Do not lose this draft · 名称',`language_${code}_preserves_editor_draft`);
      assert(accounts.every((account,index)=>account.label===originalLabels[index])&&originalLabels.every(label=>contents(get('accounts')).includes(label)),`language_${code}_does_not_translate_user_labels`);
      assert(get('auto-rotation').checked,`language_${code}_preserves_rotation_setting`);
    }
    const languageCalls=outbound.slice(beforeLanguage).filter(packet=>packet.method==='tools/call');
    assert(languageCalls.length===10&&languageCalls.every(packet=>packet.params.name==='accounts_set_language'&&Object.keys(packet.params.arguments).length===1),
      'language_switch_saves_only_display_preference_never_account_mutations');
    await get('editor-cancel').click();
    const beforeInvalidLanguage=outbound.length;get('language-select').value='invalid';await get('language-select').handlers.get('change')();
    assert(outbound.length===beforeInvalidLanguage&&document.documentElement.lang==='zh-CN','invalid_language_is_not_persisted');
    return {passed:true,synthetic_only:true,real_accounts_read:false,real_codex_connected:false,
      real_browser_rendered:false,screenshot_created:false,checks};
  } finally {
    messageHandlers.get('beforeunload')?.();
  }
}

function servePreview() {
const server = http.createServer((request, response) => {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, {'Content-Type':'text/plain'}); response.end('Synthetic fixture only.'); return;
  }
  const resource = request.url === '/' ? host : request.url === '/panel' ? panel : null;
  if (resource === null) { response.writeHead(404); response.end(); return; }
  response.writeHead(200, {'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
  response.end(request.method === 'HEAD' ? '' : resource);
});
server.listen(0, '127.0.0.1', () => {
  process.stdout.write(JSON.stringify({synthetic_only:true,real_accounts_read:false,real_codex_connected:false,url:'http://127.0.0.1:'+server.address().port+'/'})+'\n');
});
const shutdown = () => { server.close(); server.closeAllConnections(); };
process.once('SIGINT', shutdown);
process.once('SIGTERM', shutdown);
setTimeout(shutdown, 10 * 60 * 1000).unref();
}

if (process.argv.includes('--dom-check')) {
  checkIsolatedDom().then(report=> {
    const reportPath = path.resolve(__dirname,'..','validation-output','routing-panel-dom-result.json');
    fs.writeFileSync(reportPath,JSON.stringify(report,null,2));
    process.stdout.write(JSON.stringify(report)+'\n');
  }).catch(error=> {
    process.stderr.write('Synthetic panel check failed: '+error.message+'\n');process.exitCode=1;
  });
} else servePreview();
