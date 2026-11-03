const fs=require('fs'),vm=require('vm'),assert=require('assert');
const script=fs.readFileSync(require('path').join(__dirname,'../web/admin.html'),'utf8').match(/<script>([\s\S]*?)<\/script>/)[1];
for(const owner of [true,false])new vm.Script(owner?script.replaceAll('/*OWNER*/','').replaceAll('/*END*/',''):script.replace(/\/\*OWNER\*\/[\s\S]*?\/\*END\*\//g,''));
const nodes=new Map(),node=()=>({innerHTML:'',textContent:'',value:'',isConnected:true,style:{},classList:{toggle(){},add(){},remove(){}},querySelectorAll(){return []}});
const c={document:{querySelector(s){if(!nodes.has(s))nodes.set(s,node());return nodes.get(s)}},window:{},URLSearchParams,setTimeout,clearTimeout};
vm.createContext(c);vm.runInContext(script.replace(/load\(\)\.catch\([\s\S]*$/,''),c);
vm.runInContext(`globalThis.pending=[];api=()=>new Promise(resolve=>pending.push(resolve));S.me={today:'2026-10-25'};S.reg={};S.draft={};S.weekDate='2026-10-26';globalThis.first=VIEWS.week(document.querySelector('#main'));renderId++;S.weekDate='2026-11-02';globalThis.second=VIEWS.week(document.querySelector('#main'));`,c);
const week=date=>({days:[{date,slots:[]}],inTerm:true,week:1,parity:'odd'});
(async()=>{c.pending[1](week('2026-11-02'));await c.second;c.pending[0](week('2026-10-26'));await c.first;assert(nodes.get('#wk').innerHTML.includes('02.11'));console.log('PASS UI: owner/head scripts parse; stale week response ignored');
for(const owner of [true,false]){
  vm.runInContext(`S.me={owner:${owner}};S.chat={chat:2,topic:0};api=async path=>path==='chats'?{chats:[]}:{rows:[
    {id:1,direction:'out',chat:2,message:1,time:1,text:'[фото]',media:'image'},
    {id:2,direction:'out',chat:2,message:2,time:1,text:'[GIF]',media:'video'},
    {id:3,direction:'in',chat:2,message:3,time:1,text:'расшифровка <script>',media:null}
  ]};globalThis.journal=VIEWS.log(document.querySelector('#main'));`,c);
  await c.journal;await Promise.resolve();
  const html=nodes.get('#ml').innerHTML;
  assert(html.includes('<img src="/api/log/media?id=1"')&&html.includes('<video src="/api/log/media?id=2"'));
  assert(html.includes('расшифровка &lt;script&gt;')&&!html.includes('/api/log/media?id=3'));
}
console.log('PASS UI: owner/head journal renders bot media and escapes incoming transcripts');})().catch(e=>{console.error(e);process.exitCode=1});
