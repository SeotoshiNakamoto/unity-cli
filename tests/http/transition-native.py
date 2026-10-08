"""Hidden, main-only reload validation. No Show/Focus/input/scene saves."""
import ctypes
from ctypes import wintypes
import concurrent.futures as cf
import hashlib
import http.client
import json
from pathlib import Path
import shutil
import subprocess
import sys
import threading
import time
import traceback

OUT=Path('D:/tmp/http-transition');CLI=str(OUT/'unity-cli.exe');PROJECT='D:/Projects/ProjectD/client';PID=129620;PORT=8090
DEST=Path('D:/Projects/ProjectD/tools/unity-cli/unity-connector/Editor')
LOG=Path(PROJECT)/'Logs/Editor.log'
STATE='''return Newtonsoft.Json.JsonConvert.SerializeObject(new { playing=EditorApplication.isPlaying, paused=EditorApplication.isPaused,
compiling=EditorApplication.isCompiling, enabled=UnityEngine.FrameDebugger.enabled,
windows=Resources.FindObjectsOfTypeAll<EditorWindow>().Count(w=>w.GetType().Name=="FrameDebuggerWindow"),
pid=System.Diagnostics.Process.GetCurrentProcess().Id, optionsEnabled=EditorSettings.enterPlayModeOptionsEnabled, options=(int)EditorSettings.enterPlayModeOptions,
game=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="GameView").Select(w=>new { id=w.GetEntityId().ToString(), behavior=Convert.ToInt32(w.GetType().GetProperty("enterPlayModeBehavior",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(w)) }).ToArray(),
sceneDirty=Enumerable.Range(0,SceneManager.sceneCount).Select(i=>SceneManager.GetSceneAt(i).isDirty).ToArray() });'''

def cli(*args,timeout=180,check=True):
    t=time.monotonic();r=subprocess.run([CLI,'--project',PROJECT,'--timeout',str(timeout*1000),*args],capture_output=True,stdin=subprocess.DEVNULL,text=True,encoding='utf-8',errors='replace',timeout=timeout+15,creationflags=subprocess.CREATE_NO_WINDOW)
    row={'args':args,'exit':r.returncode,'stdout':r.stdout.strip(),'stderr':r.stderr.strip(),'ms':(time.monotonic()-t)*1000}
    if check and r.returncode:raise AssertionError(row)
    return row

def execute(code,**kwargs):return json.loads(cli('exec',code,**kwargs)['stdout'])
def state():return execute(STATE)
def save(name,data): (OUT/(name+'.json')).write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding='utf-8')
def ready():cli('instances','wait','--state','ready',timeout=180)
def raw(command,params=None,connection=None,timeout=30):
    c=connection or http.client.HTTPConnection('127.0.0.1',PORT,timeout=timeout)
    try:
        c.request('POST','/command',json.dumps({'command':command,'params':params or {}}),{'Content-Type':'application/json'})
        r=c.getresponse();data=r.read();return {'status':r.status,'bytes':len(data),'body':data.decode(errors='replace'),'json':json.loads(data) if data else None}
    finally:
        if connection is None:c.close()

