'use strict';
// Exercises production routing components with synthetic credentials and a local fixture.
// No desktop, official model service, real account, or third-party code is launched.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const { spawn } = require('node:child_process');

const exe = process.argv[2];
if (!exe || !path.isAbsolute(exe) || !fs.existsSync(exe)) {
  throw new Error('An absolute path to the built test executable is required.');
}
const parent = path.resolve(__dirname, '..', 'validation-output');
fs.mkdirSync(parent, { recursive: true });
const root = path.join(parent, 'routing-' + crypto.randomUUID());
fs.mkdirSync(root);
const home = path.join(root, 'codex-home');
fs.mkdirSync(home);
const authPath = path.join(home, 'auth.json');
const report = {
  synthetic_only: true,
  real_desktop_connected: false,
  real_subscription_validated: false,
  real_model_requested: false,
  passed: false,
  phase: 'setup',
  checks: [],
  unexpected_authorization: false
};
let seed, harness, mock, relayPort, holdNext = false, rejectNext = false, completeAndHoldNext = false;
let encodingNext = null;
let genericLimitedNext=false,partialQuotaNext=false;
const quotaAccounts=new Set(),earlyQuotaAccounts=new Set();
let parallelQuotaBatch=null;
let earlyQuotaMedia='text/event-stream';
const quotaError=Buffer.from('{"error":{"type":"usage_limit_reached","resets_at":2000000000}}');
const earlyQuota=Buffer.from('event: error\ndata:{"type":"error","error":{"code":"usage_limit_reached"}}\n\n');
let heldResponse = null, holdResolve = null;
const pendingHttp = new Set();
const pendingWs = new Set();
const observations = [];
const wsObservations = [];

function failure(kind, check = null) {
  return Object.assign(new Error('Synthetic routing verification failed.'), { kind, check });
}
function check(ok, label) {
  if (!ok) throw failure('assertion_failed', label);
  report.checks.push(label);
}
function profile(id) {
  return crypto.createHash('sha256').update('synthetic-' + id + '\0synthetic-user-' + id).digest('hex');
}
function token(id, revision = 1) {
  const now = Math.floor(Date.now() / 1000);
  const claims = {
    iss: 'https://auth.openai.com',
    aud: 'app_EMoamEEZ73f0CkXaXp7hrann',
    sub: 'synthetic-user-' + id,
    email: 'synthetic-' + id + '@example.test',
    iat: now, exp: now + 7200, synthetic_revision: revision,
    'https://api.openai.com/auth': {
      chatgpt_account_id: 'synthetic-' + id,
      chatgpt_user_id: 'synthetic-user-' + id,
      chatgpt_plan_type: 'plus'
    }
  };
  return Buffer.from('{"alg":"RS256","typ":"JWT"}').toString('base64url') + '.' +
    Buffer.from(JSON.stringify(claims)).toString('base64url') + '.syntheticSignature';
}
const tokens = { a: token('a'), b: token('b') };
const knownTokens = new Map([['Bearer ' + tokens.a, 'A'], ['Bearer ' + tokens.b, 'B']]);
function writeAuth(id, value = tokens[id]) {
  fs.writeFileSync(authPath, JSON.stringify({ auth_mode: 'chatgpt', tokens: {
    access_token: value, id_token: value, refresh_token: 'synthetic-refresh-' + id,
    account_id: 'synthetic-' + id
  } }));
}
function authHash() { return crypto.createHash('sha256').update(fs.readFileSync(authPath)).digest('hex'); }

