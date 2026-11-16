import React from 'react';
import {createRoot,Root} from 'react-dom/client';
import {LikeC4ModelProvider,ReactLikeC4} from '@likec4/diagram';
import {layoutLikeC4Model,GraphvizWasmAdapter} from '@likec4/layouts';
import {buildModel,GraphPage} from './model';
type Callback={invokeMethodAsync(method:string,id:string):Promise<void>};
type Mount={root:Root; graphviz:GraphvizWasmAdapter};
const mounts=new WeakMap<HTMLElement,Mount>();
export async function render(element:HTMLElement,page:GraphPage,labels:Record<string,string>,callback:Callback):Promise<boolean> {
  dispose(element);
  if(page.nodes.length===0)return true;
  const mount:Mount={root:createRoot(element),graphviz:new GraphvizWasmAdapter()};
  mounts.set(element,mount);
  try {
    const {model,ids}=buildModel(page,labels);
    const layouted=await layoutLikeC4Model(model,{graphviz:mount.graphviz,concurrency:1,timeout:20000,throwOnTimeout:true});
    if(mounts.get(element)!==mount)return false;
    mount.root.render(<LikeC4ModelProvider likec4model={layouted}><ReactLikeC4 viewId="topology" injectFontCss={false} controls={false} enableSearch={false} enableFocusMode={false} enableElementDetails={false} enableRelationshipDetails={false} enableNotations={false} showNavigationButtons={false} nodesSelectable onNodeClick={(node)=>{const id=ids.get(node.id);if(id&&mounts.get(element)===mount)void callback.invokeMethodAsync('SelectNode',id);}} style={{height:'100%',width:'100%'}} /></LikeC4ModelProvider>);
    return true;
  } catch { if(mounts.get(element)===mount)dispose(element);return false; }
}
export function dispose(element:HTMLElement) {const previous=mounts.get(element);if(previous){mounts.delete(element);previous.root.unmount();previous.graphviz.dispose();element.replaceChildren();}}
