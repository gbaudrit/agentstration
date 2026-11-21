import React,{useEffect,useState} from 'react';
import {createRoot,Root} from 'react-dom/client';
import {LikeC4ModelProvider,ReactLikeC4} from '@likec4/diagram';
import {layoutLikeC4Model,GraphvizWasmAdapter} from '@likec4/layouts';
import {buildModel,GraphPage,rendererId} from './model';
import {focusedResourceViewport,resourceViewportAnchor} from './resource-viewport';
import {DiagramRenderQueue,DiagramRenderInput} from './render-queue';
import {buildDeploymentModel,DeploymentOptions} from './deployment-model';
type Callback={invokeMethodAsync(method:string,id:string):Promise<void>};
type Mount={root:Root; graphviz:GraphvizWasmAdapter; queue:DiagramRenderQueue; revision:number; focusRoot?:string; focusAnchor?:number; viewport?:{zoomIn:()=>unknown;zoomOut:()=>unknown;fit:()=>unknown;focus:()=>unknown;initial:()=>unknown}};
function ThemedDiagram({element,children,revision,fit}:{element:HTMLElement;children:(scheme:'light'|'dark')=>React.ReactNode;revision:object;fit?:()=>void}){
 const shell=element.closest('.app-shell')??document.documentElement;
 const read=()=>shell.classList.contains('theme-dark')?'dark':'light';
 const [scheme,setScheme]=useState<'light'|'dark'>(read);
 useEffect(()=>{const observer=new MutationObserver(()=>setScheme(read()));observer.observe(shell,{attributes:true,attributeFilter:['class']});return()=>observer.disconnect();},[shell]);
 useEffect(()=>{
  if(!fit)return;
  let first=0,second=0,width=0,height=0;
  const schedule=()=>{cancelAnimationFrame(first);cancelAnimationFrame(second);first=requestAnimationFrame(()=>{second=requestAnimationFrame(fit);});};
  const observer=new ResizeObserver(()=>{
   const bounds=element.getBoundingClientRect();
   if(bounds.width>0&&bounds.height>0&&(bounds.width!==width||bounds.height!==height)){width=bounds.width;height=bounds.height;schedule();}
  });
  observer.observe(element);schedule();
  return()=>{observer.disconnect();cancelAnimationFrame(first);cancelAnimationFrame(second);};
 },[revision,element]);
 return children(scheme);
}
const mounts=new WeakMap<HTMLElement,Mount>();
async function apply(element:HTMLElement,mount:Mount,input:DiagramRenderInput,current:()=>boolean):Promise<boolean>{
 const built=input.options?.mode==='deployment'||input.options?.mode==='observed'
  ?buildDeploymentModel(input.page,input.labels,input.options)
  :buildModel(input.page,input.labels,input.notes,input.options);
 const revision=++mount.revision;
 const fit=input.options?.mode==='resource'?()=>{
  if(current()&&mounts.get(element)===mount&&mount.revision===revision)void Promise.resolve(mount.viewport?.initial()).catch(()=>{});
 }:undefined;
 const {model,ids}=built;
 const links='links' in built&&built.links instanceof Map?built.links:new Map<string,string>();
 const layouted=await layoutLikeC4Model(model,{graphviz:mount.graphviz,concurrency:1,timeout:20000,throwOnTimeout:true});
 if(!current()||mounts.get(element)!==mount)return true;
 mount.focusRoot=input.options?.root?rendererId(input.options.root):undefined;
 mount.focusAnchor=input.options?.root?resourceViewportAnchor(input.options.root,input.page.relations):.5;
 mount.root.render(<LikeC4ModelProvider likec4model={layouted}><ThemedDiagram element={element} revision={layouted} fit={fit}>{scheme=><ReactLikeC4 colorScheme={scheme} onInitialized={({xyflow})=>{
  if(!current()||mounts.get(element)!==mount)return;
  const focus=()=>{
   if(mounts.get(element)!==mount||!mount.focusRoot)return;
   const node=xyflow.getNode(mount.focusRoot);if(!node)return;
   const bounds=element.getBoundingClientRect();
   const camera=focusedResourceViewport(bounds.width,bounds.height,xyflow.getNodesBounds([node]),mount.focusAnchor);
   if(camera)return xyflow.setViewport(camera,{duration:150});
  };
  mount.viewport={zoomIn:()=>xyflow.zoomIn({duration:150}),zoomOut:()=>xyflow.zoomOut({duration:150}),fit:()=>xyflow.fitView({duration:150}),focus,
   initial:async()=>{const revision=mount.revision;await xyflow.fitView({duration:0,maxZoom:1});if(mounts.get(element)===mount&&mount.revision===revision&&xyflow.getZoom()<.6)await focus();}};
 }} viewId="topology" injectFontCss={false} controls={false} enableSearch={false} enableFocusMode={false} enableElementDetails={false} enableRelationshipDetails={false} enableNotations={false} showNavigationButtons={false} nodesSelectable onEdgeClick={(edge)=>{if(!current()||mounts.get(element)!==mount)return;for(const relation of edge.relations){const id=links.get(relation);if(id){input.selectLink?.(id);break;}}}} onNodeClick={(node)=>{const id=ids.get(node.id);if(id&&current()&&mounts.get(element)===mount)input.select(id);}} style={{height:'100%',width:'100%'}} />}</ThemedDiagram></LikeC4ModelProvider>);
 return true;
}
export function render(element:HTMLElement,page:GraphPage,labels:Record<string,string>,callback:Callback,overlayNotes:Record<string,string>|null=null,options?:DeploymentOptions):Promise<boolean> {
 if(page.nodes.length===0){dispose(element);return Promise.resolve(true);}
 let mount=mounts.get(element);
 if(!mount){
  const created:Mount={root:createRoot(element),revision:0,graphviz:new GraphvizWasmAdapter(),queue:new DiagramRenderQueue((input,current)=>apply(element,created,input,current))};
  mounts.set(element,created);mount=created;
 }
 return mount.queue.enqueue({page,labels,notes:overlayNotes??{},options,select:id=>{void callback.invokeMethodAsync('SelectNode',id);},selectLink:id=>{void callback.invokeMethodAsync('SelectLink',id);}});
}
export function dispose(element:HTMLElement) {
 const previous=mounts.get(element);
 if(previous){mounts.delete(element);previous.queue.dispose();previous.root.unmount();previous.graphviz.dispose();element.replaceChildren();}
}

export function viewport(element:HTMLElement,action:string){
 const controls=mounts.get(element)?.viewport;
 if(action==='in')return controls?.zoomIn();
 if(action==='out')return controls?.zoomOut();
 if(action==='fit')return controls?.fit();
 if(action==='focus')return controls?.focus();
}