class Lines {
  constructor(child) {
    this.child = child;
    this.next = 0;
    this.pending = new Map();
    this.buffer = '';
    this.ready = new Promise((resolve, reject) => { this.resolveReady = resolve; this.rejectReady = reject; });
    this.ready.catch(() => {});
    child.stderr.on('data', () => {});
    child.stdin.on('error', () => this.fail('line_input_closed'));
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', part => {
      this.buffer += part;
      if (this.buffer.length > 131072) return this.fail('line_protocol_invalid');
      while (this.buffer.includes('\n')) {
        const cut = this.buffer.indexOf('\n');
        const line = this.buffer.slice(0, cut);
        this.buffer = this.buffer.slice(cut + 1);
        try {
          const packet = JSON.parse(line);
          if (packet.ready === true) this.resolveReady(packet);
          const waiter = this.pending.get(packet.id);
          if (waiter) {
            clearTimeout(waiter.timer);
            this.pending.delete(packet.id);
            packet.error ? waiter.reject(failure('line_rpc_rejected')) : waiter.resolve(packet);
          }
        } catch { this.fail('line_protocol_invalid'); }
      }
    });
    child.on('error', () => this.fail('child_start_failed'));
    child.on('exit', () => this.fail('child_exited'));
  }
  fail(kind) {
    this.rejectReady(failure(kind));
    for (const waiter of this.pending.values()) {
      clearTimeout(waiter.timer);
      waiter.reject(failure(kind));
    }
    this.pending.clear();
  }
  send(packet) {
    const id = ++this.next;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(failure('line_timeout'));
      }, 15000);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ ...packet, id }) + '\n');
    });
  }
}
function deadline(promise, milliseconds, kind) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(failure(kind)), milliseconds);
    promise.then(value => { clearTimeout(timer); resolve(value); }, error => { clearTimeout(timer); reject(error); });
  });
}
function listen(server) {
  return new Promise((resolve, reject) => {
    server.once('error', () => reject(failure('fixture_listen_failed')));
    server.listen(0, '127.0.0.1', () => resolve(server.address().port));
  });
}
function control(pipeName, packet) {
  return new Promise((resolve, reject) => {
    const socket = net.connect({ path: '\\\\.\\pipe\\' + pipeName });
    let buffer = '', done = false;
    const timer = setTimeout(() => finish(false), 5000);
    function finish(ok, value) {
      if (done) return;
      done = true;
      clearTimeout(timer);
      socket.destroy();
      ok ? resolve(value) : reject(failure('control_unavailable'));
    }
    socket.on('error', () => finish(false));
    socket.once('connect', () => socket.write(JSON.stringify(packet) + '\n'));
    socket.on('data', bytes => {
      buffer += bytes.toString('utf8');
      if (buffer.length > 16384) return finish(false);
      if (!buffer.includes('\n')) return;
      try { finish(true, JSON.parse(buffer.slice(0, buffer.indexOf('\n')))); } catch { finish(false); }
    });
  });
}
function request(base, route, { method = 'POST', bearer = tokens.a, account = 'synthetic-a', body = null, headers = {} } = {}) {
  return new Promise((resolve, reject) => {
    const bytes = body === null ? Buffer.alloc(0) : body;
    const req = http.request(new URL(base + route), { method, agent: false, headers: {
      Authorization: 'Bearer ' + bearer, 'ChatGPT-Account-Id': account,
      ...(method === 'POST' ? { 'Content-Type': 'application/json', 'Content-Length': bytes.length } : {}),
      ...headers
    } }, res => {
      const parts = [];
      let length = 0;
      res.on('data', part => {
        length += part.length;
        if (length > 1024 * 1024) { req.destroy(); return; }
        parts.push(part);
      });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(parts) }));
      res.on('error', () => reject(failure('fixture_response_failed')));
    });
    pendingHttp.add(req);
    req.once('close', () => pendingHttp.delete(req));
    if (Object.entries(headers).some(([name, value]) => name.toLowerCase() === 'connection' &&
      String(value).split(',').some(item => item.trim().toLowerCase() === 'x-synthetic-hop'))) {
      req.once('finish', () => {
        // Inspect the fixture's serialized header; never retain or output its text.
        report.fixture_connection_nominates_hop = typeof req._header === 'string' &&
          req._header.split('\r\n').some(line => {
            const match = /^connection\s*:(.*)$/i.exec(line);
            return !!match && match[1].split(',').some(item => item.trim().toLowerCase() === 'x-synthetic-hop');
          });
      });
    }
    req.on('error', () => reject(failure('fixture_request_failed')));
    req.setTimeout(15000, () => req.destroy());
    // Completing an HTTP body leaves the TCP connection open for the SID lookup.
    req.end(bytes);
  });
}
function closeAfterCompletion(base, body) {
  return new Promise((resolve,reject) => {
    let complete=false, received=Buffer.alloc(0);
    const req=http.request(new URL(base+'/responses'),{method:'POST',agent:false,headers:{
      Authorization:'Bearer '+tokens.a,'ChatGPT-Account-Id':'synthetic-a','Content-Type':'application/json','Content-Length':body.length
    }},res=>{
      res.on('data',part=>{
        received=Buffer.concat([received,part]);
        if(received.length>1024*1024){req.destroy();reject(failure('fixture_response_too_large'));return;}
        if(received.includes(Buffer.from('"type":"response.completed"'))){complete=true;resolve(received);res.destroy();}
      });
      res.on('error',()=>{if(!complete)reject(failure('fixture_completion_missing'));});
    });
    req.on('error',()=>{if(!complete)reject(failure('fixture_request_failed'));});
    pendingHttp.add(req);req.once('close',()=>pendingHttp.delete(req));req.end(body);
  });
}

function wsFrame(payload, opcode = 1, fin = true, masked = true) {
  const bytes = Buffer.isBuffer(payload) ? payload : Buffer.from(payload);
  if (bytes.length > 2 * 1024 * 1024) throw failure('fixture_ws_frame_limit');
  const extra = bytes.length < 126 ? 0 : bytes.length <= 65535 ? 2 : 8;
  const header = Buffer.alloc(2 + extra + (masked ? 4 : 0));
  header[0] = (fin ? 0x80 : 0) | opcode;
  header[1] = (masked ? 0x80 : 0) | (extra === 0 ? bytes.length : extra === 2 ? 126 : 127);
  if (extra === 2) header.writeUInt16BE(bytes.length, 2);
  if (extra === 8) header.writeBigUInt64BE(BigInt(bytes.length), 2);
  const encoded = Buffer.from(bytes);
  if (masked) {
    const mask = crypto.randomBytes(4); mask.copy(header, 2 + extra);
    for (let i = 0; i < encoded.length; i++) encoded[i] ^= mask[i % 4];
  }
  return Buffer.concat([header, encoded]);
}

class FramePeer {
  constructor(socket, expectedMasked, echo = false) {
    this.socket = socket; this.expectedMasked = expectedMasked; this.echo = echo;
    this.buffer = Buffer.alloc(0); this.parts = []; this.kind = null; this.messages = [];
    this.waiters = []; this.closeSent = false; this.closeReceived = false;
    this.closed = new Promise(resolve => { this.resolveClosed = resolve; });
    pendingWs.add(socket); socket.setNoDelay(true);
    socket.on('data', bytes => { try { this.receive(bytes); } catch { this.fail(); } });
    socket.on('error', () => this.fail());
    socket.on('close', () => { pendingWs.delete(socket); this.resolveClosed(this.closeReceived); this.failWaiters(); });
  }
  failWaiters() {
    for (const waiter of this.waiters.splice(0)) waiter.reject(failure('fixture_ws_disconnected'));
  }
  fail() { this.failWaiters(); this.socket.destroy(); }
  next() {
    if (this.messages.length) return Promise.resolve(this.messages.shift());
    return deadline(new Promise((resolve, reject) => this.waiters.push({ resolve, reject })), 5000, 'fixture_ws_message_timeout');
  }
  receive(bytes) {
    this.buffer = Buffer.concat([this.buffer, bytes]);
    if (this.buffer.length > 4 * 1024 * 1024) throw failure('fixture_ws_buffer_limit');
    while (this.buffer.length >= 2) {
      const first = this.buffer[0], second = this.buffer[1], opcode = first & 15;
      const fin = !!(first & 0x80), masked = !!(second & 0x80);
      if ((first & 0x70) || masked !== this.expectedMasked) throw failure('fixture_ws_invalid_frame');
      let size = second & 127, offset = 2;
      if (size === 126) { if (this.buffer.length < 4) return; size = this.buffer.readUInt16BE(2); offset = 4; }
      if (size === 127) {
        if (this.buffer.length < 10) return;
        const wide = this.buffer.readBigUInt64BE(2);
        if (wide > 2097152n) throw failure('fixture_ws_frame_limit');
        size = Number(wide); offset = 10;
      }
      if (size > 2 * 1024 * 1024 || opcode >= 8 && (!fin || size > 125)) throw failure('fixture_ws_frame_limit');
      const maskOffset = offset; if (masked) offset += 4;
      if (this.buffer.length < offset + size) return;
      const payload = Buffer.from(this.buffer.subarray(offset, offset + size));
      if (masked) for (let i = 0; i < size; i++) payload[i] ^= this.buffer[maskOffset + (i % 4)];
      this.buffer = this.buffer.subarray(offset + size);
      if (opcode === 9) { this.socket.write(wsFrame(payload, 10, true, !this.expectedMasked)); continue; }
      if (opcode === 10) continue;
      if (opcode === 8) {
        this.closeReceived = true;
        if (!this.closeSent) { this.closeSent = true; this.socket.write(wsFrame(payload, 8, true, !this.expectedMasked)); }
        this.socket.end(); continue;
      }
      if (opcode !== 0 && opcode !== 1 && opcode !== 2) throw failure('fixture_ws_invalid_frame');
      if (opcode !== 0) { if (this.kind !== null) throw failure('fixture_ws_invalid_continuation'); this.kind = opcode; }
      else if (this.kind === null) throw failure('fixture_ws_invalid_continuation');
      this.parts.push(payload);
      if (this.parts.reduce((total, part) => total + part.length, 0) > 2 * 1024 * 1024) throw failure('fixture_ws_message_limit');
      if (this.echo) this.socket.write(wsFrame(payload, opcode, fin, false));
      if (!fin) continue;
      const message = { opcode: this.kind, bytes: Buffer.concat(this.parts) };
      this.parts = []; this.kind = null;
      const waiter = this.waiters.shift();
      if (waiter) waiter.resolve(message); else if (!this.echo) this.messages.push(message);
    }
  }
  send(payload, opcode = 1, fin = true) { this.socket.write(wsFrame(payload, opcode, fin, !this.expectedMasked)); }
  async close() {
    this.closeSent = true;
    const code = Buffer.alloc(2); code.writeUInt16BE(1000);
    this.socket.write(wsFrame(code, 8, true, !this.expectedMasked));
    return deadline(this.closed, 5000, 'fixture_ws_close_timeout');
  }
}

