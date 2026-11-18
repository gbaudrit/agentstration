import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
const roots=execFileSync('npm',['ls','--omit=dev','--all','--parseable'],{encoding:'utf8'}).trim().split('\n').slice(1);
let text='Third-party notices for the optional System Explorer browser adapter.\nGenerated from installed production dependencies; see package-lock.json.\n\n';
for(const root of [...new Set(roots)].sort()){
 const pkg=JSON.parse(fs.readFileSync(path.join(root,'package.json'),'utf8'));
 text+=`${pkg.name}@${pkg.version} — ${JSON.stringify(pkg.license??'See upstream package')}\n`;
 for(const name of fs.readdirSync(root).filter(n=>/^(license|licence|copying|notice)(\..*)?$/i.test(n))){
  const file=path.join(root,name);if(fs.statSync(file).isFile())text+=fs.readFileSync(file,'utf8')+'\n';
 }
 text+='\n';
}
fs.writeFileSync('THIRD_PARTY_NOTICES.txt',text);
