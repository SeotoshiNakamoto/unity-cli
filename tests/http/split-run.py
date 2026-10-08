"""Prepared by implementation worker; ONLY execution workers invoke live phases.
Frozen baseline/candidate copies avoid stash, branch changes and broad restore.
"""
import ast
import concurrent.futures as cf
import ctypes
import hashlib
import json
import os
from pathlib import Path
import socket
import statistics
import subprocess
import sys
import time
import traceback
import check as h

BASE=Path('D:/tmp/http-split')
ART=BASE/'artifacts'
MAN=json.loads((BASE/'manifest.json').read_text(encoding='utf-8-sig'))
ROOT=Path('D:/Projects/unity-cli')
PROJECT=Path(h.PROJECT).parent
DEST=PROJECT/'tools/unity-cli/unity-connector/Editor'
RUNTIME=tuple(MAN['candidate'])
PHASE=sys.argv[1]
OLD=str(BASE/'unity-cli-old.exe');NEW=str(BASE/'unity-cli-new.exe')
FIXTURE=DEST/'HttpThreadProbe.cs'
LOG=PROJECT/'client/Logs/Editor.log'


def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()

def process(args,cwd=None,timeout=300):
    r=subprocess.run(args,cwd=cwd,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=timeout,creationflags=subprocess.CREATE_NO_WINDOW)
    if r.returncode:raise AssertionError({'args':args,'exit':r.returncode,'stdout':r.stdout[-3000:],'stderr':r.stderr[-3000:]})
    return r.stdout

def select(group):
    h.OUT=ART/group;h.OUT.mkdir(parents=True,exist_ok=True)
    h.CLI=OLD if group=='baseline' else NEW

def invariants(mode=None):
    for name,value in MAN['protected'].items():assert digest(PROJECT/name)==value,'Protected #418 file changed externally: '+name
    assert digest(PROJECT/'tools/unity-cli/unity-cli.exe')==MAN['installedCliHash'],'Installed CLI changed'
    for name,value in MAN['meta'].items():assert digest(DEST/(name+'.meta'))==value,'Runtime meta changed: '+name
    if mode:
        for name,value in MAN[mode].items():assert digest(DEST/name)==value,'Wrong connector version: '+name
    assert process(['git','-C',str(ROOT),'rev-parse','HEAD']).strip()==MAN['rootHead'],'Root HEAD changed'
    # Other agents commit to ProjectD main; only fail when a commit touched paths Unity loads.
    moved=process(['git','-C',str(PROJECT),'diff','--name-only',MAN['projectHead'],'HEAD','--','client','tools/unity-cli']).split()
    assert not moved,'Project HEAD changed Unity paths: '+repr(moved)
    for repo in (ROOT,PROJECT):assert process(['git','-C',str(repo),'branch','--show-current']).strip()=='main','Branch changed; do not switch it'
    changed=process(['git','-C',str(PROJECT),'diff','--name-only']).splitlines()
    cached=process(['git','-C',str(PROJECT),'diff','--cached','--name-only']).splitlines()
    untracked=process(['git','-C',str(PROJECT),'ls-files','--others','--exclude-standard']).splitlines()
    allowed=set(MAN['protected'])|{'tools/unity-cli/unity-connector/Editor/'+n for n in RUNTIME}|{'tools/unity-cli/unity-connector/Editor/HttpThreadProbe.cs','tools/unity-cli/unity-connector/Editor/HttpThreadProbe.cs.meta'}
    # Other agents may edit ProjectD outside the Unity project (e.g. buildops/); only guard paths Unity loads.
    relevant={p for p in changed+cached+untracked if p.startswith(('client/','tools/unity-cli/'))}
    assert relevant<=allowed,'Unexpected ProjectD paths: '+repr(relevant-allowed)
    return {'working':changed,'staged':cached,'untracked':untracked}

def identify(edit=True):
    # A read-only status then one queue probe; never start/stop another instance.
    status=h.cli('status');assert f'PID:     {MAN["expectedPid"]}' in status['stdout'],status
    h.ready()
    assert h.PID==MAN['expectedPid'] and h.PORT==MAN['expectedPort'],(h.PID,h.PORT)
    state=h.success('exec',{'code':'return new Dictionary<string, object> { ["playing"] = EditorApplication.isPlaying, ["compiling"] = EditorApplication.isCompiling, ["project"] = Application.dataPath };'})
    assert state['project']==h.PROJECT+'/Assets' and not state['compiling'],state
    if edit:assert state['playing'] is False,'Unexpected Play Mode: stop and report, do not hide external use'
    return state