function connectRealtime(base) {
  return new Promise((resolve, reject) => {
    const url = new URL(base + '/realtime?model=synthetic-realtime');
    const socket = net.connect({ host: '127.0.0.1', port: Number(url.port) });
    const key = crypto.randomBytes(16).toString('base64');
    let buffer = Buffer.alloc(0), done = false;
    const timer = setTimeout(() => finish(false), 5000);
    function finish(ok, value) {
      if (done) return; done = true; clearTimeout(timer);
      socket.removeListener('data', handshake); socket.removeListener('error', failed);
      if (!ok) { socket.destroy(); reject(failure('fixture_ws_upgrade_failed')); } else resolve(value);
    }
    function failed() { finish(false); }
    function handshake(bytes) {
      buffer = Buffer.concat([buffer, bytes]);
      if (buffer.length > 32768) return finish(false);
      const end = buffer.indexOf('\r\n\r\n'); if (end < 0) return;
      const lines = buffer.subarray(0, end).toString('latin1').split('\r\n');
      const accept = lines.find(line => /^sec-websocket-accept:/i.test(line))?.split(':').slice(1).join(':').trim();
      if (!/^HTTP\/1\.1 101\b/.test(lines[0]) || accept !== crypto.createHash('sha1')
        .update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64')) return finish(false);
      const peer = new FramePeer(socket, false);
      finish(true, peer); if (buffer.length > end + 4) peer.receive(buffer.subarray(end + 4));
    }
    socket.on('data', handshake); socket.once('error', failed);
    socket.once('connect', () => socket.write([
      'GET ' + url.pathname + url.search + ' HTTP/1.1', 'Host: 127.0.0.1:' + url.port,
      'Connection: Upgrade, X-Synthetic-Hop', 'Upgrade: websocket', 'Sec-WebSocket-Version: 13',
      'Sec-WebSocket-Key: ' + key, 'Authorization: Bearer ' + tokens.a, 'ChatGPT-Account-Id: synthetic-a',
      'User-Agent: Synthetic-Realtime-Fixture', 'X-OpenAI-Actor-Authorization: synthetic-actor-bound-to-A',
      'X-Synthetic-Hop: discard-me'
    ].join('\r\n') + '\r\n\r\n'));
  });
}

const body = Buffer.from(JSON.stringify({
  model: 'synthetic-model', stream: true,
  reasoning: { effort: 'high', summary: 'auto' },
  input: [{ role: 'user', content: [{ type: 'input_text', text: '现场合成验证' }] }],
  tools: [{ type: 'function', name: 'synthetic_tool', parameters: { type: 'object' } }],
  parallel_tool_calls: true, store: false, synthetic_extension: { keep: ['exact', 1] }
}));
const sse = Buffer.from('event: response.created\ndata: {"type":"response.created","response":{"id":"synthetic-response"}}\n\n' +
  'event: response.output_text.delta\ndata: {"type":"response.output_text.delta","delta":"合成输出"}\n\n' +
  'event: response.completed\ndata: {"type":"response.completed","response":{"id":"synthetic-response","model":"synthetic-model","usage":{"input_tokens":1,"output_tokens":1}}}\n\n');

async function start() {
  report.phase = 'synthetic_seed';
  seed = spawn(exe, ['--mcp-test', root], { cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  report.seed_pid = seed.pid;
  const seeder = new Lines(seed);
  await seeder.send({ jsonrpc: '2.0', method: 'initialize', params: {} });
  for (const id of ['a', 'b']) {
    writeAuth(id);
    const captured = await seeder.send({ jsonrpc: '2.0', method: 'tools/call', params: {
      name: 'accounts_capture_current', arguments: { label: 'Synthetic ' + id.toUpperCase() }
    } });
    check(captured.result?.isError === false &&
      captured.result?.structuredContent?.accounts?.some(account => account.id === profile(id)),
    'synthetic_profile_' + id.toUpperCase() + '_saved_with_production_DPAPI');
  }
  writeAuth('a');
  const initialHash = authHash();
  seed.stdin.end();
  await waitExit(seed, 5000);
  check(seed.exitCode === 0, 'isolated_seeder_clean_exit');

  report.phase = 'owned_mock';
  mock = http.createServer((req, res) => {
    const label = knownTokens.get(req.headers.authorization);
    const accountCorrect = label && req.headers['chatgpt-account-id'] === 'synthetic-' + label.toLowerCase();
    if (!accountCorrect) {
      report.unexpected_authorization = true;
      req.resume();
      res.writeHead(401); res.end('{}'); return;
    }
    const parts = [];
    let length = 0;
    req.on('data', bytes => { length += bytes.length; if (length <= 1024 * 1024) parts.push(bytes); else req.destroy(); });
    req.on('error', () => {});
    req.on('end', () => {
      const observation = {
        label, url: req.url, body: Buffer.concat(parts),
        privateHeadersRemoved: !req.headers.cookie && !req.headers['x-openai-actor-authorization'] && !req.headers['x-synthetic-hop'],
        cookieAbsent: req.headers.cookie === undefined,
        actorAbsent: req.headers['x-openai-actor-authorization'] === undefined,
        hopAbsent: req.headers['x-synthetic-hop'] === undefined,
        connectionNominatesHop: (req.headers.connection || '').split(',')
          .some(value => value.trim().toLowerCase() === 'x-synthetic-hop'),
        safeHeaderPreserved: req.headers['user-agent'] === 'Synthetic-Routing-Fixture',
        uncompressedResponseRequested: req.headers['accept-encoding'] === 'identity',
        desktopActorPreserved: req.headers['x-openai-actor-authorization'] === 'synthetic-actor-bound-to-A'
        , sticky: req.headers['x-codex-turn-state'] || null, routingHint: req.headers['x-codex-routing-hint'] || null, contentEncoding:req.headers['content-encoding']||null
      };
      observations.push(observation);
      if (req.url.startsWith('/backend-api/codex/models')) {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end('{"models":[{"slug":"synthetic-model"}]}'); return;
      }
      if (req.url === '/backend-api/codex/realtime/calls' || req.url === '/backend-api/codex/alpha/notes/v2/read_file') {
        res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{"synthetic":true}'); return;
      }
      if(req.url==='/backend-api/codex/responses/compact'){
        res.writeHead(200,{'Content-Type':'application/json'});res.end('{"output":[]}');return;
      }
      if (req.url !== '/backend-api/codex/responses') { res.writeHead(404); res.end('{}'); return; }
      if(genericLimitedNext){genericLimitedNext=false;res.writeHead(429,{'Content-Type':'application/json'});res.end('{"error":{"type":"rate_limit_exceeded"}}');return;}
      if(quotaAccounts.has(label)){
        if(label==='A'&&parallelQuotaBatch){
          parallelQuotaBatch.push(res);
          if(parallelQuotaBatch.length===2){const batch=parallelQuotaBatch;parallelQuotaBatch=null;for(const response of batch){response.writeHead(429,{'Content-Type':'application/json'});response.end(quotaError);}}
          return;
        }
        res.writeHead(429,{'Content-Type':'application/json'});res.end(quotaError);return;
      }
      if(earlyQuotaAccounts.has(label)){res.writeHead(200,{'Content-Type':earlyQuotaMedia});res.end(earlyQuota);return;}
      if(partialQuotaNext){partialQuotaNext=false;res.writeHead(200,{'Content-Type':'text/event-stream'});res.end(Buffer.concat([Buffer.from('data:{"type":"response.output_text.delta","delta":"synthetic partial"}\n\n'),earlyQuota]));return;}
      if (rejectNext) { rejectNext = false; res.writeHead(401, { 'Content-Type': 'application/json' }); res.end('{"error":"synthetic_rejection"}'); return; }
      const encoding=encodingNext;encodingNext=null;
      res.writeHead(200, { 'Content-Type': 'text/event-stream',...(encoding?{'Content-Encoding':encoding}:{}),
        'Connection': 'X-Synthetic-Response-Hop', 'X-Synthetic-Response-Hop': 'discard-me',
        'Set-Cookie': 'synthetic=discard', 'X-Synthetic-Response-Safe': 'preserve-me' });
      if(encoding){res.end(encoding==='gzip'?zlib.gzipSync(sse):encoding==='br'?zlib.brotliCompressSync(sse):sse);return;}
      if (completeAndHoldNext) {
        completeAndHoldNext = false;
        res.write(sse.toString('utf8').replaceAll('data: ', 'data:'));
        return;
      }
      res.write(sse.subarray(0, 13));
      if (holdNext) {
        holdNext = false;
        heldResponse = res;
        holdResolve?.();
      } else res.end(sse.subarray(13));
    });
  });
  mock.on('upgrade', (req, socket, head) => {
    if (req.headers.authorization !== 'Bearer ' + tokens.a || req.headers['chatgpt-account-id'] !== 'synthetic-a'
      || !req.url.startsWith('/backend-api/codex/realtime?')) {
      report.unexpected_authorization = true;
      socket.end('HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n'); return;
    }
    wsObservations.push({ label: 'A', actorPreserved: req.headers['x-openai-actor-authorization'] === 'synthetic-actor-bound-to-A',
      hopAbsent: req.headers['x-synthetic-hop'] === undefined,
      userAgentPreserved: req.headers['user-agent'] === 'Synthetic-Realtime-Fixture' });
    const key = req.headers['sec-websocket-key'];
    socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: '
      + crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64') + '\r\n\r\n');
    const peer = new FramePeer(socket, true, true); if (head.length) peer.receive(head);
  });
  const mockPort = await listen(mock);
  report.mock_owner_pid = process.pid;
  report.phase = 'production_harness';
  harness = spawn(exe, ['--routing-test', root], { cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  report.harness_pid = harness.pid;
  const channel = new Lines(harness);
  harness.stdin.write(JSON.stringify({ upstreamURL: 'http://127.0.0.1:' + mockPort + '/backend-api/codex/',
    profileA: profile('a'), profileB: profile('b'), upstreamPid: process.pid }) + '\n');
  const ready = await deadline(channel.ready, 20000, 'harness_ready_timeout');
  check(ready.syntheticOnly === true && ready.realDesktopConnected === false && ready.upstreamOwned === true,
    'production_harness_rejects_real_credentials_and_requires_owned_mock');
  const base = ready.relayUrl.replace(/\/$/, '');
  relayPort = Number(new URL(base).port);
  check(new URL(base).hostname === '127.0.0.1', 'production_relay_is_ipv4_loopback_only');
  const pipeStatus = await control(ready.controlPipe, { operation: 'status' });
  check(pipeStatus.ok === true && pipeStatus.status?.connected === false,
    'production_current_user_control_pipe_published');
  check((await channel.send({ operation: 'status' })).ok === true,
    'production_live_client_verifies_descriptor_and_control_server_PID');

  const refusedBeforeBinding = await channel.send({ operation: 'switch', profile: profile('b') });
  check(!refusedBeforeBinding.ok && refusedBeforeBinding.selected === 'A', 'selection_requires_authenticated_native_frontend');
  const countBeforeDenied = observations.length;
  await request(base, '/responses', { body, bearer: 'synthetic-invalid' });
  const beforeBinding = await channel.send({ operation: 'status' });
  check(!beforeBinding.connected && !beforeBinding.nativeConnectionObserved && observations.length === countBeforeDenied,
    'invalid_desktop_credentials_cannot_establish_native_binding');
  rejectNext = true;
  const initialRejected = await request(base, '/responses', { body, headers: {
    Connection: 'keep-alive, X-Synthetic-Hop', 'X-Synthetic-Hop': 'discard-me'
  } });
  const boundAfterRejection = await channel.send({ operation: 'status' });
  check(initialRejected.status === 401 && boundAfterRejection.connected && boundAfterRejection.nativeConnectionObserved &&
    boundAfterRejection.lastCompleted === 'unknown' && boundAfterRejection.failureCode === 'upstream-rejected' &&
    boundAfterRejection.failureHttpStatus === 401, 'upstream_rejection_binds_authenticated_frontend_without_claiming_success');
  const recoverSelection = await channel.send({ operation: 'switch', profile: profile('b') });
  check(recoverSelection.ok && recoverSelection.selected === 'B' && recoverSelection.failureCode === null &&
    recoverSelection.verificationState === 'unverified', 'failed_first_request_does_not_deadlock_account_selection');
  check((await channel.send({ operation: 'switch', profile: profile('a') })).ok, 'bootstrap_fixture_returns_to_A_without_model_replay');

  report.phase = 'request_integrity';
  const responseA = await request(base, '/responses', { body, headers: {
    Cookie: 'synthetic=discard', 'X-OpenAI-Actor-Authorization': 'synthetic-actor',
    Connection: 'keep-alive, X-Synthetic-Hop', 'X-Synthetic-Hop': 'discard-me', 'User-Agent': 'Synthetic-Routing-Fixture'
  } });
  check(responseA.status === 200 && responseA.body.equals(sse) && observations.at(-1).label === 'A',
    'A_request_and_SSE_bytes_pass_through_production_relay');
  check(observations.at(-1).body.equals(body), 'model_reasoning_tools_and_unknown_fields_are_byte_identical');
  check(observations.at(-1).uncompressedResponseRequested, 'identity_response_encoding_preserves_observable_SSE_without_decoding_content');
  const hopEvidence = await channel.send({ operation: 'evidence' });
  report.kestrel_connection_nominates_hop = hopEvidence.incomingNominationObserved === true;
  report.header_filters = {
    requestPrivateRemoved: observations.at(-1).privateHeadersRemoved,
    safeRequestPreserved: observations.at(-1).safeHeaderPreserved,
    responseHopAbsent: responseA.headers['x-synthetic-response-hop'] === undefined,
    cookieAbsent: responseA.headers['set-cookie'] === undefined,
    safeResponsePreserved: responseA.headers['x-synthetic-response-safe'] === 'preserve-me'
  };
  report.request_private_headers = {
    CookieAbsent: observations.at(-1).cookieAbsent,
    ActorAbsent: observations.at(-1).actorAbsent,
    HopAbsent: observations.at(-1).hopAbsent,
    ConnectionNominatesHop: observations.at(-1).connectionNominatesHop
  };
  check(Object.values(report.header_filters).every(value => value === true),
    'credential_cookie_and_connection_headers_are_filtered');
  check((await channel.send({ operation: 'switch', profile: profile('b') })).selected === 'B', 'production_controller_selects_B');
  const responseB = await request(base, '/responses', { body });
  check(responseB.status === 200 && responseB.body.equals(sse) && observations.at(-1).label === 'B' && observations.at(-1).body.equals(body),
    'B_credential_replaces_only_authorization_and_account_header');
  const models = await request(base, '/models?client_version=synthetic%20client', { method: 'GET' });
  check(models.status === 200 && observations.at(-1).label === 'A' &&
    observations.at(-1).url.endsWith('?client_version=synthetic%20client'), 'native_model_catalog_and_query_stay_on_desktop_A');
  check((await channel.send({ operation: 'switch', profile: profile('a') })).selected === 'A', 'production_controller_returns_to_A');
  const returnA = await request(base, '/responses', { body });
  check(returnA.status === 200 && observations.at(-1).label === 'A', 'synthetic_A_B_A_requests_verified');
  const evidence = await channel.send({ operation: 'evidence' });
  check(evidence.lastUsed === 'A' && evidence.lastCompleted === 'A' && evidence.activeRequests === 0 &&
    evidence.blockedOAuthAttempts === 0, 'completed_SSE_evidence_is_observed_without_OAuth');
  check(authHash() === initialHash, 'subscription_selection_and_requests_do_not_modify_desktop_auth');

  report.phase = 'frontend_access_control';
  let before = observations.length;
  check((await request(base, '/responses', { body, bearer: 'synthetic-invalid' })).status === 401,
    'invalid_frontend_bearer_rejected');
  check((await request(base, '/responses', { body, bearer: tokens.b, account: 'synthetic-b' })).status === 401,
    'selected_subscription_B_cannot_authorize_frontend');
  check((await request(base, '/responses', { body, account: 'synthetic-b' })).status === 401,
    'wrong_frontend_account_id_rejected');
  check((await request(base, '/responses', { body, headers: { Origin: 'https://example.invalid' } })).status === 403,
    'browser_origin_rejected');
  check((await channel.send({ operation: 'status' })).failureCode === 'browser-origin', 'local_access_refusal_has_separate_safe_diagnostic');
  check((await request(base, '/arbitrary-route', { body })).status === 404,
    'arbitrary_upstream_path_rejected');
  check((await request(base, '/responses', { method: 'GET', headers: {
    Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13',
    'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64')
  } })).status === 426, 'websocket_upgrade_returns_official_HTTP_fallback_status');
  check(observations.length === before, 'rejected_requests_never_reach_upstream');
  check((await request(base, '/responses', { body })).body.equals(sse), 'full_HTTP_request_succeeds_after_WS_fallback');

  report.phase = 'active_request_safety';
  holdNext = true;
  const active = new Promise(resolve => { holdResolve = resolve; });
  const streaming = request(base, '/responses', { body });
  streaming.catch(() => {});
  await deadline(active, 5000, 'held_stream_timeout');
  const busyStatus = await channel.send({ operation: 'status' });
  const refusedSwitch = await channel.send({ operation: 'switch', profile: profile('b') });
  check(busyStatus.busy === true && busyStatus.activeRequests === 1 && refusedSwitch.ok === false && refusedSwitch.selected === 'A',
    'active_production_stream_refuses_account_change');
  const busyRemoval=await channel.send({operation:'remove',profile:profile('b')});
  check(!busyRemoval.ok && busyRemoval.savedProfiles.length===2,'active_production_stream_refuses_inactive_account_removal');
  heldResponse.end(sse.subarray(13)); heldResponse = null; holdResolve = null;
  check((await streaming).body.equals(sse), 'busy_stream_finishes_with_original_account_and_bytes');

  report.phase = 'latest_desktop_credential';
  const old = tokens.a;
  tokens.a = token('a', 2);
  knownTokens.set('Bearer ' + tokens.a, 'A');
  writeAuth('a');
  const rotatedHash = authHash();
  before = observations.length;
  check((await request(base, '/responses', { body, bearer: old })).status === 401 && observations.length === before,
    'initial_desktop_token_is_rejected_after_native_token_rotation');
  check((await request(base, '/responses', { body })).status === 200 && observations.at(-1).label === 'A',
    'latest_desktop_file_token_is_used_without_independent_A_refresh');

  report.phase = 'upstream_auth_failure';
  const selectedB = await channel.send({ operation: 'switch', profile: profile('b') });
  check(selectedB.ok === true && selectedB.selected === 'B', 'B_selected_before_upstream_rejection');
  rejectNext = true; before = observations.length;
  const rejected = await request(base, '/responses', { body });
  const afterReject = await channel.send({ operation: 'evidence' });
  check(rejected.status === 403 && observations.length === before + 1 && observations.at(-1).label === 'B' &&
    afterReject.selected === 'B' && afterReject.blockedOAuthAttempts === 1,
    'B_401_does_not_refresh_A_replay_or_change_selection');
  check(authHash() === rotatedHash, 'upstream_B_auth_failure_preserves_latest_desktop_auth');
  check(afterReject.connected && afterReject.failureCode === 'selected-rejected' && afterReject.failureHttpStatus === 401 &&
    afterReject.verificationState === 'unverified' && afterReject.lastCompleted === 'unknown',
    'selected_account_401_reports_true_upstream_status_without_disconnecting_or_reusing_old_completion');

  report.phase = 'desktop_A_realtime_and_history';
  const evidenceBeforeDesktop = await channel.send({ operation: 'evidence' });
  const desktopBody = Buffer.from('{"synthetic":true,"data":"opaque desktop A request"}');
  for (const [route, label] of [['/realtime/calls', 'realtime_call'], ['/alpha/notes/v2/read_file', 'history_notes']]) {
    const response = await request(base, route, { body: desktopBody,
      headers: { 'X-OpenAI-Actor-Authorization': 'synthetic-actor-bound-to-A' } });
    check(response.status === 200 && response.body.equals(Buffer.from('{"synthetic":true}')) &&
      observations.at(-1).label === 'A' && observations.at(-1).body.equals(desktopBody),
    label + '_HTTP_preserves_desktop_A_and_original_bytes_while_B_selected');
    check(observations.at(-1).desktopActorPreserved, label + '_HTTP_preserves_A_bound_actor_header');
  }
  const ws = await connectRealtime(base);
  check(wsObservations.length === 1 && wsObservations[0].label === 'A', 'Realtime_WS_101_uses_latest_A_while_B_selected');
  check(wsObservations[0].actorPreserved && wsObservations[0].hopAbsent && wsObservations[0].userAgentPreserved,
    'Realtime_WS_preserves_A_actor_and_native_header_but_strips_raw_Connection_nomination');
  const textBytes = Buffer.from('synthetic 现场语音🙂');
  ws.send(textBytes.subarray(0, 11), 1, false); ws.send(textBytes.subarray(11), 0, true);
  const echoedText = await ws.next();
  check(echoedText.opcode === 1 && echoedText.bytes.equals(textBytes), 'Realtime_fragmented_UTF8_bytes_are_identical');
  const binaryBytes = Buffer.alloc(40000); for (let i = 0; i < binaryBytes.length; i++) binaryBytes[i] = i % 251;
  ws.send(binaryBytes, 2);
  const echoedBinary = await ws.next();
  check(echoedBinary.opcode === 2 && echoedBinary.bytes.equals(binaryBytes), 'Realtime_binary_larger_than_buffer_is_identical');
  const realtimeStatus = await channel.send({ operation: 'status' });
  const realtimeSwitch = await channel.send({ operation: 'switch', profile: profile('a') });
  check(realtimeStatus.busy === true && realtimeStatus.activeRequests === 1 && realtimeSwitch.ok === false &&
    realtimeSwitch.selected === 'B', 'active_desktop_A_Realtime_prevents_subscription_change');
  check(await ws.close(), 'Realtime_close_handshake_finishes');
  let evidenceAfterDesktop;
  for (let attempt = 0; attempt < 25; attempt++) {
    evidenceAfterDesktop = await channel.send({ operation: 'evidence' });
    if (evidenceAfterDesktop.activeRequests === 0) break;
    await new Promise(resolve => setTimeout(resolve, 80));
  }
  check(evidenceAfterDesktop.activeRequests === 0 && evidenceAfterDesktop.selected === 'B',
    'Realtime_disconnect_releases_request_lease_without_changing_selected_B');
  check(evidenceAfterDesktop.lastUsed === evidenceBeforeDesktop.lastUsed &&
    evidenceAfterDesktop.lastCompleted === evidenceBeforeDesktop.lastCompleted,
  'desktop_A_realtime_and_history_do_not_claim_B_inference_completion');
  check(authHash() === rotatedHash, 'Realtime_and_history_preserve_latest_desktop_auth_file');
  check(!report.unexpected_authorization, 'mock_received_only_generated_synthetic_credentials');
  completeAndHoldNext = true;
  const earlyCount=observations.length;
  await deadline(closeAfterCompletion(base,body),5000,'terminal_delivery_timeout');
  let delivered;
  for(let attempt=0;attempt<30;attempt++){
    delivered=await channel.send({operation:'evidence'});
    if(delivered.activeRequests===0)break;
    await new Promise(resolve=>setTimeout(resolve,30));
  }
  check(delivered.activeRequests===0 && delivered.lastCompleted==='B' && delivered.verificationState==='completed' &&
    observations.length===earlyCount+1, 'delivered_terminal_without_optional_data_space_survives_native_client_close_before_upstream_EOF');
  for(const encoding of ['identity','gzip','br']){
    encodingNext=encoding;
    const reply=await request(base,'/responses',{body});
    const expected=encoding==='gzip'?zlib.gzipSync(sse):encoding==='br'?zlib.brotliCompressSync(sse):sse;
    const observed=await channel.send({operation:'evidence'});
    check(reply.status===200 && reply.body.equals(expected) && reply.headers['content-encoding']===encoding &&
      observed.lastCompleted==='B' && observed.verificationState==='completed' && observed.responseEncoding===encoding && observed.responseFormat==='sse',
      'encoded_'+encoding+'_response_wire_bytes_preserved_and_completed');
  }
  const compactReply=await request(base,'/responses/compact',{body});
  const afterCompact=await channel.send({operation:'evidence'});
  check(compactReply.status===200 && observations.at(-1).label==='B' && afterCompact.lastCompleted==='B' &&
    afterCompact.verificationState==='completed' && afterCompact.responseFormat==='sse',
    'selected_B_compaction_keeps_last_model_completion_evidence');
  await channel.send({ operation: 'binding-current', current: false });
  const staleBinding = await channel.send({ operation: 'status' });
  check(!staleBinding.connected && !staleBinding.nativeConnectionObserved && staleBinding.failureCode === 'native-session-ended',
    'exited_authenticated_backend_cannot_leave_a_stale_connected_badge');
  check(!(await channel.send({ operation: 'switch', profile: profile('a') })).ok,
    'selection_is_refused_after_authenticated_backend_exits');
  await channel.send({ operation: 'binding-current', current: true });
  check(!(await channel.send({ operation: 'status' })).connected, 'new_backend_liveness_alone_does_not_restore_authentication_proof');
  await request(base, '/models?client_version=synthetic', {method:'GET'});
  check((await channel.send({ operation: 'status' })).connected, 'fresh_authenticated_request_restores_current_native_binding');
  const currentRemoval=await channel.send({operation:'remove',profile:profile('b')});
  check(!currentRemoval.ok && currentRemoval.savedProfiles.length===2 && currentRemoval.selected==='B',
    'removal_refuses_current_selected_account_without_switching_or_deleting');
  const desktopRemoval=await channel.send({operation:'remove',profile:profile('a')});
  check(!desktopRemoval.ok && desktopRemoval.savedProfiles.length===2,'removal_refuses_native_desktop_login_even_when_B_selected');
  await channel.send({operation:'switch',profile:profile('a')});
  const removed=await channel.send({operation:'remove',profile:profile('b')});
  check(removed.ok && removed.savedProfiles.length===1 && removed.savedProfiles[0]==='A' && removed.selected==='A',
    'authenticated_control_archives_only_inactive_B_under_selection_gate');
  const restored=await channel.send({operation:'restore-removed'});
  check(restored.ok && restored.savedProfiles.length===2 && restored.selected==='A' && authHash()===rotatedHash,
    'removal_undo_restores_saved_B_without_changing_desktop_auth_or_selection');
  report.phase='seamless_quota_rotation';
  let rotationStatus=await channel.send({operation:'status'});
  check(rotationStatus.autoRotationSupported && !rotationStatus.autoRotationEnabled,'quota_rotation_is_explicitly_off_by_default');
  quotaAccounts.add('A');
  let requestCount=observations.length;
  const offQuota=await request(base,'/responses',{body});
  check(offQuota.status===429 && offQuota.body.equals(quotaError) && observations.length===requestCount+1 &&
    (await channel.send({operation:'status'})).selected==='A','disabled_rotation_returns_original_quota_error_without_another_account');
  check((await channel.send({operation:'auto-rotation',enabled:true})).autoRotationEnabled,'authenticated_user_toggle_enables_rotation');
  const continuation=Buffer.from(JSON.stringify({model:'synthetic-model',stream:true,store:false,reasoning:{effort:'max'},
    input:[{role:'user',content:[{type:'input_text',text:'continue the project at its current step'}]},
      {type:'function_call',call_id:'synthetic-project-tool',name:'synthetic_tool',arguments:'{}'},
      {type:'function_call_output',call_id:'synthetic-project-tool',output:'prior project changes and tool results'}],synthetic_extension:{unchanged:true}}));
  requestCount=observations.length;
  const resumed=await request(base,'/responses',{body:continuation,headers:{'X-Codex-Turn-State':'synthetic-A-sticky','X-Codex-Routing-Hint':'synthetic-A-routing'}});
  rotationStatus=await channel.send({operation:'status'});
  check(resumed.status===200 && resumed.body.equals(sse) && observations.length===requestCount+2 &&
    observations[requestCount].label==='A' && observations[requestCount+1].label==='B',
    'one_native_request_hides_A_quota_rejection_and_finishes_with_B_without_user_retry');
  check(observations[requestCount].body.equals(continuation) && observations[requestCount+1].body.equals(continuation) &&
    observations[requestCount+1].sticky===null && observations[requestCount+1].routingHint===null && authHash()===rotatedHash,
    'project_context_tool_results_model_effort_and_raw_bytes_survive_rotation_without_desktop_auth_change');
  check(rotationStatus.selected==='B' && rotationStatus.lastCompleted==='B' && rotationStatus.lastRotationFrom==='A' &&
    rotationStatus.lastRotationTo==='B' && rotationStatus.lastRotationState==='completed','rotation_records_actual_B_completion_separately_from_selection');
  requestCount=observations.length;
  check((await request(base,'/responses',{body:continuation})).status===200 && observations.length===requestCount+1 && observations.at(-1).label==='B',
    'next_project_step_keeps_B_without_returning_to_the_exhausted_account');
  await channel.send({operation:'switch',profile:profile('a')});
  parallelQuotaBatch=[];requestCount=observations.length;
  const parallelResults=await Promise.all([request(base,'/responses',{body:continuation}),request(base,'/responses',{body:continuation})]);
  check(parallelResults.every(response=>response.status===200&&response.body.equals(sse))&&observations.length===requestCount+4&&
    observations.slice(requestCount).filter(row=>row.label==='A').length===2&&observations.slice(requestCount).filter(row=>row.label==='B').length===2,
    'parallel_sessions_both_resume_after_A_quota_even_when_first_rotation_already_selected_B');
  await channel.send({operation:'auto-rotation',enabled:false});quotaAccounts.add('B');requestCount=observations.length;
  check((await request(base,'/responses',{body})).status===429 && observations.length===requestCount+1 &&
    (await channel.send({operation:'status'})).selected==='B','turning_rotation_off_restores_single_account_behavior');
  quotaAccounts.clear();await channel.send({operation:'switch',profile:profile('a')});await channel.send({operation:'auto-rotation',enabled:true});
  earlyQuotaAccounts.add('A');requestCount=observations.length;
  const earlyResumed=await request(base,'/responses',{body:continuation});earlyQuotaAccounts.clear();
  check(earlyResumed.status===200 && earlyResumed.body.equals(sse) && observations.length===requestCount+2 &&
    (await channel.send({operation:'status'})).selected==='B','early_SSE_quota_error_is_handled_before_any_error_is_exposed_to_native_client');
  await channel.send({operation:'switch',profile:profile('a')});earlyQuotaAccounts.add('A');earlyQuotaMedia='text/plain';requestCount=observations.length;
  const untypedQuotaResumed=await request(base,'/responses',{body:continuation});earlyQuotaAccounts.clear();earlyQuotaMedia='text/event-stream';
  check(untypedQuotaResumed.status===200&&untypedQuotaResumed.body.equals(sse)&&observations.length===requestCount+2,
    'nonstandard_response_media_type_does_not_hide_explicit_pre_output_quota_error');
  await channel.send({operation:'switch',profile:profile('a')});quotaAccounts.add('A');requestCount=observations.length;
  const compressedContinuation=zlib.gzipSync(continuation);
  const compressedResumed=await request(base,'/responses',{body:compressedContinuation,headers:{'Content-Encoding':'gzip'}});quotaAccounts.clear();
  check(compressedResumed.status===200 && compressedResumed.body.equals(sse) && observations.length===requestCount+2 &&
    observations[requestCount].body.equals(compressedContinuation) && observations[requestCount+1].body.equals(compressedContinuation) &&
    observations[requestCount+1].contentEncoding==='gzip','compressed_project_context_is_retried_byte_identically_without_reencoding');
  requestCount=observations.length;genericLimitedNext=true;
  check((await request(base,'/responses',{body})).status===429 && observations.length===requestCount+1,
    'generic_rate_limit_never_triggers_cross_account_replay');
  requestCount=observations.length;partialQuotaNext=true;
  const partial=await request(base,'/responses',{body});
  check(partial.body.includes(Buffer.from('synthetic partial')) && observations.length===requestCount+1 &&
    (await channel.send({operation:'status'})).lastCompleted==='unknown','already_started_response_is_never_replayed_on_another_account');
  await channel.send({operation:'rotation-candidates',allowed:false});quotaAccounts.add('B');requestCount=observations.length;
  check((await request(base,'/responses',{body})).status===429 && observations.length===requestCount+1,
    'no_eligible_account_stops_once_without_rotation_to_self_or_an_infinite_loop');
  check((await channel.send({operation:'status'})).failureCode==='quota-exhausted','all_accounts_unavailable_has_explicit_quota_diagnostic');
  quotaAccounts.clear();await channel.send({operation:'auto-rotation',enabled:false});
  report.phase = 'completed';
  report.passed = true;
}

async function waitExit(child, timeout) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  await new Promise(resolve => {
    const timer = setTimeout(resolve, timeout);
    child.once('exit', () => { clearTimeout(timer); resolve(); });
  });
}
function listenerClosed(port) {
  return new Promise(resolve => {
    const socket = net.connect({ host: '127.0.0.1', port });
    let done = false;
    const timer = setTimeout(() => finish(false), 2000);
    function finish(closed) {
      if (done) return;
      done = true; clearTimeout(timer); socket.destroy(); resolve(closed);
    }
    socket.once('connect', () => finish(false));
    socket.once('error', () => finish(true));
  });
}
async function cleanup() {
  for (const req of pendingHttp) req.destroy();
  for (const socket of pendingWs) socket.destroy();
  if (heldResponse) { heldResponse.destroy(); heldResponse = null; }
  if (harness) {
    harness.stdin.end();
    await waitExit(harness, 8000);
    report.harness_clean_exit = harness.exitCode === 0;
    if (!report.harness_clean_exit) {
      report.passed = false;
      report.failure = report.failure || { kind: 'harness_cleanup_failed', check: null };
      if (harness.exitCode === null && harness.signalCode === null) harness.kill();
    } else if (report.passed) {
      const removed = !fs.existsSync(path.join(root, 'vault', 'routing', 'descriptor.dpapi'));
      const closed = relayPort && await listenerClosed(relayPort);
      if (removed && closed) report.checks.push('stdin_EOF_removes_descriptor_closes_listener_and_exits_zero');
      else {
        report.passed = false;
        report.failure = { kind: 'harness_cleanup_incomplete', check: null };
      }
    }
  }
  if (seed && seed.exitCode === null && seed.signalCode === null) {
    seed.stdin.end(); await waitExit(seed, 3000);
    if (seed.exitCode === null && seed.signalCode === null) seed.kill();
  }
  if (mock) { mock.closeAllConnections(); await new Promise(resolve => mock.close(resolve)); }
  fs.writeFileSync(path.join(root, 'routing-protocol.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
  process.exitCode = report.passed ? 0 : 1;
}
start().catch(error => {
  report.failure = { kind: error.kind || 'integration_failed', check: error.check || null };
}).finally(cleanup);
