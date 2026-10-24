'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const release = require('./public-release.cjs');

function run() {
  const checks = [];
  const check = (name,action) => {action();checks.push(name);};
  const rejected = action => assert.throws(action,error=>error instanceof release.ReleaseError);
  for (const value of ['../escape','a/../b','/absolute','C:/user/file','a\\b','a//b','a/./b','name:stream','file@private'])
    check('reject-path-'+checks.length,()=>rejected(()=>release.relative(value)));
  for (const value of ['auth.json','Core/accounts.json','bin/app.cs','Plugin/runtime/app.exe','native/vendor.cs','config.toml','vault.dpapi','.env','a.pdb','a.dll','bundle.js','validation-output/data.json'])
    check('reject-artifact-'+checks.length,()=>assert.equal(release.permitted(value),false));
  check('public-identifiers-allowed',()=>assert.deepEqual(release.scanText('fixture.cs','app_publicClient123 '+ 'a'.repeat(64)),[]));
  check('explicit-synthetic-credentials-allowed',()=>assert.deepEqual(release.scanText('fixture.cjs',"refresh_token:'synthetic-refresh-fixture'"),[]));
  const fakeKey = 'gh'+'p_'+'Z'.repeat(36);
  const jwt = ['eyJ'+'A'.repeat(15),'eyJ'+'B'.repeat(15),'C'.repeat(18)].join('.');
  for (const [name,input] of [['key',fakeKey],['jwt',jwt],['pem','-----BEGIN '+'PRIVATE KEY-----'],['credential','refresh_'+"token:'unlabeled-fixture-value'"],['email','person@'+'private-domain.invalidname'],['checkout','D:'+String.fromCharCode(92)+'codex'+'gongzuoqu'+String.fromCharCode(92)+'source']])
    check('detect-'+name,()=>assert(release.scanText('fixture.txt',input).length>0));
  check('scan-report-never-echoes-matched-secret',()=>assert(!JSON.stringify(release.scanText('fixture.txt',fakeKey)).includes(fakeKey)));
  check('binary-rejected',()=>rejected(()=>release.decode(Buffer.from([65,0,66]))));
  check('invalid-utf8-rejected',()=>rejected(()=>release.decode(Buffer.from([0xff,0xfe]))));
  check('publication-hold-enforced',()=>rejected(()=>release.assertPurpose({publicationAllowed:false,findings:[]},'publication')));
  check('unresolved-findings-cannot-be-waived-by-flag',()=>rejected(()=>release.assertPurpose({publicationAllowed:true,findings:[{status:'unresolved'}]},'publication')));
  check('review-is-not-publication',()=>release.assertPurpose({publicationAllowed:false,findings:[{status:'unresolved'}]},'review'));
  check('unsupported-purpose-refused',()=>rejected(()=>release.assertPurpose({publicationAllowed:true,findings:[]},'other')));
  check('public-preview-needs-separate-authorization',()=>rejected(()=>release.assertPurpose({sourcePreviewPublicationAllowed:false,findings:[]},'source-preview')));
  check('public-preview-does-not-resolve-product-findings',()=>release.assertPurpose({sourcePreviewPublicationAllowed:true,publicationAllowed:false,findings:[{status:'unresolved'}]},'source-preview'));

  // Only fresh owned fixtures are written. No recursive deletion or user files.
  const base = path.resolve(__dirname,'..','validation-output');
  fs.mkdirSync(base,{recursive:true});
  const fixture = path.join(base,'release-check-'+crypto.randomUUID());
  fs.mkdirSync(fixture);
  fs.writeFileSync(path.join(fixture,'plain.txt'),'Synthetic public fixture.');
  fs.linkSync(path.join(fixture,'plain.txt'),path.join(fixture,'linked.txt'));
  check('hardlinks-refused',()=>rejected(()=>release.plainFile(fixture,'linked.txt')));
  fs.mkdirSync(path.join(fixture,'target'));
  fs.symlinkSync(path.join(fixture,'target'),path.join(fixture,'junction'),'junction');
  fs.writeFileSync(path.join(fixture,'target','synthetic.txt'),'Synthetic fixture.');
  check('linked-parent-refused',()=>rejected(()=>release.plainFile(fixture,'junction/synthetic.txt')));
  return {passed:true,syntheticOnly:true,checks};
}
module.exports = {run};