def audit(offset,label,expected_timeout=False):
    with LOG.open('rb') as f:f.seek(offset);text=f.read().decode('utf-8',errors='replace')
    errors=[s for s in text.splitlines() if 'error CS' in s or s.startswith(('NullReferenceException:','InvalidOperationException:','ObjectDisposedException:','AggregateException:','SynchronizationLockException:','ThreadAbortException:'))]
    assert not errors,errors
    warnings=text.count('Background HTTP shutdown exceeded 5 seconds; retaining generation')
    assert warnings==(1 if expected_timeout else 0),{'unexpected_shutdown_warnings':warnings}
    r={'new_error_CS':text.count('error CS'),'new_lifecycle_errors':errors,'timeout_warnings':warnings,'package_cancellations':text.count('[Package Manager Window] Operation cancelled')}
    (h.OUT/(label+'-log.json')).write_text(json.dumps(r,indent=2))
    return r

def compile_editor():
    offset=LOG.stat().st_size
    response=h.cli('editor','refresh','--compile',timeout=300)
    log=audit(offset,'compile')
    identify();return {'cli':response,'log':log}

def switch(mode,fixture=True):
    invariants();identify()
    for name in RUNTIME:(DEST/name).write_bytes((BASE/(('committed' if mode=='baseline' else mode)+'-editor')/name).read_bytes())
    if fixture:
        if FIXTURE.exists():assert '_http_thread_probe' in FIXTURE.read_text(),'Foreign fixture collision'
        FIXTURE.write_bytes((ROOT/'tests/http/HttpThreadProbe.cs').read_bytes())
    r=compile_editor();invariants(mode);return r

def response_phase(group):
    select(group);invariants(group);identify();h.PHASE='baseline' if group=='baseline' else 'regression'
    start=LOG.stat().st_size
    h.baseline_or_regression() # 30 responses + 25 samples/command; no actual captures.
    result={'responses':len(json.loads((h.OUT/(h.PHASE+'-responses.json')).read_text())),'log':audit(start,'responses')}
    assert result['responses']==30
    return result

def compare_responses():
    select('comparison')
    for prefix,group in [('baseline','baseline'),('regression','candidate')]:
        for suffix in ('tools','responses','timings'):(h.OUT/(prefix+'-'+suffix+'.json')).write_bytes((ART/group/(prefix+'-'+suffix+'.json')).read_bytes())
    h.compare()
    means={}
    for prefix in ('baseline','regression'):
        data=json.loads((h.OUT/(prefix+'-timings.json')).read_text())
        means[prefix]={c:{'n':sum(r['command']==c for r in data),'mean_ms':statistics.mean(r['ms'] for r in data if r['command']==c)} for c in ('console','exec')}
    assert all(v['n']>=20 for g in means.values() for v in g.values())
    return {'responses':30,'differences':[],'performance':means,'503':'native cancellation path checked separately; intended new error'}

def handle_count():
    from ctypes import wintypes
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];k.OpenProcess.restype=wintypes.HANDLE
    k.GetProcessHandleCount.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)];k.CloseHandle.argtypes=[wintypes.HANDLE]
    p=k.OpenProcess(0x1000,False,h.PID);assert p
    try:
        n=wintypes.DWORD();assert k.GetProcessHandleCount(p,ctypes.byref(n));return n.value
    finally:k.CloseHandle(p)

def footprint():
    h.cli('exec','GC.Collect();\nGC.WaitForPendingFinalizers();\nreturn 1;')
    samples=[]
    for _ in range(3):
        time.sleep(.5);samples.append({'handles':handle_count(),'threads':h.os_thread_count()})
    return {'handles':statistics.median(s['handles'] for s in samples),'threads':statistics.median(s['threads'] for s in samples),'samples':samples}

