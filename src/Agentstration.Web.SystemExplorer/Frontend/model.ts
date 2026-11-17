import { Builder } from '@likec4/core/builder';
export interface GraphNode { id:string; identity:{kind:string; name:string;resourceUid?:string}; displayName?:string|null; state:string;
 evidence?:{generation?:number|null;publication?:string|null}|null;
 deployment?:{deploymentUid:string;configurationId:string;agentUid:string;revisionUid:string;revisionName:string;agentVersion:number;definitionHash:string;environment:string;targetId:string;targetName:string;targetKind:string;runtime:string;recordedAt:string}|null }
export interface GraphRelation { source:string; target:string; kind:string }
export interface GraphPage {nodes:GraphNode[]; relations:GraphRelation[]}
// User names never enter LikeC4 expressions; the identity mapping is collision-free.
export function rendererId(id:string):string { return 'n'+Array.from(new TextEncoder().encode(id),n=>n.toString(16).padStart(2,'0')).join(''); }
export function buildModel(page:GraphPage, labels:Record<string,string> = {}, overlayNotes:Record<string,string> = {}) {
  if(page.nodes.length>100) throw new Error('explorer_page_limit');
  const ids=new Map<string,string>();
  let builder:Builder.Any=Builder.specification({elements:{resource:{}}}) as unknown as Builder.Any;
  for(const node of [...page.nodes].sort((a,b)=>a.id < b.id ? -1 : a.id > b.id ? 1 : 0)) {
    if(ids.has(rendererId(node.id))) throw new Error('explorer_duplicate_identity');
    ids.set(rendererId(node.id),node.id);
    builder=builder.model(({resource},_)=>_((resource as (id:string, props:{title:string;description:string})=>ReturnType<typeof resource>)(rendererId(node.id),{title:(node.displayName??node.identity.name).replace(/[\x00-\x1f]/g,' ').slice(0,200), description:node.identity.kind+(Object.hasOwn(overlayNotes,node.id) && typeof overlayNotes[node.id]==='string' ? '\n'+overlayNotes[node.id].replace(/[\x00-\x1f]/g,' ').slice(0,300) : '')}))) as unknown as Builder.Any;
  }
  for(const edge of page.relations) {
    const source=rendererId(edge.source),target=rendererId(edge.target);
    if(ids.has(source)&&ids.has(target)&&source!==target)
      builder=builder.model(({rel},_)=>_(rel(source,target,{title:labels[edge.kind]??labels.relationship??edge.kind}))) as unknown as Builder.Any;
  }
  builder=builder.views(({view,$include},_)=>_(view('topology').with($include('*')))) as unknown as Builder.Any;
  return { model:builder.toLikeC4Model(), ids };
}

// Presentation grouping only: exported semantic nodes and every original graph ID stay intact.
export function buildDefinitionModel(page:GraphPage,labels:Record<string,string> = {},notes:Record<string,string> = {}) {
 const groups=new Map<string,GraphNode[]>(),representatives=new Map<string,string>();
 for(const node of page.nodes) {
  const key=node.identity.resourceUid??node.id;
  const members=groups.get(key)??[];members.push(node);groups.set(key,members);
 }
 const nodes:GraphNode[]=[];
 for(const members of groups.values()) {
  const primary=members.find(node=>node.state==='recorded-generation')??members[0]!;
  const title=members.find(node=>node.displayName)?.displayName??primary.identity.name;
  const generations=[...new Set(members.filter(node=>node.state==='recorded-generation').map(node=>node.evidence?.generation).filter(value=>value!=null))];
  const current=members.find(node=>node.state==='definition')?.evidence?.generation;
  const publications=[...new Set(members.map(node=>node.evidence?.publication).filter(value=>value!=null))];
  const badges=[generations.length?(labels['recorded-generations']??'')+' '+generations.join(', '):'',
   current!=null&&generations.some(value=>value!==current)?(labels['current-generation']??'')+' '+current:'',
   publications.length?(labels.publication??'')+' '+publications.join(', '):''].filter(Boolean).join(' · ');
  nodes.push({...primary,displayName:title+(badges?' · '+badges:'')});
  for(const member of members)representatives.set(member.id,primary.id);
 }
 const relations=page.relations.map(edge=>({...edge,source:representatives.get(edge.source)!,target:representatives.get(edge.target)!}));
 return buildModel({nodes,relations},labels,notes);
}
