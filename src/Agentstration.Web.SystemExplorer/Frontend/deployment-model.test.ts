import {test} from 'node:test';
import assert from 'node:assert/strict';
import {layoutLikeC4Model,GraphvizWasmAdapter} from '@likec4/layouts';
import {buildDeploymentModel} from './deployment-model';
import {GraphPage} from './model';
const d={deploymentUid:'dep-1',configurationId:'cfg-1',agentUid:'agent',revisionUid:'revision',revisionName:'Agent v2',agentVersion:2,definitionHash:'hash',environment:'prod"; injected attacker',targetId:'pool.α',targetName:'Target',targetKind:'worker-pool',runtime:'awp-worker',recordedAt:'2026-11-17T00:00:00Z'};
const page:GraphPage={nodes:[{id:'deployment:dep-1:cfg-1',identity:{kind:'AgentDeployment',name:'deploy'},state:'recorded-deployment-configuration',deployment:d}],relations:[]};
const labels={'runtime-awp-worker':'AWP Worker','deployment-environment':'Declared environment','deployment-desired':'Desired',worker:'Worker'};
test('desired topology is a native deployment view with environment, target and container instance, retaining Agent as a component',async()=>{
 const before=JSON.stringify(page);const result=buildDeploymentModel(page,labels,{mode:'deployment',root:page.nodes[0]!.id});
 const view=result.model.view('topology').$view;
 assert.equal(view._type,'deployment');assert.equal(view.nodes.length,3);
 assert.equal([...result.model.elements()].find(e=>e.id==='runtime')?.kind,'container');
 assert.equal([...result.model.elements()].find(e=>e.id==='runtime.agent')?.kind,'component');
 assert.equal(view.nodes.filter(n=>n.kind==='instance').length,1);
 assert.ok(view.nodes.some(n=>n.parent!==null));
 assert.equal(new Set([...result.ids.values()]).size,1);assert.equal(JSON.stringify(page),before);
 const graphviz=new GraphvizWasmAdapter();
 try {const layout=await layoutLikeC4Model(result.model,{graphviz});assert.equal(layout.view('topology').$view._type,'deployment');assert.ok(layout.view('topology').$view.bounds.width>0);} finally {graphviz.dispose();}
});
test('observed topology has distinct attested Worker sessions and never turns the declared target into an observed host',()=>{
 const placements=['s1','s2','s1'].map(sessionId=>({workerId:'worker-id',sessionId,runId:'run',assignmentId:'assignment',attemptId:'attempt',lastHeartbeatAt:'2026-11-17T00:00:00Z',leaseExpiresAt:'2026-11-17T00:00:30Z',presence:'unknown'}));
 const result=buildDeploymentModel(page,labels,{mode:'observed',root:page.nodes[0]!.id,placements});const view=result.model.view('topology').$view;
 assert.equal(view._type,'deployment');assert.equal(view.nodes.filter(n=>n.kind==='instance').length,2);
 assert.equal(view.nodes.filter(n=>n.kind==='workerSession').length,2);
 assert.equal(view.nodes.some(n=>n.title==='Target'),false);
 assert.throws(()=>buildDeploymentModel(page,labels,{mode:'observed',root:page.nodes[0]!.id,placements:[]}),/placement_unavailable/);
 assert.throws(()=>buildDeploymentModel(page,labels,{mode:'deployment',root:'hidden'}),/deployment_unavailable/);
});