def reloads(group):
    select(group);invariants(group);identify();original=FIXTURE.read_text(encoding='utf-8');start=LOG.stat().st_size
    rows=[]
    for i in range(7): # two warmups + five measured, SAME procedure on both versions.
        before=footprint();old=h.probe('state')
        if group=='candidate':h.probe('configure',shutdown_path=str(h.OUT/'shutdown.jsonl'))
        large=partial=None
        try:
            large=socket.create_connection(('127.0.0.1',h.PORT),timeout=20)
            data=json.dumps({'command':'exec','params':{'code':"return new string('z',16*1024*1024);"}}).encode()
            large.sendall(f'POST /command HTTP/1.1\r\nHost: 127.0.0.1:{h.PORT}\r\nContent-Length: {len(data)}\r\n\r\n'.encode()+data)
            header=large.recv(1024);assert b'200' in header[:50],header[:100]
            partial=socket.create_connection(('127.0.0.1',h.PORT),timeout=20)
            partial.sendall(f'POST /command HTTP/1.1\r\nHost: 127.0.0.1:{h.PORT}\r\nContent-Length: 99999\r\n\r\n{{'.encode())
            FIXTURE.write_text(original+f'\n// split {group} reload {i}\n',encoding='utf-8')
            try:reply=h.request('_http_thread_probe',{'compile':True,'delay':30000,'id':f'reload-{i}'},timeout=40)
            except (OSError,TimeoutError,h.http.client.HTTPException) as ex:reply={'closed':type(ex).__name__}
            if group=='candidate':assert reply.get('status')==503 and 'outcome is unknown' in reply['json']['message'],reply
        finally:
            if large:large.close()
            if partial:partial.close()
        h.ready();new=h.probe('state');assert old['epoch']!=new['epoch'],'No actual domain reload'
        after=footprint();rows.append({'round':i,'warmup':i<2,'before':before,'after':after,'reply':reply,'epoch':old['epoch']})
        h.save('reload-footprint-progress',rows)
    report={'rows':rows,'measured':rows[2:],'log':audit(start,'reloads')}
    if group=='candidate':
        reports=[json.loads(s) for s in (h.OUT/'shutdown.jsonl').read_text().splitlines()]
        epochs={r['epoch'] for r in rows};reports=[r for r in reports if r['epoch'] in epochs]
        assert len(reports)==7 and all(r['unfinished']==0 and r['loopCompleted'] for r in reports),reports
        report['shutdown']=reports
    h.save('reload-footprint',report);return {'measured_rounds':5,'warmups':2,'footprints':[r['after'] for r in rows[2:]],'log':report['log']}

def slope(values):
    n=len(values);mx=(n-1)/2;my=statistics.mean(values)
    return sum((i-mx)*(v-my) for i,v in enumerate(values))/sum((i-mx)**2 for i in range(n))

def footprint_comparison():
    select('comparison');metrics={};decisions=[]
    for key in ('handles','threads'):
        a=[r['after'][key] for r in json.loads((ART/'baseline/reload-footprint.json').read_text())['measured']]
        b=[r['after'][key] for r in json.loads((ART/'candidate/reload-footprint.json').read_text())['measured']]
        noise=max(a)-min(a);slope_excess=slope(b)-slope(a);delta_excess=(b[-1]-b[0])-(a[-1]-a[0])
        slope_limit=max(5,noise/4) if key=='handles' else 2
        delta_limit=max(30,noise) if key=='handles' else max(12,noise)
        bad_slope=slope_excess>slope_limit;bad_delta=delta_excess>delta_limit;up=sum(b[i+1]>b[i] for i in range(4))
        decision='FAIL' if bad_slope and bad_delta and up>=3 else 'REVIEW' if bad_slope or bad_delta else 'PASS'
        decisions.append(decision);metrics[key]={'baseline':a,'candidate':b,'baseline_noise':noise,'baseline_slope':slope(a),'candidate_slope':slope(b),'slope_excess':slope_excess,'slope_limit':slope_limit,'delta_excess':delta_excess,'delta_limit':delta_limit,'positive_intervals':up,'decision':decision}
    decision='FAIL' if 'FAIL' in decisions else 'REVIEW' if 'REVIEW' in decisions else 'PASS'
    return {'decision':decision,'metrics':metrics,'note':'Control-adjusted heuristic, not proof of no possible leak; REVIEW requires orchestrator interpretation'}

