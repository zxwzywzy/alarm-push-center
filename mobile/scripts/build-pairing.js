import {build} from 'esbuild';
await build({entryPoints:['admin-pairing.js'],bundle:true,minify:true,format:'esm',outfile:'../AlarmPushCenter.OpenWrt/wwwroot/mobile-pairing.js'});
