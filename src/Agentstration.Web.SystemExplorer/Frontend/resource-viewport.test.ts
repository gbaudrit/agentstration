import {test} from 'node:test';
import assert from 'node:assert/strict';
import {focusedResourceViewport,resourceViewportAnchor} from './resource-viewport';

test('focused camera keeps a dense graph root centered at readable scale',()=>{
 const root={x:-800,y:1600,width:260,height:140};
 const view=focusedResourceViewport(1200,700,root)!;
 assert.equal(view.zoom,.8);
 assert.equal((root.x+root.width/2)*view.zoom+view.x,600);
 assert.equal((root.y+root.height/2)*view.zoom+view.y,350);
 assert.deepEqual(root,{x:-800,y:1600,width:260,height:140});
});
test('small viewports still contain a large focused resource',()=>{
 const root={x:400,y:300,width:700,height:500};
 const view=focusedResourceViewport(320,240,root)!;
 assert.ok(root.x*view.zoom+view.x>=24-1e-6);
 assert.ok(root.y*view.zoom+view.y>=24-1e-6);
 assert.ok((root.x+root.width)*view.zoom+view.x<=296+1e-6);
 assert.ok((root.y+root.height)*view.zoom+view.y<=216+1e-6);
});
test('hidden or unmeasured canvases do not change the camera',()=>{
 assert.equal(focusedResourceViewport(0,500,{x:0,y:0,width:200,height:100}),null);
 assert.equal(focusedResourceViewport(500,300,{x:0,y:0,width:0,height:100}),null);
 assert.equal(focusedResourceViewport(500,300,{x:NaN,y:0,width:200,height:100}),null);
});

test('directional framing leaves room for authorized dependency or usage branches',()=>{
 assert.equal(resourceViewportAnchor('root',[{source:'root',target:'tool'}]),.25);
 assert.equal(resourceViewportAnchor('root',[{source:'agent',target:'root'}]),.75);
 assert.equal(resourceViewportAnchor('root',[]),.5);
 assert.equal(resourceViewportAnchor('root',[{source:'root',target:'tool'},{source:'agent',target:'root'}]),.5);
 const root={x:100,y:100,width:200,height:100};
 const view=focusedResourceViewport(1200,700,root,.25)!;
 assert.equal((root.x+root.width/2)*view.zoom+view.x,300);
 const mobile=focusedResourceViewport(200,300,root,.25)!;
 assert.ok(root.x*mobile.zoom+mobile.x>=24-1e-6);
});
