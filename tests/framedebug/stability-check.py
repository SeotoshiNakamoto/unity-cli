"""Main-project capture checks; hidden CLI only, no focus/input/scene saves."""
import ctypes
from ctypes import wintypes
import json
from pathlib import Path
import subprocess
import sys
import time
import threading

PROJECT = 'D:/Projects/ProjectD/client'
CLI = 'D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe'
OUT = Path('D:/tmp/framedebug-stability')
OUT.mkdir(parents=True, exist_ok=True)
STATE = '''var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
var f=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
return Newtonsoft.Json.JsonConvert.SerializeObject(new { enabled=UnityEngine.FrameDebugger.enabled,
limit=(int)t.GetProperty("limit",f).GetValue(null), hash=(int)t.GetProperty("eventsHash",f).GetValue(null),
count=(int)t.GetProperty("count",f).GetValue(null), paused=EditorApplication.isPaused,
pid=System.Diagnostics.Process.GetCurrentProcess().Id,
windowReady=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="FrameDebuggerWindow").All(w=>!(bool)(w.GetType().GetProperty("IsEnablingFrameDebugger",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(w)??false)),
playing=EditorApplication.isPlaying, windows=Resources.FindObjectsOfTypeAll<EditorWindow>().Count(w=>w.GetType().Name=="FrameDebuggerWindow"),
sceneDirty=Enumerable.Range(0,SceneManager.sceneCount).Select(i=>SceneManager.GetSceneAt(i).isDirty).ToArray() });'''


def call(*args, timeout=180, check=True):
    p = subprocess.run([CLI, '--project', PROJECT, '--timeout', str(timeout * 1000), *args],
                       stdin=subprocess.DEVNULL, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', timeout=timeout + 10,
                       creationflags=subprocess.CREATE_NO_WINDOW)
    row = {'exit': p.returncode, 'stdout': p.stdout.strip(), 'stderr': p.stderr.strip()}
    if check and p.returncode:
        raise AssertionError(row)
    return row


def exec_code(code):
    return json.loads(call('exec', code)['stdout'])


def state():
    return exec_code(STATE)


def set_pause(value):
    return exec_code('EditorApplication.isPaused=' + str(value).lower() + ';\nreturn EditorApplication.isPaused;')


def set_limit(value):
    return exec_code('var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");\nt.GetProperty("limit",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).SetValue(null,' + str(value) + ');\nreturn true;')


def save(name, value):
    (OUT / (name + '.json')).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def dump(path, prefix=False):
    path.unlink(missing_ok=True)
    args = ['framedebug', 'dump', '--output', str(path), '--capture-timeout', '90']
    if prefix:
        args += ['--max-events', '10']
    r = call(*args, timeout=120, check=False)
    if r['exit'] == 0:
        r['result'] = json.loads(r['stdout'])
    elif 'Details: ' in r['stderr']:
        r['result'] = json.loads(r['stderr'].split('Details: ', 1)[1].splitlines()[0])
    if path.exists():
        d = json.loads(path.read_text(encoding='utf-8-sig'))
        r['payload'] = {k: d[k] for k in ('complete', 'error', 'summary', 'missingFields')}
        r['indices'] = [e['index'] for e in d['events']]
    return r


