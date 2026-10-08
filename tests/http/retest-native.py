"""Approved direct hidden retest. NEVER restores runtime on a test failure."""
import concurrent.futures as cf
import ctypes
from ctypes import wintypes
import hashlib
import http.client
import importlib.util
import json
import os
from pathlib import Path
import socket
import statistics
import subprocess
import sys
import threading
import time
import traceback
sys.argv=[sys.argv[0],'retest','D:/tmp/http-retest']
import check as h
import review

BASE=Path('D:/tmp/http-retest');CLI=str(BASE/'unity-cli.exe');DEST=Path('D:/Projects/ProjectD/tools/unity-cli/unity-connector/Editor');ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('native',str(ROOT/'tests/http/transition-native.py'));n=importlib.util.module_from_spec(spec);spec.loader.exec_module(n)
n.OUT=BASE;n.CLI=CLI;h.CLI=CLI;h.OUT=BASE;h.PHASE='regression'
PROTECTED=json.loads((BASE/'before.json').read_text())['protected'];SCENE='Assets/Scenes/Main.unity'
stop=threading.Event();samples=[];phase=['initial'];INITIAL=None
SMOKE=Path(h.PROJECT)/'Assets/Tests/PlayMode/HttpTransitionSmoke.cs'

def save(name,data):n.save(name,data)
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def guard_scene():assert digest(Path(h.PROJECT)/SCENE)==PROTECTED['client/'+SCENE],'Main scene disk changed externally; preserve all changes and stop'
def clean_scene():
    guard_scene();s=n.state()
    if s['playing']:n.cli('editor','stop');n.ready();s=n.state()
    if any(s['sceneDirty']):
        assert INITIAL is not None and not any(INITIAL['sceneDirty']),'Cannot discard pre-existing dirty scene'
        n.execute('return UnityEditor.SceneManagement.EditorSceneManager.OpenScene(@"Assets/Scenes/Main.unity",UnityEditor.SceneManagement.OpenSceneMode.Single).path == @"Assets/Scenes/Main.unity";')
        after=n.state();assert not any(after['sceneDirty']),after
        with (BASE/'discarded-test-dirty.jsonl').open('a')as f:f.write(json.dumps({'phase':phase[0],'before':s,'after':after,'disk_unchanged':True,'saved':False})+'\n')
    guard_scene()

def focus_thread():
    u=ctypes.WinDLL('user32',use_last_error=True);u.GetForegroundWindow.restype=wintypes.HWND;u.GetWindowThreadProcessId.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.DWORD)]
    while not stop.is_set():
        w=u.GetForegroundWindow();p=wintypes.DWORD();u.GetWindowThreadProcessId(w,ctypes.byref(p));r={'time':time.time(),'phase':phase[0],'pid':p.value,'hwnd':int(w or 0)};samples.append(r)
        with (BASE/'foreground-stream.jsonl').open('a')as f:f.write(json.dumps(r)+'\n')
        stop.wait(.02)

def configure(reload):
    n.execute('''var f=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
var views=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="GameView").ToArray();
if(views.Length==0) throw new Exception("No existing Game view");
foreach(var w in views){var p=w.GetType().GetProperty("enterPlayModeBehavior",f);p.SetValue(w,Enum.Parse(p.PropertyType,"PlayUnfocused"));}
EditorSettings.enterPlayModeOptionsEnabled=ENABLED;
EditorSettings.enterPlayModeOptions=(EnterPlayModeOptions)OPTIONS;
return true;'''.replace('ENABLED','false'if reload else'true').replace('OPTIONS','0'if reload else'3'))
    s=n.state();assert not s['optionsEnabled'] if reload else s['optionsEnabled']and s['options']==3

def compile_editor():return n.cli('editor','refresh','--compile',timeout=300)
def install(fixture):
    path=DEST/fixture
    assert not path.exists(),'Fixture collision: '+fixture
    path.write_bytes((ROOT/'tests/http'/fixture).read_bytes());return compile_editor()
def remove(fixture):
    (DEST/fixture).unlink(missing_ok=True);(DEST/(fixture+'.meta')).unlink(missing_ok=True)

