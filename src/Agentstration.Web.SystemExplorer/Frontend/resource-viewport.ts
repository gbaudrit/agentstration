export interface ViewportBox { x:number; y:number; width:number; height:number }
export interface ResourceViewport { x:number; y:number; zoom:number }

// Change only the camera; source/layout coordinates and semantic identities stay intact.
export function focusedResourceViewport(width:number,height:number,root:ViewportBox,anchor=.5):ResourceViewport|null {
  if (![width,height,root.x,root.y,root.width,root.height].every(Number.isFinite)
      || width<=48 || height<=48 || root.width<=0 || root.height<=0) return null;
  const zoom=Math.min(.8,(width-48)/root.width,(height-48)/root.height);
  const center=Math.min(width-24-root.width*zoom/2,Math.max(24+root.width*zoom/2,width*anchor));
  return {x:center-(root.x+root.width/2)*zoom,y:height/2-(root.y+root.height/2)*zoom,zoom};
}

export function resourceViewportAnchor(root:string,relations:readonly {source:string;target:string}[]):number {
 const outgoing=relations.some(edge=>edge.source===root),incoming=relations.some(edge=>edge.target===root);
 return outgoing && !incoming ? .25 : incoming && !outgoing ? .75 : .5;
}
