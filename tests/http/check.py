"""Main ProjectD HTTP regression harness. All child processes use CREATE_NO_WINDOW.
Raw HTTP supplements the installed CLI for malformed/disconnected/concurrent clients.
Screen phase is deliberately separate and MUST be invoked once, last.
"""
import concurrent.futures as cf
import ctypes
from ctypes import wintypes
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import socket
import statistics
import subprocess
import sys
import time
import zlib

PROJECT = 'D:/Projects/ProjectD/client'
CLI = os.environ.get('UNITY_HTTP_TEST_CLI', 'D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe')
ROOT = Path(__file__).resolve().parents[2]
OUT = Path(sys.argv[2]); OUT.mkdir(parents=True, exist_ok=True)
PHASE = sys.argv[1]
PORT = None
PID = None


def os_thread_count():
    class ThreadEntry(ctypes.Structure):
        _fields_ = [('dwSize',wintypes.DWORD),('cntUsage',wintypes.DWORD),('th32ThreadID',wintypes.DWORD),('th32OwnerProcessID',wintypes.DWORD),('tpBasePri',wintypes.LONG),('tpDeltaPri',wintypes.LONG),('dwFlags',wintypes.DWORD)]
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateToolhelp32Snapshot.restype=wintypes.HANDLE
    kernel.CreateToolhelp32Snapshot.argtypes=[wintypes.DWORD,wintypes.DWORD]
    kernel.Thread32First.argtypes=[wintypes.HANDLE,ctypes.POINTER(ThreadEntry)]
    kernel.Thread32Next.argtypes=[wintypes.HANDLE,ctypes.POINTER(ThreadEntry)]
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.CreateToolhelp32Snapshot(4,0)
    assert handle and handle!=ctypes.c_void_p(-1).value
    try:
        entry=ThreadEntry();entry.dwSize=ctypes.sizeof(entry);count=0
        ok=kernel.Thread32First(handle,ctypes.byref(entry))
        while ok:
            count+=entry.th32OwnerProcessID==PID
            ok=kernel.Thread32Next(handle,ctypes.byref(entry))
        assert count>0,(PID,count)
        return count
    finally:kernel.CloseHandle(handle)


def save(name, data):
    (OUT / (name + '.json')).write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding='utf-8')


def cli(*args, timeout=180, check=True):
    start = time.perf_counter()
    p = subprocess.run([CLI, '--project', PROJECT, '--timeout', str(timeout * 1000), *args],
                       capture_output=True, text=True, encoding='utf-8', errors='replace',
                       timeout=timeout + 5, creationflags=subprocess.CREATE_NO_WINDOW)
    row = dict(args=args, code=p.returncode, stdout=p.stdout.strip(), stderr=p.stderr.strip(),
               ms=(time.perf_counter() - start) * 1000)
    if check and p.returncode: raise AssertionError(row)
    return row


def ready(state='ready'):
    global PORT, PID
    r = cli('instances', 'wait', '--state', state, timeout=120)
    r = cli('status')
    match = re.search(r'port (\d+)', r['stdout'])
    assert match, r
    PORT = int(match[1])
    PID = int(re.search(r'PID:\s+(\d+)',r['stdout'])[1])
    return r


def request(command=None, params=None, *, body=None, method='POST', path='/command', headers=None, timeout=120):
    data = body if body is not None else json.dumps(dict(command=command, params=params or {}))
    c = http.client.HTTPConnection('127.0.0.1', PORT, timeout=timeout)
    t = time.perf_counter()
    try:
        c.request(method, path, data if method != 'GET' else None,
                  {'Content-Type': 'application/json', **(headers or {})})
        r = c.getresponse(); raw = r.read()
        assert int(r.getheader('Content-Length', len(raw))) == len(raw)
        return dict(status=r.status, json=json.loads(raw) if raw else None,
                    bytes=len(raw), ms=(time.perf_counter()-t)*1000)
    finally: c.close()


def success(command, params=None):
    r = request(command, params)
    assert r['status'] == 200 and r['json']['success'], r
    return r['json']['data']


def probe(action='work', **params):
    return success('_http_thread_probe', dict(action=action, **params))


