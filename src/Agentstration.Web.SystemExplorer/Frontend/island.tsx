import React from 'react';
import {createRoot,Root} from 'react-dom/client';
import {LikeC4ModelProvider,ReactLikeC4} from '@likec4/diagram';
import {layoutLikeC4Model,GraphvizWasmAdapter} from '@likec4/layouts';
import {buildModel,GraphPage} from './model';
import {DiagramRenderQueue,DiagramRenderInput} from './render-queue';
import {buildDeploymentModel,DeploymentOptions} from './deployment-model';
type Callback={invokeMethodAsync(method:string,id:string):Promise<void>};
type Mount={root:Root; graphviz:GraphvizWasmAdapter; queue:DiagramRenderQueue};
const mounts=new WeakMap<HTMLElement,Mount>();
async function apply(element:HTMLElement,mount:Mount,input:DiagramRenderInput,current:()=>boolean):Promise<boolean>{
 const built=input.options?.mode==='deployment'||input.options?.mode==='observed'
  ?buildDeploymentModel(input.page,input.labels,input.options)
  :buildModel(input.page,input.labels,input.notes);
 const {model,ids}=built;
 const links='links' in built&&built.links instanceof Map?built.links:new Map<string,string>();
 const layouted=await layoutLikeC4Model(model,{graphviz:mount.graphviz,concurrency:1,timeout:20000,throwOnTimeout:true});
 if(!current()||mounts.get(element)!==mount)return true;
 mount.root.render(<LikeC4ModelProvider likec4model={layouted}><ReactLikeC4 viewId="topology" injectFontCss={false} controls={false} enableSearch={false} enableFocusMode={false} enableElementDetails={false} enableRelationshipDetails={false} enableNotations={false} showNavigationButtons={false} nodesSelectable onEdgeClick={(edge)=>{if(!current()||mounts.get(element)!==mount)return;for(const relation of edge.relations){const id=links.get(relation);if(id){input.selectLink?.(id);break;}}}} onNodeClick={(node)=>{const id=ids.get(node.id);if(id&&current()&&mounts.get(element)===mount)input.select(id);}} style={{height:'100%',width:'100%'}} /></LikeC4ModelProvider>);
 return true;
}
export function render(element:HTMLElement,page:GraphPage,labels:Record<string,string>,callback:Callback,overlayNotes:Record<string,string>|null=null,options?:DeploymentOptions):Promise<boolean> {
 if(page.nodes.length===0){dispose(element);return Promise.resolve(true);}
 let mount=mounts.get(element);
 if(!mount){
  const created:Mount={root:createRoot(element),graphviz:new GraphvizWasmAdapter(),queue:new DiagramRenderQueue((input,current)=>apply(element,created,input,current))};
  mounts.set(element,created);mount=created;
 }
 return mount.queue.enqueue({page,labels,notes:overlayNotes??{},options,select:id=>{void callback.invokeMethodAsync('SelectNode',id);},selectLink:id=>{void callback.invokeMethodAsync('SelectLink',id);}});
}
export function dispose(element:HTMLElement) {
 const previous=mounts.get(element);
 if(previous){mounts.delete(element);previous.queue.dispose();previous.root.unmount();previous.graphviz.dispose();element.replaceChildren();}
}
