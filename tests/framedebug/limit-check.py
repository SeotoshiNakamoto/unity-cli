"""Bounded screen/limit comparison. Baseline must use committed runtime files.
Shared counters in the output directory cover all phases; reserve before a call.
No input/focus/window calls except screenshot and dump tools under user approval.
"""
import json
from pathlib import Path
import sys
import zlib

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'http'))
import check as h
LABEL=h.PHASE


def reserve(kind):
    path=h.OUT/'screen-counts.json'
    counts=json.loads(path.read_text()) if path.exists() else {'screenshot':0,'dump':0}
    assert counts[kind]<3, f'{kind} budget exhausted: {counts}'
    counts[kind]+=1;path.write_text(json.dumps(counts),encoding='utf-8')
    return counts


def state():
    frame=h.success('framedebug',{'action':'status'})
    editor=h.success('exec',{'code':'return new Dictionary<string, object> { ["playing"] = EditorApplication.isPlaying, ["paused"] = EditorApplication.isPaused, ["frameWindows"] = Resources.FindObjectsOfTypeAll<EditorWindow>().Count(w => w.GetType().Name == "FrameDebuggerWindow") };'})
    return {'frame':frame,'editor':editor}


rows={};h.ready();rows['edit_initial']=state()
png=h.OUT/(LABEL+'.png');dump=h.OUT/(LABEL+'-dump.json')
try:
    reserve('screenshot')
    rows['screenshot']=h.request('screenshot',{'view':'game','output_path':str(png)})
    h.save(LABEL+'-limit',rows)
    assert rows['screenshot']['json']['success'],rows['screenshot']
    data=png.read_bytes();assert data[:8]==b'\x89PNG\r\n\x1a\n'
    off=8;chunks=[]
    while off<len(data):
        length=int.from_bytes(data[off:off+4],'big');kind=data[off+4:off+8];payload=data[off+8:off+8+length]
        assert zlib.crc32(kind+payload)==int.from_bytes(data[off+8+length:off+12+length],'big')
        chunks.append(kind.decode());off+=length+12
    assert off==len(data) and chunks[-1]=='IEND' and 'IDAT' in chunks
    rows['png']={'bytes':len(data),'chunks':chunks};png.unlink()
    rows['edit_after_screenshot']=state()
    rows['play']=h.cli('editor','play',check=False);h.ready('playing')
    rows['play_initial']=state()
    assert rows['play_initial']['editor']['playing'] and not rows['play_initial']['frame']['enabled']
    # Set only a disabled native selector, no window/enable/limit-change GUI calls.
    # Record whether the actual native setter accepts -1 or normalizes it to 0.
    rows['negative_setter']=h.success('exec',{'code':'''var t = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
var p = t.GetProperty("limit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
var prior = (int)p.GetValue(null);
p.SetValue(null, -1);
return new Dictionary<string, object> { ["prior"] = prior, ["requested"] = -1, ["actual"] = (int)p.GetValue(null), ["enabled"] = (bool)typeof(UnityEngine.Object).Assembly.GetType("UnityEngine.FrameDebugger").GetProperty("enabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null) };'''})
    rows['before_dump']=state();h.save(LABEL+'-limit',rows)
    reserve('dump')
    rows['dump']=h.request('framedebug',{'action':'dump','output':str(dump),'max_events':10,'capture_timeout':60},timeout=90)
    h.save(LABEL+'-limit',rows)
    assert rows['dump']['json']['success'],rows['dump']
    payload=json.loads(dump.read_text(encoding='utf-8-sig'))
    assert payload['complete'] and payload['events'] and payload['summary']['failedEvents']==0
    rows['summary']=payload['summary']
    rows['after_dump']=state()
    assert rows['before_dump']['frame']['enabled']==rows['after_dump']['frame']['enabled']
    assert rows['before_dump']['editor']['paused']==rows['after_dump']['editor']['paused']
    assert rows['before_dump']['editor']['frameWindows']==rows['after_dump']['editor']['frameWindows']
    rows['limit_equal']=rows['before_dump']['frame']['limit']==rows['after_dump']['frame']['limit']
    # A baseline mismatch is diagnostic, not a transport failure. Candidate must
    # restore the accepted native value exactly before publishing the response.
    if LABEL!='baseline_limit':assert rows['limit_equal'],rows
finally:
    if 'negative_setter' in rows:
        prior=rows['negative_setter']['prior']
        rows['restore_setup']=h.success('exec',{'code':f'''var t = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
var p = t.GetProperty("limit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
p.SetValue(null, {prior});
return (int)p.GetValue(null);'''})
        assert rows['restore_setup']==prior
    rows['stop']=h.cli('editor','stop',check=False);h.ready()
    rows['edit_final']=state()
    assert not rows['edit_final']['editor']['playing']
    png.unlink(missing_ok=True);dump.unlink(missing_ok=True);h.save(LABEL+'-limit',rows)
print(json.dumps({'phase':LABEL,'limit_equal':rows.get('limit_equal'),'native_setter':rows.get('negative_setter'),'summary':rows.get('summary'),'screen_counts':json.loads((h.OUT/'screen-counts.json').read_text())},ensure_ascii=False))