def job(command, params):
    ack = success(command, dict(params, **{'async': True}))
    for _ in range(600):
        r = request('job_status', {'job_id': ack['job_id']})['json']
        if r['message'] != 'running': return r
        time.sleep(.05)
    raise AssertionError('job did not finish')


def baseline_or_regression():
    ready()
    schemas = success('list'); save(PHASE+'-tools', schemas)
    cases = [
        ('console', {'lines': 1, 'type': 'error', 'filter': '__http_no_matching_log__', 'stacktrace': 'none'}),
        ('exec', {'code': 'return 1;'}),
        ('profiler', {'action': 'status'}),
        ('profiler', {'action': 'hierarchy', 'max': 1}),
        ('framedebug', {'action': 'status'}),
        ('framedebug', {'action': 'dump', 'max_events': 0}),
        ('screenshot', {'action': 'list_windows'}),
        ('screenshot', {'view': 'window', 'window_type': '__http_nonexistent_window__', 'output_path': str(OUT/'never.png')}),
        ('ui_snapshot', {'action': 'query', 'selector': 'id=__http_absent_element__', 'source': 'editor'}),
        ('ui_snapshot', {'action': 'events', 'event_action': 'status'}),
        ('refresh_unity', {'compile': 'none'}),
        ('manage_editor', {'action': 'stop'}),
        ('mppm', {'action': 'status'}),
        ('manage_parrel_sync', {'action': 'list'}),
        ('trace_method', {'action': 'list'}),
        ('menu', {'menu_path': 'File/Quit'}),
        ('reserialize', {'paths': [{}]}),  # invalid string array; never calls ForceReserializeAssets
        ('run_tests', {'mode': 'Unknown'}),
        ('projectd_e2e', {'action': 'capabilities'}),
        ('_http_unknown_command', {}),
        ('exec', {'code': 'return __http_compile_error__;'}),
        ('exec', {'code': 'throw new InvalidOperationException("HTTP_EXPECTED_RUNTIME");\nreturn 1;'}),
        ('profiler', {'action': 'hierarchy', 'frame': 'not-an-int'}),
        ('job_status', {}),
        ('_http_thread_probe', {'delay': 100, 'id': 'serialize'}),
    ]
    expected = {t['name'] for t in schemas}
    used = {c for c, _ in cases}
    assert expected <= used, (expected-used, expected)
    rows=[]
    for command, params in cases:
        r=request(command, params); rows.append(dict(command=command, params=params, **r))
    rows += [dict(protocol=label, **request(**kw)) for label, kw in [
        ('bad_json', dict(body='{')),
        ('missing_command', dict(body='{}')),
        ('wrong_route', dict(method='GET',path='/absent')),
        ('origin', dict(headers={'Origin':'http://example.invalid'})),
        ('options', dict(method='OPTIONS')),
    ]]
    save(PHASE+'-responses', rows)
    ack_result=job('_http_thread_probe', {'delay': 250, 'id': 'async'})
    assert ack_result['success'] and ack_result['data']['enteredThread'] == ack_result['data']['GetterThread']
    save(PHASE+'-async', ack_result)
    codefile=OUT/'inline.cs'; codefile.write_text('return 1;', encoding='utf-8')
    cli_rows=[cli('status'),cli('instances','list'),cli('exec','return 1;'),
              cli('exec','--file',str(codefile)),cli('exec','return 1;','--async')]
    ack=json.loads(cli_rows[-1]['stdout'])
    cli_rows.append(cli('job', ack['job_id']))
    save(PHASE+'-cli',cli_rows)
    timings=[]
    for cmd, args in [('console',('console','--lines','1','--stacktrace','none')),('exec',('exec','return 1;'))]:
        for _ in range(2): cli(*args)
        for _ in range(25): timings.append(dict(command=cmd, **cli(*args)))
    save(PHASE+'-timings',timings)
    print(json.dumps({'phase':PHASE,'tools':sorted(expected),'latency_ms':{c:statistics.mean(r['ms'] for r in timings if r['command']==c) for c in ['console','exec']}},ensure_ascii=False))


def smoke():
    ready()
    r=cli('test','EditMode','--filter','UnityCliConnector.HttpTests.EditModeSmoke.PureMainThreadSmoke')
    data=json.loads(r['stdout']);assert data['passed']==1 and data['failed']==0,data
    assert success('exec',{'code':'return EditorApplication.isPlaying;'}) is False
    save('smoke',r)
    print('PASS: native EditMode smoke test passed, Editor remains Edit Mode')


