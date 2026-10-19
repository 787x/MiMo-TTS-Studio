// Stop must also cover an in-flight request's finally block, not only its timer.
export function startPolling(action:(signal:AbortSignal)=>Promise<void>, interval=1200){
 let stopped=false
 let timer:ReturnType<typeof setTimeout>|undefined
 const controller=new AbortController()
 async function tick(){
  try{await action(controller.signal)}
  catch{ /* The caller displays recoverable request errors. */ }
  finally{if(!stopped)timer=setTimeout(tick,interval)}
 }
 void tick()
 return ()=>{stopped=true;controller.abort();if(timer!==undefined)clearTimeout(timer)}
}