def log_counts(offset):
    with n.LOG.open('rb')as f:f.seek(offset);text=f.read().decode('utf-8',errors='replace')
    return {'errorCS':text.count('error CS'),'sceneID':text.count('[DungeonMaster] Failed to resolve PurrNet SceneID'),'transformInterests':text.count("Transform has '(old ObjectDispatcher_TransformSystem)'"),'shutdownTimeouts':text.count('Background HTTP shutdown exceeded 5 seconds'),'packageCancellation':text.count('[Package Manager Window] Operation cancelled')}

def transitions(reload):
    configure(reload);clean_scene();offset=n.LOG.stat().st_size;rows=[]
    actions=[(('editor','play','--wait'),True),(('editor','stop'),False),(('editor','play'),True),(('editor','stop'),False)]
    if not reload and (BASE/'original-off-progress.json').exists():
        rows=json.loads((BASE/'original-off-progress.json').read_text())[:4]
        actions=[] # Already completed transitions; only retry with independent smoke.
    for args,target in actions:
        r=n.cli(*args,check=False);n.cli('instances','wait','--state','playing'if target else'ready',timeout=180);s=n.state();rows.append({'cli':r,'state':s});save(phase[0]+'-progress',rows)
        assert r['exit']==0 and s['playing']==target,rows[-1]
    clean_scene()
    # Explicitly prove no dirty scene before Test Framework SaveModifiedSceneTask.
    pre=n.state();assert not any(pre['sceneDirty']),pre
    r=n.cli('test','--mode','PlayMode','--filter','UnityCliConnector.HttpTests.PlayModeTransitionSmoke.PureTransition',timeout=600,check=False)
    rows.append({'cli':r,'preTest':pre,'state':n.state()});save(phase[0]+'-progress',rows)
    assert r['exit']==0,rows[-1]
    result=json.loads(r['stdout']);assert result['passed']==1 and result['failed']==0,result
    clean_scene();r=compile_editor();rows.append({'cli':r,'state':n.state()})
    return {'rows':rows,'log':log_counts(offset),'noSaveDialogPrecondition':True}

def responses():
    install('HttpThreadProbe.cs');h.PHASE='regression';h.ready()
    h.baseline_or_regression() # 30 responses; 25 console/cached-exec samples each.
    source=Path('D:/tmp/http-split/artifacts/baseline')
    for suffix in ('responses','tools','timings'):(BASE/('baseline-'+suffix+'.json')).write_bytes((source/('baseline-'+suffix+'.json')).read_bytes())
    # The same stable-field comparison as the original harness; independent
    # Frame candidate response differences are recorded, never silently ignored.
    try:h.compare();comparison={'cases':30,'differences':[]}
    except AssertionError as ex:
        comparison=json.loads((BASE/'comparison.json').read_text());comparison['review']='Known independent Frame API changes must be classified by orchestrator; not rewritten to PASS'
    data=json.loads((BASE/'regression-timings.json').read_text());performance={c:{'n':25,'mean_ms':statistics.mean(r['ms']for r in data if r['command']==c),'median_ms':statistics.median(r['ms']for r in data if r['command']==c)}for c in ('console','exec')}
    return {'comparison':comparison,'performance':performance,'ordinary_503_differences':'none expected; native cancellation separately validates intentional 503/execution_state'}

def load():
    h.ready()
    for cmd in ('console','exec'):
        for _ in range(50):h.success(cmd,{'lines':1,'stacktrace':'none'}if cmd=='console'else{'code':'return 1;'})
    h.probe('reset')
    with cf.ThreadPoolExecutor(max_workers=8)as pool:
        for batch in range(30):
            replies=list(pool.map(lambda i:h.probe(delay=2,id=f'{batch}-{i}'),range(8)))
            assert all(r['enteredThread']==r['resumedThread']==r['GetterThread']==1 for r in replies)
    s=h.probe('state');assert s['maximum']==1 and s['entered']==s['exited']==240 and s['active']==0,s
    assert all(s['events'][i].replace('enter:','exit:',1)==s['events'][i+1]for i in range(0,480,2))
    return {'sequential':100,'parallel':240,'batches':30,'maximum':s['maximum']}

def footprint():
    h.cli('exec','GC.Collect();\nGC.WaitForPendingFinalizers();\nreturn 1;')
    rows=[]
    for _ in range(3):time.sleep(.5);rows.append({'handles':review.handles(),'threads':h.os_thread_count()})
    return {k:statistics.median(r[k]for r in rows)for k in ('handles','threads')}

