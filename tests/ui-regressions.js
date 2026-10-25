const fs=require('fs'),vm=require('vm'),assert=require('assert');
const script=fs.readFileSync(require('path').join(__dirname,'../web/admin.html'),'utf8').match(/<script>([\s\S]*?)<\/script>/)[1];
for(const owner of [true,false])new vm.Script(owner?script.replaceAll('/*OWNER*/','').replaceAll('/*END*/',''):script.replace(/\/\*OWNER\*\/[\s\S]*?\/\*END\*\//g,''));
const nodes=new Map(),node=()=>({innerHTML:'',textContent:'',value:'',isConnected:true,classList:{toggle(){},add(){},remove(){}},querySelectorAll(){return []}});
const c={document:{querySelector(s){if(!nodes.has(s))nodes.set(s,node());return nodes.get(s)}},window:{},setTimeout,clearTimeout};
vm.createContext(c);vm.runInContext(script.replace(/load\(\)\.catch\([\s\S]*$/,''),c);
vm.runInContext(`globalThis.pending=[];api=()=>new Promise(resolve=>pending.push(resolve));S.me={today:'2026-10-25'};S.reg={};S.draft={};S.weekDate='2026-10-26';globalThis.first=VIEWS.week(document.querySelector('#main'));renderId++;S.weekDate='2026-11-02';globalThis.second=VIEWS.week(document.querySelector('#main'));`,c);
const week=date=>({days:[{date,slots:[]}],inTerm:true,week:1,parity:'odd'});
(async()=>{c.pending[1](week('2026-11-02'));await c.second;c.pending[0](week('2026-10-26'));await c.first;assert(nodes.get('#wk').innerHTML.includes('02.11'));console.log('PASS UI: owner/head scripts parse; stale week response ignored');})().catch(e=>{console.error(e);process.exitCode=1});
