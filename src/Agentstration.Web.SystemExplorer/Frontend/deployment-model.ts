import {Builder,type AnyTypes,type DeploymentViewBuilder} from '@likec4/core/builder';
import {rendererId,GraphPage} from './model';
export interface ObservedPlacement {workerId:string;sessionId:string;runId:string;assignmentId:string;attemptId:string;lastHeartbeatAt:string;leaseExpiresAt:string;presence:string}
export interface DeploymentOptions {mode:string;root?:string|null;placements?:ObservedPlacement[]}
const clean=(value:string)=>value.replace(/[\x00-\x1f]/g,' ').slice(0,300);
// Deployment nodes and container instances are native LikeC4 deployment entities.
// The Agent revision remains a logical component of its runtime container.
export function buildDeploymentModel(page:GraphPage,labels:Record<string,string>,options:DeploymentOptions) {
 if(page.nodes.length>100)throw new Error('explorer_node_limit');
 const selected=page.nodes.find(node=>node.id===options.root && node.deployment);
 if(!selected?.deployment)throw new Error('explorer_deployment_unavailable');
 const d=selected.deployment;
 const placements=options.mode==='observed'?(options.placements??[]):[];
 if(placements.length>100)throw new Error('explorer_placement_limit');
 const instances=options.mode==='observed'
   ? [...new Map(placements.map(p=>[p.workerId+'/'+p.sessionId,p])).values()]
   : [null];
 if(instances.length>32)throw new Error('explorer_placement_limit');
 if(instances.length===0)throw new Error('explorer_placement_unavailable');
 const ids=new Map<string,string>();
 const env=rendererId(d.deploymentUid+'/'+d.environment),target=rendererId(d.targetId);
 const runtime='runtime';
 const runtimeLabel=options.mode==='observed'?(labels['runtime-awp-worker']??'AWP'):(labels['runtime-'+d.runtime]??d.runtime);
 let builder:Builder.Any=Builder.specification({elements:{container:{},component:{}},deployments:{environment:{},target:{},workerSession:{}}}) as unknown as Builder.Any;
 builder=builder.model(({container,component},_)=>_(container(runtime,{title:clean(runtimeLabel),description:clean(d.revisionName)}).with(
   component('agent',{title:clean(d.revisionName),description:clean(d.definitionHash)})
 ))) as unknown as Builder.Any;
 builder=builder.deployment(({environment},_)=>_((environment as (id:string,props:{title:string;description?:string})=>ReturnType<typeof environment>)(env,{title:clean(d.environment),description:labels['deployment-environment']}))) as unknown as Builder.Any;
 const leaves:string[]=[];
 // IDs and kind expressions are generated; authored labels never become builder expressions.
 for(const placement of instances) {
  const host=placement?rendererId(placement.workerId+'/'+placement.sessionId):target;
  const instance='instance';
  const leaf=env+'.'+host+'.'+instance;
  leaves.push(leaf);ids.set(leaf,selected.id);ids.set(env,selected.id);ids.set(env+'.'+host,selected.id);
  const title=placement?(labels.worker??'')+' '+placement.workerId.slice(0,8):d.targetName;
  const description=placement?placement.sessionId+' · '+placement.lastHeartbeatAt:labels['deployment-desired']??'';
  builder=builder.deployment(({target,workerSession,instanceOf},_)=>_(
   ((placement?workerSession:target) as (id:string,props:{title:string;description:string;technology:string})=>ReturnType<typeof target>)(env+'.'+host,{title:clean(title),description:clean(description),technology:placement?'AWP':d.targetKind})
    .with(instanceOf(instance,runtime,{title:clean(placement?(labels['runtime-awp-worker']??'AWP Worker'):runtimeLabel),description:clean(d.revisionName+' · '+d.agentVersion)}))
  )) as unknown as Builder.Any;
 }
 builder=builder.views(({deploymentView,$include,$includeAncestors},_)=>_(deploymentView('topology').with(
   ...leaves.map(leaf=>$include<DeploymentViewBuilder<AnyTypes>>(leaf)),$includeAncestors(true)
 ))) as unknown as Builder.Any;
 return {model:builder.toLikeC4Model(),ids};
}