def foreground(stop,rows,phase):
    u=ctypes.WinDLL('user32',use_last_error=True);u.GetForegroundWindow.restype=wintypes.HWND
    u.GetWindowThreadProcessId.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.DWORD)]
    while not stop.is_set():
        hwnd=u.GetForegroundWindow();p=wintypes.DWORD();u.GetWindowThreadProcessId(hwnd,ctypes.byref(p))
        row={'time':time.time(),'phase':phase[0],'hwnd':int(hwnd or 0),'pid':p.value};rows.append(row)
        with (OUT/'foreground-live.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(row)+'\n')
        stop.wait(.02)

def configure_game():
    return execute('''var f=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
var views=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="GameView").ToArray();
if(views.Length==0) throw new Exception("No existing Game view; never open one");
foreach(var w in views) { var p=w.GetType().GetProperty("enterPlayModeBehavior",f);p.SetValue(w,Enum.Parse(p.PropertyType,"PlayUnfocused")); }
EditorSettings.enterPlayModeOptionsEnabled=false;
return true;''')

def restore(initial):
    s=state()
    if s['playing']:cli('editor','stop');ready()
    # Disable through the native API; no window creation.
    execute('''var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
t.GetMethod("SetEnabled",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,new object[]{false,ProfilerDriver.connectedProfiler});
foreach(var w in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="FrameDebuggerWindow").ToArray()) UnityEngine.Object.DestroyImmediate(w);
EditorApplication.isPaused=false;
return true;''')
    settings='''EditorSettings.enterPlayModeOptionsEnabled=ENABLED;
EditorSettings.enterPlayModeOptions= (EnterPlayModeOptions)OPTIONS;
var f=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
var views=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="GameView").ToArray();
var ids=new string[]{IDS};var values=new int[]{VALUES};
if(views.Length!=values.Length) throw new Exception("Game view count changed");
for(int i=0;i<ids.Length;i++){ var w=views.Single(v=>v.GetEntityId().ToString()==ids[i]);var p=w.GetType().GetProperty("enterPlayModeBehavior",f);p.SetValue(w,Enum.ToObject(p.PropertyType,values[i])); }
return true;'''.replace('OPTIONS',str(initial['options'])).replace('ENABLED',str(initial['optionsEnabled']).lower()).replace('IDS',','.join(json.dumps(g['id']) for g in initial['game'])).replace('VALUES',','.join(str(g['behavior']) for g in initial['game']))
    execute(settings)
    return state()

def transitions(rows,dirty):
    actions=[(('editor','play','--wait'),True),(('editor','stop'),False),(('editor','play'),True),(('editor','stop'),False)]
    if len(sys.argv)>1 and sys.argv[1]=='resume':
        rows.extend(json.loads((OUT/'interrupted-transitions.json').read_text()))
        actions=actions[1:] # Saved successful play --wait must not be repeated.
    for args,target in actions:
        result=cli(*args,check=False);cli('instances','wait','--state','playing' if target else 'ready',timeout=180)
        after=state();rows.append({'result':result,'state':after});save('transitions-progress',rows)
        assert result['exit']==0 and after['playing']==target,rows[-1]
    if dirty:
        rows.append({'test':'PlayMode','status':'BLOCKED','reason':'Existing scene is dirty; Unity Test Framework SaveModifiedSceneTask would show a save dialog. No scene save/dialog allowed.'})
    else:
        result=cli('test','--mode','PlayMode','--filter','Spiral.Audio.Testing.AudioListenerDistanceCachePlayModeTests.DisabledPooledListenerIsReplacedByNewFloorListener',timeout=600,check=False)
        after=state();rows.append({'result':result,'state':after});save('transitions-progress',rows)
        assert result['exit']==0 and not after['playing'],rows[-1]
        payload=json.loads(result['stdout']);assert payload['passed']==1 and payload['failed']==0,payload
    result=cli('editor','refresh','--compile',timeout=300,check=False);after=state();rows.append({'result':result,'state':after});save('transitions-progress',rows)
    assert result['exit']==0 and not after['playing'] and not after['compiling'],rows[-1]

def keepalive():
    rows=[];marker=OUT/'keepalive-entered.txt';marker.unlink(missing_ok=True)
    for i in range(5):
        report=OUT/'io-shutdown.jsonl'
        connection=http.client.HTTPConnection('127.0.0.1',PORT,timeout=20)
        connection.request('GET','/health');r=connection.getresponse();assert r.status==200;r.read()
        arm=raw('_http_transition_probe',{'reload_ms':200,'report':str(report)});assert arm['json']['success'],arm
        time.sleep(.35)
        label=f'raw-{i}';code=f'File.AppendAllText(@"{marker}","{label}\\n");\nreturn "{label}";'
        try:reply=raw('exec',{'code':code},connection=connection)
        except (OSError,http.client.HTTPException) as ex:reply={'closed':type(ex).__name__,'message':str(ex)}
        finally:connection.close()
        ready()
        # Fresh CLI also performs a health->command keep-alive exchange. Record
        # its own handler marker and exit, without resending a lost request.
        label_cli=f'cli-{i}';result=cli('exec',f'File.AppendAllText(@"{marker}","{label_cli}\\n");\nreturn "{label_cli}";',check=False)
        entries=marker.read_text() if marker.exists() else ''
        row={'round':i,'wire':reply,'raw_entered':label+'\n' in entries,'cli':result,'cli_entered':label_cli+'\n' in entries}
        rows.append(row);save('keepalive-progress',rows)
        if result['exit']==0:assert row['cli_entered'],row
        if reply.get('status')==200 and reply.get('bytes',0)>0:assert row['raw_entered'],row
    return rows

def async_types():
    rows=[]
    for value in (True,False,'true','false',0,1,None,[],{}):
        reply=raw('manage_editor',{'action':'stop','async':value});rows.append({'async':value,'response':reply})
        # A normal job acknowledgment must not be inferred from a lost body.
    save('async-native',rows);return rows

def dump_reload():
    # Deployed dump-stability version enables natively without opening a window.
    # Assert this property from the deployed source, never use Show/OpenWindow.
    deployed=(DEST/'Tools/ManageFrameDebugger.cs').read_text()
    assert 'api.OpenWindow()' not in deployed,'BLOCKED: deployed Frame tool still opens an OS window'
    # Arm immediately before submission; validate that dump was actually running
    # at the reload boundary using the interrupted job/partial file, not guesswork.
    report=OUT/'dump-shutdown.jsonl';path=OUT/'reload-dump.json';path.unlink(missing_ok=True)
    arm=raw('_http_transition_probe',{'reload_ms':500,'report':str(report)});assert arm['json']['success']
    reply=raw('framedebug',{'action':'dump','async':True,'output':str(path),'capture_timeout':30})
    ready();after=state();job=None
    if reply.get('json',{}).get('success') and reply['json'].get('data',{}).get('job_id'):
        job=cli('job',reply['json']['data']['job_id'],timeout=20,check=False)
    payload=json.loads(path.read_text(encoding='utf-8-sig')) if path.exists() else None
    evidence={'wire':reply,'job_after_reload':job,'dump':{k:payload.get(k) for k in ('complete','error','summary')} if payload else None,'state':after}
    save('dump-reload',evidence)
    return evidence

def main():
    rows=[];samples=[];stop=threading.Event();phase=['initial'];monitor=threading.Thread(target=foreground,args=(stop,samples,phase),daemon=True);monitor.start()
    initial=None;result={};offset=LOG.stat().st_size
    try:
        time.sleep(.05);assert samples and samples[-1]['pid']!=PID,'Unity already foreground; do not move focus'
        initial=state();save('initial',initial)
        assert initial['pid']==PID and not initial['playing'] and not initial['enabled'] and initial['windows']==0,initial
        phase[0]='setup';configure_game();result['configured']=state();assert not result['configured']['optionsEnabled']
        fixture=DEST/'TransitionProbe.cs';assert not fixture.exists(),'Foreign fixture collision'
        fixture.write_bytes((Path(__file__).parent/'TransitionProbe.cs').read_bytes())
        cli('editor','refresh','--compile',timeout=300)
        phase[0]='transitions';transitions(rows,any(initial['sceneDirty']));result['transitions']=rows
        phase[0]='keepalive';result['keepalive']=keepalive()
        phase[0]='async-types';result['async']=async_types()
        phase[0]='dump-reload';result['dump']=dump_reload()
    except Exception as ex:
        result['error']=str(ex);result['traceback']=traceback.format_exc();result['transitions']=rows
    finally:
        phase[0]='cleanup'
        if initial:
            try:
                result['restored']=restore(initial)
                assert result['restored']['options']==initial['options'] and result['restored']['optionsEnabled']==initial['optionsEnabled']
                assert result['restored']['game']==initial['game'] and result['restored']['sceneDirty']==initial['sceneDirty']
                assert not result['restored']['playing'] and not result['restored']['enabled'] and result['restored']['windows']==0
            except Exception as ex:result['cleanup_error']=str(ex)
        fixture=DEST/'TransitionProbe.cs'
        if fixture.exists():fixture.unlink();fixture.with_suffix('.cs.meta').unlink(missing_ok=True)
        try:result['final_compile']=cli('editor','refresh','--compile',timeout=300)
        except Exception as ex:result['final_compile_error']=str(ex)
        phase[0]='end';stop.set();monitor.join(timeout=3)
        foreground_rows=[r for r in samples if r['pid']==PID]
        result['focus']={'samples':len(samples),'unityForegroundSamples':len(foreground_rows),'unityForegroundEpisodes':sum(r['pid']==PID and (i==0 or samples[i-1]['pid']!=PID) for i,r in enumerate(samples))}
        save('foreground',samples)
        with LOG.open('rb') as f:f.seek(offset);tail=f.read().decode(errors='replace')
        (OUT/'native.log').write_text(tail,encoding='utf-8')
        result['log']={'errorCS':[s for s in tail.splitlines() if 'error CS' in s],'lifecycle':[s for s in tail.splitlines() if s.startswith(('ObjectDisposedException:','NullReferenceException:','SynchronizationLockException:','AggregateException:'))],'shutdownTimeouts':tail.count('Background HTTP shutdown exceeded 5 seconds'),'packageCancellations':tail.count('[Package Manager Window] Operation cancelled')}
        before=json.loads((OUT/'before.json').read_text());result['protected']={p:hashlib.sha256((Path('D:/Projects/ProjectD')/p).read_bytes()).hexdigest()==digest for p,digest in before['protected'].items()}
        save('native-result',result)
        print(json.dumps({k:result.get(k) for k in ('error','cleanup_error','final_compile_error','focus','log','protected')}))
    if result.get('error') or result.get('cleanup_error') or result.get('final_compile_error') or result['focus']['unityForegroundSamples'] or not all(result['protected'].values()) or result['log']['errorCS'] or result['log']['lifecycle']:sys.exit(1)

if __name__=='__main__':main()