def compare():
    def normalize(obj):
        if isinstance(obj, dict):
            return {k:normalize(v) for k,v in obj.items() if k not in {'ms','bytes','epoch','threads','captured_at','timestamp'}}
        if isinstance(obj,list): return [normalize(v) for v in obj]
        if isinstance(obj,str):
            obj = re.sub(r'artifacts[\\/]+(baseline|candidate)', 'artifacts/PHASE', obj)
            return re.sub(r'exec_[\w]+_\w+', 'exec_JOB', obj)
        return obj
    a=sorted(json.loads((OUT/'baseline-tools.json').read_text(encoding='utf-8')), key=lambda t:t['name'])
    b=sorted(json.loads((OUT/'regression-tools.json').read_text(encoding='utf-8')), key=lambda t:t['name'])
    assert a==b, 'tool schema changed'
    a=json.loads((OUT/'baseline-responses.json').read_text(encoding='utf-8'))
    b=json.loads((OUT/'regression-responses.json').read_text(encoding='utf-8'))
    differences=[]
    for old,new in zip(a,b):
        if normalize(old)!=normalize(new): differences.append(dict(old=old,new=new))
    save('comparison',dict(cases=len(a), differences=differences))
    assert not differences, differences
    print(f'PASS: {len(a)} complete HTTP responses equivalent (documented volatile fields excluded)')


