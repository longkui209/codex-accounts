/* Offline MCP integration check. The server uses a new synthetic home only. */
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const vm = require('node:vm');
const { spawn } = require('node:child_process');

const project = path.resolve(__dirname, '..');
const isolated = path.join(project, 'validation-output', 'mcp-' + crypto.randomUUID());
const home = path.join(isolated, 'codex-home');
fs.mkdirSync(home, { recursive: true });
const jwt = obj => Buffer.from('{"alg":"RS256","typ":"JWT"}').toString('base64url') + '.' + Buffer.from(JSON.stringify(obj)).toString('base64url') + '.syntheticSignature';
const auth = { auth_mode:'chatgpt', OPENAI_API_KEY:null, tokens:{
  account_id:'synthetic-plugin-account',
  id_token:jwt({sub:'synthetic-user',email:'demo@example.test',name:'Synthetic display name',preferred_username:'synthetic-handle',picture:'https://cdn.openai.com/synthetic-avatar.png',exp:Math.floor(Date.now()/1000)+7200}),
  access_token:jwt({sub:'synthetic-user',exp:Math.floor(Date.now()/1000)+3600,'https://api.openai.com/auth':{chatgpt_plan_type:'pro'}}),
  refresh_token:'synthetic-plugin-refresh-not-real'
},last_refresh:new Date().toISOString() };
fs.writeFileSync(path.join(home, 'auth.json'), JSON.stringify(auth));
const exe = path.join(project, 'dist', 'CodexAccountSwitcher.exe');
const installedRoot = process.argv[2] ? path.resolve(process.argv[2]) : null;
let launchExecutable = exe, launchArgs = ['--mcp-test', isolated], launchDirectory = project;
if (installedRoot) {
  const config = JSON.parse(fs.readFileSync(path.join(installedRoot, '.mcp.json'), 'utf8')).mcpServers.codex_accounts;
  if(config.command!=='cmd.exe'||config.cwd!=='.'||JSON.stringify(config.args)!==JSON.stringify(['/d','/c','runtime\\CodexAccountSwitcher.exe','--mcp']))
    throw new Error('Installed launcher config differs from the reviewed fixed command.');
  launchExecutable = config.command; launchArgs = [...config.args.slice(0,-1),'--mcp-test',isolated]; launchDirectory=installedRoot;
}
const child = spawn(launchExecutable, launchArgs, { cwd:launchDirectory, windowsHide:true, stdio:['pipe','pipe','pipe'] });
let line = '', next = 0;
const waiting = new Map();
const checks = [];
function assert(ok,message) { if(!ok) throw new Error(message); }
child.stdout.setEncoding('utf8');
child.stdout.on('data',chunk => {
  line += chunk;
  while(line.includes('\n')) {
    const end = line.indexOf('\n'), entry = line.slice(0,end).trim(); line = line.slice(end+1);
    if(!entry) continue;
    let message; try { message=JSON.parse(entry); } catch { for(const waiter of waiting.values())waiter.reject(new Error('Server stdout is not JSON-RPC.')); continue; }
    const waiter=waiting.get(message.id); if(waiter){waiting.delete(message.id);clearTimeout(waiter.timeout);message.error?waiter.reject(new Error('MCP error: '+message.error.code)):waiter.resolve(message.result);}
  }
});
child.stderr.on('data',()=>{}); // Never print unfiltered process output.
child.on('error',()=>{for(const waiter of waiting.values())waiter.reject(new Error('MCP process could not start.'));});
function call(method,params={}) {
  const id=++next;
  return new Promise((resolve,reject)=>{
    const timeout=setTimeout(()=>{waiting.delete(id);reject(new Error('MCP response timed out.'));},10000);
    waiting.set(id,{resolve,reject,timeout}); child.stdin.write(JSON.stringify({jsonrpc:'2.0',id,method,params})+'\n');
  });
}
function assertPanelDisplayModes(html, resourceModes) {
  const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
  assert(script, 'Embedded panel script is missing.');
  const outbound = [], elements = new Map();
  const element = () => ({
    classList:{toggle(){}}, style:{setProperty(){}},
    addEventListener(){}, replaceChildren(){}, append(){}, setAttribute(){}, focus(){}, parentElement:{hidden:false}
  });
  const parent = {postMessage(message){outbound.push(message);}};
  const window = {parent,addEventListener(){}};
  const document = {
    getElementById(id){if(!elements.has(id))elements.set(id,element());return elements.get(id);},
    createElement:element, documentElement:{dataset:{},style:{setProperty(){}},clientWidth:640}, hidden:false
  };
  // Run the actual embedded client until its first handshake. No server, filesystem,
  // browser storage or credentials are exposed to this isolated UI context.
  new vm.Script(script).runInNewContext({window,document,setTimeout(){return 1;},clearTimeout(){},setInterval(){return 1;},clearInterval(){}});
  const initialize = outbound.find(message=>message.method==='ui/initialize');
  assert(initialize?.params?.protocolVersion==='2026-01-26','Panel did not initiate the standard MCP Apps handshake.');
  const appModes=initialize.params.appCapabilities?.availableDisplayModes;
  assert(Array.isArray(appModes)&&appModes.includes('inline')&&appModes.includes('fullscreen'),'Panel handshake does not support both inline and fullscreen.');
  assert(resourceModes.every(mode=>appModes.includes(mode)),'Resource modes differ from the running panel capabilities.');
  assert(!outbound.some(message=>message.method==='tools/call'),'Panel called server tools before the initialization handshake.');
}
async function main() {
  const init=await call('initialize',{protocolVersion:'2025-06-18',capabilities:{},clientInfo:{name:'synthetic-account-plugin-check',version:'1'}});
  assert(init.serverInfo.name==='codex-accounts'&&init.capabilities.tools&&init.capabilities.resources,'Initialization failed.');
  child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
  checks.push('stdio initialization and capabilities');
  const list=await call('tools/list'); assert(list.tools.length===27,'Unexpected tools count.');
  for(const tool of list.tools) assert(tool.inputSchema.type==='object','Tool schema invalid.');
  const panel=list.tools.find(t=>t.name==='accounts_panel'); assert(panel._meta.ui.resourceUri==='ui://codex-accounts/panel.html','Panel metadata missing.');
  assert(panel._meta['openai/ui'].entrypoints.some(entry=>entry.type==='global'),'Desktop sidebar click entrypoint is missing.');
  for(const name of ['accounts_prepare_reset','accounts_use_reset','accounts_remove','accounts_restore_removed','accounts_set_auto_rotation','accounts_request_desktop_switch','accounts_cancel_desktop_switch','accounts_profile_details','accounts_install_startup','accounts_uninstall_startup','accounts_set_language']) {
    const resetTool=list.tools.find(tool=>tool.name===name);
    assert(resetTool && JSON.stringify(resetTool._meta.ui.visibility)==='["app"]','Reset controls must be app-only.');
  }
  checks.push('27 plugin tools; account mutations, startup controls and display language are app-only');
  const languageTool=list.tools.find(tool=>tool.name==='accounts_set_language');
  assert(languageTool.annotations.destructiveHint===false&&languageTool.annotations.idempotentHint===true,'Display preference must not be described as destructive.');
  for(const name of ['accounts_install_startup','accounts_uninstall_startup']){
    const action=await call('tools/call',{name,arguments:{confirmed:true}});assert(action.isError,'Isolation must reject real startup mutations.');
  }
  const startup=await call('tools/call',{name:'accounts_startup_status',arguments:{}});
  assert(startup.structuredContent.startupIntegration.state==='isolated','Isolated startup status must not read real integration state.');
  const resources=await call('resources/list'); assert(resources.resources[0].uri===panel._meta.ui.resourceUri,'Resource not discoverable.');
  const resource=await call('resources/read',{uri:panel._meta.ui.resourceUri});
  assert(resource.contents[0].mimeType==='text/html;profile=mcp-app'&&resource.contents[0].text.includes('ui/initialize'),'MCP Apps component is missing.');
  const display=resource.contents[0]._meta?.['openai/ui'];
  assert(Array.isArray(display?.availableDisplayModes)&&display.availableDisplayModes.includes('inline')&&display.availableDisplayModes.includes('fullscreen'),'Resource does not advertise inline and fullscreen before UI initialization.');
  assert(display.preferredDisplayMode==='fullscreen','Desktop click entrypoint must prefer fullscreen.');
  assertPanelDisplayModes(resource.contents[0].text,display.availableDisplayModes);
  checks.push('embedded MCP Apps HTML and matching fullscreen resource/client handshake');
  const capture=await call('tools/call',{name:'accounts_capture_current',arguments:{label:'合成插件账号'}});
  assert(!capture.isError&&capture.structuredContent.accounts.length===1,'Synthetic profile capture failed.');
  const resultText=JSON.stringify(capture);
  assert(!resultText.includes(auth.tokens.refresh_token)&&!resultText.includes(auth.tokens.access_token)&&!resultText.includes('demo@example.test'),'Tool leaked credentials or unmasked email.');
  checks.push('synthetic encrypted capture and no token / email leak');
  const account=capture.structuredContent.accounts[0];
  const authBeforePreference=fs.readFileSync(path.join(home,'auth.json'));
  for(const language of ['en','zh-TW','ja','ko','es','fr','de','pt-BR','ru','zh-CN']){
    const changed=await call('tools/call',{name:'accounts_set_language',arguments:{language}});
    const current=await call('tools/call',{name:'accounts_list',arguments:{}});
    assert(!changed.isError&&changed.structuredContent.languagePreference===language&&current.structuredContent.languagePreference===language,'Display preference did not persist.');
    assert(current.structuredContent.accounts.length===1&&current.structuredContent.accounts[0].id===account.id&&fs.readFileSync(path.join(home,'auth.json')).equals(authBeforePreference),'Display preference changed account or authentication.');
  }
  const invalidLanguage=await call('tools/call',{name:'accounts_set_language',arguments:{language:'unsupported'}});
  assert(invalidLanguage.isError,'Unsupported language must be rejected.');
  checks.push('ten display languages persist independently without changing saved accounts or authentication; invalid language rejected');
  assert(!resultText.includes('Synthetic display name')&&!resultText.includes('synthetic-handle'),'Model-visible listing must not expose personal profile details.');
  const profiles=await call('tools/call',{name:'accounts_profile_details',arguments:{}});
  const profile=profiles.structuredContent.profiles.find(entry=>entry.accountId===account.id);
  assert(profile.displayName==='Synthetic display name' && profile.username==='synthetic-handle' && profile.source==='login-cache','App profile details must come from the matching saved login.');
  assert(!JSON.stringify(profiles).includes(auth.tokens.access_token) && !JSON.stringify(profiles).includes(auth.tokens.refresh_token),'Profile-only response must never return credentials.');
  checks.push('optional profile fields from matching saved login only; hidden from model listings, no credentials');
  const renamed=await call('tools/call',{name:'accounts_rename',arguments:{account_id:account.id,label:'已改名'}});
  assert(!renamed.isError&&renamed.structuredContent.accounts[0].label==='已改名','Profile rename failed.');
  checks.push('native tool rename persists');
  const savedAuthA=JSON.stringify(auth);
  const authB=JSON.parse(savedAuthA);authB.tokens.account_id='synthetic-plugin-account-B';
  fs.writeFileSync(path.join(home,'auth.json'),JSON.stringify(authB));
  const captureB=await call('tools/call',{name:'accounts_capture_current',arguments:{label:'Synthetic removable B'}});
  assert(!captureB.isError,'Synthetic removable B capture failed.');
  const profileB=captureB.structuredContent.accounts.find(entry=>entry.id!==account.id);
  fs.writeFileSync(path.join(home,'auth.json'),savedAuthA);
  const unconfirmed=await call('tools/call',{name:'accounts_remove',arguments:{account_id:profileB.id,confirmed:false}});
  assert(unconfirmed.isError,'Removal must require confirmation.');
  const protectedCurrent=await call('tools/call',{name:'accounts_remove',arguments:{account_id:account.id,confirmed:true}});
  assert(protectedCurrent.isError,'Current account must be protected.');
  const removal=await call('tools/call',{name:'accounts_remove',arguments:{account_id:profileB.id,confirmed:true}});
  assert(!removal.isError && removal.structuredContent.accounts.length===1 && removal.structuredContent.lastRemoved,'Synthetic removal failed.');
  const undo=await call('tools/call',{name:'accounts_restore_removed',arguments:{removal_id:removal.structuredContent.lastRemoved.removalId}});
  assert(!undo.isError && undo.structuredContent.accounts.length===2 && undo.structuredContent.lastRemoved===null &&
    fs.readFileSync(path.join(home,'auth.json'),'utf8')===savedAuthA,'Removal undo must preserve desktop auth.');
  checks.push('confirmed inactive-record removal and undo; current account and desktop auth protected');
  const bad=await call('tools/call',{name:'accounts_request_switch',arguments:{account_id:account.id}});
  assert(bad.isError,'Isolation must prohibit starting a real switch worker.');
  const login=await call('tools/call',{name:'accounts_begin_login',arguments:{label:'禁止真实登录'}});
  assert(login.isError,'Isolation must prohibit real OAuth.');
  const live=await call('tools/call',{name:'accounts_switch_live',arguments:{account_id:account.id}});
  assert(live.isError,'Isolation must prohibit real live switching.');
  checks.push('isolated mode rejects actual switching and OAuth');
  const usage=await call('tools/call',{name:'accounts_refresh_usage',arguments:{force:true}});
  assert(usage.isError,'Isolation must prohibit real usage network calls.');
  checks.push('isolated mode never requests real account quotas');
  for(const name of ['accounts_prepare_reset','accounts_use_reset']) {
    const reset=await call('tools/call',{name,arguments:{account_id:account.id,credit_id:'synthetic-card',confirmation_id:'a'.repeat(32),confirmed:true}});
    assert(reset.isError,'Isolation must prohibit real reset lookup and redemption.');
  }
  checks.push('isolated mode rejects actual reset lookup and redemption');
  const rotation=await call('tools/call',{name:'accounts_set_auto_rotation',arguments:{enabled:true}});
  assert(rotation.isError,'Isolated MCP mode must not enable production routing.');
  checks.push('isolated plugin mode cannot enable real account auto-rotation');
  for(const name of ['accounts_request_desktop_switch','accounts_cancel_desktop_switch']) {
    const result=await call('tools/call',{name,arguments:{account_id:account.id,request_id:'1'.repeat(32),confirmed:true}});
    assert(result.isError,'Isolated MCP mode must not register or execute a real desktop-login switch.');
  }
  checks.push('isolated plugin mode cannot register or apply production desktop-login sync');
  const missing=await call('tools/call',{name:'accounts_capture_current',arguments:{}});
  assert(missing.isError,'Missing label must be rejected.');
  assert((await call('tools/call',{name:'accounts_pending_status',arguments:{}})).structuredContent.pending===null,'Unexpected pending operation.');
  checks.push('invalid input and pending-state behavior');
  child.stdin.end();
  await new Promise((resolve,reject)=>{if(child.exitCode!==null)return resolve();const timeout=setTimeout(()=>reject(new Error('Server failed to exit after EOF.')),5000);child.once('exit',code=>{clearTimeout(timeout);code===0?resolve():reject(new Error('Server exit failed.'));});});
  fs.writeFileSync(path.join(project,'validation-output',installedRoot?'mcp-installed-result.json':'mcp-result.json'),JSON.stringify({passed:true,installed_package:!!installedRoot,checks},null,2));
  console.log(JSON.stringify({passed:true,installed_package:!!installedRoot,checks}));
}
main().catch(error=>{
  fs.writeFileSync(path.join(project,'validation-output','mcp-result.json'),JSON.stringify({passed:false,error:error.message},null,2));
  console.error('MCP validation failed: '+error.message); child.kill(); process.exitCode=1;
});