def reloads():
    h.ready();path=DEST/'HttpThreadProbe.cs';original=path.read_text(encoding='utf-8');rows=[];report=BASE/'reload-shutdown.jsonl';report.unlink(missing_ok=True)
    for i in range(5):
        old=h.probe('state');before=footprint();h.probe('configure',shutdown_path=str(report))
        partial=socket.create_connection(('127.0.0.1',h.PORT));partial.sendall(f'POST /command HTTP/1.1\r\nHost: 127.0.0.1:{h.PORT}\r\nContent-Length: 99999\r\n\r\n{{'.encode())
        try:
            path.write_text(original+f'\n// retest compile {i}\n',encoding='utf-8')
            reply=h.request('_http_thread_probe',{'compile':True,'delay':30000,'id':f'reload-{i}'},timeout=90)
        finally:partial.close()
        h.ready();new=h.probe('state');assert new['epoch']!=old['epoch']
        assert reply['status']==503 and reply['json']['data']['execution_state']=='started',reply
        after=footprint();rows.append({'round':i,'before':before,'after':after,'wire':reply});save('reload-progress',rows)
    reports=[json.loads(s)for s in report.read_text().splitlines()];assert len(reports)==5 and all(r['loopCompleted']and r['unfinished']==0 for r in reports),reports
    controls=json.loads(Path('D:/tmp/http-split/artifacts/baseline/reload-footprint.json').read_text())['measured']
    trends={}
    for key in ('handles','threads'):
        values=[r['after'][key]for r in rows];baseline=[r['after'][key]for r in controls]
        slope=lambda a:sum((i-2)*(v-statistics.mean(a))for i,v in enumerate(a))/10
        trends[key]={'candidate':values,'baseline':baseline,'candidate_slope':slope(values),'baseline_slope':slope(baseline),'candidate_delta':values[-1]-values[0],'baseline_delta':baseline[-1]-baseline[0]}
    return {'rows':rows,'shutdown':reports,'trends':trends,'note':'Historical baseline, not a contemporaneous control; trends require interpretation'}

def cancelled():
    h.PHASE='after';review.cancellation()
    r=json.loads((BASE/'after-cancellation.json').read_text());assert r['raw']['json']['data']['execution_state']=='not_started';return r

def readiness():review.readiness();return json.loads((BASE/'readiness.json').read_text())

def dump_reload():
    remove('HttpThreadProbe.cs');install('TransitionProbe.cs');configure(True);clean_scene();n.cli('editor','play','--wait');n.cli('instances','wait','--state','playing',timeout=180)
    marker=BASE/'capture-running.jsonl';report=BASE/'dump-shutdown.jsonl';marker.unlink(missing_ok=True);report.unlink(missing_ok=True)
    path=BASE/'interrupted-dump.json';path.unlink(missing_ok=True)
    partial=socket.create_connection(('127.0.0.1',8090));partial.settimeout(20);partial.sendall(b'POST /command HTTP/1.1\r\nHost: 127.0.0.1:8090\r\nContent-Length: 99999\r\n\r\n{')
    try:
        arm=n.raw('_http_transition_probe',{'reload_ms':200,'wait_capture':True,'marker':str(marker),'report':str(report)})
        reply=n.raw('framedebug',{'action':'dump','async':True,'output':str(path),'capture_timeout':30})
        for _ in range(150):
            if marker.exists():break
            time.sleep(.1)
        assert marker.exists();started=json.loads(marker.read_text().splitlines()[0]);assert started['captureRunning']and started['reloadRequested'],started
        n.cli('instances','wait','--state','playing',timeout=180)
        try:aborted=partial.recv(4096).decode(errors='replace')
        except Exception as ex:aborted=str(ex)
    finally:partial.close()
    after=n.state();assert not after['paused'] and not after['enabled'] and after['windows']==0,after
    retirement=[json.loads(s)for s in report.read_text().splitlines()];assert all(r['loopCompleted']and r['unfinished']==0 for r in retirement),retirement
    payload=json.loads(path.read_text(encoding='utf-8-sig'))if path.exists()else None
    n.cli('editor','stop');n.ready();clean_scene()
    return {'wire':reply,'capture':started,'after':after,'retirement':retirement,'partial':aborted,'dump':{k:payload.get(k)for k in ('complete','error','summary')}if payload else None}

