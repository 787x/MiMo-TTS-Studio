import {test} from 'node:test'
import assert from 'node:assert/strict'
import {startPolling} from '../src/polling.ts'
test('unmount during an in-flight request aborts and never schedules another poll',async()=>{
 let calls=0,finish,signal
 const completed=new Promise(resolve=>{finish=resolve})
 const stop=startPolling(async s=>{calls++;signal=s;await completed},5)
 assert.equal(calls,1)
 stop()
 assert.equal(signal.aborted,true)
 finish()
 await new Promise(resolve=>setTimeout(resolve,25))
 assert.equal(calls,1)
})
test('stopping an idle polling timer prevents further requests',async()=>{
 let calls=0
 const stop=startPolling(async()=>{calls++},10)
 await new Promise(resolve=>setTimeout(resolve,2))
 stop()
 await new Promise(resolve=>setTimeout(resolve,25))
 assert.equal(calls,1)
})
