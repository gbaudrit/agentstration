import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DiagramRenderQueue,DiagramRenderInput} from './render-queue';
const input=(id:string):DiagramRenderInput=>({page:{nodes:[{id,identity:{kind:'Agent',name:id},state:'draft'}],relations:[]},labels:{},notes:{},select:()=>{}});
function deferred(){let resolve!:(success:boolean)=>void;const promise=new Promise<boolean>(done=>{resolve=done;});return{promise,resolve};}
test('incremental layouts keep the active graph and coalesce intermediate pending batches',async()=>{
 const first=deferred();const applied:string[]=[];
 const queue=new DiagramRenderQueue(async value=>{applied.push(value.page.nodes[0].id);return applied.length===1?first.promise:true;});
 const a=queue.enqueue(input('first'));const b=queue.enqueue(input('intermediate'));const c=queue.enqueue(input('latest'));
 assert.deepEqual(applied,['first']);
 assert.equal(await b,true);
 first.resolve(true);
 assert.deepEqual(await Promise.all([a,c]),[true,true]);
 assert.deepEqual(applied,['first','latest']);
 queue.dispose();
});
test('navigation disposes queued work and prevents an old layout from committing',async()=>{
 const pending=deferred();let commits=0;
 const queue=new DiagramRenderQueue(async(_,current)=>{await pending.promise;if(current())commits++;return true;});
 const active=queue.enqueue(input('old'));const queued=queue.enqueue(input('queued'));
 queue.dispose();pending.resolve(true);
 assert.deepEqual(await Promise.all([active,queued]),[true,true]);
 assert.equal(commits,0);
 assert.equal(await queue.enqueue(input('disposed')),true);
});