def client_policy():
    # Same empty-200 wire response to old/new CLI, isolated discovery directory.
    # No Unity handler is invoked and nothing is automatically resent.
    import http.server
    import threading
    select('comparison');identify();requests=[]
    class Empty(http.server.BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_GET(self):
            data=b'{"success":true,"message":"ok","data":null}'
            self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
        def do_POST(self):
            requests.append(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
            self.send_response(200);self.send_header('Content-Length','0');self.end_headers()
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Empty)
    thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
    home=h.OUT/'empty-home';directory=home/'.unity-cli/instances';directory.mkdir(parents=True,exist_ok=True)
    env=os.environ.copy();env['USERPROFILE']=str(home);env['HOME']=str(home)
    results={}
    try:
        for key,binary in [('old',OLD),('new',NEW)]:
            (directory/'main.json').write_text(json.dumps({'state':'ready','projectPath':h.PROJECT,'port':server.server_port,'pid':h.PID,'timestamp':int(time.time()*1000)}))
            r=subprocess.run([binary,'--project',h.PROJECT,'--timeout','3000','exec','return 919;'],capture_output=True,text=True,encoding='utf-8',env=env,timeout=10,creationflags=subprocess.CREATE_NO_WINDOW)
            results[key]={'exit':r.returncode,'stdout':r.stdout,'stderr':r.stderr}
        assert results['old']['exit']==0 and results['new']['exit']!=0 and 'could not be confirmed' in results['new']['stderr'],results
        assert len(requests)==2,'CLI automatically retried an empty response'
        return {'wire':'same empty HTTP 200','old_new':results,'requests':len(requests),'unity_handler_calls':0}
    finally:server.shutdown();server.server_close();thread.join(timeout=2)


def cancellations(group):
    select(group);invariants(group);identify();marker=h.OUT/'cancel-entered.txt';marker.unlink(missing_ok=True)
    # Reload (not old-listener restart) guarantees the old waiting handler cannot
    # simply run later in the committed connector, which lacks cancellable dispatch.
    h.success('_http_thread_probe',{'delay':4000,'id':'blocker','record':str(marker),'reload_after':1300,'async':True})
    with cf.ThreadPoolExecutor(max_workers=2) as p:
        wait=p.submit(h.cli,'exec',f'File.AppendAllText(@"{marker}","queued-exec-enter\\n");\nreturn 919;',timeout=20,check=False)
        wire=p.submit(h.request,'_http_thread_probe',{'id':'queued-handler','record':str(marker),'delay':1},timeout=20)
        cli=wait.result()
        try:reply=wire.result()
        except (OSError,h.http.client.HTTPException,TimeoutError) as ex:reply={'closed':type(ex).__name__}
    h.ready();entries=marker.read_text() if marker.exists() else ''
    entered='queued-exec-enter' in entries or 'enter:queued-handler' in entries
    result={'cli':cli,'wire':reply,'entries':entries,'queued_entered':entered,'false_success':cli['code']==0 and not entered}
    if group=='candidate':assert not entered and cli['code']!=0 and 'not executed' in cli['stderr'].lower() and reply.get('status')==503,result
    else:result['interpretation']='REPRODUCED' if result['false_success'] else 'NOT_REPRODUCED (executed or failed; report, no retry needed)'
    h.save('cancellation',result);return result

def transitions():
    select('candidate');invariants('candidate');identify();r={}
    try:
        r['play']=h.cli('editor','play',timeout=180);h.ready('playing')
        assert h.success('exec',{'code':'return EditorApplication.isPlaying;'}) is True
        r['playing_status']=h.cli('status');r['playing_exec']=h.cli('exec','return 1;')
    finally:
        r['stop']=h.cli('editor','stop',timeout=180);h.ready()
        assert h.success('exec',{'code':'return EditorApplication.isPlaying;'}) is False
        h.save('transitions',r)
    return r

def load():
    select('candidate');invariants('candidate');identify();start=LOG.stat().st_size
    for cmd in ('console','exec'):
        for _ in range(50):h.success(cmd,{'lines':1,'stacktrace':'none'} if cmd=='console' else {'code':'return 1;'})
    h.probe('reset')
    with cf.ThreadPoolExecutor(max_workers=8) as pool:
        for batch in range(30):
            replies=list(pool.map(lambda i:h.probe(delay=2,id=f'{batch}-{i}'),range(8)))
            assert all(r['enteredThread']==r['resumedThread']==r['GetterThread']==1 for r in replies)
    state=h.probe('state');assert state['maximum']==1 and state['entered']==state['exited']==240 and state['active']==0,state
    assert all(state['events'][i].replace('enter:','exit:',1)==state['events'][i+1] for i in range(0,480,2))
    h.save('load-state',state);return {'sequential':100,'parallel':240,'batches':30,'maximum':1,'log':audit(start,'load')}

def readiness():
    select('candidate');invariants('candidate');identify();import review
    review.readiness();return json.loads((h.OUT/'readiness.json').read_text())

def retired():
    select('candidate');invariants('candidate');identify();start=LOG.stat().st_size;import review
    review.retirement();return {'evidence':json.loads((h.OUT/'forced-retirement.json').read_text()),'log':audit(start,'retirement',expected_timeout=True)}

def screen():
    select('candidate');invariants('candidate');identify()
    windows=h.success('exec',{'code':'return Resources.FindObjectsOfTypeAll<EditorWindow>().Count(w=>w.GetType().Name=="FrameDebuggerWindow");'})
    if windows==0:return {'decision':'BLOCKED','reason':'No existing Frame Debugger window. Opening/showing/focusing one is forbidden; orchestrator must arrange an existing window or explicit exemption. No screenshot/dump attempted.','screenshot':0,'dump':0}
    counts_path=h.OUT/'screen-counts.json'
    assert not (h.OUT/'active-screen-attempted').exists(),'Screen stage already attempted; do not retry'
    (h.OUT/'active-screen-attempted').write_text('max one screenshot and one dump for this plan')
    counts=json.loads(counts_path.read_text()) if counts_path.exists() else {'screenshot':0,'dump':0}
    assert counts['dump']==0 and counts['screenshot']==0,'No remaining one-shot permission'
    process([sys.executable,str(ROOT/'tests/framedebug/active-check.py'),'active',str(h.OUT)],timeout=240)
    result=json.loads((h.OUT/'active-frame.json').read_text());counts=json.loads(counts_path.read_text())
    assert counts=={'screenshot':1,'dump':1},counts
    return {'counts':counts,'before':{k:result['before'][k] for k in ('enabled','limit','other')},'after':{k:result['after'][k] for k in ('enabled','limit','other')},'summary':result['summary'],'final':result['final']['other']}

def final():
    select('candidate');invariants();identify()
    # ALWAYS leave candidate deployed on a successful finish, never restore baseline.
    for name in RUNTIME:(DEST/name).write_bytes((BASE/'candidate-editor'/name).read_bytes())
    if FIXTURE.exists():assert '_http_thread_probe' in FIXTURE.read_text();FIXTURE.unlink()
    FIXTURE.with_suffix('.cs.meta').unlink(missing_ok=True)
    compile_editor();invariants('candidate')
    schemas=h.success('list');assert len(schemas)==15 and all(t['name']!='_http_thread_probe' for t in schemas)
    state=h.success('exec',{'code':'return new Dictionary<string, object> { ["playing"] = EditorApplication.isPlaying, ["paused"] = EditorApplication.isPaused, ["frameWindows"] = Resources.FindObjectsOfTypeAll<EditorWindow>().Count(w=>w.GetType().Name=="FrameDebuggerWindow") };'})
    assert state['playing'] is False and state['paused'] is False,state
    screen_path=ART/'candidate/active-frame.json'
    if screen_path.exists():assert state['frameWindows']==json.loads(screen_path.read_text())['initial']['other']['windows'],state
    return {'candidate_left_deployed':True,'normal_tools':15,'state':state,'git':invariants('candidate'),'installed_cli_unchanged':True}

def git_final():
    select('candidate');identify();git=invariants('candidate')
    assert not FIXTURE.exists() and not FIXTURE.with_suffix('.cs.meta').exists(),'Temporary fixture remains'
    assert not git['untracked'],'Unexpected ProjectD untracked files'
    return {'project_git':git,'allowed_user_paths':list(MAN['protected']),'candidate_runtime_paths':['tools/unity-cli/unity-connector/Editor/'+n for n in RUNTIME],
            'root_git':process(['git','-C',str(ROOT),'status','--short']).splitlines(),'root_branch':process(['git','-C',str(ROOT),'branch','--show-current']).strip(),
            'project_branch':process(['git','-C',str(PROJECT),'branch','--show-current']).strip(),'protected_hashes_unchanged':True,'installed_cli_unchanged':True,'candidate_retained':True}


def rollback():
    # Artifacts/patches never overwritten. Restore ONLY our runtime and own tests;
    # do NOT use git reset, stash, checkout, clean or broad project restore.
    select('failure');h.CLI=NEW
    # Avoid overwriting newer runtime authoring if ownership changed mid-test.
    for repo,key in ((ROOT,'rootHead'),(PROJECT,'projectHead')):
        assert process(['git','-C',str(repo),'rev-parse','HEAD']).strip()==MAN[key],'HEAD changed externally; manual runtime restore required'
        assert process(['git','-C',str(repo),'branch','--show-current']).strip()=='main','Branch changed externally; manual runtime restore required'
    try:h.cli('editor','stop',timeout=90,check=False)
    except Exception:pass
    for name in RUNTIME:(DEST/name).write_bytes((BASE/'committed-editor'/name).read_bytes())
    if FIXTURE.exists() and '_http_thread_probe' in FIXTURE.read_text():
        FIXTURE.unlink();FIXTURE.with_suffix('.cs.meta').unlink(missing_ok=True)
    process(['git','-C',str(ROOT),'restore','--',*['unity-connector/Editor/'+n for n in RUNTIME],'cmd/status.go','internal/client/client.go'])
    for name in ('cmd/readiness_test.go','internal/client/response_policy_test.go'):(ROOT/name).unlink(missing_ok=True)
    try:compile_editor()
    except Exception as ex:return {'source_restored':True,'compile_unconfirmed':str(ex),'patches_preserved':True}
    return {'source_restored':True,'compile_confirmed':True,'patches_preserved':True}

ACTIONS={
 'baseline-setup':lambda:(select('baseline'),switch('baseline'))[1],
 'baseline-responses':lambda:response_phase('baseline'),
 'baseline-reloads':lambda:reloads('baseline'),
 'baseline-cancel':lambda:cancellations('baseline'),
 'candidate-setup':lambda:(select('candidate'),switch('candidate'))[1],
 'candidate-responses':lambda:response_phase('candidate'),
 'compare-responses':compare_responses,
 'candidate-reloads':lambda:reloads('candidate'),
 'compare-footprints':footprint_comparison,
 'candidate-cancel':lambda:cancellations('candidate'),'client-policy':client_policy,
 'load':load,'readiness':readiness,'transitions':transitions,'retirement':retired,'screen':screen,'final':final,'git-final':git_final,'rollback':rollback,
}

if __name__=='__main__':
    ART.mkdir(parents=True,exist_ok=True)
    try:
        result=ACTIONS[PHASE]()
        (ART/(PHASE+'-summary.json')).write_text(json.dumps(result,indent=2,ensure_ascii=False),encoding='utf-8')
        if isinstance(result,dict) and result.get('decision')=='FAIL':raise AssertionError('Control-adjusted footprint failure')
        print(json.dumps({'phase':PHASE,'status':result.get('decision','PASS') if isinstance(result,dict) else 'PASS','summary':str(ART/(PHASE+'-summary.json'))},ensure_ascii=False))
    except Exception as ex:
        error={'phase':PHASE,'error':str(ex),'traceback':traceback.format_exc()}
        try:error['rollback']=rollback()
        except Exception as cleanup:error['rollback_error']=str(cleanup)
        (ART/(PHASE+'-failure.json')).write_text(json.dumps(error,indent=2,ensure_ascii=False),encoding='utf-8')
        print(json.dumps({'phase':PHASE,'status':'FAIL','error':str(ex),'details':str(ART/(PHASE+'-failure.json'))},ensure_ascii=False));sys.exit(1)
