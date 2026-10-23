import fs from 'node:fs';
const file='ios/App/CapApp-SPM/Package.swift';
fs.writeFileSync(file,fs.readFileSync(file,'utf8').replace(/path: "([^"\n]+)"/g,(_,p)=>`path: "${p.replace(/\\/g,'/')}"`));