def trace(label):
    path = OUT / (label + '-trace.json')
    code = '''var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
var f=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
var rows=new List<object>(); var started=EditorApplication.timeSinceStartup;
EditorApplication.CallbackFunction callback=null; callback=()=> { try {
var events=(Array)t.GetMethod("GetFrameEvents",f).Invoke(null,null);
var names=Enumerable.Range(0,events.Length).Select(i=>(string)t.GetMethod("GetFrameEventInfoName",f).Invoke(null,new object[]{i})).ToArray();
var types=events.Cast<object>().Select(e=>e.GetType().GetField("m_Type").GetValue(e).ToString()).ToArray();
rows.Add(new { elapsed=EditorApplication.timeSinceStartup-started, paused=EditorApplication.isPaused, playing=EditorApplication.isPlaying,
enabled=UnityEngine.FrameDebugger.enabled, hash=(int)t.GetProperty("eventsHash",f).GetValue(null), count=(int)t.GetProperty("count",f).GetValue(null),
limit=(int)t.GetProperty("limit",f).GetValue(null), gameTime=Time.time, frame=Time.frameCount, names=names, types=types });
if(rows.Count<600 && EditorApplication.timeSinceStartup-started<10) return;
} catch(Exception e) { rows.Add(new { error=e.ToString() }); }
EditorApplication.update-=callback;
File.WriteAllText(@"OUTPUT",Newtonsoft.Json.JsonConvert.SerializeObject(rows)); }; EditorApplication.update+=callback;
return true;'''.replace('OUTPUT', str(path))
    script = OUT / (label + '-trace.cs')
    script.write_text(code, encoding='utf-8')
    call('exec', '--file', str(script), '--allow-deferred-code')
    return path


def cleanup(initial):
    call('framedebug', 'disable', check=False)
    call('editor', 'stop', check=False)
    call('instances', 'wait', '--state', 'ready', timeout=180)
    exec_code('foreach(var w in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="FrameDebuggerWindow")) w.Close();\nreturn true;')
    set_limit(initial['limit'])
    set_pause(initial['paused'])
    final = state()
    assert not final['playing'] and not final['enabled'] and final['windows'] == 0, final
    assert final['sceneDirty'] == initial['sceneDirty'], (initial, final)
    return final


def diagnostics():
    initial = state()
    assert not initial['playing'] and not initial['enabled'] and initial['windows'] == 0, initial
    rows = {'initial': initial, 'runs': []}
    try:
        call('editor', 'play', '--wait')
        rows['before_enable'] = state()
        call('framedebug', 'enable')
        rows['after_enable'] = state()
        for paused in (True, False):
            set_pause(paused)
            set_limit(3)
            label = 'baseline-paused-' + str(paused).lower()
            p = trace(label)
            before = state()
            result = dump(OUT / (label + '-dump.json'), prefix=True)
            after = state()
            for _ in range(240):
                if p.exists():
                    break
                time.sleep(.05)
            assert p.exists(), 'Diagnostic callback did not finish'
            samples = json.loads(p.read_text(encoding='utf-8'))
            changes = []
            for a, b in zip(samples, samples[1:]):
                if (a.get('hash'), a.get('count')) != (b.get('hash'), b.get('count')):
                    changes.append({'before': {k: a.get(k) for k in ('hash', 'count', 'limit', 'paused', 'gameTime', 'frame')},
                                    'after': {k: b.get(k) for k in ('hash', 'count', 'limit', 'paused', 'gameTime', 'frame')},
                                    'typesBefore': a.get('types'), 'typesAfter': b.get('types'),
                                    'namesBefore': a.get('names'), 'namesAfter': b.get('names')})
            rows['runs'].append({'before': before, 'after': after, 'dump': result, 'changes': changes, 'trace': str(p)})
    finally:
        rows['final'] = cleanup(initial)
        save('diagnostics', rows)
    print(json.dumps({'enablePause': [rows['before_enable']['paused'], rows['after_enable']['paused']],
                      'runs': [{'exit': r['dump']['exit'], 'changes': len(r['changes']), 'paused': r['before']['paused']} for r in rows['runs']]}))


