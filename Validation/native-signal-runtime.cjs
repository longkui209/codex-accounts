'use strict';
// Optional read-only conformance check. Use only the currently verified official app.asar.
// Vendor modules are read locally, executed in a synthetic realm and never exported.
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path'),crypto=require('node:crypto');
(async()=>{
 const fd=fs.openSync(process.argv[2],'r');
 const timers=new Set();
 try {
  const prefix=Buffer.alloc(16);fs.readSync(fd,prefix,0,16,0);
  const header=Buffer.alloc(prefix.readUInt32LE(12));fs.readSync(fd,header,0,header.length,16);
  const assets=JSON.parse(header).files.webview.files.assets.files,base=8+prefix.readUInt32LE(4);
  const read=name=>{const row=assets[name];if(!row||row.unpacked||row.link||!name.endsWith('.js'))throw Error('Unsupported static module');const b=Buffer.alloc(row.size);fs.readSync(fd,b,0,b.length,base+Number(row.offset));return b.toString('utf8')};
  const sharedNames=Object.keys(assets).filter(name=>/^app-shared-[a-f0-9]+\.js$/.test(name));
  if(sharedNames.length!==1)throw Error('Shared module is not unique');
  const shared=sharedNames[0],source=read(shared);
  const one=pattern=>{const matches=[...source.matchAll(pattern)];if(matches.length!==1)throw Error('Runtime contract is not unique');return matches[0]};
  const signal=one(/function (?<name>[\w$]+)\(e,t,n\)\{let r=[\w$]+\(`signal`,e,/g);
  const family=one(/function (?<name>[\w$]+)\(e,t,n,r\)\{return [\w$]+\([\w$]+\(e,\(e,\{signal:r\}\)=>r\(\(\{scope:n\}\)=>typeof t==`function`\?t\(e,\{get:n\.get,get queryClient\(\)\{return n\.queryClient\},scope:n\}\):t,n\),r\),`signal-family`\)\}/g);
  const scope=one(/function (?<name>[\w$]+)\(e,t,n,r,i,a\)\{let o=r==null\?[\w$]+\(n\):void 0,s=\{chain:t,get:[\w$]+\(t,n,r,i,a\),/g);
  const node=one(/function (?<name>[\w$]+)\(\{parentScope:e,providedValue:t,queryClient:n,scope:r,scopeKey:i\}\)\{return\{cachedBindings:new WeakMap,/g);
  const initializers=[...source.matchAll(/function (?<name>[\w$]+)\(\)\{return\(\k<name>=t\(\(\(\)=>\{/g)];
  const before=entry=>initializers.filter(init=>init.index<entry.index).at(-1)?.groups.name;
  const after=entry=>initializers.find(init=>init.index>entry.index)?.groups.name;
  const setup=[after(node),before(signal),before(family)];
  if(setup.some(name=>!name))throw Error('Runtime initializer was not identified');
  const context=vm.createContext({URL,TextEncoder,TextDecoder,Event,EventTarget,AbortController,AbortSignal,Headers,Request,Response,performance,queueMicrotask,
   setTimeout:(...args)=>{const id=setTimeout(...args);timers.add(id);return id},clearTimeout:id=>{clearTimeout(id);timers.delete(id)},
   setInterval:(...args)=>{const id=setInterval(...args);timers.add(id);return id},clearInterval:id=>{clearInterval(id);timers.delete(id)},
   console:{warn(){},error(){},log(){}},navigator:{userAgent:'Synthetic conformance check'},location:{protocol:'app:',host:'-',href:'app://-/'}});
  const modules=new Map();
  function load(name){
   if(modules.has(name))return modules.get(name);
   let source=read(name);
   if(name===shared)source+='\nexport function __privateProbe(){'+setup.map(init=>init+'();').join('')+'const token={id:Symbol("synthetic"),parent:null};const node='+node.groups.name+'({scope:token,providedValue:{},scopeKey:"synthetic"});const scope='+scope.groups.name+'(token,new Map([[token.id,node]]),node);return {scope,token,signal:'+signal.groups.name+',family:'+family.groups.name+'};}';
   const module=new vm.SourceTextModule(source,{context,identifier:name});modules.set(name,module);return module;
  }
  const module=load(shared);
  await module.link((specifier,referencing)=>{
   if(!specifier.startsWith('./'))throw Error('Nonlocal import rejected');
   const name=path.posix.normalize(path.posix.join(path.posix.dirname(referencing.identifier),specifier));
   if(name.includes('/')||name.includes('..'))throw Error('Module outside assets rejected');return load(name);
  });
  await module.evaluate({timeout:10000});
  const api=module.namespace.__privateProbe();
  let oldFailed=false;
  try{api.scope.get(api.family(api.token,()=>({allowed:false}),()=>{}));}catch{oldFailed=true;}
  let mounted=0,unmounted=0,set;
  const state=api.signal(api.token,()=>({allowed:false}),{onMount:update=>{mounted++;set=update;return()=>{unmounted++}}});
  const seen=[];
  const dispose=api.scope.watch(({get})=>{seen.push(get(state).allowed)});
  if(typeof set!=='function')throw Error('Signal never mounted');
  set({allowed:true});set({allowed:false});dispose();
  let productionPlansVerified=0,workLifecycleVerified=false;
  if(process.argv[3]) {
   const audit=JSON.parse(fs.readFileSync(process.argv[3],'utf8'));
   if(!audit.passed||audit.plans.length!==2)throw Error('Adapter audit did not pass');
   context.__fixtureApi=api;
   const events=new EventTarget();let listeners=0;
   context.addEventListener=(name,callback)=>{listeners++;events.addEventListener(name,callback)};
   context.removeEventListener=(name,callback)=>{listeners--;events.removeEventListener(name,callback)};
   context.dispatchEvent=event=>events.dispatchEvent(event);
   for(const plan of audit.plans) {
    const asset=plan.asset.slice('app://-/assets/'.length);
    const assetSource=read(asset);
    if(crypto.createHash('sha256').update(assetSource).digest('hex')!==plan.hash)throw Error('Audited module changed');
    if(![plan.factory,plan.store].every(name=>/^[A-Za-z_$][\w$]*$/.test(name)))throw Error('Invalid local binding');
    const bindings=[];
    for(const imported of assetSource.matchAll(/import\{([^}]+)\}from["`]\.\/(app-shared-[a-f0-9]+\.js)["`]/g)) {
     if(imported[2]!==shared)continue;
     for(const binding of imported[1].split(',')) {
      const pair=/^([\w$]+) as ([\w$]+)$/.exec(binding);
      if(pair&&pair[2]===plan.factory)bindings.push(pair[1]);
     }
    }
    if(plan.importedSignalExport)bindings.push(plan.importedSignalExport);
    if(bindings.length!==1||typeof module.namespace[bindings[0]]!=='function')throw Error('Actual signal import was not resolved');
    context.__fixtureFactory=module.namespace[bindings[0]];
    const code='(()=>{'+audit.runtimePrelude+'\nconst '+plan.factory+'=__fixtureFactory,'+plan.store+'=__fixtureApi.token;'+plan.signalInitialization+';const observed=[];const dispose=__fixtureApi.scope.watch(({get})=>{const value=__caNativeGateAllows(get,"local");if(observed.at(-1)!==value)observed.push(value)});for(const allowed of [true,false]){globalThis.__codexAccountsInferenceGateV1={version:1,connected:true,allowed,expiresAtMs:Date.now()+10000};globalThis.dispatchEvent(new Event("codex-accounts-inference-gate-v1"))}dispose();delete globalThis.__codexAccountsInferenceGateV1;return observed;})()';
    const observed=new vm.Script(code).runInContext(context,{timeout:10000});
    if(observed.join(',')!=='false,true,false'||listeners!==0)throw Error('Production signal lifecycle failed');
    if(plan.work) {
     const derivedBindings=[];
     for(const imported of assetSource.matchAll(/import\{([^}]+)\}from["`]\.\/(app-shared-[a-f0-9]+\.js)["`]/g)) {
      if(imported[2]!==shared)continue;
      for(const binding of imported[1].split(',')) {
       const pair=/^([\w$]+) as ([\w$]+)$/.exec(binding);
       if(pair&&pair[2]===plan.derived)derivedBindings.push(pair[1]);
      }
     }
     if(plan.importedDerivedExport)derivedBindings.push(plan.importedDerivedExport);
     if(derivedBindings.length!==1||typeof module.namespace[derivedBindings[0]]!=='function')throw Error('Actual derived import was not resolved');
     if(![plan.derived,plan.nativeAtom].every(name=>/^[A-Za-z_$][\w$]*$/.test(name)))throw Error('Invalid Work binding');
     context.__fixtureDerived=module.namespace[derivedBindings[0]];
     const workCode='(()=>{'+audit.runtimePrelude+'\nconst '+plan.factory+'=__fixtureFactory,'+plan.store+'=__fixtureApi.token,'+plan.derived+'=__fixtureDerived,'+plan.nativeAtom+'=__fixtureApi.signal(__fixtureApi.token,()=>true);'+plan.signalInitialization+';const local='+plan.derived+'('+plan.store+','+plan.workCallback+');const observed=[];const dispose=__fixtureApi.scope.watch(({get})=>{const value=get(local);if(observed.at(-1)!==value)observed.push(value)});for(const allowed of [true,false]){globalThis.__codexAccountsInferenceGateV1={version:1,connected:true,allowed,expiresAtMs:Date.now()+10000};globalThis.dispatchEvent(new Event("codex-accounts-inference-gate-v1"))}dispose();delete globalThis.__codexAccountsInferenceGateV1;const original=__fixtureApi.scope.get('+plan.nativeAtom+');return {observed,original};})()';
     const work=new vm.Script(workCode).runInContext(context,{timeout:10000});
     if(work.observed.join(',')!=='true,false,true'||work.original!==true||listeners!==0)throw Error('Production Work subscription failed');
     workLifecycleVerified=true;
    }
    productionPlansVerified++;
   }
  }
  const passed=oldFailed&&mounted===1&&unmounted===1&&seen.join(',')==='false,true,false'&&(!process.argv[3]||productionPlansVerified===2&&workLifecycleVerified);
  console.log(JSON.stringify({passed,oldFamilyReadRejected:oldFailed,mounted,unmounted,transitions:seen,productionPlansVerified,workLifecycleVerified,officialModulesExecuted:modules.size,accountDataRead:false,desktopLaunched:false}));
  if(!passed)process.exitCode=1;
 }finally{for(const id of timers){clearInterval(id);clearTimeout(id)}fs.closeSync(fd)}
})().catch(error=>{console.log(JSON.stringify({passed:false,errorType:error.name,message:error.message,accountDataRead:false}));process.exitCode=1});
