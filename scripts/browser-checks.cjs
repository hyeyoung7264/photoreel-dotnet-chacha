const fs=require('node:fs');
const path=require('node:path');
const {chromium}=require('../evidence/playwright');
const base=process.env.PHOTOREEL_TEST_URL||'http://127.0.0.1:8775';
function assert(value,message){if(!value)throw new Error(message);}
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const context=await browser.newContext({viewport:{width:1440,height:1100}});
 const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 const report={base,checks:{},errors};
 try{
  await page.goto(base);await page.waitForFunction(()=>state.pid&&state.meta);report.pid=await page.evaluate(()=>state.pid);
  const files=[0,1,2,3].map(i=>path.resolve(`evidence/browser-fixtures/synthetic-${i}.jpg`));
  await page.locator('#file').setInputFiles(files);await page.waitForFunction(()=>state.photos.length===4,{},{timeout:60000});
  await page.locator('#concept').fill('잔잔한 기록 15초');await page.locator('#title').fill('C# 로컬 검증');await page.locator('#duration').selectOption('15');await page.locator('#btn-plan').click();
  await page.waitForFunction(()=>state.plan&&state.plan.style==='calm');
  report.checks.upload_plan=await page.evaluate(()=>({photos:state.photos.length,style:state.plan.style,clip:state.photos.every(p=>p.methods.semantic==='clip')}));
  assert(report.checks.upload_plan.clip,'Real CLIP not used in browser upload');
  await page.locator('#title').fill('Current title');await page.locator('#aspect').selectOption('4:5');await page.locator('#style').selectOption('cinematic');
  await page.waitForFunction(()=>state.plan.style==='cinematic'&&state.plan.title==='Current title'&&state.plan.output.aspect==='4:5');
  assert(await page.evaluate(()=>state.plannedInput===inputKey()),'Style replan incorrectly marked fresh');report.checks.style_keeps_current_inputs=true;
  await page.evaluate(async()=>{for(const scene of state.plan.scenes)scene.included=true;await savePlan();});
  const count=await page.locator('#scenes .scene').count();
  if(count>1){await page.locator('#scenes .scene').first().getByTitle('뒤로',{exact:true}).click();await page.locator('#scenes .scene').last().getByText('빼기',{exact:true}).click();}
  await page.locator('#scenes select[title="움직임"]').first().selectOption('zoom-out');await page.waitForFunction(()=>document.getElementById('save-state').textContent==='저장됨');
  await page.screenshot({path:'evidence/browser-plan.png',fullPage:true});
  report.checks.editor=await page.evaluate(()=>({included:state.plan.scenes.filter(s=>s.included).length,excluded:state.plan.scenes.filter(s=>!s.included).length,motion:state.plan.scenes.find(s=>s.included).motion}));
  assert(report.checks.editor.included===3&&report.checks.editor.excluded===1&&report.checks.editor.motion==='zoom-out','Editor changes were not verified');
  // Full API plan still runs the actual renderer; reduce test frame dimensions only.
  await page.evaluate(async()=>{state.plan.output={aspect:'test-9:16',width:240,height:426,fps:12};await savePlan();});
  let releaseSave;let capturedSave=false;const saveGate=new Promise(resolve=>releaseSave=resolve);
  await page.route('**/api/projects/*/plan',async route=>{
    if(route.request().method()==='PUT'&&!capturedSave){capturedSave=true;const response=await route.fetch();await saveGate;await route.fulfill({response});}else await route.continue();
  });
  await page.locator('#btn-render').click();await page.waitForFunction(()=>state.rendering);
  assert(await page.locator('#grade').isDisabled(),'Editor remains enabled during final save');
  assert(await page.locator('#title').isDisabled(),'Title remains enabled during final save');
  assert(await page.evaluate(()=>document.querySelector('#s-plan').inert),'Scene editing remains interactive');
  releaseSave();await page.waitForFunction(()=>state.renders?.some(r=>r.status==='ok'),{},{timeout:120000});
  await page.unroute('**/api/projects/*/plan');assert(await page.locator('#grade').isEnabled(),'Editor did not unlock');report.checks.final_save_locks_edits=true;
  const video=page.locator('#result video');await video.waitFor();await page.waitForFunction(()=>document.querySelector('#result video')?.readyState>=1);
  await video.evaluate(async v=>{v.muted=true;await v.play();});await page.waitForFunction(()=>document.querySelector('#result video').currentTime>.15);
  report.checks.playback=await video.evaluate(v=>({duration:v.duration,width:v.videoWidth,height:v.videoHeight,currentTime:v.currentTime}));
  const result=await context.request.get(`${base}/api/projects/${report.pid}`);const data=await result.json();const n=data.renders.filter(r=>r.status==='ok').at(-1).render;
  const download=await context.request.get(`${base}/api/projects/${report.pid}/renders/${n}/video?download=1`);assert(download.ok(),'Download failed');assert(download.headers()['content-disposition'].includes('attachment'),'Download header missing');
  const buffer=await download.body();assert(buffer.subarray(4,8).toString()==='ftyp','MP4 missing');fs.writeFileSync('evidence/browser-video.mp4',buffer);
  const range=await context.request.get(`${base}/api/projects/${report.pid}/renders/${n}/video`,{headers:{Range:'bytes=0-99'}});assert(range.status()===206&&(await range.body()).length===100,'Range playback failed');report.checks.download_range=true;
  await page.reload();await page.waitForFunction(()=>state.renders?.some(r=>r.status==='ok'));assert(await page.evaluate(()=>state.pid)===report.pid,'Restore ID mismatch');report.checks.reload_restore=true;
  // An HTTP 400 saving the plan must prevent POST render.
  let renders=0;const watch=req=>{if(req.method()==='POST'&&/\/render$/.test(req.url()))renders++;};page.on('request',watch);
  await page.route('**/api/projects/*/plan',async route=>route.request().method()==='PUT'?route.fulfill({status:400,contentType:'application/json',body:JSON.stringify({detail:'검증용 저장 실패'})}):route.continue());
  await page.locator('#btn-render').click();await page.waitForFunction(()=>document.getElementById('save-state').textContent==='저장 실패');assert(renders===0,'Render started after failed save');report.checks.failed_save_blocks_render=true;
  await page.unroute('**/api/projects/*/plan');page.off('request',watch);
  // A changed concept must be submitted before POST render, then cancellation is real.
  await page.locator('#aspect').selectOption('9:16');await page.locator('#concept').fill('신나는 하이라이트 15초');await page.locator('#btn-render').click();await page.waitForFunction(()=>state.plan.style==='upbeat'&&document.getElementById('progress').hidden===false);
  await page.locator('#btn-cancel').click();await page.waitForFunction(()=>!state.rendering,{},{timeout:60000});
  const cancelled=await context.request.get(`${base}/api/projects/${report.pid}`);const cancelData=await cancelled.json();assert(cancelData.plan.style==='upbeat','Stale plan rendered');assert(cancelData.job.state==='cancelled','Cancel not terminal');report.checks.stale_concept_and_cancel=true;
  await page.screenshot({path:'evidence/browser-result.png',fullPage:true});assert(errors.length===0,'Browser JS errors');report.pass=true;
 }catch(e){report.pass=false;report.error=e.stack;throw e;}
 finally{fs.writeFileSync('evidence/browser-results.json',JSON.stringify(report,null,2));await browser.close();console.log(JSON.stringify(report,null,2));}
})().catch(()=>process.exitCode=1);