class ForegroundMonitor:
    def __init__(self, unity_pid):
        self.unity_pid = unity_pid
        self.rows = []
        self.phase = 'before'
        self.stop_event = threading.Event()
        self.api = ctypes.WinDLL('user32', use_last_error=True)
        self.api.GetForegroundWindow.restype = wintypes.HWND
        self.api.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        self.sample()
        assert self.rows[-1]['pid'] != unity_pid, 'BLOCKED: Unity is already foreground; select another application manually before testing'
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def sample(self):
        hwnd = self.api.GetForegroundWindow()
        pid = wintypes.DWORD()
        self.api.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        self.rows.append({'time': time.time(), 'phase': self.phase, 'hwnd': int(hwnd or 0), 'pid': pid.value})

    def run(self):
        while not self.stop_event.wait(.02):
            self.sample()

    def finish(self, label):
        self.phase = 'after'
        self.sample()
        self.stop_event.set()
        self.thread.join(timeout=2)
        assert not self.thread.is_alive()
        episodes = sum(r['pid'] == self.unity_pid and (i == 0 or self.rows[i-1]['pid'] != self.unity_pid) for i, r in enumerate(self.rows))
        result = {'unityPid': self.unity_pid, 'samples': len(self.rows), 'unityForegroundSamples': sum(r['pid'] == self.unity_pid for r in self.rows), 'unityForegroundEpisodes': episodes, 'rows': self.rows}
        save(label + '-foreground', result)
        return {k: v for k, v in result.items() if k != 'rows'}


def unfocused_play_settings():
    return exec_code('''var f=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
var views=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="GameView").ToArray();
var old=views.Select(w=>Convert.ToInt32(w.GetType().GetProperty("enterPlayModeBehavior",f).GetValue(w))).ToArray();
foreach(var w in views) { var p=w.GetType().GetProperty("enterPlayModeBehavior",f); p.SetValue(w,Enum.Parse(p.PropertyType,"PlayUnfocused")); }
return Newtonsoft.Json.JsonConvert.SerializeObject(old);''')


def restore_play_settings(old):
    exec_code('''var f=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
var views=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="GameView").ToArray();
var values=new int[]{VALUES}; if(views.Length!=values.Length) throw new Exception("Game view configuration changed during test");
for(int i=0;i<views.Length;i++) { var p=views[i].GetType().GetProperty("enterPlayModeBehavior",f); p.SetValue(views[i],Enum.ToObject(p.PropertyType,values[i])); }
return true;'''.replace('VALUES', ','.join(str(v) for v in old)))


class UnsupportedStartState(Exception):
    pass


def prepare_enabled(paused):
    call('framedebug', 'enable')
    set_pause(True)
    samples = []
    for _ in range(100):
        s = state()
        samples.append({k: s[k] for k in ('enabled', 'limit', 'count', 'windowReady')})
        if s['enabled'] and s['count'] >= 10 and s['windowReady']:
            break
        time.sleep(.1)
    else:
        raise AssertionError({'setup': 'No ready frame after enable', 'samples': samples})
    for _ in range(20):
        set_limit(3)
        a = state()
        time.sleep(.1)
        b = state()
        if a['limit'] == b['limit'] == 3 and a['windowReady'] and b['windowReady']:
            set_pause(paused)
            if not paused:
                native = exec_code('''var t=typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
var f=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
t.GetMethod("SetEnabled",f).Invoke(null,new object[]{true,ProfilerDriver.connectedProfiler});
t.GetProperty("limit",f).SetValue(null,3);
return Newtonsoft.Json.JsonConvert.SerializeObject(new { enabled=UnityEngine.FrameDebugger.enabled, paused=EditorApplication.isPaused, limit=t.GetProperty("limit",f).GetValue(null) });''')
                if not native['enabled']:
                    raise UnsupportedStartState(native)
            before = state()
            if before['limit'] == 3 and before['enabled']:
                return samples
    raise AssertionError({'setup': 'Native/UI initialization kept overriding limit', 'last': state()})


