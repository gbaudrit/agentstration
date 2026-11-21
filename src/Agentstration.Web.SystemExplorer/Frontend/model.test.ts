import {test} from 'node:test';
import assert from 'node:assert/strict';
import {buildModel,rendererId,GraphPage} from './model';
import {layoutLikeC4Model,GraphvizWasmAdapter} from '@likec4/layouts';
import {resourceIcon} from './resource-icons';
const node=(id:string,name='same-name')=>({id,identity:{kind:'Agent',name},state:'draft'});
test('resource focus uses local product icons and native rectangles without changing identities or arrow direction',async()=>{
 const input:GraphPage={nodes:[node('agent'),{...node('data'),identity:{kind:'DataSource',name:'Data'}}],relations:[{source:'agent',target:'data',kind:'exact-dependency'}]};
 const before=JSON.stringify(input);const result=buildModel(input,{'kind:DataSource':'Source de données'}, {},{mode:'resource',root:'data'});
 const view=result.model.view('topology').$view;
 assert.equal(view.autoLayout.direction,'LR');
 assert.deepEqual(view.nodes.find(n=>n.id===rendererId('data'))?.description,{txt:'Source de données'});
 for(const item of view.nodes){assert.equal(item.shape,'rectangle');assert.match(item.icon??'',/^data:image\/svg\+xml,/);}
 assert.notEqual(view.nodes.find(n=>n.id===rendererId('data'))?.color,view.nodes.find(n=>n.id===rendererId('agent'))?.color);
 assert.notEqual(resourceIcon('Agent'),resourceIcon('Tool'));assert.notEqual(resourceIcon('KnowledgeSource'),resourceIcon('DataSource'));
 assert.equal(resourceIcon('__proto__'),resourceIcon('unknown-kind'));
 assert.equal(JSON.stringify(input),before);
 const graphviz=new GraphvizWasmAdapter();
 try{const layout=await layoutLikeC4Model(result.model,{graphviz});assert.equal(layout.view('topology').$view.edges.length,1);}finally{graphviz.dispose();}
});
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
test('volatile localized overlay notes do not alter graph input or canonical mapping',()=>{
 const input:GraphPage={nodes:[node('resource:a')],relations:[]}; const before=JSON.stringify(input);
 const plain=buildModel(input); const decorated=buildModel(input,{}, {'resource:a':'Observed: 1; <img src=x>', 'hidden':'private'});
 assert.equal([...decorated.model.elements()].length,1);
 assert.deepEqual([...plain.ids],[...decorated.ids]); assert.equal(JSON.stringify(input),before);
});
test('summary links retain a safe callback identity independent of hostile labels and native relationship IDs',async()=>{
 const input:GraphPage={nodes:[node('agent'),node('tool')],relations:[{source:'agent',target:'tool',kind:'summary:abc'}]};
 const {model,links}=buildModel(input,{'summary:abc':'via Nantes access · 2.0.0 <img src=x>'});
 const relation=[...model.relationships()][0]!;
 assert.equal(links.get(relation.id),'summary:abc');assert.equal(relation.title,'via Nantes access · 2.0.0 <img src=x>');
 const graphviz=new GraphvizWasmAdapter();
 try {const layout=await layoutLikeC4Model(model,{graphviz});const edge=layout.view('topology').$view.edges[0]!;assert.ok(edge.relations.some(id=>links.get(id)==='summary:abc'));} finally {graphviz.dispose();}
 assert.equal(buildModel({nodes:input.nodes,relations:[{source:'agent',target:'tool',kind:'exact-dependency'}]}).links.size,0);
});
