import { Builder } from '@likec4/core/builder';
import {resourceIcon} from './resource-icons';
export interface GraphNode { id:string; identity:{kind:string; name:string;resourceUid?:string}; displayName?:string|null; state:string;
 evidence?:{generation?:number|null;publication?:string|null}|null;
 deployment?:{deploymentUid:string;configurationId:string;agentUid:string;revisionUid:string;revisionName:string;agentVersion:number;definitionHash:string;environment:string;targetId:string;targetName:string;targetKind:string;runtime:string;recordedAt:string}|null }
export interface GraphRelation { source:string; target:string; kind:string }
export interface GraphPage {nodes:GraphNode[]; relations:GraphRelation[]}
// User names never enter LikeC4 expressions; the identity mapping is collision-free.
export function rendererId(id:string):string { return 'n'+Array.from(new TextEncoder().encode(id),n=>n.toString(16).padStart(2,'0')).join(''); }
export function buildModel(page:GraphPage, labels:Record<string,string> = {}, overlayNotes:Record<string,string> = {}, options?:{mode:string;root?:string|null}) {
  if(page.nodes.length>100) throw new Error('explorer_page_limit');
  const ids=new Map<string,string>();
  let builder:Builder.Any=Builder.specification({elements:{resource:{}}}) as unknown as Builder.Any;
  for(const node of [...page.nodes].sort((a,b)=>a.id < b.id ? -1 : a.id > b.id ? 1 : 0)) {
    if(ids.has(rendererId(node.id))) throw new Error('explorer_duplicate_identity');
    ids.set(rendererId(node.id),node.id);
    builder=builder.model(({resource},_)=>_((resource as (id:string, props:{title:string;description:string})=>ReturnType<typeof resource>)(rendererId(node.id),{title:(node.displayName??node.identity.name).replace(/[\x00-\x1f]/g,' ').slice(0,200), ...(options?.mode==='resource'?{icon:resourceIcon(node.identity.kind),shape:'rectangle',color:node.id===options.root?'sky':'blue',style:{iconPosition:'top',iconSize:'sm'}}:{}), description:node.identity.kind+(Object.hasOwn(overlayNotes,node.id) && typeof overlayNotes[node.id]==='string' ? '\n'+overlayNotes[node.id].replace(/[\x00-\x1f]/g,' ').slice(0,300) : '')}))) as unknown as Builder.Any;
  }
  for(const edge of page.relations) {
    const source=rendererId(edge.source),target=rendererId(edge.target);
    if(ids.has(source)&&ids.has(target)&&source!==target)
      builder=builder.model(({rel},_)=>_(rel(source,target,{title:(labels[edge.kind]??labels.relationship??edge.kind).replace(/[\x00-\x1f]/g,' ').slice(0,300),metadata:{explorerLink:edge.kind.startsWith('summary:')?edge.kind:''}}))) as unknown as Builder.Any;
  }
  builder=builder.views(({view,$include,$autoLayout},_)=>_(view('topology').with($include('*'),...(options?.mode==='resource'?[$autoLayout('LR')]:[])))) as unknown as Builder.Any;
  const model=builder.toLikeC4Model();
  const links=new Map<string,string>();
  for(const relation of model.relationships()){const id=relation.metadata.explorerLink;if(typeof id==='string'&&id.startsWith('summary:'))links.set(relation.id,id);}
  return { model, ids, links };
}