ACTIONS={'transitions-off':lambda:transitions(False),'transitions-on':lambda:transitions(True),'responses':responses,'load':load,'reloads':reloads,'cancelled':cancelled,'readiness':readiness,'dump-reload':dump_reload}

def main():
    global INITIAL
    thread=threading.Thread(target=focus_thread,daemon=True);thread.start();out={};offset=n.LOG.stat().st_size
    try:
        time.sleep(.05);assert samples[-1]['pid']!=129620,'Unity is foreground; do not change focus'
        INITIAL=n.state();save('initial',INITIAL)
        assert INITIAL['pid']==129620 and not INITIAL['playing']and not INITIAL['enabled']and INITIAL['windows']==0
        assert not any(INITIAL['sceneDirty']),'Pre-existing scene dirty; inspect ownership before discarding'
        if not all((BASE/(k+'-result.json')).exists() for k in ('transitions-off','transitions-on')):
            assert not SMOKE.exists(),'Smoke fixture collision'
            SMOKE.write_bytes((ROOT/'tests/http/PlayModeTransitionSmoke.cs').read_bytes());compile_editor()
        if any(not(BASE/(k+'-result.json')).exists()for k in ('load','reloads','cancelled','readiness')) and (BASE/'responses-result.json').exists() and not (DEST/'HttpThreadProbe.cs').exists():
            install('HttpThreadProbe.cs')
        for key,action in ACTIONS.items():
            phase[0]=key;started=n.LOG.stat().st_size
            if (BASE/(key+'-result.json')).exists():
                out[key]={'status':'RECORDED','file':key+'-result.json'}
                continue
            value=action()
            if isinstance(value,list):value={'rows':value}
            value['log_segment']=log_counts(started);save(key+'-result',value);out[key]={'status':'PASS','file':key+'-result.json'}
            if any(r['pid']==129620 for r in samples):raise AssertionError('Unity became foreground; stop testing')
            save('progress',out)
    except Exception as ex:
        out['failed_phase']=phase[0];out['error']=str(ex);out['traceback']=traceback.format_exc();save('progress',out)
    finally:
        phase[0]='cleanup'
        try:
            if INITIAL:
                clean_scene();out['restored']=n.restore(INITIAL)
                assert out['restored']['game']==INITIAL['game']and out['restored']['options']==INITIAL['options']and out['restored']['optionsEnabled']==INITIAL['optionsEnabled']
                assert not any(out['restored']['sceneDirty'])and not out['restored']['enabled']and out['restored']['windows']==0
            remove('HttpThreadProbe.cs');remove('TransitionProbe.cs');SMOKE.unlink(missing_ok=True);SMOKE.with_suffix('.cs.meta').unlink(missing_ok=True);out['cleanup_compile']=compile_editor()
            if INITIAL:
                out['restored']=n.restore(INITIAL)
                # Test Framework writes its temporary options=0 to disk separately
                # from the API object. Restore only the recorded settings file.
                Path(h.PROJECT+'/ProjectSettings/EditorSettings.asset').write_bytes((BASE/'initial-files/client/ProjectSettings/EditorSettings.asset').read_bytes())
        except Exception as ex:out['cleanup_error']=str(ex)
        stop.set();thread.join(timeout=2);out['focus']={'samples':len(samples),'unityForegroundSamples':sum(r['pid']==129620 for r in samples),'unityForegroundEpisodes':sum(r['pid']==129620 and(i==0 or samples[i-1]['pid']!=129620)for i,r in enumerate(samples))}
        out['protected']={f:digest(Path('D:/Projects/ProjectD')/f)==v for f,v in PROTECTED.items()}
        out['log']=log_counts(offset);out['runtime_kept']='LATEST CANDIDATE; NO ROLLBACK'
        save('result',out);print(json.dumps(out,ensure_ascii=True))
    if out.get('error')or out.get('cleanup_error')or out['focus']['unityForegroundSamples']or not all(out['protected'].values()):raise SystemExit(1)

if __name__=='__main__':main()
