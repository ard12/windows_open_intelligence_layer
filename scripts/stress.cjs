// Stress the packaged worker using real ETLs. No fixture or original recording is modified.
const fs=require('fs'),path=require('path'),crypto=require('crypto'),{spawn}=require('child_process'),readline=require('readline'),{performance}=require('perf_hooks'),assert=require('assert/strict');
const root=path.resolve(__dirname,'..');
const executable=path.resolve(process.argv[2]||path.join(root,'dist/WindowsOpenIntelligence/Woi.Worker.exe'));
const reportPath=path.resolve(process.argv[3]||path.join(root,'.artifacts/validation/stress.json'));
const iterations=Number(process.argv[4]||20);
const inputs=process.argv.slice(5).map(x=>path.resolve(x));
if(!inputs.length||!Number.isInteger(iterations)||iterations<1||iterations>100)throw new Error('Usage: node scripts/stress.cjs worker.exe report.json iterations trace1.etl [trace2.etl]');
const cache=path.join(path.dirname(reportPath),'stress-cache-'+crypto.randomUUID());fs.mkdirSync(cache,{recursive:true});
const hashes=inputs.map(x=>crypto.createHash('sha256').update(fs.readFileSync(x)).digest('hex'));
let totalRequests=0,pagedEvents=0,restarts=0,worker,sequence=0,peak=0;
const samples={import:[],contributors:[],metrics:[],firstPage:[],laterPage:[],evidence:[],export:[]};
function launch(cacheDirectory=cache){
    const process=spawn(executable,['--cache-root',cacheDirectory],{windowsHide:true,stdio:['pipe','pipe','pipe']});
    const pending=new Map();let stderr='';
    process.stderr.on('data',chunk=>stderr+=chunk.toString().slice(0,8192));
    readline.createInterface({input:process.stdout}).on('line',line=>{
        let message;try{message=JSON.parse(line);}catch{return;}
        const request=pending.get(message.id);if(!request||message.kind==='progress'){request?.progress?.(message);return;}
        pending.delete(message.id);clearTimeout(request.timeout);
        if(message.kind==='error')request.reject(Object.assign(new Error(message.error),{code:message.code}));else request.resolve(message.result);
    });
    process.on('exit',code=>{for(const item of pending.values()){clearTimeout(item.timeout);item.reject(new Error('Worker exited '+code+' '+stderr));}pending.clear();});
    async function request(method,parameters,progress){
        totalRequests++;const id=String(++sequence);
        return new Promise((resolve,reject)=>{
            const timeout=setTimeout(()=>{pending.delete(id);reject(new Error('Timed out: '+method));},180000);
            pending.set(id,{resolve,reject,timeout,progress});
            process.stdin.write(JSON.stringify({id,version:1,method,parameters})+'\n');
        });
    }
    return {process,request};
}
async function timed(method,parameters,key){
    const before=performance.now();const result=await worker.request(method,parameters);
    if(samples[key])samples[key].push(performance.now()-before);return result;
}
const p95=values=>values.length?[...values].sort((a,b)=>a-b)[Math.ceil(values.length*.95)-1]:null;
(async()=>{
    const runs=[];
    try{
        worker=launch();
        for(let i=0;i<iterations;i++){
            const index=i%inputs.length,source=inputs[index];
            const summary=await timed('import',{path:source},'import'),session=summary.session;
            assert.equal(session.sourceHash,hashes[index]);assert(session.indexedEvents>0);
            const interval={startMs:0,endMs:session.durationMs,ranking:'CPU'};
            const contributors=await timed('contributors',interval,'contributors');
            const fullContributorMs=samples.contributors.at(-1);
            assert(contributors.some(x=>x.cpuSamples>0||x.diskBytes>0),'No resource contributors');
            const metrics=await timed('metrics',interval,'metrics');assert(metrics.length<=2000);
            let page=await timed('events',{...interval,category:'CPU',limit:500},'firstPage');
            const selected=page.events[0];assert(selected);
            const detail=await timed('evidence',{index:selected.index},'evidence');
            assert.equal(detail.observation.processId,selected.processId);assert.equal(detail.observation.index,selected.index);
            const seen=new Set();let pages=0;
            do{
                for(const event of page.events){assert(!seen.has(event.index),'Duplicate event across pages');seen.add(event.index);}
                pages++;pagedEvents+=page.events.length;
                if(!page.more||pages>=10)break;
                page=await timed('events',{...interval,category:'CPU',limit:500,cursor:page.nextCursor},'laterPage');
            }while(true);
            // Exact partial-interval replay exercises edge buckets instead of only SQLite aggregates.
            await timed('contributors',{startMs:session.durationMs*.213,endMs:session.durationMs*.789,ranking:'Disk'},'contributors');
            const partialContributorMs=samples.contributors.at(-1);
            const bookmark={id:'stress-'+i,label:'stress interval '+i,startMs:0,endMs:Math.min(1000,session.durationMs)};
            await worker.request('bookmark',bookmark);
            assert((await worker.request('bookmarks',{})).some(x=>x.id===bookmark.id));
            const destination=path.join(cache,'report-'+i+'.json');
            await timed('export',{path:destination,format:'json',interval},'export');
            const report=JSON.parse(fs.readFileSync(destination,'utf8'));assert.equal(report.session.sourceHash,hashes[index]);assert(report.contributors.length>0);
            const diagnostics=await worker.request('diagnostics',{});peak=Math.max(peak,diagnostics.peakWorkingSetBytes);
            assert(peak<2147483648,'Worker exceeded 2 GiB during repeated replay');
            runs.push({iteration:i,fullContributorMs,partialContributorMs,sourceFile:path.basename(source),events:session.indexedEvents,cpuSamples:contributors.reduce((sum,x)=>sum+(x.cpuSamples||0),0),diskBytes:contributors.reduce((sum,x)=>sum+(x.diskBytes||0),0),pages,workingSetBytes:diagnostics.workingSetBytes});
            if(i%5===4){worker.process.kill();worker=launch();restarts++;}
            console.log('Stress iteration '+(i+1)+'/'+iterations+' passed: '+path.basename(source)+' contributors full='+fullContributorMs.toFixed(1)+' ms, partial='+partialContributorMs.toFixed(1)+' ms');
        }
        const damaged=path.join(cache,'damaged.etl');fs.writeFileSync(damaged,Buffer.from([1,2,3,4,5]));
        await assert.rejects(()=>worker.request('import',{path:damaged}));
        await worker.request('import',{path:inputs[0]});
        await assert.rejects(()=>worker.request('events',{startMs:0,endMs:1000,cursor:-1}),error=>error.code==='InvalidQuery');
        await assert.rejects(()=>worker.request('sql',{query:'SELECT * FROM anything'}),error=>error.code==='UnknownMethod');
        // Cancellation and a crash during decoding must not publish a completed cache.
        const abortCache=path.join(cache,'cancel');fs.mkdirSync(abortCache);
        const abortWorker=launch(abortCache);
        let cancelled=false;
        try {
            await abortWorker.request('import',{path:inputs[0]},message=>{
                if(message.result?.stage==='Decode')
                    abortWorker.request('cancel',{requestId:message.id}).catch(()=>{});
            });
        }catch(error){cancelled=error.code==='Cancelled';}
        assert(cancelled,'Import cancellation was not acknowledged');
        abortWorker.process.stdin.end();
        const markerExists=directory=>fs.readdirSync(directory,{withFileTypes:true}).some(entry=>
            entry.isDirectory()?markerExists(path.join(directory,entry.name)):entry.name==='complete.json');
        assert(!markerExists(abortCache),'Cancelled import published a completed cache');
        const crashCache=path.join(cache,'crash');fs.mkdirSync(crashCache);
        let crashWorker=launch(crashCache);
        await assert.rejects(()=>crashWorker.request('import',{path:inputs[0]},message=>{
            if(message.result?.stage==='Decode')crashWorker.process.kill();
        }));
        assert(!markerExists(crashCache),'Crashed import published a completed cache');
        crashWorker=launch(crashCache);
        const rebuilt=await crashWorker.request('import',{path:inputs[0]});
        assert.equal(rebuilt.session.sourceHash,hashes[0]);crashWorker.process.stdin.end();
        inputs.forEach((input,index)=>assert.equal(crypto.createHash('sha256').update(fs.readFileSync(input)).digest('hex'),hashes[index]));
        const results={schema:1,success:true,recordedUtc:new Date().toISOString(),iterations,totalRequests,pagedEvents,restarts,peakWorkerBytes:peak,
            p95Ms:Object.fromEntries(Object.entries(samples).map(([key,values])=>[key,p95(values)])),runs,
            sourceIntegrityPreserved:true,damagedImportRecovery:true,unsupportedQueriesRejected:true,cancellationAtomicity:true,crashDuringImportRecovery:true,
            limitations:['This stress run measures replay and worker recovery. Privileged recording and desktop interaction have separate tests.']};
        fs.mkdirSync(path.dirname(reportPath),{recursive:true});fs.writeFileSync(reportPath,JSON.stringify(results,null,2));console.log(JSON.stringify({...results,runs:undefined},null,2));
    }finally{if(worker){worker.process.stdin.end();setTimeout(()=>worker.process.kill(),2000).unref();}}
})().catch(error=>{fs.mkdirSync(path.dirname(reportPath),{recursive:true});fs.writeFileSync(reportPath,JSON.stringify({success:false,error:error.stack},null,2));console.error(error);process.exitCode=1;});
