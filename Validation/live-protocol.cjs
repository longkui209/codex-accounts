'use strict';
// Only fresh synthetic vaults, our own official CLI, and a loopback mock are used.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),http=require('node:http');
const crypto=require('node:crypto'),{spawn}=require('node:child_process');
const exe=process.argv[2],cli=process.argv[3];
if(!exe||!cli||!path.isAbsolute(exe)||!path.isAbsolute(cli))throw new Error('Absolute executable paths required.');
const parent=path.resolve(__dirname,'..','validation-output');
fs.mkdirSync(parent,{recursive:true});
const root=path.join(parent,'live-'+crypto.randomUUID());fs.mkdirSync(root);
const home=path.join(root,'app-server-home');fs.mkdirSync(home);
const seedHome=path.join(root,'codex-home');fs.mkdirSync(seedHome);
const cap=crypto.randomBytes(32).toString('base64url');
const hash=crypto.createHash('sha256').update(cap).digest('hex');
const pipeName='codex-account-live-test-'+crypto.randomUUID().replace(/-/g,'');
const report={synthetic_only:true,real_desktop_connected:false,real_subscription_validated:false,
  checks:[],passed:false,phase:'setup',model_requested:false,unexpected_authorization:false};
let seed,server,harness,mock, rejectB=false;
const clients=[];
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function check(ok,label){if(!ok)throw Object.assign(new Error('Integration assertion failed.'),{check:label,kind:'assertion_failed'});report.checks.push(label);}
function token(id,issuer='https://auth.openai.com'){
  const claims={iss:issuer,aud:'app_EMoamEEZ73f0CkXaXp7hrann',sub:'synthetic-user-'+id,
    email:'synthetic-'+id+'@example.test',exp:Math.floor(Date.now()/1000)+7200,
    'https://api.openai.com/auth':{chatgpt_account_id:'synthetic-'+id,chatgpt_user_id:'synthetic-user-'+id,chatgpt_plan_type:'plus'}};
  return Buffer.from('{"alg":"RS256","typ":"JWT"}').toString('base64url')+'.'+Buffer.from(JSON.stringify(claims)).toString('base64url')+'.syntheticSignature';
}
const tokens={a:token('a'),b:token('b')}, allowed=new Set(Object.values(tokens).map(value=>'Bearer '+value));
const profile=id=>crypto.createHash('sha256').update('synthetic-'+id+'\0synthetic-user-'+id).digest('hex');
class Lines{
  constructor(child){this.child=child;this.next=0;this.pending=new Map();this.buffer='';
    this.ready=new Promise((resolve,reject)=>{this.readyResolve=resolve;this.readyReject=reject;});
    // Seed mode has no readiness packet; its rejected ready promise must be observed.
    this.ready.catch(()=>{});child.stderr.on('data',()=>{});
    child.stdout.setEncoding('utf8');child.stdout.on('data',part=>{
      this.buffer+=part;if(this.buffer.length>131072)return this.fail('line_protocol_invalid');
      while(this.buffer.includes('\n')){const cut=this.buffer.indexOf('\n'),line=this.buffer.slice(0,cut);this.buffer=this.buffer.slice(cut+1);
        try{const packet=JSON.parse(line);if(packet.ready===true)this.readyResolve(packet);
          const waiter=this.pending.get(packet.id);if(waiter){clearTimeout(waiter.timer);this.pending.delete(packet.id);
            packet.error?waiter.reject(Object.assign(new Error('Line RPC rejected.'),{kind:'line_rpc_rejected'})):waiter.resolve(packet);}}
        catch{this.fail('line_protocol_invalid');}}
    });child.on('error',()=>this.fail('child_start_failed'));child.on('exit',()=>this.fail('child_exited'));
  }
  fail(kind){this.readyReject(Object.assign(new Error('Fixture child unavailable.'),{kind}));
    for(const waiter of this.pending.values()){clearTimeout(waiter.timer);waiter.reject(Object.assign(new Error('Fixture line failed.'),{kind}));}this.pending.clear();}
  send(packet){const id=++this.next;return new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{this.pending.delete(id);reject(Object.assign(new Error('Fixture operation timeout.'),{kind:'line_timeout'}));},50000);
    this.pending.set(id,{resolve,reject,timer});this.child.stdin.write(JSON.stringify({...packet,id})+'\n');});}
}
function frame(data,opcode=1){const body=Buffer.isBuffer(data)?data:Buffer.from(data),extra=body.length<126?0:body.length<=65535?2:8;
  if(body.length>2*1024*1024)throw new Error('Synthetic frame too large.');
  const h=Buffer.alloc(2+extra+4),mask=crypto.randomBytes(4);h[0]=0x80|opcode;h[1]=0x80|(extra===0?body.length:extra===2?126:127);
  if(extra===2)h.writeUInt16BE(body.length,2);if(extra===8)h.writeBigUInt64BE(BigInt(body.length),2);mask.copy(h,2+extra);
  const bytes=Buffer.from(body);for(let i=0;i<bytes.length;i++)bytes[i]^=mask[i%4];return Buffer.concat([h,bytes]);}