def stress():
    ready(); records={}
    ack=success('_http_thread_probe',{'delay':1000,'id':'async-running','async':True})
    running=request('job_status',{'job_id':ack['job_id']})['json']
    assert running['success'] and running['message']=='running',running
    for _ in range(100):
        done=request('job_status',{'job_id':ack['job_id']})['json']
        if done['message']!='running':break
        time.sleep(.02)
    assert done['success'] and done['data']['GetterThread']==done['data']['resumedThread']
    records['async_job']=dict(running=running,completed=done)
    submitted=cli('exec','System.Threading.Thread.Sleep(2000);\nreturn 2;','--async')
    assert submitted['ms']<1500,submitted
    ack=json.loads(submitted['stdout']);time.sleep(2.2)
    records['async_submission']=dict(submitted=submitted,result=cli('job',ack['job_id']))
    for command in ['console','exec']:
        for i in range(150):
            success(command, {'lines':1,'stacktrace':'none'} if command=='console' else {'code':'return 1;'})
    records['sequential']=300
    for width in (4,8):
        probe('reset')
        with cf.ThreadPoolExecutor(max_workers=width) as pool:
            for round_ in range(50):
                responses=list(pool.map(lambda i: probe(delay=2, id=f'{width}-{round_}-{i}'),range(width)))
                assert all(r['enteredThread']==r['resumedThread']==r['GetterThread'] for r in responses)
        state=probe('state')
        assert state['maximum']==1 and state['entered']==state['exited']==width*50 and state['active']==0,state
        events=state['events']; assert all(events[i].replace('enter:','exit:',1)==events[i+1] for i in range(0,len(events),2))
        records[f'parallel_{width}']=state
    with cf.ThreadPoolExecutor(max_workers=8) as pool:
        jobs=list(pool.map(lambda i: success('_http_thread_probe',{'delay':2,'id':f'job-{i}','async':True}),range(64)))
    ids=[j['job_id'] for j in jobs];assert len(set(ids))==64,ids
    for jid in ids:
        for _ in range(200):
            result=request('job_status',{'job_id':jid})['json']
            if result['message']!='running':break
            time.sleep(.02)
        assert result['success'] and result['data']['GetterThread']==1,result
    records['parallel_jobs']=dict(unique_ids=len(set(ids)),completed=64)
    for code in ('return __http_compile_error__;', 'throw new InvalidOperationException("HTTP_EXPECTED_RUNTIME");\nreturn 1;'):
        failed=job('exec',{'code':code});assert not failed['success'],failed
    records['async_errors']='compile/runtime errors preserved'
    probe('reset')
    with cf.ThreadPoolExecutor(max_workers=3) as pool:
        long=pool.submit(request,'exec',{'code':'System.Threading.Thread.Sleep(2000);\nreturn 2;'})
        time.sleep(.3)
        local_status=cli('status')
        health=request(method='GET',path='/health')
        short=pool.submit(request,'console',{'lines':1,'stacktrace':'none'})
        a=long.result(); b=short.result()
        assert a['json']['success'] and b['json']['success']
        records['long_short']=dict(long=a,console=b,status=local_status,health=health)
    large=success('exec',{'code':'return new string(\'x\', 4 * 1024 * 1024);'})
    assert len(large)==4*1024*1024 and set(large)=={'x'}
    records['large']=dict(length=len(large),sha256=hashlib.sha256(large.encode()).hexdigest())
    large_input='body-'+'q'*(2*1024*1024)
    echoed=probe(delay=1,id=large_input)
    assert echoed['id']==large_input
    records['large_request']=dict(length=len(large_input),getter_thread=echoed['GetterThread'])
    p=subprocess.Popen([CLI,'--project',PROJECT,'exec','System.Threading.Thread.Sleep(2000);\nreturn 2;'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
    time.sleep(.6); p.kill(); p.communicate(timeout=5)
    time.sleep(2.5); assert success('exec',{'code':'return 1;'})==1
    # Abandoned partial POST must not retain a body reader during shutdown.
    sock=socket.create_connection(('127.0.0.1',PORT));sock.sendall(f'POST /command HTTP/1.1\r\nHost: 127.0.0.1:{PORT}\r\nContent-Length: 99999\r\n\r\n{{'.encode())
    sock.close(); assert success('exec',{'code':'return 1;'})==1
    records['disconnect']='CLI killed and incomplete POST closed: recovered'
    save('stress',records); print('PASS: 300 sequential + 200/400 parallel; peak=1, main-thread getters/continuations; 4 MiB; disconnect')


def lifetime():
    ready(); rows=[]
    fixture=Path('D:/Projects/ProjectD/tools/unity-cli/unity-connector/Editor/HttpThreadProbe.cs')
    original=fixture.read_text(encoding='utf-8')
    for i in range(5):
        old=probe('state');old['os_threads']=os_thread_count()
        probe('configure',shutdown_path=str(OUT/'shutdown.jsonl'))
        partial=socket.create_connection(('127.0.0.1',PORT));partial.sendall(f'POST /command HTTP/1.1\r\nHost: 127.0.0.1:{PORT}\r\nContent-Length: 99999\r\n\r\n{{'.encode())
        with cf.ThreadPoolExecutor(max_workers=2) as pool:
            fixture.write_text(original+f'\n// HTTP regression compile round {i}\n',encoding='utf-8')
            pending=pool.submit(request,'_http_thread_probe',{'delay':30000,'id':'reload-pending','compile':True},timeout=90)
            time.sleep(.3)
            refresh=cli('exec','return 1;',timeout=120,check=False)
            try: outcome=pending.result(timeout=100)
            except (OSError,http.client.HTTPException,TimeoutError) as ex: outcome={'terminated':type(ex).__name__}
        partial.close();ready()
        new=probe('state');new['os_threads']=os_thread_count();assert old['epoch']!=new['epoch'],(old,new,refresh)
        assert success('exec',{'code':'return 1;'})==1
        rows.append(dict(round=i,old=old,new=new,pending=outcome,refresh=refresh,port=PORT))
    counts=[r['new']['os_threads'] for r in rows]
    assert counts[-1]-min(counts)<=12, counts
    if PHASE != 'baseline_lifetime':
        reports=[json.loads(line) for line in (OUT/'shutdown.jsonl').read_text(encoding='utf-8').splitlines()]
        epochs={r['old']['epoch'] for r in rows}
        relevant=[r for r in reports if r['epoch'] in epochs]
        assert len(relevant)==5 and all(r['unfinished']==0 and r['loopCompleted'] for r in relevant),relevant
        save('shutdown',relevant)
    save(PHASE+'-reloads',rows)
    # Safe managed stop of only the main connector listener, then existing recovery.
    try:
        killed=request('_http_thread_probe',{'action':'kill_listener'})
        save('kill-listener',killed)
    except (OSError,http.client.HTTPException) as ex: save('kill-listener',{'terminated':type(ex).__name__})
    ready(); assert success('exec',{'code':'return 1;'})==1
    save('recovery',probe('state'))
    # CLI initiates both transitions. No scene saves/window calls.
    play=[]
    try:
        play.append(cli('editor','play',timeout=180,check=False));ready('playing')
        assert success('exec',{'code':'return EditorApplication.isPlaying;'}) is True
        play.extend([cli('status'),cli('console','--lines','1','--stacktrace','none'),cli('exec','return 1;')])
    finally:
        play.append(cli('editor','stop',timeout=180,check=False));ready()
        assert success('exec',{'code':'return EditorApplication.isPlaying;'}) is False
    save('play',play)
    print('PASS: 5 real source recompilations, pending + partial requests, listener recovery, Play/Edit transitions; thread counts '+str(counts))


def screen():
    # One-shot marker survives failure: no retry allowed by user.
    marker=OUT/'screen-started';assert not marker.exists(),'screen tests already attempted'
    marker.write_text('one-shot',encoding='utf-8');ready()
    rows={}
    png=OUT/'capture.png';dump=OUT/'framedebug.json'
    r=request('screenshot',{'output_path':str(png),'view':'game'})
    rows['screenshot']=r;save('screen',rows)
    assert r['json']['success'] and png.exists(),r
    data=png.read_bytes();assert len(data)>20 and data[:8]==b'\x89PNG\r\n\x1a\n' and data[-12:]==b'\x00\x00\x00\x00IEND\xaeB`\x82'
    offset=8;chunks=[]
    while offset<len(data):
        length=int.from_bytes(data[offset:offset+4],'big');kind=data[offset+4:offset+8]
        payload=data[offset+8:offset+8+length]
        crc=int.from_bytes(data[offset+8+length:offset+12+length],'big')
        assert len(payload)==length and zlib.crc32(kind+payload)==crc,(kind,length)
        chunks.append(kind.decode('ascii'));offset+=12+length
    assert offset==len(data) and 'IDAT' in chunks and chunks[-1]=='IEND'
    rows['png_size']=len(data);rows['png_chunks']=chunks;png.unlink()
    initial=success('framedebug',{'action':'status'})
    rows['before']=initial
    try:
        rows['play']=cli('editor','play',timeout=180,check=False);ready('playing')
        assert success('exec',{'code':'return EditorApplication.isPlaying;'}) is True
        # Compare active debugger state in the same Play domain. A disabled
        # native limit sentinel can change from -1 to 0 on first initialization.
        initial=success('framedebug',{'action':'status'});rows['before_dump']=initial
        rows['paused_before']=success('exec',{'code':'return EditorApplication.isPaused;'})
        rows['dump']=request('framedebug',{'action':'dump','output':str(dump),'max_events':10,'capture_timeout':60},timeout=90)
        save('screen',rows)
        assert rows['dump']['json']['success'],rows['dump']
        payload=json.loads(dump.read_text(encoding='utf-8-sig'))
        save('screen-dump-summary',{'keys':list(payload),'payload':payload})
        assert payload.get('complete') and payload.get('events'),payload
        assert all(not event.get('error') for event in payload['events']),payload['events']
    finally:
        try:
            after=success('framedebug',{'action':'status'});rows['after']=after
            assert after['enabled']==initial['enabled'] and not after['capturing'],after
            if initial['enabled']:assert after['limit']==initial['limit'],after
            rows['paused_after']=success('exec',{'code':'return EditorApplication.isPaused;'})
            assert rows['paused_after']==rows['paused_before']
        finally:
            rows['stop']=cli('editor','stop',timeout=180,check=False);ready()
            assert success('exec',{'code':'return EditorApplication.isPlaying;'}) is False
            png.unlink(missing_ok=True);dump.unlink(missing_ok=True);save('screen',rows)
    print('PASS: one screenshot PNG complete; one Play framedebug dump populated, state restored, Edit Mode')


if __name__=='__main__':
    {'baseline':baseline_or_regression,'regression':baseline_or_regression,'compare':compare,
     'stress':stress,'lifetime':lifetime,'baseline_lifetime':lifetime,'screen':screen,'smoke':smoke}[PHASE]()
