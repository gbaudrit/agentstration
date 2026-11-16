import {test} from 'node:test';
import assert from 'node:assert/strict';
import {buildModel,rendererId,GraphPage} from './model';
import {layoutLikeC4Model,GraphvizWasmAdapter} from '@likec4/layouts';
const node=(id:string,name='same-name')=>({id,identity:{kind:'Agent',name},state:'draft'});
test('canonical identities remain collision-free for homonyms, unicode, punctuation and hostile renderer expressions',()=>{
 const ids=['resource:one','resource:two','a.b','a_b','é','e\u0301','__proto__','a\"; resource attacker'];
 assert.equal(new Set(ids.map(rendererId)).size,ids.length);
 for(const id of ids)assert.match(rendererId(id),/^n[0-9a-f]+$/);
 const input={nodes:ids.map(id=>node(id)),relations:[]};const {model,ids:mapping}=buildModel(input);
 assert.equal([...model.elements()].length,ids.length);
 for(const id of ids)assert.equal(mapping.get(rendererId(id)),id);
 assert.deepEqual([...buildModel({nodes:[...input.nodes].reverse(),relations:[]}).ids],[...mapping]);
});
test('layout consumes the builder model without mutating semantic input or including unknown endpoints',async()=>{
 const input:GraphPage={nodes:[node('resource:a','<img src=x onerror=alert(1)>'),node('resource:b')],relations:[{source:'resource:a',target:'resource:b',kind:'system-membership'},{source:'resource:a',target:'private',kind:'exact-dependency'}]};
 const before=JSON.stringify(input);const {model}=buildModel(input,{'system-membership':'Appartenance logique'});
 assert.equal([...model.relationships()].length,1);
 const graphviz=new GraphvizWasmAdapter();
 try{const layout=await layoutLikeC4Model(model,{graphviz});assert.equal(layout.view('topology').$view.nodes.length,2);assert.ok(layout.view('topology').$view.bounds.width>0);}finally{graphviz.dispose();}
 assert.equal(JSON.stringify(input),before);
});
test('bounded pages and duplicate IDs fail before layout',()=>{
 assert.throws(()=>buildModel({nodes:Array.from({length:101},(_,i)=>node(String(i))),relations:[]}),/explorer_page_limit/);
 assert.throws(()=>buildModel({nodes:[node('a'),node('a')],relations:[]}),/explorer_duplicate_identity/);
});