class Rpc{
  constructor(socket){this.socket=socket;this.buffer=Buffer.alloc(0);this.parts=[];this.fragment=false;this.next=0;this.pending=new Map();this.events=[];
    socket.on('data',bytes=>{try{this.receive(bytes);}catch{this.fail();socket.destroy();}});socket.on('error',()=>this.fail());socket.on('close',()=>this.fail());}
  fail(){for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(Object.assign(new Error('Synthetic WS unavailable.'),{kind:'ws_unavailable'}));}this.pending.clear();}
  send(value){this.socket.write(frame(JSON.stringify(value)));}
  call(method,params={}){const id=++this.next;return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{this.pending.delete(id);reject(Object.assign(new Error('WS RPC timeout.'),{kind:'ws_timeout'}));},30000);
    this.pending.set(id,{resolve,reject,timer});this.send({id,method,params});});}
  receive(bytes){this.buffer=Buffer.concat([this.buffer,bytes]);if(this.buffer.length>4*1024*1024)throw new Error('Frame limit.');
    while(this.buffer.length>=2){const first=this.buffer[0],second=this.buffer[1],fin=!!(first&128),op=first&15;if(first&112||second&128)throw new Error('Frame encoding.');
      let length=second&127,offset=2;if(length===126){if(this.buffer.length<4)return;length=this.buffer.readUInt16BE(2);offset=4;}
      if(length===127){if(this.buffer.length<10)return;const long=this.buffer.readBigUInt64BE(2);if(long>2097152n)throw new Error('Frame limit.');length=Number(long);offset=10;}
      if(length>2097152)throw new Error('Frame limit.');if(this.buffer.length<offset+length)return;
      const payload=Buffer.from(this.buffer.subarray(offset,offset+length));this.buffer=this.buffer.subarray(offset+length);
      if(op===9){this.socket.write(frame(payload,10));continue;}if(op===10)continue;if(op===8){this.socket.end();continue;}
      if(op===1){if(this.fragment)throw new Error('Fragment sequence.');this.fragment=true;}else if(op!==0||!this.fragment)throw new Error('Frame type.');
      this.parts.push(payload);if(this.parts.reduce((n,b)=>n+b.length,0)>2097152)throw new Error('Message limit.');if(!fin)continue;
      const packet=JSON.parse(Buffer.concat(this.parts).toString('utf8'));this.parts=[];this.fragment=false;
      const waiter=this.pending.get(packet.id);if(waiter&&!packet.method){this.pending.delete(packet.id);clearTimeout(waiter.timer);
        packet.error?waiter.reject(Object.assign(new Error('Synthetic RPC refused.'),{kind:'rpc_rejected',code:packet.error.code})):waiter.resolve(packet.result);}
      else if(packet.method&&packet.id!==undefined)this.send({id:packet.id,error:{code:-32601,message:'Synthetic client does not refresh.'}});
      else if(['account/updated','account/login/completed'].includes(packet.method))this.events.push({method:packet.method,success:packet.params?.success===true});
    }}
  close(){this.fail();this.socket.destroy();}
}
function connect(endpoint,capability=null,rejected=false,headers=[]){return new Promise((resolve,reject)=>{
  const tcp=typeof endpoint==='number',socket=net.connect(tcp?{host:'127.0.0.1',port:endpoint}:{path:endpoint});
  const key=crypto.randomBytes(16).toString('base64');let data=Buffer.alloc(0),done=false;
  const timer=setTimeout(()=>finish(false,'upgrade_timeout'),5000);
  function finish(ok,kind,value){if(done)return;done=true;clearTimeout(timer);socket.removeListener('data',receive);socket.removeListener('error',error);
    if(!ok)socket.destroy();ok?resolve(value):reject(Object.assign(new Error('Synthetic upgrade failed.'),{kind}));}
  function error(){finish(false,'connect_unavailable');}
  function receive(bytes){data=Buffer.concat([data,bytes]);if(data.length>16384)return finish(false,'upgrade_invalid');const end=data.indexOf('\r\n\r\n');if(end<0)return;
    const lines=data.subarray(0,end).toString('ascii').split('\r\n'),status=Number(lines[0].split(' ')[1]);
    if(rejected){if(status===401||status===403){socket.destroy();return finish(true,null,{status});}return finish(false,'expected_rejection_missing');}
    const accept=lines.find(line=>/^sec-websocket-accept:/i.test(line))?.split(':').slice(1).join(':').trim();
    if(status!==101||accept!==crypto.createHash('sha1').update(key+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64'))return finish(false,'upgrade_rejected');
    const client=new Rpc(socket);finish(true,null,client);if(data.length>end+4)client.receive(data.subarray(end+4));}
  socket.on('error',error);socket.on('data',receive);socket.once('connect',()=>socket.write([
    'GET / HTTP/1.1','Host: '+(tcp?'127.0.0.1:'+endpoint:'localhost'),'Upgrade: websocket','Connection: Upgrade',
    'Sec-WebSocket-Key: '+key,'Sec-WebSocket-Version: 13',...(capability?['Authorization: Bearer '+capability]:[]),...headers].join('\r\n')+'\r\n\r\n'));});}
function listen(server){return new Promise((resolve,reject)=>{server.once('error',()=>reject(Object.assign(new Error('Fixture listen failed.'),{kind:'fixture_listen_failed'})));server.listen(0,'127.0.0.1',()=>resolve(server.address().port));});}
async function port(){const probe=net.createServer(),value=await listen(probe);await new Promise(resolve=>probe.close(resolve));return value;}
async function seedAuth(lines,id,issuer='https://auth.openai.com'){
  const jwt=issuer==='https://auth.openai.com'?tokens[id]:token(id,issuer);
  fs.writeFileSync(path.join(seedHome,'auth.json'),JSON.stringify({auth_mode:'chatgpt',
    tokens:{access_token:jwt,id_token:jwt,refresh_token:'synthetic-refresh-'+id,account_id:'synthetic-'+id}}));
  const response=await lines.send({jsonrpc:'2.0',method:'tools/call',params:{name:'accounts_capture_current',arguments:{label:'Synthetic '+id.toUpperCase()}}});
  check(response.result?.isError===false&&response.result?.structuredContent?.accounts?.some(account=>account.id===profile(id)),'synthetic_profile_saved_'+id+'_'+report.checks.length);
}
function control(pipe,request){return new Promise((resolve,reject)=>{const socket=net.connect({path:pipe});let buffer='';const timer=setTimeout(()=>{socket.destroy();reject(Object.assign(new Error('Control timeout.'),{kind:'control_timeout'}));},50000);
  socket.on('error',()=>{clearTimeout(timer);reject(Object.assign(new Error('Control unavailable.'),{kind:'control_unavailable'}));});socket.once('connect',()=>socket.write(JSON.stringify(request)+'\n'));
  socket.on('data',bytes=>{buffer+=bytes.toString('utf8');if(buffer.length>16384){socket.destroy();clearTimeout(timer);reject(Object.assign(new Error('Control response invalid.'),{kind:'control_invalid'}));return;}
    if(!buffer.includes('\n'))return;clearTimeout(timer);socket.destroy();try{resolve(JSON.parse(buffer.slice(0,buffer.indexOf('\n'))));}catch{reject(Object.assign(new Error('Control response invalid.'),{kind:'control_invalid'}));}});});}
async function start(){
  report.phase='seed_vault';
  seed=spawn(exe,['--mcp-test',root],{cwd:root,windowsHide:true,stdio:['pipe','pipe','pipe']});report.seed_pid=seed.pid;
  const seeder=new Lines(seed);await seeder.send({jsonrpc:'2.0',method:'initialize',params:{}});await seedAuth(seeder,'a');await seedAuth(seeder,'b');
  mock=http.createServer((request,response)=>{const url=new URL(request.url,'http://127.0.0.1');
    if(/\/responses(?:\/|$)/.test(url.pathname))report.model_requested=true;
    if(request.headers.authorization&&!allowed.has(request.headers.authorization)){report.unexpected_authorization=true;request.destroy();return;}
    response.setHeader('Content-Type','application/json');
    if(url.pathname==='/backend-api/wham/config/bundle')response.end('{}');
    else if(url.pathname==='/backend-api/wham/accounts/check'&&allowed.has(request.headers.authorization)){
      response.end(JSON.stringify({accounts:(rejectB?['a']:['a','b']).map(id=>({id:'synthetic-'+id,workspace_backend_origin:'https://example.invalid',account_routing_override:'NO_CONSTRAINT'}))}));
    }else{response.statusCode=404;response.end('{}');}request.resume();});
  const mockPort=await listen(mock),serverPort=await port();
  fs.writeFileSync(path.join(home,'config.toml'),'cli_auth_credentials_store = "ephemeral"\nchatgpt_base_url = "http://127.0.0.1:'+mockPort+'/backend-api"\n[analytics]\nenabled = false\n');
  const env={...process.env,CODEX_HOME:home,NO_PROXY:[process.env.NO_PROXY,'127.0.0.1','localhost'].filter(Boolean).join(',')};
  for(const name of ['OPENAI_API_KEY','CODEX_API_KEY','CODEX_ACCESS_TOKEN','OPENAI_FEDERATION_RULE_ID','OPENAI_IDENTITY_TOKEN_FILE','OPENAI_WORKLOAD_IDENTITY_CONTEXT','CODEX_APP_TOOLS_PIPE_PATH','CODEX_THREAD_ID','CODEX_SESSION_ID','CODEX_APP_SERVER_WS_URL','CODEX_APP_SERVER_FORCE_CLI'])delete env[name];
  report.phase='owned_backend_start';
  server=spawn(cli,['-c','cli_auth_credentials_store="ephemeral"','app-server','--listen','ws://127.0.0.1:'+serverPort,'--ws-auth','capability-token','--ws-token-sha256',hash],
    {cwd:home,env,windowsHide:true,stdio:['ignore','ignore','pipe']});report.backend_pid=server.pid;server.stderr.on('data',()=>{});
  server.on('error',()=>{report.backend_start_failed=true;});
  let probe;
  for(let retry=0;retry<40;retry++){if(server.exitCode!==null)throw Object.assign(new Error('Owned backend exited.'),{kind:'backend_start_failed'});
    try{probe=await connect(serverPort,cap);break;}catch(error){if(error.kind!=='connect_unavailable')throw error;await pause(100);}}
  check(!!probe,'owned_official_backend_ready');probe.close();
  check((await connect(serverPort,null,true)).status===401||(await connect(serverPort,null,true)).status===403,'missing_backend_capability_rejected');
  report.phase='production_components';
  harness=spawn(exe,['--live-test',root],{cwd:root,env,windowsHide:true,stdio:['pipe','pipe','pipe']});report.harness_pid=harness.pid;
  const channel=new Lines(harness);
  harness.stdin.write(JSON.stringify({backendUrl:'ws://127.0.0.1:'+serverPort+'/',capability:cap,backendPid:server.pid,
    profileA:profile('a'),profileB:profile('b'),pipeName})+'\n');
  const ready=await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>reject(Object.assign(new Error('Harness timeout.'),{kind:'harness_timeout'})),45000);
    channel.ready.then(value=>{clearTimeout(timer);resolve(value);},error=>{clearTimeout(timer);reject(error);});
  });
  check(ready.syntheticOnly===true,'production_harness_synthetic_guard');
  const endpoint='\\\\.\\pipe\\'+pipeName;
  check((await connect(endpoint,null,true,['Origin: https://example.invalid'])).status===403,'production_gateway_rejects_origin');
  const observer=await connect(endpoint,null,false,['Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits']);clients.push(observer);
  const second=await connect(endpoint);clients.push(second);
  for(const [client,name] of [[observer,'synthetic_live_observer'],[second,'synthetic_live_second']]){
    await client.call('initialize',{clientInfo:{name,title:'Synthetic production integration',version:'1.0.0'},capabilities:{experimentalApi:true}});
    client.send({method:'initialized',params:{}});
  }
  check((await observer.call('account/read',{refreshToken:false})).workspaceRouting?.chatgptAccountId==='synthetic-a','initial_A_verified_through_production_gateway');
  const status=await control('\\\\.\\pipe\\'+ready.controlPipe,{operation:'status'});
  check(status.ok===true&&status.status?.Connected!==false&&status.status?.connected!==false,'production_control_server_status');
  for(const id of ['b','a']){
    const changed=await channel.send({operation:'switch',profile:profile(id)});
    check(changed.ok===true&&changed.selected===id.toUpperCase(),'production_controller_switch_'+id);
    check((await observer.call('account/read',{refreshToken:false})).workspaceRouting?.chatgptAccountId==='synthetic-'+id,'observer_sees_'+id);
    check((await second.call('account/read',{refreshToken:false})).workspaceRouting?.chatgptAccountId==='synthetic-'+id,'second_client_sees_'+id);
  }
  report.phase='busy_safety';await channel.send({operation:'mock-busy',enabled:true});
  const busy=await channel.send({operation:'switch',profile:profile('b')});
  check(busy.ok===false&&busy.selected==='A','busy_tracker_refuses_switch_without_model_request');
  await channel.send({operation:'mock-busy',enabled:false});
  let blocked=false;try{await observer.call('account/logout');}catch(error){blocked=error.kind==='rpc_rejected'&&error.code===-32601;}
  check(blocked,'native_logout_blocked_by_production_gateway');
  report.phase='credential_validation';await seedAuth(seeder,'b','https://example.invalid');
  const malformed=await channel.send({operation:'switch',profile:profile('b')});
  check(malformed.ok===false&&malformed.selected==='A','invalid_issuer_credential_refused_before_mutation');
  await seedAuth(seeder,'b');
  report.phase='rollback';rejectB=true;
  const rollback=await channel.send({operation:'switch',profile:profile('b')});
  check(rollback.ok===false&&rollback.selected==='A','target_workspace_rejection_restores_A');rejectB=false;
  check((await observer.call('account/read',{refreshToken:false})).workspaceRouting?.chatgptAccountId==='synthetic-a','rollback_A_observed_on_frontend');
  await pause(200);
  check(observer.events.filter(event=>event.method==='account/updated').length>=2,'native_account_notifications_relayed');
  check(observer.events.filter(event=>event.method==='account/login/completed'&&event.success).length>=2,'native_login_completion_notifications_relayed');
  report.phase='ownership_safety';await channel.send({operation:'endpoint',enabled:false});
  check((await channel.send({operation:'switch',profile:profile('b')})).ok===false,'endpoint_ownership_loss_refuses_switch');
  check(!fs.existsSync(path.join(home,'auth.json')),'owned_ephemeral_backend_does_not_write_auth_file');
  check(!report.unexpected_authorization,'mock_receives_only_generated_authorization');
  check(!report.model_requested,'no_inference_request');
  report.passed=true;report.phase='completed';
}
async function waitExit(child,timeout){if(!child||child.exitCode!==null||child.signalCode!==null)return;
  await new Promise(resolve=>{const timer=setTimeout(resolve,timeout);child.once('exit',()=>{clearTimeout(timer);resolve();});});}
async function cleanup(){
  for(const client of clients)client.close();
  if(harness){harness.stdin.end();await waitExit(harness,8000);report.harness_clean_exit=harness.exitCode===0;
    if(!report.harness_clean_exit){report.passed=false;report.failure=report.failure||{kind:'harness_cleanup_failed'};if(harness.exitCode===null&&harness.signalCode===null)harness.kill();}}
  if(seed){seed.stdin.end();await waitExit(seed,4000);if(seed.exitCode===null&&seed.signalCode===null)seed.kill();}
  if(server&&server.exitCode===null&&server.signalCode===null){server.kill();await waitExit(server,4000);}
  if(mock){mock.closeAllConnections();await new Promise(resolve=>mock.close(resolve));}
  fs.writeFileSync(path.join(root,'live-protocol.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify(report));process.exitCode=report.passed?0:1;
}
start().catch(error=>{report.failure={kind:error.kind||'integration_failed',check:error.check||null};}).finally(cleanup);
