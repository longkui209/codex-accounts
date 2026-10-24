'use strict';

// Source-only exporter. No networking, subprocesses, Git operations or credentials.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {TextDecoder} = require('node:util');
const MANIFEST = 'release/source-files.json';
const STATUS = 'release/review-status.json';
const MAX_FILE = 4 * 1024 * 1024;

class ReleaseError extends Error {
  constructor(code, findings = []) { super(code); this.code = code; this.findings = findings; }
}
function refuse(code) { throw new ReleaseError(code); }
function relative(value) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_.\/-]+$/.test(value) ||
      value.startsWith('/') || value.split('/').some(p => !p || p === '.' || p === '..')) refuse('unsafe-relative-path');
  return value;
}
function permitted(value) {
  relative(value);
  const parts = value.toLowerCase().split('/');
  if (parts.some(p => ['.git','bin','obj','out','runtime','native','validation-output','.inspection','.build-home'].includes(p))) return false;
  const leaf = parts.at(-1);
  if (/^(?:auth|accounts)\.json$|^config\.toml(?:\.|$)|^\.env(?:\.|$)/.test(leaf)) return false;
  return ['license','.gitignore','.gitattributes','nuget.config'].includes(leaf) || /\.(?:cs|csproj|xaml|ps1|cjs|json|md|html|txt|yml|yaml)$/.test(leaf);
}
function plainFile(root, name) {
  const base = path.resolve(root);
  if (fs.lstatSync(base).isSymbolicLink()) refuse('linked-checkout');
  let current = base;
  const components = relative(name).split('/');
  for (let i = 0; i < components.length; i++) {
    current = path.join(current, components[i]);
    const info = fs.lstatSync(current);
    if (info.isSymbolicLink()) refuse('linked-source');
    if (i < components.length - 1 && !info.isDirectory()) refuse('invalid-source-directory');
    if (i === components.length - 1 && (!info.isFile() || info.nlink !== 1 || info.size > MAX_FILE)) refuse('nonplain-source-file');
  }
  const actual = fs.realpathSync(current);
  const resolvedBase = fs.realpathSync(base) + path.sep;
  if (!actual.startsWith(resolvedBase)) refuse('source-outside-checkout');
  return current;
}
function decode(bytes) {
  if (bytes.includes(0)) refuse('binary-source');
  try { return new TextDecoder('utf-8', {fatal:true}).decode(bytes); }
  catch { refuse('non-utf8-source'); }
}
function scanText(name, text) {
  const found = [];
  const rules = [
    ['access-key', /\b(?:gh[pousr]_[A-Za-z0-9]{20,255}|github_pat_[A-Za-z0-9_]{20,255}|sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,255})\b/g],
    ['jwt', /\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g],
    ['private-key', /-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----/g],
    ['personal-user-path', /\b[A-Z]:[\\/]+Users[\\/]+(?:Administrator|ADMINI[^\\/\s"']*)/gi],
    ['development-path', /\b[A-Z]:[\\/]+(?:codexgongzuoqu|kaifagongju)[\\/]/gi]
  ];
  const rows = text.split(/\r?\n/);
  rows.forEach((line, index) => {
    for (const [rule, regex] of rules) {
      regex.lastIndex = 0;
      if (regex.test(line)) found.push({path:name, rule, line:index + 1});
    }
    const emails = /(?<![A-Za-z0-9_.+:/-])[A-Za-z0-9_.+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})/g;
    for (const match of line.matchAll(emails)) {
      const host = match[1].toLowerCase();
      if (!/\.(?:test|invalid)$/.test(host) && !['example.com','example.org','example.net'].includes(host))
        found.push({path:name, rule:'nonfixture-email', line:index + 1});
    }
    const literals = /\b(?:access_token|refresh_token|id_token|client_secret|password)\s*["']?\s*[:=]\s*["']([^"'\r\n]{8,})["']/gi;
    for (const match of line.matchAll(literals)) {
      if (!/^(?:synthetic(?:[-_]|$)|fixture[-_]|test[-_]|demo[-_]|<[^>]+>$|\*{3,}$)/i.test(match[1]))
        found.push({path:name, rule:'credential-literal', line:index + 1});
    }
  });
  return found;
}
function readJson(root, name) {
  try { return JSON.parse(decode(fs.readFileSync(plainFile(root, name)))); }
  catch (error) { if (error instanceof ReleaseError) throw error; refuse('invalid-json-input'); }
}
function statusFor(root) {
  const value = readJson(root, STATUS);
  if (value.schemaVersion !== 1 || typeof value.publicationAllowed !== 'boolean' ||
      typeof value.programApplicationAllowed !== 'boolean' || typeof value.sourcePreviewPublicationAllowed !== 'boolean' || !Array.isArray(value.findings) ||
      value.findings.some(f => !/^C-\d{2}$/.test(f.id) || !['unresolved','pending-evidence','resolved','reviewed-with-limits'].includes(f.status)))
    refuse('invalid-review-status');
  return value;
}
function assertPurpose(status, purpose) {
  if (!['review','source-preview','publication'].includes(purpose)) refuse('invalid-export-purpose');
  if (purpose === 'source-preview' && !status.sourcePreviewPublicationAllowed) refuse('source-preview-publication-not-authorized');
  if (purpose === 'publication' && (!status.publicationAllowed || status.findings.some(f => f.status === 'unresolved')))
    refuse('publication-on-hold');
}
function inspect(root) {
  const manifest = readJson(root, MANIFEST);
  if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.files) || manifest.files.length < 1 || manifest.files.length > 512)
    refuse('invalid-source-manifest');
  const destinations = new Set(), entries = [], findings = [];
  for (const item of manifest.files) {
    const source = relative(typeof item === 'string' ? item : item.source);
    const destination = relative(typeof item === 'string' ? item : item.destination);
    if (!permitted(source) || !permitted(destination)) refuse('forbidden-source-path');
    const key = destination.toLowerCase();
    if (destinations.has(key)) refuse('duplicate-export-destination');
    destinations.add(key);
    const bytes = fs.readFileSync(plainFile(root, source));
    const text = decode(bytes);
    if (destination.endsWith('.json')) { try {JSON.parse(text);} catch {refuse('invalid-export-json');} }
    findings.push(...scanText(destination, text));
    entries.push({source,destination,bytes,text,sha256:crypto.createHash('sha256').update(bytes).digest('hex')});
  }
  const required = ['readme.md','license','security.md','contributing.md','privacy.md',MANIFEST,STATUS,'agents.md'];
  if (required.some(p => !destinations.has(p))) refuse('required-public-file-missing');
  for (const entry of entries.filter(e => e.destination.endsWith('.md'))) {
    for (const match of entry.text.matchAll(/!?\[[^\]]*\]\(([^\s)]+)\)/g)) {
      const target = match[1];
      if (/^(?:https?:|mailto:|#)/i.test(target)) continue;
      const link = path.posix.normalize(path.posix.join(path.posix.dirname(entry.destination),target.split('#')[0]));
      if (!destinations.has(link.toLowerCase())) findings.push({path:entry.destination,rule:'missing-local-link',line:entry.text.slice(0,match.index).split('\n').length});
    }
  }
  if (findings.length) throw new ReleaseError('source-content-check-failed', findings);
  return {entries,status:statusFor(root)};
}
function safeDirectory(parent, leaf) {
  const destination = path.join(parent, leaf);
  if (!fs.existsSync(destination)) fs.mkdirSync(destination);
  const stat = fs.lstatSync(destination);
  if (stat.isSymbolicLink() || !stat.isDirectory()) refuse('unsafe-export-directory');
  return destination;
}
function exportSource(root, purpose = 'review') {
  const checked = inspect(root);
  assertPurpose(checked.status, purpose);
  if (purpose === 'source-preview') {
    const introduction = checked.entries.find(e => e.destination === 'README.md')?.text || '';
    const review = checked.entries.find(e => e.destination === 'docs/COMPLIANCE_REVIEW.md')?.text || '';
    if (!/experimental/i.test(introduction) || !introduction.includes('docs/COMPLIANCE_REVIEW.md') ||
        checked.status.findings.filter(f=>f.status==='unresolved').some(f=>!review.includes(f.id)))
      refuse('experimental-disclosure-incomplete');
  }
  const project = checked.entries.find(e => e.destination === 'CodexAccountSwitcher.csproj');
  const version = project?.text.match(/<Version>([0-9]+\.[0-9]+\.[0-9]+)<\/Version>/)?.[1];
  if (!version) refuse('missing-application-version');
  const output = safeDirectory(safeDirectory(root, 'dist'), 'public-source');
  const label = `codex-accounts-${version}-${purpose}-${new Date().toISOString().replace(/[-:.]/g,'')}-${crypto.randomBytes(4).toString('hex')}`;
  const bundle = path.join(output,label);
  fs.mkdirSync(bundle); // New export only; never overwrite or delete a prior artifact.
  const sourceRoot = safeDirectory(bundle, 'source');
  for (const entry of checked.entries) {
    let directory = sourceRoot;
    const segments = entry.destination.split('/');
    for (const segment of segments.slice(0,-1)) directory = safeDirectory(directory, segment);
    const target = path.join(directory, segments.at(-1));
    fs.writeFileSync(target,entry.bytes,{flag:'wx'});
    if (crypto.createHash('sha256').update(fs.readFileSync(target)).digest('hex') !== entry.sha256) refuse('export-hash-mismatch');
  }
  const inventory = checked.entries.map(e => ({path:e.destination,bytes:e.bytes.length,sha256:e.sha256}));
  const report = {passed:true,kind:'source-only',purpose,version,files:inventory.length,
    sensitiveFindings:0,vendorBinariesIncluded:false,credentialsIncluded:false,
    publicationAllowed:checked.status.publicationAllowed,sourcePreviewPublicationAllowed:checked.status.sourcePreviewPublicationAllowed,programApplicationAllowed:checked.status.programApplicationAllowed,
    unresolvedFindings:checked.status.findings.filter(f=>f.status==='unresolved').map(f=>f.id)};
  fs.writeFileSync(path.join(bundle,'inventory.json'),JSON.stringify(inventory,null,2)+'\n',{flag:'wx'});
  fs.writeFileSync(path.join(bundle,'SHA256SUMS.txt'),inventory.map(f=>`${f.sha256}  ${f.path}`).join('\n')+'\n',{flag:'wx'});
  fs.writeFileSync(path.join(bundle,'export-report.json'),JSON.stringify(report,null,2)+'\n',{flag:'wx'});
  inspect(sourceRoot); // The exported manifest must work without private source replacements.
  return {...report,bundle:path.relative(root,bundle).split(path.sep).join('/')};
}
async function main() {
  const root = path.resolve(__dirname,'..'), [command,...args] = process.argv.slice(2);
  if (command === 'test' && args.length === 0) return require('./public-release.test.cjs').run();
  if (command === 'check' && args.length === 0) {
    const {entries,status} = inspect(root);
    return {passed:true,files:entries.length,bytes:entries.reduce((n,e)=>n+e.bytes.length,0),sensitiveFindings:0,
      publicationAllowed:status.publicationAllowed,sourcePreviewPublicationAllowed:status.sourcePreviewPublicationAllowed,programApplicationAllowed:status.programApplicationAllowed};
  }
  if (command === 'export' && (args.length === 0 || args.length === 2 && args[0] === '--purpose'))
    return exportSource(root,args[1] || 'review');
  refuse('usage-check-test-or-export-purpose-review-source-preview-publication');
}
module.exports = {ReleaseError,relative,permitted,plainFile,decode,scanText,assertPurpose,inspect,exportSource};
if (require.main === module) main().then(result=>process.stdout.write(JSON.stringify(result)+'\n')).catch(error=>{
  const report = error instanceof ReleaseError ? {passed:false,code:error.code,findings:error.findings} : {passed:false,code:'release-check-failed'};
  process.stderr.write(JSON.stringify(report)+'\n'); process.exitCode=1;
});
