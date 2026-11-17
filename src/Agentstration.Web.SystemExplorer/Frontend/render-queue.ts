import type {GraphPage} from './model';
import type {DeploymentOptions} from './deployment-model';
export interface DiagramRenderInput {
 page:GraphPage;
 labels:Record<string,string>;
 notes:Record<string,string>;
 select:(id:string)=>void;
 options?:DeploymentOptions;
}
// Keep the displayed graph while one layout runs; retain only the latest pending update.
export class DiagramRenderQueue {
 private pending?:{input:DiagramRenderInput;done:(success:boolean)=>void};
 private running=false;
 private disposed=false;
 constructor(private readonly apply:(input:DiagramRenderInput,current:()=>boolean)=>Promise<boolean>){}
 enqueue(input:DiagramRenderInput):Promise<boolean> {
  if(this.disposed)return Promise.resolve(true);
  return new Promise(done=>{
   this.pending?.done(true);
   this.pending={input,done};
   if(!this.running)void this.drain();
  });
 }
 dispose(){this.disposed=true;this.pending?.done(true);this.pending=undefined;}
 private async drain(){
  this.running=true;
  try{
   while(!this.disposed&&this.pending){
    const request=this.pending;this.pending=undefined;
    let success=false;
    try{success=await this.apply(request.input,()=>!this.disposed);}catch{success=false;}
    request.done(this.disposed||success);
   }
  }finally{this.running=false;}
 }
}
