"""One approved active-state dump including window setup/restoration, no input."""
import json,sys,zlib
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'http'))
import check as h
h.ready();rows={};budget=h.OUT/'screen-counts.json'

def reserve(kind):
    counts=json.loads(budget.read_text()) if budget.exists() else {'screenshot':0,'dump':0}
    assert counts[kind]<1,counts
    counts[kind]+=1;budget.write_text(json.dumps(counts))

def state():
    r=h.success('framedebug',{'action':'status'})
    r['other']=h.success('exec',{'code':'''var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
return new Dictionary<string, object> { ["connection"] = t.GetMethod("GetRemotePlayerGUID",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,null), ["paused"] = EditorApplication.isPaused, ["playing"] = EditorApplication.isPlaying, ["windows"] = Resources.FindObjectsOfTypeAll<EditorWindow>().Count(w=>w.GetType().Name=="FrameDebuggerWindow") };'''})
    return r

initial=state();rows['initial']=initial
assert initial['other']['windows']>0 and not initial['other']['playing'], 'BLOCKED: an existing Frame Debugger window is required; never open/show/focus a new window' 
png=h.OUT/'active.png';dump=h.OUT/'active-dump.json'
try:
    reserve('screenshot');capture=h.request('screenshot',{'view':'game','output_path':str(png)})
    assert capture['json']['success'],capture
    data=png.read_bytes();assert data[:8]==b'\x89PNG\r\n\x1a\n' and data[-12:]==b'\0\0\0\0IEND\xaeB`\x82';rows['png_bytes']=len(data);png.unlink()
    h.cli('editor','play');h.ready('playing')
    h.success('framedebug',{'action':'enable'})
    value=h.success('exec',{'code':'''var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
var p=t.GetProperty("limit",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);p.SetValue(null,3);return (int)p.GetValue(null);'''})
    assert value==3
    rows['before']=state();assert rows['before']['enabled'] and rows['before']['limit']==3
    reserve('dump');rows['dump']=h.request('framedebug',{'action':'dump','output':str(dump),'max_events':10,'capture_timeout':60},timeout=90)
    assert rows['dump']['json']['success'],rows['dump']
    payload=json.loads(dump.read_text(encoding='utf-8-sig'));assert payload['complete'] and payload['summary']['failedEvents']==0
    rows['summary']=payload['summary'];rows['after']=state()
    for key in ('enabled','limit'):assert rows['before'][key]==rows['after'][key],rows
    for key in ('connection','paused','windows'):assert rows['before']['other'][key]==rows['after']['other'][key],rows
finally:
    h.success('framedebug',{'action':'disable'})
    h.cli('editor','stop',check=False);h.ready()
    h.success('exec',{'code':f'''var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
t.GetProperty("limit",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).SetValue(null,{initial['limit']});EditorApplication.isPaused={str(initial['other']['paused']).lower()};return true;'''})
    if initial['enabled']: h.success('framedebug',{'action':'enable'})
    rows['final']=state();assert rows['final']['other']['playing'] is False
    assert rows['final']['enabled']==initial['enabled'] and rows['final']['limit']==initial['limit']
    assert rows['final']['other']['windows']==initial['other']['windows']
    png.unlink(missing_ok=True);dump.unlink(missing_ok=True);h.save('active-frame',rows)
print(json.dumps({'before':{k:rows['before'][k] for k in ('enabled','limit','other')},'after':{k:rows['after'][k] for k in ('enabled','limit','other')},'summary':rows['summary'],'counts':json.loads(budget.read_text())}))