def repetitions(case_filter=None, count=5):
    label = 'focus-repetitions-' + (case_filter or 'all')
    initial = state()
    assert not initial['playing'] and not initial['enabled'] and initial['windows'] == 0, initial
    rows = {'initial': initial, 'runs': []}
    monitor = ForegroundMonitor(initial['pid'])
    settings = None
    try:
        settings = unfocused_play_settings()
        assert settings, 'A Game view must already exist; never open another window'
        monitor.phase = 'enter-play'
        call('editor', 'play', '--wait')
        for case in ((case_filter,) if case_filter else ('a', 'b', 'c')):
            for n in range(count):
                monitor.phase = case + '-' + str(n) + '-setup'
                if case == 'a':
                    call('framedebug', 'disable')
                    exec_code('foreach(var w in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().Name=="FrameDebuggerWindow")) w.Close();\nreturn true;')
                    set_pause(False)
                    set_limit(0)
                else:
                    try:
                        setup = prepare_enabled(case == 'b')
                        rows.setdefault('enabledSetup', []).append(setup)
                    except UnsupportedStartState as ex:
                        assert case == 'c'
                        rows['runs'].append({'case': case, 'iteration': n, 'success': False, 'blocked': True,
                                             'reason': 'Unity rejects native capture while unpaused', 'native': ex.args[0]})
                        save(label, rows)
                        print(json.dumps({'case': case, 'iteration': n, 'blocked': True, 'native': ex.args[0]}), flush=True)
                        continue
                before = state()
                assert before['playing'] and before['enabled'] == (case != 'a'), {'case': case, 'setup': before}
                assert before['paused'] == (case == 'b') and before['windows'] == 0, {'case': case, 'setup': before}
                if case != 'a':
                    assert before['limit'] == 3, before
                path = OUT / ('focus-repeat-' + case + '-' + str(n) + '.json')
                monitor.phase = case + '-' + str(n) + '-dump'
                result = dump(path, prefix=case != 'a')
                monitor.phase = case + '-' + str(n) + '-after'
                after = state()
                restored = all(before[k] == after[k] for k in ('enabled', 'limit', 'paused', 'playing', 'windows', 'sceneDirty'))
                success = result['exit'] == 0 and result.get('payload', {}).get('complete') and result['payload']['summary']['failedEvents'] == 0
                if success:
                    assert result['result']['pausedDuringCapture'], 'Capture did not report paused state'
                    assert result['result']['retryCount'] <= result['result']['maxRetries'] == 3
                rows['runs'].append({'case': case, 'iteration': n, 'before': before, 'after': after,
                                     'success': bool(success), 'restored': restored, 'dump': result})
                save(label, rows)
                print(json.dumps({'case': case, 'iteration': n, 'success': bool(success), 'restored': restored,
                                  'retryCount': result.get('result', {}).get('retryCount')}), flush=True)
                if not restored:
                    raise AssertionError('State restoration failed')
                if success:
                    assert result['indices'] == list(range(result['payload']['summary']['dumpedEvents']))
        call('framedebug', 'disable')
        call('editor', 'stop')
        call('instances', 'wait', '--state', 'ready', timeout=180)
        monitor.phase = 'edit-dump'
        before = state()
        rows['edit'] = {'before': before, 'dump': dump(OUT / ('focus-edit-' + (case_filter or 'all') + '.json'), prefix=True), 'after': state()}
        rows['edit']['restored'] = all(before[k] == rows['edit']['after'][k] for k in ('enabled', 'limit', 'paused', 'playing', 'windows'))
    finally:
        try:
            monitor.phase = 'cleanup'
            rows['final'] = cleanup(initial)
        finally:
            if settings is not None:
                restore_play_settings(settings)
                rows['playSettingsRestored'] = True
            rows['foreground'] = monitor.finish(label)
            save(label, rows)
    assert rows['foreground']['unityForegroundEpisodes'] == 0, rows['foreground']
    print(json.dumps({case: {'success': sum(r['success'] for r in rows['runs'] if r['case'] == case),
                            'total': sum(r['case'] == case for r in rows['runs'])} for case in ('a', 'b', 'c')}))


if __name__ == '__main__':
    if sys.argv[1] == 'diagnostics':
        diagnostics()
    else:
        repetitions(sys.argv[2] if len(sys.argv) > 2 else None, int(sys.argv[3]) if len(sys.argv) > 3 else 5)
