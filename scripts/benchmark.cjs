// Usage: node scripts/benchmark.cjs trace.etl output.json [worker.exe]
// The default uses the bundled SDK and Release worker. All timings include JSON stdio.
const fs=require('fs'),path=require('path'),{spawn}=require('child_process'),readline=require('readline'),crypto=require('crypto'),{performance}=require('perf_hooks');
const root=path.resolve(__dirname,'..');
const source=path.resolve(process.argv[2]||'');
const output=path.resolve(process.argv[3]||path.join(root,'.artifacts/validation/benchmark.json'));
if(!process.argv[2]||!fs.existsSync(source)){throw new Error('Supply an existing ETL path.');}
const cache=path.join(path.dirname(output),'benchmark-cache-'+crypto.randomUUID());
fs.mkdirSync(cache,{recursive:true});
const executable=process.argv[4]||path.join(root,'.tools/dotnet/dotnet.exe');
const args=process.argv[4]?[]:[path.join(root,'src/Woi.Worker/bin/Release/net10.0-windows/Woi.Worker.dll')];
const worker=spawn(executable,[...args,'--cache-root',cache],{windowsHide:true,stdio:['pipe','pipe','pipe']});
const pending=new Map();let counter=0,errors='';
worker.stderr.on('data',x=>errors+=x.toString().slice(0,10000));
readline.createInterface({input:worker.stdout}).on('line',line=>{
    let message;try{message=JSON.parse(line);}catch{return;}
    const item=pending.get(message.id);if(!item||message.kind==='progress')return;
    clearTimeout(item.timer);pending.delete(message.id);
    if(message.kind==='error')item.reject(new Error(message.code+': '+message.error));else item.resolve(message.result);
});
worker.on('exit',code=>{for(const item of pending.values()){clearTimeout(item.timer);item.reject(new Error('Worker exited '+code+' '+errors));}pending.clear();});
async function request(method,parameters){
    const id=String(++counter);
    return new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>{pending.delete(id);reject(new Error('Timed out '+method));},180000);
        pending.set(id,{resolve,reject,timer});
        worker.stdin.write(JSON.stringify({id,version:1,method,parameters})+'\n');
    });
}
const percentile=values=>[...values].sort((a,b)=>a-b)[Math.ceil(values.length*.95)-1];
async function timed(method,parameters){const start=performance.now();const result=await request(method,parameters);return {result,ms:performance.now()-start};}
(async()=>{
    try {
        const cold=await timed('import',{path:source});
        const warm=await timed('import',{path:source});
        const session=warm.result.session;
        const interval={startMs:session.durationMs/4,endMs:session.durationMs*.75,ranking:'CPU'};
        const timings={metrics:[],contributors:[],firstPage:[],sequentialPages:[]};
        for(let i=0;i<20;i++){
            timings.metrics.push((await timed('metrics',interval)).ms);
            timings.contributors.push((await timed('contributors',interval)).ms);
            timings.firstPage.push((await timed('events',{startMs:0,endMs:session.durationMs,limit:500})).ms);
        }
        let cursor=null,pages=0,total=0,more=true;
        const seen=new Set();
        while(more && pages<100){
            const page=await timed('events',{startMs:0,endMs:session.durationMs,limit:500,cursor});
            timings.sequentialPages.push(page.ms);pages++;total+=page.result.events.length;
            for(const event of page.result.events){if(seen.has(event.index))throw new Error('Duplicate event in consecutive pages');seen.add(event.index);}
            cursor=page.result.nextCursor;more=page.result.more;
        }
        const diagnostics=await request('diagnostics',{});
        const p95=Object.fromEntries(Object.entries(timings).map(([key,values])=>[key,percentile(values)]));
        const report={schema:1,recordedUtc:new Date().toISOString(),sourceSha256:session.sourceHash,sourceBytes:fs.statSync(source).size,
            sourceFile:path.basename(source),analysisVersion:session.analysisVersion,events:session.indexedEvents,
            coldImportMs:cold.ms,verifiedWarmImportMs:warm.ms,p95Ms:p95,samples:20,sequentialPages:pages,pagedEvents:total,
            peakWorkerBytes:diagnostics.peakWorkingSetBytes,
            gates:{coldUnderTwoMinutes:cold.ms<120000,warmUnderFiveSeconds:warm.ms<5000,queriesUnder300Ms:Object.values(p95).every(x=>x<300),peakUnderTwoGiB:diagnostics.peakWorkingSetBytes<2147483648},
            limitations:['Small fixture measurements do not certify the maximum 1 GiB / five-million-event envelope.','Import includes worker summary; desktop rendering is measured separately.']};
        fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(report,null,2));
        console.log(JSON.stringify(report,null,2));
    }finally{worker.stdin.end();setTimeout(()=>{if(worker.exitCode===null)worker.kill();},2000).unref();}
})().catch(error=>{console.error(error.message);process.exitCode=1;});
